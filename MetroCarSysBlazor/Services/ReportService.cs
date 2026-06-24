using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MetroCarSysBlazor.Data;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Capa de datos — equivalente a data.py del Streamlit.
/// Usa SQL crudo con FromSqlRaw/SqlQuery para evitar el mapeo completo
/// de los models de FoxPro (80+ propiedades por tabla), y proyecta
/// directamente a DTOs tipados.
/// </summary>
public class ReportService
{
    private readonly IDbContextFactory<NorturDbContext> _dbFactory;
    private readonly IMemoryCache _cache;

    public static readonly DateOnly FechaMinValida = new(2021, 1, 1);
    public static readonly DateOnly FechaMaxValida = new(2027, 12, 31);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    // Tráfico es operación viva: TTL menor al timer de auto-refresh (60s) para que
    // cada tick del timer encuentre el caché vencido y traiga datos frescos.
    private static readonly TimeSpan CacheTtlTrafico = TimeSpan.FromSeconds(55);

    public ReportService(IDbContextFactory<NorturDbContext> dbFactory, IMemoryCache cache)
    {
        _dbFactory = dbFactory;
        _cache = cache;
    }

    public async Task<List<ServicioDto>> GetServiciosAsync()
    {
        return await _cache.GetOrCreateAsync("servicios", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Database
                .SqlQuery<ServicioDto>($"""
                    SELECT id_servici AS IdServici, nombre AS Nombre
                    FROM servicio
                    WHERE _deleted = 0
                    ORDER BY nombre
                    """)
                .ToListAsync();
        }) ?? new();
    }

    public async Task<List<ReservaFechaServicioRow>> GetReservasPorFechaServicioAsync(
        DateOnly desde,
        DateOnly hasta,
        IReadOnlyCollection<string> serviciosSel,
        bool incluirCanceladas)
    {
        var servKey = serviciosSel.Count == 0 ? "all" : string.Join(",", serviciosSel.OrderBy(x => x));
        var key = $"rfs|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{incluirCanceladas}|{servKey}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // Construimos el WHERE dinámico igual que data.py
            var where = new List<string>
            {
                "v._deleted = 0",
                $"v.f_reserva BETWEEN '{desde:yyyy-MM-dd}' AND '{hasta:yyyy-MM-dd}'"
            };
            if (!incluirCanceladas)
                where.Add("v.estado_via <> 'CANCELADO'");
            if (serviciosSel.Count > 0)
            {
                var lista = string.Join(",", serviciosSel.Select(s => $"'{s.Replace("'", "''")}'"));
                where.Add($"v.id_servici IN ({lista})");
            }

            var sql = $"""
                SELECT
                    v.f_reserva                              AS Fecha,
                    v.id_servici                             AS CodServicio,
                    COALESCE(s.nombre, v.id_servici)         AS Servicio,
                    COUNT(*)                                 AS Reservas,
                    SUM(CASE WHEN v.estado_via='CANCELADO' THEN 1 ELSE 0 END) AS Canceladas,
                    SUM(COALESCE(v.pax, 0))                  AS Pax
                FROM viaje v
                LEFT JOIN servicio s ON v.id_servici = s.id_servici
                WHERE {string.Join(" AND ", where)}
                GROUP BY v.f_reserva, v.id_servici, s.nombre
                ORDER BY v.f_reserva, Servicio
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var result = new List<ReservaFechaServicioRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new ReservaFechaServicioRow(
                    DateOnly.FromDateTime(reader.GetDateTime(0)),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5)
                ));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Planilla de servicios del día (réplica de la "Operación de Tráfico" del FoxPro).
    /// Filtro fiel al original (arma_grid_viaje, modo REFRESH):
    ///   - f_reserva = día exacto
    ///   - _deleted = 0
    ///   - se ocultan los CANCELADO (el FoxPro los borra del cursor)
    ///   - incluye ambos orígenes ('P' plantilla y 'T' transportación)
    ///   - orden por hs_inicio
    /// </summary>
    public async Task<List<PlanillaTraficoRow>> GetPlanillaTraficoAsync(DateOnly dia)
    {
        var key = $"trafico|{dia:yyyyMMdd}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlTrafico;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var sql = $"""
                SELECT
                    v.id_viaje                                           AS IdViaje,
                    v.f_reserva                                          AS Fecha,
                    v.origen                                             AS Origen,
                    CONVERT(varchar(5), v.hs_present, 108)               AS HPre,
                    CONVERT(varchar(5), v.hs_inicio, 108)               AS HIni,
                    CONVERT(varchar(5), v.hs_fin, 108)                  AS HFin,
                    CONVERT(varchar(5), v.hs_aviso, 108)                AS HAvi,
                    CONVERT(varchar(5), v.hs_fin_apr, 108)              AS HCie,
                    v.cronogram2                                         AS UPr,
                    v.cronograma                                         AS UCb,
                    v.id_interno                                         AS UAs,
                    v.chequeo                                            AS Chq,
                    v.chequeo_ag                                         AS Ag,
                    LTRIM(RTRIM(v.d_destino)) + ' a ' + LTRIM(RTRIM(v.h_destino)) AS Recorrido,
                    v.fletero                                            AS Fletero,
                    v.nombre_cho                                         AS Chofer,
                    LEFT(LTRIM(RTRIM(v.id_vehicu2)), 4)                  AS Veh,
                    v.id_cliente                                         AS Cliente,
                    v.pax                                                AS Pax,
                    v.agua                                               AS Agua,
                    CASE WHEN LTRIM(RTRIM(
                        COALESCE(v.adi_cod_1,'') + COALESCE(v.adi_cod_2,'') +
                        COALESCE(v.adi_cod_3,'') + COALESCE(v.adi_cod_4,'') +
                        COALESCE(v.adi_cod_5,''))) <> '' THEN 'A' ELSE '' END AS Adj,
                    v.comentario                                         AS Comentario,
                    v.grupo                                              AS Grupo,
                    v.vuelo                                              AS Vuelo,
                    v.nombre_gui                                         AS Guia,
                    v.estado_via                                         AS Estado,
                    v.id_chofer                                          AS IdChofer
                FROM viaje v
                WHERE v._deleted = 0
                  AND v.f_reserva = '{dia:yyyy-MM-dd}'
                  AND v.estado_via <> 'CANCELADO'
                ORDER BY v.hs_inicio, v.hs_present
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            // Lectura por nombre de columna (robusta frente a cambios de orden en el SELECT).
            string S(System.Data.Common.DbDataReader r, string c) { var i = r.GetOrdinal(c); return r.IsDBNull(i) ? "" : r.GetValue(i).ToString()!.Trim(); }
            int? N(System.Data.Common.DbDataReader r, string c) { var i = r.GetOrdinal(c); return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i)); }

            // Cliente interno NORTUR (parametro.id_cliente_prueba). El FoxPro lo lee del parámetro;
            // en la réplica es estable y vale 'NORTUR' (mismo criterio que GetReservasPorBandaHorariaAsync).
            const string idClientePrueba = "NORTUR";

            var result = new List<PlanillaTraficoRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var cronograma = S(reader, "UCb");
                var cronogramaCbio = S(reader, "UPr");
                var idChofer = S(reader, "IdChofer");
                var cliente = S(reader, "Cliente");

                // Réplica de chkNortur (arma_grid_viaje): fila interna si
                // cronograma, cronogramacbio (réplica: cronogram2) o id_chofer = NORTUR.
                bool esNortur = cronograma == idClientePrueba
                             || cronogramaCbio == idClientePrueba
                             || idChofer == idClientePrueba;

                result.Add(new PlanillaTraficoRow(
                    IdViaje: Convert.ToInt32(reader.GetValue(reader.GetOrdinal("IdViaje"))),
                    Fecha: DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("Fecha"))),
                    Origen: S(reader, "Origen"),
                    EsNortur: esNortur,
                    HPre: S(reader, "HPre"),
                    HIni: S(reader, "HIni"),
                    HFin: S(reader, "HFin"),
                    HAvi: S(reader, "HAvi"),
                    HCie: S(reader, "HCie"),
                    UPr: S(reader, "UPr"),
                    UCb: cronograma,
                    UAs: S(reader, "UAs"),
                    Chq: N(reader, "Chq"),
                    Ag: N(reader, "Ag"),
                    Recorrido: S(reader, "Recorrido"),
                    Fletero: S(reader, "Fletero"),
                    Chofer: S(reader, "Chofer"),
                    Veh: S(reader, "Veh"),
                    Cliente: cliente,
                    Pax: N(reader, "Pax"),
                    Agua: N(reader, "Agua"),
                    Adj: S(reader, "Adj"),
                    Comentario: S(reader, "Comentario"),
                    Grupo: S(reader, "Grupo"),
                    Vuelo: S(reader, "Vuelo"),
                    Guia: S(reader, "Guia"),
                    Estado: S(reader, "Estado")
                ));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Token de versión de los datos de Tráfico de un día. Query ultraliviana SIN caché:
    /// la usa el auto-refresh de la planilla (cada 60s) para saber si algo cambió en la
    /// base antes de recargar la grilla completa. Sin filtro _deleted: un borrado lógico
    /// también debe disparar el refresh. Incluye vehiculo porque el panel Buses muestra
    /// el estado vivo de la flota.
    /// </summary>
    public async Task<TraficoVersion> GetTraficoVersionAsync(DateOnly dia)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var sql = $"""
            SELECT
                (SELECT COUNT(*)           FROM viaje    WHERE f_reserva = '{dia:yyyy-MM-dd}') AS CantViajes,
                (SELECT MAX(_updated_at)   FROM viaje    WHERE f_reserva = '{dia:yyyy-MM-dd}') AS UltViaje,
                (SELECT MAX(_updated_at)   FROM vehiculo)                                       AS UltVehiculo
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            DateTime? D(int i) => reader.IsDBNull(i) ? null : reader.GetDateTime(i);
            return new TraficoVersion(reader.GetInt32(0), D(1), D(2));
        }
        return new TraficoVersion(0, null, null);
    }

    /// <summary>
    /// Borra del caché las entradas de Tráfico de un día. La llama el auto-refresh
    /// cuando detecta cambios, para que la recarga siguiente vaya directo a la base
    /// aunque el TTL de 55s no haya vencido todavía.
    /// </summary>
    public void InvalidarCacheTrafico(DateOnly dia)
    {
        _cache.Remove($"trafico|{dia:yyyyMMdd}");
        _cache.Remove($"trafico-cxl|{dia:yyyyMMdd}");
        _cache.Remove($"trafico-buses|{DateOnly.FromDateTime(DateTime.Today):yyyyMMdd}");
    }

    public void InvalidarCacheDetalle(int idViaje) =>
        _cache.Remove($"detalle-viaje|{idViaje}");

    /// <summary>
    /// Listas de unidades para los combos de la pantalla de Tráfico (réplica del Init de trafico2.scx):
    ///   - Asignadas (cursorCronogramaTrafico): todos los internos activos,
    ///     ordenados por empresa (fletero.orden) y nº de interno. Filtra "U/Cb" (viaje.cronograma).
    ///   - Programadas (cursorCronogramaDiagrama): internos individuales si el fletero tiene
    ///     flag "diagrama", o una sola entrada por empresa (id_contratado) si no.
    ///     Filtra "U/Pr" (viaje.cronogramacbio → columna cronogram2 en la réplica).
    /// </summary>
    public async Task<CombosUnidadesTrafico> GetCombosUnidadesTraficoAsync()
    {
        const string key = "trafico-combos-unidades";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // FoxPro: Empty(a.f_delete) → en la réplica el date vacío llega NULL o muy antiguo.
            const string sqlAsignadas = """
                SELECT LTRIM(RTRIM(a.cronograma)) AS cron
                FROM vehiculo a
                INNER JOIN fletero b ON a.fletero = b.id_contrat
                WHERE (a.f_delete IS NULL OR a.f_delete < '1901-01-01')
                  AND a.activo = 1 AND a._deleted = 0 AND b._deleted = 0
                ORDER BY b.orden, a.interno
                """;

            const string sqlProgramadas = """
                SELECT CASE WHEN b.diagrama = 1 THEN LTRIM(RTRIM(a.cronograma))
                            ELSE LTRIM(RTRIM(b.id_contrat)) END AS cron,
                       b.orden
                FROM vehiculo a
                INNER JOIN fletero b ON a.fletero = b.id_contrat
                WHERE a._deleted = 0 AND b._deleted = 0
                GROUP BY CASE WHEN b.diagrama = 1 THEN LTRIM(RTRIM(a.cronograma))
                              ELSE LTRIM(RTRIM(b.id_contrat)) END,
                         b.orden
                ORDER BY b.orden, cron
                """;

            var asignadas = new List<string>();
            var programadas = new List<string>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sqlAsignadas;
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    if (!reader.IsDBNull(0)) asignadas.Add(reader.GetString(0));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sqlProgramadas;
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    if (!reader.IsDBNull(0)) programadas.Add(reader.GetString(0));
            }

            // El combo no admite vacíos ni repetidos (el FoxPro los mostraba tal cual).
            return new CombosUnidadesTrafico(
                programadas.Where(c => c.Length > 0).Distinct().ToList(),
                asignadas.Where(c => c.Length > 0).Distinct().ToList());
        }) ?? new CombosUnidadesTrafico(new(), new());
    }

    /// <summary>
    /// Panel "Buses" — estado vivo de la flota (réplica de arma_grid_vehiculo de trafico2.scx,
    /// la grilla derecha que aparece al tildar el checkbox Buses).
    /// Fuente: `vehiculo` (cada unidad guarda su último estado/viaje/chofer) + `fletero` (orden
    /// de empresas) + `chofer_franco` para la columna Franco. Fiel al FoxPro:
    ///   - mismo filtro que el combo de internos: activo, sin f_delete, orden empresa + interno
    ///   - Franco se busca para HOY (el FoxPro usa Date(), no el día navegado) y solo
    ///     para internos &lt; 999 (los >= 999 son unidades especiales/placeholder)
    ///   - ASIGNADO con hs_inicio &lt;= ahora se muestra como CURSO (solo display)
    /// </summary>
    public async Task<List<PanelBusRow>> GetPanelBusesAsync()
    {
        var hoy = DateOnly.FromDateTime(DateTime.Today);
        var key = $"trafico-buses|{hoy:yyyyMMdd}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlTrafico;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // OUTER APPLY TOP 1 = el "Select ... Into Cursor tpFranco" por fila del FoxPro
            // (si el chofer tiene más de un franco cargado para hoy, toma el primero).
            var sql = $"""
                SELECT
                    LTRIM(RTRIM(v.fletero))    AS Fletero,
                    v.interno                  AS Interno,
                    LTRIM(RTRIM(v.id_chofer))  AS Chofer,
                    LTRIM(RTRIM(v.id_chofer2)) AS Chofer2,
                    CASE WHEN v.interno < 999 THEN COALESCE(LTRIM(RTRIM(cf.codigo)), '') ELSE '' END AS Franco,
                    LTRIM(RTRIM(v.estado))     AS Estado,
                    v.id_viaje                 AS IdViaje,
                    LTRIM(RTRIM(v.id_zona))    AS Zona,
                    LTRIM(RTRIM(v.nextel))     AS Nextel,
                    v.pax                      AS Pax,
                    LTRIM(RTRIM(v.id_vehicul)) AS Vehiculo,
                    v.hs_inicio                AS HsInicio
                FROM vehiculo v
                INNER JOIN fletero f ON v.fletero = f.id_contrat
                OUTER APPLY (
                    SELECT TOP 1 c.codigo
                    FROM chofer_franco c
                    WHERE c.id_chofer = v.id_chofer
                      AND c.fecha = '{hoy:yyyy-MM-dd}'
                      AND c._deleted = 0
                ) cf
                WHERE v.activo = 1
                  AND (v.f_delete IS NULL OR v.f_delete < '1901-01-01')
                  AND v._deleted = 0 AND f._deleted = 0
                ORDER BY f.orden, v.interno
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            string S(System.Data.Common.DbDataReader r, string c) { var i = r.GetOrdinal(c); return r.IsDBNull(i) ? "" : r.GetValue(i).ToString()!.Trim(); }
            int? N(System.Data.Common.DbDataReader r, string c) { var i = r.GetOrdinal(c); return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i)); }

            var ahora = DateTime.Now;
            var result = new List<PanelBusRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var iHs = reader.GetOrdinal("HsInicio");
                DateTime? hsInicio = reader.IsDBNull(iHs) ? null : reader.GetDateTime(iHs);

                // Réplica del Do While de arma_grid_vehiculo: ASIGNADO ya iniciado → CURSO.
                var estado = S(reader, "Estado");
                if (estado == "ASIGNADO" && hsInicio is not null && hsInicio <= ahora)
                    estado = "CURSO";

                result.Add(new PanelBusRow(
                    Fletero: S(reader, "Fletero"),
                    Interno: N(reader, "Interno") ?? 0,
                    Chofer: S(reader, "Chofer"),
                    Chofer2: S(reader, "Chofer2"),
                    Franco: S(reader, "Franco"),
                    Estado: estado,
                    IdViaje: N(reader, "IdViaje"),
                    Zona: S(reader, "Zona"),
                    Nextel: S(reader, "Nextel"),
                    Pax: N(reader, "Pax"),
                    Vehiculo: S(reader, "Vehiculo"),
                    HsInicio: hsInicio
                ));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Servicios CANCELADOS del día — réplica del botón "Cxl" de trafico2.scx
    /// (arma_grid_viaje caso CANCELADO + columnas de arma_grid_viaje_sup_cnl).
    /// El FoxPro hace INNER JOIN con viaje_motivo_cancela; acá LEFT JOIN para no
    /// perder cancelados sin motivo cargado (queda Motivo vacío).
    /// </summary>
    public async Task<List<TraficoCanceladoRow>> GetTraficoCanceladosAsync(DateOnly dia)
    {
        var key = $"trafico-cxl|{dia:yyyyMMdd}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlTrafico;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var sql = $"""
                SELECT
                    v.id_viaje                                           AS IdViaje,
                    v.f_reserva                                          AS Fecha,
                    CASE WHEN LTRIM(RTRIM(COALESCE(v.comentario, ''))) <> ''
                         THEN 'C' ELSE '' END                            AS Ob,
                    CASE WHEN LTRIM(RTRIM(
                        COALESCE(v.adi_cod_1,'') + COALESCE(v.adi_cod_2,'') +
                        COALESCE(v.adi_cod_3,'') + COALESCE(v.adi_cod_4,'') +
                        COALESCE(v.adi_cod_5,''))) <> '' THEN 'A' ELSE '' END AS Ad,
                    CONVERT(varchar(5), v.hs_inicio, 108)                AS HIni,
                    CONVERT(varchar(5), v.hs_fin_apr, 108)               AS HFin,
                    CONVERT(varchar(5), v.hs_aviso, 108)                 AS HAvi,
                    CONVERT(varchar(5), v.hs_fin, 108)                   AS HCie,
                    v.cronogram2                                         AS UPr,
                    v.cronograma                                         AS UCb,
                    v.interno                                            AS UAs,
                    v.chequeo                                            AS Chq,
                    LTRIM(RTRIM(v.d_destino)) + ' a ' + LTRIM(RTRIM(v.h_destino)) AS Recorrido,
                    COALESCE(m.motivo, '')                               AS Motivo,
                    LEFT(LTRIM(RTRIM(v.id_vehicu2)), 4)                  AS Veh,
                    v.id_cliente                                         AS Cliente,
                    v.pax                                                AS Pax,
                    v.comentario                                         AS Comentario,
                    v.grupo                                              AS Grupo,
                    v.vuelo                                              AS Vuelo,
                    v.nombre_gui                                         AS Guia
                FROM viaje v
                LEFT JOIN viaje_motivo_cancela m
                       ON v.id_motivo = m.id AND m._deleted = 0
                WHERE v._deleted = 0
                  AND v.f_reserva = '{dia:yyyy-MM-dd}'
                  AND v.estado_via = 'CANCELADO'
                ORDER BY v.hs_inicio
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            string S(System.Data.Common.DbDataReader r, string c) { var i = r.GetOrdinal(c); return r.IsDBNull(i) ? "" : r.GetValue(i).ToString()!.Trim(); }
            int? N(System.Data.Common.DbDataReader r, string c) { var i = r.GetOrdinal(c); return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i)); }

            var result = new List<TraficoCanceladoRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new TraficoCanceladoRow(
                    IdViaje: Convert.ToInt32(reader.GetValue(reader.GetOrdinal("IdViaje"))),
                    Fecha: DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("Fecha"))),
                    Ob: S(reader, "Ob"),
                    Ad: S(reader, "Ad"),
                    HIni: S(reader, "HIni"),
                    HFin: S(reader, "HFin"),
                    HAvi: S(reader, "HAvi"),
                    HCie: S(reader, "HCie"),
                    UPr: S(reader, "UPr"),
                    UCb: S(reader, "UCb"),
                    UAs: N(reader, "UAs"),
                    Chq: N(reader, "Chq"),
                    Recorrido: S(reader, "Recorrido"),
                    Motivo: S(reader, "Motivo"),
                    Veh: S(reader, "Veh"),
                    Cliente: S(reader, "Cliente"),
                    Pax: N(reader, "Pax"),
                    Comentario: S(reader, "Comentario"),
                    Grupo: S(reader, "Grupo"),
                    Vuelo: S(reader, "Vuelo"),
                    Guia: S(reader, "Guia")
                ));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Detalle completo de un viaje — réplica de solo lectura del form FoxPro `trafico_zoom.scx`
    /// ("Zoom del Viaje"). Trae la fila de `viaje` más los lookups que hace el Init() del Fox:
    ///   - id_servici/2/3 → servicio.nombre (1°/2°/3° servicio)
    ///   - id_operado     → cliente.razon_soci (operador turístico)
    ///   - id_vehicu2     → vehiculo_tipo.nombre + capacidad (pax)
    ///   - id_grupo       → cliente_grupo.nombre / f_grupo_fi / f_grupo_fc (si grupo &lt;&gt; 'SIN GRUPO')
    /// Nombres de columna ya mapeados FoxPro→SQL (truncados a 10 chars en la réplica).
    /// </summary>
    public async Task<DetalleViajeDto?> GetDetalleViajeAsync(int idViaje, DateOnly? fReserva = null)
    {
        var key = $"detalle-viaje|{idViaje}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // LEFT JOIN a los catálogos para resolver nombres en una sola pasada.
            // ser1/ser2/ser3 = nombre del 1°/2°/3° servicio. op = razón social del operador.
            // vt = tipo de vehículo (+ capacidad). cg = grupo del cliente.
            //
            // PERFORMANCE: si recibimos la fecha de reserva (la fila de la planilla SIEMPRE
            // la conoce), filtramos también por f_reserva. Así la query hace un SEEK por el
            // índice existente ix_viaje_f_reserva (~1.000 lecturas, 0 ms CPU) en lugar de un
            // SCAN paralelo completo de viaje (521K filas → ~84.000 lecturas + 125 ms CPU que
            // saturaban el SQL Server 2012 en cada apertura del Zoom). id_viaje es único, así
            // que sumar f_reserva no cambia el resultado: solo evita el scan.
            var fResFiltro = fReserva is null
                ? ""
                : $"AND v.f_reserva = '{fReserva.Value:yyyy-MM-dd}'";
            var sql = $"""
                SELECT
                    v.id_viaje                                  AS IdViaje,
                    v.f_pedido                                  AS FPedido,
                    v.f_reserva                                 AS FReserva,
                    v.estado_via                                AS Estado,
                    v.origen                                    AS Origen,
                    v.hs_present                                AS HsPresent,
                    v.hs_inicio                                 AS HsInicio,
                    v.hs_fin_apr                                AS HsFinAprox,
                    v.hs_fin                                    AS HsFin,
                    v.duracion                                  AS Duracion,
                    v.hs_ini_rut                                AS HsIniRuta,
                    v.hs_fin_rut                                AS HsFinRuta,
                    v.id_viaje_i                                AS IdRuta,
                    v.odometro                                  AS OdometroIni,
                    v.odometro_f                                AS OdometroFin,
                    v.km_recorri                                AS KmRecorrido,
                    v.id_cliente                                AS IdCliente,
                    v.nombre_cli                                AS NombreCliente,
                    v.id_operado                                AS IdOperador,
                    op.razon_soci                               AS NombreOperador,
                    v.voucher_nr                                AS Voucher,
                    v.id_servici                                AS IdServicio1,
                    ser1.nombre                                 AS Servicio1,
                    v.id_servic2                                AS IdServicio2,
                    ser2.nombre                                 AS Servicio2,
                    v.id_servic3                                AS IdServicio3,
                    ser3.nombre                                 AS Servicio3,
                    v.cabecera                                  AS Cabecera,
                    v.km                                        AS Km,
                    v.id_vehicu2                                AS TipoVehiculo,
                    vt.pax                                      AS Capacidad,
                    v.pax                                       AS Pax,
                    v.agua                                      AS Agua,
                    v.hs                                        AS Horas,
                    v.vuelo                                     AS Vuelo,
                    v.nombre_gui                                AS Guia,
                    v.grupo                                     AS Grupo,
                    cg.nombre                                   AS NombreGrupo,
                    cg.f_grupo_fi                               AS FGrupoFin,
                    cg.f_grupo_fc                               AS FGrupoFactura,
                    v.d_destino                                 AS Desde,
                    v.h_destino                                 AS Hasta,
                    v.d_destino_                                AS Distrito,
                    v.recorrido_                                AS RecorridoCelular,
                    v.comentario                                AS Comentario,
                    v.[file]                                    AS Adjunto,
                    v.moneda_con                                AS MonedaLiquidar,
                    v.importe_co                                AS ImporteLiquidar,
                    v.descuento_                                AS PorcDescuento,
                    v.sin_cargo                                 AS BonificadoCliente,
                    v.moneda_pag                                AS MonedaPago,
                    v.importe_pa                                AS ImportePago,
                    v.sin_cargo_                                AS BonificadoEmpresa,
                    v.nombre_cho                                AS NombreChofer,
                    v.id_chofer                                 AS IdChofer,
                    v.id_chofer2                                AS IdChofer2,
                    v.tipo_chofe                                AS TipoChofer,
                    v.id_vehicul                                AS Vehiculo,
                    v.fletero                                   AS Fletero
                FROM viaje v
                    LEFT JOIN servicio       ser1 ON ser1.id_servici = v.id_servici AND ser1._deleted = 0
                    LEFT JOIN servicio       ser2 ON ser2.id_servici = v.id_servic2 AND ser2._deleted = 0
                    LEFT JOIN servicio       ser3 ON ser3.id_servici = v.id_servic3 AND ser3._deleted = 0
                    LEFT JOIN cliente        op   ON op.id_cliente   = v.id_operado AND op._deleted = 0
                    LEFT JOIN vehiculo_tipo  vt   ON vt.id_vehicul   = v.id_vehicu2 AND vt._deleted = 0
                    LEFT JOIN cliente_grupo  cg   ON cg.id           = v.id_grupo   AND cg._deleted = 0
                WHERE v._deleted = 0
                  {fResFiltro}
                  AND v.id_viaje = {idViaje}
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            string S(string col) { var i = reader.GetOrdinal(col); return reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString()!.Trim(); }
            int? N(string col) { var i = reader.GetOrdinal(col); return reader.IsDBNull(i) ? null : Convert.ToInt32(reader.GetValue(i)); }
            decimal D(string col) { var i = reader.GetOrdinal(col); return reader.IsDBNull(i) ? 0m : Convert.ToDecimal(reader.GetValue(i)); }
            bool B(string col) { var i = reader.GetOrdinal(col); return !reader.IsDBNull(i) && Convert.ToBoolean(reader.GetValue(i)); }
            DateTime? DT(string col) { var i = reader.GetOrdinal(col); return reader.IsDBNull(i) ? null : reader.GetDateTime(i); }
            DateOnly? DO(string col) { var d = DT(col); return d is null ? null : DateOnly.FromDateTime(d.Value); }

            var hsInicio = DT("HsInicio");
            var hsPresent = DT("HsPresent");

            return new DetalleViajeDto
            {
                IdViaje = N("IdViaje") ?? idViaje,
                FPedido = DO("FPedido"),
                FReserva = DO("FReserva"),
                Estado = S("Estado"),
                // origen='P' → "Transporte Personal"; cualquier otro → "Servicio Especial" (lógica Init() del Fox)
                TipoServicio = S("Origen") == "P" ? "Transporte Personal" : "Servicio Especial",
                HsInicio = hsInicio,
                Presentacion = DescribirPresentacion(hsInicio, hsPresent),
                HsFinAprox = DT("HsFinAprox"),
                HsFin = DT("HsFin"),
                Duracion = S("Duracion"),
                HsIniRuta = DT("HsIniRuta"),
                HsFinRuta = DT("HsFinRuta"),
                IdRuta = N("IdRuta"),
                OdometroIni = N("OdometroIni"),
                OdometroFin = N("OdometroFin"),
                KmRecorrido = N("KmRecorrido"),
                IdCliente = S("IdCliente"),
                NombreCliente = S("NombreCliente"),
                IdOperador = S("IdOperador"),
                NombreOperador = S("NombreOperador"),
                Voucher = N("Voucher"),
                IdServicio1 = S("IdServicio1"),
                Servicio1 = S("Servicio1"),
                IdServicio2 = S("IdServicio2"),
                Servicio2 = S("Servicio2"),
                IdServicio3 = S("IdServicio3"),
                Servicio3 = S("Servicio3"),
                Cabecera = S("Cabecera"),
                Km = N("Km"),
                TipoVehiculo = S("TipoVehiculo"),
                Capacidad = N("Capacidad"),
                Pax = N("Pax"),
                Agua = N("Agua"),
                Horas = N("Horas"),
                Vuelo = S("Vuelo"),
                Guia = S("Guia"),
                Grupo = S("Grupo"),
                NombreGrupo = S("NombreGrupo"),
                FGrupoFin = DO("FGrupoFin"),
                FGrupoFactura = DO("FGrupoFactura"),
                Desde = S("Desde"),
                Hasta = S("Hasta"),
                Distrito = S("Distrito"),
                RecorridoCelular = S("RecorridoCelular"),
                Comentario = S("Comentario"),
                Adjunto = S("Adjunto"),
                MonedaLiquidar = S("MonedaLiquidar"),
                ImporteLiquidar = D("ImporteLiquidar"),
                PorcDescuento = D("PorcDescuento"),
                BonificadoCliente = B("BonificadoCliente"),
                MonedaPago = S("MonedaPago"),
                ImportePago = D("ImportePago"),
                BonificadoEmpresa = B("BonificadoEmpresa"),
                NombreChofer = S("NombreChofer"),
                IdChofer = S("IdChofer"),
                IdChofer2 = S("IdChofer2"),
                TipoChofer = S("TipoChofer"),
                Vehiculo = S("Vehiculo"),
                Fletero = S("Fletero"),
            };
        });
    }

    /// <summary>
    /// Adicionales de un viaje (grilla del Fox `arma_grid_adicional`):
    /// SELECT nombre, cantidad FROM viaje_adicional WHERE id_viaje = ...
    /// </summary>
    public async Task<List<AdicionalViajeRow>> GetAdicionalesViajeAsync(int idViaje)
    {
        var key = $"adic-viaje|{idViaje}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT nombre, cantidad, precio
                FROM viaje_adicional
                WHERE _deleted = 0 AND id_viaje = {idViaje}
                ORDER BY nombre
                """;
            var result = new List<AdicionalViajeRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new AdicionalViajeRow(
                    Nombre: reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString()!.Trim(),
                    Cantidad: reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1)),
                    Precio: reader.IsDBNull(2) ? 0m : Convert.ToDecimal(reader.GetValue(2))
                ));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Traduce la diferencia (hs_inicio − hs_present) en segundos a la etiqueta de
    /// "Presentación" del Fox (Init): en hora / 5 / 15 / 30 / 45 min antes / 1 / 2 horas antes.
    /// </summary>
    private static string DescribirPresentacion(DateTime? hsInicio, DateTime? hsPresent)
    {
        if (hsPresent is null || hsInicio is null) return "en hora";
        var seg = Math.Round((hsInicio.Value - hsPresent.Value).TotalSeconds);
        return seg switch
        {
            300  => "5 minutos antes",
            900  => "15 minutos antes",
            1800 => "30 minutos antes",
            2700 => "45 minutos antes",
            3600 => "1 hora antes",
            7200 => "2 horas antes",
            <= 0 => "en hora",
            _    => $"{seg / 60:0} min antes"
        };
    }

    public async Task<List<VehiculoTipoDto>> GetVehiculosTipoAsync()
    {
        return await _cache.GetOrCreateAsync("vehiculo_tipo", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id_vehicul, nombre
                FROM vehiculo_tipo
                WHERE ISNULL(f_delete,'') = '' AND vende = 1
                ORDER BY id_vehicul
                """;
            var result = new List<VehiculoTipoDto>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(new VehiculoTipoDto(reader.GetString(0), reader.GetString(1)));
            return result;
        }) ?? new();
    }

    public async Task<List<BandaHorariaRow>> GetReservasPorBandaHorariaAsync(
        DateOnly desde,
        DateOnly hasta,
        IReadOnlyCollection<string> vehiculosSel)
    {
        var vehKey = vehiculosSel.Count == 0 ? "all" : string.Join(",", vehiculosSel.OrderBy(x => x));
        var key = $"rbh|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{vehKey}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var vehWhere = "";
            if (vehiculosSel.Count > 0)
            {
                var lista = string.Join(",", vehiculosSel.Select(v => $"'{v.Replace("'", "''")}'"));
                vehWhere = $"AND v.id_vehicul IN ({lista})";
            }

            // Clasifica cada viaje en su banda horaria usando CASE sobre CAST(hs_inicio AS TIME).
            // Fiel al FoxPro: excluye CANCELADO, excluye cliente NORTUR, solo origen='T'.
            var sql = $"""
                SELECT
                    v.f_reserva                         AS Fecha,
                    v.id_vehicul                        AS TipoVehiculo,
                    CAST(v.hs_inicio AS TIME)           AS HsInicio,
                    CASE
                        WHEN CAST(v.hs_inicio AS TIME) BETWEEN '00:00' AND '00:01' THEN '00:00-00:01'
                        WHEN CAST(v.hs_inicio AS TIME) BETWEEN '00:02' AND '06:29' THEN '00:02-06:29'
                        WHEN CAST(v.hs_inicio AS TIME) BETWEEN '06:30' AND '08:29' THEN '06:30-08:29'
                        WHEN CAST(v.hs_inicio AS TIME) BETWEEN '08:30' AND '14:00' THEN '08:30-14:00'
                        WHEN CAST(v.hs_inicio AS TIME) BETWEEN '14:01' AND '18:00' THEN '14:01-18:00'
                        WHEN CAST(v.hs_inicio AS TIME) BETWEEN '18:01' AND '23:59' THEN '18:01-23:59'
                        ELSE NULL
                    END                                 AS Banda,
                    COUNT(*)                            AS Reservas
                FROM viaje v
                WHERE v._deleted = 0
                  AND v.f_reserva BETWEEN '{desde:yyyy-MM-dd}' AND '{hasta:yyyy-MM-dd}'
                  AND v.origen = 'T'
                  AND v.estado_via <> 'CANCELADO'
                  AND v.id_cliente <> 'NORTUR'
                  AND v.hs_inicio IS NOT NULL
                  {vehWhere}
                GROUP BY v.f_reserva, v.id_vehicul, CAST(v.hs_inicio AS TIME)
                ORDER BY v.f_reserva, v.id_vehicul
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var result = new List<BandaHorariaRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var banda = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (banda is null) continue;
                result.Add(new BandaHorariaRow(
                    DateOnly.FromDateTime(reader.GetDateTime(0)),
                    reader.IsDBNull(1) ? "" : reader.GetString(1).Trim(),
                    banda,
                    reader.GetInt32(4)
                ));
            }
            return result;
        }) ?? new();
    }

    public async Task<TableroDto> GetTableroAsync()
    {
        return await _cache.GetOrCreateAsync("tablero", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // Réplica EXACTA de aviso_agenda.prg del FoxPro (verificado contra el .exe productivo,
            // 24/06/2026). Reglas no obvias que copiamos mal antes y acá están corregidas:
            //   1) Las ventanas de aviso salen de `parametro`, NO son 30 días fijos:
            //      aviso_veh (VTV) ≈ 7, aviso_mat (matafuego) ≈ 10, aviso_cho (chofer) ≈ 30.
            //   2) Vehículos: solo `f_delete` vacío + uso='PROPIO'. NO se filtra por `activo`.
            //   3) En FoxPro `Empty(fecha) <= Date()+n` da TRUE, así que un vencimiento SIN FECHA
            //      cuenta como VENCIDO (ej: 99 vehículos sin matafuego → entran en el total).
            //   4) El total del FoxPro = vencidos + por-vencer. Acá lo desglosamos en dos columnas
            //      excluyentes (Vencidos rojo / ProxVencer ámbar) para la UI; su suma = total FoxPro.
            //   Columnas reales (truncadas a 10 en la réplica): vehiculo.verificac2=VTV,
            //   vehiculo.vencimient=matafuego; chofer.registro_v=registro, registro_3=CNRT, registro_4=AEP.
            cmd.CommandText = """
                DECLARE @hoy   DATE = CAST(GETDATE() AS DATE);

                DECLARE @avVeh INT = (SELECT TOP 1 aviso_veh FROM parametro);
                DECLARE @avMat INT = (SELECT TOP 1 aviso_mat FROM parametro);
                DECLARE @avCho INT = (SELECT TOP 1 aviso_cho FROM parametro);
                SET @avVeh = ISNULL(@avVeh, 7);
                SET @avMat = ISNULL(@avMat, 10);
                SET @avCho = ISNULL(@avCho, 30);
                DECLARE @vtvLim DATE = DATEADD(day, @avVeh, @hoy);
                DECLARE @matLim DATE = DATEADD(day, @avMat, @hoy);
                DECLARE @choLim DATE = DATEADD(day, @avCho, @hoy);

                SELECT
                    @avVeh AS AvisoVeh, @avMat AS AvisoMat, @avCho AS AvisoCho,
                    -- Vehículos PROPIOS activos (sin f_delete). NO se mira `activo`.
                    SUM(CASE WHEN base=1 THEN 1 ELSE 0 END)                                                                  AS Vehiculos,
                    -- VTV (verificac2): vencido = sin fecha o < hoy; por vencer = hoy..hoy+avVeh
                    SUM(CASE WHEN base=1 AND (verificac2 IS NULL OR verificac2 <  @hoy)                            THEN 1 ELSE 0 END) AS VtvVencidos,
                    SUM(CASE WHEN base=1 AND  verificac2 IS NOT NULL AND verificac2 >= @hoy AND verificac2 <= @vtvLim THEN 1 ELSE 0 END) AS VtvProxVencer,
                    -- Matafuego (vencimient)
                    SUM(CASE WHEN base=1 AND (vencimient IS NULL OR vencimient <  @hoy)                            THEN 1 ELSE 0 END) AS MatVencidos,
                    SUM(CASE WHEN base=1 AND  vencimient IS NOT NULL AND vencimient >= @hoy AND vencimient <= @matLim THEN 1 ELSE 0 END) AS MatProxVencer
                FROM (
                    SELECT verificac2, vencimient,
                           CASE WHEN f_delete IS NULL AND uso = 'PROPIO' THEN 1 ELSE 0 END AS base
                    FROM vehiculo
                ) v;

                SELECT
                    SUM(CASE WHEN base=1 THEN 1 ELSE 0 END)                                                                  AS Choferes,
                    -- Registro (registro_v)
                    SUM(CASE WHEN base=1 AND (registro_v IS NULL OR registro_v <  @hoy)                            THEN 1 ELSE 0 END) AS RegVencidos,
                    SUM(CASE WHEN base=1 AND  registro_v IS NOT NULL AND registro_v >= @hoy AND registro_v <= @choLim THEN 1 ELSE 0 END) AS RegProxVencer,
                    -- CNRT (registro_3)
                    SUM(CASE WHEN base=1 AND (registro_3 IS NULL OR registro_3 <  @hoy)                            THEN 1 ELSE 0 END) AS CnrtVencidos,
                    SUM(CASE WHEN base=1 AND  registro_3 IS NOT NULL AND registro_3 >= @hoy AND registro_3 <= @choLim THEN 1 ELSE 0 END) AS CnrtProxVencer,
                    -- AEP (registro_4): el FoxPro NO cuenta los NULL acá (casi todos lo tienen vacío),
                    -- por eso AEP solo considera fechas cargadas.
                    SUM(CASE WHEN base=1 AND registro_4 IS NOT NULL AND registro_4 <  @hoy                          THEN 1 ELSE 0 END) AS AepVencidos,
                    SUM(CASE WHEN base=1 AND registro_4 IS NOT NULL AND registro_4 >= @hoy AND registro_4 <= @choLim THEN 1 ELSE 0 END) AS AepProxVencer
                FROM (
                    SELECT registro_v, registro_3, registro_4,
                           CASE WHEN (f_delete IS NULL OR LEN(LTRIM(RTRIM(CAST(f_delete AS nvarchar)))) = 0) THEN 1 ELSE 0 END AS base
                    FROM chofer
                ) c;
                """;

            using var reader = await cmd.ExecuteReaderAsync();

            var dto = new TableroDto();
            if (await reader.ReadAsync())
            {
                dto.AvisoVeh       = reader.GetInt32(0);
                dto.AvisoMat       = reader.GetInt32(1);
                dto.AvisoCho       = reader.GetInt32(2);
                dto.Vehiculos      = reader.GetInt32(3);
                dto.VtvVencidos    = reader.GetInt32(4);
                dto.VtvProxVencer  = reader.GetInt32(5);
                dto.MatVencidos    = reader.GetInt32(6);
                dto.MatProxVencer  = reader.GetInt32(7);
            }
            await reader.NextResultAsync();
            if (await reader.ReadAsync())
            {
                dto.Choferes       = reader.GetInt32(0);
                dto.RegVencidos    = reader.GetInt32(1);
                dto.RegProxVencer  = reader.GetInt32(2);
                dto.CnrtVencidos   = reader.GetInt32(3);
                dto.CnrtProxVencer = reader.GetInt32(4);
                dto.AepVencidos    = reader.GetInt32(5);
                dto.AepProxVencer  = reader.GetInt32(6);
            }
            return dto;
        }) ?? new();
    }

    // ════════════════════════════════════════════════════════════════════
    //  CLIENTES — ABM (solo lectura). Réplica de cliente.scx + cliente_abm.scx.
    //  Tabla `cliente` con dueño FoxPro: SOLO LECTURA desde Blazor (strangler).
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lista de clientes (réplica de la grilla de cliente.scx). "Egresado" =
    /// f_delete con valor. Sin paginar (la grilla FoxPro muestra todo con scroll).
    /// </summary>
    public async Task<List<ClienteListaRow>> GetClientesAsync()
    {
        return await _cache.GetOrCreateAsync("clientes-lista", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    RTRIM(ISNULL(id_cliente, '')) AS Codigo,
                    RTRIM(ISNULL(razon_soci, '')) AS RazonSocial,
                    RTRIM(ISNULL(telefono,   '')) AS Telefono,
                    RTRIM(ISNULL(celular,    '')) AS Celular,
                    RTRIM(ISNULL(domicilio,  '')) AS Domicilio,
                    RTRIM(ISNULL(domicilio_, '')) AS Nro,
                    RTRIM(ISNULL(domicilio2, '')) AS Piso,
                    RTRIM(ISNULL(domicilio3, '')) AS Depto,
                    RTRIM(ISNULL(localidad,  '')) AS Localidad,
                    ISNULL(descuento, 0)          AS Descuento,
                    RTRIM(ISNULL(contacto1,  '')) AS Contacto1,
                    RTRIM(ISNULL(contacto2,  '')) AS Contacto2,
                    f_delete                       AS FInhabilitacion
                FROM cliente
                WHERE _deleted = 0
                ORDER BY razon_soci
                """;
            var result = new List<ClienteListaRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                result.Add(new ClienteListaRow(
                    rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
                    rd.GetString(8), rd.GetDecimal(9), rd.GetString(10), rd.GetString(11),
                    rd.IsDBNull(12) ? null : DateOnly.FromDateTime(rd.GetDateTime(12))));
            }
            return result;
        }) ?? new();
    }

    /// <summary>Ficha completa de un cliente (réplica del form cliente_abm.scx
    /// en modo consulta), incluida la sección de correos y los rubros excluidos.</summary>
    public async Task<ClienteDetalleDto?> GetClienteDetalleAsync(string idCliente)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        var id = idCliente.Replace("'", "''");

        var det = new ClienteDetalleDto();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT TOP 1
                    RTRIM(ISNULL(id_cliente,'')), RTRIM(ISNULL(razon_soci,'')),
                    RTRIM(ISNULL(domicilio,'')),  RTRIM(ISNULL(domicilio_,'')),
                    RTRIM(ISNULL(domicilio2,'')), RTRIM(ISNULL(domicilio3,'')),
                    RTRIM(ISNULL(cpostal,'')),    RTRIM(ISNULL(localidad,'')),
                    RTRIM(ISNULL(provincia,'')),  RTRIM(ISNULL(telefono,'')),
                    RTRIM(ISNULL(celular,'')),    RTRIM(ISNULL(tipo_resp,'')),
                    RTRIM(ISNULL(ncuit,'')),      RTRIM(ISNULL(email,'')),
                    RTRIM(ISNULL(comentario,'')), f_delete,
                    ISNULL(descuento,0),          ISNULL(incremento,0),
                    RTRIM(ISNULL(empresa_fc,'')), RTRIM(ISNULL(ob_precio,'')),
                    RTRIM(ISNULL(id_lista_p,'')), RTRIM(ISNULL(cairo,'')),
                    RTRIM(ISNULL(fc_prefere,'')),
                    ISNULL(bus24,0),  ISNULL(pide_pax,0), ISNULL(voucher,0),
                    ISNULL(arsa,0),   ISNULL(plantilla_,0), ISNULL(envia_gps,0),
                    RTRIM(ISNULL(envia_gps_,'')), RTRIM(ISNULL(envia_gps2,'')),
                    f_create, f_modify
                FROM cliente
                WHERE id_cliente = '{id}' AND _deleted = 0
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;
            det.Codigo = rd.GetString(0);   det.RazonSocial = rd.GetString(1);
            det.Domicilio = rd.GetString(2); det.Nro = rd.GetString(3);
            det.Piso = rd.GetString(4);     det.Depto = rd.GetString(5);
            det.CPostal = rd.GetString(6);  det.Localidad = rd.GetString(7);
            det.Provincia = rd.GetString(8); det.Telefono = rd.GetString(9);
            det.Celular = rd.GetString(10); det.TipoResp = rd.GetString(11);
            det.Ncuit = rd.GetString(12);   det.Email = rd.GetString(13);
            det.Comentario = rd.GetString(14);
            det.FInhabilitacion = rd.IsDBNull(15) ? null : DateOnly.FromDateTime(rd.GetDateTime(15));
            det.Descuento = rd.GetDecimal(16); det.Incremento = rd.GetDecimal(17);
            det.EmpresaFc = rd.GetString(18); det.ObPrecio = rd.GetString(19);
            det.ListaPrecio = rd.GetString(20); det.Cairo = rd.GetString(21);
            det.FcPrefere = rd.GetString(22);
            det.Bus24 = rd.GetBoolean(23); det.PidePax = rd.GetBoolean(24);
            det.Voucher = rd.GetBoolean(25); det.Arsa = rd.GetBoolean(26);
            det.PlantillaDestinoEmpresa = rd.GetBoolean(27); det.EnviaGps = rd.GetBoolean(28);
            det.GpsTipo = rd.GetString(29); det.GpsHora = rd.GetString(30);
            det.FCreate = rd.IsDBNull(31) ? null : DateOnly.FromDateTime(rd.GetDateTime(31));
            det.FModify = rd.IsDBNull(32) ? null : DateOnly.FromDateTime(rd.GetDateTime(32));
        }

        // Resolver descripción de la empresa de facturación y del tipo de responsable
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT TOP 1 RTRIM(ISNULL(nombre, id_respons))
                FROM responsable_tipo
                WHERE id_respons = '{det.TipoResp.Replace("'", "''")}' AND _deleted = 0
                """;
            var o = await cmd.ExecuteScalarAsync();
            det.TipoRespDesc = o as string ?? det.TipoResp;
        }

        // Rubros de adicionales excluidos
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT RTRIM(ISNULL(rubro,''))
                FROM cliente_adicional_excluido
                WHERE id_cliente = '{id}' AND _deleted = 0
                ORDER BY rubro
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                if (!rd.IsDBNull(0)) det.RubrosExcluidos.Add(rd.GetString(0));
        }

        // Correos y contactos (email1..10 / contacto1..10 / cargo1..2)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT
                    RTRIM(ISNULL(email1,'')),  RTRIM(ISNULL(email2,'')),  RTRIM(ISNULL(email3,'')),
                    RTRIM(ISNULL(email4,'')),  RTRIM(ISNULL(email5,'')),  RTRIM(ISNULL(email6,'')),
                    RTRIM(ISNULL(email7,'')),  RTRIM(ISNULL(email8,'')),  RTRIM(ISNULL(email9,'')),
                    RTRIM(ISNULL(email10,'')),
                    RTRIM(ISNULL(contacto1,'')),  RTRIM(ISNULL(contacto2,'')),  RTRIM(ISNULL(contacto3,'')),
                    RTRIM(ISNULL(contacto4,'')),  RTRIM(ISNULL(contacto5,'')),  RTRIM(ISNULL(contacto6,'')),
                    RTRIM(ISNULL(contacto7,'')),  RTRIM(ISNULL(contacto8,'')),  RTRIM(ISNULL(contacto9,'')),
                    RTRIM(ISNULL(contacto10,'')),
                    RTRIM(ISNULL(cargo1,'')),  RTRIM(ISNULL(cargo2,''))
                FROM cliente WHERE id_cliente = '{id}' AND _deleted = 0
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            if (await rd.ReadAsync())
            {
                for (int i = 0; i < 10; i++)
                {
                    var email = rd.GetString(i);
                    var contacto = rd.GetString(10 + i);
                    var cargo = i == 0 ? rd.GetString(20) : i == 1 ? rd.GetString(21) : "";
                    if (!string.IsNullOrWhiteSpace(email) ||
                        !string.IsNullOrWhiteSpace(contacto) ||
                        !string.IsNullOrWhiteSpace(cargo))
                        det.Correos.Add(new ClienteCorreoRow(i + 1, contacto, cargo, email));
                }
            }
        }

        return det;
    }

    // ════════════════════════════════════════════════════════════════════
    //  CHOFERES — ABM (solo lectura). Réplica de chofer.scx + chofer_abm.scx.
    //  Tabla `chofer` con dueño FoxPro: SOLO LECTURA desde Blazor (strangler).
    //  Mapa de columnas truncadas → docs/logica-foxpro/CHOFER_ABM.md
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lista de choferes (réplica de la grilla de chofer.scx). "Egresado" =
    /// f_delete con valor. Sin paginar (la grilla FoxPro muestra todo con scroll).
    /// </summary>
    public async Task<List<ChoferListaRow>> GetChoferesAsync()
    {
        return await _cache.GetOrCreateAsync("choferes-lista", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    RTRIM(ISNULL(id_chofer,  '')) AS Codigo,
                    RTRIM(ISNULL(fletero,    '')) AS Fletero,
                    RTRIM(ISNULL(nombre,     '')) AS Nombre,
                    RTRIM(ISNULL(domicilio,  '')) AS Domicilio,
                    RTRIM(ISNULL(domicilio_, '')) AS Nro,
                    RTRIM(ISNULL(domicilio2, '')) AS Piso,
                    RTRIM(ISNULL(domicilio3, '')) AS Depto,
                    RTRIM(ISNULL(localidad,  '')) AS Localidad,
                    RTRIM(ISNULL(telefono,   '')) AS Telefono,
                    RTRIM(ISNULL(celular,    '')) AS Celular,
                    RTRIM(ISNULL(tdoc,       '')) AS Tdoc,
                    RTRIM(ISNULL(ndoc,       '')) AS Ndoc,
                    registro_v                     AS VtoRegistro,
                    registro_3                     AS VtoCnrt,
                    registro_4                     AS VtoAep,
                    f_delete                       AS FInhabilitacion
                FROM chofer
                WHERE _deleted = 0
                ORDER BY nombre
                """;
            var result = new List<ChoferListaRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                result.Add(new ChoferListaRow(
                    rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
                    rd.GetString(8), rd.GetString(9), rd.GetString(10), rd.GetString(11),
                    Fecha(rd, 12), Fecha(rd, 13), Fecha(rd, 14), Fecha(rd, 15)));
            }
            return result;
        }) ?? new();

        static DateOnly? Fecha(System.Data.Common.DbDataReader rd, int i) =>
            rd.IsDBNull(i) ? null : DateOnly.FromDateTime(rd.GetDateTime(i));
    }

    /// <summary>Ficha completa de un chofer (réplica del form chofer_abm.scx en
    /// modo consulta), con las 5 pestañas: Datos Personales, Condiciones Laborales,
    /// Vehículos, Domicilios y Teléfonos.</summary>
    public async Task<ChoferDetalleDto?> GetChoferDetalleAsync(string idChofer)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        var id = idChofer.Replace("'", "''");

        var det = new ChoferDetalleDto();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT TOP 1
                    RTRIM(ISNULL(id_chofer,'')), RTRIM(ISNULL(fletero,'')),
                    RTRIM(ISNULL(nombre,'')),    RTRIM(ISNULL(apellido,'')),
                    RTRIM(ISNULL(nombre1,'')),   RTRIM(ISNULL(nombre2,'')),
                    RTRIM(ISNULL(padre,'')),     RTRIM(ISNULL(madre,'')),
                    RTRIM(ISNULL(tdoc,'')),      RTRIM(ISNULL(ndoc,'')),
                    RTRIM(ISNULL(ncuil,'')),     RTRIM(ISNULL(ncuit,'')),
                    RTRIM(ISNULL(email,'')),     RTRIM(ISNULL(comentario,'')),
                    RTRIM(ISNULL(estado_civ,'')),RTRIM(ISNULL(lugar_naci,'')),
                    RTRIM(ISNULL(grupo_sang,'')),RTRIM(ISNULL(rh_pos_neg,'')),
                    f_nac, f_delete, f_create, f_modify,
                    RTRIM(ISNULL(registro_n,'')),registro_v,
                    RTRIM(ISNULL(registro_2,'')),registro_3, registro_4,
                    RTRIM(ISNULL(nextel,'')),    RTRIM(ISNULL(nextel_cel,'')),
                    -- Condiciones laborales
                    f_ingreso,
                    ISNULL(lunes,0), ISNULL(martes,0), ISNULL(miercoles,0),
                    ISNULL(jueves,0), ISNULL(viernes,0), ISNULL(sabado,0), ISNULL(domingo,0),
                    h_i_jornal, h_f_jornal, ISNULL(jornal,0), ISNULL(jornal_apl,0),
                    RTRIM(ISNULL(id_lista_p,'')), ISNULL(legajo,0), ISNULL(auditor,0),
                    RTRIM(ISNULL(ypf_pin,'')),   RTRIM(ISNULL(esso_pin,'')),
                    -- Domicilio DNI
                    RTRIM(ISNULL(domicilio,'')), RTRIM(ISNULL(domicilio_,'')),
                    RTRIM(ISNULL(domicilio2,'')),RTRIM(ISNULL(domicilio3,'')),
                    RTRIM(ISNULL(entre_call,'')),RTRIM(ISNULL(entre_cal2,'')),
                    RTRIM(ISNULL(cpostal,'')),   RTRIM(ISNULL(localidad,'')),
                    RTRIM(ISNULL(partido,'')),   RTRIM(ISNULL(provincia,'')),
                    -- Domicilio real
                    RTRIM(ISNULL(real_domic,'')),RTRIM(ISNULL(real_domi2,'')),
                    RTRIM(ISNULL(real_domi3,'')),RTRIM(ISNULL(real_domi4,'')),
                    RTRIM(ISNULL(real_domi5,'')),RTRIM(ISNULL(real_domi6,'')),
                    RTRIM(ISNULL(real_domi7,'')),RTRIM(ISNULL(real_domi8,'')),
                    RTRIM(ISNULL(real_domi9,'')),RTRIM(ISNULL(real_dom10,'')),
                    -- Teléfonos
                    RTRIM(ISNULL(telefono,'')),  RTRIM(ISNULL(celular,'')),
                    RTRIM(ISNULL(tel_1,'')), RTRIM(ISNULL(linea_1,'')), RTRIM(ISNULL(cel_1,'')),
                    RTRIM(ISNULL(tel_2,'')), RTRIM(ISNULL(linea_2,'')), RTRIM(ISNULL(cel_2,'')),
                    RTRIM(ISNULL(tel_3,'')), RTRIM(ISNULL(linea_3,'')), RTRIM(ISNULL(cel_3,'')),
                    RTRIM(ISNULL(tel_4,'')), RTRIM(ISNULL(linea_4,'')), RTRIM(ISNULL(cel_4,'')),
                    RTRIM(ISNULL(tel_5,'')), RTRIM(ISNULL(linea_5,'')), RTRIM(ISNULL(cel_5,''))
                FROM chofer
                WHERE id_chofer = '{id}' AND _deleted = 0
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;
            int i = 0;
            det.Codigo = rd.GetString(i++); det.Fletero = rd.GetString(i++);
            det.Nombre = rd.GetString(i++); det.Apellido = rd.GetString(i++);
            det.Nombre1 = rd.GetString(i++); det.Nombre2 = rd.GetString(i++);
            det.Padre = rd.GetString(i++); det.Madre = rd.GetString(i++);
            det.Tdoc = rd.GetString(i++); det.Ndoc = rd.GetString(i++);
            det.Ncuil = rd.GetString(i++); det.Ncuit = rd.GetString(i++);
            det.Email = rd.GetString(i++); det.Comentario = rd.GetString(i++);
            det.EstadoCivil = rd.GetString(i++); det.LugarNacimiento = rd.GetString(i++);
            det.GrupoSanguineo = rd.GetString(i++); det.RhPosNeg = rd.GetString(i++);
            det.FNac = D(rd, i++); det.FInhabilitacion = D(rd, i++);
            det.FCreate = D(rd, i++); det.FModify = D(rd, i++);
            det.RegistroNro = rd.GetString(i++); det.VtoRegistro = D(rd, i++);
            det.RegistroNroCnrt = rd.GetString(i++); det.VtoCnrt = D(rd, i++);
            det.VtoAep = D(rd, i++);
            det.Nextel = rd.GetString(i++); det.NextelCel = rd.GetString(i++);
            det.FIngreso = D(rd, i++);
            det.Lunes = rd.GetBoolean(i++); det.Martes = rd.GetBoolean(i++);
            det.Miercoles = rd.GetBoolean(i++); det.Jueves = rd.GetBoolean(i++);
            det.Viernes = rd.GetBoolean(i++); det.Sabado = rd.GetBoolean(i++);
            det.Domingo = rd.GetBoolean(i++);
            det.HInicioJornal = DT(rd, i++); det.HFinJornal = DT(rd, i++);
            det.Jornal = rd.GetInt64(i++); det.JornalAplica = rd.GetBoolean(i++);
            det.IdListaPrecio = rd.GetString(i++); det.Legajo = rd.GetInt64(i++);
            det.Auditor = rd.GetBoolean(i++);
            det.YpfPin = rd.GetString(i++); det.EssoPin = rd.GetString(i++);
            det.Domicilio = rd.GetString(i++); det.Nro = rd.GetString(i++);
            det.Piso = rd.GetString(i++); det.Depto = rd.GetString(i++);
            det.Entre1 = rd.GetString(i++); det.Entre2 = rd.GetString(i++);
            det.CPostal = rd.GetString(i++); det.Localidad = rd.GetString(i++);
            det.Partido = rd.GetString(i++); det.Provincia = rd.GetString(i++);
            det.RealDomicilio = rd.GetString(i++); det.RealNro = rd.GetString(i++);
            det.RealPiso = rd.GetString(i++); det.RealDepto = rd.GetString(i++);
            det.RealCPostal = rd.GetString(i++); det.RealLocalidad = rd.GetString(i++);
            det.RealPartido = rd.GetString(i++); det.RealProvincia = rd.GetString(i++);
            det.RealEntre1 = rd.GetString(i++); det.RealEntre2 = rd.GetString(i++);
            det.Telefono = rd.GetString(i++); det.Celular = rd.GetString(i++);
            for (int t = 0; t < 5; t++)
                det.Telefonos.Add(new ChoferTelefonoRow(
                    t + 1, rd.GetString(i++), rd.GetString(i++), rd.GetString(i++)));
        }

        // Vehículos asignados (tabla vehiculo_chofer + datos del vehículo)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT
                    RTRIM(ISNULL(vc.id_vehicul,'')) AS IdVehiculo,
                    ISNULL(vc.interno, 0)           AS Interno,
                    RTRIM(ISNULL(v.dominio,''))     AS Patente,
                    RTRIM(ISNULL(v.modelo,''))      AS Modelo
                FROM vehiculo_chofer vc
                LEFT JOIN vehiculo v ON v.id_vehicul = vc.id_vehicul AND v._deleted = 0
                WHERE vc.id_chofer = '{id}' AND vc._deleted = 0
                ORDER BY vc.interno
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                det.Vehiculos.Add(new ChoferVehiculoRow(
                    rd.GetString(0), rd.GetInt32(1), rd.GetString(2), rd.GetString(3)));
        }

        return det;

        static DateOnly? D(System.Data.Common.DbDataReader rd, int i) =>
            rd.IsDBNull(i) ? null : DateOnly.FromDateTime(rd.GetDateTime(i));
        static DateTime? DT(System.Data.Common.DbDataReader rd, int i) =>
            rd.IsDBNull(i) ? null : rd.GetDateTime(i);
    }

    /// <summary>
    /// Login con el flujo del FoxPro (login.scx): existencia → baja lógica
    /// (f_delete) → contraseña, cada caso con su mensaje propio. Devuelve
    /// acceso/nivel/operador para cargar los claims de la sesión.
    /// </summary>
    public async Task<LoginResultDto> LoginAsync(string usuario, string password)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT TOP 1
                   RTRIM(usuario),
                   RTRIM(ISNULL(password, '')),
                   RTRIM(ISNULL(acceso,   '')),
                   RTRIM(ISNULL(nivel,    '')),
                   ISNULL(operador, 0),
                   CASE WHEN f_delete IS NULL OR f_delete < '1900-01-01' THEN 0 ELSE 1 END
            FROM usuario
            WHERE usuario = '{usuario.Replace("'", "''")}'
              AND _deleted = 0
            """;
        await using var rd = await cmd.ExecuteReaderAsync();

        if (!await rd.ReadAsync())
            return LoginResultDto.Fallo("Usuario inexistente");
        if (rd.GetInt32(5) == 1)
            return LoginResultDto.Fallo("Usuario inhabilitado");
        if (!string.Equals(rd.GetString(1), password.Trim(), StringComparison.OrdinalIgnoreCase))
            return LoginResultDto.Fallo("Contraseña incorrecta");

        return new LoginResultDto(true, null,
            rd.GetString(0), rd.GetString(2), rd.GetString(3), rd.GetBoolean(4));
    }

    // ════════════════════════════════════════════════════════════════════
    //  Vehículos - Flota  (réplica de vehiculo.scx + vehiculo_abm.scx, solo lectura)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Lista de la flota (grilla de vehiculo.scx). Mismas 15 columnas que el
    /// FoxPro. Egresado = f_delete cargada O !activo (doble condición, distinto de chofer).</summary>
    public async Task<List<VehiculoListaRow>> GetVehiculosAsync()
    {
        return await _cache.GetOrCreateAsync("vehiculos-lista", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    RTRIM(ISNULL(id_vehicul, '')) AS IdVehiculo,
                    RTRIM(ISNULL(cronograma, '')) AS Cronograma,
                    RTRIM(ISNULL(fletero,    '')) AS Fletero,
                    RTRIM(ISNULL(marca_y_mo, '')) AS Marca,
                    RTRIM(ISNULL(color,      '')) AS Color,
                    RTRIM(ISNULL(dominio,    '')) AS Dominio,
                    RTRIM(ISNULL(poliza_nom, '')) AS PolizaNombre,
                    RTRIM(ISNULL(poliza_nro, '')) AS PolizaNro,
                    poliza_vto                     AS PolizaVto,
                    RTRIM(ISNULL(estado_cnr, '')) AS EstadoCnrt,
                    RTRIM(ISNULL(radicacion, '')) AS Radicacion,
                    RTRIM(ISNULL(tacografo_,'')) AS TacografoMarca,
                    RTRIM(ISNULL(tacografo2,'')) AS TacografoNro,
                    RTRIM(ISNULL(habilitaci, '')) AS HabilitacionNro,
                    habilitac2                     AS HabilitacionVto,
                    ISNULL(interno, 0)             AS Interno,
                    RTRIM(ISNULL(uso,        '')) AS Uso,
                    ISNULL(activo, 0)              AS Activo,
                    f_delete                       AS FBaja
                FROM vehiculo
                WHERE _deleted = 0
                ORDER BY interno, dominio
                """;
            var result = new List<VehiculoListaRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                result.Add(new VehiculoListaRow(
                    rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
                    Fecha(rd, 8), rd.GetString(9), rd.GetString(10), rd.GetString(11),
                    rd.GetString(12), rd.GetString(13), Fecha(rd, 14),
                    (int)rd.GetInt64(15), rd.GetString(16), rd.GetBoolean(17), Fecha(rd, 18)));
            }
            return result;
        }) ?? new();

        static DateOnly? Fecha(System.Data.Common.DbDataReader rd, int i) =>
            rd.IsDBNull(i) ? null : DateOnly.FromDateTime(rd.GetDateTime(i));
    }

    /// <summary>Ficha completa de un vehículo (réplica del form vehiculo_abm.scx en modo
    /// consulta), con las 6 pestañas: Datos Vehículo, Permisos, Dueños, Cubiertas, Tarjetas,
    /// Repuestos.</summary>
    public async Task<VehiculoDetalleDto?> GetVehiculoDetalleAsync(string idVehiculo)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        var id = idVehiculo.Replace("'", "''");

        var det = new VehiculoDetalleDto();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT TOP 1
                    RTRIM(ISNULL(id_vehicul,'')), RTRIM(ISNULL(dominio,'')),
                    RTRIM(ISNULL(marca_y_mo,'')), ISNULL(modelo,0),
                    ISNULL(interno,0),            RTRIM(ISNULL(cronograma,'')),
                    RTRIM(ISNULL(fletero,'')),    RTRIM(ISNULL(id_vehicu2,'')),
                    RTRIM(ISNULL(color,'')),      ISNULL(pax,0),
                    RTRIM(ISNULL(uso,'')),        ISNULL(activo,0),
                    RTRIM(ISNULL(chasis,'')),     RTRIM(ISNULL(motor,'')),
                    RTRIM(ISNULL(m_chasis,'')),   RTRIM(ISNULL(m_carrocer,'')),
                    RTRIM(ISNULL(mod_chasis,'')),
                    -- Seguros / CNRT / habilitaciones / vtos
                    RTRIM(ISNULL(poliza_nom,'')), RTRIM(ISNULL(poliza_nro,'')), poliza_vto,
                    RTRIM(ISNULL(estado_cnr,'')), RTRIM(ISNULL(radicacion,'')),
                    RTRIM(ISNULL(habilitaci,'')), habilitac2,
                    RTRIM(ISNULL(verificaci,'')), verificac2,
                    vencimient, puerto_aeo,
                    RTRIM(ISNULL(tacografo_,'')), RTRIM(ISNULL(tacografo2,'')),
                    RTRIM(ISNULL(nextel,'')),     RTRIM(ISNULL(tac_au_oes,'')),
                    RTRIM(ISNULL(tac_au_sol,'')), RTRIM(ISNULL(comentario,'')),
                    f_compra, f_venta, f_delete, f_create, f_modify,
                    -- GPS / comodidades / combustible
                    RTRIM(ISNULL(gps_activo,'')),
                    ISNULL(bano,0), ISNULL(bar,0), ISNULL(video,0), ISNULL(wifi,0),
                    ISNULL(litro_tanq,0), ISNULL(autonomia,0),
                    ISNULL(d_cons_pro,0), ISNULL(h_cons_pro,0), ISNULL(hasta100km,0),
                    -- Estado operativo (lo pisa Tráfico)
                    RTRIM(ISNULL(estado,'')),     RTRIM(ISNULL(nombre_cho,'')),
                    -- Cubiertas (r1..r7) — nros de serie por posición
                    ISNULL(r1,0), ISNULL(r2,0), ISNULL(r3,0), ISNULL(r4,0),
                    ISNULL(r5,0), ISNULL(r6,0), ISNULL(r7,0),
                    -- Tarjetas combustible
                    RTRIM(ISNULL(ypf_tar,'')), ypf_venc, RTRIM(ISNULL(ypf_pin,'')),
                    RTRIM(ISNULL(esso_tar,'')), esso_venc, RTRIM(ISNULL(esso_pin,''))
                FROM vehiculo
                WHERE id_vehicul = '{id}' AND _deleted = 0
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;
            int i = 0;
            det.Codigo = rd.GetString(i++); det.Dominio = rd.GetString(i++);
            det.Marca = rd.GetString(i++); det.Modelo = rd.GetInt32(i++);
            det.Interno = rd.GetInt64(i++); det.Cronograma = rd.GetString(i++);
            det.Fletero = rd.GetString(i++); det.IdTipo = rd.GetString(i++);
            det.Color = rd.GetString(i++); det.Pax = rd.GetInt64(i++);
            det.Uso = rd.GetString(i++); det.Activo = rd.GetBoolean(i++);
            det.Chasis = rd.GetString(i++); det.Motor = rd.GetString(i++);
            det.MarcaChasis = rd.GetString(i++); det.MarcaCarroceria = rd.GetString(i++);
            det.ModeloChasis = rd.GetString(i++);
            det.PolizaNombre = rd.GetString(i++); det.PolizaNro = rd.GetString(i++);
            det.PolizaVto = D(rd, i++);
            det.EstadoCnrt = rd.GetString(i++); det.Radicacion = rd.GetString(i++);
            det.HabilitacionNro = rd.GetString(i++); det.HabilitacionVto = D(rd, i++);
            det.VerificacionNro = rd.GetString(i++); det.VerificacionVto = D(rd, i++);
            det.VencimientoMat = D(rd, i++); det.PuertoAeoVto = D(rd, i++);
            det.TacografoMarca = rd.GetString(i++); det.TacografoNro = rd.GetString(i++);
            det.Nextel = rd.GetString(i++); det.TacAuOeste = rd.GetString(i++);
            det.TacAuSol = rd.GetString(i++); det.Comentario = rd.GetString(i++);
            det.FCompra = D(rd, i++); det.FVenta = D(rd, i++);
            det.FBaja = D(rd, i++); det.FCreate = D(rd, i++); det.FModify = D(rd, i++);
            det.GpsActivo = rd.GetString(i++);
            det.Bano = rd.GetBoolean(i++); det.Bar = rd.GetBoolean(i++);
            det.Video = rd.GetBoolean(i++); det.Wifi = rd.GetBoolean(i++);
            det.LitroTanque = rd.GetInt64(i++); det.Autonomia = rd.GetInt64(i++);
            det.ConsumoDesde = rd.GetInt64(i++); det.ConsumoHasta = rd.GetInt64(i++);
            det.Hasta100Km = rd.GetBoolean(i++);
            det.Estado = rd.GetString(i++); det.ConductorLogoneado = rd.GetString(i++);
            for (int c = 0; c < 7; c++)
            {
                var serie = rd.GetInt64(i++);
                det.Cubiertas.Add(new VehiculoCubiertaRow(c + 1, serie));
            }
            det.YpfTarjeta = rd.GetString(i++); det.YpfVenc = D(rd, i++); det.YpfPin = rd.GetString(i++);
            det.EssoTarjeta = rd.GetString(i++); det.EssoVenc = D(rd, i++); det.EssoPin = rd.GetString(i++);
        }

        // Pestaña Dueños (vehiculo_dueno + nombre desde dueno)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT
                    RTRIM(ISNULL(vd.id_dueno,'')) AS IdDueno,
                    RTRIM(ISNULL(d.nombre,''))     AS Nombre,
                    ISNULL(vd.porcentaje, 0)       AS Porcentaje
                FROM vehiculo_dueno vd
                LEFT JOIN dueno d ON d.id_dueno = vd.id_dueno AND d._deleted = 0
                WHERE vd.id_vehicul = '{id}' AND vd._deleted = 0
                ORDER BY vd.porcentaje DESC
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                det.Duenos.Add(new VehiculoDuenoRow(
                    rd.GetString(0), rd.GetString(1), rd.GetDecimal(2)));
        }

        // Pestaña Permisos (vehiculo_permiso + nombre desde permiso)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT
                    ISNULL(vp.id_permiso, 0)       AS IdPermiso,
                    RTRIM(ISNULL(p.nombre,''))     AS Nombre,
                    RTRIM(ISNULL(vp.nro_permis,'')) AS NroPermiso,
                    vp.f_venc                       AS FVenc,
                    vp.f_baja                       AS FBaja
                FROM vehiculo_permiso vp
                LEFT JOIN permiso p ON p.id_permiso = vp.id_permiso AND p._deleted = 0
                WHERE vp.id_vehicul = '{id}' AND vp._deleted = 0
                ORDER BY vp.id_permiso
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                det.Permisos.Add(new VehiculoPermisoRow(
                    (int)rd.GetInt64(0), rd.GetString(1), rd.GetString(2),
                    D(rd, 3), D(rd, 4)));
        }

        // Pestaña Repuestos (vehiculo_repuesto — vacía en la réplica hoy)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT
                    RTRIM(ISNULL(id_repuest,'')) AS IdRepuesto,
                    ISNULL(cantidad, 0)           AS Cantidad
                FROM vehiculo_repuesto
                WHERE id_vehicul = '{id}' AND _deleted = 0
                ORDER BY id_repuest
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                det.Repuestos.Add(new VehiculoRepuestoRow(rd.GetString(0), rd.GetDecimal(1)));
        }

        return det;

        static DateOnly? D(System.Data.Common.DbDataReader rd, int i) =>
            rd.IsDBNull(i) ? null : DateOnly.FromDateTime(rd.GetDateTime(i));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  FACTURACIÓN — Resumen de Liquidaciones (liquidacion_cliente.scx)
    //  Réplica fiel de solo lectura del browser maestro-detalle.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Grilla superior del Resumen de Liquidaciones (cabeceras). Replica el SELECT
    /// activo de <c>liquidacion_cliente.scx → arma_grid()</c>:
    /// <code>
    /// Subtotal  = ROUND((subtotal+extra)*t_cambio, 2)
    /// Exento    = adicional        (adicionales = exentos, sin IVA)
    /// TotalGral = ROUND((subtotal+extra)*t_cambio + iva + adicional, 2)
    /// Factura   = tcp-lcp-SUBSTR(ncp,1,4)-SUBSTR(ncp,5) si hay comprobante
    /// </code>
    /// CLIENTE une contra <c>cliente</c> (razon_social); PROVEEDOR contra
    /// <c>fletero</c> por <c>id_contrat</c>. Filtros: Nº exacto (gana sobre todo)
    /// o tipo + rango de <c>fecha</c> + cliente opcional.
    /// </summary>
    public async Task<List<LiquidacionRow>> GetLiquidacionesAsync(
        int nroLiquidacion, string tipo, DateOnly desde, DateOnly hasta, string? idCliente)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        // La razón social sale de cliente o fletero según el tipo. Resolvemos con
        // un LEFT JOIN a cada tabla y elegimos con COALESCE (SQL 2012 friendly).
        var t = tipo.Replace("'", "''");
        string where;
        if (nroLiquidacion > 0)
        {
            where = $"l.idliquidac = {nroLiquidacion}";
        }
        else
        {
            where = $"l.tipo = '{t}' AND l.fecha BETWEEN '{desde:yyyy-MM-dd}' AND '{hasta:yyyy-MM-dd}'";
            if (!string.IsNullOrWhiteSpace(idCliente))
                where += $" AND l.id_cliente = '{idCliente.Replace("'", "''")}'";
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                l.idliquidac                                              AS IdLiquidacion,
                RTRIM(ISNULL(l.tipo, ''))                                 AS Tipo,
                l.fecha                                                   AS Fecha,
                RTRIM(ISNULL(l.id_cliente, ''))                           AS Codigo,
                RTRIM(ISNULL(COALESCE(c.razon_soci, f.razon_soci, f.nombre), '')) AS RazonSocial,
                RTRIM(ISNULL(l.moneda, ''))                               AS Moneda,
                ROUND((ISNULL(l.subtotal,0) + ISNULL(l.extra,0)) * ISNULL(l.t_cambio,1), 2)         AS Subtotal,
                ISNULL(l.iva, 0)                                          AS Iva,
                ISNULL(l.adicional, 0)                                    AS Exento,
                ROUND((ISNULL(l.subtotal,0) + ISNULL(l.extra,0)) * ISNULL(l.t_cambio,1)
                      + ISNULL(l.iva,0) + ISNULL(l.adicional,0), 2)       AS TotalGral,
                l.fcomp                                                   AS Fcomp,
                CASE WHEN RTRIM(ISNULL(l.tcp,'')) <> ''
                     THEN RTRIM(l.tcp) + '-' + RTRIM(ISNULL(l.lcp,'')) + '-'
                          + SUBSTRING(ISNULL(l.ncp,''),1,4) + '-' + SUBSTRING(ISNULL(l.ncp,''),5,8)
                     ELSE '' END                                          AS Factura,
                l.f_pago                                                  AS FPago,
                RTRIM(ISNULL(l.forma_pago, ''))                           AS FormaPago,
                RTRIM(ISNULL(l.banco, ''))                                AS Banco,
                ISNULL(l.n_pago, 0)                                       AS NPago,
                ISNULL(l.retencion_, 0)                                   AS RetIva,
                ISNULL(l.retencion2, 0)                                   AS RetIibb,
                ISNULL(l.retencion3, 0)                                   AS RetSuss,
                ISNULL(l.pago, 0)                                         AS Pago
            FROM liquidacion l
                LEFT JOIN cliente c ON l.id_cliente = c.id_cliente AND c._deleted = 0
                LEFT JOIN fletero f ON l.id_cliente = f.id_contrat AND f._deleted = 0
            WHERE l._deleted = 0 AND {where}
            ORDER BY l.fecha, l.idliquidac
            """;

        var result = new List<LiquidacionRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            result.Add(new LiquidacionRow(
                rd.GetInt32(0), rd.GetString(1), Dt(rd, 2), rd.GetString(3), rd.GetString(4),
                rd.GetString(5), rd.GetDecimal(6), rd.GetDecimal(7), rd.GetDecimal(8),
                rd.GetDecimal(9), Dt(rd, 10), rd.GetString(11), Dt(rd, 12), rd.GetString(13),
                rd.GetString(14), rd.GetInt64(15), rd.GetDecimal(16), rd.GetDecimal(17),
                rd.GetDecimal(18), rd.GetDecimal(19)));
        }
        return result;

        static DateOnly? Dt(System.Data.Common.DbDataReader rd, int i) =>
            rd.IsDBNull(i) ? null : DateOnly.FromDateTime(rd.GetDateTime(i));
    }

    /// <summary>Grilla inferior: detalle (<c>liquidacion_detalle</c>) de la liquidación
    /// seleccionada. Una fila por servicio/adicional. NO incluye el ajuste global de
    /// cabecera (vive solo en <c>liquidacion</c>).</summary>
    public async Task<List<LiquidacionDetalleRow>> GetLiquidacionDetalleAsync(int idLiquidacion)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                d.id                              AS Id,
                ISNULL(d.id_viaje, 0)             AS IdViaje,
                RTRIM(ISNULL(d.tipo, ''))         AS Tipo,
                RTRIM(ISNULL(d.id_adicion, ''))   AS IdAdicional,
                RTRIM(ISNULL(d.nombre, ''))       AS Nombre,
                RTRIM(ISNULL(d.moneda, ''))       AS Moneda,
                ISNULL(d.cantidad, 0)             AS Cantidad,
                ISNULL(d.precio, 0)               AS Precio,
                ISNULL(d.importe, 0)              AS Importe,
                RTRIM(ISNULL(d.d_destino_, ''))   AS DDestinoProv,
                ISNULL(d.km_recorri, 0)           AS KmRecorrido,
                ISNULL(d.descuento, 0)            AS Descuento,
                ISNULL(d.incremento, 0)           AS Incremento,
                ISNULL(d.id_viaje_i, 0)           AS IdViajeInt
            FROM liquidacion_detalle d
            WHERE d.idliquidac = {idLiquidacion} AND d._deleted = 0
            ORDER BY d.id
            """;
        var result = new List<LiquidacionDetalleRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            result.Add(new LiquidacionDetalleRow(
                rd.GetInt32(0), rd.GetInt64(1), rd.GetString(2), rd.GetString(3),
                rd.GetString(4), rd.GetString(5), rd.GetInt64(6), rd.GetDecimal(7),
                rd.GetDecimal(8), rd.GetString(9), rd.GetInt64(10), rd.GetDecimal(11),
                rd.GetDecimal(12), rd.GetInt64(13)));
        }
        return result;
    }

    /// <summary>
    /// Cabecera CRUDA de UNA liquidación, para reconstruir la solapa "Liquidacion" del
    /// form <c>facturacion_cliente_nueva.scx</c> (visor de solo lectura "Liquidación a
    /// Clientes"). A diferencia de <see cref="GetLiquidacionesAsync"/> —que colapsa
    /// <c>(subtotal+extra)*t_cambio</c> en una sola columna— acá traemos los campos sin
    /// colapsar tal como los grabó <c>bGraba</c>:
    /// <list type="bullet">
    ///   <item><c>subtotal</c> = nSubtotal_ajustado = total NETO de servicios → caja "Subtotal".</item>
    ///   <item><c>extra</c> = nExtra_ajustado = ajuste global manual (normalmente 0) → caja "Extras".</item>
    /// </list>
    /// El desglose superior "Totales por servicios" (Subtotal del Servicio / Extras /
    /// Descuento / Incremento) NO está en la cabecera: se reconstruye en la página desde
    /// <c>liquidacion_detalle</c> (filas "HORA A DISPOSICION" = extras; resto = subtotal).
    /// </summary>
    public async Task<LiquidacionCabeceraDto?> GetLiquidacionCabeceraAsync(int idLiquidacion)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        // CAST a decimal por si alguna columna numérica de FoxPro vino como float en la réplica.
        cmd.CommandText = $"""
            SELECT
                l.idliquidac                                              AS IdLiquidacion,
                RTRIM(ISNULL(l.tipo, ''))                                 AS Tipo,
                l.fecha                                                   AS Fecha,
                RTRIM(ISNULL(l.id_cliente, ''))                           AS Codigo,
                RTRIM(ISNULL(COALESCE(c.razon_soci, f.razon_soci, f.nombre), '')) AS RazonSocial,
                RTRIM(ISNULL(l.moneda, ''))                               AS Moneda,
                CAST(ISNULL(l.subtotal, 0) AS decimal(18,4))              AS Subtotal,
                CAST(ISNULL(l.extra, 0)    AS decimal(18,4))              AS Extra,
                CAST(ISNULL(l.t_cambio, 1) AS decimal(18,4))              AS TCambio,
                CAST(ISNULL(l.adicional, 0) AS decimal(18,4))             AS Adicional,
                CAST(ISNULL(l.iva, 0)      AS decimal(18,4))              AS Iva,
                CAST(ISNULL(l.piva, 0)     AS decimal(18,4))              AS Piva,
                RTRIM(ISNULL(l.motivo, ''))                               AS Motivo,
                CAST(ISNULL(l.total, 0)    AS decimal(18,4))              AS Total
            FROM liquidacion l
                LEFT JOIN cliente c ON l.id_cliente = c.id_cliente AND c._deleted = 0
                LEFT JOIN fletero f ON l.id_cliente = f.id_contrat AND f._deleted = 0
            WHERE l._deleted = 0 AND l.idliquidac = {idLiquidacion}
            """;
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new LiquidacionCabeceraDto(
            rd.GetInt32(0), rd.GetString(1),
            rd.IsDBNull(2) ? null : DateOnly.FromDateTime(rd.GetDateTime(2)),
            rd.GetString(3), rd.GetString(4), rd.GetString(5),
            rd.GetDecimal(6), rd.GetDecimal(7), rd.GetDecimal(8), rd.GetDecimal(9),
            rd.GetDecimal(10), rd.GetDecimal(11), rd.GetString(12), rd.GetDecimal(13));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  FACTURACIÓN — Liquidación a Clientes, modo "POR ESTADO" (facturacion_cliente_nueva.scx)
    //  Réplica de la BÚSQUEDA bBusca del form: el árbol NO sale de las liquidaciones
    //  ya grabadas sino de los VIAJES pendientes de liquidar (cliente → grupo),
    //  igual que el FoxPro. Es solo lectura: mostramos qué falta liquidar, sin
    //  valorizar (el motor de tarifas no está migrado — strangler).
    //
    //  Universo POR ESTADO (doc FACTURACION_LIQUIDACION.md §3.1):
    //    estado_via = 'FINALIZADO' AND f_grupo_fi < HOY  (grupo ya vencido)
    //    excluye el cliente de prueba (parametro.id_cliente, hoy = 'NORTUR').
    //  Universo POR FECHA: ídem pero f_grupo_fi BETWEEN desde AND hasta.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Viajes pendientes de liquidar agrupados cliente → grupo, tal como los arma
    /// la búsqueda (<c>bBusca</c>) de <c>facturacion_cliente_nueva.scx</c>. Replica
    /// el árbol de la pantalla "Liquidación a Clientes" del FoxPro.
    /// <para>
    /// <paramref name="porFecha"/> = false → modo POR ESTADO (default del FoxPro,
    /// el más usado): trae todos los grupos vencidos (<c>f_grupo_fi &lt; HOY</c>),
    /// ignora el rango de fechas. true → modo POR FECHA: filtra
    /// <c>f_grupo_fi BETWEEN desde AND hasta</c>.
    /// </para>
    /// Excluye el cliente de prueba (<c>parametro.id_cliente</c>). Solo lectura.
    /// </summary>
    public async Task<List<ViajePendienteRow>> GetViajesPendientesLiquidarAsync(
        bool porFecha, DateOnly desde, DateOnly hasta)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        // Candado temporal del grupo. POR ESTADO: vencido (f_grupo_fi < HOY).
        // POR FECHA: el fin del grupo cae en el rango pedido.
        var candado = porFecha
            ? $"v.f_grupo_fi BETWEEN '{desde:yyyy-MM-dd}' AND '{hasta:yyyy-MM-dd}'"
            : "v.f_grupo_fi < CAST(GETDATE() AS date)";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                CAST(ISNULL(v.id_viaje, 0)   AS bigint)        AS IdViaje,
                CAST(ISNULL(v.id_viaje_i, 0) AS bigint)        AS IdViajeInt,
                RTRIM(ISNULL(v.id_cliente, ''))                AS IdCliente,
                RTRIM(ISNULL(v.nombre_cli, ''))                AS Cliente,
                RTRIM(ISNULL(NULLIF(RTRIM(v.grupo), ''), 'SIN GRUPO')) AS Grupo,
                v.f_reserva                                    AS FReserva,
                v.hs_inicio                                    AS HsInicio,
                v.hs_fin                                       AS HsFin,
                RTRIM(ISNULL(v.id_servici, ''))                AS Servicio,
                RTRIM(ISNULL(v.d_destino, ''))                 AS Destino,
                RTRIM(ISNULL(v.id_vehicul, ''))                AS Vehiculo,
                RTRIM(ISNULL(v.nombre_cho, ''))                AS Chofer,
                CAST(ISNULL(v.pax, 0)        AS int)           AS Pax,
                RTRIM(ISNULL(v.cabecera, ''))                  AS Cabecera,
                CAST(ISNULL(v.km_recorri, 0) AS bigint)        AS KmRecorrido,
                CAST(ISNULL(v.importe_co, 0) AS decimal(18,2)) AS ImporteConvenido,
                RTRIM(ISNULL(v.moneda_con, ''))                AS MonedaConvenida,
                v.f_grupo_fi                                   AS FinGrupo
            FROM viaje v
            WHERE v._deleted = 0
              AND RTRIM(v.estado_via) = 'FINALIZADO'
              AND {candado}
              AND RTRIM(ISNULL(v.id_cliente, '')) <>
                  (SELECT TOP 1 RTRIM(ISNULL(id_cliente, '')) FROM parametro)
            ORDER BY v.nombre_cli, v.grupo, v.hs_inicio
            """;

        var result = new List<ViajePendienteRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            result.Add(new ViajePendienteRow(
                rd.GetInt64(0), rd.GetInt64(1), rd.GetString(2), rd.GetString(3),
                rd.GetString(4), Dt(rd, 5), Dt(rd, 6), Dt(rd, 7), rd.GetString(8),
                rd.GetString(9), rd.GetString(10), rd.GetString(11), rd.GetInt32(12),
                rd.GetString(13), rd.GetInt64(14), rd.GetDecimal(15), rd.GetString(16),
                Dt(rd, 17)));
        }
        return result;

        static DateTime? Dt(System.Data.Common.DbDataReader rd, int i) =>
            rd.IsDBNull(i) ? null : rd.GetDateTime(i);
    }

    /// <summary>
    /// Solapa "Adicionales" de "Liquidación a Clientes": los <c>viaje_adicional</c> de
    /// los viajes pendientes de un grupo (cliente + grupo), tal como la grilla del
    /// FoxPro (<c>arma_adicional</c>). Réplica de solo lectura, CON precio valorizado.
    /// <para>
    /// <c>viaje_adicional.precio</c> viene en 0 en la réplica: el precio que muestra el
    /// FoxPro lo VALORIZA en vivo (<c>obtiene_adicional</c>) buscando en
    /// <c>adicional_lista_precio</c> por <b>adicional × tipo de vehículo × vigencia</b>.
    /// Acá replicamos esa búsqueda con un <c>OUTER APPLY</c> (verificado contra el grupo
    /// GATE1/SAM-02: AGUA×30×1200=36000, total 242.400 = idéntico al FoxPro).
    /// </para>
    /// El precio se trae SOLO para los adicionales <c>ABONA</c>; los <c>EXCLUIDO</c>
    /// (rubro en <c>cliente_adicional_excluido</c>) no se cobran → precio/total 0,
    /// igual que el FoxPro. El <c>Total adicionales</c> de la solapa suma solo ABONA.
    /// <para>
    /// ⚠️ Esto es una porción acotada del motor de tarifas (solo adicionales). La
    /// valorización de SERVICIOS (cascada cabecera/servicio × modo_fac, horas extra) NO
    /// está migrada — esa sigue en el FoxPro y por eso la solapa Liquidacion no calcula.
    /// </para>
    /// </summary>
    public async Task<List<ViajeAdicionalRow>> GetAdicionalesGrupoAsync(
        string idCliente, string grupo, bool porFecha, DateOnly desde, DateOnly hasta)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var candado = porFecha
            ? $"v.f_grupo_fi BETWEEN '{desde:yyyy-MM-dd}' AND '{hasta:yyyy-MM-dd}'"
            : "v.f_grupo_fi < CAST(GETDATE() AS date)";
        var cli = idCliente.Replace("'", "''");
        // El nodo "SIN GRUPO" del árbol = viajes con grupo vacío.
        var grpFiltro = grupo == "SIN GRUPO"
            ? "RTRIM(ISNULL(v.grupo, '')) = ''"
            : $"RTRIM(ISNULL(v.grupo, '')) = '{grupo.Replace("'", "''")}'";

        using var cmd = conn.CreateCommand();
        // OUTER APPLY al tarifario: precio del adicional para el TIPO de vehículo del
        // viaje (v.id_vehicul guarda el TIPO: BUS/MINI/…), vigente a la fecha del viaje.
        // Si hay varias vigencias que cubren la fecha, gana la más reciente (fdesdevg DESC).
        cmd.CommandText = $"""
            SELECT
                CAST(ISNULL(va.id_viaje, 0) AS bigint)         AS IdViaje,
                RTRIM(ISNULL(va.id_adicion, ''))               AS IdAdicional,
                RTRIM(ISNULL(va.nombre, ''))                   AS Nombre,
                CAST(ISNULL(va.cantidad, 0) AS bigint)         AS Cantidad,
                CASE WHEN cae.id IS NOT NULL THEN 'EXCLUIDO' ELSE 'ABONA' END AS Estado,
                v.hs_inicio                                    AS Inicio,
                RTRIM(ISNULL(v.id_vehicul, ''))                AS Vehiculo,
                RTRIM(ISNULL(v.id_servici, ''))                AS Servicio,
                RTRIM(ISNULL(v.cabecera, ''))                  AS Cabecera,
                RTRIM(ISNULL(v.d_destino, ''))                 AS Destino,
                -- precio/total solo si ABONA (los EXCLUIDO no se cobran)
                CASE WHEN cae.id IS NOT NULL THEN CAST(0 AS decimal(18,2))
                     ELSE CAST(ISNULL(pe.precio, 0) AS decimal(18,2)) END AS Precio,
                CASE WHEN cae.id IS NOT NULL THEN CAST(0 AS decimal(18,2))
                     ELSE CAST(ISNULL(va.cantidad,0) * ISNULL(pe.precio,0) AS decimal(18,2)) END AS Total,
                CASE WHEN cae.id IS NULL AND pe.precio IS NULL THEN CAST(1 AS bit)
                     ELSE CAST(0 AS bit) END AS SinTarifa
            FROM viaje_adicional va
                INNER JOIN viaje v ON v.id_viaje = va.id_viaje AND v._deleted = 0
                LEFT JOIN adicional a ON RTRIM(a.id_adicion) = RTRIM(va.id_adicion) AND a._deleted = 0
                LEFT JOIN cliente_adicional_excluido cae
                       ON cae._deleted = 0
                      AND RTRIM(cae.id_cliente) = '{cli}'
                      AND RTRIM(cae.rubro) = RTRIM(a.rubro)
                OUTER APPLY (
                    SELECT TOP 1 alp.precio
                    FROM adicional_lista_precio alp
                    WHERE alp._deleted = 0
                      AND RTRIM(alp.id_adicion) = RTRIM(va.id_adicion)
                      AND RTRIM(alp.id_vehicul) = RTRIM(v.id_vehicul)
                      AND v.hs_inicio >= alp.fdesdevg AND v.hs_inicio <= alp.fhastavg
                    ORDER BY alp.fdesdevg DESC
                ) tar
                -- Precio efectivo (obtiene_adicional): si el adicional trae precio propio
                -- cargado (> 0) se respeta ESE; si viene en 0 se busca en el tarifario.
                -- Si no hay ninguno → NULL (sin tarifa). Replica fiel del FoxPro.
                CROSS APPLY (
                    SELECT precio = CASE WHEN ISNULL(va.precio, 0) > 0 THEN va.precio
                                         ELSE tar.precio END
                ) pe
            WHERE va._deleted = 0
              AND RTRIM(v.estado_via) = 'FINALIZADO'
              AND {candado}
              AND RTRIM(ISNULL(v.id_cliente, '')) = '{cli}'
              AND {grpFiltro}
            ORDER BY va.id_viaje, va.id_adicion
            """;

        var result = new List<ViajeAdicionalRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            result.Add(new ViajeAdicionalRow(
                rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetInt64(3),
                rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetDateTime(5),
                rd.GetString(6), rd.GetString(7), rd.GetString(8), rd.GetString(9),
                rd.GetDecimal(10), rd.GetDecimal(11), rd.GetBoolean(12)));
        }
        return result;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  FACTURACIÓN — Motor de valorización de SERVICIOS (arma_servicio)
    //  Réplica fiel del motor de tarifas de facturacion_cliente_nueva.scx.
    //  Calcula en vivo el importe de cada viaje del grupo (solo lectura; NO graba).
    //
    //  Validado contra 8.656 viajes históricos de liquidaciones reales: 99,4% al
    //  peso (el resto = servicios cuya tarifa fue cambiada retroactivamente; para
    //  viajes PENDIENTES con la tarifa actual ese caso no aplica). Detalle del
    //  relevamiento en docs/logica-foxpro/FACTURACION_LIQUIDACION.md §3.2.
    //
    //  Cascada de precio por viaje (gana el primero) — arma_servicio:
    //    1. importe_co > 0  → ese importe (× descuento_ % si lo hay)
    //    2. sin_cargo       → 0
    //    3. cabecera + cliente.fc_prefere='C' → tarifa de la cabecera ("C")
    //       si no, el 1er servicio (id_servici) según servicio.modo_fac:
    //         S → tarifa directa
    //         K → tarifa × km   (km = km_recorri, o km del servicio si es 0)
    //         H → tarifa + HORAS EXTRA si la duración real supera la teórica
    //  Descuento/incremento en cascada: descuento_ % → cliente.descuento %
    //    → cliente.incremento %  (el nivel "período" cliente_descuento está vacío).
    //
    //  ⚠️ Trampas replicadas EXACTO del FoxPro (sin ellas no cuadra):
    //   · Duración teórica modo H: el FoxPro suma minutos_du como HORAS (×3600 en
    //     vez de ×60) — es un bug, pero hay que respetarlo. Como casi todos los
    //     servicios tienen minutos_du=0, en la práctica casi no afecta.
    //   · Horas extra: solo si dur_real > dur_teórica. Fracción con parametro
    //     fraccion_h: minutos entre fraccion_h y 30 → +media hora; >30 → +hora.
    //     Tarifa de la hora extra = servicio parametro.cliente_ad ('HORA ADICIONAL').
    //   · obtiene_tarifa busca lista_precio por lista del cliente × servicio ×
    //     TIPO de vehículo (v.id_vehicul) × vigencia (gana la más reciente).
    //     Sin tarifa → precio -1 (viaje marcado SinTarifa, fila amarilla).
    //  ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Valoriza en vivo los viajes pendientes de un grupo (cliente + grupo),
    /// replicando el motor de tarifas <c>arma_servicio</c> de
    /// <c>facturacion_cliente_nueva.scx</c>. Solo lectura — NO escribe en
    /// <c>liquidacion</c>/<c>liquidacion_detalle</c> (regla strangler).
    /// <para>
    /// Devuelve una fila por viaje con su importe de servicio, horas extra,
    /// descuento/incremento y la moneda de la lista. Los viajes sin tarifa quedan
    /// con <see cref="ViajeValorizadoRow.SinTarifa"/> = true (el FoxPro los pinta
    /// de amarillo y bloquea el grabado).
    /// </para>
    /// Solo se valoriza el 1er servicio (<c>id_servici</c>): los servicios 2º/3º
    /// son rarísimos y se suman aparte si existen (ver <c>imp_serv_2/3</c> del form).
    /// </summary>
    public async Task<List<ViajeValorizadoRow>> ValorizarGrupoAsync(
        string idCliente, string grupo, bool porFecha, DateOnly desde, DateOnly hasta)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var candado = porFecha
            ? $"v.f_grupo_fi BETWEEN '{desde:yyyy-MM-dd}' AND '{hasta:yyyy-MM-dd}'"
            : "v.f_grupo_fi < CAST(GETDATE() AS date)";
        var cli = idCliente.Replace("'", "''");
        var grpFiltro = grupo == "SIN GRUPO"
            ? "RTRIM(ISNULL(v.grupo, '')) = ''"
            : $"RTRIM(ISNULL(v.grupo, '')) = '{grupo.Replace("'", "''")}'";

        using var cmd = conn.CreateCommand();
        // Parámetros del motor: cliente_ad (servicio hora extra) y fraccion_h.
        // Se leen de la tabla parametro (1 fila), igual que el Init del form.
        cmd.CommandText = $"""
            DECLARE @cliente_ad varchar(30), @fraccion int;
            SELECT TOP 1 @cliente_ad = RTRIM(ISNULL(cliente_ad,'')),
                         @fraccion   = ISNULL(fraccion_h, 25)
            FROM parametro;

            SELECT
                CAST(ISNULL(v.id_viaje, 0)   AS bigint)        AS IdViaje,
                CAST(ISNULL(v.id_viaje_i, 0) AS bigint)        AS IdViajeInt,
                v.hs_inicio                                    AS HsInicio,
                v.hs_fin                                       AS HsFin,
                RTRIM(ISNULL(v.id_servici, ''))                AS Servicio,
                RTRIM(ISNULL(v.cabecera, ''))                  AS Cabecera,
                RTRIM(ISNULL(v.id_vehicul, ''))                AS Vehiculo,
                RTRIM(ISNULL(v.d_destino, ''))                 AS Destino,
                CAST(ISNULL(v.pax, 0)        AS int)           AS Pax,
                CAST(ISNULL(g.km_efec, 0)    AS bigint)        AS Km,
                RTRIM(ISNULL(g.moneda, ''))                    AS Moneda,
                CAST(g.imp_base  AS decimal(18,2))             AS ImpServicio,
                CAST(g.imp_extra AS decimal(18,2))             AS ImpExtra,
                CAST(g.imp_desc  AS decimal(18,2))             AS ImpDescuento,
                CAST(g.imp_incr  AS decimal(18,2))             AS ImpIncremento,
                CASE WHEN g.imp_base = -1 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS SinTarifa
            FROM viaje v
                CROSS APPLY (
                    SELECT
                        c.fc_prefere, c.id_lista_p AS cli_lista,
                        c.descuento  AS cli_desc, c.incremento AS cli_incr
                    FROM cliente c WHERE c.id_cliente = v.id_cliente AND c._deleted = 0
                ) c
                LEFT JOIN servicio s ON RTRIM(s.id_servici) = RTRIM(v.id_servici) AND s._deleted = 0
                -- km efectivo (modo K): km_recorri, o km del servicio si es 0
                CROSS APPLY (SELECT km_efec = CASE WHEN ISNULL(v.km_recorri,0)=0
                                                    THEN ISNULL(s.km,0) ELSE v.km_recorri END) ke
                -- moneda de la lista del cliente
                OUTER APPLY (
                    SELECT TOP 1 RTRIM(lpm.id_moneda_) AS moneda
                    FROM lista_precio_modelo lpm
                    WHERE lpm._deleted = 0 AND RTRIM(lpm.id_lista_p) = RTRIM(c.cli_lista)
                ) mon
                -- tarifa del 1er servicio: lista × servicio × tipo vehículo × vigencia
                OUTER APPLY (
                    SELECT TOP 1 lp.precio FROM lista_precio lp
                    WHERE lp._deleted = 0 AND RTRIM(lp.id_lista_p) = RTRIM(c.cli_lista)
                      AND RTRIM(lp.id_servici) = RTRIM(v.id_servici)
                      AND RTRIM(lp.id_vehicul) = RTRIM(v.id_vehicul)
                      AND CAST(v.hs_fin AS date) BETWEEN lp.f_vigencia AND lp.f_vigenci2
                    ORDER BY lp.f_vigencia DESC
                ) tar
                -- tarifa de la cabecera (cuando fc_prefere = 'C')
                OUTER APPLY (
                    SELECT TOP 1 lp.precio FROM lista_precio lp
                    WHERE lp._deleted = 0 AND RTRIM(lp.id_lista_p) = RTRIM(c.cli_lista)
                      AND RTRIM(lp.id_servici) = RTRIM(v.cabecera)
                      AND RTRIM(lp.id_vehicul) = RTRIM(v.id_vehicul)
                      AND CAST(v.hs_fin AS date) BETWEEN lp.f_vigencia AND lp.f_vigenci2
                    ORDER BY lp.f_vigencia DESC
                ) tarcab
                -- tarifa de la hora extra (servicio parametro.cliente_ad)
                OUTER APPLY (
                    SELECT TOP 1 lp.precio FROM lista_precio lp
                    WHERE lp._deleted = 0 AND RTRIM(lp.id_lista_p) = RTRIM(c.cli_lista)
                      AND RTRIM(lp.id_servici) = @cliente_ad
                      AND RTRIM(lp.id_vehicul) = RTRIM(v.id_vehicul)
                      AND CAST(v.hs_fin AS date) BETWEEN lp.f_vigencia AND lp.f_vigenci2
                    ORDER BY lp.f_vigencia DESC
                ) tarext
                CROSS APPLY (
                    SELECT
                        km_efec = ke.km_efec,
                        moneda  = CASE WHEN ISNULL(v.importe_co,0) > 0 THEN RTRIM(ISNULL(v.moneda_con,''))
                                       WHEN v.sin_cargo = 1 THEN 'PESOS'
                                       ELSE ISNULL(mon.moneda, '') END,
                        -- exceso de minutos (solo modo H y solo si dur_real > dur_teórica;
                        -- dur_teórica replica el bug FoxPro: minutos_du cuenta como horas)
                        exc_min = CASE WHEN s.modo_fac = 'H'
                                        AND DATEDIFF(minute, v.hs_inicio, v.hs_fin)
                                            > (ISNULL(s.horas_dura,0)*60 + ISNULL(s.minutos_du,0)*60)
                                       THEN DATEDIFF(minute, v.hs_inicio, v.hs_fin)
                                            - (ISNULL(s.horas_dura,0)*60 + ISNULL(s.minutos_du,0)*60)
                                       ELSE 0 END
                ) calc
                CROSS APPLY (
                    SELECT
                        -- precio base según la cascada
                        imp_base = CASE
                            WHEN ISNULL(v.importe_co,0) > 0 THEN v.importe_co
                            WHEN v.sin_cargo = 1 THEN 0
                            WHEN RTRIM(ISNULL(v.cabecera,'')) <> '' AND c.fc_prefere = 'C'
                                 THEN ISNULL(tarcab.precio, -1)
                            WHEN s.modo_fac = 'K' THEN ISNULL(tar.precio, -1) * calc.km_efec
                            ELSE ISNULL(tar.precio, -1) END,
                        -- horas extra (modo H): horas enteras × tarifa + fracción
                        imp_extra = CASE
                            WHEN s.modo_fac = 'H' AND calc.exc_min > 0 AND tarext.precio IS NOT NULL THEN
                                FLOOR(calc.exc_min / 60.0) * tarext.precio
                                + CASE WHEN (calc.exc_min % 60) > @fraccion AND (calc.exc_min % 60) <= 30
                                            THEN tarext.precio / 2.0
                                       WHEN (calc.exc_min % 60) > 30 THEN tarext.precio
                                       ELSE 0 END
                            ELSE 0 END
                ) b
                CROSS APPLY (
                    SELECT
                        km_efec   = calc.km_efec,
                        moneda    = calc.moneda,
                        imp_base  = b.imp_base,
                        imp_extra = b.imp_extra,
                        -- descuento/incremento en cascada sobre (base + extra)
                        imp_desc  = CASE
                            WHEN b.imp_base = -1 THEN 0
                            WHEN ISNULL(v.descuento_,0) > 0
                                 THEN ROUND((b.imp_base + b.imp_extra) * v.descuento_ / 100.0, 2)
                            WHEN ISNULL(c.cli_desc,0) > 0
                                 THEN ROUND((b.imp_base + b.imp_extra) * c.cli_desc / 100.0, 2)
                            ELSE 0 END,
                        imp_incr  = CASE
                            WHEN b.imp_base = -1 THEN 0
                            WHEN ISNULL(v.descuento_,0) > 0 THEN 0
                            WHEN ISNULL(c.cli_desc,0)   > 0 THEN 0
                            WHEN ISNULL(c.cli_incr,0)   > 0
                                 THEN ROUND((b.imp_base + b.imp_extra) * c.cli_incr / 100.0, 2)
                            ELSE 0 END
                ) g
            WHERE v._deleted = 0
              AND RTRIM(v.estado_via) = 'FINALIZADO'
              AND {candado}
              AND RTRIM(ISNULL(v.id_cliente, '')) = '{cli}'
              AND {grpFiltro}
            ORDER BY v.hs_inicio, v.id_viaje
            """;

        var result = new List<ViajeValorizadoRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            result.Add(new ViajeValorizadoRow(
                rd.GetInt64(0), rd.GetInt64(1), Dt(rd, 2), Dt(rd, 3),
                rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
                rd.GetInt32(8), rd.GetInt64(9), rd.GetString(10),
                rd.GetDecimal(11), rd.GetDecimal(12), rd.GetDecimal(13), rd.GetDecimal(14),
                rd.GetBoolean(15)));
        }
        return result;

        static DateTime? Dt(System.Data.Common.DbDataReader rd, int i) =>
            rd.IsDBNull(i) ? null : rd.GetDateTime(i);
    }

    /// <summary>
    /// Arma los totales de la solapa "Liquidación" (réplica de <c>arma_liquidacion</c>)
    /// combinando los servicios valorizados (<see cref="ValorizarGrupoAsync"/>) con los
    /// adicionales (<see cref="GetAdicionalesGrupoAsync"/>). Solo lectura.
    /// <para>
    /// Como el form arranca sin ajuste manual global (porc/imp descuento/incremento = 0)
    /// ni IVA (<c>parametro.piva = 0</c>), reproduce la pantalla en su estado inicial:
    /// <c>Subtotal → ×tipo_cambio → +IVA → +adicionales(exentos)</c>. El tipo de cambio
    /// es 1 si la moneda es PESOS; para USS/USD/EURO el FoxPro lo pide a mano, acá se
    /// usa el parámetro <paramref name="tipoCambio"/> (default 1, igual que el form
    /// antes de que el usuario lo cargue).
    /// </para>
    /// </summary>
    public async Task<LiquidacionTotalesRow> CalcularTotalesLiquidacionAsync(
        string idCliente, string grupo, bool porFecha, DateOnly desde, DateOnly hasta,
        decimal tipoCambio = 1m)
    {
        var servicios = await ValorizarGrupoAsync(idCliente, grupo, porFecha, desde, hasta);
        var adicionales = await GetAdicionalesGrupoAsync(idCliente, grupo, porFecha, desde, hasta);

        // arma_liquidacion: subtotal = Σ servicio ; extra = Σ horas extra ;
        // descuento/incremento = Σ de cada uno ; total = subtotal+extra+incr-desc.
        var subtotal   = servicios.Where(s => !s.SinTarifa).Sum(s => s.ImpServicio);
        var extra      = servicios.Where(s => !s.SinTarifa).Sum(s => s.ImpExtra);
        var descuento  = servicios.Sum(s => s.ImpDescuento);
        var incremento = servicios.Sum(s => s.ImpIncremento);
        var total      = subtotal + extra + incremento - descuento;

        // Moneda dominante: la de los servicios con tarifa (si todas iguales).
        // PESOS → tipo de cambio bloqueado en 1 (como el FoxPro).
        var moneda = servicios.Where(s => !s.SinTarifa && s.Moneda.Length > 0)
                              .Select(s => s.Moneda).FirstOrDefault() ?? "PESOS";
        var tc = moneda == "PESOS" ? 1m : tipoCambio;

        // pesifica → IVA (parametro.piva, hoy 0) → + adicionales exentos (sin IVA)
        var piva = await GetPivaAsync();
        var totalFinal   = Math.Round(total * tc, 2);
        var iva          = Math.Round(totalFinal * piva / 100m, 2);
        var totalConIva  = totalFinal + iva;
        var totalAdic    = adicionales.Where(a => a.Estado != "EXCLUIDO").Sum(a => a.Total);
        var totalLiq     = totalConIva + totalAdic;

        var hayErrores = servicios.Any(s => s.SinTarifa)
                      || adicionales.Any(a => a.SinTarifa);

        return new LiquidacionTotalesRow(
            subtotal, extra, descuento, incremento, total, tc, totalFinal,
            piva, iva, totalConIva, totalAdic, totalLiq, moneda, hayErrores);
    }

    /// <summary>IVA por defecto del sistema (<c>parametro.piva</c>). En NORTUR hoy es 0.</summary>
    private async Task<decimal> GetPivaAsync()
    {
        return await _cache.GetOrCreateAsync("nortur:piva", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TOP 1 ISNULL(piva, 0) FROM parametro";
            var v = await cmd.ExecuteScalarAsync();
            return v is decimal d ? d : Convert.ToDecimal(v ?? 0);
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    //  FACTURACIÓN — Liquidaciones estimadas (facturacion_cliente_estimada.scx)
    //  Proyección de venta agregada por mes/cliente (solo lectura).
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Proyección de venta: viajes <c>origen='T'</c> del rango ya liquidados,
    /// agregados por mes. A diferencia del FoxPro (que revaloriza viaje por viaje
    /// contra <c>lista_precio</c> vigente), acá usamos el importe YA liquidado en
    /// <c>liquidacion_detalle</c> — fuente más confiable para visualización y
    /// SQL 2012-friendly. Cada fila = un mes con su total estimado en pesos.
    /// </summary>
    public async Task<List<FacturacionEstimadaMesRow>> GetFacturacionEstimadaPorMesAsync(
        DateOnly desde, DateOnly hasta)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        // Mes = primeros 7 chars de la fecha de la liquidación (yyyy-MM). SQL 2012:
        // CONVERT(char(7), fecha, 120) da 'yyyy-MM'.
        cmd.CommandText = $"""
            SELECT
                CONVERT(char(7), l.fecha, 120)   AS Mes,
                COUNT(DISTINCT l.idliquidac)     AS Liquidaciones,
                SUM(CASE WHEN d.tipo = 'SERVICIO' THEN 1 ELSE 0 END) AS Servicios,
                SUM(ISNULL(d.importe, 0))        AS TotalEstimado
            FROM liquidacion l
                INNER JOIN liquidacion_detalle d ON d.idliquidac = l.idliquidac AND d._deleted = 0
            WHERE l._deleted = 0 AND l.tipo = 'CLIENTE'
                  AND l.fecha BETWEEN '{desde:yyyy-MM-dd}' AND '{hasta:yyyy-MM-dd}'
            GROUP BY CONVERT(char(7), l.fecha, 120)
            ORDER BY Mes
            """;
        var result = new List<FacturacionEstimadaMesRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            result.Add(new FacturacionEstimadaMesRow(
                rd.GetString(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetDecimal(3)));
        return result;
    }

    /// <summary>Proyección por cliente para el rango (top N por total). Mismo criterio
    /// que <see cref="GetFacturacionEstimadaPorMesAsync"/> pero agrupado por cliente.</summary>
    public async Task<List<FacturacionEstimadaClienteRow>> GetFacturacionEstimadaPorClienteAsync(
        DateOnly desde, DateOnly hasta)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                RTRIM(ISNULL(l.id_cliente, ''))  AS Codigo,
                RTRIM(ISNULL(MAX(c.razon_soci), l.id_cliente)) AS RazonSocial,
                COUNT(DISTINCT l.idliquidac)     AS Liquidaciones,
                SUM(ISNULL(d.importe, 0))        AS TotalEstimado
            FROM liquidacion l
                INNER JOIN liquidacion_detalle d ON d.idliquidac = l.idliquidac AND d._deleted = 0
                LEFT JOIN cliente c ON l.id_cliente = c.id_cliente AND c._deleted = 0
            WHERE l._deleted = 0 AND l.tipo = 'CLIENTE'
                  AND l.fecha BETWEEN '{desde:yyyy-MM-dd}' AND '{hasta:yyyy-MM-dd}'
            GROUP BY l.id_cliente
            ORDER BY TotalEstimado DESC
            """;
        var result = new List<FacturacionEstimadaClienteRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            result.Add(new FacturacionEstimadaClienteRow(
                rd.GetString(0), rd.GetString(1), rd.GetInt32(2), rd.GetDecimal(3)));
        return result;
    }
}

/// <summary>Resultado del login — espeja las variables públicas del FoxPro
/// (cUsuario, cAcceso, cNivel, lOperadorMesaDeTrafico).</summary>
public record LoginResultDto(
    bool Exito, string? Error,
    string Usuario, string Acceso, string Nivel, bool Operador)
{
    public static LoginResultDto Fallo(string error) => new(false, error, "", "", "", false);
}

public class TableroDto
{
    // Días de la ventana de aviso (de la tabla `parametro`), para el tooltip de cada chip.
    public int AvisoVeh       { get; set; } = 7;   // VTV
    public int AvisoMat       { get; set; } = 10;  // Matafuego
    public int AvisoCho       { get; set; } = 30;  // Chofer (registro/CNRT/AEP)

    public int Vehiculos      { get; set; }
    public int VtvProxVencer  { get; set; }
    public int VtvVencidos    { get; set; }
    public int MatProxVencer  { get; set; }
    public int MatVencidos    { get; set; }
    public int Choferes       { get; set; }
    public int RegProxVencer  { get; set; }
    public int RegVencidos    { get; set; }
    public int CnrtProxVencer { get; set; }
    public int CnrtVencidos   { get; set; }
    public int AepProxVencer  { get; set; }
    public int AepVencidos    { get; set; }
}

public record ServicioDto(string IdServici, string Nombre);
public record VehiculoTipoDto(string Id, string Nombre);

// ── Clientes (ABM solo lectura) ──
public record ClienteListaRow(
    string Codigo, string RazonSocial, string Telefono, string Celular,
    string Domicilio, string Nro, string Piso, string Depto, string Localidad,
    decimal Descuento, string Contacto1, string Contacto2, DateOnly? FInhabilitacion);

public record ClienteCorreoRow(int Orden, string Contacto, string Cargo, string Email);

public class ClienteDetalleDto
{
    public string Codigo = "", RazonSocial = "";
    public string Domicilio = "", Nro = "", Piso = "", Depto = "";
    public string CPostal = "", Localidad = "", Provincia = "";
    public string Telefono = "", Celular = "";
    public string TipoResp = "", TipoRespDesc = "", Ncuit = "", Email = "", Comentario = "";
    public DateOnly? FInhabilitacion, FCreate, FModify;
    public decimal Descuento, Incremento;
    public string EmpresaFc = "", ObPrecio = "", ListaPrecio = "", Cairo = "", FcPrefere = "";
    public bool Bus24, PidePax, Voucher, Arsa, PlantillaDestinoEmpresa, EnviaGps;
    public string GpsTipo = "", GpsHora = "";
    public List<string> RubrosExcluidos = new();
    public List<ClienteCorreoRow> Correos = new();
}

// ── Choferes (ABM solo lectura) ──
public record ChoferListaRow(
    string Codigo, string Fletero, string Nombre, string Domicilio,
    string Nro, string Piso, string Depto, string Localidad,
    string Telefono, string Celular, string Tdoc, string Ndoc,
    DateOnly? VtoRegistro, DateOnly? VtoCnrt, DateOnly? VtoAep, DateOnly? FInhabilitacion);

public record ChoferVehiculoRow(string IdVehiculo, int Interno, string Patente, string Modelo);
public record ChoferTelefonoRow(int Orden, string Telefono, string Linea, string Celular);

// ── Vehículos - Flota ──────────────────────────────────────────────────────

/// <summary>Una fila de la grilla de Vehículos (vehiculo.scx). Las 15 columnas del FoxPro,
/// más Interno/Uso/Activo/FBaja para filtros (Ver Activos / Flota Propia) y el pintado de
/// egresados (egresado = !Activo OR FBaja).</summary>
public record VehiculoListaRow(
    string IdVehiculo, string Cronograma, string Fletero, string Marca,
    string Color, string Dominio, string PolizaNombre, string PolizaNro,
    DateOnly? PolizaVto, string EstadoCnrt, string Radicacion, string TacografoMarca,
    string TacografoNro, string HabilitacionNro, DateOnly? HabilitacionVto,
    int Interno, string Uso, bool Activo, DateOnly? FBaja)
{
    public bool EsEgresado => !Activo || FBaja is not null;
    public bool EsPropio => string.Equals(Uso, "PROPIO", StringComparison.OrdinalIgnoreCase);
}

public record VehiculoDuenoRow(string IdDueno, string Nombre, decimal Porcentaje);
public record VehiculoPermisoRow(int IdPermiso, string Nombre, string NroPermiso, DateOnly? FVenc, DateOnly? FBaja);
public record VehiculoCubiertaRow(int Posicion, long NroSerie);
public record VehiculoRepuestoRow(string IdRepuesto, decimal Cantidad);

public class VehiculoDetalleDto
{
    // Datos Vehículo
    public string Codigo = "", Dominio = "", Marca = "", Cronograma = "", Fletero = "", IdTipo = "", Color = "";
    public int Modelo;
    public long Interno, Pax;
    public string Uso = "";
    public bool Activo;
    public string Chasis = "", Motor = "", MarcaChasis = "", MarcaCarroceria = "", ModeloChasis = "";
    // Seguros / CNRT / habilitaciones / vencimientos
    public string PolizaNombre = "", PolizaNro = "";
    public DateOnly? PolizaVto;
    public string EstadoCnrt = "", Radicacion = "", HabilitacionNro = "", VerificacionNro = "";
    public DateOnly? HabilitacionVto, VerificacionVto, VencimientoMat, PuertoAeoVto;
    public string TacografoMarca = "", TacografoNro = "", Nextel = "", TacAuOeste = "", TacAuSol = "", Comentario = "";
    public DateOnly? FCompra, FVenta, FBaja, FCreate, FModify;
    // GPS / comodidades / combustible
    public string GpsActivo = "";
    public bool Bano, Bar, Video, Wifi, Hasta100Km;
    public long LitroTanque, Autonomia, ConsumoDesde, ConsumoHasta;
    // Estado operativo (lo pisa Tráfico, no es del ABM)
    public string Estado = "", ConductorLogoneado = "";
    // Tarjetas combustible
    public string YpfTarjeta = "", YpfPin = "", EssoTarjeta = "", EssoPin = "";
    public DateOnly? YpfVenc, EssoVenc;
    // Grillas de pestañas
    public List<VehiculoDuenoRow> Duenos = new();
    public List<VehiculoPermisoRow> Permisos = new();
    public List<VehiculoCubiertaRow> Cubiertas = new();
    public List<VehiculoRepuestoRow> Repuestos = new();

    public bool EsEgresado => !Activo || FBaja is not null;
    public bool GpsActivoBool => GpsActivo == "1" || string.Equals(GpsActivo, "S", StringComparison.OrdinalIgnoreCase);
    public string UsoDescripcion => string.IsNullOrWhiteSpace(Uso) ? "—" : Uso;
}

public class ChoferDetalleDto
{
    // Datos personales
    public string Codigo = "", Fletero = "", Nombre = "", Apellido = "", Nombre1 = "", Nombre2 = "";
    public string Padre = "", Madre = "";
    public string Tdoc = "", Ndoc = "", Ncuil = "", Ncuit = "", Email = "", Comentario = "";
    public string EstadoCivil = "", LugarNacimiento = "", GrupoSanguineo = "", RhPosNeg = "";
    public DateOnly? FNac, FInhabilitacion, FCreate, FModify;
    public string RegistroNro = "", RegistroNroCnrt = "";
    public DateOnly? VtoRegistro, VtoCnrt, VtoAep;
    public string Nextel = "", NextelCel = "";
    // Condiciones laborales
    public DateOnly? FIngreso;
    public bool Lunes, Martes, Miercoles, Jueves, Viernes, Sabado, Domingo;
    public DateTime? HInicioJornal, HFinJornal;
    public long Jornal, Legajo;
    public bool JornalAplica, Auditor;
    public string IdListaPrecio = "", YpfPin = "", EssoPin = "";
    // Domicilio DNI
    public string Domicilio = "", Nro = "", Piso = "", Depto = "", Entre1 = "", Entre2 = "";
    public string CPostal = "", Localidad = "", Partido = "", Provincia = "";
    // Domicilio real (donde vive)
    public string RealDomicilio = "", RealNro = "", RealPiso = "", RealDepto = "", RealEntre1 = "", RealEntre2 = "";
    public string RealCPostal = "", RealLocalidad = "", RealPartido = "", RealProvincia = "";
    // Teléfonos
    public string Telefono = "", Celular = "";
    public List<ChoferTelefonoRow> Telefonos = new();
    // Vehículos asignados
    public List<ChoferVehiculoRow> Vehiculos = new();

    public string RhDescripcion => RhPosNeg switch
    {
        "P" => "Positivo", "N" => "Negativo", _ => "No informó"
    };
    public string DiasTrabajo
    {
        get
        {
            var d = new List<string>();
            if (Lunes) d.Add("Lun"); if (Martes) d.Add("Mar"); if (Miercoles) d.Add("Mié");
            if (Jueves) d.Add("Jue"); if (Viernes) d.Add("Vie"); if (Sabado) d.Add("Sáb");
            if (Domingo) d.Add("Dom");
            return d.Count == 0 ? "" : string.Join(" · ", d);
        }
    }
}

public record BandaHorariaRow(DateOnly Fecha, string TipoVehiculo, string Banda, int Reservas);

public record ReservaFechaServicioRow(
    DateOnly Fecha,
    string CodServicio,
    string Servicio,
    int Reservas,
    int Canceladas,
    int Pax);

/// <summary>
/// Una fila de la planilla de servicios del día (Operación de Tráfico).
/// Mapea las columnas de la grilla del FoxPro contra los campos de `viaje`.
/// </summary>
public record PlanillaTraficoRow(
    int IdViaje,
    DateOnly Fecha,
    string Origen,      // 'P' = Empresa/Plantilla, 'T' = Turismo/Transportación
    bool EsNortur,      // servicio interno de NORTUR (réplica de chkNortur)
    string HPre,
    string HIni,
    string HFin,
    string HAvi,
    string HCie,
    string UPr,
    string UCb,
    string UAs,
    int? Chq,
    int? Ag,
    string Recorrido,
    string Fletero,
    string Chofer,
    string Veh,
    string Cliente,
    int? Pax,
    int? Agua,
    string Adj,
    string Comentario,
    string Grupo,
    string Vuelo,
    string Guia,
    string Estado);

/// <summary>
/// Listas para los combos de unidades de la pantalla de Tráfico (trafico2.scx):
/// Programadas = "interno por empresas" (filtra U/Pr), Asignadas = "todos los internos" (filtra U/Cb).
/// </summary>
public record CombosUnidadesTrafico(List<string> Programadas, List<string> Asignadas);

/// <summary>Token de versión para el auto-refresh de Tráfico (detección de cambios).</summary>
public record TraficoVersion(int CantViajes, DateTime? UltimoCambioViaje, DateTime? UltimoCambioVehiculo);

/// <summary>
/// Una fila del panel "Buses" de la pantalla de Tráfico (grid2 de trafico2.scx,
/// armado por arma_grid_vehiculo): estado vivo de cada unidad de la flota.
/// Chofer/Chofer2 son los códigos de chofer (id_chofer/id_chofer2), igual que el FoxPro.
/// </summary>
public record PanelBusRow(
    string Fletero,
    int Interno,
    string Chofer,
    string Chofer2,
    string Franco,      // código de chofer_franco del chofer para hoy ('' si trabaja)
    string Estado,      // LIBERADO / ASIGNADO / CURSO (display) / ...
    int? IdViaje,       // último viaje asignado (0 = sin viaje)
    string Zona,
    string Nextel,
    int? Pax,
    string Vehiculo,
    DateTime? HsInicio);

/// <summary>
/// Una fila de la vista de servicios CANCELADOS del día (botón "Cxl" del FoxPro).
/// Columnas según arma_grid_viaje_sup_cnl: incluye Motivo de cancelación;
/// U/As acá es `interno` (numérico), no id_interno como en la vista normal.
/// </summary>
public record TraficoCanceladoRow(
    int IdViaje,
    DateOnly Fecha,
    string Ob,          // 'C' si tiene comentario
    string Ad,          // 'A' si tiene adicionales
    string HIni,
    string HFin,
    string HAvi,
    string HCie,
    string UPr,
    string UCb,
    int? UAs,
    int? Chq,
    string Recorrido,
    string Motivo,
    string Veh,
    string Cliente,
    int? Pax,
    string Comentario,
    string Grupo,
    string Vuelo,
    string Guia);

/// <summary>
/// Una línea de la grilla de Adicionales del "Zoom del Viaje" (tabla viaje_adicional).
/// </summary>
public record AdicionalViajeRow(string Nombre, int Cantidad, decimal Precio);

/// <summary>
/// Detalle completo de un viaje para el modal "Zoom del Viaje" (solo lectura).
/// Espeja los campos del form FoxPro `trafico_zoom.scx`. Campos como clase mutable
/// (no record posicional) por la cantidad de propiedades.
/// </summary>
public class DetalleViajeDto
{
    // ── Cabecera / estado ──
    public int IdViaje { get; set; }
    public DateOnly? FPedido { get; set; }
    public DateOnly? FReserva { get; set; }
    public string Estado { get; set; } = "";
    public string TipoServicio { get; set; } = "";   // Transporte Personal / Servicio Especial
    public int? Voucher { get; set; }

    // ── Horarios ──
    public DateTime? HsInicio { get; set; }
    public string Presentacion { get; set; } = "";    // "en hora" / "15 minutos antes" / ...
    public DateTime? HsFinAprox { get; set; }
    public DateTime? HsFin { get; set; }
    public string Duracion { get; set; } = "";

    // ── Ruta / odómetros ──
    public DateTime? HsIniRuta { get; set; }
    public DateTime? HsFinRuta { get; set; }
    public int? IdRuta { get; set; }
    public int? OdometroIni { get; set; }
    public int? OdometroFin { get; set; }
    public int? KmRecorrido { get; set; }

    // ── Cliente / operador ──
    public string IdCliente { get; set; } = "";
    public string NombreCliente { get; set; } = "";
    public string IdOperador { get; set; } = "";
    public string NombreOperador { get; set; } = "";

    // ── Servicios ──
    public string IdServicio1 { get; set; } = "";
    public string Servicio1 { get; set; } = "";
    public string IdServicio2 { get; set; } = "";
    public string Servicio2 { get; set; } = "";
    public string IdServicio3 { get; set; } = "";
    public string Servicio3 { get; set; } = "";
    public string Cabecera { get; set; } = "";
    public int? Km { get; set; }

    // ── Vehículo / pax ──
    public string TipoVehiculo { get; set; } = "";
    public int? Capacidad { get; set; }
    public int? Pax { get; set; }
    public int? Agua { get; set; }
    public int? Horas { get; set; }

    // ── Vuelo / guía / grupo ──
    public string Vuelo { get; set; } = "";
    public string Guia { get; set; } = "";
    public string Grupo { get; set; } = "";
    public string NombreGrupo { get; set; } = "";
    public DateOnly? FGrupoFin { get; set; }
    public DateOnly? FGrupoFactura { get; set; }

    // ── Recorrido ──
    public string Desde { get; set; } = "";
    public string Hasta { get; set; } = "";
    public string Distrito { get; set; } = "";
    public string RecorridoCelular { get; set; } = "";
    public string Comentario { get; set; } = "";
    public string Adjunto { get; set; } = "";

    // ── Financiero ──
    public string MonedaLiquidar { get; set; } = "";
    public decimal ImporteLiquidar { get; set; }
    public decimal PorcDescuento { get; set; }
    public bool BonificadoCliente { get; set; }
    public string MonedaPago { get; set; } = "";
    public decimal ImportePago { get; set; }
    public bool BonificadoEmpresa { get; set; }

    // ── Chofer / unidad ──
    public string NombreChofer { get; set; } = "";
    public string IdChofer { get; set; } = "";
    public string IdChofer2 { get; set; } = "";
    public string TipoChofer { get; set; } = "";
    public string Vehiculo { get; set; } = "";
    public string Fletero { get; set; } = "";

    public bool TieneAdjunto => !string.IsNullOrWhiteSpace(Adjunto);
    public bool TieneGrupo => !string.IsNullOrWhiteSpace(Grupo) && Grupo != "SIN GRUPO";
}

// ── Facturación: Resumen de Liquidaciones (liquidacion_cliente.scx) ──

/// <summary>Fila de la grilla de cabeceras (liquidacion ⨝ cliente|fletero).
/// Subtotal/Exento/TotalGral ya vienen calculados como en el FoxPro.</summary>
public record LiquidacionRow(
    int IdLiquidacion, string Tipo, DateOnly? Fecha, string Codigo, string RazonSocial,
    string Moneda, decimal Subtotal, decimal Iva, decimal Exento, decimal TotalGral,
    DateOnly? Fcomp, string Factura, DateOnly? FPago, string FormaPago, string Banco,
    long NPago, decimal RetIva, decimal RetIibb, decimal RetSuss, decimal Pago)
{
    public bool TieneFactura => !string.IsNullOrWhiteSpace(Factura);
}

/// <summary>Fila del detalle (liquidacion_detalle) de una liquidación.</summary>
public record LiquidacionDetalleRow(
    int Id, long IdViaje, string Tipo, string IdAdicional, string Nombre, string Moneda,
    long Cantidad, decimal Precio, decimal Importe, string DDestinoProv, long KmRecorrido,
    decimal Descuento, decimal Incremento, long IdViajeInt);

/// <summary>Cabecera cruda de una liquidación (campos sin colapsar) para reconstruir la
/// solapa "Liquidacion" del visor read-only de "Liquidación a Clientes". Ver
/// <see cref="ReportService.GetLiquidacionCabeceraAsync"/>.</summary>
public record LiquidacionCabeceraDto(
    int IdLiquidacion, string Tipo, DateOnly? Fecha, string Codigo, string RazonSocial,
    string Moneda, decimal Subtotal, decimal Extra, decimal TCambio, decimal Adicional,
    decimal Iva, decimal Piva, string Motivo, decimal Total);

/// <summary>Viaje pendiente de liquidar (universo POR ESTADO / POR FECHA de la
/// pantalla "Liquidación a Clientes"). Una fila por viaje FINALIZADO de un grupo
/// vencido. Solo lectura: refleja qué falta liquidar, sin valorizar.</summary>
public record ViajePendienteRow(
    long IdViaje, long IdViajeInt, string IdCliente, string Cliente, string Grupo,
    DateTime? FReserva, DateTime? HsInicio, DateTime? HsFin, string Servicio,
    string Destino, string Vehiculo, string Chofer, int Pax, string Cabecera,
    long KmRecorrido, decimal ImporteConvenido, string MonedaConvenida, DateTime? FinGrupo);

/// <summary>Adicional de un viaje pendiente (solapa "Adicionales" de Liquidación a
/// Clientes), valorizado contra <c>adicional_lista_precio</c> (adicional × tipo
/// vehículo × vigencia), igual que el FoxPro. <c>Estado</c>: ABONA o EXCLUIDO según el
/// rubro esté en <c>cliente_adicional_excluido</c> (EXCLUIDO → precio/total 0).
/// <c>SinTarifa</c> = ABONA pero sin precio vigente en el tarifario (el FoxPro lo
/// pintaría como error de tarifa).</summary>
public record ViajeAdicionalRow(
    long IdViaje, string IdAdicional, string Nombre, long Cantidad, string Estado,
    DateTime? Inicio, string Vehiculo, string Servicio, string Cabecera, string Destino,
    decimal Precio, decimal Total, bool SinTarifa);

/// <summary>Viaje valorizado en vivo por el motor de tarifas (<c>arma_servicio</c>),
/// solapa "Servicios" de Liquidación a Clientes. <c>ImpServicio</c> = importe base del
/// servicio (o -1 si no hay tarifa → <c>SinTarifa</c>); <c>ImpExtra</c> = horas extra;
/// <c>ImpDescuento</c>/<c>ImpIncremento</c> = ajuste en cascada. El importe neto del
/// viaje es <c>ImpServicio + ImpExtra + ImpIncremento - ImpDescuento</c>.</summary>
public record ViajeValorizadoRow(
    long IdViaje, long IdViajeInt, DateTime? HsInicio, DateTime? HsFin,
    string Servicio, string Cabecera, string Vehiculo, string Destino, int Pax,
    long Km, string Moneda, decimal ImpServicio, decimal ImpExtra,
    decimal ImpDescuento, decimal ImpIncremento, bool SinTarifa)
{
    /// <summary>Importe neto del viaje (base + extra + incremento − descuento).
    /// Si no hay tarifa (<see cref="SinTarifa"/>) devuelve 0 para no contaminar el total.</summary>
    public decimal ImporteNeto => SinTarifa ? 0m
        : ImpServicio + ImpExtra + ImpIncremento - ImpDescuento;
}

/// <summary>Totales de la solapa "Liquidación" (réplica de <c>arma_liquidacion</c>):
/// los mismos campos que muestra el FoxPro. Todo en la moneda de la lista; el pesaje
/// (× tipo de cambio) y el IVA se aplican sobre el total de servicios. Los adicionales
/// (exentos) se suman sin IVA al final.</summary>
public record LiquidacionTotalesRow(
    decimal Subtotal, decimal Extra, decimal Descuento, decimal Incremento,
    decimal Total, decimal TipoCambio, decimal TotalFinal, decimal Piva,
    decimal Iva, decimal TotalConIva, decimal Adicionales, decimal TotalLiquidacion,
    string Moneda, bool HayErroresTarifa);

// ── Facturación: Liquidaciones estimadas (proyección) ──

public record FacturacionEstimadaMesRow(
    string Mes, int Liquidaciones, int Servicios, decimal TotalEstimado);

public record FacturacionEstimadaClienteRow(
    string Codigo, string RazonSocial, int Liquidaciones, decimal TotalEstimado);
