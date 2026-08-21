using Microsoft.Extensions.Caching.Memory;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MetroCarSysBlazor.Services;

// ═══════════════════════════════════════════════════════════════════════════════
//  LIBRO DE NOVEDADES — submenú Tráfico → "Libro de Novedades" (3 ítems del FoxPro)
//  Plano: docs/PlanoFoxPro/trafico/LIBRO_NOVEDADES.md
//
//  Los tres forms que cuelgan de ese submenú:
//    · libro_novedad.scx           → la LISTA del libro (grilla + panel de mensaje)
//    · libro_novedad_envia_correo.scx → el proceso BATCH que manda las novedades/siniestros
//                                      a la lista de distribución interna y estampa f_envio
//    · libro_novedad_parametro.scx → el ABM de esa lista de distribución
//
//  El alta de una novedad (F2 de la planilla) ya vivía en ReportService.cs
//  (GetNovedadesViajeAsync / GetNovedadesUnidadAsync) y en AbmService.AltaNovedadAsync.
//  Acá va todo lo que agrega el submenú: consulta histórica, envío y destinatarios.
// ═══════════════════════════════════════════════════════════════════════════════

public partial class ReportService
{
    // ───────────────────────────────────────────────────────────────────────────
    //  1) LA LISTA DEL LIBRO (libro_novedad.scx → arma_grid)
    // ───────────────────────────────────────────────────────────────────────────
    //  El FoxPro hace literalmente `SELECT * FROM libro_novedad ORDER BY f_carga DESC`:
    //  TODAS las filas, sin filtro. Hoy son 48.617 (desde el 18/05/2012) — traerlas todas a
    //  una grilla Blazor viola la regla de performance del proyecto
    //  (docs/performance/PERFORMANCE_GRILLAS_Y_CONEXION.md). Acá se acota por rango de fechas
    //  (default: últimos 30 días) y se agregan los filtros que la operación pide a mano en el
    //  FoxPro leyendo la pantalla: usuario, ligadura a una reserva y búsqueda de texto.

    /// <summary>Qué novedades traer según su ligadura a una reserva.</summary>
    public enum LigaduraNovedad
    {
        /// <summary>Todas.</summary>
        Todas,
        /// <summary>Solo las que cuelgan de una reserva (<c>id_viaje &gt; 0</c>).</summary>
        ConReserva,
        /// <summary>Solo las sueltas (<c>id_viaje = 0</c>) — casi la mitad del libro.</summary>
        SinReserva,
    }

    /// <summary>
    /// La lista del libro de guardia acotada por fecha de carga, con los filtros de la pantalla.
    /// </summary>
    /// <param name="texto">Busca en asunto Y mensaje (el FoxPro no tiene buscador: hay que
    /// scrollear 48.617 filas a mano).</param>
    public async Task<List<LibroNovedadRow>> GetLibroNovedadesAsync(
        DateOnly desde, DateOnly hasta, string? usuario = null,
        LigaduraNovedad ligadura = LigaduraNovedad.Todas, string? texto = null)
    {
        // Las fechas se acotan al rango válido del proyecto igual que el resto de los informes,
        // pero con piso 2012: el libro arranca el 18/05/2012, antes que FechaMinValida (2021),
        // y con el clamp normal la consulta histórica devolvería vacío (misma trampa que
        // ClampComb en Combustible).
        var min = new DateOnly(2012, 1, 1);
        if (desde < min) desde = min;
        if (hasta > FechaMaxValida) hasta = FechaMaxValida;
        if (hasta < desde) hasta = desde;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        var filtroLigadura = ligadura switch
        {
            LigaduraNovedad.ConReserva => " AND ISNULL(n.id_viaje, 0) > 0",
            LigaduraNovedad.SinReserva => " AND ISNULL(n.id_viaje, 0) = 0",
            _ => "",
        };
        var filtroUsuario = string.IsNullOrWhiteSpace(usuario) ? "" : " AND n.usuario_cr = @usr";
        var filtroTexto = string.IsNullOrWhiteSpace(texto)
            ? ""
            : " AND (n.asunto LIKE @txt OR n.mensaje LIKE @txt)";

        // TOP 5000: techo de seguridad. Con el rango default (30 días ≈ 150 filas) no se toca
        // nunca; protege el caso "el usuario pide 2012–2026" de traer las 48.617.
        cmd.CommandText = $"""
            SELECT TOP 5000
                n.id                              AS Id,
                n.f_carga                         AS FCarga,
                RTRIM(ISNULL(n.asunto, ''))       AS Asunto,
                ISNULL(n.mensaje, '')             AS Mensaje,
                RTRIM(ISNULL(n.usuario_cr, ''))   AS Usuario,
                ISNULL(n.id_viaje, 0)             AS IdViaje,
                n.f_envio                         AS FEnvio,
                ISNULL(n.finalizo, 0)             AS Finalizo,
                RTRIM(ISNULL(v.nombre_cli, ''))   AS Cliente,
                v.f_reserva                       AS FReserva
            FROM libro_novedad n
            LEFT JOIN viaje v
                   ON v.id_viaje = n.id_viaje AND v._deleted = 0
            WHERE n._deleted = 0
              AND n.f_carga >= @desde
              AND n.f_carga <  DATEADD(day, 1, @hasta)
              {filtroLigadura}{filtroUsuario}{filtroTexto}
            ORDER BY n.f_carga DESC, n.id DESC
            """;
        cmd.Parameters.Add(NuevoParam(cmd, "@desde", desde.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(NuevoParam(cmd, "@hasta", hasta.ToDateTime(TimeOnly.MinValue)));
        if (filtroUsuario.Length > 0) cmd.Parameters.Add(NuevoParam(cmd, "@usr", usuario!.Trim()));
        if (filtroTexto.Length > 0) cmd.Parameters.Add(NuevoParam(cmd, "@txt", "%" + texto!.Trim() + "%"));

        var lista = new List<LibroNovedadRow>();
        using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            lista.Add(new LibroNovedadRow(
                Id: Convert.ToInt32(rd["Id"]),
                FCarga: rd["FCarga"] is DBNull ? null : Convert.ToDateTime(rd["FCarga"]),
                Asunto: (string)rd["Asunto"],
                Mensaje: ((string)rd["Mensaje"]).TrimEnd(),
                Usuario: (string)rd["Usuario"],
                IdViaje: Convert.ToInt64(rd["IdViaje"]),
                FEnvio: rd["FEnvio"] is DBNull ? null : DateOnly.FromDateTime(Convert.ToDateTime(rd["FEnvio"])),
                Finalizo: Convert.ToBoolean(rd["Finalizo"]),
                Cliente: (string)rd["Cliente"],
                FReserva: rd["FReserva"] is DBNull ? null : DateOnly.FromDateTime(Convert.ToDateTime(rd["FReserva"]))));
        }
        return lista;
    }

    /// <summary>Usuarios que alguna vez cargaron una novedad — para el combo del filtro.</summary>
    public async Task<List<string>> GetUsuariosLibroNovedadAsync()
    {
        return await _cache.GetOrCreateAsync("libro-novedad-usuarios", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // Solo los que cargaron algo en los últimos 3 años: el libro arrastra usuarios de
            // 2012 que hace una década no existen, y ensucian el combo.
            cmd.CommandText = """
                SELECT DISTINCT RTRIM(usuario_cr) AS Usuario
                FROM libro_novedad
                WHERE _deleted = 0
                  AND usuario_cr IS NOT NULL AND RTRIM(usuario_cr) <> ''
                  AND f_carga >= DATEADD(year, -3, GETDATE())
                ORDER BY 1
                """;
            var lista = new List<string>();
            using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) lista.Add(rd.GetString(0));
            return lista;
        }) ?? new List<string>();
    }

    /// <summary>Una novedad puntual (para el editor en modo modifica/baja).</summary>
    public async Task<LibroNovedadRow?> GetLibroNovedadAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1
                n.id, n.f_carga, RTRIM(ISNULL(n.asunto,'')), ISNULL(n.mensaje,''),
                RTRIM(ISNULL(n.usuario_cr,'')), ISNULL(n.id_viaje,0), n.f_envio,
                ISNULL(n.finalizo,0), RTRIM(ISNULL(v.nombre_cli,'')), v.f_reserva
            FROM libro_novedad n
            LEFT JOIN viaje v ON v.id_viaje = n.id_viaje AND v._deleted = 0
            WHERE n.id = @id AND n._deleted = 0
            """;
        cmd.Parameters.Add(NuevoParam(cmd, "@id", id));
        using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new LibroNovedadRow(
            rd.GetInt32(0),
            rd.IsDBNull(1) ? null : rd.GetDateTime(1),
            rd.GetString(2), rd.GetString(3).TrimEnd(), rd.GetString(4),
            rd.GetInt64(5),
            rd.IsDBNull(6) ? null : DateOnly.FromDateTime(rd.GetDateTime(6)),
            rd.GetBoolean(7), rd.GetString(8),
            rd.IsDBNull(9) ? null : DateOnly.FromDateTime(rd.GetDateTime(9)));
    }

    // ───────────────────────────────────────────────────────────────────────────
    //  2) EL ENVÍO DE CORREOS (libro_novedad_envia_correo.scx → Init + envio.Click)
    // ───────────────────────────────────────────────────────────────────────────
    //  El Init arma la tanda: novedades con f_envio vacío + siniestros con f_envio vacío.
    //  ⚠ El FoxPro estampa f_envio DESPUÉS del bucle de envío, PASE LO QUE PASE: si el SMTP
    //  falla, la novedad queda marcada como enviada igual y nadie la vuelve a ver. Ese bug NO
    //  se replica (ver CorreoNovedadesService).

    /// <summary>
    /// Las novedades pendientes de envío (<c>f_envio</c> vacío), con los datos del viaje que el
    /// FoxPro mete en el cuerpo del correo. Es la tanda del bloque NOVEDADES.
    /// </summary>
    public async Task<List<NovedadEnvioRow>> GetNovedadesPendientesEnvioAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        // El FoxPro: `SELECT * FROM libro_novedad WHERE Empty(f_envio) ORDER BY f_carga DESC`.
        // El LEFT JOIN a `viaje` trae de una lo que allá se resuelve con un SELECT por fila
        // dentro del Do While (una query por novedad).
        cmd.CommandText = """
            SELECT
                n.id                                AS Id,
                n.f_carga                           AS FCarga,
                RTRIM(ISNULL(n.asunto, ''))         AS Asunto,
                ISNULL(n.mensaje, '')               AS Mensaje,
                RTRIM(ISNULL(n.usuario_cr, ''))     AS Usuario,
                ISNULL(n.id_viaje, 0)               AS IdViaje,
                RTRIM(ISNULL(v.nombre_cli, ''))     AS Cliente,
                RTRIM(ISNULL(v.hs_s_inici, ''))     AS HoraServicio,
                RTRIM(ISNULL(v.d_destino, ''))      AS Desde,
                RTRIM(ISNULL(v.h_destino, ''))      AS Hasta,
                ISNULL(v.interno, 0)                AS Interno,
                RTRIM(ISNULL(v.nombre_cho, ''))     AS Chofer,
                RTRIM(ISNULL(v.cronograma, ''))     AS Cronograma
            FROM libro_novedad n
            LEFT JOIN viaje v
                   ON v.id_viaje = n.id_viaje AND v._deleted = 0
            WHERE n._deleted = 0 AND n.f_envio IS NULL
            ORDER BY n.f_carga DESC, n.id DESC
            """;

        var lista = new List<NovedadEnvioRow>();
        using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            lista.Add(new NovedadEnvioRow(
                Id: Convert.ToInt32(rd["Id"]),
                FCarga: rd["FCarga"] is DBNull ? null : Convert.ToDateTime(rd["FCarga"]),
                Asunto: (string)rd["Asunto"],
                Mensaje: ((string)rd["Mensaje"]).TrimEnd(),
                Usuario: (string)rd["Usuario"],
                IdViaje: Convert.ToInt64(rd["IdViaje"]),
                Cliente: (string)rd["Cliente"],
                HoraServicio: (string)rd["HoraServicio"],
                Desde: (string)rd["Desde"],
                Hasta: (string)rd["Hasta"],
                Interno: Convert.ToInt64(rd["Interno"]),
                Chofer: (string)rd["Chofer"],
                Cronograma: (string)rd["Cronograma"]));
        }
        return lista;
    }

    /// <summary>
    /// Las últimas novedades YA enviadas, con los mismos campos que la tanda. Sirve para que la
    /// pantalla de envío pueda mostrar <b>un ejemplo</b> del correo cuando no hay nada pendiente
    /// (que es el estado normal un rato después de cada corrida): sin esto, el previsualizador
    /// queda en blanco y no se puede ver qué formato tiene lo que se manda.
    /// </summary>
    public async Task<List<NovedadEnvioRow>> GetUltimasNovedadesEnviadasAsync(int top = 10)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        // El TOP + ORDER BY va en una tabla derivada, ANTES del LEFT JOIN a `viaje`: así el sort
        // es de 10 filas y no de las ~48.000 novedades ya enviadas. Medido: 252 ms contra 832 ms
        // con el JOIN afuera (19/08/2026). Ojo con este JOIN en general: `libro_novedad.id_viaje`
        // es bigint y `viaje.id_viaje` es int, así que la comparación lleva un CONVERT implícito.
        cmd.CommandText = $"""
            SELECT
                n.id                                AS Id,
                n.f_carga                           AS FCarga,
                RTRIM(ISNULL(n.asunto, ''))         AS Asunto,
                ISNULL(n.mensaje, '')               AS Mensaje,
                RTRIM(ISNULL(n.usuario_cr, ''))     AS Usuario,
                ISNULL(n.id_viaje, 0)               AS IdViaje,
                RTRIM(ISNULL(v.nombre_cli, ''))     AS Cliente,
                RTRIM(ISNULL(v.hs_s_inici, ''))     AS HoraServicio,
                RTRIM(ISNULL(v.d_destino, ''))      AS Desde,
                RTRIM(ISNULL(v.h_destino, ''))      AS Hasta,
                ISNULL(v.interno, 0)                AS Interno,
                RTRIM(ISNULL(v.nombre_cho, ''))     AS Chofer,
                RTRIM(ISNULL(v.cronograma, ''))     AS Cronograma
            FROM (
                SELECT TOP {Math.Clamp(top, 1, 50)} id, f_carga, asunto, mensaje, usuario_cr, id_viaje
                FROM libro_novedad
                WHERE _deleted = 0 AND f_envio IS NOT NULL
                ORDER BY f_carga DESC, id DESC
            ) n
            LEFT JOIN viaje v
                   ON v.id_viaje = n.id_viaje AND v._deleted = 0
            ORDER BY n.f_carga DESC, n.id DESC
            """;

        var lista = new List<NovedadEnvioRow>();
        using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            lista.Add(new NovedadEnvioRow(
                Id: Convert.ToInt32(rd["Id"]),
                FCarga: rd["FCarga"] is DBNull ? null : Convert.ToDateTime(rd["FCarga"]),
                Asunto: (string)rd["Asunto"],
                Mensaje: ((string)rd["Mensaje"]).TrimEnd(),
                Usuario: (string)rd["Usuario"],
                IdViaje: Convert.ToInt64(rd["IdViaje"]),
                Cliente: (string)rd["Cliente"],
                HoraServicio: (string)rd["HoraServicio"],
                Desde: (string)rd["Desde"],
                Hasta: (string)rd["Hasta"],
                Interno: Convert.ToInt64(rd["Interno"]),
                Chofer: (string)rd["Chofer"],
                Cronograma: (string)rd["Cronograma"]));
        }
        return lista;
    }

    /// <summary>
    /// Los siniestros pendientes de envío (<c>f_envio</c> vacío). Es la tanda del bloque
    /// SINIESTROS. El cuerpo de cada uno se arma después con
    /// <see cref="GetSiniestroDetalleAsync"/>, que ya existe y trae las 5 solapas.
    /// </summary>
    /// <remarks>
    /// ⚠ El FoxPro usa `FROM siniestro a , chofer b WHERE a.id_chofer = b.id_chofer` — un INNER
    /// JOIN implícito: <b>un siniestro cuyo chofer ya no está en la tabla no se envía nunca</b>.
    /// Acá se replica el INNER JOIN (para no cambiar qué entra en la tanda) pero se cuenta
    /// aparte cuántos quedan afuera por eso, que es información que allá no se ve.
    /// Al 19/08/2026 la tanda está en cero: los 313 siniestros ya tienen f_envio.
    /// </remarks>
    public async Task<(List<SiniestroRow> Tanda, int SinChofer)> GetSiniestrosPendientesEnvioAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                s.id                              AS Id,
                RTRIM(ISNULL(c.nombre, ''))       AS Conductor,
                RTRIM(ISNULL(s.id_vehicul, ''))   AS Dominio,
                ISNULL(s.interno, 0)              AS Interno,
                s.fecha                           AS Fecha,
                RTRIM(ISNULL(s.lugar, ''))        AS Lugar,
                RTRIM(ISNULL(s.marca_y_mo, ''))   AS Marca,
                RTRIM(ISNULL(s.tipo_acc, ''))     AS TipoAcc,
                RTRIM(ISNULL(s.localidad, ''))    AS Localidad,
                ISNULL(s.id_viaje, 0)             AS IdViaje
            FROM siniestro s
            INNER JOIN chofer c ON c.id_chofer = s.id_chofer
            WHERE s._deleted = 0 AND s.f_envio IS NULL
            ORDER BY s.id;

            SELECT COUNT(*)
            FROM siniestro s
            WHERE s._deleted = 0 AND s.f_envio IS NULL
              AND NOT EXISTS (SELECT 1 FROM chofer c WHERE c.id_chofer = s.id_chofer);
            """;

        var tanda = new List<SiniestroRow>();
        using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            tanda.Add(new SiniestroRow(
                Convert.ToInt32(rd["Id"]), (string)rd["Conductor"], (string)rd["Dominio"],
                (int)Convert.ToInt64(rd["Interno"]),
                rd["Fecha"] is DBNull ? null : DateOnly.FromDateTime(Convert.ToDateTime(rd["Fecha"])),
                (string)rd["Lugar"], (string)rd["Marca"], (string)rd["TipoAcc"],
                (string)rd["Localidad"], Convert.ToInt64(rd["IdViaje"])));
        }
        var sinChofer = 0;
        if (await rd.NextResultAsync() && await rd.ReadAsync())
            sinChofer = rd.GetInt32(0);
        return (tanda, sinChofer);
    }

    // ───────────────────────────────────────────────────────────────────────────
    //  3) LOS DESTINATARIOS (libro_novedad_parametro.scx → arma_grid)
    // ───────────────────────────────────────────────────────────────────────────
    //  La lista de distribución INTERNA de la empresa (12 contactos al 19/08/2026: gerencia,
    //  monitoreo, tráfico, proveedores…). NO confundir con los contactos del CLIENTE de la
    //  ficha `cliente` (contacto1..10 / email1..10), que son los del aviso al cliente del F2.
    //
    //  ⚠ Truncados de la réplica: `combustible` → `combustibl`. La PK lógica es `contacto`
    //  (el FoxPro hace todos sus WHERE por ese campo, no hay id). La tabla arrastra además
    //  contacto_1..10 / email_1..10 de una versión anterior: ni el form ni el envío los tocan.

    /// <summary>La lista de distribución interna, ordenada por contacto (igual que el FoxPro).</summary>
    public async Task<List<DestinatarioCorreoRow>> GetDestinatariosCorreoAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                RTRIM(ISNULL(contacto, ''))  AS Contacto,
                RTRIM(ISNULL(email, ''))     AS Email,
                ISNULL(novedad, 0)           AS Novedad,
                ISNULL(siniestro, 0)         AS Siniestro,
                ISNULL(combustibl, 0)        AS Combustible,
                ISNULL(auditoria, 0)         AS Auditoria,
                ISNULL(taller, 0)            AS Taller
            FROM libro_novedad_parametro
            WHERE _deleted = 0
            ORDER BY contacto
            """;
        var lista = new List<DestinatarioCorreoRow>();
        using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            lista.Add(new DestinatarioCorreoRow(
                (string)rd["Contacto"], (string)rd["Email"],
                Convert.ToBoolean(rd["Novedad"]), Convert.ToBoolean(rd["Siniestro"]),
                Convert.ToBoolean(rd["Combustible"]), Convert.ToBoolean(rd["Auditoria"]),
                Convert.ToBoolean(rd["Taller"])));
        }
        return lista;
    }

    /// <summary>
    /// La configuración SMTP de <c>parametro</c> (la misma fila única que usa la pantalla de
    /// Parámetros Empresa) + la fecha del último envío del informe de Combustible/Taller.
    /// </summary>
    /// <remarks>
    /// Truncados: <c>smtp_server</c>→<c>smtp_serve</c>, <c>smtp_puerto</c>→<c>smtp_puert</c>,
    /// <c>smtp_usuario</c>→<c>smtp_usuar</c>, <c>smtp_password</c>→<c>smtp_passw</c>,
    /// <c>smtp_nombre</c>→<c>smtp_nombr</c>, <c>f_ult_envio_comb</c>→<c>f_ult_envi</c>.
    /// </remarks>
    public async Task<SmtpConfigDto> GetSmtpConfigAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1
                RTRIM(ISNULL(smtp_serve, '')) AS Servidor,
                ISNULL(smtp_puert, 0)         AS Puerto,
                RTRIM(ISNULL(smtp_usuar, '')) AS Usuario,
                ISNULL(smtp_passw, '')        AS Password,
                RTRIM(ISNULL(smtp_nombr, '')) AS Remitente,
                f_ult_envi                    AS UltimoEnvio
            FROM parametro
            """;
        using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return new SmtpConfigDto();
        return new SmtpConfigDto
        {
            Servidor = (string)rd["Servidor"],
            Puerto = (int)Convert.ToInt64(rd["Puerto"]),
            Usuario = (string)rd["Usuario"],
            Password = ((string)rd["Password"]).Trim(),
            Remitente = (string)rd["Remitente"],
            UltimoEnvio = rd["UltimoEnvio"] is DBNull
                ? null : DateOnly.FromDateTime(Convert.ToDateTime(rd["UltimoEnvio"])),
        };
    }
}

// ── DTOs del Libro de Novedades ────────────────────────────────────────────────

/// <summary>
/// Una fila de la lista del libro de guardia. <paramref name="IdViaje"/> en 0 = novedad SUELTA
/// (sin reserva). <paramref name="FEnvio"/> con fecha = ya salió en un correo de la tanda diaria.
/// </summary>
public record LibroNovedadRow(
    int Id, DateTime? FCarga, string Asunto, string Mensaje, string Usuario,
    long IdViaje, DateOnly? FEnvio, bool Finalizo, string Cliente, DateOnly? FReserva)
{
    /// <summary>Las novedades de unidad guardan la unidad como texto en el asunto
    /// (<c>"int: 8 dom: AD255RA chof:…"</c>) porque la tabla no tiene columna interno.</summary>
    public bool EsDeUnidad => Asunto.StartsWith("int:", StringComparison.OrdinalIgnoreCase);

    public string Origen => IdViaje > 0 ? "Reserva" : EsDeUnidad ? "Unidad" : "Suelta";
}

/// <summary>Una novedad de la tanda de envío, con los datos del viaje que van al cuerpo.</summary>
public record NovedadEnvioRow(
    int Id, DateTime? FCarga, string Asunto, string Mensaje, string Usuario, long IdViaje,
    string Cliente, string HoraServicio, string Desde, string Hasta,
    long Interno, string Chofer, string Cronograma);

/// <summary>
/// Un destinatario de la lista de distribución interna (<c>libro_novedad_parametro</c>).
/// <paramref name="Contacto"/> es la PK lógica (no hay columna id).
/// </summary>
public record DestinatarioCorreoRow(
    string Contacto, string Email,
    bool Novedad, bool Siniestro, bool Combustible, bool Auditoria, bool Taller)
{
    /// <summary>Cuántos de los 5 informes recibe — para la grilla.</summary>
    public int Suscripciones =>
        (Novedad ? 1 : 0) + (Siniestro ? 1 : 0) + (Combustible ? 1 : 0)
        + (Auditoria ? 1 : 0) + (Taller ? 1 : 0);
}

/// <summary>Config SMTP de la fila única de <c>parametro</c>.</summary>
public class SmtpConfigDto
{
    public string Servidor = "", Usuario = "", Password = "", Remitente = "";
    public int Puerto;
    /// <summary>`parametro.f_ult_envi` — fecha del último envío del informe de Combustible/Taller.</summary>
    public DateOnly? UltimoEnvio;

    public bool Configurado => Servidor.Length > 0 && Puerto > 0;
}
