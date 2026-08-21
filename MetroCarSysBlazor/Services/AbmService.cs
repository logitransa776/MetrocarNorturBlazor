using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MetroCarSysBlazor.Data;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Capa de ESCRITURA del proyecto (INSERT/UPDATE) — separada de <see cref="ReportService"/>
/// (que es solo lectura). Estrena la escritura con el ABM de Usuarios (tabla <c>usuario</c>,
/// cuyo dueño ya migró a SQL: la sync DBF→SQL de esa tabla está apagada).
///
/// Reglas no negociables (skill abm-metrocar):
///  - Escritura SIEMPRE con <see cref="SqlParameter"/> (nunca string + Replace).
///  - Baja LÓGICA con <c>f_delete</c> (nunca DELETE físico).
///  - Auditoría de negocio: <c>f_create</c> / <c>f_modify</c> / <c>f_delete</c> (date).
///  - En INSERT setear <c>_deleted = 0</c> explícito (los informes filtran por esa columna).
///  - Una transacción por operación; el chequeo de PK duplicada va dentro de la misma conexión.
///  - Tras cada escritura, invalidar el caché de la grilla (<see cref="ReportService.InvalidarCacheAbm"/>).
/// </summary>
public partial class AbmService
{
    private readonly IDbContextFactory<NorturDbContext> _dbFactory;
    private readonly ReportService _reports;

    public AbmService(IDbContextFactory<NorturDbContext> dbFactory, ReportService reports)
    {
        _dbFactory = dbFactory;
        _reports = reports;
    }

    /// <summary>Resultado de una operación de escritura: éxito + mensaje de error (si falló).</summary>
    public record AbmResult(bool Ok, string? Error, int? Id = null)
    {
        /// <summary>Operación exitosa PERO con una salvedad para mostrarle al operador (p. ej.
        /// se grabó el dato principal pero no su bitácora porque falta replicar la tabla).
        /// No es un error: <see cref="Ok"/> sigue en true.</summary>
        public string? Aviso { get; init; }

        public static AbmResult Fallo(string error) => new(false, error);
        public static AbmResult Exito(int? id = null) => new(true, null, id);
        public static AbmResult Exito(int? id, string aviso) => new(true, null, id) { Aviso = aviso };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  USUARIOS — alta / modifica / baja / password
    //  Espeja usuario_abm.scx (alta/baja/modifica) y cambio_password.scx.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Caracteres especiales admitidos en una contraseña (set acotado y ASCII).</summary>
    public const string PasswordEspeciales = "!@#$%&*-_.?";

    /// <summary>
    /// Política de contraseñas NUEVAS (24/07/2026, pedida por el usuario): 8–15 caracteres
    /// (el tope 15 es el límite de la columna <c>usuario.password nvarchar(15)</c>), con al menos
    /// UNA mayúscula, UNA minúscula, UN dígito y UN carácter especial del set <see cref="PasswordEspeciales"/>.
    /// No se admiten otros símbolos, espacios ni letras acentuadas (evita problemas de codepage/teclado
    /// en el login de texto plano). Solo se aplica a claves nuevas — las existentes que no se cambian
    /// no se validan ni se reescriben. Devuelve <c>null</c> si es válida, o el mensaje de error a mostrar.
    /// </summary>
    public static string? ValidarPasswordFuerte(string? password)
    {
        password = (password ?? "").Trim();
        if (password.Length < 8)
            return "La contraseña debe tener al menos 8 caracteres.";
        if (password.Length > 15)
            return "La contraseña no puede superar 15 caracteres.";

        bool mayus = false, minus = false, digito = false, especial = false;
        foreach (var c in password)
        {
            if (c >= 'A' && c <= 'Z') mayus = true;
            else if (c >= 'a' && c <= 'z') minus = true;
            else if (c >= '0' && c <= '9') digito = true;
            else if (PasswordEspeciales.Contains(c)) especial = true;
            else return $"El carácter «{c}» no está permitido. Usá letras (sin acentos), números y estos símbolos: {PasswordEspeciales}";
        }

        var faltan = new List<string>();
        if (!mayus)    faltan.Add("una mayúscula");
        if (!minus)    faltan.Add("una minúscula");
        if (!digito)   faltan.Add("un número");
        if (!especial) faltan.Add($"un carácter especial ({PasswordEspeciales})");
        if (faltan.Count > 0)
            return "La contraseña debe incluir " + string.Join(", ", faltan) + ".";

        return null;
    }

    /// <summary>
    /// Alta de un usuario (usuario_abm.scx modo "alta"). Valida nombre no vacío, contraseña,
    /// y unicidad del nombre. El <c>id</c> se calcula como MAX(id)+1 (la PK física NO es identity,
    /// así lo hace el FoxPro). Graba <c>nivel="12345"</c> fijo, <c>f_create=hoy</c>, <c>_deleted=0</c>.
    /// El acceso por Internet viaja dentro del string <c>acceso</c> (letra 'I') — sin columna aparte.
    /// </summary>
    public async Task<AbmResult> AltaUsuarioAsync(string usuario, string password, string acceso, bool operador)
    {
        usuario = (usuario ?? "").Trim();
        password = (password ?? "").Trim();

        if (string.IsNullOrWhiteSpace(usuario))
            return AbmResult.Fallo("No se cargó el nombre del Usuario.");
        if (usuario.Length > 15)
            return AbmResult.Fallo("El nombre de usuario no puede superar 15 caracteres.");
        var errPass = ValidarPasswordFuerte(password);
        if (errPass is not null)
            return AbmResult.Fallo(errPass);
        if ((acceso ?? "").Length > 15)
            return AbmResult.Fallo("Demasiados permisos seleccionados para este usuario.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // 1) Anti-duplicado (dentro de la misma tx). Cuenta también los _deleted por las dudas.
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM usuario WHERE usuario = @u";
                chk.Parameters.Add(new SqlParameter("@u", usuario));
                var existe = (int)(await chk.ExecuteScalarAsync() ?? 0);
                if (existe > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo($"El usuario «{usuario}» ya está cargado.");
                }
            }

            // 2) Próximo id (PK física no identity → MAX(id)+1, como el FoxPro).
            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM usuario";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            // 3) INSERT.
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO usuario (id, usuario, password, nivel, acceso, operador, f_create, _deleted)
                    VALUES (@id, @usuario, @password, '12345', @acceso, @operador, CAST(GETDATE() AS date), 0)
                    """;
                ins.Parameters.Add(new SqlParameter("@id", nuevoId));
                ins.Parameters.Add(new SqlParameter("@usuario", usuario));
                ins.Parameters.Add(new SqlParameter("@password", password));
                ins.Parameters.Add(new SqlParameter("@acceso", acceso ?? ""));
                ins.Parameters.Add(new SqlParameter("@operador", operador));
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta el usuario: {ex.Message}");
        }
    }

    /// <summary>
    /// Modificación de un usuario (usuario_abm.scx modo "modifica"). La PK lógica (<c>usuario</c>)
    /// NO se edita. Actualiza password, acceso, operador y <c>f_modify=hoy</c>. Puede REHABILITAR
    /// (rehabilitar=true → limpia f_delete). No permite quitarse el permiso 'S' a sí mismo.
    /// </summary>
    public async Task<AbmResult> ModificaUsuarioAsync(
        int id, string password, string acceso, bool operador, bool rehabilitar, string usuarioEditorLogueado)
    {
        password = (password ?? "").Trim();
        // Password en blanco = no cambiar → no se toca la columna ni se valida (regla del FoxPro).
        // Si viene una clave nueva, debe cumplir la política de contraseñas.
        bool cambiaPassword = password.Length > 0;
        if (cambiaPassword)
        {
            var errPass = ValidarPasswordFuerte(password);
            if (errPass is not null)
                return AbmResult.Fallo(errPass);
        }
        if ((acceso ?? "").Length > 15)
            return AbmResult.Fallo("Demasiados permisos seleccionados para este usuario.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Anti-autobloqueo: el editor no puede quitarse a sí mismo el permiso 'S' (Usuarios).
            string usuarioObjetivo;
            await using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT RTRIM(ISNULL(usuario,'')) FROM usuario WHERE id = @id AND _deleted = 0";
                q.Parameters.Add(new SqlParameter("@id", id));
                usuarioObjetivo = (string?)(await q.ExecuteScalarAsync()) ?? "";
            }
            if (string.IsNullOrEmpty(usuarioObjetivo))
            {
                await tx.RollbackAsync();
                return AbmResult.Fallo("El usuario ya no existe.");
            }
            if (string.Equals(usuarioObjetivo, usuarioEditorLogueado, StringComparison.OrdinalIgnoreCase)
                && !PermisosCatalogo.Tiene(acceso, 'S'))
            {
                await tx.RollbackAsync();
                return AbmResult.Fallo("No podés quitarte a vos mismo el permiso «Usuarios y Password».");
            }

            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = $"""
                    UPDATE usuario
                    SET {(cambiaPassword ? "password = @password," : "")}
                        acceso   = @acceso,
                        operador = @operador,
                        f_modify = CAST(GETDATE() AS date)
                        {(rehabilitar ? ", f_delete = NULL" : "")}
                    WHERE id = @id AND _deleted = 0
                    """;
                if (cambiaPassword)
                    upd.Parameters.Add(new SqlParameter("@password", password));
                upd.Parameters.Add(new SqlParameter("@acceso", acceso ?? ""));
                upd.Parameters.Add(new SqlParameter("@operador", operador));
                upd.Parameters.Add(new SqlParameter("@id", id));
                var filas = await upd.ExecuteNonQueryAsync();
                if (filas == 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("El usuario ya no existe.");
                }
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo modificar el usuario: {ex.Message}");
        }
    }

    /// <summary>
    /// Baja LÓGICA de un usuario (usuario_abm.scx modo "baja"): <c>f_delete = hoy</c>. Nunca borra
    /// físico. Reglas del FoxPro + defensa nuestra: no se puede inhabilitar a SUPERVISOR ni a uno mismo.
    /// </summary>
    public async Task<AbmResult> BajaUsuarioAsync(int id, string usuarioEditorLogueado)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            string usuarioObjetivo;
            await using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT RTRIM(ISNULL(usuario,'')) FROM usuario WHERE id = @id AND _deleted = 0";
                q.Parameters.Add(new SqlParameter("@id", id));
                usuarioObjetivo = (string?)(await q.ExecuteScalarAsync()) ?? "";
            }
            if (string.IsNullOrEmpty(usuarioObjetivo))
            {
                await tx.RollbackAsync();
                return AbmResult.Fallo("El usuario ya no existe.");
            }
            if (string.Equals(usuarioObjetivo, "SUPERVISOR", StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync();
                return AbmResult.Fallo("No se puede inhabilitar al usuario SUPERVISOR.");
            }
            if (string.Equals(usuarioObjetivo, usuarioEditorLogueado, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync();
                return AbmResult.Fallo("No podés inhabilitarte a vos mismo.");
            }

            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE usuario SET f_delete = CAST(GETDATE() AS date) WHERE id = @id AND _deleted = 0";
                upd.Parameters.Add(new SqlParameter("@id", id));
                await upd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo inhabilitar el usuario: {ex.Message}");
        }
    }

    /// <summary>
    /// Cambio de contraseña de un usuario (cambio_password.scx). Texto plano (nvarchar(15)),
    /// consistente con el login actual. No toca permisos ni operador; setea <c>f_modify=hoy</c>.
    /// </summary>
    public async Task<AbmResult> CambiarPasswordAsync(int id, string nuevaPassword)
    {
        nuevaPassword = (nuevaPassword ?? "").Trim();
        var errPass = ValidarPasswordFuerte(nuevaPassword);
        if (errPass is not null)
            return AbmResult.Fallo(errPass);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE usuario SET password = @p, f_modify = CAST(GETDATE() AS date) WHERE id = @id AND _deleted = 0";
            upd.Parameters.Add(new SqlParameter("@p", nuevaPassword));
            upd.Parameters.Add(new SqlParameter("@id", id));
            var filas = await upd.ExecuteNonQueryAsync();
            if (filas == 0)
                return AbmResult.Fallo("El usuario ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo cambiar la contraseña: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FLETEROS — alta / modifica / baja  (espeja fletero_abm.scx)
    //  ⚠ ANDAMIAJE (05/07/2026): estos métodos están LISTOS pero NO se llaman
    //  todavía — la pantalla es solo lectura. La tabla `fletero` sigue con dueño
    //  FoxPro (sync DBF→SQL viva). Antes de activarlos: bloquear el ABM de fletero
    //  en FoxPro + apagar su sync + coordinar con Facturación (catálogo compartido).
    //  Regla strangler: skill abm-metrocar.
    //  Tabla: id (int, PK física NO identity → MAX(id)+1), id_contrat (nvarchar 15,
    //  PK lógica tipeada), razon_soci, nombre, orden (bigint), cuit, etc.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Alta de un fletero (fletero_abm.scx modo "alta"). Valida id_contrat y razón
    /// social no vacíos + unicidad de id_contrat. id físico = MAX(id)+1 (no identity).</summary>
    public async Task<AbmResult> AltaFleteroAsync(FleteroInput f)
    {
        var (ok, err) = ValidarFletero(f, esAlta: true);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Anti-duplicado por PK lógica id_contrat (dentro de la tx).
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM fletero WHERE id_contrat = @c";
                chk.Parameters.Add(new SqlParameter("@c", f.IdContrat.Trim()));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo($"El fletero «{f.IdContrat.Trim()}» ya está cargado.");
                }
            }

            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM fletero";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO fletero
                        (id, id_contrat, razon_soci, nombre, orden, cuit, tipo_resp,
                         domicilio, localidad, postal, provincia, telefono, celular, email,
                         contacto, id_lista_p, id_lista_2, modo_liq, fc_prefere, diagrama,
                         f_create, _deleted)
                    VALUES
                        (@id, @idc, @razon, @nombre, @orden, @cuit, @tresp,
                         @dom, @loc, @cp, @prov, @tel, @cel, @email,
                         @cont, @lp, @l2, @mliq, @fcp, @diag,
                         CAST(GETDATE() AS date), 0)
                    """;
                AgregarParamsFletero(ins, f, nuevoId);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta el fletero: {ex.Message}");
        }
    }

    /// <summary>Modificación de un fletero (fletero_abm.scx modo "modifica"). La PK lógica
    /// id_contrat NO se edita. rehabilitar=true limpia f_delete. Setea f_modify=hoy.</summary>
    public async Task<AbmResult> ModificaFleteroAsync(int id, FleteroInput f, bool rehabilitar)
    {
        var (ok, err) = ValidarFletero(f, esAlta: false);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = $"""
                    UPDATE fletero SET
                        razon_soci = @razon, nombre = @nombre, orden = @orden, cuit = @cuit,
                        tipo_resp = @tresp, domicilio = @dom, localidad = @loc, postal = @cp,
                        provincia = @prov, telefono = @tel, celular = @cel, email = @email,
                        contacto = @cont, id_lista_p = @lp, id_lista_2 = @l2, modo_liq = @mliq,
                        fc_prefere = @fcp, diagrama = @diag, f_modify = CAST(GETDATE() AS date)
                        {(rehabilitar ? ", f_delete = NULL" : "")}
                    WHERE id = @id AND _deleted = 0
                    """;
                AgregarParamsFletero(upd, f, id);
                if (await upd.ExecuteNonQueryAsync() == 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("El fletero ya no existe.");
                }
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo modificar el fletero: {ex.Message}");
        }
    }

    /// <summary>Baja LÓGICA de un fletero (f_delete = hoy). Nunca borra físico.</summary>
    public async Task<AbmResult> BajaFleteroAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE fletero SET f_delete = CAST(GETDATE() AS date) WHERE id = @id AND _deleted = 0";
            upd.Parameters.Add(new SqlParameter("@id", id));
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El fletero ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo inhabilitar el fletero: {ex.Message}");
        }
    }

    private static (bool, string?) ValidarFletero(FleteroInput f, bool esAlta)
    {
        if (esAlta && string.IsNullOrWhiteSpace(f.IdContrat))
            return (false, "Cargá el código del fletero (id_contrat).");
        if (esAlta && f.IdContrat.Trim().Length > 15)
            return (false, "El código no puede superar 15 caracteres.");
        if (string.IsNullOrWhiteSpace(f.RazonSocial))
            return (false, "Cargá la razón social del fletero.");
        if (f.RazonSocial.Trim().Length > 50)
            return (false, "La razón social no puede superar 50 caracteres.");
        if ((f.Nombre ?? "").Trim().Length > 30)
            return (false, "El nombre corto no puede superar 30 caracteres.");
        if ((f.Cuit ?? "").Trim().Length > 13)
            return (false, "El CUIT no puede superar 13 caracteres.");
        return (true, null);
    }

    private static void AgregarParamsFletero(SqlCommand cmd, FleteroInput f, int id)
    {
        // En alta el INSERT usa @idc; en modifica el UPDATE no lo referencia, pero agregarlo
        // de más no molesta (SqlCommand ignora los parámetros no usados por el texto).
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@idc", (object?)f.IdContrat?.Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@razon", (object?)(f.RazonSocial ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@nombre", (object?)(f.Nombre ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@orden", f.Orden));
        cmd.Parameters.Add(new SqlParameter("@cuit", (object?)(f.Cuit ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@tresp", (object?)(f.TipoResp ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@dom", (object?)(f.Domicilio ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@loc", (object?)(f.Localidad ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cp", (object?)(f.Postal ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@prov", (object?)(f.Provincia ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@tel", (object?)(f.Telefono ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cel", (object?)(f.Celular ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@email", (object?)(f.Email ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cont", (object?)(f.Contacto ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@lp", (object?)(f.IdListaP ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@l2", (object?)(f.IdLista2 ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@mliq", (object?)(f.ModoLiq ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@fcp", (object?)(f.FcPrefere ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@diag", f.Diagrama));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TIPO DE VEHÍCULOS — alta / modifica / baja  (espeja vehiculo_tipo_abm.scx)
    //  ⚠ ANDAMIAJE (05/07/2026): listos pero NO se llaman todavía (pantalla solo
    //  lectura). Tabla `vehiculo_tipo` con dueño FoxPro. Antes de activar: bloquear
    //  el ABM en FoxPro + apagar sync. Es el catálogo más chico (6 filas) → primer
    //  ABM real candidato. PK física id (int, NO identity), PK lógica id_vehicul.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Alta de un tipo de vehículo. Valida código y nombre + unicidad de id_vehicul.</summary>
    public async Task<AbmResult> AltaTipoVehiculoAsync(TipoVehiculoInput t)
    {
        var (ok, err) = ValidarTipoVehiculo(t, esAlta: true);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM vehiculo_tipo WHERE id_vehicul = @c";
                chk.Parameters.Add(new SqlParameter("@c", t.Codigo.Trim()));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo($"El tipo «{t.Codigo.Trim()}» ya está cargado.");
                }
            }

            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM vehiculo_tipo";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO vehiculo_tipo
                        (id, id_vehicul, nombre, pax, id_vehicu2, consumo_mi, consumo_ma,
                         vende, dir_dibujo, f_create, _deleted)
                    VALUES
                        (@id, @cod, @nombre, @pax, @sub, @cmin, @cmax,
                         @vende, @dib, CAST(GETDATE() AS date), 0)
                    """;
                AgregarParamsTipoVehiculo(ins, t, nuevoId);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta el tipo de vehículo: {ex.Message}");
        }
    }

    /// <summary>Modificación de un tipo de vehículo. La PK lógica id_vehicul NO se edita.</summary>
    public async Task<AbmResult> ModificaTipoVehiculoAsync(int id, TipoVehiculoInput t, bool rehabilitar)
    {
        var (ok, err) = ValidarTipoVehiculo(t, esAlta: false);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = $"""
                    UPDATE vehiculo_tipo SET
                        nombre = @nombre, pax = @pax, id_vehicu2 = @sub,
                        consumo_mi = @cmin, consumo_ma = @cmax, vende = @vende,
                        dir_dibujo = @dib, f_modify = CAST(GETDATE() AS date)
                        {(rehabilitar ? ", f_delete = NULL" : "")}
                    WHERE id = @id AND _deleted = 0
                    """;
                AgregarParamsTipoVehiculo(upd, t, id);
                if (await upd.ExecuteNonQueryAsync() == 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("El tipo de vehículo ya no existe.");
                }
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo modificar el tipo de vehículo: {ex.Message}");
        }
    }

    /// <summary>Baja LÓGICA de un tipo de vehículo (f_delete = hoy).</summary>
    public async Task<AbmResult> BajaTipoVehiculoAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE vehiculo_tipo SET f_delete = CAST(GETDATE() AS date) WHERE id = @id AND _deleted = 0";
            upd.Parameters.Add(new SqlParameter("@id", id));
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El tipo de vehículo ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo inhabilitar el tipo de vehículo: {ex.Message}");
        }
    }

    private static (bool, string?) ValidarTipoVehiculo(TipoVehiculoInput t, bool esAlta)
    {
        if (esAlta && string.IsNullOrWhiteSpace(t.Codigo))
            return (false, "Cargá el código del tipo (BUS, VAN, etc.).");
        if (esAlta && t.Codigo.Trim().Length > 15)
            return (false, "El código no puede superar 15 caracteres.");
        if (string.IsNullOrWhiteSpace(t.Nombre))
            return (false, "Cargá el nombre del tipo de vehículo.");
        if (t.Nombre.Trim().Length > 30)
            return (false, "El nombre no puede superar 30 caracteres.");
        if (t.Pax < 0)
            return (false, "La capacidad de pasajeros no puede ser negativa.");
        if (t.ConsumoMin is decimal cmin && t.ConsumoMax is decimal cmax && cmax < cmin)
            return (false, "El consumo máximo no puede ser menor que el mínimo.");
        return (true, null);
    }

    private static void AgregarParamsTipoVehiculo(SqlCommand cmd, TipoVehiculoInput t, int id)
    {
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@cod", (object?)t.Codigo?.Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@nombre", (object?)(t.Nombre ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@pax", t.Pax));
        cmd.Parameters.Add(new SqlParameter("@sub", (object?)(t.Subtipo ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cmin", (object?)t.ConsumoMin ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cmax", (object?)t.ConsumoMax ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@vende", t.Vende));
        cmd.Parameters.Add(new SqlParameter("@dib", (object?)(t.DirDibujo ?? "").Trim() ?? DBNull.Value));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TRÁFICO — Cabeceras · Francos · Viáticos + catálogos de Viático
    //  ⚠ ANDAMIAJE (05/07/2026): construido pero NO se llama todavía (pantallas
    //  solo lectura). Tablas con dueño FoxPro. Antes de activar: flag en
    //  AbmFeatureFlags = true + bloquear el ABM en FoxPro + apagar sync + replicar
    //  las 5 tablas al server nuevo (hoy NO están allá).
    //  🐛 DIFERENCIA con Fleteros/TipoVehiculo: estas tablas hacen BAJA FÍSICA
    //  (DELETE), no lógica — NO tienen f_delete/f_create. Los INSERT tampoco setean
    //  esos campos (no existen). Solo _deleted = 0 (metadata de la réplica).
    // ═══════════════════════════════════════════════════════════════════════

    // ── Cabeceras/Recorridos (cabecera_recorrido_abm.scx) ────────────────────

    /// <summary>Alta de una cabecera. codigo = PK lógica; valida no vacío + 1ª descripción +
    /// recorrido, y unicidad de codigo. id = MAX(id)+1 (no identity). Baja será física.</summary>
    public async Task<AbmResult> AltaCabeceraAsync(CabeceraInput c)
    {
        if (string.IsNullOrWhiteSpace(c.Codigo)) return AbmResult.Fallo("Cargá el código de la cabecera.");
        if (c.Codigo.Trim().Length > 20) return AbmResult.Fallo("El código no puede superar 20 caracteres.");
        if (string.IsNullOrWhiteSpace(c.Nombre)) return AbmResult.Fallo("Cargá la 1ª descripción.");
        if (string.IsNullOrWhiteSpace(c.Recorrido)) return AbmResult.Fallo("Cargá el recorrido.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM cabecera WHERE codigo = @c AND _deleted = 0";
                chk.Parameters.Add(new SqlParameter("@c", c.Codigo.Trim()));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo($"La cabecera «{c.Codigo.Trim()}» ya está cargada.");
                }
            }

            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM cabecera";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO cabecera (id, codigo, nombre, nombre1, nombre2, recorrido, _deleted)
                    VALUES (@id, @cod, @n0, @n1, @n2, @rec, 0)
                    """;
                AgregarParamsCabecera(ins, c, nuevoId);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta la cabecera: {ex.Message}");
        }
    }

    /// <summary>Modificación de una cabecera. El código (PK lógica) NO se edita.</summary>
    public async Task<AbmResult> ModificaCabeceraAsync(int id, CabeceraInput c)
    {
        if (string.IsNullOrWhiteSpace(c.Nombre)) return AbmResult.Fallo("Cargá la 1ª descripción.");
        if (string.IsNullOrWhiteSpace(c.Recorrido)) return AbmResult.Fallo("Cargá el recorrido.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = """
                UPDATE cabecera SET nombre = @n0, nombre1 = @n1, nombre2 = @n2, recorrido = @rec
                WHERE id = @id AND _deleted = 0
                """;
            AgregarParamsCabecera(upd, c, id);
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("La cabecera ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo modificar la cabecera: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de una cabecera (DELETE — así lo hace el FoxPro; no hay f_delete).</summary>
    public async Task<AbmResult> BajaCabeceraAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM cabecera WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("La cabecera ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar la cabecera: {ex.Message}");
        }
    }

    private static void AgregarParamsCabecera(SqlCommand cmd, CabeceraInput c, int id)
    {
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@cod", (object?)c.Codigo?.Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@n0", (object?)(c.Nombre ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@n1", (object?)(c.Nombre1 ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@n2", (object?)(c.Nombre2 ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@rec", (object?)(c.Recorrido ?? "").Trim() ?? DBNull.Value));
    }

    // ── Francos (chofer_franco_abm.scx = alta MASIVA + baja puntual) ─────────

    /// <summary>Alta MASIVA de francos (chofer_franco_abm.scx): para cada chofer × cada fecha
    /// del rango, inserta un franco si NO existe ya uno ese día (evita duplicados). Devuelve la
    /// cantidad insertada en el Id del AbmResult. Baja física para cada uno vía BajaFrancoAsync.</summary>
    public async Task<AbmResult> AltaFrancosAsync(IReadOnlyList<string> idsChofer, IReadOnlyList<DateOnly> fechas, string codigo, string motivo)
    {
        if (idsChofer is null || idsChofer.Count == 0) return AbmResult.Fallo("Elegí al menos un chofer.");
        if (fechas is null || fechas.Count == 0) return AbmResult.Fallo("Cargá al menos una fecha de franco.");
        if (string.IsNullOrWhiteSpace(codigo)) return AbmResult.Fallo("Elegí el motivo del franco.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            int insertados = 0;
            foreach (var idc in idsChofer)
            {
                foreach (var f in fechas)
                {
                    // ¿ya tiene franco ese día?
                    await using (var chk = conn.CreateCommand())
                    {
                        chk.Transaction = tx;
                        chk.CommandText = "SELECT COUNT(*) FROM chofer_franco WHERE id_chofer = @c AND fecha = @f AND _deleted = 0";
                        chk.Parameters.Add(new SqlParameter("@c", idc));
                        chk.Parameters.Add(new SqlParameter("@f", f.ToDateTime(TimeOnly.MinValue)));
                        if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0) continue;
                    }

                    int nuevoId;
                    await using (var mx = conn.CreateCommand())
                    {
                        mx.Transaction = tx;
                        mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM chofer_franco";
                        nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
                    }

                    await using (var ins = conn.CreateCommand())
                    {
                        ins.Transaction = tx;
                        ins.CommandText = """
                            INSERT INTO chofer_franco (id, id_chofer, codigo, motivo, fecha, trabajo, _deleted)
                            VALUES (@id, @c, @cod, @mot, @f, 0, 0)
                            """;
                        ins.Parameters.Add(new SqlParameter("@id", nuevoId));
                        ins.Parameters.Add(new SqlParameter("@c", idc));
                        ins.Parameters.Add(new SqlParameter("@cod", codigo.Trim()));
                        ins.Parameters.Add(new SqlParameter("@mot", (object?)(motivo ?? "").Trim() ?? DBNull.Value));
                        ins.Parameters.Add(new SqlParameter("@f", f.ToDateTime(TimeOnly.MinValue)));
                        await ins.ExecuteNonQueryAsync();
                        insertados++;
                    }
                }
            }
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(insertados);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudieron generar los francos: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de un franco puntual (chofer_franco.scx → beliminar: DELETE por id).</summary>
    public async Task<AbmResult> BajaFrancoAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM chofer_franco WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El franco ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar el franco: {ex.Message}");
        }
    }

    // ── Viáticos (chofer_viatico_abm.scx) ────────────────────────────────────

    /// <summary>Alta de un viático. Valida chofer, motivo, forma de liquidación, forma de pago
    /// e importe > 0 (como el FoxPro). id = MAX(id)+1. Baja física.</summary>
    public async Task<AbmResult> AltaViaticoAsync(ViaticoInput v)
    {
        var (ok, err) = ValidarViatico(v);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM chofer_viatico";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO chofer_viatico
                        (id, fecha, id_chofer, id_motivo, id_liquida, forma_pago, importe, f_pago, _deleted)
                    VALUES
                        (@id, @f, @cho, @mot, @liq, @fpg, @imp, @fp, 0)
                    """;
                AgregarParamsViatico(ins, v, nuevoId);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta el viático: {ex.Message}");
        }
    }

    /// <summary>Modificación de un viático.</summary>
    public async Task<AbmResult> ModificaViaticoAsync(int id, ViaticoInput v)
    {
        var (ok, err) = ValidarViatico(v);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = """
                UPDATE chofer_viatico SET
                    fecha = @f, id_chofer = @cho, id_motivo = @mot, id_liquida = @liq,
                    forma_pago = @fpg, importe = @imp, f_pago = @fp
                WHERE id = @id AND _deleted = 0
                """;
            AgregarParamsViatico(upd, v, id);
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El viático ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo modificar el viático: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de un viático (DELETE por id — así lo hace el FoxPro).</summary>
    public async Task<AbmResult> BajaViaticoAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM chofer_viatico WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El viático ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar el viático: {ex.Message}");
        }
    }

    private static (bool, string?) ValidarViatico(ViaticoInput v)
    {
        if (string.IsNullOrWhiteSpace(v.IdChofer)) return (false, "Elegí el conductor.");
        if (v.IdMotivo <= 0) return (false, "Elegí el motivo del viático.");
        if (v.IdLiquida <= 0) return (false, "Elegí la forma de liquidación.");
        if (string.IsNullOrWhiteSpace(v.FormaPago)) return (false, "Elegí la forma de pago.");
        if (v.Importe <= 0) return (false, "Cargá el importe (mayor a 0).");
        return (true, null);
    }

    private static void AgregarParamsViatico(SqlCommand cmd, ViaticoInput v, int id)
    {
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@f", v.Fecha.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(new SqlParameter("@cho", (object?)v.IdChofer?.Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@mot", v.IdMotivo));
        cmd.Parameters.Add(new SqlParameter("@liq", v.IdLiquida));
        cmd.Parameters.Add(new SqlParameter("@fpg", (object?)(v.FormaPago ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@imp", v.Importe));
        cmd.Parameters.Add(new SqlParameter("@fp", v.FPago is DateOnly fp ? fp.ToDateTime(TimeOnly.MinValue) : (object)DBNull.Value));
    }

    // ── Catálogos de Viático: Motivo y Forma de Liquidación (idénticos) ──────
    //  chofer_viatico_motivo_abm.scx / chofer_viatico_liquida_abm.scx: id (MAX+1)
    //  + nombre (UPPER, único). Baja física. Un solo par de helpers parametrizado
    //  por nombre de tabla (constante del código, no del usuario → seguro).

    public Task<AbmResult> AltaViaticoMotivoAsync(string nombre) => AltaCatalogoAsync("chofer_viatico_motivo", nombre, "el motivo");
    public Task<AbmResult> ModificaViaticoMotivoAsync(int id, string nombre) => ModificaCatalogoAsync("chofer_viatico_motivo", id, nombre);
    public Task<AbmResult> BajaViaticoMotivoAsync(int id) => BajaCatalogoAsync("chofer_viatico_motivo", id);

    public Task<AbmResult> AltaViaticoLiquidaAsync(string nombre) => AltaCatalogoAsync("chofer_viatico_liquida", nombre, "la forma de liquidación");
    public Task<AbmResult> ModificaViaticoLiquidaAsync(int id, string nombre) => ModificaCatalogoAsync("chofer_viatico_liquida", id, nombre);
    public Task<AbmResult> BajaViaticoLiquidaAsync(int id) => BajaCatalogoAsync("chofer_viatico_liquida", id);

    private async Task<AbmResult> AltaCatalogoAsync(string tabla, string nombre, string etiqueta)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return AbmResult.Fallo($"Cargá el nombre de {etiqueta}.");
        if (nombre.Trim().Length > 60) return AbmResult.Fallo("El nombre no puede superar 60 caracteres.");
        var val = nombre.Trim().ToUpperInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = $"SELECT COUNT(*) FROM {tabla} WHERE nombre = @n AND _deleted = 0";
                chk.Parameters.Add(new SqlParameter("@n", val));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo($"«{val}» ya está cargado.");
                }
            }

            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = $"SELECT ISNULL(MAX(id), 0) + 1 FROM {tabla}";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = $"INSERT INTO {tabla} (id, nombre, _deleted) VALUES (@id, @n, 0)";
                ins.Parameters.Add(new SqlParameter("@id", nuevoId));
                ins.Parameters.Add(new SqlParameter("@n", val));
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta: {ex.Message}");
        }
    }

    private async Task<AbmResult> ModificaCatalogoAsync(string tabla, int id, string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return AbmResult.Fallo("Cargá el nombre.");
        if (nombre.Trim().Length > 60) return AbmResult.Fallo("El nombre no puede superar 60 caracteres.");
        var val = nombre.Trim().ToUpperInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = $"UPDATE {tabla} SET nombre = @n WHERE id = @id AND _deleted = 0";
            upd.Parameters.Add(new SqlParameter("@n", val));
            upd.Parameters.Add(new SqlParameter("@id", id));
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El registro ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo modificar: {ex.Message}");
        }
    }

    private async Task<AbmResult> BajaCatalogoAsync(string tabla, int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = $"DELETE FROM {tabla} WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El registro ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MÓDULO RESERVAS — CATÁLOGOS: Operadores · Grupos · Destinos (06/07/2026)
    //  ⚠ ANDAMIAJE: construido pero NO se llama todavía (pantallas solo lectura).
    //  Antes de activar cada uno: flag en AbmFeatureFlags = true + bloquear el ABM
    //  en FoxPro + apagar la sync de esa tabla (regla strangler, skill abm-metrocar).
    //  🐛 Las 3 tablas hacen BAJA FÍSICA (DELETE) — NO tienen f_delete/f_create; los
    //  INSERT solo setean _deleted = 0 (metadata de la réplica). id = MAX(id)+1 (no identity).
    //  Grupo A del plan Buslink: destino + cliente_operador (cutover temprano);
    //  Grupo B: cliente_grupo (cambia de dueño el día D). Planos: docs/PlanoFoxPro/catalogos/.
    // ═══════════════════════════════════════════════════════════════════════

    // ── Operadores (cliente_operador_abm.scx) ────────────────────────────────
    //  id_operado = PK lógica GLOBAL (no por cliente). Obligatorios: código, cliente
    //  (debe existir) y nombre. email valida '@' y se graba en minúscula; el resto MAYÚSCULAS.

    /// <summary>Alta de un operador. Valida código/cliente/nombre + que el cliente exista +
    /// unicidad global de id_operado. id físico = MAX(id)+1. Baja será física.</summary>
    public async Task<AbmResult> AltaOperadorAsync(OperadorInput o)
    {
        var (ok, err) = ValidarOperador(o, esAlta: true);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // El cliente debe existir (como el FoxPro).
            await using (var chkc = conn.CreateCommand())
            {
                chkc.Transaction = tx;
                chkc.CommandText = "SELECT COUNT(*) FROM cliente WHERE LTRIM(RTRIM(id_cliente)) = @cli AND _deleted = 0";
                chkc.Parameters.Add(new SqlParameter("@cli", o.IdCliente.Trim()));
                if ((int)(await chkc.ExecuteScalarAsync() ?? 0) == 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo($"El cliente «{o.IdCliente.Trim()}» no existe.");
                }
            }

            // Anti-duplicado global de id_operado.
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM cliente_operador WHERE LTRIM(RTRIM(id_operado)) = @c AND _deleted = 0";
                chk.Parameters.Add(new SqlParameter("@c", o.IdOperador.Trim()));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo($"El operador «{o.IdOperador.Trim()}» ya está cargado.");
                }
            }

            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM cliente_operador";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO cliente_operador
                        (id, id_operado, id_cliente, nombre, telefono, celular, interno, email, comentario, _deleted)
                    VALUES
                        (@id, @cod, @cli, @nom, @tel, @cel, @int, @email, @com, 0)
                    """;
                AgregarParamsOperador(ins, o, nuevoId);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta el operador: {ex.Message}");
        }
    }

    /// <summary>Modificación de un operador (el código PK lógica NO se edita; sí puede cambiar de
    /// cliente, que se revalida que exista). Espeja el UPDATE de cliente_operador_abm.scx.</summary>
    public async Task<AbmResult> ModificaOperadorAsync(int id, OperadorInput o)
    {
        var (ok, err) = ValidarOperador(o, esAlta: false);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var chkc = conn.CreateCommand())
            {
                chkc.Transaction = tx;
                chkc.CommandText = "SELECT COUNT(*) FROM cliente WHERE LTRIM(RTRIM(id_cliente)) = @cli AND _deleted = 0";
                chkc.Parameters.Add(new SqlParameter("@cli", o.IdCliente.Trim()));
                if ((int)(await chkc.ExecuteScalarAsync() ?? 0) == 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo($"El cliente «{o.IdCliente.Trim()}» no existe.");
                }
            }

            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE cliente_operador SET
                        id_cliente = @cli, nombre = @nom, telefono = @tel, celular = @cel,
                        interno = @int, email = @email, comentario = @com
                    WHERE id = @id AND _deleted = 0
                    """;
                AgregarParamsOperador(upd, o, id);
                if (await upd.ExecuteNonQueryAsync() == 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("El operador ya no existe.");
                }
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo modificar el operador: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de un operador (DELETE — así lo hace el FoxPro; no hay f_delete).
    /// ⚠ El FoxPro no valida referencias: si el operador tiene viajes históricos, viaje.id_operado
    /// queda huérfano. Acá lo permitimos igual (fidelidad), pero podría avisarse.</summary>
    public async Task<AbmResult> BajaOperadorAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM cliente_operador WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El operador ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar el operador: {ex.Message}");
        }
    }

    private static (bool, string?) ValidarOperador(OperadorInput o, bool esAlta)
    {
        if (esAlta && string.IsNullOrWhiteSpace(o.IdOperador))
            return (false, "Cargá el código de operador.");
        if (esAlta && o.IdOperador.Trim().Length > 15)
            return (false, "El código de operador no puede superar 15 caracteres.");
        if (string.IsNullOrWhiteSpace(o.IdCliente))
            return (false, "Cargá el código del cliente donde trabaja el operador.");
        if (string.IsNullOrWhiteSpace(o.Nombre))
            return (false, "Cargá el nombre del operador.");
        if ((o.Email ?? "").Trim().Length > 0 && !(o.Email ?? "").Contains('@'))
            return (false, "El e-mail debe contener el carácter @.");
        return (true, null);
    }

    private static void AgregarParamsOperador(SqlCommand cmd, OperadorInput o, int id)
    {
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@cod", (object?)o.IdOperador?.Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cli", (object?)o.IdCliente?.Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@nom", (object?)(o.Nombre ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@tel", (object?)(o.Telefono ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cel", (object?)(o.Celular ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@int", (object?)(o.Interno ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@email", (object?)(o.Email ?? "").Trim().ToLowerInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@com", (object?)(o.Comentario ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
    }

    // ── Destinos (destino_abm.scx) ───────────────────────────────────────────
    //  destino = nombre (PK lógica, MAYÚSCULAS). Solo el nombre es obligatorio. Baja física.
    //  🐛 Bug heredado corregido: el modifica del FoxPro hace `contacto = contacto` (no guarda
    //  el contacto). Acá SÍ se graba el contacto editado.

    /// <summary>Alta de un destino. Solo el nombre es obligatorio + unicidad por nombre. Todo en
    /// MAYÚSCULAS. id físico = MAX(id)+1. Baja será física.</summary>
    public async Task<AbmResult> AltaDestinoAsync(DestinoInput d)
    {
        if (string.IsNullOrWhiteSpace(d.Destino)) return AbmResult.Fallo("Cargá el destino.");
        if (d.Destino.Trim().Length > 50) return AbmResult.Fallo("El destino no puede superar 50 caracteres.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM destino WHERE LTRIM(RTRIM(destino)) = @d AND _deleted = 0";
                chk.Parameters.Add(new SqlParameter("@d", d.Destino.Trim().ToUpperInvariant()));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo($"El destino «{d.Destino.Trim().ToUpperInvariant()}» ya está cargado.");
                }
            }

            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM destino";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO destino
                        (id, destino, direccion, localidad, telefono, contacto, correo, cabecera, mas100km, _deleted)
                    VALUES
                        (@id, @dest, @dir, @loc, @tel, @cont, @cor, @cab, @m100, 0)
                    """;
                AgregarParamsDestino(ins, d, nuevoId);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta el destino: {ex.Message}");
        }
    }

    /// <summary>Modificación de un destino (por id). El nombre SÍ es editable en el FoxPro.
    /// 🐛 A diferencia del FoxPro, acá SÍ se graba el contacto (bug `contacto=contacto` corregido).</summary>
    public async Task<AbmResult> ModificaDestinoAsync(int id, DestinoInput d)
    {
        if (string.IsNullOrWhiteSpace(d.Destino)) return AbmResult.Fallo("Cargá el destino.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = """
                UPDATE destino SET
                    destino = @dest, direccion = @dir, localidad = @loc, telefono = @tel,
                    contacto = @cont, correo = @cor, mas100km = @m100, cabecera = @cab
                WHERE id = @id AND _deleted = 0
                """;
            AgregarParamsDestino(upd, d, id);
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El destino ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo modificar el destino: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de un destino (DELETE — así lo hace el FoxPro; no hay f_delete).</summary>
    public async Task<AbmResult> BajaDestinoAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM destino WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El destino ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar el destino: {ex.Message}");
        }
    }

    /// <summary>Alta de una localidad al catálogo satélite destino_localidad (botón "Nueva Localidad"
    /// del destino_abm.scx). Anti-duplicado por nombre. UPPER.</summary>
    public async Task<AbmResult> AltaLocalidadAsync(string localidad)
    {
        localidad = (localidad ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(localidad)) return AbmResult.Fallo("Cargá la localidad.");
        if (localidad.Length > 100) return AbmResult.Fallo("La localidad no puede superar 100 caracteres.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM destino_localidad WHERE LTRIM(RTRIM(localidad)) = @l AND _deleted = 0";
                chk.Parameters.Add(new SqlParameter("@l", localidad));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("La localidad ya está cargada.");
                }
            }
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO destino_localidad (localidad, _deleted) VALUES (@l, 0)";
                ins.Parameters.Add(new SqlParameter("@l", localidad));
                await ins.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo agregar la localidad: {ex.Message}");
        }
    }

    private static void AgregarParamsDestino(SqlCommand cmd, DestinoInput d, int id)
    {
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@dest", (object?)(d.Destino ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@dir", (object?)(d.Direccion ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@loc", (object?)(d.Localidad ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@tel", (object?)(d.Telefono ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cont", (object?)(d.Contacto ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cor", (object?)(d.Correo ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cab", (object?)(d.Cabecera ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@m100", d.Mas100Km));
    }

    // ── Grupos (cliente_grupo_abm.scx) ───────────────────────────────────────
    //  ⚠ El ABM de Grupos NO tiene alta (los grupos nacen desde Reservas). Solo modifica y baja,
    //  y ambas operan EN CASCADA sobre los viajes del grupo (por la dupla desnormalizada
    //  id_cliente+grupo). Baja = cancelación masiva de viajes con motivo + DELETE del grupo
    //  (solo si no hay FINALIZADO/FACTURADO). Ver plano CLIENTE_GRUPO_ABM.md.

    /// <summary>Baja de un grupo (cliente_grupo_abm.scx modo "baja"). NO es un simple delete: es una
    /// cancelación masiva de los viajes del grupo con motivo. Reglas verificadas contra el fuente:
    ///  - Hay ASIGNADO (o CURSO) → bloquea (hay que pasarlos a SIN ASIGNAR antes).
    ///  - Hay SIN ASIGNAR y nada ASIGNADO → cancela: si NO hay FINALIZADO/FACTURADO cancela TODO el
    ///    grupo; si los hay, solo los SIN ASIGNAR. Requiere <paramref name="idMotivo"/> (&gt; 0).
    ///  - Solo FINALIZADO/FACTURADO o nada cancelable → no hace nada.
    ///  - DELETE del grupo SOLO si no había FINALIZADO/FACTURADO (nHayF = 0).
    /// El UI debe haber consultado GetViajesGrupoPorEstadoAsync y pedido el motivo antes de llamar.</summary>
    public async Task<AbmResult> BajaGrupoAsync(int id, string idCliente, string nombreGrupo, int idMotivo)
    {
        idCliente = (idCliente ?? "").Trim();
        nombreGrupo = (nombreGrupo ?? "").Trim();
        if (string.IsNullOrWhiteSpace(idCliente) || string.IsNullOrWhiteSpace(nombreGrupo))
            return AbmResult.Fallo("Faltan datos del grupo (cliente / nombre).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Reconteo por estado DENTRO de la tx (no confiar en lo que vio el UI).
            var (haySA, hayAsig, hayFin) = (0, 0, 0);
            await using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    SELECT RTRIM(ISNULL(estado_via,'')) AS Estado, COUNT(*) AS Cnt
                    FROM viaje
                    WHERE _deleted = 0 AND LTRIM(RTRIM(id_cliente)) = @cli AND LTRIM(RTRIM(grupo)) = @grp
                    GROUP BY estado_via
                    """;
                q.Parameters.Add(new SqlParameter("@cli", idCliente));
                q.Parameters.Add(new SqlParameter("@grp", nombreGrupo));
                await using var rd = await q.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var est = rd.GetString(0).ToUpperInvariant();
                    var cnt = rd.GetInt32(1);
                    if (est == "SIN ASIGNAR") haySA += cnt;
                    else if (est == "ASIGNADO" || est == "CURSO" || est == "EN CURSO") hayAsig += cnt;
                    else if (est == "FINALIZADO" || est == "FACTURADO") hayFin += cnt;
                }
            }

            // Bloqueo: hay ASIGNADO/CURSO → no se puede eliminar.
            if (hayAsig > 0)
            {
                await tx.RollbackAsync();
                return AbmResult.Fallo("Hay reservas en estado ASIGNADO: no se puede eliminar el grupo " +
                                       "hasta que se hayan pasado a SIN ASIGNAR.");
            }

            // Cancelación masiva de los SIN ASIGNAR (con motivo obligatorio).
            if (haySA > 0)
            {
                if (idMotivo <= 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("Falta el motivo de cancelación.");
                }
                await using var can = conn.CreateCommand();
                can.Transaction = tx;
                // Si NO hay FINALIZADO/FACTURADO, cancela TODO el grupo; si los hay, solo los SIN ASIGNAR.
                var filtro = hayFin == 0 ? "" : " AND estado_via = 'SIN ASIGNAR'";
                can.CommandText = $"""
                    UPDATE viaje SET
                        estado_via = 'CANCELADO', interno = 0, id_motivo = @mot,
                        id_vehicul = '', id_chofer = '', nombre_cho = '', franco = 0
                    WHERE _deleted = 0 AND LTRIM(RTRIM(id_cliente)) = @cli AND LTRIM(RTRIM(grupo)) = @grp{filtro}
                    """;
                can.Parameters.Add(new SqlParameter("@mot", idMotivo));
                can.Parameters.Add(new SqlParameter("@cli", idCliente));
                can.Parameters.Add(new SqlParameter("@grp", nombreGrupo));
                await can.ExecuteNonQueryAsync();
            }
            else if (hayFin == 0)
            {
                // No hay SIN ASIGNAR ni FINALIZADO/FACTURADO → no hay nada que cancelar (grupo vacío
                // o solo CANCELADO). El FoxPro igual llega al DELETE si nHayF=0.
            }

            // DELETE del grupo SOLO si no hay historia FINALIZADO/FACTURADO.
            if (hayFin == 0)
            {
                await using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM cliente_grupo WHERE id = @id";
                del.Parameters.Add(new SqlParameter("@id", id));
                await del.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo eliminar el grupo: {ex.Message}");
        }
    }

    /// <summary>Modificación de un grupo (cliente_grupo_abm.scx modo "modifica"): renombrar y/o cambiar
    /// fecha fin. Arrastra los viajes por la dupla (id_cliente, grupo). Reglas verificadas:
    ///  - Bloqueado si el grupo está facturado (f_grupo_fc con valor).
    ///  - Si el nombre cambia, valida que el nuevo no exista para ese cliente.
    ///  - Estados: CANCELADO se ignora; SIN ASIGNAR/ASIGNADO/FINALIZADO = modificables; FACTURADO = bloqueante.
    ///    Si NO hay ninguno modificable (todo FACTURADO/cancelado) → bloquea.
    ///  - UPDATE viaje SET grupo, f_grupo_fi + UPDATE cliente_grupo SET nombre, f_grupo_fi, f_grupo_in.</summary>
    public async Task<AbmResult> ModificaGrupoAsync(int id, string idCliente, string nombreOriginal,
        string nombreNuevo, DateOnly? fInicio, DateOnly? fFin)
    {
        idCliente = (idCliente ?? "").Trim();
        nombreOriginal = (nombreOriginal ?? "").Trim();
        nombreNuevo = (nombreNuevo ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(nombreNuevo)) return AbmResult.Fallo("Cargá el nuevo nombre del grupo.");
        if (fFin is null) return AbmResult.Fallo("Cargá la fecha de partida (fin) del grupo.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Candado por facturación.
            await using (var chkf = conn.CreateCommand())
            {
                chkf.Transaction = tx;
                chkf.CommandText = "SELECT f_grupo_fc FROM cliente_grupo WHERE id = @id AND _deleted = 0";
                chkf.Parameters.Add(new SqlParameter("@id", id));
                var fc = await chkf.ExecuteScalarAsync();
                if (fc is null) { await tx.RollbackAsync(); return AbmResult.Fallo("El grupo ya no existe."); }
                if (fc is not DBNull) { await tx.RollbackAsync(); return AbmResult.Fallo("El grupo está facturado: no se puede modificar."); }
            }

            // Si cambió el nombre, validar que el nuevo no exista para ese cliente.
            if (!string.Equals(nombreOriginal, nombreNuevo, StringComparison.OrdinalIgnoreCase))
            {
                await using var chkn = conn.CreateCommand();
                chkn.Transaction = tx;
                chkn.CommandText = """
                    SELECT COUNT(*) FROM cliente_grupo
                    WHERE _deleted = 0 AND LTRIM(RTRIM(id_cliente)) = @cli AND LTRIM(RTRIM(nombre)) = @nom
                    """;
                chkn.Parameters.Add(new SqlParameter("@cli", idCliente));
                chkn.Parameters.Add(new SqlParameter("@nom", nombreNuevo));
                if ((int)(await chkn.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("Ese nombre de grupo ya existe para ese cliente.");
                }
            }

            // Clasificación de estados (modifica): modificable = SIN ASIGNAR/ASIGNADO/FINALIZADO.
            int hayMod = 0, hayFac = 0;
            await using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    SELECT RTRIM(ISNULL(estado_via,'')) AS Estado, COUNT(*) AS Cnt
                    FROM viaje
                    WHERE _deleted = 0 AND LTRIM(RTRIM(id_cliente)) = @cli AND LTRIM(RTRIM(grupo)) = @grp
                    GROUP BY estado_via
                    """;
                q.Parameters.Add(new SqlParameter("@cli", idCliente));
                q.Parameters.Add(new SqlParameter("@grp", nombreOriginal));
                await using var rd = await q.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var est = rd.GetString(0).ToUpperInvariant();
                    var cnt = rd.GetInt32(1);
                    if (est is "SIN ASIGNAR" or "ASIGNADO" or "FINALIZADO") hayMod += cnt;
                    else if (est == "FACTURADO") hayFac += cnt;
                    // CANCELADO se ignora
                }
            }

            // Si hay viajes pero ninguno modificable → bloquea (todo FACTURADO/cancelado).
            var totalViajes = hayMod + hayFac;
            if (totalViajes > 0 && hayMod == 0)
            {
                await tx.RollbackAsync();
                return AbmResult.Fallo("Los estados de las reservas ya no pueden modificarse " +
                                       "(se encuentran todas FACTURADAS).");
            }

            // Arrastre a los viajes (solo si hay modificables).
            if (hayMod > 0)
            {
                await using var uv = conn.CreateCommand();
                uv.Transaction = tx;
                uv.CommandText = """
                    UPDATE viaje SET grupo = @nom, f_grupo_fi = @ffin
                    WHERE _deleted = 0 AND LTRIM(RTRIM(id_cliente)) = @cli AND LTRIM(RTRIM(grupo)) = @grp
                    """;
                uv.Parameters.Add(new SqlParameter("@nom", nombreNuevo));
                uv.Parameters.Add(new SqlParameter("@ffin", fFin.Value.ToDateTime(TimeOnly.MinValue)));
                uv.Parameters.Add(new SqlParameter("@cli", idCliente));
                uv.Parameters.Add(new SqlParameter("@grp", nombreOriginal));
                await uv.ExecuteNonQueryAsync();
            }

            // Update de la cabecera del grupo.
            await using (var ug = conn.CreateCommand())
            {
                ug.Transaction = tx;
                ug.CommandText = """
                    UPDATE cliente_grupo SET nombre = @nom, f_grupo_fi = @ffin, f_grupo_in = @fini
                    WHERE id = @id AND _deleted = 0
                    """;
                ug.Parameters.Add(new SqlParameter("@nom", nombreNuevo));
                ug.Parameters.Add(new SqlParameter("@ffin", fFin.Value.ToDateTime(TimeOnly.MinValue)));
                ug.Parameters.Add(new SqlParameter("@fini", fInicio is DateOnly fi ? fi.ToDateTime(TimeOnly.MinValue) : (object)DBNull.Value));
                ug.Parameters.Add(new SqlParameter("@id", id));
                if (await ug.ExecuteNonQueryAsync() == 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("El grupo ya no existe.");
                }
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo modificar el grupo: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MÓDULO TRÁFICO — Guardia · Contactos · Rubros · Voucher (06/07/2026)
    //  ANDAMIAJE: escritura construida pero apagada por AbmFeatureFlags (doble candado:
    //  el botón Grabar del editor está Disabled Y estos métodos abortan si el flag está en
    //  false). Al activar: flag=true + bloquear FoxPro + apagar sync (skill abm-metrocar).
    //  🐛 Baja FÍSICA (DELETE) en las 3 tablas. id = MAX(id)+1 (no identity).
    //  ⚠ `estacion` es catálogo COMPARTIDO con Combustible → coordinar dueño único.
    // ═══════════════════════════════════════════════════════════════════════

    // ── Guardias (trafico_guardia_abm.scx) ───────────────────────────────────
    //  Validaciones del audita_carga: interno/chofer/fechas obligatorios + hs_inicio < hs_fin.
    //  Modifica bloqueado si fpago cargado (guardia ya pagada). fpago NO se edita acá (lo
    //  escribe la Liquidación de choferes). 🐛 Bug del fuente `Wher Id` (typo) — no copiar.

    /// <summary>Alta de una guardia (trafico_guardia_abm.scx modo "alta"). Abortada por el flag.</summary>
    public async Task<AbmResult> AltaGuardiaAsync(GuardiaInput g)
    {
        if (!AbmFeatureFlags.GuardiaAbmActivo)
            return AbmResult.Fallo("La escritura de Guardias todavía no está habilitada (sigue en FoxPro).");
        var (ok, err) = ValidarGuardia(g);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM viaje_guardia";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO viaje_guardia
                        (id, interno, id_vehicul, id_chofer, nombre_cho, franco, fecha, hs_inicio, hs_fin, _deleted)
                    VALUES
                        (@id, @int, @veh, @cho, @nom, @frc, @fec, @ini, @fin, 0)
                    """;
                AgregarParamsGuardia(ins, g, nuevoId);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta la guardia: {ex.Message}");
        }
    }

    /// <summary>Modificación de una guardia (trafico_guardia_abm.scx modo "modifica"). Abortada por
    /// el flag. Bloqueada si la guardia ya tiene fecha de pago (fpago).</summary>
    public async Task<AbmResult> ModificaGuardiaAsync(int id, GuardiaInput g)
    {
        if (!AbmFeatureFlags.GuardiaAbmActivo)
            return AbmResult.Fallo("La escritura de Guardias todavía no está habilitada (sigue en FoxPro).");
        var (ok, err) = ValidarGuardia(g);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Candado: guardia ya pagada no se modifica (como el FoxPro).
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM viaje_guardia WHERE id = @id AND fpago IS NOT NULL";
                chk.Parameters.Add(new SqlParameter("@id", id));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("La guardia ya fue pagada — no se puede modificar.");
                }
            }

            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE viaje_guardia SET
                        interno = @int, id_vehicul = @veh, id_chofer = @cho, nombre_cho = @nom,
                        franco = @frc, fecha = @fec, hs_inicio = @ini, hs_fin = @fin
                    WHERE id = @id AND _deleted = 0
                    """;
                AgregarParamsGuardia(upd, g, id);
                if (await upd.ExecuteNonQueryAsync() == 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("La guardia ya no existe.");
                }
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo modificar la guardia: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de una guardia (DELETE — así lo hace el FoxPro). Abortada por el flag.</summary>
    public async Task<AbmResult> BajaGuardiaAsync(int id)
    {
        if (!AbmFeatureFlags.GuardiaAbmActivo)
            return AbmResult.Fallo("La escritura de Guardias todavía no está habilitada (sigue en FoxPro).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM viaje_guardia WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("La guardia ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar la guardia: {ex.Message}");
        }
    }

    private static (bool, string?) ValidarGuardia(GuardiaInput g)
    {
        if (g.Interno <= 0 && string.IsNullOrWhiteSpace(g.IdVehiculo))
            return (false, "Cargá el vehículo (interno) de la guardia.");
        if (string.IsNullOrWhiteSpace(g.IdChofer))
            return (false, "Cargá el chofer de la guardia.");
        if (g.HsInicio >= g.HsFin)
            return (false, "El inicio de la guardia debe ser anterior al fin.");
        return (true, null);
    }

    private static void AgregarParamsGuardia(SqlCommand cmd, GuardiaInput g, int id)
    {
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@int", g.Interno));
        cmd.Parameters.Add(new SqlParameter("@veh", (object?)(g.IdVehiculo ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cho", (object?)(g.IdChofer ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@nom", (object?)(g.Nombre ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@frc", g.Franco));
        cmd.Parameters.Add(new SqlParameter("@fec", g.Fecha.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(new SqlParameter("@ini", g.HsInicio));
        cmd.Parameters.Add(new SqlParameter("@fin", g.HsFin));
    }

    // ── Contactos / Proveedores (estacion_abm.scx) ───────────────────────────
    //  nombre + rubro obligatorios; no duplicar (nombre + rubro). Email valida '@'. Baja física.

    /// <summary>Alta de un contacto/proveedor (estacion_abm.scx modo "alta"). Abortada por el flag.</summary>
    public async Task<AbmResult> AltaContactoAsync(ContactoInput c)
    {
        if (!AbmFeatureFlags.ContactosAbmActivo)
            return AbmResult.Fallo("La escritura de Contactos todavía no está habilitada (sigue en FoxPro).");
        var (ok, err) = ValidarContacto(c);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM estacion WHERE nombre = @nom AND rubro = @rub AND _deleted = 0";
                chk.Parameters.Add(new SqlParameter("@nom", (c.Nombre ?? "").Trim().ToUpperInvariant()));
                chk.Parameters.Add(new SqlParameter("@rub", c.RubroId));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("Ese nombre ya existe para ese rubro.");
                }
            }

            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM estacion";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO estacion
                        (id, rubro, nombre, domicilio, localidad, provincia, telefono, celular, radio,
                         email, contacto1, contacto2, medio_pago, control_sa, ult_lote, cairo_codi,
                         cairo_iibb, ypf_ruta, esso_card, cta_cte, _deleted)
                    VALUES
                        (@id, @rub, @nom, @dom, @loc, @prov, @tel, @cel, @rad, @email, @c1, @c2, @mp,
                         @csa, @lote, @ccod, @ciibb, @ypf, @esso, @cc, 0)
                    """;
                AgregarParamsContacto(ins, c, nuevoId);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta el contacto: {ex.Message}");
        }
    }

    /// <summary>Modificación de un contacto (estacion_abm.scx modo "modifica"). Abortada por el flag.</summary>
    public async Task<AbmResult> ModificaContactoAsync(int id, ContactoInput c)
    {
        if (!AbmFeatureFlags.ContactosAbmActivo)
            return AbmResult.Fallo("La escritura de Contactos todavía no está habilitada (sigue en FoxPro).");
        var (ok, err) = ValidarContacto(c);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = """
                UPDATE estacion SET
                    rubro = @rub, nombre = @nom, domicilio = @dom, localidad = @loc, provincia = @prov,
                    telefono = @tel, celular = @cel, radio = @rad, email = @email, contacto1 = @c1,
                    contacto2 = @c2, medio_pago = @mp, control_sa = @csa, ult_lote = @lote,
                    cairo_codi = @ccod, cairo_iibb = @ciibb, ypf_ruta = @ypf, esso_card = @esso, cta_cte = @cc
                WHERE id = @id AND _deleted = 0
                """;
            AgregarParamsContacto(upd, c, id);
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El contacto ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo modificar el contacto: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de un contacto (DELETE — así lo hace el FoxPro). Abortada por el flag.</summary>
    public async Task<AbmResult> BajaContactoAsync(int id)
    {
        if (!AbmFeatureFlags.ContactosAbmActivo)
            return AbmResult.Fallo("La escritura de Contactos todavía no está habilitada (sigue en FoxPro).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM estacion WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El contacto ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar el contacto: {ex.Message}");
        }
    }

    private static (bool, string?) ValidarContacto(ContactoInput c)
    {
        if (string.IsNullOrWhiteSpace(c.Nombre))
            return (false, "Cargá la razón social del contacto.");
        if (c.RubroId <= 0)
            return (false, "Elegí el rubro del contacto.");
        if ((c.Email ?? "").Trim().Length > 0 && !(c.Email ?? "").Contains('@'))
            return (false, "El e-mail debe contener el carácter @.");
        return (true, null);
    }

    private static void AgregarParamsContacto(SqlCommand cmd, ContactoInput c, int id)
    {
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@rub", c.RubroId));
        cmd.Parameters.Add(new SqlParameter("@nom", (object?)(c.Nombre ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@dom", (object?)(c.Domicilio ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@loc", (object?)(c.Localidad ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@prov", (object?)(c.Provincia ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@tel", (object?)(c.Telefono ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cel", (object?)(c.Celular ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@rad", (object?)(c.Radio ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@email", (object?)(c.Email ?? "").Trim().ToLowerInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@c1", (object?)(c.Contacto1 ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@c2", (object?)(c.Contacto2 ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@mp", (object?)(c.MedioPago ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@csa", c.ControlSaldo));
        cmd.Parameters.Add(new SqlParameter("@lote", c.UltLote));
        cmd.Parameters.Add(new SqlParameter("@ccod", (object?)(c.CairoCodigo ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@ciibb", (object?)(c.CairoIibb ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@ypf", c.YpfRuta));
        cmd.Parameters.Add(new SqlParameter("@esso", c.EssoCard));
        cmd.Parameters.Add(new SqlParameter("@cc", c.CtaCte));
    }

    // ── Rubros de contacto (estacion_rubro_abm.scx) ──────────────────────────
    //  id + rubro (nombre) + flag audita. Baja física. La columna del nombre es `rubro`
    //  (no `nombre`) → no reusa AltaCatalogoAsync genérico.

    /// <summary>Alta de un rubro de contacto (estacion_rubro_abm.scx). Abortada por el flag.</summary>
    public async Task<AbmResult> AltaRubroContactoAsync(string rubro, bool audita)
    {
        if (!AbmFeatureFlags.RubrosContactoAbmActivo)
            return AbmResult.Fallo("La escritura de Rubros todavía no está habilitada (sigue en FoxPro).");
        if (string.IsNullOrWhiteSpace(rubro)) return AbmResult.Fallo("Cargá el nombre del rubro.");
        if (rubro.Trim().Length > 60) return AbmResult.Fallo("El rubro no puede superar 60 caracteres.");
        var val = rubro.Trim().ToUpperInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM estacion_rubro WHERE rubro = @r AND _deleted = 0";
                chk.Parameters.Add(new SqlParameter("@r", val));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo($"El rubro «{val}» ya está cargado.");
                }
            }

            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM estacion_rubro";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO estacion_rubro (id, rubro, audita, _deleted) VALUES (@id, @r, @a, 0)";
                ins.Parameters.Add(new SqlParameter("@id", nuevoId));
                ins.Parameters.Add(new SqlParameter("@r", val));
                ins.Parameters.Add(new SqlParameter("@a", audita));
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta el rubro: {ex.Message}");
        }
    }

    /// <summary>Modificación de un rubro de contacto. Abortada por el flag.</summary>
    public async Task<AbmResult> ModificaRubroContactoAsync(int id, string rubro, bool audita)
    {
        if (!AbmFeatureFlags.RubrosContactoAbmActivo)
            return AbmResult.Fallo("La escritura de Rubros todavía no está habilitada (sigue en FoxPro).");
        if (string.IsNullOrWhiteSpace(rubro)) return AbmResult.Fallo("Cargá el nombre del rubro.");
        var val = rubro.Trim().ToUpperInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE estacion_rubro SET rubro = @r, audita = @a WHERE id = @id AND _deleted = 0";
            upd.Parameters.Add(new SqlParameter("@r", val));
            upd.Parameters.Add(new SqlParameter("@a", audita));
            upd.Parameters.Add(new SqlParameter("@id", id));
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El rubro ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo modificar el rubro: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de un rubro de contacto (DELETE). Abortada por el flag.</summary>
    public async Task<AbmResult> BajaRubroContactoAsync(int id)
    {
        if (!AbmFeatureFlags.RubrosContactoAbmActivo)
            return AbmResult.Fallo("La escritura de Rubros todavía no está habilitada (sigue en FoxPro).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM estacion_rubro WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El rubro ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar el rubro: {ex.Message}");
        }
    }

    // ── Voucher: marca de recepción (trafico_voucher.scx botones) ─────────────
    //  UPDATE viaje SET voucher_re = fecha. Toca `viaje` → se enciende el DÍA D con el circuito.
    //  WHERE por id_viaje + f_reserva (no hay índice por id_viaje, regla de perf de la skill).

    /// <summary>Marca la fecha de recepción de un voucher (botón "1º Viaje"). Abortada por el flag.</summary>
    public async Task<AbmResult> MarcarRecepcionAsync(long idViaje, DateOnly fReserva, DateOnly fecha)
    {
        if (!AbmFeatureFlags.VoucherRecepcionActivo)
            return AbmResult.Fallo("La marca de recepción de vouchers se habilita con el circuito de Tráfico (día D).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE viaje SET voucher_re = @f WHERE id_viaje = @id AND f_reserva = @fr AND _deleted = 0";
            upd.Parameters.Add(new SqlParameter("@f", fecha.ToDateTime(TimeOnly.MinValue)));
            upd.Parameters.Add(new SqlParameter("@id", idViaje));
            upd.Parameters.Add(new SqlParameter("@fr", fReserva.ToDateTime(TimeOnly.MinValue)));
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El viaje ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito((int)idViaje);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo marcar la recepción: {ex.Message}");
        }
    }

    /// <summary>Marca/limpia la recepción de un conjunto de viajes ("Todos los Viajes" / "Limpia recep").
    /// <paramref name="fecha"/> null = limpiar. Abortada por el flag.</summary>
    public async Task<AbmResult> MarcarRecepcionLoteAsync(IEnumerable<(long IdViaje, DateOnly FReserva)> viajes, DateOnly? fecha)
    {
        if (!AbmFeatureFlags.VoucherRecepcionActivo)
            return AbmResult.Fallo("La marca de recepción de vouchers se habilita con el circuito de Tráfico (día D).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            int n = 0;
            foreach (var (idViaje, fReserva) in viajes)
            {
                await using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE viaje SET voucher_re = @f WHERE id_viaje = @id AND f_reserva = @fr AND _deleted = 0";
                upd.Parameters.Add(new SqlParameter("@f", fecha is DateOnly d ? d.ToDateTime(TimeOnly.MinValue) : (object)DBNull.Value));
                upd.Parameters.Add(new SqlParameter("@id", idViaje));
                upd.Parameters.Add(new SqlParameter("@fr", fReserva.ToDateTime(TimeOnly.MinValue)));
                n += await upd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(n);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo procesar la recepción: {ex.Message}");
        }
    }

    // ── F2 · Alta de Novedades (libro_novedad_abm.scx modo "alta") ────────────
    //  trafico2.scx → libro_novedad_alta. Plano: docs/PlanoFoxPro/trafico/TRAFICO_F2_NOVEDADES.md
    //
    //  El libro de guardia de la mesa de tráfico: 1.594 novedades en 2026 (~5 por día), las
    //  cargan los mismos operadores que mueven el cronograma (DAMIAN, MAURO, PSTELE, RICARDO).
    //
    //  ⚠ La tabla tiene 20 columnas pero la operación real usa CINCO. Verificado contra
    //  producción (04/08/2026): prioridad, f_aviso, avisar_en, telefono, radio y usuario_de
    //  están vacíos en las 1.594 filas de 2026 — son campos muertos de una versión anterior.
    //  El INSERT del FoxPro es exactamente:
    //      INSERT INTO libro_novedad (f_carga, asunto, mensaje, usuario_create, id_viaje)
    //  (usuario_create → truncado a `usuario_cr` en la réplica).
    //
    //  Lo que NO se migra (decisión del usuario, 04/08/2026):
    //   · El ENVÍO DE CORREO al cliente. El FoxPro puede mandarle la novedad a hasta 10
    //     contactos de la ficha del cliente al grabar. Es una acción hacia afuera de la empresa
    //     y se queda en FoxPro por ahora. Ojo: `f_envio` NO lo escribe el alta — lo llena
    //     después otro proceso (libro_novedad_envia_correo.scx), todavía sin relevar.
    //   · Modificar y dar de baja. 🐛 En el fuente la BAJA está ROTA: el DELETE está comentado
    //     y encima apunta a la tabla `agenda` (copy-paste de otro ABM), así que el botón
    //     "Eliminar" no borra nada. El Modificar solo cambia el mensaje.

    /// <summary>
    /// Alta de una novedad en el libro de guardia (F2). <paramref name="idViaje"/> en 0 = novedad
    /// SUELTA, sin reserva asociada (son 752 de las 1.594 de 2026, casi la mitad). Abortada por
    /// el flag.
    /// </summary>
    public async Task<AbmResult> AltaNovedadAsync(int idViaje, string asunto, string mensaje, string usuario)
    {
        if (!AbmFeatureFlags.NovedadesAbmActivo)
            return AbmResult.Fallo("La carga de novedades todavía no está habilitada (sigue en FoxPro).");

        // Las dos únicas validaciones del FoxPro (audita_carga).
        asunto = (asunto ?? "").Trim();
        mensaje = (mensaje ?? "").Trim();
        if (asunto.Length == 0) return AbmResult.Fallo("Falta cargar el asunto de la novedad.");
        if (mensaje.Length == 0) return AbmResult.Fallo("Falta cargar el mensaje de la novedad.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // id NO es identity (viene del DBF) → MAX(id)+1 dentro de la transacción, que es el
            // patrón del proyecto. El último de producción al 04/08/2026 era 50154.
            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM libro_novedad";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO libro_novedad (id, f_carga, asunto, mensaje, usuario_cr, id_viaje, finalizo, _deleted)
                    VALUES (@id, GETDATE(), @asunto, @mensaje, @usr, @viaje, 0, 0)
                    """;
                ins.Parameters.Add(new SqlParameter("@id", nuevoId));
                ins.Parameters.Add(new SqlParameter("@asunto", asunto));
                ins.Parameters.Add(new SqlParameter("@mensaje", mensaje));
                ins.Parameters.Add(new SqlParameter("@usr", usuario ?? ""));
                // El FoxPro guarda 0 en las novedades sueltas, no NULL.
                ins.Parameters.Add(new SqlParameter("@viaje", (long)idViaje));
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo cargar la novedad: {ex.Message}");
        }
    }

    // ── F6-F9 · Cambio de CRONOGRAMA (diagramación) ───────────────────────────
    //  trafico2.scx → viaje_cambia_cronograma + trafico_cambia_cronograma.scx
    //  Plano: docs/PlanoFoxPro/trafico/TRAFICO_CRONOGRAMA.md
    //
    //  Es la operación MÁS FRECUENTE del circuito: 29.467 cambios en 191 días de 2026 (154/día).
    //  Hay TRES unidades por viaje y esta capa mueve las dos primeras:
    //    U/Pr = cronogram2  (unidad PROGRAMADA por el diagramador)
    //    U/Cb = cronograma  (unidad prevista VIGENTE)
    //    U/As = interno     (la real que sale — la escribe Asignar, no esto)
    //
    //  Dos modos, ruteados por permiso (D gana sobre T, como el Do Case del FoxPro):
    //    diagramador (D) → escribe cronogram2 Y cronograma, SIN log, puede ser masivo
    //    operador    (T) → escribe SOLO cronograma, motivo OBLIGATORIO, deja log CBIO UNIDAD
    //  Los dos resetean chequeo = 0 (si cambió la unidad prevista, el chequeo anterior no vale).
    //
    //  Mejoras deliberadas sobre el FoxPro:
    //    · transacción única (el FoxPro hace UPDATE + INSERT sueltos)
    //    · relectura con UPDLOCK: la web es multiusuario, el FoxPro era efectivamente mono
    //    · WHERE anclado en f_reserva (no hay índice por id_viaje)
    //    · 🐛 NO se copia el bug del masivo: el "Todas las Reservas" del fuente graba
    //      `cCronogramaNuevo = thisform.cronograma.Value` — el interno PELADO, sin prefijo de
    //      fletero ni pad de ceros ("49" en vez de "NT0049") — e ignora los radios S/C y NORTUR.
    //      En la base no hay ni un cronograma numérico en 512k filas (o el .exe ya lo corrigió,
    //      o nadie usó nunca ese botón). Acá el masivo arma el código igual que el simple.

    /// <summary>Modo de cambio de cronograma. Lo decide el permiso del usuario, no la pantalla.</summary>
    public enum ModoCronograma { Diagramador, Operador }

    /// <summary>
    /// Cambia el cronograma de UN viaje (botón "Reserva Actual"). Si el viaje es una ruta
    /// (<c>id_viaje_i</c> &gt; 0) el cambio pega a TODOS sus tramos, y en modo operador deja un
    /// renglón de log por tramo — igual que el FoxPro. Abortada por el flag.
    /// </summary>
    /// <param name="motivo">Obligatorio en modo operador; ignorado en diagramador.</param>
    public async Task<AbmResult> CambiarCronogramaAsync(
        int idViaje, DateOnly fReserva, string cronogramaNuevo,
        ModoCronograma modo, string motivo, string usuario)
    {
        if (!AbmFeatureFlags.CronogramaAbmActivo)
            return AbmResult.Fallo("El cambio de cronograma se habilita con el circuito de Tráfico (día D).");
        if (string.IsNullOrWhiteSpace(cronogramaNuevo))
            return AbmResult.Fallo("No se cargó el cronograma o no existe la unidad.");
        // Validación del bAceptar del FoxPro: en modo operador el motivo es obligatorio.
        if (modo == ModoCronograma.Operador && string.IsNullOrWhiteSpace(motivo))
            return AbmResult.Fallo("Debe cargar un motivo de cambio de unidad.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // ¿Es una ruta? El dato se lee ACÁ y no se recibe de la pantalla: la grilla no lo
            // trae, y además así se lee fresco dentro de la transacción. id_viaje_i es BIGINT
            // (regla del proyecto: castear, no leer con GetInt32).
            long idViajeInt;
            await using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT TOP 1 CAST(ISNULL(id_viaje_i, 0) AS bigint) FROM viaje WITH (UPDLOCK) WHERE f_reserva = @fr AND id_viaje = @id AND _deleted = 0";
                q.Parameters.Add(new SqlParameter("@fr", fReserva.ToDateTime(TimeOnly.MinValue)));
                q.Parameters.Add(new SqlParameter("@id", idViaje));
                var v = await q.ExecuteScalarAsync();
                if (v is null) { await tx.RollbackAsync(); return AbmResult.Fallo("El viaje ya no existe."); }
                idViajeInt = Convert.ToInt64(v);
            }

            // Los tramos afectados: el viaje solo, o toda la ruta. En modo operador el log
            // necesita un renglón por tramo, igual que el FoxPro.
            var tramos = new List<int>();
            if (idViajeInt > 0)
            {
                await using var sel = conn.CreateCommand();
                sel.Transaction = tx;
                sel.CommandText = "SELECT id_viaje FROM viaje WITH (UPDLOCK) WHERE f_reserva = @fr AND id_viaje_i = @int AND _deleted = 0 ORDER BY id_viaje";
                sel.Parameters.Add(new SqlParameter("@fr", fReserva.ToDateTime(TimeOnly.MinValue)));
                sel.Parameters.Add(new SqlParameter("@int", idViajeInt));
                await using var rd = await sel.ExecuteReaderAsync();
                while (await rd.ReadAsync()) tramos.Add(rd.GetInt32(0));
            }
            else
            {
                tramos.Add(idViaje);
            }
            if (tramos.Count == 0)
            {
                await tx.RollbackAsync();
                return AbmResult.Fallo("El viaje ya no existe.");
            }

            var n = await AplicarCronogramaAsync(conn, tx, tramos, fReserva, cronogramaNuevo, modo, motivo, usuario);
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(n);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo cambiar el cronograma: {ex.Message}");
        }
    }

    /// <summary>
    /// Cambio MASIVO ("Todas las Reservas", solo diagramador): les pone el cronograma nuevo a
    /// todos los viajes de <paramref name="idsViaje"/>. El llamador arma esa lista con la misma
    /// regla del FoxPro — las filas A LA VISTA cuyo <c>cronograma</c> es el anterior Y que
    /// todavía NO tienen interno asignado — y se la muestra al usuario antes de grabar.
    /// Abortada por el flag.
    /// </summary>
    public async Task<AbmResult> CambiarCronogramaMasivoAsync(
        IReadOnlyList<(int IdViaje, DateOnly FReserva)> viajes, string cronogramaNuevo, string usuario)
    {
        if (!AbmFeatureFlags.CronogramaAbmActivo)
            return AbmResult.Fallo("El cambio de cronograma se habilita con el circuito de Tráfico (día D).");
        if (string.IsNullOrWhiteSpace(cronogramaNuevo))
            return AbmResult.Fallo("No se cargó el cronograma o no existe la unidad.");
        if (viajes.Count == 0)
            return AbmResult.Fallo("No hay reservas para cambiar.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            int total = 0;
            // Una transacción para todo el lote: o entra el tablero entero o no entra nada.
            // El FoxPro va fila por fila sin transacción y puede dejarlo a medio aplicar.
            foreach (var grupo in viajes.GroupBy(v => v.FReserva))
            {
                var ids = grupo.Select(v => v.IdViaje).ToList();
                total += await AplicarCronogramaAsync(
                    conn, tx, ids, grupo.Key, cronogramaNuevo, ModoCronograma.Diagramador, "", usuario);
            }
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(total);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo aplicar el cambio masivo: {ex.Message}");
        }
    }

    /// <summary>
    /// Ctrl+F8 — "Copia Cronograma del Diagramador": pisa la unidad vigente (U/Cb) con la que
    /// planificó el diagramador (U/Pr). Abortada por el flag.
    /// ⚠️ Este atajo NO está en el fuente en disco (ahí Ctrl+F8 togglea los cancelados); viene
    /// del .exe productivo, así que el mecanismo está deducido del resto de la capa: mismo
    /// UPDATE + reset de chequeo, y log solo en modo operador. Confirmar contra el .exe.
    /// </summary>
    public async Task<AbmResult> CopiarCronogramaDiagramadorAsync(
        int idViaje, DateOnly fReserva, ModoCronograma modo, string usuario)
    {
        if (!AbmFeatureFlags.CronogramaAbmActivo)
            return AbmResult.Fallo("El cambio de cronograma se habilita con el circuito de Tráfico (día D).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            string programada;
            await using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT TOP 1 cronogram2 FROM viaje WITH (UPDLOCK) WHERE f_reserva = @fr AND id_viaje = @id AND _deleted = 0";
                sel.Parameters.Add(new SqlParameter("@fr", fReserva.ToDateTime(TimeOnly.MinValue)));
                sel.Parameters.Add(new SqlParameter("@id", idViaje));
                var v = await sel.ExecuteScalarAsync();
                if (v is null) { await tx.RollbackAsync(); return AbmResult.Fallo("El viaje ya no existe."); }
                programada = v is DBNull ? "" : v.ToString()!.Trim();
            }
            if (string.IsNullOrWhiteSpace(programada))
            {
                await tx.RollbackAsync();
                return AbmResult.Fallo("El viaje no tiene unidad programada (U/Pr) para copiar.");
            }

            await AplicarCronogramaAsync(conn, tx, new List<int> { idViaje }, fReserva, programada,
                                         modo, "COPIA DEL DIAGRAMADOR", usuario);
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(idViaje);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo copiar el cronograma: {ex.Message}");
        }
    }

    /// <summary>
    /// El núcleo compartido: aplica el cronograma a una lista de viajes del MISMO día, dentro de
    /// una transacción ya abierta. Acá vive la única diferencia real entre los dos modos.
    /// </summary>
    private static async Task<int> AplicarCronogramaAsync(
        SqlConnection conn, SqlTransaction tx, IReadOnlyList<int> idsViaje, DateOnly fReserva,
        string cronogramaNuevo, ModoCronograma modo, string motivo, string usuario)
    {
        int n = 0;
        foreach (var id in idsViaje)
        {
            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                // Diagramador: planifica → escribe las DOS columnas (U/Pr y U/Cb).
                // Operador: ajusta → toca solo U/Cb y respeta lo que planeó el diagramador.
                upd.CommandText = modo == ModoCronograma.Diagramador
                    ? "UPDATE viaje SET cronogram2 = @cr, cronograma = @cr, chequeo = 0 WHERE f_reserva = @fr AND id_viaje = @id AND _deleted = 0"
                    : "UPDATE viaje SET cronograma = @cr, chequeo = 0 WHERE f_reserva = @fr AND id_viaje = @id AND _deleted = 0";
                upd.Parameters.Add(new SqlParameter("@cr", cronogramaNuevo));
                upd.Parameters.Add(new SqlParameter("@fr", fReserva.ToDateTime(TimeOnly.MinValue)));
                upd.Parameters.Add(new SqlParameter("@id", id));
                n += await upd.ExecuteNonQueryAsync();
            }

            // Solo el operador deja auditoría: el diagramador arma el tablero y no loguea.
            if (modo != ModoCronograma.Operador) continue;
            await using var log = conn.CreateCommand();
            log.Transaction = tx;
            log.CommandText = """
                INSERT INTO viaje_log (id_viaje, usuario, motivo, hora, cronograma, id_chofer,
                                       interno_or, interno_ne, comentario)
                VALUES (@id, @usr, 'CBIO UNIDAD', GETDATE(), @cr, '', 0, 0, @com)
                """;
            log.Parameters.Add(new SqlParameter("@id", id));
            log.Parameters.Add(new SqlParameter("@usr", usuario ?? ""));
            log.Parameters.Add(new SqlParameter("@cr", cronogramaNuevo));
            log.Parameters.Add(new SqlParameter("@com", motivo ?? ""));
            await log.ExecuteNonQueryAsync();
        }
        return n;
    }

    // ── F4 · Aviso sobre el viaje (trafico_hs_aviso.scx) ──────────────────────
    //  UPDATE viaje SET hs_aviso. Plano: docs/PlanoFoxPro/trafico/TRAFICO_F4_AVISO.md
    //
    //  Es la escritura de MENOR superficie de todo el circuito `viaje`: una sola columna, sin
    //  máquina de estados, sin odómetro, sin cascadas, sin importes, sin GPS. Aun así toca
    //  `viaje` → apagada por el flag hasta el día D (si Blazor escribiera hoy, la próxima
    //  replicación DBF→SQL de esa fila lo pisaría sin aviso).
    //
    //  Fiel al FoxPro:
    //    · el aviso debe ser ESTRICTAMENTE anterior a hs_inicio (el `<=` del fuente rechaza
    //      también el igual)
    //    · la fecha no puede ser anterior a hoy (Valid del textbox f_reserva)
    //    · hs_aviso NULL = vuelve al aviso automático (el "No Avisar" del FoxPro graba un
    //      datetime vacío, que NO apaga el aviso: lo devuelve al default de parametro)
    //  Mejoras deliberadas sobre el FoxPro:
    //    · transacción + relectura del viaje adentro (FoxPro era mono-usuario; la web no)
    //    · deja rastro en viaje_log (motivo AVISO) — el FoxPro no audita este cambio
    //    · NO se copia la regla del fuente `IF hs_inicio - 2100 = aviso THEN aviso - 150`
    //      (correr 2,5 min un aviso que quedó a 35 min exactos): no tiene explicación en el
    //      código ni en los datos. Ver pregunta 4 del plano.

    /// <summary>Graba la hora de aviso de un viaje (F4). <paramref name="hsAviso"/> null = volver
    /// al aviso automático de <c>parametro.aviso_tiem</c>. Abortada por el flag.</summary>
    public async Task<AbmResult> GrabarAvisoViajeAsync(
        int idViaje, DateOnly fReserva, DateTime? hsAviso, string usuario)
    {
        if (!AbmFeatureFlags.AvisoViajeActivo)
            return AbmResult.Fallo("El aviso sobre el viaje se habilita con el circuito de Tráfico (día D).");

        // Validación de fecha pasada (Valid de f_reserva en trafico_hs_aviso.scx). Se chequea
        // antes de abrir conexión: no depende de la base.
        if (hsAviso is DateTime v && DateOnly.FromDateTime(v) < DateOnly.FromDateTime(DateTime.Today))
            return AbmResult.Fallo("El aviso no puede quedar en una fecha anterior a hoy.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Relectura con UPDLOCK dentro de la transacción: la hora de salida pudo cambiar
            // entre que se abrió el diálogo y se apretó Grabar (Zoom de otro operador, o el
            // FoxPro). Validamos contra el dato de AHORA, no contra el que vio la pantalla.
            DateTime? hsInicio = null;
            string estado = "";
            await using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = """
                    SELECT TOP 1 hs_inicio, estado_via
                    FROM viaje WITH (UPDLOCK)
                    WHERE f_reserva = @fr AND id_viaje = @id AND _deleted = 0
                    """;
                sel.Parameters.Add(new SqlParameter("@fr", fReserva.ToDateTime(TimeOnly.MinValue)));
                sel.Parameters.Add(new SqlParameter("@id", idViaje));
                await using var rd = await sel.ExecuteReaderAsync();
                if (!await rd.ReadAsync())
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("El viaje ya no existe.");
                }
                if (!rd.IsDBNull(0)) hsInicio = rd.GetDateTime(0);
                if (!rd.IsDBNull(1)) estado = rd.GetString(1).Trim();
            }

            if (estado is "CANCELADO" or "FINALIZADO" or "FACTURADO")
            {
                await tx.RollbackAsync();
                return AbmResult.Fallo($"El viaje está {estado} — no tiene sentido programarle un aviso.");
            }

            // La validación del FoxPro: hs_inicio <= aviso → error. O sea el aviso tiene que
            // ser estrictamente anterior a la salida del servicio.
            if (hsAviso is DateTime a && hsInicio is DateTime ini && ini <= a)
            {
                await tx.RollbackAsync();
                return AbmResult.Fallo("El aviso tiene que ser anterior a la hora del servicio.");
            }

            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE viaje SET hs_aviso = @av WHERE f_reserva = @fr AND id_viaje = @id AND _deleted = 0";
                upd.Parameters.Add(new SqlParameter("@av", hsAviso ?? (object)DBNull.Value));
                upd.Parameters.Add(new SqlParameter("@fr", fReserva.ToDateTime(TimeOnly.MinValue)));
                upd.Parameters.Add(new SqlParameter("@id", idViaje));
                if (await upd.ExecuteNonQueryAsync() == 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("El viaje ya no existe.");
                }
            }

            // Auditoría (mejora sobre el FoxPro). Mismas columnas que usa el resto del
            // circuito; ojo con los nombres truncados por la réplica: interno_or / interno_ne.
            await using (var log = conn.CreateCommand())
            {
                log.Transaction = tx;
                log.CommandText = """
                    INSERT INTO viaje_log (id_viaje, usuario, motivo, hora, cronograma, id_chofer,
                                           interno_or, interno_ne, comentario)
                    VALUES (@id, @usr, 'AVISO', GETDATE(), '', '', 0, 0, @com)
                    """;
                log.Parameters.Add(new SqlParameter("@id", idViaje));
                log.Parameters.Add(new SqlParameter("@usr", usuario ?? ""));
                log.Parameters.Add(new SqlParameter("@com", hsAviso is DateTime h
                    ? $"AVISO A LAS {h:dd/MM/yyyy HH:mm}"
                    : "AVISO AUTOMATICO (SE BORRO LA HORA MANUAL)"));
                await log.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(idViaje);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo grabar el aviso: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  COMBUSTIBLE — Conciliación de cargas por lote (andamiaje, 07/07/2026)
    //  vehiculo_combustible_mant_sobre_lote: el "sobre" físico de tickets se numera con el
    //  contador GLOBAL parametro.lote_sobre. Marcar = UPDATE vehiculo_sobre.n_sobre = lote.
    //  ⚠ Toca la tabla VIVA vehiculo_sobre + parametro (compartidos con FoxPro) → apagado por el
    //  flag ConciliacionCombustibleAbmActivo hasta el día D. Numerador consumido no se devuelve.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Reserva un nuevo número de lote: <c>parametro.lote_sobre + 1</c> (UPDATE) y lo devuelve
    /// en <c>AbmResult.Id</c>. Como el FoxPro, el número se consume aunque después no se marque nada.
    /// Abortada por el flag.</summary>
    public async Task<AbmResult> NuevoLoteAsync()
    {
        if (!AbmFeatureFlags.ConciliacionCombustibleAbmActivo)
            return AbmResult.Fallo("La conciliación de cargas todavía no está habilitada (sigue en FoxPro).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Incremento atómico del numerador global y devolución del nuevo valor.
            await using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE parametro SET lote_sobre = ISNULL(lote_sobre, 0) + 1;
                SELECT CAST(lote_sobre AS bigint) FROM parametro;
                """;
            var nuevo = Convert.ToInt64(await upd.ExecuteScalarAsync() ?? 0L);
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito((int)Math.Min(nuevo, int.MaxValue));
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo generar el nuevo lote: {ex.Message}");
        }
    }

    /// <summary>Asigna (Marca) o quita (Desmarca) una carga a un lote: <c>UPDATE vehiculo_sobre
    /// SET n_sobre = @lote WHERE id = @id</c> (lote 0 = desmarcar). Abortada por el flag.</summary>
    public async Task<AbmResult> MarcarLoteAsync(int idCarga, long lote)
    {
        if (!AbmFeatureFlags.ConciliacionCombustibleAbmActivo)
            return AbmResult.Fallo("La conciliación de cargas todavía no está habilitada (sigue en FoxPro).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE vehiculo_sobre SET n_sobre = @lote WHERE id = @id AND ISNULL(_deleted,0) = 0";
            upd.Parameters.Add(new SqlParameter("@lote", lote));
            upd.Parameters.Add(new SqlParameter("@id", idCarga));
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("La carga ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(idCarga);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo asignar el lote: {ex.Message}");
        }
    }

    /// <summary>Quita el lote de una carga (Desmarca = n_sobre 0). Atajo de <see cref="MarcarLoteAsync"/>.</summary>
    public Task<AbmResult> DesmarcarLoteAsync(int idCarga) => MarcarLoteAsync(idCarga, 0);

    /// <summary>Marca/Desmarca en LOTE todas las cargas de una lista (Marca Todo / Desm Todo).
    /// Una sola transacción. Abortada por el flag.</summary>
    public async Task<AbmResult> MarcarLoteMasivoAsync(IReadOnlyList<int> idsCarga, long lote)
    {
        if (!AbmFeatureFlags.ConciliacionCombustibleAbmActivo)
            return AbmResult.Fallo("La conciliación de cargas todavía no está habilitada (sigue en FoxPro).");
        if (idsCarga is null || idsCarga.Count == 0)
            return AbmResult.Fallo("No hay cargas para procesar.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            int n = 0;
            foreach (var id in idsCarga)
            {
                await using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE vehiculo_sobre SET n_sobre = @lote WHERE id = @id AND ISNULL(_deleted,0) = 0";
                upd.Parameters.Add(new SqlParameter("@lote", lote));
                upd.Parameters.Add(new SqlParameter("@id", id));
                n += await upd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(n);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo procesar la conciliación masiva: {ex.Message}");
        }
    }

    // ── ABM de una carga de combustible (vehiculo_combustible_carga_sobre) ──
    //  p_x_ltr = importe/litros DERIVADO al grabar (no de tarifario). Alta con id = MAX(id)+1.
    //  Baja FÍSICA. Todo gated por ConciliacionCombustibleAbmActivo.

    /// <summary>Alta de una carga (vehiculo_combustible_carga_sobre modo "alta"). Abortada por el flag.</summary>
    public async Task<AbmResult> AltaCargaCombustibleAsync(CargaCombustibleInput c)
    {
        if (!AbmFeatureFlags.ConciliacionCombustibleAbmActivo)
            return AbmResult.Fallo("La escritura de cargas todavía no está habilitada (sigue en FoxPro).");
        var (ok, err) = ValidarCarga(c);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM vehiculo_sobre";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO vehiculo_sobre
                        (id, n_factura, n_remito, idrubro, interno, dominio, chofer, estacion,
                         estacion_n, tipo_carga, odometro, litros, p_x_ltr, importe, f_carga, fecha,
                         hora, lleno, f_pago, dos_carga, n_sobre, u_create, f_create, _deleted)
                    VALUES
                        (@id, 0, 0, @rub, @int, @dom, @cho, @est, @estn, @tipo, @odo, @lts, @pxl,
                         @imp, @fc, @fc, @hora, @lleno, @fpago, @dos, 0, @user, SYSDATETIME(), 0)
                    """;
                AgregarParamsCarga(ins, c, nuevoId);
                await ins.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta la carga: {ex.Message}");
        }
    }

    /// <summary>Modificación de una carga (modo "modifica"). Abortada por el flag.</summary>
    public async Task<AbmResult> ModificaCargaCombustibleAsync(int id, CargaCombustibleInput c)
    {
        if (!AbmFeatureFlags.ConciliacionCombustibleAbmActivo)
            return AbmResult.Fallo("La escritura de cargas todavía no está habilitada (sigue en FoxPro).");
        var (ok, err) = ValidarCarga(c);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = """
                UPDATE vehiculo_sobre SET
                    idrubro = @rub, interno = @int, dominio = @dom, chofer = @cho, estacion = @est,
                    estacion_n = @estn, tipo_carga = @tipo, odometro = @odo, litros = @lts,
                    p_x_ltr = @pxl, importe = @imp, f_carga = @fc, fecha = @fc, hora = @hora,
                    lleno = @lleno, f_pago = @fpago, dos_carga = @dos, u_modify = @user, f_modify = SYSDATETIME()
                WHERE id = @id AND ISNULL(_deleted,0) = 0
                """;
            AgregarParamsCarga(upd, c, id);
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("La carga ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo modificar la carga: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de una carga (DELETE — así lo hace el FoxPro). Abortada por el flag.</summary>
    public async Task<AbmResult> BajaCargaCombustibleAsync(int id)
    {
        if (!AbmFeatureFlags.ConciliacionCombustibleAbmActivo)
            return AbmResult.Fallo("La escritura de cargas todavía no está habilitada (sigue en FoxPro).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM vehiculo_sobre WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("La carga ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar la carga: {ex.Message}");
        }
    }

    private static (bool, string?) ValidarCarga(CargaCombustibleInput c)
    {
        if (c.Interno <= 0 && string.IsNullOrWhiteSpace(c.Dominio))
            return (false, "Cargá el vehículo (interno o dominio).");
        if (c.EstacionId <= 0)
            return (false, "Elegí la estación.");
        if (c.Odometro <= 0)
            return (false, "El odómetro debe ser mayor que cero.");
        if (c.Litros <= 0)
            return (false, "Los litros deben ser mayores que cero.");
        return (true, null);
    }

    private static void AgregarParamsCarga(SqlCommand cmd, CargaCombustibleInput c, int id)
    {
        // p_x_ltr DERIVADO = importe/litros (no de tarifario), como el FoxPro.
        decimal pxl = c.Litros > 0 ? Math.Round(c.Importe / c.Litros, 4) : 0m;
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@rub", c.IdRubro));
        cmd.Parameters.Add(new SqlParameter("@int", (long)c.Interno));
        cmd.Parameters.Add(new SqlParameter("@dom", (object?)(c.Dominio ?? "").Trim().ToUpperInvariant() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cho", (object?)(string.IsNullOrWhiteSpace(c.Chofer) ? "SIN CHOFER" : c.Chofer.Trim())));
        cmd.Parameters.Add(new SqlParameter("@est", (long)c.EstacionId));
        cmd.Parameters.Add(new SqlParameter("@estn", (object?)(c.Estacion ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@tipo", (object?)(c.TipoCarga ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@odo", c.Odometro));
        cmd.Parameters.Add(new SqlParameter("@lts", c.Litros));
        cmd.Parameters.Add(new SqlParameter("@pxl", pxl));
        cmd.Parameters.Add(new SqlParameter("@imp", c.Importe));
        cmd.Parameters.Add(new SqlParameter("@fc", c.FCarga.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(new SqlParameter("@hora", (object?)(c.Hora ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@lleno", c.Lleno));
        cmd.Parameters.Add(new SqlParameter("@fpago", (object?)(c.FPago ?? "").Trim() ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@dos", c.DosCarga));
        cmd.Parameters.Add(new SqlParameter("@user", (object?)(c.Usuario ?? "").Trim() ?? DBNull.Value));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  COMBUSTIBLE — Depósitos de estación (andamiaje, circuito histórico 2013-2017)
    //  vehiculo_estacion_saldo_carga (alta ingreso/egreso) + _mant (baja física).
    //  Egreso = importe × −1. empresa = "NORTUR" (NO el "PATAGONIA" hardcodeado del fuente).
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Alta de un depósito/egreso (vehiculo_estacion_saldo_carga). EsEgreso graba importe × −1.
    /// id = MAX(id)+1 (no identity). Abortada por el flag.</summary>
    public async Task<AbmResult> AltaDepositoEstacionAsync(DepositoEstacionInput d)
    {
        if (!AbmFeatureFlags.DepositosCombustibleAbmActivo)
            return AbmResult.Fallo("La escritura de depósitos todavía no está habilitada (sigue en FoxPro).");
        if (d.EstacionId <= 0)
            return AbmResult.Fallo("Elegí la estación.");
        if (d.Importe <= 0)
            return AbmResult.Fallo("El importe debe ser mayor que cero (el egreso se resta automáticamente).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM vehiculo_estacion_saldo";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO vehiculo_estacion_saldo
                        (id, empresa, estacion, estacion_n, fecha, forma_pago, importe, usuario, comentario, _deleted)
                    VALUES
                        (@id, 'NORTUR', @est, @estn, @fec, @fp, @imp, @user, @com, 0)
                    """;
                ins.Parameters.Add(new SqlParameter("@id", nuevoId));
                ins.Parameters.Add(new SqlParameter("@est", (long)d.EstacionId));
                ins.Parameters.Add(new SqlParameter("@estn", (object?)(d.Estacion ?? "").Trim() ?? DBNull.Value));
                ins.Parameters.Add(new SqlParameter("@fec", d.Fecha.ToDateTime(TimeOnly.MinValue)));
                ins.Parameters.Add(new SqlParameter("@fp", (object?)(d.FormaPago ?? "").Trim() ?? DBNull.Value));
                ins.Parameters.Add(new SqlParameter("@imp", d.EsEgreso ? -Math.Abs(d.Importe) : Math.Abs(d.Importe)));
                ins.Parameters.Add(new SqlParameter("@user", (object?)(d.Usuario ?? "").Trim() ?? DBNull.Value));
                ins.Parameters.Add(new SqlParameter("@com", (object?)(d.Comentario ?? "").Trim() ?? DBNull.Value));
                await ins.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta el depósito: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de un depósito (vehiculo_estacion_saldo_mant → DELETE). Abortada por el flag.</summary>
    public async Task<AbmResult> BajaDepositoEstacionAsync(int id)
    {
        if (!AbmFeatureFlags.DepositosCombustibleAbmActivo)
            return AbmResult.Fallo("La escritura de depósitos todavía no está habilitada (sigue en FoxPro).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM vehiculo_estacion_saldo WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El depósito ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar el depósito: {ex.Message}");
        }
    }

    // ── Artículos por rubro de consumo (estacion_rubro_articulo_abm.scx) ──
    //  nombre + rubro obligatorios (validación del _abm); no duplicar (nombre + rubro). id = MAX(id)+1
    //  (no identity). Baja FÍSICA (DELETE, sin f_delete). Todo gated por ArticulosRubroAbmActivo.

    /// <summary>Alta de un artículo (estacion_rubro_articulo_abm modo "alta"). Abortada por el flag.</summary>
    public async Task<AbmResult> AltaArticuloRubroAsync(int rubroId, string nombre)
    {
        if (!AbmFeatureFlags.ArticulosRubroAbmActivo)
            return AbmResult.Fallo("La escritura de artículos todavía no está habilitada (sigue en FoxPro).");
        nombre = (nombre ?? "").Trim();
        if (rubroId <= 0) return AbmResult.Fallo("Elegí el rubro.");
        if (string.IsNullOrWhiteSpace(nombre)) return AbmResult.Fallo("Cargá el nombre del artículo.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM estacion_rubro_articulo WHERE nombre = @nom AND idrubro = @rub AND ISNULL(_deleted,0) = 0";
                chk.Parameters.Add(new SqlParameter("@nom", nombre.ToUpperInvariant()));
                chk.Parameters.Add(new SqlParameter("@rub", (long)rubroId));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("Ese artículo ya existe para ese rubro.");
                }
            }
            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM estacion_rubro_articulo";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO estacion_rubro_articulo (id, idrubro, nombre, _deleted) VALUES (@id, @rub, @nom, 0)";
                ins.Parameters.Add(new SqlParameter("@id", nuevoId));
                ins.Parameters.Add(new SqlParameter("@rub", (long)rubroId));
                ins.Parameters.Add(new SqlParameter("@nom", nombre));
                await ins.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta el artículo: {ex.Message}");
        }
    }

    /// <summary>Modificación de un artículo (modo "modifica"). Abortada por el flag.</summary>
    public async Task<AbmResult> ModificaArticuloRubroAsync(int id, int rubroId, string nombre)
    {
        if (!AbmFeatureFlags.ArticulosRubroAbmActivo)
            return AbmResult.Fallo("La escritura de artículos todavía no está habilitada (sigue en FoxPro).");
        nombre = (nombre ?? "").Trim();
        if (rubroId <= 0) return AbmResult.Fallo("Elegí el rubro.");
        if (string.IsNullOrWhiteSpace(nombre)) return AbmResult.Fallo("Cargá el nombre del artículo.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE estacion_rubro_articulo SET idrubro = @rub, nombre = @nom WHERE id = @id AND ISNULL(_deleted,0) = 0";
            upd.Parameters.Add(new SqlParameter("@rub", (long)rubroId));
            upd.Parameters.Add(new SqlParameter("@nom", nombre));
            upd.Parameters.Add(new SqlParameter("@id", id));
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El artículo ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo modificar el artículo: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de un artículo (DELETE — así lo hace el FoxPro). Abortada por el flag.</summary>
    public async Task<AbmResult> BajaArticuloRubroAsync(int id)
    {
        if (!AbmFeatureFlags.ArticulosRubroAbmActivo)
            return AbmResult.Fallo("La escritura de artículos todavía no está habilitada (sigue en FoxPro).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM estacion_rubro_articulo WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El artículo ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar el artículo: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MÓDULO RESERVAS — puertas de alta al circuito `viaje` (ANDAMIAJE, apagado por flag)
    //
    //  ⚠ Estas 3 no son catálogos: insertan filas en `viaje` (el circuito central). Son Fase 4
    //  del plan Buslink y cambian de dueño el DÍA D. La lógica está codificada completa y fiel a
    //  los planos (RESERVA_TRANSPORTACION.md / RESERVA_PLANTILLAS.md), pero cada método aborta si
    //  su flag es false — HOY NO SE INVOCA NINGUNA (la UI las tiene deshabilitadas). Mejora sobre
    //  el FoxPro: TODO va con transacción (el FoxPro grababa sin transacción).
    //
    //  Reglas de oro al escribir (cuando llegue el día D):
    //   - str_f_reserva SIEMPRE sincronizado con f_reserva (YYYYMMDD char — informes viejos filtran por él).
    //   - Estado inicial único 'SIN ASIGNAR'; cronograma = cronogram2 = 'S/C'.
    //   - viaje_log con motivo 'ALTA' en cada inserción.
    //   - reserva_plantilla: baja FÍSICA (DELETE), id no-identity (MAX(id)+1).
    // ═══════════════════════════════════════════════════════════════════════════

    private const string SinFlagReservas =
        "La escritura de reservas todavía no está habilitada (el circuito de viajes sigue en FoxPro hasta el día del corte a Buslink).";

    // ── A) RESERVAS ESPECIALES — alta manual (viaje.origen='T') ──
    // Réplica de graba_viaje del plano RESERVA_TRANSPORTACION.md: resolución de grupo, loop
    // días×servicios (modo normal) o loop por día (modo ruta), viaje_log, viaje_adicional, guia.

    /// <summary>
    /// Alta de una reserva especial (reserva_transportacion_con_adicional.scx, graba_viaje).
    /// Genera N filas en `viaje` (días × cantidad de servicios, o una por día en modo ruta),
    /// resuelve/crea el grupo, loguea y graba adicionales. TODO en una transacción. Devuelve el
    /// rango de ids grabados en AbmResult.Id (el primero). Andamiaje: abortada por el flag.
    /// </summary>
    public async Task<AbmResult> AltaReservaEspecialAsync(ReservaEspecialInput r)
    {
        if (!AbmFeatureFlags.ReservasEspecialesAbmActivo)
            return AbmResult.Fallo(SinFlagReservas);

        var (ok, err) = ValidarReservaEspecial(r);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // 1) Resolución del grupo (INSERT/UPDATE cliente_grupo + arrastre de f_grupo_fin).
            long idGrupo = 0;
            var grupoNombre = string.IsNullOrWhiteSpace(r.Grupo) ? "SIN GRUPO" : r.Grupo.Trim();
            var fGrupoFin = r.FGrupoFin ?? r.FReserva;
            if (grupoNombre != "SIN GRUPO")
                (idGrupo, fGrupoFin) = await ResolverGrupoAsync(conn, tx, r.IdCliente.Trim(), grupoNombre, r.FReserva, fGrupoFin);

            // 2) Inserción de los viajes.
            int primerId = 0, ultimoId = 0, cantidad = 0;
            if (r.VariosDias)
            {
                // Modo ruta: una fila por día del rango, compartiendo id_viaje_i.
                long serie = await SiguienteParametroAsync(conn, tx, "id_viaje_int");
                var dia = r.FReserva;
                var comentarioRuta = ("SERV. RUTA " + r.Comentario).Trim();
                while (dia <= (r.FVuelve ?? r.FReserva))
                {
                    bool esPrimero = dia == r.FReserva;
                    bool esUltimo = dia == (r.FVuelve ?? r.FReserva);
                    var hIni = esPrimero ? r.HoraInicio : new TimeOnly(0, 0);
                    var hFin = esUltimo ? (r.HoraVuelve ?? new TimeOnly(23, 59)) : new TimeOnly(23, 59);
                    ultimoId = await InsertViajeAsync(conn, tx, r, dia, hIni, hFin, grupoNombre, fGrupoFin,
                        idGrupo, r.IdServicio1, comentarioRuta, serie);
                    if (primerId == 0) primerId = ultimoId;
                    cantidad++;
                    dia = dia.AddDays(1);
                }
                // Adicionales una sola vez, sobre el último id (fiel al FoxPro).
                await GrabarAdicionalesAsync(conn, tx, ultimoId, r.Adicionales);
                await GrabarLogAsync(conn, tx, ultimoId, r.Usuario, "CARGA DE RESERVA");
            }
            else
            {
                // Modo normal: días (f_reserva → f_fin) × cantidad de servicios.
                var dia = r.FReserva;
                var fFin = r.FFin ?? r.FReserva;
                while (dia <= fFin)
                {
                    for (int i = 0; i < Math.Max(1, r.CantidadServicios); i++)
                    {
                        ultimoId = await InsertViajeAsync(conn, tx, r, dia, r.HoraInicio, r.HoraFin ?? r.HoraInicio,
                            grupoNombre, fGrupoFin, idGrupo, r.IdServicio1, r.Comentario, null);
                        if (primerId == 0) primerId = ultimoId;
                        await GrabarAdicionalesAsync(conn, tx, ultimoId, r.Adicionales);
                        await GrabarLogAsync(conn, tx, ultimoId, r.Usuario, "CARGA DE RESERVA");
                        cantidad++;
                    }
                    dia = dia.AddDays(1);
                }
            }

            // 3) Upsert de la guía (tabla guia) si se cargó nombre.
            await UpsertGuiaAsync(conn, tx, r.GuiaNombre, r.GuiaTelefono);

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(primerId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo grabar la reserva: {ex.Message}");
        }
    }

    // ── B) PLANTILLAS — ABM de filas de reserva_plantilla (baja física, id no-identity) ──

    /// <summary>Alta de una fila de plantilla (reserva_plantilla_mantenimiento_abm.scx modo "alta").
    /// id = MAX(id)+1 (no identity). Andamiaje: abortada por el flag.</summary>
    public async Task<AbmResult> AltaPlantillaFilaAsync(PlantillaFilaInput p)
    {
        if (!AbmFeatureFlags.PlantillasAbmActivo)
            return AbmResult.Fallo(SinFlagReservas);
        var (ok, err) = ValidarPlantillaFila(p);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Cabecera única dentro de la plantilla (como el FoxPro).
            if (!string.IsNullOrWhiteSpace(p.Cabecera) && p.Cabecera.Trim() != "SIN CABECERA")
            {
                await using var chk = conn.CreateCommand();
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM reserva_plantilla WHERE RTRIM(id_reserva)=@r AND RTRIM(cabecera)=@c AND _deleted=0";
                chk.Parameters.Add(new SqlParameter("@r", p.IdReserva.Trim()));
                chk.Parameters.Add(new SqlParameter("@c", p.Cabecera.Trim()));
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo($"La cabecera «{p.Cabecera.Trim()}» ya existe en esta plantilla.");
                }
            }

            int nuevoId;
            await using (var mx = conn.CreateCommand())
            {
                mx.Transaction = tx;
                mx.CommandText = "SELECT ISNULL(MAX(id),0)+1 FROM reserva_plantilla";
                nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                // Grabamos TODOS los campos (igualando el bug heredado del INSERT que omitía
                // d_destino_/km_real/gps_cod/iata_*: acá se graban, como sí hace el UPDATE del FoxPro).
                ins.CommandText = """
                    INSERT INTO reserva_plantilla
                        (id, id_reserva, cronograma, hs_inicio, hs_fin, id_servici, id_vehicul,
                         desde, hasta, pax, km, hs, km_real, comentario, cabecera, empresa_de,
                         recorrido_, d_destino_, iata_desde, iata_hasta, gps_cod, id_guia,
                         nombre_gui, guia_dueno, dia_siguie, _deleted)
                    VALUES
                        (@id, @res, @cron, @hi, @hf, @serv, @veh, @desde, @hasta, @pax, @km, @hs,
                         @kmr, @com, @cab, @emp, @rec, @prov, @iad, @iah, @gps, @idg, @gnom, @gduen, @diasig, 0)
                    """;
                AgregarParamsPlantilla(ins, p, nuevoId);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(nuevoId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo agregar la fila de plantilla: {ex.Message}");
        }
    }

    /// <summary>Modificación de una fila de plantilla (UPDATE por id de TODOS los campos).</summary>
    public async Task<AbmResult> ModificaPlantillaFilaAsync(int id, PlantillaFilaInput p)
    {
        if (!AbmFeatureFlags.PlantillasAbmActivo)
            return AbmResult.Fallo(SinFlagReservas);
        var (ok, err) = ValidarPlantillaFila(p);
        if (!ok) return AbmResult.Fallo(err!);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE reserva_plantilla SET
                        cronograma=@cron, hs_inicio=@hi, hs_fin=@hf, id_servici=@serv, id_vehicul=@veh,
                        desde=@desde, hasta=@hasta, pax=@pax, km=@km, hs=@hs, km_real=@kmr, comentario=@com,
                        cabecera=@cab, empresa_de=@emp, recorrido_=@rec, d_destino_=@prov,
                        iata_desde=@iad, iata_hasta=@iah, gps_cod=@gps, id_guia=@idg, nombre_gui=@gnom,
                        guia_dueno=@gduen, dia_siguie=@diasig
                    WHERE id=@id AND _deleted=0
                    """;
                AgregarParamsPlantilla(upd, p, id);
                if (await upd.ExecuteNonQueryAsync() == 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("La fila de plantilla ya no existe.");
                }
            }
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo modificar la fila de plantilla: {ex.Message}");
        }
    }

    /// <summary>Baja FÍSICA de una fila de plantilla (DELETE — así lo hace el FoxPro, sin f_delete).</summary>
    public async Task<AbmResult> BajaPlantillaFilaAsync(int id)
    {
        if (!AbmFeatureFlags.PlantillasAbmActivo)
            return AbmResult.Fallo(SinFlagReservas);
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM reserva_plantilla WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("La fila de plantilla ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar la fila de plantilla: {ex.Message}");
        }
    }

    /// <summary>Elimina TODA una plantilla (DELETE por id_reserva — "Eliminar Todo" del FoxPro).</summary>
    public async Task<AbmResult> BajaPlantillaCompletaAsync(string idReserva)
    {
        if (!AbmFeatureFlags.PlantillasAbmActivo)
            return AbmResult.Fallo(SinFlagReservas);
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM reserva_plantilla WHERE RTRIM(id_reserva) = @r";
            del.Parameters.Add(new SqlParameter("@r", idReserva.Trim()));
            var n = await del.ExecuteNonQueryAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(n);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar la plantilla: {ex.Message}");
        }
    }

    /// <summary>Renombra una plantilla (UPDATE id_reserva). Si el destino existe, FUSIONA (suma filas).</summary>
    public async Task<AbmResult> RenombrarPlantillaAsync(string origen, string destino)
    {
        if (!AbmFeatureFlags.PlantillasAbmActivo)
            return AbmResult.Fallo(SinFlagReservas);
        if (string.IsNullOrWhiteSpace(destino))
            return AbmResult.Fallo("Cargá el nuevo nombre de la plantilla.");
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE reserva_plantilla SET id_reserva = @dst WHERE RTRIM(id_reserva) = @org";
            upd.Parameters.Add(new SqlParameter("@dst", destino.Trim()));
            upd.Parameters.Add(new SqlParameter("@org", origen.Trim()));
            var n = await upd.ExecuteNonQueryAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(n);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo renombrar la plantilla: {ex.Message}");
        }
    }

    /// <summary>Duplica una plantilla (copia todas las filas con id_reserva nuevo, ids nuevos).</summary>
    public async Task<AbmResult> DuplicarPlantillaAsync(string origen, string destino)
    {
        if (!AbmFeatureFlags.PlantillasAbmActivo)
            return AbmResult.Fallo(SinFlagReservas);
        if (string.IsNullOrWhiteSpace(destino))
            return AbmResult.Fallo("Cargá el nombre de la plantilla destino.");
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // SELECT INTO con ids nuevos: copiamos todas las columnas menos id, poniendo id = MAX+ROW_NUMBER.
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DECLARE @base int = (SELECT ISNULL(MAX(id),0) FROM reserva_plantilla);
                INSERT INTO reserva_plantilla
                    (id, id_reserva, cronograma, hs_inicio, hs_fin, hs_entrada, desde, desde_rama,
                     hasta, hasta_rama, id_servici, id_vehicul, comentario, hs, km, km_real, pax,
                     id_guia, nombre_gui, guia_dueno, gps_cod, adi_cod_1, adi_nom_1, adi_can_1,
                     adi_cod_2, adi_nom_2, adi_can_2, adi_cod_3, adi_nom_3, adi_can_3, adi_cod_4,
                     adi_nom_4, adi_can_4, adi_cod_5, adi_nom_5, adi_can_5, d_destino_, cabecera,
                     empresa_de, iata_desde, iata_hasta, recorrido_, tipo_mov, cod_cab, dia_siguie, _deleted)
                SELECT
                     @base + ROW_NUMBER() OVER (ORDER BY id), @dst, cronograma, hs_inicio, hs_fin, hs_entrada,
                     desde, desde_rama, hasta, hasta_rama, id_servici, id_vehicul, comentario, hs, km, km_real, pax,
                     id_guia, nombre_gui, guia_dueno, gps_cod, adi_cod_1, adi_nom_1, adi_can_1,
                     adi_cod_2, adi_nom_2, adi_can_2, adi_cod_3, adi_nom_3, adi_can_3, adi_cod_4,
                     adi_nom_4, adi_can_4, adi_cod_5, adi_nom_5, adi_can_5, d_destino_, cabecera,
                     empresa_de, iata_desde, iata_hasta, recorrido_, tipo_mov, cod_cab, dia_siguie, 0
                FROM reserva_plantilla WHERE RTRIM(id_reserva) = @org AND _deleted = 0
                """;
            cmd.Parameters.Add(new SqlParameter("@dst", destino.Trim()));
            cmd.Parameters.Add(new SqlParameter("@org", origen.Trim()));
            var n = await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(n);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo duplicar la plantilla: {ex.Message}");
        }
    }

    // ── C) ARMADO — generación masiva (reserva_plantilla_armar.scx → viaje.origen='P') ──

    /// <summary>
    /// Arma una plantilla: genera una fila en `viaje` (origen 'P') por cada fila de la plantilla ×
    /// cada fecha del rango cuyo día-de-semana esté marcado (respetando feriados). Toma un lote
    /// (parametro.lote_plant+1) que comparten todas las filas de la corrida (permite deshacerla).
    /// TODO en una transacción por lote. Andamiaje: abortada por el flag.
    /// </summary>
    public async Task<AbmResult> ArmarPlantillaAsync(ArmadoInput a)
    {
        if (!AbmFeatureFlags.ArmadoPlantillasActivo)
            return AbmResult.Fallo(SinFlagReservas);
        if (string.IsNullOrWhiteSpace(a.IdReserva)) return AbmResult.Fallo("Elegí una plantilla.");
        if (string.IsNullOrWhiteSpace(a.IdCliente)) return AbmResult.Fallo("Elegí un cliente.");
        if (a.Hasta < a.Desde) return AbmResult.Fallo("El rango de fechas es inválido.");
        if (!a.DiasSemana.Any(d => d) && !a.IncluirFeriados) return AbmResult.Fallo("Marcá al menos un día.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Filas de la plantilla + feriados del rango (dentro de la tx para consistencia).
            var filas = await LeerPlantillaParaArmarAsync(conn, tx, a.IdReserva.Trim());
            if (filas.Count == 0) { await tx.RollbackAsync(); return AbmResult.Fallo("La plantilla no tiene filas."); }
            var feriados = await LeerFeriadosAsync(conn, tx, a.Desde, a.Hasta);

            long lote = await SiguienteParametroAsync(conn, tx, "lote_plant");
            int generados = 0;
            for (var dia = a.Desde; dia <= a.Hasta; dia = dia.AddDays(1))
            {
                bool esFeriado = feriados.Contains(dia);
                bool diaMarcado = a.DiasSemana[(int)dia.DayOfWeek == 0 ? 6 : (int)dia.DayOfWeek - 1];
                bool generar = esFeriado ? a.IncluirFeriados : diaMarcado;
                if (!generar) continue;

                foreach (var f in filas)
                {
                    await InsertViajePlantillaAsync(conn, tx, a, f, dia, lote);
                    generados++;
                }
            }
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito((int)lote);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo armar la plantilla: {ex.Message}");
        }
    }

    // ── Helpers de escritura del circuito viaje (compartidos por las 3 puertas) ──

    /// <summary>Resuelve el grupo: lo crea si no existe, o extiende su f_grupo_fin (arrastrando los
    /// viajes del grupo) si la reserva es posterior. Devuelve (idGrupo, fGrupoFinResuelto).</summary>
    private static async Task<(long, DateOnly)> ResolverGrupoAsync(
        SqlConnection conn, SqlTransaction tx, string idCliente, string nombre, DateOnly fReserva, DateOnly fGrupoFin)
    {
        long idGrupo;
        DateOnly finActual;
        await using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT TOP 1 id, f_grupo_fi FROM cliente_grupo WHERE RTRIM(id_cliente)=@cli AND RTRIM(nombre)=@nom AND _deleted=0";
            sel.Parameters.Add(new SqlParameter("@cli", idCliente));
            sel.Parameters.Add(new SqlParameter("@nom", nombre));
            await using var rd = await sel.ExecuteReaderAsync();
            if (await rd.ReadAsync())
            {
                idGrupo = Convert.ToInt64(rd.GetValue(0));
                finActual = rd.IsDBNull(1) ? fReserva : DateOnly.FromDateTime(rd.GetDateTime(1));
            }
            else { idGrupo = 0; finActual = fGrupoFin; }
        }

        if (idGrupo == 0)
        {
            // INSERT nuevo grupo (id no-identity → MAX+1).
            await using var mx = conn.CreateCommand();
            mx.Transaction = tx;
            mx.CommandText = "SELECT ISNULL(MAX(id),0)+1 FROM cliente_grupo";
            idGrupo = Convert.ToInt64(await mx.ExecuteScalarAsync() ?? 1L);
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO cliente_grupo (id, id_cliente, nombre, f_grupo_in, f_grupo_fi, _deleted)
                VALUES (@id, @cli, @nom, @ini, @fin, 0)
                """;
            ins.Parameters.Add(new SqlParameter("@id", idGrupo));
            ins.Parameters.Add(new SqlParameter("@cli", idCliente));
            ins.Parameters.Add(new SqlParameter("@nom", nombre));
            ins.Parameters.Add(new SqlParameter("@ini", fReserva.ToDateTime(TimeOnly.MinValue)));
            ins.Parameters.Add(new SqlParameter("@fin", fGrupoFin.ToDateTime(TimeOnly.MinValue)));
            await ins.ExecuteNonQueryAsync();
            return (idGrupo, fGrupoFin);
        }

        // Existe: si la reserva es posterior al fin, extender el grupo y arrastrar sus viajes.
        if (fReserva > finActual)
        {
            await using (var upG = conn.CreateCommand())
            {
                upG.Transaction = tx;
                upG.CommandText = "UPDATE cliente_grupo SET f_grupo_fi=@fin WHERE id=@id";
                upG.Parameters.Add(new SqlParameter("@fin", fReserva.ToDateTime(TimeOnly.MinValue)));
                upG.Parameters.Add(new SqlParameter("@id", idGrupo));
                await upG.ExecuteNonQueryAsync();
            }
            await using (var upV = conn.CreateCommand())
            {
                upV.Transaction = tx;
                upV.CommandText = "UPDATE viaje SET f_grupo_fi=@fin WHERE id_grupo=@id AND _deleted=0";
                upV.Parameters.Add(new SqlParameter("@fin", fReserva.ToDateTime(TimeOnly.MinValue)));
                upV.Parameters.Add(new SqlParameter("@id", idGrupo));
                await upV.ExecuteNonQueryAsync();
            }
            return (idGrupo, fReserva);
        }
        return (idGrupo, finActual);
    }

    /// <summary>Incrementa un contador global de `parametro` (id_viaje_int, lote_plant) y devuelve el nuevo.</summary>
    private static async Task<long> SiguienteParametroAsync(SqlConnection conn, SqlTransaction tx, string columna)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE parametro SET {columna} = ISNULL({columna},0)+1; SELECT CAST({columna} AS bigint) FROM parametro";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 1L);
    }

    /// <summary>INSERT de un viaje de reserva especial (origen 'T'). Devuelve el id_viaje generado.</summary>
    private static async Task<int> InsertViajeAsync(
        SqlConnection conn, SqlTransaction tx, ReservaEspecialInput r, DateOnly dia,
        TimeOnly hIni, TimeOnly hFin, string grupo, DateOnly fGrupoFin, long idGrupo,
        string idServicio, string comentario, long? idViajeRuta)
    {
        var hsInicio = dia.ToDateTime(hIni);
        // Si la hora fin < inicio, el cierre es al día siguiente (cruce de medianoche).
        var diaFin = hFin < hIni ? dia.AddDays(1) : dia;
        var hsFinApr = diaFin.ToDateTime(hFin);

        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO viaje
                (str_f_rese, origen, estado_via, cronograma, cronogram2, f_pedido, f_reserva,
                 hs_inicio, hs_s_inici, hs_fin_apr, hs_present, pax, km, agua, nombre_gui, grupo,
                 f_grupo_fi, id_grupo, vuelo, id_servici, id_servic2, id_servic3, id_cliente,
                 nombre_cli, id_operado, comentario, d_destino, h_destino, d_destino_, mas100km,
                 id_vehicu2, voucher_nr, id_viaje_i, moneda_con, importe_co, sin_cargo,
                 descuento_, moneda_pag, importe_pa, sin_cargo_, f_create, u_create, _deleted)
            OUTPUT INSERTED.id_viaje
            VALUES
                (@strf, 'T', 'SIN ASIGNAR', 'S/C', 'S/C', @fped, @fres, @hsini, @hssini, @hsfin,
                 @hspre, @pax, @km, @agua, @gnom, @grupo, @fgfin, @idgrupo, @vuelo, @serv1, @serv2,
                 @serv3, @cli, @clinom, @oper, @com, @desde, @hasta, @prov, @mas100, @veh, @vouch,
                 @idvruta, @moncon, @impcon, @sincon, @desc, @monpag, @imppag, @sinpag, @fcreate, @ucreate, 0)
            """;
        ins.Parameters.Add(new SqlParameter("@strf", dia.ToString("yyyyMMdd")));
        ins.Parameters.Add(new SqlParameter("@fped", (r.FPedido ?? dia).ToDateTime(TimeOnly.MinValue)));
        ins.Parameters.Add(new SqlParameter("@fres", dia.ToDateTime(TimeOnly.MinValue)));
        ins.Parameters.Add(new SqlParameter("@hsini", hsInicio));
        ins.Parameters.Add(new SqlParameter("@hssini", hIni.ToString("HH:mm")));
        ins.Parameters.Add(new SqlParameter("@hsfin", hsFinApr));
        ins.Parameters.Add(new SqlParameter("@hspre", (object?)(r.HoraPresentacion?.ToString("yyyy-MM-dd HH:mm:ss")) ?? DBNull.Value));
        ins.Parameters.Add(new SqlParameter("@pax", r.Pax));
        ins.Parameters.Add(new SqlParameter("@km", (long)r.Km));
        ins.Parameters.Add(new SqlParameter("@agua", r.Agua));
        ins.Parameters.Add(new SqlParameter("@gnom", Nz(r.GuiaNombreCompleto)));
        ins.Parameters.Add(new SqlParameter("@grupo", grupo));
        ins.Parameters.Add(new SqlParameter("@fgfin", fGrupoFin.ToDateTime(TimeOnly.MinValue)));
        ins.Parameters.Add(new SqlParameter("@idgrupo", idGrupo));
        ins.Parameters.Add(new SqlParameter("@vuelo", string.IsNullOrWhiteSpace(r.Vuelo) ? "SIN VUELO" : r.Vuelo.Trim()));
        ins.Parameters.Add(new SqlParameter("@serv1", idServicio.Trim()));
        ins.Parameters.Add(new SqlParameter("@serv2", Nz(r.IdServicio2)));
        ins.Parameters.Add(new SqlParameter("@serv3", Nz(r.IdServicio3)));
        ins.Parameters.Add(new SqlParameter("@cli", r.IdCliente.Trim()));
        ins.Parameters.Add(new SqlParameter("@clinom", Nz(r.NombreCliente)));
        ins.Parameters.Add(new SqlParameter("@oper", Nz(r.IdOperador)));
        ins.Parameters.Add(new SqlParameter("@com", Nz(comentario)));
        ins.Parameters.Add(new SqlParameter("@desde", Nz(r.Desde)));
        ins.Parameters.Add(new SqlParameter("@hasta", Nz(r.Hasta)));
        ins.Parameters.Add(new SqlParameter("@prov", Nz(r.Provincia)));
        ins.Parameters.Add(new SqlParameter("@mas100", r.Mas100Km ? 1 : 0));
        ins.Parameters.Add(new SqlParameter("@veh", r.TipoVehiculo.Trim()));
        ins.Parameters.Add(new SqlParameter("@vouch", (long)r.Voucher));
        ins.Parameters.Add(new SqlParameter("@idvruta", (object?)idViajeRuta ?? DBNull.Value));
        // Valor Especial (permiso F): si no se usó, van neutros.
        ins.Parameters.Add(new SqlParameter("@moncon", Nz(r.MonedaConvenida)));
        ins.Parameters.Add(new SqlParameter("@impcon", r.ImporteConvenido));
        ins.Parameters.Add(new SqlParameter("@sincon", r.SinCargoCliente ? 1 : 0));
        ins.Parameters.Add(new SqlParameter("@desc", r.Descuento));
        ins.Parameters.Add(new SqlParameter("@monpag", Nz(r.MonedaPago)));
        ins.Parameters.Add(new SqlParameter("@imppag", r.ImportePago));
        ins.Parameters.Add(new SqlParameter("@sinpag", r.SinCargoEmpresa ? 1 : 0));
        ins.Parameters.Add(new SqlParameter("@fcreate", DateTime.Today));
        ins.Parameters.Add(new SqlParameter("@ucreate", Nz(r.Usuario)));
        return Convert.ToInt32(await ins.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>INSERT de un viaje de plantilla (origen 'P'), con la lógica E/S de la cabecera.</summary>
    private static async Task InsertViajePlantillaAsync(
        SqlConnection conn, SqlTransaction tx, ArmadoInput a, PlantillaFilaArmar f, DateOnly dia, long lote)
    {
        // Horas char "HH:MM" → datetime; cruce de medianoche → día siguiente.
        var hIni = ParseHora(f.HsInicio);
        var hFin = ParseHora(f.HsFin);
        var hsInicio = dia.ToDateTime(hIni);
        var diaFin = hFin < hIni ? dia.AddDays(1) : dia;
        var hsFinApr = diaFin.ToDateTime(hFin);

        // Lógica E/S: posición 7 de la cabecera + check "nombre de planta como origen/destino".
        string desde = f.Desde, hasta = f.Hasta;
        if (a.NombrePlanta && f.Cabecera.Length >= 7)
        {
            char es = f.Cabecera[6];
            if (es == 'E') { desde = f.Desde; hasta = f.EmpresaDestino; }
            else if (es == 'S') { desde = f.EmpresaDestino; hasta = f.Hasta; }
        }

        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO viaje
                (str_f_rese, origen, estado_via, cronograma, cronogram2, f_reserva, hs_inicio,
                 hs_s_inici, hs_fin_apr, pax, km, hs, grupo, f_grupo_fi, vuelo, id_servici,
                 id_cliente, nombre_cli, comentario, id_vehicu2, id_guia, nombre_gui, guia_dueno,
                 lote, gps_cod, id_plantil, d_destino, h_destino, d_destino_, cabecera, recorrido_,
                 f_create, u_create, _deleted)
            OUTPUT INSERTED.id_viaje
            VALUES
                (@strf, 'P', 'SIN ASIGNAR', @cron, @cron, @fres, @hsini, @hssini, @hsfin, @pax, @km,
                 @hs, 'SIN GRUPO', @fres, 'SIN VUELO', @serv, @cli, @clinom, @com, @veh, @idg, @gnom,
                 @gduen, @lote, @gps, @idplant, @desde, @hasta, @prov, @cab, @rec, @fcreate, @ucreate, 0)
            """;
        ins.Parameters.Add(new SqlParameter("@strf", dia.ToString("yyyyMMdd")));
        ins.Parameters.Add(new SqlParameter("@cron", Nz(f.Cronograma)));
        ins.Parameters.Add(new SqlParameter("@fres", dia.ToDateTime(TimeOnly.MinValue)));
        ins.Parameters.Add(new SqlParameter("@hsini", hsInicio));
        ins.Parameters.Add(new SqlParameter("@hssini", hIni.ToString("HH:mm")));
        ins.Parameters.Add(new SqlParameter("@hsfin", hsFinApr));
        ins.Parameters.Add(new SqlParameter("@pax", f.Pax));
        ins.Parameters.Add(new SqlParameter("@km", (long)f.Km));
        ins.Parameters.Add(new SqlParameter("@hs", (long)f.Hs));
        ins.Parameters.Add(new SqlParameter("@serv", f.IdServicio.Trim()));
        ins.Parameters.Add(new SqlParameter("@cli", a.IdCliente.Trim()));
        ins.Parameters.Add(new SqlParameter("@clinom", Nz(a.NombreCliente)));
        ins.Parameters.Add(new SqlParameter("@com", Nz(f.Comentario)));
        ins.Parameters.Add(new SqlParameter("@veh", f.TipoVeh.Trim()));
        ins.Parameters.Add(new SqlParameter("@idg", Nz(f.IdGuia)));
        ins.Parameters.Add(new SqlParameter("@gnom", Nz(f.NombreGuia)));
        ins.Parameters.Add(new SqlParameter("@gduen", Nz(f.GuiaDueno)));
        ins.Parameters.Add(new SqlParameter("@lote", lote));
        ins.Parameters.Add(new SqlParameter("@gps", Nz(f.GpsCod)));
        ins.Parameters.Add(new SqlParameter("@idplant", (long)f.Id));
        ins.Parameters.Add(new SqlParameter("@desde", Nz(desde)));
        ins.Parameters.Add(new SqlParameter("@hasta", Nz(hasta)));
        ins.Parameters.Add(new SqlParameter("@prov", Nz(f.Provincia)));
        ins.Parameters.Add(new SqlParameter("@cab", Nz(f.Cabecera)));
        ins.Parameters.Add(new SqlParameter("@rec", Nz(f.Recorrido)));
        ins.Parameters.Add(new SqlParameter("@fcreate", DateTime.Today));
        ins.Parameters.Add(new SqlParameter("@ucreate", Nz(a.Usuario)));
        var idViaje = Convert.ToInt32(await ins.ExecuteScalarAsync() ?? 0);
        await GrabarAdicionalesAsync(conn, tx, idViaje, f.Adicionales);
        await GrabarLogAsync(conn, tx, idViaje, a.Usuario, "CARGA DE PLANTILLA");
    }

    private static async Task GrabarAdicionalesAsync(
        SqlConnection conn, SqlTransaction tx, int idViaje, IReadOnlyList<AdicionalInput>? adics)
    {
        if (adics is null || adics.Count == 0) return;
        foreach (var ad in adics)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO viaje_adicional (id_viaje, id_adicion, nombre, cantidad, precio, _deleted)
                VALUES (@v, @cod, @nom, @can, @pre, 0)
                """;
            ins.Parameters.Add(new SqlParameter("@v", idViaje));
            ins.Parameters.Add(new SqlParameter("@cod", Nz(ad.Codigo)));
            ins.Parameters.Add(new SqlParameter("@nom", Nz(ad.Nombre)));
            ins.Parameters.Add(new SqlParameter("@can", ad.Cantidad));
            ins.Parameters.Add(new SqlParameter("@pre", ad.Precio));
            await ins.ExecuteNonQueryAsync();
        }
    }

    private static async Task GrabarLogAsync(SqlConnection conn, SqlTransaction tx, int idViaje, string usuario, string comentario)
    {
        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO viaje_log (id_viaje, usuario, motivo, hora, cronograma, id_chofer, interno_or, interno_ne, comentario, _deleted)
            VALUES (@v, @u, 'ALTA', @h, '', '', 0, 0, @c, 0)
            """;
        ins.Parameters.Add(new SqlParameter("@v", idViaje));
        ins.Parameters.Add(new SqlParameter("@u", Nz(usuario)));
        ins.Parameters.Add(new SqlParameter("@h", DateTime.Now));
        ins.Parameters.Add(new SqlParameter("@c", comentario));
        await ins.ExecuteNonQueryAsync();
    }

    private static async Task UpsertGuiaAsync(SqlConnection conn, SqlTransaction tx, string? nombre, string? telefono)
    {
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim() == "SIN GUIA") return;
        await using var sel = conn.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = "SELECT COUNT(*) FROM guia WHERE RTRIM(nombre)=@n AND _deleted=0";
        sel.Parameters.Add(new SqlParameter("@n", nombre.Trim()));
        if ((int)(await sel.ExecuteScalarAsync() ?? 0) == 0)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO guia (nombre, telefono, _deleted) VALUES (@n, @t, 0)";
            ins.Parameters.Add(new SqlParameter("@n", nombre.Trim()));
            ins.Parameters.Add(new SqlParameter("@t", Nz(telefono)));
            await ins.ExecuteNonQueryAsync();
        }
    }

    // Lee las filas de una plantilla en un shape apto para el armado (dentro de la tx del armado).
    private static async Task<List<PlantillaFilaArmar>> LeerPlantillaParaArmarAsync(SqlConnection conn, SqlTransaction tx, string idReserva)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, RTRIM(ISNULL(cronograma,'')), RTRIM(ISNULL(hs_inicio,'')), RTRIM(ISNULL(hs_fin,'')),
                   RTRIM(ISNULL(id_servici,'')), RTRIM(ISNULL(id_vehicul,'')), RTRIM(ISNULL(desde,'')),
                   RTRIM(ISNULL(hasta,'')), CAST(ISNULL(pax,0) AS int), CAST(ISNULL(km,0) AS int),
                   CAST(ISNULL(hs,0) AS int), RTRIM(ISNULL(cabecera,'')), RTRIM(ISNULL(empresa_de,'')),
                   RTRIM(ISNULL(recorrido_,'')), RTRIM(ISNULL(d_destino_,'')), RTRIM(ISNULL(comentario,'')),
                   RTRIM(ISNULL(id_guia,'')), RTRIM(ISNULL(nombre_gui,'')), RTRIM(ISNULL(guia_dueno,'')),
                   RTRIM(ISNULL(gps_cod,'')),
                   RTRIM(ISNULL(adi_cod_1,'')), RTRIM(ISNULL(adi_nom_1,'')), CAST(ISNULL(adi_can_1,0) AS int),
                   RTRIM(ISNULL(adi_cod_2,'')), RTRIM(ISNULL(adi_nom_2,'')), CAST(ISNULL(adi_can_2,0) AS int),
                   RTRIM(ISNULL(adi_cod_3,'')), RTRIM(ISNULL(adi_nom_3,'')), CAST(ISNULL(adi_can_3,0) AS int),
                   RTRIM(ISNULL(adi_cod_4,'')), RTRIM(ISNULL(adi_nom_4,'')), CAST(ISNULL(adi_can_4,0) AS int),
                   RTRIM(ISNULL(adi_cod_5,'')), RTRIM(ISNULL(adi_nom_5,'')), CAST(ISNULL(adi_can_5,0) AS int)
            FROM reserva_plantilla WHERE RTRIM(id_reserva)=@r AND _deleted=0
            ORDER BY RTRIM(ISNULL(hs_inicio,'')), id
            """;
        cmd.Parameters.Add(new SqlParameter("@r", idReserva));
        var result = new List<PlantillaFilaArmar>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var adics = new List<AdicionalInput>();
            for (int i = 20; i <= 32; i += 3)
            {
                var cod = rd.GetString(i).Trim(); var nom = rd.GetString(i + 1).Trim(); var can = rd.GetInt32(i + 2);
                if (!string.IsNullOrWhiteSpace(cod) || !string.IsNullOrWhiteSpace(nom))
                    adics.Add(new AdicionalInput(cod, nom, can, 0m));
            }
            result.Add(new PlantillaFilaArmar(
                rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4),
                rd.GetString(5), rd.GetString(6), rd.GetString(7), rd.GetInt32(8), rd.GetInt32(9),
                rd.GetInt32(10), rd.GetString(11), rd.GetString(12), rd.GetString(13), rd.GetString(14),
                rd.GetString(15), rd.GetString(16), rd.GetString(17), rd.GetString(18), rd.GetString(19), adics));
        }
        return result;
    }

    private static async Task<HashSet<DateOnly>> LeerFeriadosAsync(SqlConnection conn, SqlTransaction tx, DateOnly desde, DateOnly hasta)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT fecha FROM feriado WHERE _deleted=0 AND fecha BETWEEN @d AND @h";
        cmd.Parameters.Add(new SqlParameter("@d", desde.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(new SqlParameter("@h", hasta.ToDateTime(TimeOnly.MinValue)));
        var set = new HashSet<DateOnly>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            if (!rd.IsDBNull(0)) set.Add(DateOnly.FromDateTime(rd.GetDateTime(0)));
        return set;
    }

    private static void AgregarParamsPlantilla(SqlCommand cmd, PlantillaFilaInput p, int id)
    {
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@res", p.IdReserva.Trim()));
        cmd.Parameters.Add(new SqlParameter("@cron", Nz(p.Cronograma)));
        cmd.Parameters.Add(new SqlParameter("@hi", Nz(p.HoraIni)));
        cmd.Parameters.Add(new SqlParameter("@hf", Nz(p.HoraFin)));
        cmd.Parameters.Add(new SqlParameter("@serv", p.IdServicio.Trim()));
        cmd.Parameters.Add(new SqlParameter("@veh", p.TipoVeh.Trim()));
        cmd.Parameters.Add(new SqlParameter("@desde", Nz(p.Desde)));
        cmd.Parameters.Add(new SqlParameter("@hasta", Nz(p.Hasta)));
        cmd.Parameters.Add(new SqlParameter("@pax", (long)p.Pax));
        cmd.Parameters.Add(new SqlParameter("@km", (long)p.Km));
        cmd.Parameters.Add(new SqlParameter("@hs", (long)p.Hs));
        cmd.Parameters.Add(new SqlParameter("@kmr", (long)p.Km));
        cmd.Parameters.Add(new SqlParameter("@com", Nz(p.Comentario)));
        cmd.Parameters.Add(new SqlParameter("@cab", Nz(p.Cabecera)));
        cmd.Parameters.Add(new SqlParameter("@emp", Nz(p.EmpresaDestino)));
        cmd.Parameters.Add(new SqlParameter("@rec", Nz(p.Recorrido)));
        cmd.Parameters.Add(new SqlParameter("@prov", Nz(p.Provincia)));
        cmd.Parameters.Add(new SqlParameter("@iad", Nz(p.IataDesde)));
        cmd.Parameters.Add(new SqlParameter("@iah", Nz(p.IataHasta)));
        cmd.Parameters.Add(new SqlParameter("@gps", Nz(p.GpsCod)));
        cmd.Parameters.Add(new SqlParameter("@idg", Nz(p.IdGuia)));
        cmd.Parameters.Add(new SqlParameter("@gnom", Nz(p.NombreGuia)));
        cmd.Parameters.Add(new SqlParameter("@gduen", Nz(p.GuiaDueno)));
        cmd.Parameters.Add(new SqlParameter("@diasig", p.DiaSiguiente ? 1 : 0));
    }

    private static (bool, string?) ValidarReservaEspecial(ReservaEspecialInput r)
    {
        if ((r.FPedido ?? r.FReserva) > r.FReserva) return (false, "La fecha del pedido no puede ser posterior a la de la reserva.");
        if ((r.FFin ?? r.FReserva) < r.FReserva) return (false, "La fecha «duplica hasta» es anterior a la de la reserva.");
        if (r.VariosDias && (r.FVuelve ?? r.FReserva) < r.FReserva) return (false, "La fecha de vuelta es anterior a la de la reserva.");
        if (string.IsNullOrWhiteSpace(r.IdCliente)) return (false, "Cargá el cliente.");
        if (string.IsNullOrWhiteSpace(r.IdServicio1)) return (false, "Cargá el 1° servicio.");
        if (r.Pax <= 0) return (false, "La cantidad de pasajeros debe ser mayor a 0.");
        if (string.IsNullOrWhiteSpace(r.Desde) || string.IsNullOrWhiteSpace(r.Hasta)) return (false, "Cargá Desde y Hasta.");
        if (string.IsNullOrWhiteSpace(r.TipoVehiculo)) return (false, "Elegí el tipo de vehículo.");
        return (true, null);
    }

    private static (bool, string?) ValidarPlantillaFila(PlantillaFilaInput p)
    {
        if (string.IsNullOrWhiteSpace(p.IdReserva)) return (false, "Falta el nombre de la plantilla.");
        if (string.IsNullOrWhiteSpace(p.HoraIni)) return (false, "Cargá la hora de inicio.");
        if (string.IsNullOrWhiteSpace(p.IdServicio)) return (false, "Elegí el servicio.");
        if (string.IsNullOrWhiteSpace(p.TipoVeh)) return (false, "Elegí el tipo de vehículo.");
        if (p.Pax <= 0) return (false, "La cantidad de pasajeros debe ser mayor a 0.");
        if (p.Km <= 0 && p.Hs <= 0) return (false, "Cargá kilómetros u horas.");
        if (string.IsNullOrWhiteSpace(p.Desde) || string.IsNullOrWhiteSpace(p.Hasta)) return (false, "Cargá Desde y Hasta.");
        return (true, null);
    }

    private static TimeOnly ParseHora(string hhmm) =>
        TimeOnly.TryParseExact(hhmm?.Trim(), "HH:mm", out var t) ? t : new TimeOnly(0, 0);

    /// <summary>Helper: string vacío/null → DBNull; si no, el string tal cual (para SqlParameter).</summary>
    private static object Nz(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s.Trim();

    // ═══════════════════════════════════════════════════════════════════════
    //  SESIONES Y BITÁCORA DE ACCESOS
    //   - usuario_sesion : 1 fila por SESIÓN (inicio/fin/IP/host + session_id).
    //   - usuarios_logs  : 1 fila por EVENTO (LOGIN/LOGOUT/EXPIRADA/VENCIDA/
    //                      LOGIN_FALLIDO), con su session_id para cruzar.
    //  Tablas nuevas, dueño SQL. Escriben desde /auth/login y /auth/logout.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Duración máxima de una sesión (espeja el ExpiresUtc de la cookie, 8 hs).
    /// Una sesión abierta que supera este tiempo se considera VENCIDA (no EXPIRADA).</summary>
    public static readonly TimeSpan DuracionSesion = TimeSpan.FromHours(8);

    /// <summary>
    /// Registra un ingreso exitoso: genera el <c>session_id</c> (GUID), cierra las sesiones
    /// abiertas previas (EXPIRADA, o VENCIDA si superaron 8 hs) registrando su evento, inserta
    /// la sesión nueva en <c>usuario_sesion</c> y el evento LOGIN en <c>usuarios_logs</c>.
    /// Devuelve el session_id (para guardarlo en la cookie) o null si falló (NUNCA tira: el
    /// registro no debe impedir el login).
    /// </summary>
    public async Task<Guid?> RegistrarLoginAsync(string usuario, string? ip, string? hostname)
    {
        usuario = (usuario ?? "").Trim();
        if (string.IsNullOrWhiteSpace(usuario)) return null;
        var sessionId = Guid.NewGuid();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = (SqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            try
            {
                // 1) Datos del usuario (id + copia de credenciales/permisos al momento del login).
                int idUsuario; string password = "", nivel = "", acceso = "";
                await using (var q = conn.CreateCommand())
                {
                    q.Transaction = tx;
                    q.CommandText = """
                        SELECT TOP 1 id,
                               RTRIM(ISNULL(password, '')),
                               RTRIM(ISNULL(nivel,    '')),
                               RTRIM(ISNULL(acceso,   ''))
                        FROM usuario WHERE usuario = @u AND _deleted = 0
                        """;
                    q.Parameters.Add(new SqlParameter("@u", usuario));
                    await using var rd = await q.ExecuteReaderAsync();
                    if (!await rd.ReadAsync()) { await tx.RollbackAsync(); return null; }
                    idUsuario = rd.GetInt32(0);
                    password = rd.GetString(1);
                    nivel = rd.GetString(2);
                    acceso = rd.GetString(3);
                }

                // 2) Cerrar sesiones abiertas previas → registrar su evento (VENCIDA si +8hs, si no EXPIRADA).
                //    Se listan primero para poder loguear cada una con su session_id real.
                var previas = new List<(Guid? sid, bool vencida)>();
                await using (var sel = conn.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = """
                        SELECT session_id,
                               CASE WHEN DATEDIFF(second, f_inicio, SYSDATETIME()) > @seg THEN 1 ELSE 0 END
                        FROM usuario_sesion WHERE id_usuario = @id AND activa = 1
                        """;
                    sel.Parameters.Add(new SqlParameter("@id", idUsuario));
                    sel.Parameters.Add(new SqlParameter("@seg", (int)DuracionSesion.TotalSeconds));
                    await using var rd = await sel.ExecuteReaderAsync();
                    while (await rd.ReadAsync())
                        previas.Add((rd.IsDBNull(0) ? null : rd.GetGuid(0), rd.GetInt32(1) == 1));
                }
                foreach (var (sid, vencida) in previas)
                {
                    var motivo = vencida ? "VENCIDA" : "EXPIRADA";
                    await using (var upd = conn.CreateCommand())
                    {
                        upd.Transaction = tx;
                        upd.CommandText = """
                            UPDATE usuario_sesion
                            SET f_fin = SYSDATETIME(), activa = 0, motivo_fin = @m, _updated_at = SYSDATETIME()
                            WHERE id_usuario = @id AND activa = 1
                              AND (session_id = @sid OR (@sid IS NULL AND session_id IS NULL))
                            """;
                        upd.Parameters.Add(new SqlParameter("@m", motivo));
                        upd.Parameters.Add(new SqlParameter("@id", idUsuario));
                        upd.Parameters.Add(new SqlParameter("@sid", (object?)sid ?? DBNull.Value));
                        await upd.ExecuteNonQueryAsync();
                    }
                    await LogEventoAsync(conn, tx, sid, motivo, idUsuario, usuario, password, nivel, acceso, ip, hostname, null);
                }

                // 3) INSERT de la sesión nueva (id no identity → MAX(id)+1).
                int nuevoId;
                await using (var mx = conn.CreateCommand())
                {
                    mx.Transaction = tx;
                    mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM usuario_sesion";
                    nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
                }
                await using (var ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT INTO usuario_sesion
                            (id, id_usuario, usuario, password, nivel, acceso,
                             f_inicio, f_fin, activa, ip, hostname, terminal,
                             puerto, puerto_env, motivo_fin, session_id,
                             _deleted, _created_at, _updated_at)
                        VALUES
                            (@id, @idu, @usuario, @password, @nivel, @acceso,
                             SYSDATETIME(), NULL, 1, @ip, @hostname, 0,
                             NULL, NULL, NULL, @sid, 0, SYSDATETIME(), SYSDATETIME())
                        """;
                    ins.Parameters.Add(new SqlParameter("@id", nuevoId));
                    ins.Parameters.Add(new SqlParameter("@idu", idUsuario));
                    ins.Parameters.Add(new SqlParameter("@usuario", usuario));
                    ins.Parameters.Add(new SqlParameter("@password", Nz(password)));
                    ins.Parameters.Add(new SqlParameter("@nivel", Nz(nivel)));
                    ins.Parameters.Add(new SqlParameter("@acceso", Nz(acceso)));
                    ins.Parameters.Add(new SqlParameter("@ip", Nz(ip)));
                    ins.Parameters.Add(new SqlParameter("@hostname", Nz(hostname)));
                    ins.Parameters.Add(new SqlParameter("@sid", sessionId));
                    await ins.ExecuteNonQueryAsync();
                }

                // 4) Evento LOGIN en la bitácora.
                await LogEventoAsync(conn, tx, sessionId, "LOGIN", idUsuario, usuario, password, nivel, acceso, ip, hostname, null);

                await tx.CommitAsync();
                _reports.InvalidarCacheAbm();
                return sessionId;
            }
            catch { await tx.RollbackAsync(); return null; }
        }
        catch { return null; }
    }

    /// <summary>
    /// Cierra la sesión activa de un usuario (logout): UPDATE de la fila abierta
    /// (<c>f_fin</c>, <c>activa=0</c>, <c>motivo_fin='LOGOUT'</c>) + evento LOGOUT en la bitácora.
    /// Usa el <c>session_id</c> de la cookie para cerrar exactamente esa sesión; si no viene,
    /// cierra todas las abiertas del usuario. No tira si algo falla.
    /// </summary>
    public async Task RegistrarLogoutAsync(string usuario, Guid? sessionId = null)
    {
        usuario = (usuario ?? "").Trim();
        if (string.IsNullOrWhiteSpace(usuario)) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = (SqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            try
            {
                // Datos del usuario para la copia en el evento.
                int? idUsuario = null; string password = "", nivel = "", acceso = "";
                await using (var q = conn.CreateCommand())
                {
                    q.Transaction = tx;
                    q.CommandText = """
                        SELECT TOP 1 id, RTRIM(ISNULL(password,'')), RTRIM(ISNULL(nivel,'')), RTRIM(ISNULL(acceso,''))
                        FROM usuario WHERE usuario = @u AND _deleted = 0
                        """;
                    q.Parameters.Add(new SqlParameter("@u", usuario));
                    await using var rd = await q.ExecuteReaderAsync();
                    if (await rd.ReadAsync())
                    { idUsuario = rd.GetInt32(0); password = rd.GetString(1); nivel = rd.GetString(2); acceso = rd.GetString(3); }
                }

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        UPDATE s
                        SET s.f_fin = SYSDATETIME(), s.activa = 0,
                            s.motivo_fin = 'LOGOUT', s._updated_at = SYSDATETIME()
                        FROM usuario_sesion s
                        INNER JOIN usuario u ON u.id = s.id_usuario
                        WHERE u.usuario = @u AND s.activa = 1
                          AND (@sid IS NULL OR s.session_id = @sid)
                        """;
                    cmd.Parameters.Add(new SqlParameter("@u", usuario));
                    cmd.Parameters.Add(new SqlParameter("@sid", (object?)sessionId ?? DBNull.Value));
                    await cmd.ExecuteNonQueryAsync();
                }

                await LogEventoAsync(conn, tx, sessionId, "LOGOUT", idUsuario, usuario, password, nivel, acceso, null, null, null);

                await tx.CommitAsync();
                _reports.InvalidarCacheAbm();
            }
            catch { await tx.RollbackAsync(); }
        }
        catch { /* el logout nunca falla por el registro */ }
    }

    /// <summary>
    /// Cierra la sesión de un usuario que CERRÓ EL NAVEGADOR (lo detecta
    /// <see cref="SesionCircuitoTracker"/> cuando cae el último circuito y no vuelve):
    /// <c>f_fin</c>, <c>activa=0</c>, <c>motivo_fin='DESCONECTADO'</c> + evento DESCONECTADO.
    ///
    /// Solo actúa si la sesión sigue <c>activa=1</c>: si el usuario ya había apretado Cerrar
    /// sesión (o el barrido la venció), el UPDATE no toca ninguna fila y NO se registra el
    /// evento — así nunca aparecen dos egresos para la misma sesión.
    /// Devuelve true si efectivamente cerró la sesión. No tira si algo falla.
    /// </summary>
    public async Task<bool> RegistrarCierrePorNavegadorAsync(string usuario, Guid sessionId)
    {
        usuario = (usuario ?? "").Trim();
        if (string.IsNullOrWhiteSpace(usuario) || sessionId == Guid.Empty) return false;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = (SqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            try
            {
                int filas;
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        UPDATE usuario_sesion
                        SET f_fin = SYSDATETIME(), activa = 0,
                            motivo_fin = 'DESCONECTADO', _updated_at = SYSDATETIME()
                        WHERE session_id = @sid AND activa = 1
                        """;
                    cmd.Parameters.Add(new SqlParameter("@sid", sessionId));
                    filas = await cmd.ExecuteNonQueryAsync();
                }

                if (filas == 0) { await tx.RollbackAsync(); return false; }

                // Datos del usuario para la copia autocontenida del evento.
                int? idUsuario = null; string password = "", nivel = "", acceso = "";
                await using (var q = conn.CreateCommand())
                {
                    q.Transaction = tx;
                    q.CommandText = """
                        SELECT TOP 1 id, RTRIM(ISNULL(password,'')), RTRIM(ISNULL(nivel,'')), RTRIM(ISNULL(acceso,''))
                        FROM usuario WHERE usuario = @u AND _deleted = 0
                        """;
                    q.Parameters.Add(new SqlParameter("@u", usuario));
                    await using var rd = await q.ExecuteReaderAsync();
                    if (await rd.ReadAsync())
                    { idUsuario = rd.GetInt32(0); password = rd.GetString(1); nivel = rd.GetString(2); acceso = rd.GetString(3); }
                }

                await LogEventoAsync(conn, tx, sessionId, "DESCONECTADO", idUsuario, usuario,
                    password, nivel, acceso, null, null, "Navegador cerrado / conexión perdida");

                await tx.CommitAsync();
                _reports.InvalidarCacheAbm();
                return true;
            }
            catch { await tx.RollbackAsync(); return false; }
        }
        catch { return false; }
    }

    /// <summary>
    /// Registra un intento de login RECHAZADO (LOGIN_FALLIDO) en la bitácora, con el motivo del
    /// rechazo (ej. "Contraseña incorrecta"). No hay sesión (session_id NULL). Copia lo que se
    /// sepa del usuario si existe. No tira si falla.
    /// </summary>
    public async Task RegistrarLoginFallidoAsync(string usuario, string? ip, string? hostname, string motivo)
    {
        usuario = (usuario ?? "").Trim();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = (SqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();

            // Si el usuario existe, copiar sus datos (aunque el intento haya fallado por password).
            int? idUsuario = null; string password = "", nivel = "", acceso = "";
            await using (var q = conn.CreateCommand())
            {
                q.CommandText = """
                    SELECT TOP 1 id, RTRIM(ISNULL(password,'')), RTRIM(ISNULL(nivel,'')), RTRIM(ISNULL(acceso,''))
                    FROM usuario WHERE usuario = @u AND _deleted = 0
                    """;
                q.Parameters.Add(new SqlParameter("@u", usuario));
                await using var rd = await q.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                { idUsuario = rd.GetInt32(0); password = rd.GetString(1); nivel = rd.GetString(2); acceso = rd.GetString(3); }
            }

            await LogEventoAsync(conn, null, null, "LOGIN_FALLIDO", idUsuario,
                string.IsNullOrWhiteSpace(usuario) ? "(vacío)" : usuario,
                password, nivel, acceso, ip, hostname, motivo);
            _reports.InvalidarCacheAbm();
        }
        catch { /* no romper el flujo de login por el registro */ }
    }

    /// <summary>Inserta una fila de evento en <c>usuarios_logs</c> (id no identity → MAX(id)+1).
    /// Usa la conexión/transacción que se le pase (para ir dentro del login/logout) o abre su
    /// propia si tx es null (login fallido).</summary>
    private static async Task LogEventoAsync(
        SqlConnection conn, SqlTransaction? tx, Guid? sessionId, string evento,
        int? idUsuario, string usuario, string? password, string? nivel, string? acceso,
        string? ip, string? hostname, string? motivo)
    {
        int nuevoId;
        await using (var mx = conn.CreateCommand())
        {
            if (tx is not null) mx.Transaction = tx;
            mx.CommandText = "SELECT ISNULL(MAX(id), 0) + 1 FROM usuarios_logs";
            nuevoId = (int)(await mx.ExecuteScalarAsync() ?? 1);
        }
        await using var ins = conn.CreateCommand();
        if (tx is not null) ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO usuarios_logs
                (id, session_id, evento, f_evento, id_usuario, usuario, password, nivel, acceso,
                 ip, hostname, motivo, _deleted, _created_at, _updated_at)
            VALUES
                (@id, @sid, @ev, SYSDATETIME(), @idu, @usuario, @password, @nivel, @acceso,
                 @ip, @hostname, @motivo, 0, SYSDATETIME(), SYSDATETIME())
            """;
        ins.Parameters.Add(new SqlParameter("@id", nuevoId));
        ins.Parameters.Add(new SqlParameter("@sid", (object?)sessionId ?? DBNull.Value));
        ins.Parameters.Add(new SqlParameter("@ev", evento));
        ins.Parameters.Add(new SqlParameter("@idu", (object?)idUsuario ?? DBNull.Value));
        ins.Parameters.Add(new SqlParameter("@usuario", string.IsNullOrWhiteSpace(usuario) ? "(vacío)" : usuario.Trim()));
        ins.Parameters.Add(new SqlParameter("@password", Nz(password)));
        ins.Parameters.Add(new SqlParameter("@nivel", Nz(nivel)));
        ins.Parameters.Add(new SqlParameter("@acceso", Nz(acceso)));
        ins.Parameters.Add(new SqlParameter("@ip", Nz(ip)));
        ins.Parameters.Add(new SqlParameter("@hostname", Nz(hostname)));
        ins.Parameters.Add(new SqlParameter("@motivo", Nz(motivo)));
        await ins.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Barrido de sesiones vencidas: cierra en <c>usuario_sesion</c> las que siguen
    /// <c>activa=1</c> pero superaron las 8 hs desde <c>f_inicio</c>, y registra un evento
    /// VENCIDA por cada una. Lo llama un IHostedService periódico. Devuelve cuántas cerró.
    /// </summary>
    public async Task<int> CerrarSesionesVencidasAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = (SqlConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();

            // ¿Existen las tablas? (server sin migrar → no hacer nada).
            await using (var chk = conn.CreateCommand())
            {
                chk.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE table_name IN ('usuario_sesion','usuarios_logs')";
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) < 2) return 0;
            }

            // Listar las vencidas para loguear su evento antes de cerrarlas.
            var vencidas = new List<(int idu, string usuario, Guid? sid, string password, string nivel, string acceso, string? ip, string? host)>();
            await using (var sel = conn.CreateCommand())
            {
                sel.CommandText = """
                    SELECT id_usuario, usuario, session_id,
                           RTRIM(ISNULL(password,'')), RTRIM(ISNULL(nivel,'')), RTRIM(ISNULL(acceso,'')),
                           ip, hostname
                    FROM usuario_sesion
                    WHERE activa = 1 AND DATEDIFF(second, f_inicio, SYSDATETIME()) > @seg
                    """;
                sel.Parameters.Add(new SqlParameter("@seg", (int)DuracionSesion.TotalSeconds));
                await using var rd = await sel.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    vencidas.Add((rd.GetInt32(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetGuid(2),
                        rd.GetString(3), rd.GetString(4), rd.GetString(5),
                        rd.IsDBNull(6) ? null : rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetString(7)));
            }
            if (vencidas.Count == 0) return 0;

            await using (var upd = conn.CreateCommand())
            {
                upd.CommandText = """
                    UPDATE usuario_sesion
                    SET f_fin = SYSDATETIME(), activa = 0, motivo_fin = 'VENCIDA', _updated_at = SYSDATETIME()
                    WHERE activa = 1 AND DATEDIFF(second, f_inicio, SYSDATETIME()) > @seg
                    """;
                upd.Parameters.Add(new SqlParameter("@seg", (int)DuracionSesion.TotalSeconds));
                await upd.ExecuteNonQueryAsync();
            }
            foreach (var v in vencidas)
                await LogEventoAsync(conn, null, v.sid, "VENCIDA", v.idu, v.usuario, v.password, v.nivel, v.acceso, v.ip, v.host, null);

            _reports.InvalidarCacheAbm();
            return vencidas.Count;
        }
        catch { return 0; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Menú contextual del panel BUSES — escritura sobre la UNIDAD
    // Plano: docs/PlanoFoxPro/trafico/TRAFICO_BUSES_MENU.md
    //
    // ⚠️ ANDAMIAJE. `vehiculo` es tabla del circuito viaje (la pisa la asignación de
    // Tráfico y la réplica DBF→SQL), así que los tres métodos de acá abortan por flag
    // hasta el DÍA D. El código está escrito completo y fiel al FoxPro a propósito:
    // se prueba contra el server local y se enciende con el resto del circuito.
    //
    // Mejora obligatoria sobre el original: el FoxPro NO usa transacciones en ninguna de
    // estas operaciones (hace el UPDATE de `vehiculo` y el INSERT del log sueltos). Acá
    // van en una transacción única, como manda la regla del proyecto.
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Logonear</b> un conductor en una unidad — réplica de <c>trafico_logonear.scx</c> (botón Graba).
    /// </summary>
    /// <param name="idVehiculoRow">PK física <c>vehiculo.id</c> (el FoxPro hace <c>Where Id = nId</c>).</param>
    /// <param name="segundoConductor">false = conductor PRINCIPAL, true = ACOMPAÑANTE.</param>
    /// <param name="zona">Zona en la que queda la unidad (solo la escribe el 1º conductor).</param>
    /// <param name="hora">Fecha y hora del movimiento — en el form son EDITABLES, no es GETDATE() forzado.</param>
    /// <remarks>
    /// El FoxPro escribe distinto según el conductor:
    /// <code>
    /// primero → Update vehiculo Set id_chofer=, nombre_chofer=, franco=, id_zona=  Where Id = nId
    /// segundo → Update vehiculo Set id_chofer2=                                    Where Id = nId
    /// </code>
    /// (el 2º conductor NO toca zona ni el flag de franco), y en los dos casos inserta una fila
    /// en <c>viaje_log_chofer</c> con <c>operacion = 'LOGONEO'</c>.
    ///
    /// ⛔ <b><c>viaje_log_chofer</c> no está replicada en SQL</b> (75.001 filas en el DBF).
    /// Mientras falte, el INSERT del log se saltea y se deja constancia en el resultado: el
    /// UPDATE de <c>vehiculo</c> sin su bitácora sería una pérdida de auditoría, así que al
    /// activar el flag hay que tener la tabla replicada. Ver el plano.
    /// </remarks>
    public async Task<AbmResult> LogonearAsync(
        int idVehiculoRow, string idChofer, string nombreChofer, bool segundoConductor,
        string zona, bool tieneFranco, DateTime hora, string usuario)
    {
        if (!AbmFeatureFlags.LogoneoAbmActivo)
            return AbmResult.Fallo("El logoneo de conductores se habilita con el circuito de Tráfico (día D).");
        if (idVehiculoRow <= 0) return AbmResult.Fallo("No se identificó la unidad.");
        if (string.IsNullOrWhiteSpace(idChofer)) return AbmResult.Fallo("Elegí el conductor a logonear.");
        if (!segundoConductor && string.IsNullOrWhiteSpace(zona))
            return AbmResult.Fallo("Cargá la zona de la unidad.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Anti-doble-logoneo: se relee la unidad DENTRO de la transacción con UPDLOCK. El
            // FoxPro valida contra el cursor en memoria, que puede estar viejo si otro operador
            // logoneó primero (mismo criterio que el anti-doble-asignación del circuito).
            string choferActual, chofer2Actual, estado, idVehiculo;
            await using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    SELECT TOP 1
                        RTRIM(ISNULL(id_chofer, '')), RTRIM(ISNULL(id_chofer2, '')),
                        RTRIM(ISNULL(estado, '')),   RTRIM(ISNULL(id_vehicul, ''))
                    FROM vehiculo WITH (UPDLOCK) WHERE id = @id AND _deleted = 0
                    """;
                q.Parameters.Add(new SqlParameter("@id", idVehiculoRow));
                await using var rd = await q.ExecuteReaderAsync();
                if (!await rd.ReadAsync()) { await tx.RollbackAsync(); return AbmResult.Fallo("La unidad ya no existe."); }
                choferActual = rd.GetString(0); chofer2Actual = rd.GetString(1);
                estado = rd.GetString(2); idVehiculo = rd.GetString(3);
            }

            // Las guardas del menú + logonea_conductor(), revalidadas sobre el dato fresco.
            if (estado == "TALLER")
                { await tx.RollbackAsync(); return AbmResult.Fallo("La unidad se encuentra fuera de servicio (TALLER)."); }
            if (estado != "LIBERADO")
                { await tx.RollbackAsync(); return AbmResult.Fallo("La unidad se encuentra en servicio."); }
            if (!segundoConductor && choferActual.Length > 0)
                { await tx.RollbackAsync(); return AbmResult.Fallo($"Esa unidad ya tiene asignado un conductor ({choferActual})."); }
            if (segundoConductor && choferActual.Length == 0)
                { await tx.RollbackAsync(); return AbmResult.Fallo("No tiene asignado el primer conductor."); }
            if (segundoConductor && chofer2Actual.Length > 0)
                { await tx.RollbackAsync(); return AbmResult.Fallo("Esa unidad ya tiene un 2º conductor."); }

            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = segundoConductor
                    ? "UPDATE vehiculo SET id_chofer2 = @cho WHERE id = @id"
                    : """
                      UPDATE vehiculo
                         SET id_chofer = @cho, nombre_cho = @nom, franco = @fra, id_zona = @zona
                       WHERE id = @id
                      """;
                upd.Parameters.Add(new SqlParameter("@cho", idChofer.Trim()));
                upd.Parameters.Add(new SqlParameter("@id", idVehiculoRow));
                if (!segundoConductor)
                {
                    upd.Parameters.Add(new SqlParameter("@nom", nombreChofer ?? ""));
                    upd.Parameters.Add(new SqlParameter("@fra", tieneFranco));
                    upd.Parameters.Add(new SqlParameter("@zona", zona.Trim()));
                }
                await upd.ExecuteNonQueryAsync();
            }

            var logueado = await LogChoferAsync(conn, tx, "LOGONEO", idChofer, idVehiculo, tieneFranco,
                                                zona, segundoConductor, hora, usuario);

            await tx.CommitAsync();
            _reports.InvalidarCacheTrafico(DateOnly.FromDateTime(DateTime.Today));
            return logueado
                ? AbmResult.Exito(idVehiculoRow)
                : AbmResult.Exito(idVehiculoRow, "Logoneo grabado, pero SIN bitácora: falta replicar viaje_log_chofer.");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo("No se pudo logonear: " + ex.Message);
        }
    }

    /// <summary>
    /// <b>DesLogonear</b> un conductor — réplica de <c>trafico_deslogonear.scx</c> (botón Graba).
    /// </summary>
    /// <param name="zonaNueva">Zona de DETENCIÓN, obligatoria en el form. Solo la escribe el 1º.</param>
    /// <remarks>
    /// <code>
    /// primero → Update vehiculo Set id_chofer = "", franco = .F., id_zona = cZona_New Where Id = nId
    /// segundo → Update vehiculo Set id_chofer2 = ""                                   Where Id = nId
    /// </code>
    /// 🔴 Detalle no obvio: en el log de DESLOGONEO el FoxPro graba <c>zona = cZona</c>, la zona
    /// <b>VIEJA</b>, mientras que <c>vehiculo.id_zona</c> queda con la nueva. Se respeta.
    /// </remarks>
    public async Task<AbmResult> DeslogonearAsync(
        int idVehiculoRow, bool segundoConductor, string zonaNueva, DateTime hora, string usuario)
    {
        if (!AbmFeatureFlags.LogoneoAbmActivo)
            return AbmResult.Fallo("El deslogoneo de conductores se habilita con el circuito de Tráfico (día D).");
        if (idVehiculoRow <= 0) return AbmResult.Fallo("No se identificó la unidad.");
        if (!segundoConductor && string.IsNullOrWhiteSpace(zonaNueva))
            return AbmResult.Fallo("No se cargó la zona de detención.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            string choferActual, chofer2Actual, estado, idVehiculo, zonaVieja;
            long idViaje;
            await using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    SELECT TOP 1
                        RTRIM(ISNULL(id_chofer, '')), RTRIM(ISNULL(id_chofer2, '')),
                        RTRIM(ISNULL(estado, '')),   RTRIM(ISNULL(id_vehicul, '')),
                        RTRIM(ISNULL(id_zona, '')),  CAST(ISNULL(id_viaje, 0) AS bigint)
                    FROM vehiculo WITH (UPDLOCK) WHERE id = @id AND _deleted = 0
                    """;
                q.Parameters.Add(new SqlParameter("@id", idVehiculoRow));
                await using var rd = await q.ExecuteReaderAsync();
                if (!await rd.ReadAsync()) { await tx.RollbackAsync(); return AbmResult.Fallo("La unidad ya no existe."); }
                choferActual = rd.GetString(0); chofer2Actual = rd.GetString(1);
                estado = rd.GetString(2); idVehiculo = rd.GetString(3);
                zonaVieja = rd.GetString(4); idViaje = rd.GetInt64(5);
            }

            // Las 5 guardas de deslogonea_conductor() + la del menú (bar 11).
            if (estado == "TALLER")
                { await tx.RollbackAsync(); return AbmResult.Fallo("La unidad se encuentra fuera de servicio (TALLER)."); }
            if (estado == "GUARDIA")
                { await tx.RollbackAsync(); return AbmResult.Fallo("La unidad se encuentra en GUARDIA. Hay que liberarla."); }
            if (!segundoConductor && chofer2Actual.Length > 0)
                { await tx.RollbackAsync(); return AbmResult.Fallo("Deslogoneá primero al 2º conductor."); }
            if (!segundoConductor && choferActual.Length == 0)
                { await tx.RollbackAsync(); return AbmResult.Fallo("La unidad se encuentra sin logonear."); }
            if (segundoConductor && chofer2Actual.Length == 0)
                { await tx.RollbackAsync(); return AbmResult.Fallo("La unidad no tiene 2º conductor logoneado."); }
            if (idViaje != 0)
                { await tx.RollbackAsync(); return AbmResult.Fallo("La unidad se encuentra realizando un viaje."); }

            var choferSaliente = segundoConductor ? chofer2Actual : choferActual;

            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = segundoConductor
                    ? "UPDATE vehiculo SET id_chofer2 = '' WHERE id = @id"
                    : "UPDATE vehiculo SET id_chofer = '', nombre_cho = '', franco = 0, id_zona = @zona WHERE id = @id";
                upd.Parameters.Add(new SqlParameter("@id", idVehiculoRow));
                if (!segundoConductor) upd.Parameters.Add(new SqlParameter("@zona", zonaNueva.Trim()));
                await upd.ExecuteNonQueryAsync();
            }

            // zonaVieja, no la nueva — es lo que hace el FoxPro (ver remarks).
            var logueado = await LogChoferAsync(conn, tx, "DESLOGONEO", choferSaliente, idVehiculo, false,
                                                zonaVieja, segundoConductor, hora, usuario);

            await tx.CommitAsync();
            _reports.InvalidarCacheTrafico(DateOnly.FromDateTime(DateTime.Today));
            return logueado
                ? AbmResult.Exito(idVehiculoRow)
                : AbmResult.Exito(idVehiculoRow, "Deslogoneo grabado, pero SIN bitácora: falta replicar viaje_log_chofer.");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo("No se pudo deslogonear: " + ex.Message);
        }
    }

    /// <summary>
    /// INSERT en <c>viaje_log_chofer</c> (la bitácora de logoneo). Devuelve <c>false</c> —sin
    /// romper la transacción— si la tabla todavía no está replicada en SQL.
    /// </summary>
    private static async Task<bool> LogChoferAsync(
        SqlConnection conn, SqlTransaction tx, string operacion, string idChofer, string idVehiculo,
        bool franco, string zona, bool segundoConductor, DateTime hora, string usuario)
    {
        await using (var chk = conn.CreateCommand())
        {
            chk.Transaction = tx;
            chk.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = 'viaje_log_chofer'";
            if ((int)(await chk.ExecuteScalarAsync() ?? 0) == 0) return false;
        }

        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        // Nombres SQL previstos según el DBF (id_vehiculo → id_vehicul, tipo_chofer → tipo_chofe,
        // truncados a 10 chars por la réplica). Verificar contra sys.columns cuando exista.
        ins.CommandText = """
            INSERT INTO viaje_log_chofer
                (id_chofer, id_vehicul, franco, interno, fecha, zona, usuario, hora, operacion, tipo_chofe)
            SELECT @cho, @veh, @fra, ISNULL(v.interno, 0), CAST(@hora AS date), @zona, @usr, @hora, @op, @tipo
            FROM vehiculo v WHERE v.id_vehicul = @veh AND v._deleted = 0
            """;
        ins.Parameters.Add(new SqlParameter("@cho", idChofer ?? ""));
        ins.Parameters.Add(new SqlParameter("@veh", idVehiculo ?? ""));
        ins.Parameters.Add(new SqlParameter("@fra", franco));
        ins.Parameters.Add(new SqlParameter("@zona", zona ?? ""));
        ins.Parameters.Add(new SqlParameter("@usr", usuario ?? ""));
        ins.Parameters.Add(new SqlParameter("@hora", hora));
        ins.Parameters.Add(new SqlParameter("@op", operacion));
        // tipo_chofer del FoxPro: el texto del cuadro, no un código.
        ins.Parameters.Add(new SqlParameter("@tipo", segundoConductor ? "ACOMPAÑANTE" : "PRINCIPAL"));
        await ins.ExecuteNonQueryAsync();
        return true;
    }

    /// <summary>
    /// <b>Toma Franco</b> — la única escritura INLINE del menú de Buses (bar 18 del .mpr).
    /// Da de alta el franco de HOY del conductor logoneado en la unidad.
    /// </summary>
    /// <remarks>
    /// El FoxPro no ofrece elegir nada: fecha = <c>Date()</c>, <c>codigo = 'F'</c>,
    /// <c>motivo = 'FRANCO'</c>, <c>trabajo = .F.</c>, <c>valido = .T.</c>. Antes chequea que
    /// el chofer no tenga ya un franco ese día ("Ese Franco ya esta cargado en ese chofer").
    ///
    /// ⚠️ Se agrega una validación que el original NO tiene: si la unidad no está logoneada,
    /// <c>id_chofer</c> viene vacío y el FoxPro insertaría un franco con <c>id_chofer = ''</c>.
    /// Acá se rechaza — es un bug del original, no una regla de negocio.
    ///
    /// <c>chofer_franco</c> NO es del circuito viaje (es autocontenida y ya tiene su ABM en
    /// <c>/francos</c>), pero se deja apagada por consistencia con el resto del menú
    /// (decisión del usuario, 04/08/2026).
    /// </remarks>
    public async Task<AbmResult> TomarFrancoAsync(string idChofer, string usuario)
    {
        if (!AbmFeatureFlags.TomaFrancoActivo)
            return AbmResult.Fallo("La toma de franco desde Tráfico se habilita con el circuito (día D).");
        if (string.IsNullOrWhiteSpace(idChofer))
            return AbmResult.Fallo("La unidad no tiene conductor logoneado — no hay a quién darle el franco.");

        var hoy = DateOnly.FromDateTime(DateTime.Today);
        // Reusa el alta masiva de Francos, que ya hace el chequeo de duplicado por chofer+fecha,
        // el MAX(id)+1 y la transacción. Un chofer, una fecha, con el código/motivo fijos del menú.
        var r = await AltaFrancosAsync(new[] { idChofer.Trim() }, new[] { hoy }, "F", "FRANCO");
        if (r.Ok) _reports.InvalidarCacheTrafico(hoy);
        return r;
    }

    /// <summary>
    /// <b>Liberar unidad</b> (bar 22) — réplica de <c>trafico_vehiculo_libera.scx</c>.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>El nombre del ítem miente.</b> "pasa a Sin Asignar" sugiere que cambia
    /// <c>viaje.estado_via</c>, pero en el fuente TODO el bloque que tocaba <c>viaje</c>,
    /// <c>viaje_log</c> y <c>vehiculo_km</c> está COMENTADO (<c>*!*</c>). Lo único vivo es:
    /// <code>Update vehiculo Set estado = "LIBERADO", hs_inicio = {//::}, id_viaje = 0 Where Id = nId</code>
    /// Es una liberación de emergencia de la UNIDAD (la despega del viaje para poder
    /// reasignarla); el viaje queda como estaba. Se replica fiel — corregirlo sería cambiar
    /// el comportamiento del sistema, no migrarlo.
    ///
    /// No confundir con "Libe" de la toolbar (= FINALIZAR el viaje) ni con el "Sin Asignar"
    /// del Zoom (ese sí revierte el estado del viaje).
    ///
    /// El FoxPro busca la unidad por <c>cronograma</c> y aborta si no encuentra exactamente 1
    /// fila ("se encontro un problema con los vehiculos"). Se respeta esa guarda.
    /// </remarks>
    public async Task<AbmResult> LiberarUnidadAsync(string cronograma, string usuario)
    {
        if (!AbmFeatureFlags.LiberarUnidadActivo)
            return AbmResult.Fallo("La liberación de unidades se habilita con el circuito de Tráfico (día D).");
        if (string.IsNullOrWhiteSpace(cronograma))
            return AbmResult.Fallo("La unidad no tiene cronograma — no se puede identificar.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            int id; string estado;
            await using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                // La guarda del FoxPro es "_Tally = 1": si el cronograma matchea 0 o >1 unidades
                // activas, aborta. TOP 2 alcanza para distinguir los tres casos.
                q.CommandText = """
                    SELECT TOP 2 id, RTRIM(ISNULL(estado, ''))
                    FROM vehiculo WITH (UPDLOCK)
                    WHERE cronograma = @cro AND activo = 1 AND _deleted = 0
                    """;
                q.Parameters.Add(new SqlParameter("@cro", cronograma.Trim()));
                await using var rd = await q.ExecuteReaderAsync();
                if (!await rd.ReadAsync())
                    { await tx.RollbackAsync(); return AbmResult.Fallo("Se encontró un problema con los vehículos: ninguna unidad activa con ese cronograma."); }
                id = rd.GetInt32(0); estado = rd.GetString(1);
                if (await rd.ReadAsync())
                    { await tx.RollbackAsync(); return AbmResult.Fallo("Se encontró un problema con los vehículos: hay más de una unidad activa con ese cronograma."); }
            }

            // El form no hace nada si la unidad ya está liberada (If estado # "LIBERADO").
            if (estado == "LIBERADO")
                { await tx.RollbackAsync(); return AbmResult.Fallo("La unidad ya está LIBERADA."); }

            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                // hs_inicio = {//::} del FoxPro = datetime vacío → NULL en SQL.
                upd.CommandText = "UPDATE vehiculo SET estado = 'LIBERADO', hs_inicio = NULL, id_viaje = 0 WHERE id = @id";
                upd.Parameters.Add(new SqlParameter("@id", id));
                await upd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheTrafico(DateOnly.FromDateTime(DateTime.Today));
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo("No se pudo liberar la unidad: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SISTEMA — Parámetros Empresa y Generales (parametro_empresa.scx + parametro.scx)
    //  Plano: docs/PlanoFoxPro/sistema/PARAMETROS.md · skill modulo-sistema.
    //
    //  🔴 REGLA DE ORO: se escriben SOLO las columnas de la pantalla, una por una, con
    //     SqlParameter. NUNCA una reescritura de fila completa: en la MISMA fila viven
    //     los contadores vivos del circuito (id_viaje_i, lote_plant, lote_sobre,
    //     stock_movi) que FoxPro incrementa todo el día.
    //  El UPDATE va sin WHERE, igual que el FoxPro (la tabla tiene 1 sola fila).
    //
    //  Correcciones sobre el FoxPro (decisión 12/08/2026, §3.4 del plano):
    //   ① `aviso_mat` SÍ se graba (en el FoxPro se edita y se pierde).
    //   ② `dir_mdb` e `intranet` NO se tocan (el FoxPro los blanquea en cada Aceptar
    //      porque los escribe sin haberlos cargado).
    //   ③ `lote_plant` y `lote_sobre` NO se graban (contadores vivos: solo lectura).
    //   ④ El interruptor del GPS (`xml_envia`/`dir_xml`) y las rutas de red del FoxPro
    //      tampoco se graban — son solo lectura hasta la decisión de Fase 0.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validación de CUIT del FoxPro (<c>_ValidaCUIT</c>, funcion.prg:339 + el Valid del
    /// textbox): exige la máscara de 13 caracteres <c>30-12345678-1</c> y verifica el dígito
    /// verificador módulo 11 con los pesos 5,4,3,2,7,6,5,4,3,2. Vacío se considera VÁLIDO
    /// (igual que el FoxPro). Devuelve <c>null</c> si está bien, o el mensaje de error.
    /// </summary>
    public static string? ValidarCuit(string? cuit)
    {
        cuit = (cuit ?? "").Trim();
        if (cuit.Length == 0) return null;

        // El FoxPro admite 11 dígitos sin máscara y la arma; el Valid del form igual exige 13.
        if (cuit.Length == 11 && cuit.All(char.IsDigit))
            cuit = $"{cuit[..2]}-{cuit.Substring(2, 8)}-{cuit[10]}";

        if (cuit.Length != 13)
            return "Problemas en la carga de CUIT. Ej.: 30-12345678-1";

        // Posiciones (1-based) 1,2,4..11,13 son dígitos; 3 y 12 son los guiones.
        if (cuit[2] != '-' || cuit[11] != '-')
            return "Problemas en la carga de CUIT. Ej.: 30-12345678-1";
        for (int i = 0; i < 13; i++)
            if (i != 2 && i != 11 && !char.IsDigit(cuit[i]))
                return "Problemas en la carga de CUIT. Ej.: 30-12345678-1";

        // Suma ponderada tal cual el FoxPro (índices 1-based sobre la cadena CON máscara).
        int D(int pos1Based) => cuit[pos1Based - 1] - '0';
        var suma = D(11) * 2 + D(10) * 3 + D(9) * 4 + D(8) * 5 + D(7) * 6
                 + D(6) * 7 + D(5) * 2 + D(4) * 3 + D(2) * 4 + D(1) * 5;
        var resto = suma % 11;
        var verificador = resto == 0 ? 0 : 11 - resto;

        return D(13) == verificador
            ? null
            : "No se cargó correctamente el Nro. de CUIT. Intente nuevamente.";
    }

    /// <summary>
    /// Graba los 15 campos de Parámetros Empresa. Valida el CUIT (única validación del form
    /// FoxPro) y fuerza MAYÚSCULAS en Nombre y Dirección (los dos controles con <c>Format="!"</c>).
    /// ⛔ Deshabilitado por <see cref="AbmFeatureFlags.ParametrosAbmActivo"/> hasta el día D:
    /// hasta entonces la réplica DBF→SQL pisaría lo que escriba Buslink.
    /// </summary>
    public async Task<AbmResult> GrabarParametrosEmpresaAsync(ParametrosEmpresaEdit p)
    {
        if (!AbmFeatureFlags.ParametrosAbmActivo)
            return AbmResult.Fallo(
                "La edición de Parámetros todavía no está habilitada en Buslink. " +
                "La tabla `parametro` sigue siendo del Metrocar (FoxPro) hasta el día D.");

        var errCuit = ValidarCuit(p.Cuit);
        if (errCuit is not null) return AbmResult.Fallo(errCuit);

        if (p.SmtpPuerto is < 0 or > 65535)
            return AbmResult.Fallo("El puerto de correo debe estar entre 0 y 65535.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // Solo las 15 columnas de esta pantalla. Sin WHERE: la tabla tiene 1 fila.
            cmd.CommandText = """
                UPDATE parametro SET
                    empresa_no = @nombre,
                    empresa_di = @direccion,
                    empresa_cu = @cuit,
                    piva       = @piva,
                    empresa_te = @telefono,
                    empresa_ha = @regnac,
                    empresa_vt = @vto,
                    empresa_ci = @circuito,
                    logo       = @logo,
                    smtp_nombr = @smtpNombre,
                    smtp_serve = @smtpServidor,
                    smtp_usuar = @smtpUsuario,
                    smtp_passw = @smtpPassword,
                    smtp_puert = @smtpPuerto,
                    smtp_firma = @smtpFirma
                """;
            cmd.Parameters.Add(new SqlParameter("@nombre", (p.Nombre ?? "").Trim().ToUpperInvariant()));
            cmd.Parameters.Add(new SqlParameter("@direccion", (p.Direccion ?? "").Trim().ToUpperInvariant()));
            cmd.Parameters.Add(new SqlParameter("@cuit", (p.Cuit ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@piva", p.Piva));
            cmd.Parameters.Add(new SqlParameter("@telefono", (p.Telefono ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@regnac", p.RegNac));
            cmd.Parameters.Add(new SqlParameter("@vto", (object?)p.VtoCircuito ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@circuito", (p.CircuitoCerrado ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@logo", (p.Logo ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@smtpNombre", (p.SmtpNombre ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@smtpServidor", (p.SmtpServidor ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@smtpUsuario", (p.SmtpUsuario ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@smtpPassword", p.SmtpPassword ?? ""));
            cmd.Parameters.Add(new SqlParameter("@smtpPuerto", (long)p.SmtpPuerto));
            cmd.Parameters.Add(new SqlParameter("@smtpFirma", p.SmtpFirma ?? ""));
            await cmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            _reports.InvalidarCacheParametros();
            return AbmResult.Exito();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo("No se pudieron grabar los parámetros de la empresa: " + ex.Message);
        }
    }

    /// <summary>
    /// Graba los campos EDITABLES de Parámetros Generales. Valida que el cliente de
    /// movimientos internos exista (Valid de <c>id_cliente_prueba</c>).
    /// Corrige los 3 bugs del fuente FoxPro (ver el bloque de comentario de arriba).
    /// ⛔ Deshabilitado por <see cref="AbmFeatureFlags.ParametrosAbmActivo"/> hasta el día D.
    /// </summary>
    public async Task<AbmResult> GrabarParametrosGeneralesAsync(ParametrosGeneralesEdit p)
    {
        if (!AbmFeatureFlags.ParametrosAbmActivo)
            return AbmResult.Fallo(
                "La edición de Parámetros todavía no está habilitada en Buslink. " +
                "La tabla `parametro` sigue siendo del Metrocar (FoxPro) hasta el día D.");

        if (p.ChequeoHora is < 0 or > 23 || p.ChequeoMinuto is < 0 or > 59)
            return AbmResult.Fallo("La hora de chequeo debe estar entre 00:00 y 23:59.");
        if (p.BackupMinutos < 0)
            return AbmResult.Fallo("El tiempo entre back-ups no puede ser negativo.");
        if (!await _reports.ExisteClienteAsync(p.ClienteMovInternos))
            return AbmResult.Fallo($"Cliente Inexistente: «{p.ClienteMovInternos}».");

        // El FoxPro arma aviso_tiempo como DATETIME(1999,12,1, hh, mm): la fecha es basura,
        // solo se usa la hora. Se respeta el mismo literal para no confundir al FoxPro.
        var avisoTiempo = new DateTime(1999, 12, 1, p.ChequeoHora, p.ChequeoMinuto, 0);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // Único que sigue SIN figurar: dir_mdb / intranet — es el bug ② del FoxPro
            // (los escribía sin haberlos cargado, blanqueándolos en cada Aceptar).
            // Los contadores (lote_plant/lote_sobre), empresa_ca/lista_prec y las rutas SÍ se
            // graban desde el 12/08/2026, por decisión del usuario tras apagar el watcher.
            cmd.CommandText = """
                UPDATE parametro SET
                    empresa_ca = @empresaFact,
                    lista_prec = @listaPrecio,
                    lote_plant = @lotePlant,
                    lote_sobre = @loteSobre,
                    dir_audito = @dirAuditoria,
                    dir_ex_aud = @dirAuditoriaExt,
                    dir_factur = @dirFacturacion,
                    dir_sonido = @dirSonido,
                    backup_dir = @backupDir,
                    cliente_ad = @srvExcedente,
                    chofer_adi = @srvChofer,
                    fraccion_h = @fraccionFact,
                    fraccion_2 = @fraccionChofer,
                    porc_franc = @porcFranco,
                    imp_franco = @impFranco,
                    aviso_cho  = @avisoCho,
                    aviso_veh  = @avisoVeh,
                    aviso_mat  = @avisoMat,
                    aviso_cheq = @avisoCheq,
                    aviso_tiem = @avisoTiem,
                    bruto      = @bruto,
                    hs_extra_b = @hsExtraBus,
                    hs_extra_m = @hsExtraMb,
                    franco_mes = @francoMes,
                    porc_vacio = @porcVacio,
                    dcombsaldo = @dCombSaldo,
                    rubro_comb = @rubroComb,
                    id_cliente = @clienteInt,
                    adic_agua  = @adicAgua,
                    adic_malet = @adicMaleta,
                    ley_liq_1  = @leyLiq1,
                    ley_liq_2  = @leyLiq2,
                    backup_tim = @backupSeg
                """;
            cmd.Parameters.Add(new SqlParameter("@empresaFact", (p.EmpresaFacturacion ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@listaPrecio", (p.ListaPrecioComun ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@lotePlant", p.LotePlantillas));
            cmd.Parameters.Add(new SqlParameter("@loteSobre", p.LoteSobre));
            cmd.Parameters.Add(new SqlParameter("@dirAuditoria", (p.DirAuditoria ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@dirAuditoriaExt", (p.DirAuditoriaExterna ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@dirFacturacion", (p.DirFacturacion ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@dirSonido", (p.DirSonidoTrafico ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@backupDir", (p.BackupDir ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@srvExcedente", (p.SrvHoraExcedente ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@srvChofer", (p.SrvHorasChofer ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@fraccionFact", (long)p.FraccionFacturacion));
            cmd.Parameters.Add(new SqlParameter("@fraccionChofer", (long)p.FraccionChofer));
            cmd.Parameters.Add(new SqlParameter("@porcFranco", p.PorcFrancoTrabajado));
            cmd.Parameters.Add(new SqlParameter("@impFranco", p.ImpFrancoTrabajado));
            cmd.Parameters.Add(new SqlParameter("@avisoCho", (long)p.AvisoChoferes));
            cmd.Parameters.Add(new SqlParameter("@avisoVeh", (long)p.AvisoTecnica));
            cmd.Parameters.Add(new SqlParameter("@avisoMat", (long)p.AvisoMatafuego));   // ① corregido
            cmd.Parameters.Add(new SqlParameter("@avisoCheq", p.AvisosOperadores ? "S" : "N"));
            cmd.Parameters.Add(new SqlParameter("@avisoTiem", avisoTiempo));
            cmd.Parameters.Add(new SqlParameter("@bruto", p.SueldoBruto));
            cmd.Parameters.Add(new SqlParameter("@hsExtraBus", p.HsExtraBus));
            cmd.Parameters.Add(new SqlParameter("@hsExtraMb", p.HsExtraMinibus));
            cmd.Parameters.Add(new SqlParameter("@francoMes", (long)p.FrancosAlMes));
            cmd.Parameters.Add(new SqlParameter("@porcVacio", p.PorcVacio));
            cmd.Parameters.Add(new SqlParameter("@dCombSaldo", (object?)p.FechaSaldoComb ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@rubroComb", (long)p.RubroCombustible));
            cmd.Parameters.Add(new SqlParameter("@clienteInt", (p.ClienteMovInternos ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@adicAgua", (p.AdicionalAgua ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@adicMaleta", (p.AdicionalMaleta ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@leyLiq1", (p.LeyendaLiq1 ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@leyLiq2", (p.LeyendaLiq2 ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@backupSeg", (long)p.BackupMinutos * 60));
            await cmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            _reports.InvalidarCacheParametros();
            return AbmResult.Exito();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo("No se pudieron grabar los parámetros generales: " + ex.Message);
        }
    }

    /// <summary>
    /// Graba la configuración del SQL externo del GPS (<c>parametro_sql_server.scx</c>) y la de
    /// la vía XML. Editable desde el 12/08/2026 por decisión del usuario.
    ///
    /// 🔴 <b>Apagar <c>sql_gps</c> corta el feed de seguimiento de 136 clientes</b> (AEROLINEAS
    /// incluida, 93 % de los viajes) <b>sin que nadie reciba un error</b>: simplemente dejan de
    /// entrar filas en la tabla del proveedor. La pantalla pide confirmación explícita antes de
    /// grabar ese cambio. Ver <c>docs/PlanoFoxPro/trafico/GPS_XLM.md</c>.
    ///
    /// ⚠ <b>No se replica el parseo Maquina/Instancia del FoxPro</b>, que tiene un bug que borra
    /// la dirección cuando el servidor es una IP sin instancia (§4.3 del plano): acá el servidor
    /// se edita como un solo campo.
    /// </summary>
    public async Task<AbmResult> GrabarParametrosGpsAsync(ParametrosGpsEdit p)
    {
        if (!AbmFeatureFlags.ParametrosAbmActivo)
            return AbmResult.Fallo(
                "La edición de Parámetros todavía no está habilitada en Buslink.");

        // Si el envío queda encendido, los datos de conexión tienen que ser utilizables:
        // guardar sql_gps = 1 con el servidor vacío es la receta del fallo silencioso.
        if (p.Activo)
        {
            if (string.IsNullOrWhiteSpace(p.Servidor))
                return AbmResult.Fallo("Con el envío a GPS activo, el servidor no puede quedar vacío.");
            if (string.IsNullOrWhiteSpace(p.Base))
                return AbmResult.Fallo("Con el envío a GPS activo, la base no puede quedar vacía.");
            if (string.IsNullOrWhiteSpace(p.Tabla))
                return AbmResult.Fallo("Con el envío a GPS activo, la tabla destino no puede quedar vacía.");
        }
        var tabla = (p.Tabla ?? "").Trim();
        if (tabla.Length > 0 && !tabla.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return AbmResult.Fallo($"El nombre de tabla «{tabla}» no es un identificador válido.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE parametro SET
                    sql_gps    = @activo,
                    sql_server = @servidor,
                    sql_base   = @base,
                    sql_usuari = @usuario,
                    sql_passwo = @password,
                    sql_tabla  = @tabla,
                    url_gps    = @url,
                    xml_envia  = @xmlEnvia,
                    dir_xml    = @dirXml
                """;
            cmd.Parameters.Add(new SqlParameter("@activo", p.Activo));
            cmd.Parameters.Add(new SqlParameter("@servidor", (p.Servidor ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@base", (p.Base ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@usuario", (p.Usuario ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@password", p.Password ?? ""));
            cmd.Parameters.Add(new SqlParameter("@tabla", tabla));
            cmd.Parameters.Add(new SqlParameter("@url", (p.UrlGps ?? "").Trim()));
            cmd.Parameters.Add(new SqlParameter("@xmlEnvia", p.XmlEnvia));
            cmd.Parameters.Add(new SqlParameter("@dirXml", (p.DirXml ?? "").Trim()));
            await cmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            _reports.InvalidarCacheParametros();
            return AbmResult.Exito();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo("No se pudo grabar la configuración de GPS: " + ex.Message);
        }
    }
}

/// <summary>Datos de entrada para el ABM de Fleteros (fletero_abm.scx). Un solo record
/// para alta y modifica (en modifica se ignora IdContrat porque la PK lógica no se edita).</summary>
public record FleteroInput(
    string IdContrat, string RazonSocial, string Nombre, long Orden, string Cuit,
    string TipoResp, string Domicilio, string Localidad, string Postal, string Provincia,
    string Telefono, string Celular, string Email, string Contacto,
    string IdListaP, string IdLista2, string ModoLiq, string FcPrefere, bool Diagrama);

/// <summary>Datos de entrada para el ABM de Tipo de Vehículos (vehiculo_tipo_abm.scx).</summary>
public record TipoVehiculoInput(
    string Codigo, string Nombre, int Pax, string Subtipo,
    decimal? ConsumoMin, decimal? ConsumoMax, bool Vende, string DirDibujo);

/// <summary>Datos de entrada para el ABM de Cabeceras/Recorridos (cabecera_recorrido_abm.scx).
/// En modifica se ignora Codigo (la PK lógica no se edita).</summary>
public record CabeceraInput(string Codigo, string Nombre, string Nombre1, string Nombre2, string Recorrido);

/// <summary>Datos de entrada para el ABM de Viáticos (chofer_viatico_abm.scx).</summary>
public record ViaticoInput(
    DateOnly Fecha, string IdChofer, int IdMotivo, int IdLiquida,
    string FormaPago, decimal Importe, DateOnly? FPago);

/// <summary>Datos de entrada para el ABM de Operadores (cliente_operador_abm.scx). En modifica se
/// ignora IdOperador (la PK lógica global no se edita). email en minúscula; el resto MAYÚSCULAS.</summary>
public record OperadorInput(
    string IdOperador, string IdCliente, string Nombre, string Telefono,
    string Celular, string Interno, string Email, string Comentario);

/// <summary>Datos de entrada para el ABM de Destinos (destino_abm.scx). Todo en MAYÚSCULAS.
/// Mas100Km = recargo por distancia.</summary>
public record DestinoInput(
    string Destino, string Direccion, string Localidad, string Telefono,
    string Correo, string Contacto, string Cabecera, bool Mas100Km);

/// <summary>Datos de entrada para el ABM de Guardias (trafico_guardia_abm.scx). fpago NO se edita
/// acá (lo escribe la Liquidación de choferes). HsInicio/HsFin son datetime completos (fecha+hora).</summary>
public record GuardiaInput(
    int Interno, string IdVehiculo, string IdChofer, string Nombre, bool Franco,
    DateOnly Fecha, DateTime HsInicio, DateTime HsFin);

/// <summary>Datos de entrada para el ABM de Contactos/Proveedores (estacion_abm.scx). RubroId = FK a
/// estacion_rubro. Los campos control/ult_lote/ypf/esso/cta/cairo son legacy de Combustible.
/// email en minúscula; el resto MAYÚSCULAS.</summary>
public record ContactoInput(
    long RubroId, string Nombre, string Domicilio, string Localidad, string Provincia,
    string Telefono, string Celular, string Radio, string Email, string Contacto1, string Contacto2,
    string MedioPago, bool ControlSaldo, long UltLote, string CairoCodigo, string CairoIibb,
    bool YpfRuta, bool EssoCard, bool CtaCte);

/// <summary>Datos de entrada para el ABM de una carga de combustible (vehiculo_combustible_carga_sobre).
/// p_x_ltr se DERIVA (importe/litros) al grabar. Chofer vacío → literal "SIN CHOFER".</summary>
public record CargaCombustibleInput(
    int Interno, string Dominio, int IdRubro, int EstacionId, string Estacion, string TipoCarga,
    DateOnly FCarga, string Hora, string Chofer, long Odometro, decimal Litros, decimal Importe,
    bool Lleno, bool DosCarga, string FPago, string Usuario);

/// <summary>Datos de entrada para el ABM de un depósito de estación (vehiculo_estacion_saldo_carga).
/// EsEgreso graba el importe × −1 (empresa = "NORTUR").</summary>
public record DepositoEstacionInput(
    int EstacionId, string Estacion, DateOnly Fecha, string FormaPago, decimal Importe,
    bool EsEgreso, string Usuario, string Comentario);

// ── Inputs del módulo Reservas (puertas de alta al circuito viaje) ────────────────

/// <summary>Un adicional de una reserva/plantilla (viaje_adicional o slots inline de la plantilla).</summary>
public record AdicionalInput(string Codigo, string Nombre, int Cantidad, decimal Precio);

/// <summary>
/// Datos de entrada del alta de una reserva especial (reserva_transportacion_con_adicional.scx).
/// Replica el form completo: fechas, cliente/operador, servicios, vehículo, grupo, guía, destinos,
/// Valor Especial (permiso F), adicionales, modo ruta y multiplicadores (días × cantidad servicios).
/// </summary>
public record ReservaEspecialInput(
    // Fechas y horas
    DateOnly? FPedido, DateOnly FReserva, TimeOnly HoraInicio, TimeOnly? HoraFin,
    DateTime? HoraPresentacion, DateOnly? FFin, int CantidadServicios,
    bool VariosDias, DateOnly? FVuelve, TimeOnly? HoraVuelve,
    // Cliente / operador
    string IdCliente, string NombreCliente, string? IdOperador,
    // Servicios / vehículo / cantidades
    string IdServicio1, string? IdServicio2, string? IdServicio3, string TipoVehiculo,
    int Pax, int Km, int Agua, int Voucher,
    // Grupo
    string? Grupo, DateOnly? FGrupoFin,
    // Guía (nombre completo "NOMBRE : TEL" para viaje.nombre_gui; nombre/tel sueltos para upsert guia)
    string? GuiaNombreCompleto, string? GuiaNombre, string? GuiaTelefono,
    // Destinos
    string Desde, string Hasta, string? Provincia, bool Mas100Km, string? Vuelo, string Comentario,
    // Valor Especial (permiso F) — neutros si no se usó
    string? MonedaConvenida, decimal ImporteConvenido, bool SinCargoCliente, decimal Descuento,
    string? MonedaPago, decimal ImportePago, bool SinCargoEmpresa,
    // Adicionales + auditoría
    IReadOnlyList<AdicionalInput>? Adicionales, string Usuario);

/// <summary>Datos de entrada del ABM de una fila de plantilla (reserva_plantilla_mantenimiento_abm.scx).
/// En modifica la PK física (id) va aparte; el resto se reescribe entero.</summary>
public record PlantillaFilaInput(
    string IdReserva, string Cronograma, string HoraIni, string HoraFin, string IdServicio,
    string TipoVeh, string Desde, string Hasta, int Pax, int Km, int Hs, string Cabecera,
    string EmpresaDestino, string Recorrido, string Provincia, string IataDesde, string IataHasta,
    string GpsCod, string IdGuia, string NombreGuia, string GuiaDueno, string Comentario, bool DiaSiguiente);

/// <summary>Datos de entrada del armado de una plantilla (reserva_plantilla_armar.scx).
/// DiasSemana[0..6] = Lun..Dom. NombrePlanta = check "usar nombre de la planta como origen/destino".</summary>
public record ArmadoInput(
    string IdReserva, string IdCliente, string NombreCliente, DateOnly Desde, DateOnly Hasta,
    bool[] DiasSemana, bool IncluirFeriados, bool NombrePlanta, string Usuario);

/// <summary>Fila de plantilla leída para el armado (shape interno con adicionales resueltos).</summary>
public record PlantillaFilaArmar(
    int Id, string Cronograma, string HsInicio, string HsFin, string IdServicio, string TipoVeh,
    string Desde, string Hasta, int Pax, int Km, int Hs, string Cabecera, string EmpresaDestino,
    string Recorrido, string Provincia, string Comentario, string IdGuia, string NombreGuia,
    string GuiaDueno, string GpsCod, IReadOnlyList<AdicionalInput> Adicionales);
