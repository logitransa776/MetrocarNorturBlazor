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
    public async Task<DetalleViajeDto?> GetDetalleViajeAsync(int idViaje)
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
            cmd.CommandText = """
                DECLARE @hoy  DATE = CAST(GETDATE() AS DATE);
                DECLARE @p30  DATE = DATEADD(day, 30, @hoy);

                SELECT
                    -- Vehículos
                    SUM(CASE WHEN activo=1 AND uso='PROPIO' AND (_deleted=0 OR _deleted IS NULL) THEN 1 ELSE 0 END)                                                                          AS Vehiculos,
                    SUM(CASE WHEN activo=1 AND uso='PROPIO' AND (_deleted=0 OR _deleted IS NULL) AND verificac2 IS NOT NULL AND verificac2 <= @p30 THEN 1 ELSE 0 END)                        AS VtvProxVencer,
                    SUM(CASE WHEN activo=1 AND uso='PROPIO' AND (_deleted=0 OR _deleted IS NULL) AND verificac2 IS NOT NULL AND verificac2 <  @hoy THEN 1 ELSE 0 END)                        AS VtvVencidos,
                    SUM(CASE WHEN activo=1 AND uso='PROPIO' AND (_deleted=0 OR _deleted IS NULL) AND vencimient  IS NOT NULL AND vencimient  <= @p30 THEN 1 ELSE 0 END)                      AS MatProxVencer,
                    SUM(CASE WHEN activo=1 AND uso='PROPIO' AND (_deleted=0 OR _deleted IS NULL) AND vencimient  IS NOT NULL AND vencimient  <  @hoy THEN 1 ELSE 0 END)                      AS MatVencidos
                FROM vehiculo;

                SELECT
                    -- Choferes
                    SUM(CASE WHEN (f_delete IS NULL OR LEN(LTRIM(RTRIM(CAST(f_delete AS nvarchar))))=0) AND (_deleted=0 OR _deleted IS NULL) THEN 1 ELSE 0 END)                                                                                                                    AS Choferes,
                    SUM(CASE WHEN (f_delete IS NULL OR LEN(LTRIM(RTRIM(CAST(f_delete AS nvarchar))))=0) AND (_deleted=0 OR _deleted IS NULL) AND registro_v IS NOT NULL AND registro_v <= @p30 THEN 1 ELSE 0 END)                                                                   AS RegProxVencer,
                    SUM(CASE WHEN (f_delete IS NULL OR LEN(LTRIM(RTRIM(CAST(f_delete AS nvarchar))))=0) AND (_deleted=0 OR _deleted IS NULL) AND registro_v IS NOT NULL AND registro_v <  @hoy THEN 1 ELSE 0 END)                                                                   AS RegVencidos,
                    SUM(CASE WHEN (f_delete IS NULL OR LEN(LTRIM(RTRIM(CAST(f_delete AS nvarchar))))=0) AND (_deleted=0 OR _deleted IS NULL) AND registro_3 IS NOT NULL AND registro_3 <= @p30  AND registro_3 < '2090-01-01' THEN 1 ELSE 0 END)                                    AS CnrtProxVencer,
                    SUM(CASE WHEN (f_delete IS NULL OR LEN(LTRIM(RTRIM(CAST(f_delete AS nvarchar))))=0) AND (_deleted=0 OR _deleted IS NULL) AND registro_3 IS NOT NULL AND registro_3 <  @hoy  AND registro_3 < '2090-01-01' THEN 1 ELSE 0 END)                                    AS CnrtVencidos,
                    SUM(CASE WHEN (f_delete IS NULL OR LEN(LTRIM(RTRIM(CAST(f_delete AS nvarchar))))=0) AND (_deleted=0 OR _deleted IS NULL) AND registro_4 IS NOT NULL AND registro_4 <= @p30  AND registro_4 > '2000-01-01' THEN 1 ELSE 0 END)                                    AS AepProxVencer,
                    SUM(CASE WHEN (f_delete IS NULL OR LEN(LTRIM(RTRIM(CAST(f_delete AS nvarchar))))=0) AND (_deleted=0 OR _deleted IS NULL) AND registro_4 IS NOT NULL AND registro_4 <  @hoy  AND registro_4 > '2000-01-01' THEN 1 ELSE 0 END)                                    AS AepVencidos
                FROM chofer;
                """;

            using var reader = await cmd.ExecuteReaderAsync();

            var dto = new TableroDto();
            if (await reader.ReadAsync())
            {
                dto.Vehiculos      = reader.GetInt32(0);
                dto.VtvProxVencer  = reader.GetInt32(1);
                dto.VtvVencidos    = reader.GetInt32(2);
                dto.MatProxVencer  = reader.GetInt32(3);
                dto.MatVencidos    = reader.GetInt32(4);
            }
            await reader.NextResultAsync();
            if (await reader.ReadAsync())
            {
                dto.Choferes       = reader.GetInt32(0);
                dto.RegProxVencer  = reader.GetInt32(1);
                dto.RegVencidos    = reader.GetInt32(2);
                dto.CnrtProxVencer = reader.GetInt32(3);
                dto.CnrtVencidos   = reader.GetInt32(4);
                dto.AepProxVencer  = reader.GetInt32(5);
                dto.AepVencidos    = reader.GetInt32(6);
            }
            return dto;
        }) ?? new();
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
