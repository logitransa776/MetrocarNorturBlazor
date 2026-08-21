using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MetroCarSysBlazor.Services;

// ═══════════════════════════════════════════════════════════════════════════════
//  ESCRITURA del submenú Tráfico → Libro de Novedades
//  Plano: docs/PlanoFoxPro/trafico/LIBRO_NOVEDADES.md
//
//  El ALTA de una novedad (F2) ya vivía en AbmService.cs (AltaNovedadAsync). Acá va el resto:
//   · Modificar / Eliminar una novedad desde la pantalla del libro (libro_novedad.scx).
//   · Estampar f_envio de la tanda enviada (libro_novedad_envia_correo.scx).
//   · El ABM de la lista de distribución interna (libro_novedad_parametro.scx).
//
//  🔒 TODO detrás de flags apagados (andamiaje) — ver AbmFeatureFlags.
// ═══════════════════════════════════════════════════════════════════════════════

public partial class AbmService
{
    // ───────────────────────────────────────────────────────────────────────────
    //  Modificar / Eliminar una novedad
    // ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Modificar una novedad. <b>Solo el mensaje</b>, igual que el FoxPro
    /// (<c>libro_novedad_abm.scx</c>, rama <c>modifica</c>):
    /// <code>UPDATE libro_novedad SET mensaje = cMensaje WHERE id = nLibroNovedadGoTo</code>
    /// El asunto no se toca a propósito: en las novedades de unidad es el ÚNICO nexo con el
    /// interno (la tabla no tiene columna <c>interno</c>), así que editarlo rompería el filtro
    /// de <see cref="ReportService.GetNovedadesUnidadAsync"/>.
    /// </summary>
    public async Task<AbmResult> ModificarNovedadAsync(int id, string mensaje)
    {
        if (!AbmFeatureFlags.NovedadesAbmActivo)
            return AbmResult.Fallo("La edición del libro de novedades todavía no está habilitada (sigue en FoxPro).");

        mensaje = (mensaje ?? "").Trim();
        if (mensaje.Length == 0)
            return AbmResult.Fallo("Debe cargar el mensaje de la novedad.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE libro_novedad SET mensaje = @msg WHERE id = @id AND _deleted = 0";
            upd.Parameters.Add(new SqlParameter("@msg", mensaje));
            upd.Parameters.Add(new SqlParameter("@id", id));
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("La novedad ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo modificar la novedad: {ex.Message}");
        }
    }

    /// <summary>
    /// Eliminar una novedad. <b>Baja FÍSICA</b>, fiel al botón «Eliminar» de la lista del libro
    /// (<c>libro_novedad.scx</c>): <c>DELETE FROM libro_novedad WHERE id = …</c>. La tabla no
    /// tiene columna <c>f_delete</c>, así que no hay baja lógica de negocio posible.
    /// </summary>
    /// <remarks>
    /// 🐛 Ojo con el otro «Eliminar»: el del form <c>libro_novedad_abm.scx</c> (el que se abre
    /// desde el F2) está <b>ROTO en el fuente</b> — su <c>DELETE</c> está comentado y encima
    /// apunta a la tabla <c>agenda</c>. Confirmás la baja, la ventana se cierra y la novedad
    /// sigue ahí. El que borra de verdad es el de la lista, que es el que se replica acá.
    /// </remarks>
    public async Task<AbmResult> BajaNovedadAsync(int id)
    {
        if (!AbmFeatureFlags.NovedadesAbmActivo)
            return AbmResult.Fallo("La baja del libro de novedades todavía no está habilitada (sigue en FoxPro).");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM libro_novedad WHERE id = @id";
            del.Parameters.Add(new SqlParameter("@id", id));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("La novedad ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(id);
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar la novedad: {ex.Message}");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────
    //  Estampar f_envio de la tanda (libro_novedad_envia_correo.scx)
    // ───────────────────────────────────────────────────────────────────────────
    //  ⚠ El FoxPro corre estos UPDATE SIEMPRE, aunque el SMTP haya fallado en todos los
    //  destinatarios. Acá los llama CorreoNovedadesService solo si al menos un correo salió.

    /// <summary>Marca las novedades de la tanda como enviadas (<c>f_envio = hoy</c>).
    /// Devuelve en <c>Id</c> la cantidad de filas marcadas.</summary>
    public Task<AbmResult> MarcarNovedadesEnviadasAsync(IReadOnlyList<int> ids) =>
        MarcarEnviadoAsync("libro_novedad", ids, "novedades");

    /// <summary>Marca los siniestros de la tanda como enviados (<c>f_envio = hoy</c>).</summary>
    public Task<AbmResult> MarcarSiniestrosEnviadosAsync(IReadOnlyList<int> ids) =>
        MarcarEnviadoAsync("siniestro", ids, "siniestros");

    private async Task<AbmResult> MarcarEnviadoAsync(string tabla, IReadOnlyList<int> ids, string que)
    {
        if (!AbmFeatureFlags.EnvioCorreosActivo)
            return AbmResult.Fallo("El envío de correos desde Buslink todavía no está habilitado.");
        if (ids.Count == 0) return AbmResult.Exito(0);

        // `tabla` NO viene del usuario: son las dos constantes de arriba. Aun así se valida,
        // porque este método arma el nombre de tabla por interpolación (regla del proyecto).
        if (tabla is not ("libro_novedad" or "siniestro"))
            return AbmResult.Fallo("Tabla no admitida.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            var n = 0;
            // Los ids son int y vienen de una query nuestra; se pasan igual como parámetros
            // en lotes de 200 para no armar un IN gigante.
            foreach (var lote in ids.Chunk(200))
            {
                await using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                var nombres = lote.Select((_, i) => $"@p{i}").ToArray();
                upd.CommandText =
                    $"UPDATE {tabla} SET f_envio = CAST(GETDATE() AS date) " +
                    $"WHERE f_envio IS NULL AND id IN ({string.Join(",", nombres)})";
                for (var i = 0; i < lote.Length; i++)
                    upd.Parameters.Add(new SqlParameter($"@p{i}", lote[i]));
                n += await upd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito(n);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"El correo salió, pero no se pudo marcar el envío de las {que}: {ex.Message}");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────
    //  ABM de la lista de distribución interna (libro_novedad_parametro.scx)
    // ───────────────────────────────────────────────────────────────────────────
    //  La PK lógica es `contacto` (la tabla no tiene id): todos los WHERE del FoxPro pegan por
    //  ese campo, y por eso el Modificar lo deja deshabilitado. Baja FÍSICA (DELETE).
    //  Truncado de la réplica: `combustible` → `combustibl`.

    /// <summary>Los datos de un destinatario tal como los carga la pantalla.</summary>
    public sealed record DestinatarioCorreoInput(
        string Contacto, string Email,
        bool Novedad, bool Siniestro, bool Combustible, bool Auditoria, bool Taller);

    /// <summary>Largos del FoxPro (los textbox: contacto 30, email 70). Las columnas de la
    /// réplica son más anchas (100 / 140) pero se respeta el límite del original.</summary>
    public const int ContactoMaxLen = 30;
    public const int EmailMaxLen = 70;

    private static readonly Regex RxEmail = new(
        @"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$", RegexOptions.Compiled);

    /// <summary>Valida contacto + email. Réplica de <c>ValidarCuentaEmail()</c> del FoxPro
    /// (misma expresión regular) más las dos obligatoriedades del <c>aceptar.Click</c>.</summary>
    public static string? ValidarDestinatarioCorreo(DestinatarioCorreoInput d)
    {
        var contacto = (d.Contacto ?? "").Trim();
        var email = (d.Email ?? "").Trim();
        if (contacto.Length == 0) return "No se cargó el nombre del contacto.";
        if (contacto.Length > ContactoMaxLen) return $"El contacto no puede superar los {ContactoMaxLen} caracteres.";
        if (email.Length == 0) return "No se cargó el correo electrónico.";
        if (email.Length > EmailMaxLen) return $"El correo no puede superar los {EmailMaxLen} caracteres.";
        if (!RxEmail.IsMatch(email)) return "El correo electrónico NO es válido.";
        return null;
    }

    /// <summary>Alta de un destinatario. Rechaza el contacto duplicado, igual que el FoxPro.</summary>
    public async Task<AbmResult> AltaDestinatarioCorreoAsync(DestinatarioCorreoInput d)
    {
        if (!AbmFeatureFlags.DestinatariosCorreoAbmActivo)
            return AbmResult.Fallo("El ABM de destinatarios todavía no está habilitado (sigue en FoxPro).");

        if (ValidarDestinatarioCorreo(d) is string err) return AbmResult.Fallo(err);
        // Format = "!" en el textbox del FoxPro → el contacto se guarda SIEMPRE en mayúsculas.
        var contacto = d.Contacto.Trim().ToUpperInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var dup = conn.CreateCommand())
            {
                dup.Transaction = tx;
                dup.CommandText =
                    "SELECT COUNT(*) FROM libro_novedad_parametro WHERE _deleted = 0 AND RTRIM(contacto) = @c";
                dup.Parameters.Add(new SqlParameter("@c", contacto));
                if ((int)(await dup.ExecuteScalarAsync() ?? 0) > 0)
                {
                    await tx.RollbackAsync();
                    return AbmResult.Fallo("Atención: el contacto ya existe.");
                }
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO libro_novedad_parametro
                        (contacto, email, novedad, siniestro, combustibl, auditoria, taller, _deleted)
                    VALUES (@c, @e, @nov, @sin, @comb, @aud, @tal, 0)
                    """;
                AgregarParamsDestinatario(ins, d, contacto);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return AbmResult.Fallo($"No se pudo dar de alta el destinatario: {ex.Message}");
        }
    }

    /// <summary>
    /// Modifica el correo y los 5 tildes de un destinatario. El <b>contacto no se puede cambiar</b>
    /// (es la PK lógica: el FoxPro deshabilita ese campo en el modo modifica).
    /// </summary>
    public async Task<AbmResult> ModificarDestinatarioCorreoAsync(DestinatarioCorreoInput d)
    {
        if (!AbmFeatureFlags.DestinatariosCorreoAbmActivo)
            return AbmResult.Fallo("El ABM de destinatarios todavía no está habilitado (sigue en FoxPro).");

        if (ValidarDestinatarioCorreo(d) is string err) return AbmResult.Fallo(err);
        var contacto = d.Contacto.Trim().ToUpperInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = """
                UPDATE libro_novedad_parametro
                   SET email = @e, novedad = @nov, siniestro = @sin,
                       combustibl = @comb, auditoria = @aud, taller = @tal
                 WHERE _deleted = 0 AND RTRIM(contacto) = @c
                """;
            AgregarParamsDestinatario(upd, d, contacto);
            if (await upd.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El destinatario ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito();
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo modificar el destinatario: {ex.Message}");
        }
    }

    /// <summary>Baja de un destinatario. <b>FÍSICA</b> (<c>DELETE</c>), como el FoxPro: la tabla
    /// no tiene <c>f_delete</c>.</summary>
    public async Task<AbmResult> BajaDestinatarioCorreoAsync(string contacto)
    {
        if (!AbmFeatureFlags.DestinatariosCorreoAbmActivo)
            return AbmResult.Fallo("El ABM de destinatarios todavía no está habilitado (sigue en FoxPro).");

        contacto = (contacto ?? "").Trim().ToUpperInvariant();
        if (contacto.Length == 0) return AbmResult.Fallo("Falta el contacto a eliminar.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = (SqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM libro_novedad_parametro WHERE RTRIM(contacto) = @c";
            del.Parameters.Add(new SqlParameter("@c", contacto));
            if (await del.ExecuteNonQueryAsync() == 0)
                return AbmResult.Fallo("El destinatario ya no existe.");
            _reports.InvalidarCacheAbm();
            return AbmResult.Exito();
        }
        catch (Exception ex)
        {
            return AbmResult.Fallo($"No se pudo eliminar el destinatario: {ex.Message}");
        }
    }

    private static void AgregarParamsDestinatario(SqlCommand cmd, DestinatarioCorreoInput d, string contacto)
    {
        cmd.Parameters.Add(new SqlParameter("@c", contacto));
        cmd.Parameters.Add(new SqlParameter("@e", d.Email.Trim()));
        cmd.Parameters.Add(new SqlParameter("@nov", d.Novedad));
        cmd.Parameters.Add(new SqlParameter("@sin", d.Siniestro));
        cmd.Parameters.Add(new SqlParameter("@comb", d.Combustible));
        cmd.Parameters.Add(new SqlParameter("@aud", d.Auditoria));
        cmd.Parameters.Add(new SqlParameter("@tal", d.Taller));
    }
}
