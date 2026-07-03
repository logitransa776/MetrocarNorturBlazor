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
public class AbmService
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
        public static AbmResult Fallo(string error) => new(false, error);
        public static AbmResult Exito(int? id = null) => new(true, null, id);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  USUARIOS — alta / modifica / baja / password
    //  Espeja usuario_abm.scx (alta/baja/modifica) y cambio_password.scx.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Alta de un usuario (usuario_abm.scx modo "alta"). Valida nombre no vacío, contraseña,
    /// y unicidad del nombre. El <c>id</c> se calcula como MAX(id)+1 (la PK física NO es identity,
    /// así lo hace el FoxPro). Graba <c>nivel="12345"</c> fijo, <c>f_create=hoy</c>, <c>_deleted=0</c>.
    /// </summary>
    public async Task<AbmResult> AltaUsuarioAsync(string usuario, string password, string acceso, bool operador)
    {
        usuario = (usuario ?? "").Trim();
        password = (password ?? "").Trim();

        if (string.IsNullOrWhiteSpace(usuario))
            return AbmResult.Fallo("No se cargó el nombre del Usuario.");
        if (usuario.Length > 15)
            return AbmResult.Fallo("El nombre de usuario no puede superar 15 caracteres.");
        if (password.Length > 15)
            return AbmResult.Fallo("La contraseña no puede superar 15 caracteres.");
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
        if (password.Length > 15)
            return AbmResult.Fallo("La contraseña no puede superar 15 caracteres.");
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
                    SET password = @password,
                        acceso   = @acceso,
                        operador = @operador,
                        f_modify = CAST(GETDATE() AS date)
                        {(rehabilitar ? ", f_delete = NULL" : "")}
                    WHERE id = @id AND _deleted = 0
                    """;
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
        if (nuevaPassword.Length > 15)
            return AbmResult.Fallo("La contraseña no puede superar 15 caracteres.");

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
}
