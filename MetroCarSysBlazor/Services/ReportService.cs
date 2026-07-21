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
public partial class ReportService
{
    private readonly IDbContextFactory<NorturDbContext> _dbFactory;
    private readonly IMemoryCache _cache;

    public static readonly DateOnly FechaMinValida = new(2021, 1, 1);
    public static readonly DateOnly FechaMaxValida = new(2027, 12, 31);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    // Tráfico es operación viva: TTL menor al timer de auto-refresh (60s) para que
    // cada tick del timer encuentre el caché vencido y traiga datos frescos.
    private static readonly TimeSpan CacheTtlTrafico = TimeSpan.FromSeconds(55);
    // Grillas de ABM (Choferes/Clientes/Vehículos): la escritura sigue en FoxPro y se
    // replica a SQL, así que el dato cambia "por fuera". TTL corto para que un F5 traiga
    // datos casi-frescos; el botón "Actualizar" de cada grilla invalida el caché al instante.
    private static readonly TimeSpan CacheTtlAbm = TimeSpan.FromSeconds(60);

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

    /// <summary>Los 5 estados posibles de `viaje.estado_via`, en orden de ciclo de vida.</summary>
    public static readonly IReadOnlyList<string> EstadosViaje =
        new[] { "SIN ASIGNAR", "ASIGNADO", "FINALIZADO", "FACTURADO", "CANCELADO" };

    // Los dos "servicios" CABECERA_KM / CABECERA_SERV NO son servicios reales: son MODOS de
    // facturación (por km / por servicio de cabecera). El destino real del viaje vive en
    // d_destino/h_destino, no en id_servici. Son ~90% del volumen y aplastan el informe por
    // servicio real, así que el informe los excluye por defecto (switch reversible en la UI).
    public static readonly IReadOnlyList<string> ServiciosCabecera =
        new[] { "CABECERA_KM", "CABECERA_SERV" };

    private static string ListaCabecerasSql =>
        string.Join(",", ServiciosCabecera.Select(s => $"'{s}'"));

    // WHERE compartido del informe "Reservas por fecha y servicio" (vista agregada y detalle).
    // Convención: lista de servicios/estados vacía = sin filtro (todos).
    // excluirInterno replica los informes FoxPro: afuera el cliente de prueba de
    // `parametro.id_cliente` (hoy 'NORTUR' — guardias y viajes internos).
    // excluirCabeceras: deja fuera CABECERA_KM/SERV (modos de facturación, no servicios).
    private static string WhereReservasFechaServicio(
        DateOnly desde,
        DateOnly hasta,
        IReadOnlyCollection<string> serviciosSel,
        IReadOnlyCollection<string> estadosSel,
        bool excluirInterno,
        bool excluirCabeceras)
    {
        var where = new List<string>
        {
            "v._deleted = 0",
            $"v.f_reserva BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'"
        };
        if (serviciosSel.Count > 0)
        {
            var lista = string.Join(",", serviciosSel.Select(s => $"'{s.Replace("'", "''")}'"));
            where.Add($"v.id_servici IN ({lista})");
        }
        if (estadosSel.Count > 0 && estadosSel.Count < EstadosViaje.Count)
        {
            var lista = string.Join(",", estadosSel.Select(s => $"'{s.Replace("'", "''")}'"));
            where.Add($"v.estado_via IN ({lista})");
        }
        if (excluirInterno)
            where.Add("v.id_cliente <> (SELECT TOP 1 RTRIM(ISNULL(id_cliente, '')) FROM parametro)");
        if (excluirCabeceras)
            where.Add($"v.id_servici NOT IN ({ListaCabecerasSql})");
        return string.Join(" AND ", where);
    }

    /// <summary>
    /// Conteo de los viajes CABECERA (modos de facturación) que el informe deja fuera cuando
    /// se excluyen. Alimenta el KPI "Viajes cabecera". Respeta el resto de los filtros salvo
    /// el de servicios (las cabeceras no están en la selección de servicios reales).
    /// </summary>
    public async Task<(int Reservas, int Pax)> GetVolumenCabecerasAsync(
        DateOnly desde,
        DateOnly hasta,
        IReadOnlyCollection<string> estadosSel,
        bool excluirInterno)
    {
        var estKey = estadosSel.Count == 0 ? "all" : string.Join(",", estadosSel.OrderBy(x => x));
        var key = $"rfscab|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{excluirInterno}|{estKey}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // Mismo WHERE, pero fijando servicios = solo cabeceras y sin excluirlas.
            var where = WhereReservasFechaServicio(desde, hasta, ServiciosCabecera, estadosSel, excluirInterno, excluirCabeceras: false);
            var sql = $"""
                SELECT COUNT(*) AS Reservas, SUM(COALESCE(v.pax, 0)) AS Pax
                FROM viaje v
                WHERE {where}
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync() && !reader.IsDBNull(0))
                return (reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
            return (0, 0);
        });
    }

    public async Task<List<ReservaFechaServicioRow>> GetReservasPorFechaServicioAsync(
        DateOnly desde,
        DateOnly hasta,
        IReadOnlyCollection<string> serviciosSel,
        IReadOnlyCollection<string> estadosSel,
        bool excluirInterno,
        bool excluirCabeceras)
    {
        var servKey = serviciosSel.Count == 0 ? "all" : string.Join(",", serviciosSel.OrderBy(x => x));
        var estKey = estadosSel.Count == 0 ? "all" : string.Join(",", estadosSel.OrderBy(x => x));
        var key = $"rfs|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{excluirInterno}|{excluirCabeceras}|{estKey}|{servKey}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

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
                WHERE {WhereReservasFechaServicio(desde, hasta, serviciosSel, estadosSel, excluirInterno, excluirCabeceras)}
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
    /// Detalle una-por-una de las reservas del informe "Reservas por fecha y servicio"
    /// (mismos filtros que la vista agregada). Alimenta el drill-down por celda/día
    /// y la hoja "Reservas" del Excel.
    /// </summary>
    public async Task<List<ReservaFsDetalleRow>> GetReservasFechaServicioDetalleAsync(
        DateOnly desde,
        DateOnly hasta,
        IReadOnlyCollection<string> serviciosSel,
        IReadOnlyCollection<string> estadosSel,
        bool excluirInterno,
        bool excluirCabeceras)
    {
        var servKey = serviciosSel.Count == 0 ? "all" : string.Join(",", serviciosSel.OrderBy(x => x));
        var estKey = estadosSel.Count == 0 ? "all" : string.Join(",", estadosSel.OrderBy(x => x));
        var key = $"rfsdet|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{excluirInterno}|{excluirCabeceras}|{estKey}|{servKey}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // viaje.id_viaje y viaje.pax son int (no bigint) — leer con GetInt32.
            var sql = $"""
                SELECT
                    v.id_viaje                                            AS IdViaje,
                    v.f_reserva                                           AS Fecha,
                    COALESCE(CONVERT(varchar(5), v.hs_inicio, 108), '')   AS Hora,
                    COALESCE(v.id_servici, '')                            AS CodServicio,
                    COALESCE(s.nombre, v.id_servici, '')                  AS Servicio,
                    COALESCE(NULLIF(LTRIM(RTRIM(v.nombre_cli)), ''), v.id_cliente, '') AS Cliente,
                    LTRIM(RTRIM(COALESCE(v.d_destino, ''))) +
                        CASE WHEN LTRIM(RTRIM(COALESCE(v.h_destino, ''))) <> ''
                             THEN ' a ' + LTRIM(RTRIM(v.h_destino)) ELSE '' END AS Recorrido,
                    COALESCE(v.pax, 0)                                    AS Pax,
                    COALESCE(v.estado_via, '')                            AS Estado,
                    COALESCE(v.nombre_cho, '')                            AS Chofer,
                    v.interno                                             AS Interno,
                    COALESCE(v.origen, '')                                AS Origen,
                    COALESCE(v.grupo, '')                                 AS Grupo
                FROM viaje v
                LEFT JOIN servicio s ON v.id_servici = s.id_servici
                WHERE {WhereReservasFechaServicio(desde, hasta, serviciosSel, estadosSel, excluirInterno, excluirCabeceras)}
                ORDER BY v.f_reserva, v.hs_inicio, v.id_viaje
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var result = new List<ReservaFsDetalleRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new ReservaFsDetalleRow(
                    IdViaje: reader.GetInt32(0),
                    Fecha: DateOnly.FromDateTime(reader.GetDateTime(1)),
                    Hora: reader.GetString(2),
                    CodServicio: reader.GetString(3).Trim(),
                    Servicio: reader.GetString(4).Trim(),
                    Cliente: reader.GetString(5).Trim(),
                    Recorrido: reader.GetString(6).Trim(),
                    Pax: reader.GetInt32(7),
                    Estado: reader.GetString(8).Trim(),
                    Chofer: reader.GetString(9).Trim(),
                    Interno: reader.IsDBNull(10) ? null : Convert.ToInt32(reader.GetValue(10)),
                    Origen: reader.GetString(11).Trim(),
                    Grupo: reader.GetString(12).Trim()));
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
    // Proyección SQL compartida de la planilla de Tráfico (réplica del SELECT de arma_grid_viaje).
    // La usan TANTO la vista de día único (GetPlanillaTraficoAsync) COMO los filtros server-side
    // de "Aplicar Filtros" (GetPlanillaTraficoFiltradaAsync). Cada llamador le agrega su WHERE +
    // ORDER BY. Mantener en sync con MapPlanillaRow (lectura por nombre de columna).
    private const string TraficoProjection = """
        SELECT
            v.id_viaje                                           AS IdViaje,
            v.f_reserva                                          AS Fecha,
            v.origen                                             AS Origen,
            CONVERT(varchar(5), v.hs_present, 108)               AS HPre,
            CONVERT(varchar(5), v.hs_inicio, 108)                AS HIni,
            CONVERT(varchar(5), v.hs_fin, 108)                   AS HFin,
            CONVERT(varchar(5), v.hs_aviso, 108)                 AS HAvi,
            CONVERT(varchar(5), v.hs_fin_apr, 108)               AS HCie,
            v.cronogram2                                         AS UPr,
            v.cronograma                                         AS UCb,
            v.id_interno                                         AS UAs,
            v.chequeo                                            AS Chq,
            v.chequeo_ag                                         AS Ag,
            LTRIM(RTRIM(v.d_destino)) + ' a ' + LTRIM(RTRIM(v.h_destino)) AS Recorrido,
            -- Largo del tramo origen (para que la UI parta "DESDE a HASTA" sin ambigüedad:
            -- hay tramos con ' a ' adentro). OJO: LEN() ignora espacios finales → RTRIM extra
            -- no hace falta, pero el LTRIM sí (LEN cuenta los del inicio).
            LEN(LTRIM(v.d_destino))                              AS RecorridoDesdeLen,
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
            v.id_chofer                                          AS IdChofer,
            v.id_vehicu2                                         AS IdVehiculo,
            v.interno                                            AS Interno,
            v.hs_inicio                                          AS HsInicio,
            v.hs_fin                                             AS HsFin,
            -- Claves para el submenú "Ver Datos Extras" (réplica de verdatosex de trafico2.scx).
            -- id_operado → cliente_operador (Ver Datos Operador); gps_cod → cabecera (Ver
            -- Recorrido); [file] → ruta del adjunto (Ver Adjunto). El de Adicionales usa IdViaje.
            v.id_operado                                         AS IdOperador,
            v.gps_cod                                            AS GpsCod,
            v.[file]                                             AS Adjunto
        FROM viaje v
        """;

    // Mapea una fila del reader (proyección TraficoProjection) a PlanillaTraficoRow.
    // Réplica de la lógica chkNortur de arma_grid_viaje: fila interna NORTUR si
    // cronograma, cronogramacbio (réplica: cronogram2) o id_chofer == 'NORTUR'.
    private static PlanillaTraficoRow MapPlanillaRow(System.Data.Common.DbDataReader reader)
    {
        string S(string c) { var i = reader.GetOrdinal(c); return reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString()!.Trim(); }
        int? N(string c) { var i = reader.GetOrdinal(c); return reader.IsDBNull(i) ? null : Convert.ToInt32(reader.GetValue(i)); }
        DateTime? D(string c) { var i = reader.GetOrdinal(c); return reader.IsDBNull(i) ? null : reader.GetDateTime(i); }

        const string idClientePrueba = "NORTUR";
        var cronograma = S("UCb");
        var cronogramaCbio = S("UPr");
        var idChofer = S("IdChofer");
        var cliente = S("Cliente");
        bool esNortur = cronograma == idClientePrueba || cronogramaCbio == idClientePrueba || idChofer == idClientePrueba;

        return new PlanillaTraficoRow(
            IdViaje: Convert.ToInt32(reader.GetValue(reader.GetOrdinal("IdViaje"))),
            Fecha: DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("Fecha"))),
            Origen: S("Origen"),
            EsNortur: esNortur,
            HPre: S("HPre"),
            HIni: S("HIni"),
            HFin: S("HFin"),
            HAvi: S("HAvi"),
            HCie: S("HCie"),
            UPr: S("UPr"),
            UCb: cronograma,
            UAs: S("UAs"),
            Chq: N("Chq"),
            Ag: N("Ag"),
            Recorrido: S("Recorrido"),
            RecorridoDesdeLen: N("RecorridoDesdeLen") ?? 0,
            Fletero: S("Fletero"),
            Chofer: S("Chofer"),
            Veh: S("Veh"),
            Cliente: cliente,
            Pax: N("Pax"),
            Agua: N("Agua"),
            Adj: S("Adj"),
            Comentario: S("Comentario"),
            Grupo: S("Grupo"),
            Vuelo: S("Vuelo"),
            Guia: S("Guia"),
            Estado: S("Estado"),
            Interno: N("Interno"),
            HsInicio: D("HsInicio"),
            HsFin: D("HsFin"),
            IdChofer: idChofer,
            IdVehiculo: S("IdVehiculo"),
            IdOperador: S("IdOperador"),
            GpsCod: S("GpsCod"),
            Adjunto: S("Adjunto"));
    }

    /// <summary>Acota una fecha al rango usable de la réplica (evita fechas corruptas del FoxPro).</summary>
    private static DateOnly ClampFecha(DateOnly d) =>
        d < FechaMinValida ? FechaMinValida : (d > FechaMaxValida ? FechaMaxValida : d);

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
                {TraficoProjection}
                WHERE v._deleted = 0
                  AND v.f_reserva = '{dia:yyyyMMdd}'
                  AND v.estado_via <> 'CANCELADO'
                ORDER BY v.hs_inicio, v.hs_present
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var result = new List<PlanillaTraficoRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(MapPlanillaRow(reader));
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Planilla de Tráfico filtrada server-side — rama "Aplicar Filtros" del menú contextual de
    /// trafico2.scx (procedimiento arma_grid_viaje). A diferencia de la vista de día único,
    /// re-consulta `viaje` sobre un RANGO de fechas (xFecha1..xFecha2) con el WHERE propio de
    /// cada filtro. Misma proyección (TraficoProjection) y mapeo (MapPlanillaRow) que la vista
    /// normal. No se cachea: es una acción explícita del usuario (un disparo por aplicación).
    /// Implementado por ahora: FECHA (rango). El resto del submenú se irá agregando acá con la
    /// misma mecánica (cada uno suma su condición al WHERE sobre el mismo rango).
    /// </summary>
    public async Task<List<PlanillaTraficoRow>> GetPlanillaTraficoFiltradaAsync(TraficoFiltro filtro)
    {
        var desde = ClampFecha(filtro.Desde);
        var hasta = ClampFecha(filtro.Hasta);
        if (hasta < desde) (desde, hasta) = (hasta, desde);

        // Rango de fechas común a casi todos los filtros (réplica de Between(str_f_reserva, x1, x2)).
        var rango = $"v.f_reserva BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'";

        // WHERE por tipo de filtro (un Case de arma_grid_viaje por rama). El post-proceso del
        // FoxPro descarta los CANCELADO en la vista normal → se filtra acá en el WHERE.
        string where = filtro.Tipo switch
        {
            TraficoFiltroTipo.Fecha =>
                $"v._deleted = 0 AND {rango} AND v.estado_via <> 'CANCELADO'",
            // Tipo de Reserva (diálogo trafico_filtro_tipo_reserva.scx, código TIPO_RESERVA): rango +
            // viaje.origen = 'T'/'P'. origen es el modo de carga de la reserva, NO el servicio:
            //   'T' = por Transportación (turismo / transfer puntual, alta manual — botón Tur)
            //   'P' = por Plantilla     (servicio recurrente de empresa, armado masivo — botón Emp)
            // El FoxPro forzaba uno u otro (optiongroup excluyente). Acá se permite "Ambos": cuando
            // Texto viene null/vacío no se agrega el AND origen y devuelve los dos (mejora pedida).
            // Mismo eje P/T que los botones Emp/Tur de la toolbar, pero acá sobre un RANGO server-side.
            TraficoFiltroTipo.TipoReserva =>
                $"v._deleted = 0 AND {rango} AND v.estado_via <> 'CANCELADO'"
                + (string.IsNullOrEmpty(filtro.Texto)
                    ? ""
                    : $" AND v.origen = '{filtro.Texto.Replace("'", "''")}'"),
            // Fletero (diálogo "Buscar reservas por Fletero"): rango + viaje.fletero = X.
            TraficoFiltroTipo.Fletero =>
                $"v._deleted = 0 AND {rango} AND v.fletero = '{(filtro.Texto ?? "").Replace("'", "''")}' AND v.estado_via <> 'CANCELADO'",
            // Conductores (diálogo trafico_filtro_chofer.scx): rango + viaje.id_chofer = X.
            // En el FoxPro (bAceptar.Click) el chofer tiene prioridad sobre el fletero; acá el
            // filtro de fletero es un diálogo aparte, así que esta rama es siempre por id_chofer.
            TraficoFiltroTipo.Choferes =>
                $"v._deleted = 0 AND {rango} AND v.id_chofer = '{(filtro.Texto ?? "").Replace("'", "''")}' AND v.estado_via <> 'CANCELADO'",
            // Nº de Interno (diálogo trafico_filtro_interno.scx): rango + viaje.id_interno = X.
            // Se filtra por el CÓDIGO de unidad (NT0044, AG0001…) que vive en viaje.id_interno y es
            // único entre unidades activas — NO por el número suelto viaje.interno (que se repite y
            // no coincide con la grilla). El FoxPro original usaba el número; acá se mejora al código
            // que la operadora reconoce. El código se elige del combo de la nómina (no hay tipeo libre).
            TraficoFiltroTipo.Interno =>
                $"v._deleted = 0 AND {rango} AND v.id_interno = '{(filtro.Texto ?? "").Replace("'", "''")}' AND v.estado_via <> 'CANCELADO'",
            // Estados de la Reserva (diálogo trafico_filtro_tipo_estado.scx): rango + estado_via = X.
            // ÚNICA rama que puede traer CANCELADO: si el usuario elige justamente ese estado, NO se
            // aplica el descarte de cancelados (si no, nunca devolvería filas). Para cualquier otro
            // estado el descarte es redundante (el WHERE ya fija el estado), así que no se agrega.
            TraficoFiltroTipo.Estado =>
                $"v._deleted = 0 AND {rango} AND v.estado_via = '{(filtro.Texto ?? "").Replace("'", "''")}'",
            // Números de Vuelos (diálogo trafico_filtro_vuelo.scx): rango + viaje.vuelo = X.
            // Texto = "SIN VUELO" | "A CONFIRMAR" | el nº de vuelo elegido (modo Con Vuelo).
            TraficoFiltroTipo.Vuelo =>
                $"v._deleted = 0 AND {rango} AND v.vuelo = '{(filtro.Texto ?? "").Replace("'", "''")}' AND v.estado_via <> 'CANCELADO'",
            // Nº Reserva (diálogo trafico_nro_reserva.scx, código RESERVA): viaje.id_viaje = N.
            // IGNORA el rango de fechas (el nº de reserva es único en toda la base) — réplica fiel:
            // el FoxPro no aplica Between en esta rama. NO se descarta CANCELADO: si el operador busca
            // un nº puntual, lo quiere ver aunque esté cancelado (mismo criterio que el original, que
            // no filtra estado acá). ⚠️ No hay índice sobre viaje.id_viaje → este WHERE hace scan
            // completo (~84K lecturas en SQL 2012, ver skill modulo-trafico); es 1 disparo manual del
            // usuario, aceptable. id_viaje es int.
            TraficoFiltroTipo.Reserva =>
                $"v._deleted = 0 AND v.id_viaje = {filtro.Numero ?? 0}",
            // Nº Reserva En Ruta (mismo diálogo, código RESERVA_RUTA): viaje.id_viaje_i = N.
            // id_viaje_i (réplica de id_viaje_int, bigint) es el correlativo que agrupa los viajes de
            // una misma reserva "en ruta" (servicio multi-día del modo ruta de Reservas): un mismo
            // número devuelve los N días de esa ruta. TAMBIÉN ignora el rango (réplica fiel) y tampoco
            // descarta CANCELADO. Sin índice sobre id_viaje_i → scan; igual que arriba, disparo manual.
            TraficoFiltroTipo.ReservaRuta =>
                $"v._deleted = 0 AND v.id_viaje_i = {filtro.Numero ?? 0}",
            _ => throw new NotSupportedException($"Filtro de Tráfico aún no implementado: {filtro.Tipo}")
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var sql = $"""
            {TraficoProjection}
            WHERE {where}
            ORDER BY v.f_reserva, v.hs_inicio, v.hs_present
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var result = new List<PlanillaTraficoRow>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(MapPlanillaRow(reader));
        return result;
    }

    /// <summary>
    /// Lista de fleteros (empresas transportistas) para el combo del filtro "Fleteros" de Tráfico,
    /// réplica del diálogo "Buscar reservas por Fletero". Devuelve los códigos (fletero.id_contrat)
    /// ordenados por fletero.orden — que es lo que guarda viaje.fletero. Caché 5 min.
    /// </summary>
    public async Task<List<string>> GetFleterosAsync()
    {
        const string key = "trafico-fleteros";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            const string sql = """
                SELECT LTRIM(RTRIM(id_contrat)) AS f, MIN(orden) AS o
                FROM fletero
                WHERE _deleted = 0 AND LTRIM(RTRIM(id_contrat)) <> ''
                GROUP BY LTRIM(RTRIM(id_contrat))
                ORDER BY o, f
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Lista de conductores para el combo del filtro "Conductores" de Tráfico, réplica del
    /// diálogo trafico_filtro_chofer.scx. Devuelve (id_chofer, nombre) ordenado por id_chofer
    /// (igual que el cursor cursorTraficoChofer del FoxPro). El parámetro <paramref name="soloActivos"/>
    /// replica el checkbox "Solamente los conductores activos": tildado (true) ⇒ Empty(f_delete),
    /// destildado (false) ⇒ todos. viaje.id_chofer guarda este código. Caché 5 min por flag.
    /// </summary>
    public async Task<List<ChoferOpcion>> GetChoferesParaFiltroAsync(bool soloActivos)
    {
        var key = $"trafico-choferes-filtro|{(soloActivos ? "act" : "todos")}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // Réplica del SELECT del Init / Check1.Click de trafico_filtro_chofer.scx:
            //   Select id_chofer, nombre From chofer Where Empty(f_Delete) Order By id_chofer
            // (la cláusula Empty(f_delete) se omite cuando el checkbox está destildado).
            var sql = $"""
                SELECT RTRIM(ISNULL(id_chofer, '')) AS Codigo, RTRIM(ISNULL(nombre, '')) AS Nombre
                FROM chofer
                WHERE _deleted = 0
                  AND RTRIM(ISNULL(id_chofer, '')) <> ''
                  {(soloActivos ? "AND f_delete IS NULL" : "")}
                ORDER BY id_chofer
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = new List<ChoferOpcion>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(new ChoferOpcion(reader.GetString(0), reader.GetString(1)));
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Lista de unidades de la flota para el combo del filtro "Nº de Interno" de Tráfico.
    /// El FoxPro (trafico_filtro_interno.scx) filtraba por el número suelto viaje.interno, pero ese
    /// número se REPITE entre vehículos (162 distintos en 406 filas) y no coincide con lo que la
    /// operadora ve en la grilla. El CÓDIGO de unidad real (NT0044, AG0001, TT0109…) vive en
    /// `vehiculo.cronograma` y en `viaje.id_interno`, y entre unidades activas es ÚNICO. Por eso
    /// el combo lista los códigos de unidad de la nómina vigente (activo = 1, sin baja) con su
    /// número de interno y dominio para referencia. Caché 5 min.
    /// </summary>
    public async Task<List<InternoOpcion>> GetInternosParaFiltroAsync()
    {
        const string key = "trafico-internos-filtro";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // Código de unidad (cronograma) de la nómina activa. interno es bigint → CAST + GetInt64.
            // Orden por código (es lo que la operadora reconoce: NT0001, NT0002…).
            const string sql = """
                SELECT
                    LTRIM(RTRIM(cronograma))      AS Codigo,
                    CAST(interno AS bigint)       AS Interno,
                    RTRIM(ISNULL(dominio, ''))    AS Dominio
                FROM vehiculo
                WHERE _deleted = 0 AND activo = 1
                  AND LTRIM(RTRIM(ISNULL(cronograma, ''))) <> ''
                ORDER BY cronograma
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = new List<InternoOpcion>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(new InternoOpcion(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2)));
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Nº de vuelos reales que existen en `viaje` dentro de un rango de fechas — para el combo del
    /// modo "Con Vuelo" del filtro "Números de Vuelos" (trafico_filtro_vuelo.scx). En el FoxPro era
    /// un textbox libre; acá se ofrece la lista real del rango para evitar tipeos errados. Excluye
    /// los literales "SIN VUELO" / "A CONFIRMAR" (esos son los otros dos modos del OptionGroup) y
    /// los vacíos. NO se cachea: depende del rango que elige el usuario en el diálogo.
    /// </summary>
    public async Task<List<string>> GetVuelosEnRangoAsync(DateOnly desde, DateOnly hasta)
    {
        desde = ClampFecha(desde);
        hasta = ClampFecha(hasta);
        if (hasta < desde) (desde, hasta) = (hasta, desde);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var sql = $"""
            SELECT DISTINCT LTRIM(RTRIM(vuelo)) AS Vuelo
            FROM viaje
            WHERE _deleted = 0
              AND f_reserva BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'
              AND LTRIM(RTRIM(ISNULL(vuelo, ''))) NOT IN ('', 'SIN VUELO', 'A CONFIRMAR')
            ORDER BY Vuelo
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
        return result;
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
                (SELECT COUNT(*)           FROM viaje    WHERE f_reserva = '{dia:yyyyMMdd}') AS CantViajes,
                (SELECT MAX(_updated_at)   FROM viaje    WHERE f_reserva = '{dia:yyyyMMdd}') AS UltViaje,
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
    /// Limpia el caché de las grillas de ABM de solo lectura (Choferes/Clientes/Vehículos).
    /// La llama el botón "Actualizar" de cada grilla para forzar una recarga en vivo desde
    /// SQL aunque el TTL de 60s no haya vencido todavía.
    /// </summary>
    public void InvalidarCacheAbm()
    {
        _cache.Remove("choferes-lista");
        _cache.Remove("clientes-lista");
        _cache.Remove("vehiculos-lista");
        _cache.Remove("usuarios-lista");
        _cache.Remove("usuarios-ultima-sesion");
        _cache.Remove("siniestros-lista");
        _cache.Remove("fleteros-lista");
        _cache.Remove("tipos-vehiculo-lista");
        // Módulo Reservas: catálogos Operadores / Grupos / Destinos.
        _cache.Remove("operadores-lista");
        _cache.Remove("destinos-lista");
        _cache.Remove("destino-localidades");
        _cache.Remove("plantillas-resumen");   // Mantenimiento de Plantillas
        for (int f = 0; f <= 2; f++) _cache.Remove($"grupos-lista|{f}");   // los 3 filtros del combo
        // Reservas Especiales (grilla 'T') se cachea por filtros ("resesp|..."): se deja expirar por TTL.
        // Odómetros y Agenda de Vencimientos no se cachean por lista (dependen de filtros);
        // sus claves ("odometros|...", "agenda-venc|...") se dejan expirar por TTL.
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ABM DE USUARIOS Y PERMISOS (Sistema → Accesos del FoxPro: usuario.scx / usuario_abm.scx)
    //  Tabla `usuario`: id (int, PK física NO identity), usuario (nvarchar 15, PK lógica),
    //  password (nvarchar 15, TEXTO PLANO), nivel (nvarchar 5, "12345"), acceso (nvarchar 15,
    //  string de letras S R T C D V L F A E U B H X N M), f_create/f_modify/f_delete (date),
    //  operador (bit). f_delete cargado = inhabilitado (amarillo). La escritura vive en AbmService.
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lista de usuarios del sistema (réplica de la grilla de usuario.scx). "Inhabilitado" =
    /// f_delete con valor (se muestra en amarillo, no se oculta). Ordenada por nombre como el FoxPro.
    /// </summary>
    public async Task<List<UsuarioListaRow>> GetUsuariosAsync()
    {
        return await _cache.GetOrCreateAsync("usuarios-lista", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    id,
                    RTRIM(ISNULL(usuario, '')) AS Usuario,
                    RTRIM(ISNULL(nivel,   '')) AS Nivel,
                    RTRIM(ISNULL(acceso,  '')) AS Acceso,
                    ISNULL(operador, 0)        AS Operador,
                    f_delete                    AS FInhabilitacion
                FROM usuario
                WHERE _deleted = 0
                ORDER BY usuario
                """;
            var result = new List<UsuarioListaRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                result.Add(new UsuarioListaRow(
                    rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.GetBoolean(4),
                    rd.IsDBNull(5) ? null : DateOnly.FromDateTime(rd.GetDateTime(5))));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Ficha de un usuario por su id (para la ficha/edición del ABM). Devuelve null si no existe.
    /// El string `acceso` se decodifica en la UI a los 16 checkboxes de permisos.
    /// </summary>
    public async Task<UsuarioDetalleDto?> GetUsuarioDetalleAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1
                id,
                RTRIM(ISNULL(usuario,  '')) AS Usuario,
                RTRIM(ISNULL(password, '')) AS Password,
                RTRIM(ISNULL(nivel,    '')) AS Nivel,
                RTRIM(ISNULL(acceso,   '')) AS Acceso,
                ISNULL(operador, 0)         AS Operador,
                f_create, f_modify, f_delete
            FROM usuario
            WHERE id = @id AND _deleted = 0
            """;
        var pId = cmd.CreateParameter();
        pId.ParameterName = "@id";
        pId.Value = id;
        cmd.Parameters.Add(pId);

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return null;

        return new UsuarioDetalleDto
        {
            Id = rd.GetInt32(0),
            Usuario = rd.GetString(1),
            Password = rd.GetString(2),
            Nivel = rd.GetString(3),
            Acceso = rd.GetString(4),
            Operador = rd.GetBoolean(5),
            FCreate = rd.IsDBNull(6) ? null : DateOnly.FromDateTime(rd.GetDateTime(6)),
            FModify = rd.IsDBNull(7) ? null : DateOnly.FromDateTime(rd.GetDateTime(7)),
            FDelete = rd.IsDBNull(8) ? null : DateOnly.FromDateTime(rd.GetDateTime(8)),
        };
    }

    // ── Sesiones (historial de ingresos/egresos — tabla usuario_sesion) ──────────

    /// <summary>
    /// Última sesión de CADA usuario, indexada por id_usuario (para la grilla del ABM:
    /// último ingreso, última IP, y si está conectado ahora). Una sola query con la fila
    /// de f_inicio más reciente por usuario. Si la tabla no existe aún (server sin migrar),
    /// devuelve un diccionario vacío en vez de romper la grilla.
    /// </summary>
    public async Task<Dictionary<int, UltimaSesionRow>> GetUltimasSesionesAsync()
    {
        return await _cache.GetOrCreateAsync("usuarios-ultima-sesion", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            var result = new Dictionary<int, UltimaSesionRow>();
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // Tabla nueva: si no existe en este server, no fallar (server viejo sin migrar).
            using (var chk = conn.CreateCommand())
            {
                chk.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE table_name = 'usuario_sesion'";
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) == 0)
                    return result;
            }

            using var cmd = conn.CreateCommand();
            // La última fila por usuario (mayor f_inicio). ROW_NUMBER es válido en SQL 2012.
            cmd.CommandText = """
                SELECT id_usuario, f_inicio, f_fin, activa,
                       RTRIM(ISNULL(ip, '')), RTRIM(ISNULL(hostname, ''))
                FROM (
                    SELECT id_usuario, f_inicio, f_fin, activa, ip, hostname,
                           ROW_NUMBER() OVER (PARTITION BY id_usuario ORDER BY f_inicio DESC, id DESC) AS rn
                    FROM usuario_sesion
                    WHERE _deleted = 0
                ) t
                WHERE rn = 1
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var idu = rd.GetInt32(0);
                result[idu] = new UltimaSesionRow(
                    idu,
                    rd.IsDBNull(1) ? null : rd.GetDateTime(1),
                    rd.IsDBNull(2) ? null : rd.GetDateTime(2),
                    !rd.IsDBNull(3) && rd.GetBoolean(3),
                    rd.GetString(4),
                    rd.GetString(5));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Historial completo de sesiones de un usuario (para la ficha del ABM), más reciente
    /// primero. Cada fila = un ingreso, con su egreso (f_fin) y el detalle de IP/host/terminal.
    /// </summary>
    public async Task<List<SesionRow>> GetSesionesUsuarioAsync(int idUsuario, int tope = 100)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using (var chk = conn.CreateCommand())
        {
            chk.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE table_name = 'usuario_sesion'";
            if ((int)(await chk.ExecuteScalarAsync() ?? 0) == 0)
                return new();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT TOP {tope}
                   f_inicio, f_fin, activa,
                   RTRIM(ISNULL(ip, '')), RTRIM(ISNULL(hostname, '')),
                   ISNULL(terminal, 0), RTRIM(ISNULL(motivo_fin, ''))
            FROM usuario_sesion
            WHERE id_usuario = @id AND _deleted = 0
            ORDER BY f_inicio DESC, id DESC
            """;
        var pId = cmd.CreateParameter();
        pId.ParameterName = "@id";
        pId.Value = idUsuario;
        cmd.Parameters.Add(pId);

        var result = new List<SesionRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            result.Add(new SesionRow(
                rd.IsDBNull(0) ? null : rd.GetDateTime(0),
                rd.IsDBNull(1) ? null : rd.GetDateTime(1),
                !rd.IsDBNull(2) && rd.GetBoolean(2),
                rd.GetString(3), rd.GetString(4),
                rd.GetInt32(5), rd.GetString(6)));
        }
        return result;
    }

    /// <summary>
    /// Bitácora de accesos (tabla usuarios_logs) para la pantalla de Auditoría de accesos.
    /// Filtra por rango de fecha (obligatorio, acota el volumen), opcionalmente por usuario y
    /// por tipo de evento (lista vacía = todos). Devuelve un evento por fila, más reciente
    /// primero. Si la tabla no existe (server sin migrar), devuelve vacío.
    /// </summary>
    public async Task<List<AccesoLogRow>> GetAuditoriaAccesosAsync(
        DateOnly desde, DateOnly hasta, string? usuario, IEnumerable<string>? eventos, int tope = 5000)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using (var chk = conn.CreateCommand())
        {
            chk.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE table_name = 'usuarios_logs'";
            if ((int)(await chk.ExecuteScalarAsync() ?? 0) == 0)
                return new();
        }

        // Filtro de eventos (lista blanca de valores conocidos → seguro para concatenar).
        var permitidos = new[] { "LOGIN", "LOGOUT", "EXPIRADA", "VENCIDA", "LOGIN_FALLIDO" };
        var evFiltro = (eventos ?? Enumerable.Empty<string>())
            .Select(e => (e ?? "").Trim().ToUpperInvariant())
            .Where(e => permitidos.Contains(e))
            .Distinct().ToList();
        var evClause = evFiltro.Count > 0
            ? " AND evento IN (" + string.Join(",", evFiltro.Select(e => $"'{e}'")) + ")"
            : "";
        var usuClause = string.IsNullOrWhiteSpace(usuario)
            ? "" : $" AND usuario = '{usuario.Trim().Replace("'", "''")}'";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT TOP {tope}
                   id, session_id, evento, f_evento,
                   id_usuario, RTRIM(ISNULL(usuario, '')),
                   RTRIM(ISNULL(nivel, '')), RTRIM(ISNULL(acceso, '')),
                   RTRIM(ISNULL(ip, '')), RTRIM(ISNULL(hostname, '')), RTRIM(ISNULL(motivo, ''))
            FROM usuarios_logs
            WHERE _deleted = 0
              AND f_evento >= '{desde:yyyyMMdd}' AND f_evento < DATEADD(day, 1, '{hasta:yyyyMMdd}')
              {usuClause}{evClause}
            ORDER BY f_evento DESC, id DESC
            """;

        var result = new List<AccesoLogRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            result.Add(new AccesoLogRow(
                rd.GetInt32(0),
                rd.IsDBNull(1) ? (Guid?)null : rd.GetGuid(1),
                rd.GetString(2),
                rd.GetDateTime(3),
                rd.IsDBNull(4) ? (int?)null : rd.GetInt32(4),
                rd.GetString(5), rd.GetString(6), rd.GetString(7),
                rd.GetString(8), rd.GetString(9), rd.GetString(10)));
        }
        return result;
    }

    /// <summary>Nombres de usuario que aparecen en la bitácora (para el combo de filtro
    /// de la pantalla de Auditoría). Ordenados alfabéticamente.</summary>
    public async Task<List<string>> GetUsuariosDeLogsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using (var chk = conn.CreateCommand())
        {
            chk.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE table_name = 'usuarios_logs'";
            if ((int)(await chk.ExecuteScalarAsync() ?? 0) == 0)
                return new();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT RTRIM(ISNULL(usuario,'')) FROM usuarios_logs WHERE _deleted = 0 ORDER BY 1";
        var result = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var u = rd.GetString(0);
            if (!string.IsNullOrWhiteSpace(u)) result.Add(u);
        }
        return result;
    }

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
            // Se trae también la EMPRESA de cada interno (fletero.id_contrat — coincide con la
            // etiqueta del combo U/Programada, incluso para NORTUR cuyo id_contrat es 'NORTUR')
            // para la cascada empresa → internos entre los dos combos (pedido 16/07/2026).
            const string sqlAsignadas = """
                SELECT LTRIM(RTRIM(a.cronograma)) AS cron,
                       LTRIM(RTRIM(b.id_contrat)) AS empresa
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

            var asignadas = new List<UnidadAsignadaCombo>();
            var programadas = new List<string>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sqlAsignadas;
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    if (!reader.IsDBNull(0))
                        asignadas.Add(new UnidadAsignadaCombo(
                            reader.GetString(0),
                            reader.IsDBNull(1) ? "" : reader.GetString(1)));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sqlProgramadas;
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    if (!reader.IsDBNull(0)) programadas.Add(reader.GetString(0));
            }

            // El combo no admite vacíos ni repetidos (el FoxPro los mostraba tal cual).
            // U/Programada (pedido del usuario, 15/07/2026): se ocultan las unidades de la flota
            // NORTUR (prefijo "NT") y TODO el combo va ordenado alfabéticamente ascendente (no por
            // b.orden). Como NORTUR tiene diagrama=1, la query lo expande en sus unidades NT####
            // (que acabamos de ocultar); por eso se re-agrega la empresa como un ÚNICO ítem
            // "NORTUR", que queda en su lugar alfabético (entre MVTRAVEL y NUEVOS RUMBOS).
            // U/Asignada se deja como estaba.
            var programadasCombo = programadas
                .Where(c => c.Length > 0)
                .Where(c => !c.StartsWith("NT", StringComparison.OrdinalIgnoreCase))
                .Append("NORTUR")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CombosUnidadesTrafico(
                programadasCombo,
                asignadas.Where(c => c.Interno.Length > 0).Distinct().ToList());
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
                      AND c.fecha = '{hoy:yyyyMMdd}'
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
                    -- Largo del tramo origen — mismo criterio que TraficoProjection: la UI
                    -- parte "DESDE a HASTA" por índice, no buscando ' a ' (hay tramos con
                    -- ' a ' adentro). LEN ignora espacios finales; el LTRIM sí hace falta.
                    LEN(LTRIM(v.d_destino))                              AS RecorridoDesdeLen,
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
                  AND v.f_reserva = '{dia:yyyyMMdd}'
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
                    RecorridoDesdeLen: N(reader, "RecorridoDesdeLen") ?? 0,
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
    ///   - id_operado     → cliente_operador.nombre (operador = contacto del cliente; el Fox
    ///                       resuelve el operador SIEMPRE contra cliente_operador, no cliente)
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
                : $"AND v.f_reserva = '{fReserva.Value:yyyyMMdd}'";
            var sql = $"""
                SELECT
                    v.id_viaje                                  AS IdViaje,
                    v.f_pedido                                  AS FPedido,
                    v.f_reserva                                 AS FReserva,
                    v.estado_via                                AS Estado,
                    v.chequeo                                   AS Chequeo,
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
                    op.nombre                                   AS NombreOperador,
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
                    LEFT JOIN cliente_operador op  ON LTRIM(RTRIM(op.id_operado)) = LTRIM(RTRIM(v.id_operado)) AND op._deleted = 0
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
                Chequeo = N("Chequeo"),
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
                SELECT nombre, cantidad, precio, id_adicion
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
                    Precio: reader.IsDBNull(2) ? 0m : Convert.ToDecimal(reader.GetValue(2)),
                    Codigo: reader.IsDBNull(3) ? "" : reader.GetValue(3).ToString()!.Trim()
                ));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Ficha del operador de un viaje — réplica de "Ver Datos Operador" (submenú verdatosex de
    /// trafico2.scx → form cliente_operador_abm en modo "consulta"):
    ///   SELECT * FROM cliente_operador WHERE id_operador = viaje.id_operado
    /// Solo lectura. El operador es un contacto/persona dentro de un cliente (agencia), NO el
    /// "operador turístico" (que es otra cosa). Devuelve null si el viaje no tiene operador
    /// asignado o el código no existe (el Fox muestra "No hay asignado ningún operador").
    /// </summary>
    public async Task<OperadorDetalleDto?> GetOperadorDetalleAsync(string idOperador)
    {
        if (string.IsNullOrWhiteSpace(idOperador)) return null;
        var key = $"operador-detalle|{idOperador.Trim()}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // id_operado = nombre truncado de id_operador en la réplica. Se busca por el código
            // del operador (PK lógica). Escapado de comilla simple por las dudas (regla del proyecto).
            cmd.CommandText = $"""
                SELECT TOP 1 id_operado, id_cliente, nombre, telefono, celular,
                       nextel, interno, email, comentario
                FROM cliente_operador
                WHERE _deleted = 0 AND LTRIM(RTRIM(id_operado)) = '{idOperador.Trim().Replace("'", "''")}'
                """;
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            string S(string c) { var i = reader.GetOrdinal(c); return reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString()!.Trim(); }
            return new OperadorDetalleDto(
                IdOperador: S("id_operado"),
                IdCliente: S("id_cliente"),
                Nombre: S("nombre"),
                Telefono: S("telefono"),
                Celular: S("celular"),
                Nextel: S("nextel"),
                Interno: S("interno"),
                Email: S("email"),
                Comentario: S("comentario"));
        });
    }

    /// <summary>
    /// Texto del recorrido de cabecera de un viaje — réplica de "Ver Recorrido" (submenú
    /// verdatosex de trafico2.scx → form cabecera_recorrido_abm_zoom en modo "consulta"):
    ///   SELECT * FROM cabecera WHERE codigo = viaje.gps_cod
    /// y muestra cabecera.recorrido (descripción larga del circuito) en solo lectura.
    /// Devuelve null si el viaje no tiene gps_cod o el código no existe en cabecera (el Fox
    /// muestra "No hay ningún código de cabecera en ese viaje" / "No se encuentra datos...").
    /// </summary>
    public async Task<RecorridoCabeceraDto?> GetRecorridoCabeceraAsync(string gpsCod)
    {
        if (string.IsNullOrWhiteSpace(gpsCod)) return null;
        var key = $"recorrido-cabecera|{gpsCod.Trim()}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT TOP 1 codigo, nombre, nombre1, nombre2, recorrido
                FROM cabecera
                WHERE _deleted = 0 AND LTRIM(RTRIM(codigo)) = '{gpsCod.Trim().Replace("'", "''")}'
                """;
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            string S(string c) { var i = reader.GetOrdinal(c); return reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString()!.Trim(); }
            return new RecorridoCabeceraDto(
                Codigo: S("codigo"),
                Nombre: S("nombre"),
                Nombre1: S("nombre1"),
                Nombre2: S("nombre2"),
                Recorrido: S("recorrido"));
        });
    }

    /// <summary>
    /// Ruta cruda del archivo adjunto de un viaje (viaje.file) — para "Ver Adjunto" (submenú
    /// verdatosex de trafico2.scx). El FoxPro abría esa ruta con Shell.ShellExecute; acá la
    /// resuelve AdjuntoService contra la carpeta accesible por el servidor. Devuelve la ruta
    /// tal cual está en la base (ej O:\METROCARSYS\ADJUNTOS\x.pdf) o "" si no hay adjunto.
    /// Se acota por f_reserva (igual que el Zoom) para SEEK por ix_viaje_f_reserva en vez de scan.
    /// </summary>
    public async Task<string> GetRutaAdjuntoViajeAsync(int idViaje, DateOnly? fReserva = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        var fResFiltro = fReserva is null ? "" : $"AND f_reserva = '{fReserva.Value:yyyyMMdd}'";
        cmd.CommandText = $"""
            SELECT TOP 1 [file]
            FROM viaje
            WHERE _deleted = 0 {fResFiltro} AND id_viaje = {idViaje}
            """;
        var val = await cmd.ExecuteScalarAsync();
        return val is null || val is DBNull ? "" : val.ToString()!.Trim();
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

    // Las 6 bandas horarias (idénticas a viaje_horario del FoxPro). Orden fijo — se usa para
    // el orden de columnas del pivote y de las series del gráfico apilado.
    public static readonly IReadOnlyList<string> BandasHorarias =
        new[] { "00:00-00:01", "00:02-06:29", "06:30-08:29", "08:30-14:00", "14:01-18:00", "18:01-23:59" };

    // Expresión SQL que clasifica un viaje en su banda por CAST(hs_inicio AS TIME).
    // Comparación por strings "HH:mm" (bordes inclusivos) para reproducir el FoxPro al dígito.
    // Se comparte entre la vista agregada y el detalle para que ambas den la MISMA banda.
    private const string BandaCaseSql = """
        CASE
            WHEN CAST(v.hs_inicio AS TIME) BETWEEN '00:00' AND '00:01' THEN '00:00-00:01'
            WHEN CAST(v.hs_inicio AS TIME) BETWEEN '00:02' AND '06:29' THEN '00:02-06:29'
            WHEN CAST(v.hs_inicio AS TIME) BETWEEN '06:30' AND '08:29' THEN '06:30-08:29'
            WHEN CAST(v.hs_inicio AS TIME) BETWEEN '08:30' AND '14:00' THEN '08:30-14:00'
            WHEN CAST(v.hs_inicio AS TIME) BETWEEN '14:01' AND '18:00' THEN '14:01-18:00'
            WHEN CAST(v.hs_inicio AS TIME) BETWEEN '18:01' AND '23:59' THEN '18:01-23:59'
            ELSE NULL
        END
        """;

    // WHERE compartido del informe "Reservas por banda horaria" (agregado + detalle).
    // Fiel al FoxPro: solo origen='T', excluye el cliente interno (parametro.id_cliente),
    // exige hs_inicio no nula. Los estados se pasan como parámetro (default de la UI = todos
    // menos CANCELADO); si vienen todos (5) o vacío, no se filtra por estado.
    private static string WhereBandaHoraria(
        DateOnly desde,
        DateOnly hasta,
        IReadOnlyCollection<string> vehiculosSel,
        IReadOnlyCollection<string> estadosSel)
    {
        var where = new List<string>
        {
            "v._deleted = 0",
            $"v.f_reserva BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'",
            "v.origen = 'T'",
            "v.id_cliente <> (SELECT TOP 1 RTRIM(ISNULL(id_cliente, '')) FROM parametro)",
            "v.hs_inicio IS NOT NULL"
        };
        if (estadosSel.Count > 0 && estadosSel.Count < EstadosViaje.Count)
        {
            var lista = string.Join(",", estadosSel.Select(s => $"'{s.Replace("'", "''")}'"));
            where.Add($"v.estado_via IN ({lista})");
        }
        if (vehiculosSel.Count > 0)
        {
            var lista = string.Join(",", vehiculosSel.Select(v => $"'{v.Replace("'", "''")}'"));
            where.Add($"v.id_vehicul IN ({lista})");
        }
        return string.Join(" AND ", where);
    }

    // Vista agregada: conteo de viajes + suma de pax por fecha × tipo de vehículo × banda.
    // Trae Reservas y Pax juntos para que el toggle de métrica en la UI recalcule en memoria
    // (sin re-query), igual que el informe de fecha/servicio.
    public async Task<List<BandaHorariaRow>> GetReservasPorBandaHorariaAsync(
        DateOnly desde,
        DateOnly hasta,
        IReadOnlyCollection<string> vehiculosSel,
        IReadOnlyCollection<string> estadosSel)
    {
        var vehKey = vehiculosSel.Count == 0 ? "all" : string.Join(",", vehiculosSel.OrderBy(x => x));
        var estKey = estadosSel.Count == 0 ? "all" : string.Join(",", estadosSel.OrderBy(x => x));
        var key = $"rbh|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{vehKey}|{estKey}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var sql = $"""
                SELECT
                    v.f_reserva                       AS Fecha,
                    v.id_vehicul                      AS TipoVehiculo,
                    {BandaCaseSql}                    AS Banda,
                    COUNT(*)                          AS Reservas,
                    SUM(COALESCE(v.pax, 0))           AS Pax
                FROM viaje v
                WHERE {WhereBandaHoraria(desde, hasta, vehiculosSel, estadosSel)}
                GROUP BY v.f_reserva, v.id_vehicul, {BandaCaseSql}
                ORDER BY v.f_reserva, v.id_vehicul
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var result = new List<BandaHorariaRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var banda = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (banda is null) continue;   // hs_inicio fuera de toda banda (raro): se descarta
                result.Add(new BandaHorariaRow(
                    DateOnly.FromDateTime(reader.GetDateTime(0)),
                    reader.IsDBNull(1) ? "" : reader.GetString(1).Trim(),
                    banda,
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4)
                ));
            }
            return result;
        }) ?? new();
    }

    // Detalle uno-por-uno para el drill-down (click en celda/fila/columna del pivote).
    // Reusa el mismo DTO que el informe de fecha/servicio (ReservaFsDetalleRow) para poder
    // reusar también ReservasFsDetalleDialog + el Zoom del Viaje. Trae la banda de cada viaje
    // (con el mismo CASE) para poder filtrar en memoria por (fecha × banda). Se cachea 1 vez
    // por combinación de filtros; se pide recién al primer click/Excel.
    public async Task<List<BandaHorariaDetalleRow>> GetReservasBandaHorariaDetalleAsync(
        DateOnly desde,
        DateOnly hasta,
        IReadOnlyCollection<string> vehiculosSel,
        IReadOnlyCollection<string> estadosSel)
    {
        var vehKey = vehiculosSel.Count == 0 ? "all" : string.Join(",", vehiculosSel.OrderBy(x => x));
        var estKey = estadosSel.Count == 0 ? "all" : string.Join(",", estadosSel.OrderBy(x => x));
        var key = $"rbhdet|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{vehKey}|{estKey}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // viaje.id_viaje y viaje.pax son int (no bigint) — leer con GetInt32.
            var sql = $"""
                SELECT
                    v.id_viaje                                            AS IdViaje,
                    v.f_reserva                                           AS Fecha,
                    {BandaCaseSql}                                        AS Banda,
                    RTRIM(ISNULL(v.id_vehicul, ''))                       AS TipoVehiculo,
                    COALESCE(CONVERT(varchar(5), v.hs_inicio, 108), '')   AS Hora,
                    COALESCE(s.nombre, v.id_servici, '')                  AS Servicio,
                    COALESCE(NULLIF(LTRIM(RTRIM(v.nombre_cli)), ''), v.id_cliente, '') AS Cliente,
                    LTRIM(RTRIM(COALESCE(v.d_destino, ''))) +
                        CASE WHEN LTRIM(RTRIM(COALESCE(v.h_destino, ''))) <> ''
                             THEN ' a ' + LTRIM(RTRIM(v.h_destino)) ELSE '' END AS Recorrido,
                    COALESCE(v.pax, 0)                                    AS Pax,
                    COALESCE(v.estado_via, '')                            AS Estado,
                    COALESCE(v.nombre_cho, '')                            AS Chofer,
                    v.interno                                             AS Interno,
                    COALESCE(v.origen, '')                                AS Origen,
                    COALESCE(v.grupo, '')                                 AS Grupo
                FROM viaje v
                LEFT JOIN servicio s ON v.id_servici = s.id_servici
                WHERE {WhereBandaHoraria(desde, hasta, vehiculosSel, estadosSel)}
                ORDER BY v.f_reserva, v.hs_inicio, v.id_viaje
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var result = new List<BandaHorariaDetalleRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var banda = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (banda is null) continue;
                result.Add(new BandaHorariaDetalleRow(
                    Banda: banda,
                    TipoVehiculo: reader.GetString(3).Trim(),
                    Reserva: new ReservaFsDetalleRow(
                        IdViaje: reader.GetInt32(0),
                        Fecha: DateOnly.FromDateTime(reader.GetDateTime(1)),
                        Hora: reader.GetString(4),
                        CodServicio: "",
                        Servicio: reader.GetString(5).Trim(),
                        Cliente: reader.GetString(6).Trim(),
                        Recorrido: reader.GetString(7).Trim(),
                        Pax: reader.GetInt32(8),
                        Estado: reader.GetString(9).Trim(),
                        Chofer: reader.GetString(10).Trim(),
                        Interno: reader.IsDBNull(11) ? null : Convert.ToInt32(reader.GetValue(11)),
                        Origen: reader.GetString(12).Trim(),
                        Grupo: reader.GetString(13).Trim())));
            }
            return result;
        }) ?? new();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Informe "Reservas por cliente" (FoxPro: viaje_analisis.scx, menú Utilitarios).
    // Cuenta viajes de transportación (origen='T') por cliente × mes × tipo de unidad.
    // Plano completo: docs/PlanoFoxPro/reservas/RESERVAS_INFORME_POR_CLIENTE.md
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Los 3 tipos del informe por cliente, en orden fijo de columnas/series.</summary>
    public static readonly IReadOnlyList<string> TiposReservaCliente =
        new[] { "PROPIO", "CONTRATADO", "SIN REALIZAR" };

    // Clasificación del FoxPro por el interno de la unidad asignada al viaje:
    // 0 (o NULL en la réplica) = sin unidad todavía; <1000 = flota propia; >=1000 = fletero.
    private const string TipoReservaClienteCaseSql = """
        CASE WHEN ISNULL(v.interno, 0) = 0 THEN 'SIN REALIZAR'
             WHEN v.interno < 1000 THEN 'PROPIO'
             ELSE 'CONTRATADO' END
        """;

    /// <summary>Catálogo de motivos de cancelación (viaje_motivo_cancela, 6 filas).</summary>
    public async Task<List<MotivoCancelaDto>> GetMotivosCancelacionAsync()
    {
        return await _cache.GetOrCreateAsync("motivos-cancela", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, RTRIM(ISNULL(motivo, '')) AS motivo
                FROM viaje_motivo_cancela
                WHERE _deleted = 0
                ORDER BY id
                """;
            var result = new List<MotivoCancelaDto>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(new MotivoCancelaDto(Convert.ToInt32(reader.GetValue(0)), reader.GetString(1)));
            return result;
        }) ?? new();
    }

    // WHERE compartido del informe por cliente (agregado + detalle). Fiel al FoxPro en lo
    // esencial (origen='T'); las mejoras acordadas con el usuario (03/07/2026):
    //  - incluirInterno: el cliente de parametro.id_cliente (NORTUR, ~30% del volumen acá)
    //    se excluye por defecto — el FoxPro lo incluía.
    //  - canceladas=false → viajes NO cancelados (ISNULL(id_motivo,0)=0 — en la réplica el
    //    campo viene NULL donde el DBF tenía 0).
    //  - canceladas=true → SIEMPRE respeta el período (el FoxPro barría todo el histórico) y
    //    filtra por los motivos elegidos (lista vacía = todos los motivos, no solo el 2).
    private static string WhereReservasPorCliente(
        DateOnly desde,
        DateOnly hasta,
        bool incluirInterno,
        bool canceladas,
        IReadOnlyCollection<int> motivosSel)
    {
        var where = new List<string>
        {
            "v._deleted = 0",
            $"v.f_reserva BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'",
            "v.origen = 'T'"
        };
        if (!incluirInterno)
            where.Add("v.id_cliente <> (SELECT TOP 1 RTRIM(ISNULL(id_cliente, '')) FROM parametro)");
        if (!canceladas)
            where.Add("ISNULL(v.id_motivo, 0) = 0");
        else if (motivosSel.Count > 0)
            where.Add($"v.id_motivo IN ({string.Join(",", motivosSel)})");
        else
            where.Add("ISNULL(v.id_motivo, 0) > 0");
        return string.Join(" AND ", where);
    }

    // Vista agregada: viajes + pax por mes × cliente × tipo. Agrupa por id_cliente (no por el
    // nombre desnormalizado del FoxPro: un cliente renombrado partía en dos filas del pivot)
    // y muestra el nombre más reciente. Trae ambas métricas para el toggle Viajes↔Pax en
    // memoria, sin re-query.
    public async Task<List<ReservaClienteRow>> GetReservasPorClienteAsync(
        DateOnly desde,
        DateOnly hasta,
        bool incluirInterno,
        bool canceladas,
        IReadOnlyCollection<int> motivosSel)
    {
        var motKey = motivosSel.Count == 0 ? "all" : string.Join(",", motivosSel.OrderBy(x => x));
        var key = $"rpc|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{incluirInterno}|{canceladas}|{motKey}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // Mes 'yyyy-MM' con CONVERT(char(7), ..., 120) — SQL 2012-friendly.
            var sql = $"""
                SELECT
                    CONVERT(char(7), v.f_reserva, 120)            AS Mes,
                    RTRIM(ISNULL(v.id_cliente, ''))               AS IdCliente,
                    MAX(COALESCE(NULLIF(LTRIM(RTRIM(v.nombre_cli)), ''), RTRIM(v.id_cliente), '')) AS Cliente,
                    {TipoReservaClienteCaseSql}                   AS Tipo,
                    COUNT(*)                                      AS Viajes,
                    SUM(COALESCE(v.pax, 0))                       AS Pax
                FROM viaje v
                WHERE {WhereReservasPorCliente(desde, hasta, incluirInterno, canceladas, motivosSel)}
                GROUP BY CONVERT(char(7), v.f_reserva, 120), RTRIM(ISNULL(v.id_cliente, '')),
                    {TipoReservaClienteCaseSql}
                ORDER BY Mes, IdCliente
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var result = new List<ReservaClienteRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new ReservaClienteRow(
                    reader.GetString(0),
                    reader.GetString(1).Trim(),
                    reader.GetString(2).Trim(),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
                ));
            }
            return result;
        }) ?? new();
    }

    // Detalle uno-por-uno para el drill-down y el Excel. Reusa ReservaFsDetalleRow para reusar
    // ReservasFsDetalleDialog + Zoom del Viaje. Lleva mes/cliente/tipo para filtrar en memoria
    // y el motivo de cancelación (para la hoja Viajes del Excel en modo canceladas).
    public async Task<List<ReservaClienteDetalleRow>> GetReservasPorClienteDetalleAsync(
        DateOnly desde,
        DateOnly hasta,
        bool incluirInterno,
        bool canceladas,
        IReadOnlyCollection<int> motivosSel)
    {
        var motKey = motivosSel.Count == 0 ? "all" : string.Join(",", motivosSel.OrderBy(x => x));
        var key = $"rpcdet|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{incluirInterno}|{canceladas}|{motKey}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // viaje.id_viaje y viaje.pax son int (no bigint) — leer con GetInt32.
            var sql = $"""
                SELECT
                    v.id_viaje                                            AS IdViaje,
                    v.f_reserva                                           AS Fecha,
                    CONVERT(char(7), v.f_reserva, 120)                    AS Mes,
                    RTRIM(ISNULL(v.id_cliente, ''))                       AS IdCliente,
                    {TipoReservaClienteCaseSql}                           AS Tipo,
                    COALESCE(CONVERT(varchar(5), v.hs_inicio, 108), '')   AS Hora,
                    COALESCE(s.nombre, v.id_servici, '')                  AS Servicio,
                    COALESCE(NULLIF(LTRIM(RTRIM(v.nombre_cli)), ''), v.id_cliente, '') AS Cliente,
                    LTRIM(RTRIM(COALESCE(v.d_destino, ''))) +
                        CASE WHEN LTRIM(RTRIM(COALESCE(v.h_destino, ''))) <> ''
                             THEN ' a ' + LTRIM(RTRIM(v.h_destino)) ELSE '' END AS Recorrido,
                    COALESCE(v.pax, 0)                                    AS Pax,
                    COALESCE(v.estado_via, '')                            AS Estado,
                    COALESCE(v.nombre_cho, '')                            AS Chofer,
                    v.interno                                             AS Interno,
                    COALESCE(v.origen, '')                                AS Origen,
                    COALESCE(v.grupo, '')                                 AS Grupo,
                    RTRIM(ISNULL(m.motivo, ''))                           AS Motivo
                FROM viaje v
                LEFT JOIN servicio s ON v.id_servici = s.id_servici
                LEFT JOIN viaje_motivo_cancela m ON v.id_motivo = m.id
                WHERE {WhereReservasPorCliente(desde, hasta, incluirInterno, canceladas, motivosSel)}
                ORDER BY v.f_reserva, v.hs_inicio, v.id_viaje
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var result = new List<ReservaClienteDetalleRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new ReservaClienteDetalleRow(
                    Mes: reader.GetString(2),
                    IdCliente: reader.GetString(3).Trim(),
                    Tipo: reader.GetString(4),
                    Motivo: reader.GetString(15),
                    Reserva: new ReservaFsDetalleRow(
                        IdViaje: reader.GetInt32(0),
                        Fecha: DateOnly.FromDateTime(reader.GetDateTime(1)),
                        Hora: reader.GetString(5),
                        CodServicio: "",
                        Servicio: reader.GetString(6).Trim(),
                        Cliente: reader.GetString(7).Trim(),
                        Recorrido: reader.GetString(8).Trim(),
                        Pax: reader.GetInt32(9),
                        Estado: reader.GetString(10).Trim(),
                        Chofer: reader.GetString(11).Trim(),
                        Interno: reader.IsDBNull(12) ? null : Convert.ToInt32(reader.GetValue(12)),
                        Origen: reader.GetString(13).Trim(),
                        Grupo: reader.GetString(14).Trim())));
            }
            return result;
        }) ?? new();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Informe "Viajes por Choferes" (menú Utilitarios → Viajes por Choferes del FoxPro,
    // form viaje_analisis_chofer.scx). Cuenta viajes por chofer × día con desglose
    // turismo (origen='T') / cabecera (origen='P'), km, horario del día y duración.
    // Réplica mejorada: rango de fechas libre (el FoxPro filtraba un solo mes) y switch
    // Propios/Contratados (el FoxPro fijaba tipo_chofe='PROPIO').
    // Plano: docs/PlanoFoxPro/vehiculos-choferes/VIAJES_POR_CHOFER.md
    //
    // Trampas de la réplica SQL (confirmadas contra sys.columns 04/07/2026):
    //   str_f_reserva→str_f_rese, tipo_chofer→tipo_chofe, estado_viaje→estado_via,
    //   id_vehiculo→id_vehicu2 (¡dominio!), id_vehiculo_tipo→id_vehicul (¡el tipo!).
    //   El FoxPro filtra id_motivo=0; en la réplica viene NULL → ISNULL(id_motivo,0)=0.
    //   El FoxPro excluye tipo_chofe vacío al pedir ='PROPIO'; con "incluir contratados"
    //   sumamos también los CONTRATADO pero seguimos descartando el tipo vacío (35k filas
    //   sin clasificar) para no inflar con datos sin unidad.
    // ─────────────────────────────────────────────────────────────────────────

    // WHERE compartido del informe de choferes (agregado + detalle). incluirInterno suma el
    // cliente NORTUR (parametro.id_cliente); incluirContratados suma tipo_chofe='CONTRATADO'.
    private static string WhereViajesPorChofer(
        DateOnly desde, DateOnly hasta, bool incluirInterno, bool incluirContratados)
    {
        var where = new List<string>
        {
            "v._deleted = 0",
            $"v.f_reserva BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'",
            "v.estado_via <> 'CANCELADO'",
            "ISNULL(v.interno, 0) > 0",
            "ISNULL(v.id_motivo, 0) = 0",
            "NULLIF(LTRIM(RTRIM(v.id_chofer)), '') IS NOT NULL"
        };
        // Solo choferes con tipo clasificado (PROPIO / CONTRATADO); nunca el tipo vacío.
        where.Add(incluirContratados
            ? "v.tipo_chofe IN ('PROPIO', 'CONTRATADO')"
            : "v.tipo_chofe = 'PROPIO'");
        if (!incluirInterno)
            where.Add("v.id_cliente <> (SELECT TOP 1 RTRIM(ISNULL(id_cliente, '')) FROM parametro)");
        return string.Join(" AND ", where);
    }

    /// <summary>
    /// Agregado por chofer × día: viajes, turismo, cabecera, km, primer/último horario y pax.
    /// La fila "franco" (día sin viajes entre el primer y último día trabajado del chofer) se
    /// calcula en memoria en la página, como hacía el form FoxPro.
    /// </summary>
    public async Task<List<ViajesChoferRow>> GetViajesPorChoferAsync(
        DateOnly desde, DateOnly hasta, bool incluirInterno, bool incluirContratados)
    {
        var key = $"vpc|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{incluirInterno}|{incluirContratados}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var sql = $"""
                SELECT
                    RTRIM(ISNULL(v.id_chofer, ''))                        AS IdChofer,
                    MAX(COALESCE(NULLIF(LTRIM(RTRIM(v.nombre_cho)), ''),
                        RTRIM(ISNULL(c.nombre, '')), RTRIM(v.id_chofer))) AS Chofer,
                    MAX(RTRIM(ISNULL(c.localidad, '')))                   AS Localidad,
                    MAX(RTRIM(ISNULL(v.tipo_chofe, '')))                  AS Tipo,
                    v.f_reserva                                           AS Fecha,
                    COUNT(*)                                              AS Viajes,
                    SUM(CASE WHEN v.origen = 'T' THEN 1 ELSE 0 END)       AS Turismo,
                    SUM(CASE WHEN v.origen = 'P' THEN 1 ELSE 0 END)       AS Cabecera,
                    SUM(CAST(ISNULL(v.km, 0) AS bigint))                  AS Km,
                    SUM(COALESCE(v.pax, 0))                               AS Pax,
                    CONVERT(varchar(5), MIN(v.hs_inicio), 108)            AS HoraInicio,
                    CONVERT(varchar(5), MAX(v.hs_fin), 108)               AS HoraFin
                FROM viaje v
                LEFT JOIN chofer c ON c.id_chofer = v.id_chofer AND c._deleted = 0
                WHERE {WhereViajesPorChofer(desde, hasta, incluirInterno, incluirContratados)}
                GROUP BY RTRIM(ISNULL(v.id_chofer, '')), v.f_reserva
                ORDER BY IdChofer, v.f_reserva
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = new List<ViajesChoferRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new ViajesChoferRow(
                    IdChofer: reader.GetString(0).Trim(),
                    Chofer: reader.GetString(1).Trim(),
                    Localidad: reader.GetString(2).Trim(),
                    Tipo: reader.GetString(3).Trim(),
                    Fecha: DateOnly.FromDateTime(reader.GetDateTime(4)),
                    Viajes: reader.GetInt32(5),
                    Turismo: reader.GetInt32(6),
                    Cabecera: reader.GetInt32(7),
                    Km: reader.IsDBNull(8) ? 0 : Convert.ToInt32(reader.GetValue(8)),
                    Pax: reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                    HoraInicio: reader.IsDBNull(10) ? "" : reader.GetString(10),
                    HoraFin: reader.IsDBNull(11) ? "" : reader.GetString(11)));
            }
            return result;
        }) ?? new();
    }

    /// <summary>Detalle uno-por-uno para el drill-down/Excel del informe de choferes. IdChofer +
    /// Fecha para filtrar en memoria; el resto reusa ReservaFsDetalleRow (Zoom del Viaje).</summary>
    public async Task<List<ViajesChoferDetalleRow>> GetViajesPorChoferDetalleAsync(
        DateOnly desde, DateOnly hasta, bool incluirInterno, bool incluirContratados)
    {
        var key = $"vpcdet|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{incluirInterno}|{incluirContratados}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var sql = $"""
                SELECT
                    v.id_viaje                                            AS IdViaje,
                    v.f_reserva                                           AS Fecha,
                    RTRIM(ISNULL(v.id_chofer, ''))                        AS IdChofer,
                    COALESCE(CONVERT(varchar(5), v.hs_inicio, 108), '')   AS Hora,
                    COALESCE(s.nombre, v.id_servici, '')                  AS Servicio,
                    COALESCE(NULLIF(LTRIM(RTRIM(v.nombre_cli)), ''), v.id_cliente, '') AS Cliente,
                    LTRIM(RTRIM(COALESCE(v.d_destino, ''))) +
                        CASE WHEN LTRIM(RTRIM(COALESCE(v.h_destino, ''))) <> ''
                             THEN ' a ' + LTRIM(RTRIM(v.h_destino)) ELSE '' END AS Recorrido,
                    COALESCE(v.pax, 0)                                    AS Pax,
                    COALESCE(v.estado_via, '')                            AS Estado,
                    COALESCE(v.nombre_cho, '')                            AS Chofer,
                    v.interno                                             AS Interno,
                    COALESCE(v.origen, '')                                AS Origen,
                    COALESCE(v.grupo, '')                                 AS Grupo
                FROM viaje v
                LEFT JOIN servicio s ON v.id_servici = s.id_servici
                WHERE {WhereViajesPorChofer(desde, hasta, incluirInterno, incluirContratados)}
                ORDER BY v.f_reserva, v.hs_inicio, v.id_viaje
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = new List<ViajesChoferDetalleRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new ViajesChoferDetalleRow(
                    IdChofer: reader.GetString(2).Trim(),
                    Fecha: DateOnly.FromDateTime(reader.GetDateTime(1)),
                    Reserva: new ReservaFsDetalleRow(
                        IdViaje: reader.GetInt32(0),
                        Fecha: DateOnly.FromDateTime(reader.GetDateTime(1)),
                        Hora: reader.GetString(3),
                        CodServicio: "",
                        Servicio: reader.GetString(4).Trim(),
                        Cliente: reader.GetString(5).Trim(),
                        Recorrido: reader.GetString(6).Trim(),
                        Pax: reader.GetInt32(7),
                        Estado: reader.GetString(8).Trim(),
                        Chofer: reader.GetString(9).Trim(),
                        Interno: reader.IsDBNull(10) ? null : Convert.ToInt32(reader.GetValue(10)),
                        Origen: reader.GetString(11).Trim(),
                        Grupo: reader.GetString(12).Trim())));
            }
            return result;
        }) ?? new();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Informe "Km Unidades vs Servicios" (menú Utilitarios → Km Unidades Vs Servicios,
    // form viaje_analisis_km.scx). Por vehículo (dominio): km de servicio (SUM viaje.km),
    // km recorridos reales (odómetro vehiculo_km del/los mes/es del rango), km vacío
    // (recorrido − servicio), % vacío, días trabajados, promedio vacío/día, consumo.
    // Réplica mejorada: rango libre + switch Propios/Contratados.
    // Plano: docs/PlanoFoxPro/vehiculos-choferes/KM_UNIDADES_VS_SERVICIOS.md
    //
    // Trampas: el vehículo del viaje es v.id_vehicu2 (dominio); v.id_vehicul es el TIPO.
    //   El odómetro vehiculo_km.dominio = v.id_vehicu2 = vehiculo.dominio. ano_y_mes es
    //   'AAAAMM'. km_fin puede venir NULL/0 (mes en curso) → recorrido/vacío se calculan
    //   solo cuando hay odómetro cerrado; si no, quedan en 0 y % vacío es NULL (—).
    // ─────────────────────────────────────────────────────────────────────────

    private static string WhereKmUnidades(
        DateOnly desde, DateOnly hasta, bool incluirInterno, bool incluirContratados)
    {
        var where = new List<string>
        {
            "v._deleted = 0",
            $"v.f_reserva BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'",
            "v.estado_via <> 'CANCELADO'",
            "ISNULL(v.interno, 0) > 0",
            "NULLIF(LTRIM(RTRIM(v.id_vehicu2)), '') IS NOT NULL"
        };
        where.Add(incluirContratados
            ? "v.tipo_chofe IN ('PROPIO', 'CONTRATADO')"
            : "v.tipo_chofe = 'PROPIO'");
        if (!incluirInterno)
            where.Add("v.id_cliente <> (SELECT TOP 1 RTRIM(ISNULL(id_cliente, '')) FROM parametro)");
        return string.Join(" AND ", where);
    }

    /// <summary>
    /// Una fila por vehículo con km de servicio, km recorridos (odómetro), km vacío, % vacío,
    /// días trabajados, promedio vacío/día y consumo. El odómetro suma los meses del rango.
    /// </summary>
    public async Task<List<KmUnidadRow>> GetKmUnidadesAsync(
        DateOnly desde, DateOnly hasta, bool incluirInterno, bool incluirContratados)
    {
        var key = $"kmu|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{incluirInterno}|{incluirContratados}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // Lista de 'AAAAMM' de los meses que toca el rango, para sumar el odómetro.
            var meses = new List<string>();
            for (var d = new DateOnly(desde.Year, desde.Month, 1); d <= hasta; d = d.AddMonths(1))
                meses.Add($"{d.Year:0000}{d.Month:00}");
            var mesesIn = string.Join(",", meses.Select(m => $"'{m}'"));

            // Agregado de servicio por dominio (id_vehicu2) + días distintos trabajados.
            // OUTER APPLY suma el odómetro de esos meses (km_inicio del primer mes, km_fin del
            // último con dato) y trae el tipo/consumo del vehículo.
            var sql = $"""
                SELECT
                    s.Dominio,
                    ISNULL(ve.interno, 0)                                 AS Interno,
                    RTRIM(ISNULL(ve.id_vehicu2, ''))                      AS TipoVeh,
                    ISNULL(ve.autonomia, 0)                               AS Consumo,
                    s.Servicios,
                    s.KmServicio,
                    s.DiasTrabajados,
                    ISNULL(od.KmInicial, 0)                               AS KmInicial,
                    ISNULL(od.KmFinal, 0)                                 AS KmFinal
                FROM (
                    SELECT
                        RTRIM(ISNULL(v.id_vehicu2, ''))                   AS Dominio,
                        COUNT(*)                                          AS Servicios,
                        SUM(CAST(ISNULL(v.km, 0) AS bigint))              AS KmServicio,
                        COUNT(DISTINCT v.f_reserva)                       AS DiasTrabajados
                    FROM viaje v
                    WHERE {WhereKmUnidades(desde, hasta, incluirInterno, incluirContratados)}
                    GROUP BY RTRIM(ISNULL(v.id_vehicu2, ''))
                ) s
                LEFT JOIN vehiculo ve ON ve.dominio = s.Dominio AND ve._deleted = 0
                OUTER APPLY (
                    SELECT
                        MIN(CASE WHEN vk.km_inicio > 0 THEN vk.km_inicio END) AS KmInicial,
                        MAX(CASE WHEN vk.km_fin    > 0 THEN vk.km_fin    END) AS KmFinal
                    FROM vehiculo_km vk
                    WHERE vk.dominio = s.Dominio AND vk._deleted = 0
                      AND vk.ano_y_mes IN ({mesesIn})
                ) od
                ORDER BY s.KmServicio DESC
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = new List<KmUnidadRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var kmIni = reader.IsDBNull(7) ? 0 : Convert.ToInt64(reader.GetValue(7));
                var kmFin = reader.IsDBNull(8) ? 0 : Convert.ToInt64(reader.GetValue(8));
                var kmServicio = reader.IsDBNull(5) ? 0 : Convert.ToInt64(reader.GetValue(5));
                var recorridoBruto = kmFin - kmIni;
                // Odómetro confiable solo si está cerrado (km_fin > km_inicio) Y el recorrido es
                // físicamente coherente: no se puede recorrer MENOS que el km de servicio hecho.
                // Cuando recorrido < servicio, el odómetro está mal cargado (km_fin apenas por
                // encima del inicial) → daba % vacío absurdo (-355800%). Mejora sobre el FoxPro,
                // que calculaba el % igual sin este chequeo. Esas unidades salen como "—".
                var tieneOdometro = kmFin > kmIni && kmIni > 0 && recorridoBruto >= kmServicio;
                var recorrido = tieneOdometro ? recorridoBruto : 0;
                var kmVacio = tieneOdometro ? recorrido - kmServicio : 0;

                result.Add(new KmUnidadRow(
                    Dominio: reader.GetString(0).Trim(),
                    Interno: reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1)),
                    TipoVeh: reader.GetString(2).Trim(),
                    Consumo: reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3)),
                    Servicios: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    KmServicio: kmServicio,
                    KmRecorrido: recorrido,
                    KmVacio: kmVacio,
                    DiasTrabajados: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    TieneOdometro: tieneOdometro));
            }
            return result;
        }) ?? new();
    }

    /// <summary>Detalle uno-por-uno para el drill-down/Excel del informe de km. Dominio para
    /// filtrar en memoria; el resto reusa ReservaFsDetalleRow (Zoom del Viaje).</summary>
    public async Task<List<KmUnidadDetalleRow>> GetKmUnidadesDetalleAsync(
        DateOnly desde, DateOnly hasta, bool incluirInterno, bool incluirContratados)
    {
        var key = $"kmudet|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{incluirInterno}|{incluirContratados}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var sql = $"""
                SELECT
                    v.id_viaje                                            AS IdViaje,
                    v.f_reserva                                           AS Fecha,
                    RTRIM(ISNULL(v.id_vehicu2, ''))                       AS Dominio,
                    COALESCE(CONVERT(varchar(5), v.hs_inicio, 108), '')   AS Hora,
                    COALESCE(s.nombre, v.id_servici, '')                  AS Servicio,
                    COALESCE(NULLIF(LTRIM(RTRIM(v.nombre_cli)), ''), v.id_cliente, '') AS Cliente,
                    LTRIM(RTRIM(COALESCE(v.d_destino, ''))) +
                        CASE WHEN LTRIM(RTRIM(COALESCE(v.h_destino, ''))) <> ''
                             THEN ' a ' + LTRIM(RTRIM(v.h_destino)) ELSE '' END AS Recorrido,
                    COALESCE(v.pax, 0)                                    AS Pax,
                    COALESCE(v.estado_via, '')                            AS Estado,
                    COALESCE(v.nombre_cho, '')                            AS Chofer,
                    v.interno                                             AS Interno,
                    COALESCE(v.origen, '')                                AS Origen,
                    COALESCE(v.grupo, '')                                 AS Grupo,
                    CAST(ISNULL(v.km, 0) AS bigint)                       AS Km
                FROM viaje v
                LEFT JOIN servicio s ON v.id_servici = s.id_servici
                WHERE {WhereKmUnidades(desde, hasta, incluirInterno, incluirContratados)}
                ORDER BY v.f_reserva, v.hs_inicio, v.id_viaje
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = new List<KmUnidadDetalleRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new KmUnidadDetalleRow(
                    Dominio: reader.GetString(2).Trim(),
                    Km: reader.IsDBNull(13) ? 0 : Convert.ToInt32(reader.GetValue(13)),
                    Reserva: new ReservaFsDetalleRow(
                        IdViaje: reader.GetInt32(0),
                        Fecha: DateOnly.FromDateTime(reader.GetDateTime(1)),
                        Hora: reader.GetString(3),
                        CodServicio: "",
                        Servicio: reader.GetString(4).Trim(),
                        Cliente: reader.GetString(5).Trim(),
                        Recorrido: reader.GetString(6).Trim(),
                        Pax: reader.GetInt32(7),
                        Estado: reader.GetString(8).Trim(),
                        Chofer: reader.GetString(9).Trim(),
                        Interno: reader.IsDBNull(10) ? null : Convert.ToInt32(reader.GetValue(10)),
                        Origen: reader.GetString(11).Trim(),
                        Grupo: reader.GetString(12).Trim())));
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
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
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
    //  Mapa de columnas truncadas → docs/PlanoFoxPro/vehiculos-choferes/CHOFER_ABM.md
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lista de choferes (réplica de la grilla de chofer.scx). "Egresado" =
    /// f_delete con valor. Sin paginar (la grilla FoxPro muestra todo con scroll).
    /// </summary>
    public async Task<List<ChoferListaRow>> GetChoferesAsync()
    {
        return await _cache.GetOrCreateAsync("choferes-lista", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
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
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
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
    //  VEHÍCULOS Y CHOFERES — Odómetros (Control de Odometros: vehiculo_km.scx)
    //  Registro de lecturas de km por dominio/mes. Solo lectura (strangler).
    //  Tabla vehiculo_km (~10.500 filas). La grilla del FoxPro muestra:
    //  Dominio · Fecha (f_carga) · Año y Mes · Km Inicio · Km Fin ·
    //  Km Recorridos (= km_fin - km_inicio) · Interno · U.Creó · U.Modificó.
    //  Filtro: por dominio (o todos) + rango de f_carga; orden dominio, f_carga DESC.
    //  OJO: km_fin/km_inicio pueden venir NULL (mes en curso sin cierre) → Km Recorridos
    //  solo se calcula cuando ambos existen; si no, queda NULL (se muestra "—").
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Lecturas de odómetro (vehiculo_km). Réplica de vehiculo_km.scx → bFiltro.
    /// <paramref name="dominio"/> vacío = todos los vehículos (option "todos los Vehiculos");
    /// con dominio = filtra ese vehículo. Rango sobre <c>f_carga</c>.</summary>
    public async Task<List<OdometroRow>> GetOdometrosAsync(string? dominio, DateOnly desde, DateOnly hasta)
    {
        var dom = (dominio ?? "").Trim();
        var key = $"odometros|{dom}|{desde:yyyyMMdd}|{hasta:yyyyMMdd}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var where = $"_deleted = 0 AND f_carga BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'";
            if (!string.IsNullOrWhiteSpace(dom))
                where += $" AND dominio = '{dom.Replace("'", "''")}'";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    RTRIM(ISNULL(dominio,   '')) AS Dominio,
                    f_carga                      AS FCarga,
                    RTRIM(ISNULL(ano_y_mes, '')) AS AnoMes,
                    km_inicio                    AS KmInicio,
                    km_fin                       AS KmFin,
                    ISNULL(interno, 0)           AS Interno,
                    RTRIM(ISNULL(u_create,  '')) AS UCreo,
                    RTRIM(ISNULL(u_modify,  '')) AS UModifico
                FROM vehiculo_km
                WHERE {where}
                ORDER BY dominio, f_carga DESC
                """;
            var result = new List<OdometroRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                long? kmIni = rd.IsDBNull(3) ? null : rd.GetInt64(3);
                long? kmFin = rd.IsDBNull(4) ? null : rd.GetInt64(4);
                result.Add(new OdometroRow(
                    rd.GetString(0),
                    rd.IsDBNull(1) ? null : DateOnly.FromDateTime(rd.GetDateTime(1)),
                    rd.GetString(2), kmIni, kmFin,
                    (int)rd.GetInt64(5), rd.GetString(6), rd.GetString(7)));
            }
            return result;
        }) ?? new();
    }

    /// <summary>Dominios de la flota propia activa, para el buscador de Odómetros
    /// (réplica del autocompletar de vehiculo_km.scx: PROPIO + activo).</summary>
    public async Task<List<string>> GetDominiosFlotaPropiaAsync()
    {
        return await _cache.GetOrCreateAsync("dominios-flota-propia", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT RTRIM(ISNULL(dominio, '')) AS Dominio
                FROM vehiculo
                WHERE _deleted = 0 AND ISNULL(activo, 0) = 1 AND uso = 'PROPIO'
                      AND dominio IS NOT NULL AND RTRIM(dominio) <> ''
                ORDER BY Dominio
                """;
            var result = new List<string>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(rd.GetString(0));
            return result;
        }) ?? new();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  VEHÍCULOS Y CHOFERES — Siniestros (siniestro.scx / siniestro_abm.scx)
    //  Parte de accidente completo (~70 campos). Solo lectura (strangler).
    //  Tabla siniestro (313 filas). La lista une con chofer por id_chofer para el
    //  nombre del conductor. Grilla: Siniestro (id) · Conductor · Dominio (id_vehicul =
    //  el vehículo NORTUR) · Interno · Fecha · Lugar · Marca (marca_y_mo = del TERCERO) ·
    //  Tipo Acc. Ficha = 5 solapas del ABM.
    //
    //  🐛 TRAMPAS de la réplica (verificadas 04/07/2026):
    //   - id_vehicul = dominio del vehículo NORTUR (asegurado). dominio = dominio del
    //     TERCERO. marca_y_mo = marca/modelo del TERCERO. No confundir.
    //   - Nombres largos del form truncados a 10 chars en SQL: asegurado_dano→asegurado_,
    //     conductor_direccion→conductor_, conductor_localidad→conductor2, conductor_telefono→
    //     conductor3, conductor_celular→conductor4, conductor_dano→conductor5,
    //     propietario→propietari, propietario_direccion→propietar2, ..._localidad→propietar3,
    //     ..._telefono→propietar4, ..._celular→propietar5, propietario_dano→propietar6,
    //     descripcion_acc→descripcio, test_N_nomb→test_N_nom, test_N_tdoc→test_N_tdo, etc.
    //   - Esta tabla NO tiene f_delete: la baja lógica es solo _deleted (no hay "egresados").
    //   - id_chofer siempre matchea con chofer → INNER JOIN seguro.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Grilla de Siniestros (siniestro.scx → arma_grid). INNER JOIN a chofer
    /// por id_chofer para el nombre del conductor. Orden en memoria en la página.</summary>
    public async Task<List<SiniestroRow>> GetSiniestrosAsync()
    {
        return await _cache.GetOrCreateAsync("siniestros-lista", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    s.id                             AS Id,
                    RTRIM(ISNULL(c.nombre,     '')) AS Conductor,
                    RTRIM(ISNULL(s.id_vehicul, '')) AS Dominio,
                    ISNULL(s.interno, 0)            AS Interno,
                    s.fecha                          AS Fecha,
                    RTRIM(ISNULL(s.lugar,      '')) AS Lugar,
                    RTRIM(ISNULL(s.marca_y_mo, '')) AS Marca,
                    RTRIM(ISNULL(s.tipo_acc,   '')) AS TipoAcc,
                    RTRIM(ISNULL(s.localidad,  '')) AS Localidad,
                    ISNULL(s.id_viaje, 0)           AS IdViaje
                FROM siniestro s
                INNER JOIN chofer c ON c.id_chofer = s.id_chofer
                WHERE s._deleted = 0
                ORDER BY s.id
                """;
            var result = new List<SiniestroRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                result.Add(new SiniestroRow(
                    rd.GetInt32(0), rd.GetString(1), rd.GetString(2),
                    (int)rd.GetInt64(3),
                    rd.IsDBNull(4) ? null : DateOnly.FromDateTime(rd.GetDateTime(4)),
                    rd.GetString(5), rd.GetString(6), rd.GetString(7), rd.GetString(8),
                    rd.GetInt64(9)));
            }
            return result;
        }) ?? new();
    }

    /// <summary>Ficha completa de un siniestro (réplica de siniestro_abm.scx en modo
    /// consulta), con las 5 solapas: El Hecho / Vehículo asegurado, Conductor+Vehículo
    /// del tercero, Propietario del tercero, Daños+Descripción, Testigos.</summary>
    public async Task<SiniestroDetalleDto?> GetSiniestroDetalleAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT TOP 1
                s.id, RTRIM(ISNULL(s.id_chofer,'')), RTRIM(ISNULL(c.nombre,'')),
                RTRIM(ISNULL(s.id_vehicul,'')), ISNULL(s.interno,0), ISNULL(s.id_viaje,0),
                RTRIM(ISNULL(s.tipo_acc,'')), s.fecha, s.hora,
                RTRIM(ISNULL(s.lugar,'')), RTRIM(ISNULL(s.localidad,'')),
                RTRIM(ISNULL(s.provincia,'')), RTRIM(ISNULL(s.comisaria,'')),
                -- Vehículo asegurado (condiciones)
                ISNULL(s.velocidad,0), ISNULL(s.visible,0), ISNULL(s.bocina,0),
                ISNULL(s.lluvia,0), ISNULL(s.luces,0), ISNULL(s.mano_unica,0),
                RTRIM(ISNULL(s.asegurado_,'')),
                -- Conductor del tercero
                RTRIM(ISNULL(s.conductor,'')), ISNULL(s.edad,0),
                RTRIM(ISNULL(s.registro_n,'')), s.registro_v,
                RTRIM(ISNULL(s.tdoc,'')), RTRIM(ISNULL(s.ndoc,'')),
                RTRIM(ISNULL(s.conductor_,'')), RTRIM(ISNULL(s.conductor2,'')),
                RTRIM(ISNULL(s.conductor3,'')), RTRIM(ISNULL(s.conductor4,'')),
                -- Vehículo del tercero
                RTRIM(ISNULL(s.dominio,'')), RTRIM(ISNULL(s.marca_y_mo,'')),
                RTRIM(ISNULL(s.tipo,'')), ISNULL(s.ano,0),
                ISNULL(s.seguro,0), RTRIM(ISNULL(s.seguro_nom,'')),
                RTRIM(ISNULL(s.seguro_pol,'')), RTRIM(ISNULL(s.conductor5,'')),
                ISNULL(s.circula,0),
                -- Propietario del tercero
                RTRIM(ISNULL(s.propietari,'')), RTRIM(ISNULL(s.propietar2,'')),
                RTRIM(ISNULL(s.propietar3,'')), RTRIM(ISNULL(s.propietar4,'')),
                RTRIM(ISNULL(s.propietar5,'')), RTRIM(ISNULL(s.propietar6,'')),
                -- Descripción + daños
                RTRIM(ISNULL(s.descripcio,'')),
                ISNULL(s.aseg_delan,0), ISNULL(s.aseg_later,0), ISNULL(s.aseg_trase,0),
                ISNULL(s.otro_delan,0), ISNULL(s.otro_later,0), ISNULL(s.otro_trase,0),
                -- Testigos 1-3
                RTRIM(ISNULL(s.test_1_nom,'')), RTRIM(ISNULL(s.test_1_tdo,'')),
                RTRIM(ISNULL(s.test_1_ndo,'')), RTRIM(ISNULL(s.test_1_tel,'')),
                RTRIM(ISNULL(s.test_1_cel,'')),
                RTRIM(ISNULL(s.test_2_nom,'')), RTRIM(ISNULL(s.test_2_tdo,'')),
                RTRIM(ISNULL(s.test_2_ndo,'')), RTRIM(ISNULL(s.test_2_tel,'')),
                RTRIM(ISNULL(s.test_2_cel,'')),
                RTRIM(ISNULL(s.test_3_nom,'')), RTRIM(ISNULL(s.test_3_tdo,'')),
                RTRIM(ISNULL(s.test_3_ndo,'')), RTRIM(ISNULL(s.test_3_tel,'')),
                RTRIM(ISNULL(s.test_3_cel,'')),
                -- Auditoría
                RTRIM(ISNULL(s.usuario_cr,'')), RTRIM(ISNULL(s.usuario_mo,'')),
                s.f_ingreso, s.f_envio
            FROM siniestro s
            INNER JOIN chofer c ON c.id_chofer = s.id_chofer
            WHERE s.id = {id} AND s._deleted = 0
            """;
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;

        int i = 0;
        var d = new SiniestroDetalleDto
        {
            Id = rd.GetInt32(i++), IdChofer = rd.GetString(i++), Conductor = rd.GetString(i++),
            Dominio = rd.GetString(i++), Interno = (int)rd.GetInt64(i++), IdViaje = rd.GetInt64(i++),
            TipoAcc = rd.GetString(i++), Fecha = D(rd, i++), Hora = Hora(rd, i++),
            Lugar = rd.GetString(i++), Localidad = rd.GetString(i++),
            Provincia = rd.GetString(i++), Comisaria = rd.GetString(i++),
            Velocidad = (int)rd.GetInt32(i++), CondVisible = rd.GetBoolean(i++), CondBocina = rd.GetBoolean(i++),
            CondLluvia = rd.GetBoolean(i++), CondLuces = rd.GetBoolean(i++), CondManoUnica = rd.GetBoolean(i++),
            AseguradoDano = rd.GetString(i++),
            TerConductor = rd.GetString(i++), TerEdad = rd.GetInt32(i++),
            TerRegistroNro = rd.GetString(i++), TerRegistroVto = D(rd, i++),
            TerTdoc = rd.GetString(i++), TerNdoc = rd.GetString(i++),
            TerDireccion = rd.GetString(i++), TerLocalidad = rd.GetString(i++),
            TerTelefono = rd.GetString(i++), TerCelular = rd.GetString(i++),
            TerDominio = rd.GetString(i++), TerMarcaModelo = rd.GetString(i++),
            TerTipo = rd.GetString(i++), TerAno = rd.GetInt32(i++),
            TerSeguro = rd.GetBoolean(i++), TerSeguroNombre = rd.GetString(i++),
            TerSeguroPoliza = rd.GetString(i++), TerConductorDano = rd.GetString(i++),
            TerCircula = rd.GetBoolean(i++),
            PropNombre = rd.GetString(i++), PropDireccion = rd.GetString(i++),
            PropLocalidad = rd.GetString(i++), PropTelefono = rd.GetString(i++),
            PropCelular = rd.GetString(i++), PropDano = rd.GetString(i++),
            Descripcion = rd.GetString(i++),
            AsegDelante = rd.GetBoolean(i++), AsegLateral = rd.GetBoolean(i++), AsegTrasera = rd.GetBoolean(i++),
            OtroDelante = rd.GetBoolean(i++), OtroLateral = rd.GetBoolean(i++), OtroTrasera = rd.GetBoolean(i++),
        };
        d.Testigos.Add(new SiniestroTestigoRow(1, rd.GetString(i++), rd.GetString(i++), rd.GetString(i++), rd.GetString(i++), rd.GetString(i++)));
        d.Testigos.Add(new SiniestroTestigoRow(2, rd.GetString(i++), rd.GetString(i++), rd.GetString(i++), rd.GetString(i++), rd.GetString(i++)));
        d.Testigos.Add(new SiniestroTestigoRow(3, rd.GetString(i++), rd.GetString(i++), rd.GetString(i++), rd.GetString(i++), rd.GetString(i++)));
        d.UsuarioCreo = rd.GetString(i++); d.UsuarioModifico = rd.GetString(i++);
        d.FIngreso = D(rd, i++); d.FEnvio = D(rd, i++);
        return d;

        static DateOnly? D(System.Data.Common.DbDataReader rd, int i) =>
            rd.IsDBNull(i) ? null : DateOnly.FromDateTime(rd.GetDateTime(i));
        static TimeOnly? Hora(System.Data.Common.DbDataReader rd, int i) =>
            rd.IsDBNull(i) ? null : TimeOnly.FromDateTime(rd.GetDateTime(i));
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
            where = $"l.tipo = '{t}' AND l.fecha BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'";
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
            ? $"v.f_grupo_fi BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'"
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
            ? $"v.f_grupo_fi BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'"
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
    //  relevamiento en docs/PlanoFoxPro/facturacion/FACTURACION_LIQUIDACION.md §3.2.
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
            ? $"v.f_grupo_fi BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'"
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
                  AND l.fecha BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'
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
                  AND l.fecha BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'
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

    // ─────────────────────────────────────────────────────────────────────────────
    //  Historial del viaje — réplica del form FoxPro `trafico_historial.scx`
    //  ("Historia del viaje" del menú contextual de Tráfico). Solo lectura.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Carga el "Historial sobre la reserva": la cabecera de auditoría (quién creó/eliminó/
    /// modificó el viaje, con fechas) + la bitácora completa de movimientos de `viaje_log`.
    /// Espeja el Init() de `trafico_historial.scx`: la cabecera sale de `viaje` (campos
    /// u_create/f_create/u_delete/f_delete/u_modify/f_modify) y la grilla de `viaje_log`
    /// WHERE id_viaje = X, ordenada por hora.
    ///
    /// PERFORMANCE: la query de cabecera filtra también por f_reserva (la fila de la planilla
    /// siempre la conoce) → SEEK por ix_viaje_f_reserva en vez de un SCAN completo de `viaje`
    /// (misma trampa que el Zoom; ver GetDetalleViajeAsync). La grilla de `viaje_log` SÍ tiene
    /// índice propio por id_viaje (IX_viaje_log_idviaje), así que ahí el filtro por id_viaje
    /// ya es un seek rápido pese a las 4,4M filas de la tabla.
    /// </summary>
    public async Task<HistorialViajeDto> GetHistorialViajeAsync(int idViaje, DateOnly? fReserva = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var dto = new HistorialViajeDto { IdViaje = idViaje };

        // ── 1) Cabecera de auditoría (tabla viaje) ──
        var fResFiltro = fReserva is null
            ? ""
            : $"AND f_reserva = '{fReserva.Value:yyyyMMdd}'";
        using (var cmdCab = conn.CreateCommand())
        {
            cmdCab.CommandText = $"""
                SELECT TOP 1
                    u_create AS UCreate, f_create AS FCreate,
                    u_delete AS UDelete, f_delete AS FDelete,
                    u_modify AS UModify, f_modify AS FModify
                FROM viaje
                WHERE _deleted = 0 {fResFiltro} AND id_viaje = {idViaje}
                """;
            using var rdCab = await cmdCab.ExecuteReaderAsync();
            if (await rdCab.ReadAsync())
            {
                string S(string c) { var i = rdCab.GetOrdinal(c); return rdCab.IsDBNull(i) ? "" : rdCab.GetValue(i).ToString()!.Trim(); }
                DateOnly? DO(string c) { var i = rdCab.GetOrdinal(c); return rdCab.IsDBNull(i) ? null : DateOnly.FromDateTime(rdCab.GetDateTime(i)); }
                dto.UsuarioCreo = S("UCreate");
                dto.FechaCreo = DO("FCreate");
                dto.UsuarioElimino = S("UDelete");
                dto.FechaElimino = DO("FDelete");
                dto.UsuarioModifico = S("UModify");
                dto.FechaModifico = DO("FModify");
            }
        }

        // ── 2) Bitácora de movimientos (tabla viaje_log) ──
        // Nombres truncados por la réplica DBF→SQL (10 chars): interno_ori→interno_or,
        // interno_new→interno_ne, cronograma_new→cronogram2.
        using (var cmdLog = conn.CreateCommand())
        {
            cmdLog.CommandText = $"""
                SELECT
                    hora       AS Hora,
                    usuario    AS Usuario,
                    motivo     AS Motivo,
                    id_chofer  AS Chofer,
                    cronograma AS Cronograma,
                    cronogram2 AS CronogramaNuevo,
                    interno_or AS InternoOrig,
                    interno_ne AS InternoNuevo,
                    comentario AS Comentario
                FROM viaje_log
                WHERE id_viaje = {idViaje}
                ORDER BY hora, _sync_id
                """;
            using var rdLog = await cmdLog.ExecuteReaderAsync();
            string S(string c) { var i = rdLog.GetOrdinal(c); return rdLog.IsDBNull(i) ? "" : rdLog.GetValue(i).ToString()!.Trim(); }
            int? N(string c) { var i = rdLog.GetOrdinal(c); return rdLog.IsDBNull(i) ? null : Convert.ToInt32(rdLog.GetValue(i)); }
            DateTime? DT(string c) { var i = rdLog.GetOrdinal(c); return rdLog.IsDBNull(i) ? null : rdLog.GetDateTime(i); }
            while (await rdLog.ReadAsync())
            {
                dto.Movimientos.Add(new HistorialViajeRow(
                    DT("Hora"), S("Usuario"), S("Motivo"), S("Chofer"),
                    S("Cronograma"), S("CronogramaNuevo"),
                    N("InternoOrig"), N("InternoNuevo"), S("Comentario")));
            }
        }

        return dto;
    }

    // ───────────────────────────────────────────────────────────────────────────
    // "Novedad sobre el viaje" (menú contextual de Tráfico) — SOLO LECTURA
    // ───────────────────────────────────────────────────────────────────────────
    // El FoxPro (libro_novedad_alta → libro_novedad_abm "alta") DA DE ALTA una novedad
    // en `libro_novedad` ligada al viaje (con opción de mandar correo al cliente). La regla
    // strangler del proyecto deja la ESCRITURA en FoxPro: acá listamos las novedades ya
    // cargadas de ese viaje (libro_novedad WHERE id_viaje = X), igual patrón que el historial.
    //
    // Trampas de la réplica (verificado contra sys.columns, 30/06/2026):
    //   · usuario_create → usuario_cr (truncado a 10 chars); también usuario_de / usuario_mo.
    //   · id_viaje es bigint en libro_novedad (en `viaje` es int) → CAST sobre el parámetro.
    //   · 48.160 filas totales, 19.877 con id_viaje > 0; _deleted = 0 (no hay borradas hoy,
    //     pero filtramos igual por convención del proyecto).
    public async Task<List<NovedadViajeRow>> GetNovedadesViajeAsync(int idViaje)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                id         AS Id,
                f_carga    AS FCarga,
                asunto     AS Asunto,
                mensaje    AS Mensaje,
                usuario_cr AS UsuarioCarga,
                finalizo   AS Finalizo
            FROM libro_novedad
            WHERE _deleted = 0 AND id_viaje = {idViaje}
            ORDER BY f_carga DESC, id DESC
            """;

        var lista = new List<NovedadViajeRow>();
        using var rd = await cmd.ExecuteReaderAsync();
        string S(string c) { var i = rd.GetOrdinal(c); return rd.IsDBNull(i) ? "" : rd.GetValue(i).ToString()!.Trim(); }
        DateTime? DT(string c) { var i = rd.GetOrdinal(c); return rd.IsDBNull(i) ? null : rd.GetDateTime(i); }
        bool B(string c) { var i = rd.GetOrdinal(c); return !rd.IsDBNull(i) && Convert.ToBoolean(rd.GetValue(i)); }
        while (await rd.ReadAsync())
        {
            lista.Add(new NovedadViajeRow(
                Convert.ToInt32(rd["Id"]), DT("FCarga"), S("Asunto"),
                S("Mensaje"), S("UsuarioCarga"), B("Finalizo")));
        }
        return lista;
    }

    // ───────────────────────────────────────────────────────────────────────────
    // "Lista de pasajeros" (menú contextual de Tráfico) — SOLO LECTURA
    // ───────────────────────────────────────────────────────────────────────────
    // El FoxPro (trafico_pasajero_planilla.scx) es un ABM completo de la planilla CNRT
    // (manifiesto de pasajeros con DNI/nacionalidad/profesión + datos de empresa/choferes/
    // vehículos) que se imprime en PDF. Acá lo migramos en SOLO LECTURA: si ese viaje tiene
    // una planilla generada (viaje_pasajero) la mostramos con su detalle (viaje_pasajero_detalle);
    // si no, el diálogo avisa "sin lista generada".
    //
    // Realidad de la réplica (30/06/2026): viaje_pasajero tiene 1 fila y el detalle 0 →
    // casi siempre devolverá null. Se migra por completitud del menú, no por volumen.
    // Trampas de columnas truncadas: empresa_nom→empresa_no, razon_social→razon_soci,
    // nacionalidad→nacionalid. id_viaje es bigint.
    public async Task<PasajerosViajeDto?> GetPasajerosViajeAsync(int idViaje)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        PasajerosViajeDto? dto = null;

        // ── 1) Cabecera de la planilla (viaje_pasajero) ──
        using (var cmdCab = conn.CreateCommand())
        {
            cmdCab.CommandText = $"""
                SELECT TOP 1
                    razon_soci AS RazonSocial, id_cliente AS IdCliente, domicilio AS Domicilio,
                    cuit AS Cuit, legajo AS Legajo, d_destino AS Desde, h_destino AS Hasta,
                    clase AS Clase, f_inicio AS FInicio, f_fin AS FFin, hora AS Hora, km AS Km,
                    empresa_no AS EmpresaNom, empresa_di AS EmpresaDir, empresa_cu AS EmpresaCuit,
                    apellido_1 AS Apellido1, nombre_1 AS Nombre1, tdoc_1 AS Tdoc1, ndoc_1 AS Ndoc1,
                    apellido_2 AS Apellido2, nombre_2 AS Nombre2, tdoc_2 AS Tdoc2, ndoc_2 AS Ndoc2,
                    id_vehicul AS Vehiculo1, id_vehicu2 AS Vehiculo2, id_vehicu3 AS Vehiculo3
                FROM viaje_pasajero
                WHERE _deleted = 0 AND id_viaje = {idViaje}
                """;
            using var rd = await cmdCab.ExecuteReaderAsync();
            if (await rd.ReadAsync())
            {
                string S(string c) { var i = rd.GetOrdinal(c); return rd.IsDBNull(i) ? "" : rd.GetValue(i).ToString()!.Trim(); }
                DateOnly? DO(string c) { var i = rd.GetOrdinal(c); return rd.IsDBNull(i) ? null : DateOnly.FromDateTime(rd.GetDateTime(i)); }
                long? L(string c) { var i = rd.GetOrdinal(c); return rd.IsDBNull(i) ? null : Convert.ToInt64(rd.GetValue(i)); }

                string ChoferTxt(string ape, string nom) =>
                    (S(ape) + " " + S(nom)).Trim();

                dto = new PasajerosViajeDto
                {
                    IdViaje = idViaje,
                    RazonSocial = S("RazonSocial"),
                    IdCliente = S("IdCliente"),
                    Domicilio = S("Domicilio"),
                    Cuit = S("Cuit"),
                    Legajo = S("Legajo"),
                    Desde = S("Desde"),
                    Hasta = S("Hasta"),
                    Clase = S("Clase"),
                    FInicio = DO("FInicio"),
                    FFin = DO("FFin"),
                    Hora = S("Hora"),
                    Km = L("Km"),
                    EmpresaNom = S("EmpresaNom"),
                    EmpresaDir = S("EmpresaDir"),
                    EmpresaCuit = S("EmpresaCuit"),
                    Chofer1 = ChoferTxt("Apellido1", "Nombre1"),
                    Doc1 = (S("Tdoc1") + " " + S("Ndoc1")).Trim(),
                    Chofer2 = ChoferTxt("Apellido2", "Nombre2"),
                    Doc2 = (S("Tdoc2") + " " + S("Ndoc2")).Trim(),
                };
            }
        }

        // Sin cabecera → no hay planilla generada para este viaje.
        if (dto is null) return null;

        // ── 2) Detalle de pasajeros (viaje_pasajero_detalle) ──
        using (var cmdDet = conn.CreateCommand())
        {
            cmdDet.CommandText = $"""
                SELECT
                    id AS Id, apeynom AS ApeYNom, tdoc AS Tdoc, ndoc AS Ndoc,
                    nacionalid AS Nacionalidad, profesion AS Profesion, sexo AS Sexo, f_nac AS FNac
                FROM viaje_pasajero_detalle
                WHERE _deleted = 0 AND id_viaje = {idViaje}
                ORDER BY id
                """;
            using var rd = await cmdDet.ExecuteReaderAsync();
            string S(string c) { var i = rd.GetOrdinal(c); return rd.IsDBNull(i) ? "" : rd.GetValue(i).ToString()!.Trim(); }
            DateOnly? DO(string c) { var i = rd.GetOrdinal(c); return rd.IsDBNull(i) ? null : DateOnly.FromDateTime(rd.GetDateTime(i)); }
            while (await rd.ReadAsync())
            {
                dto.Pasajeros.Add(new PasajeroRow(
                    S("ApeYNom"), S("Tdoc"), S("Ndoc"),
                    S("Nacionalidad"), S("Profesion"), S("Sexo"), DO("FNac")));
            }
        }

        return dto;
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

// ── ABM de Usuarios y Permisos ──────────────────────────────────────────────
/// <summary>Fila de la grilla de usuarios (usuario.scx). FInhabilitacion con valor = amarillo.</summary>
public record UsuarioListaRow(
    int Id, string Usuario, string Nivel, string Acceso, bool Operador, DateOnly? FInhabilitacion);

/// <summary>Ficha/edición de un usuario. El string `acceso` se decodifica en la UI a checkboxes.</summary>
public class UsuarioDetalleDto
{
    public int Id;
    public string Usuario = "", Password = "", Nivel = "", Acceso = "";
    public bool Operador;
    public DateOnly? FCreate, FModify, FDelete;
}

/// <summary>Resumen de la última sesión de un usuario (para la grilla del ABM):
/// último ingreso/egreso, si está conectado ahora, y la IP/host del último login.</summary>
public record UltimaSesionRow(
    int IdUsuario, DateTime? Inicio, DateTime? Fin, bool Activa, string Ip, string Hostname);

/// <summary>Una fila del historial de sesiones (para la ficha del ABM): un ingreso con su
/// egreso y detalle de origen. Espeja la tabla de sesiones del FoxPro.</summary>
public record SesionRow(
    DateTime? Inicio, DateTime? Fin, bool Activa,
    string Ip, string Hostname, int Terminal, string MotivoFin);

/// <summary>Una fila de la bitácora de accesos (tabla usuarios_logs): un evento
/// (LOGIN/LOGOUT/EXPIRADA/VENCIDA/LOGIN_FALLIDO) con su session_id, usuario y origen.</summary>
public record AccesoLogRow(
    int Id, Guid? SessionId, string Evento, DateTime FEvento,
    int? IdUsuario, string Usuario, string Nivel, string Acceso,
    string Ip, string Hostname, string Motivo);

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

// ── Odómetros (vehiculo_km) ─────────────────────────────────────────────────

/// <summary>Una lectura de odómetro de la grilla de Control de Odometros (vehiculo_km.scx).
/// KmInicio/KmFin pueden ser NULL (mes en curso sin cierre); KmRecorridos solo existe cuando
/// ambos están presentes.</summary>
public record OdometroRow(
    string Dominio, DateOnly? FCarga, string AnoMes,
    long? KmInicio, long? KmFin, int Interno, string UCreo, string UModifico)
{
    /// <summary>Km recorridos = km_fin − km_inicio (como la columna calculada del FoxPro).
    /// NULL si falta alguno de los dos, o si diera negativo (odómetro incoherente).</summary>
    public long? KmRecorridos =>
        (KmInicio is long ini && KmFin is long fin && fin >= ini) ? fin - ini : null;

    /// <summary>Código de interno con el formato de la Planilla de Tráfico (viaje.id_interno):
    /// "NT" + interno a 4 dígitos (interno 1 → NT0001). "—" si no hay interno.</summary>
    public string InternoNT => Interno == 0 ? "—" : "NT" + Interno.ToString("D4");
}

// ── Siniestros (siniestro) ──────────────────────────────────────────────────

/// <summary>Una fila de la grilla de Siniestros (siniestro.scx). Dominio = vehículo NORTUR
/// (id_vehicul); Marca = del tercero (marca_y_mo).</summary>
public record SiniestroRow(
    int Id, string Conductor, string Dominio, int Interno, DateOnly? Fecha,
    string Lugar, string Marca, string TipoAcc, string Localidad, long IdViaje);

public record SiniestroTestigoRow(int Orden, string Nombre, string Tdoc, string Ndoc, string Telefono, string Celular)
{
    public bool TieneDatos =>
        !string.IsNullOrWhiteSpace(Nombre) || !string.IsNullOrWhiteSpace(Ndoc)
        || !string.IsNullOrWhiteSpace(Telefono) || !string.IsNullOrWhiteSpace(Celular);
}

/// <summary>Ficha completa de un siniestro (siniestro_abm.scx, 5 solapas). "Aseg" = vehículo
/// NORTUR asegurado; "Ter" = tercero; "Prop" = propietario del tercero.</summary>
public class SiniestroDetalleDto
{
    // Solapa 1 — El hecho + vehículo asegurado
    public int Id, Interno, Velocidad;
    public long IdViaje;
    public string IdChofer = "", Conductor = "", Dominio = "", TipoAcc = "";
    public DateOnly? Fecha;
    public TimeOnly? Hora;
    public string Lugar = "", Localidad = "", Provincia = "", Comisaria = "";
    public bool CondVisible, CondBocina, CondLluvia, CondLuces, CondManoUnica;
    public string AseguradoDano = "";
    // Solapa 2 — Conductor + vehículo del tercero
    public string TerConductor = "", TerRegistroNro = "", TerTdoc = "", TerNdoc = "";
    public int TerEdad, TerAno;
    public DateOnly? TerRegistroVto;
    public string TerDireccion = "", TerLocalidad = "", TerTelefono = "", TerCelular = "";
    public string TerDominio = "", TerMarcaModelo = "", TerTipo = "";
    public bool TerSeguro, TerCircula;
    public string TerSeguroNombre = "", TerSeguroPoliza = "", TerConductorDano = "";
    // Solapa 3 — Propietario del tercero
    public string PropNombre = "", PropDireccion = "", PropLocalidad = "", PropTelefono = "", PropCelular = "", PropDano = "";
    // Solapa 4 — Descripción + daños
    public string Descripcion = "";
    public bool AsegDelante, AsegLateral, AsegTrasera, OtroDelante, OtroLateral, OtroTrasera;
    // Solapa 5 — Testigos
    public List<SiniestroTestigoRow> Testigos = new();
    // Auditoría
    public string UsuarioCreo = "", UsuarioModifico = "";
    public DateOnly? FIngreso, FEnvio;

    public string HoraTexto => Hora is TimeOnly h ? h.ToString("HH:mm") : "—";
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

public record BandaHorariaRow(DateOnly Fecha, string TipoVehiculo, string Banda, int Reservas, int Pax);

// Una fila de detalle del informe de banda horaria (drill-down). Lleva la banda y el tipo de
// vehículo para poder filtrar en memoria; el resto reusa el DTO del informe de fecha/servicio
// para reusar ReservasFsDetalleDialog + Zoom del Viaje.
public record BandaHorariaDetalleRow(string Banda, string TipoVehiculo, ReservaFsDetalleRow Reserva);

/// <summary>Un motivo de cancelación del catálogo viaje_motivo_cancela.</summary>
public record MotivoCancelaDto(int Id, string Motivo);

/// <summary>
/// Una fila agregada del informe "Reservas por cliente": mes × cliente × tipo de unidad
/// (PROPIO / CONTRATADO / SIN REALIZAR), con viajes y pax para el toggle de métrica.
/// </summary>
public record ReservaClienteRow(string Mes, string IdCliente, string Cliente, string Tipo, int Viajes, int Pax);

// Una fila de detalle del informe por cliente (drill-down + Excel). Mes/IdCliente/Tipo para
// filtrar en memoria; Motivo para la hoja Viajes en modo canceladas; el resto reusa el DTO
// del informe de fecha/servicio para reusar ReservasFsDetalleDialog + Zoom del Viaje.
public record ReservaClienteDetalleRow(string Mes, string IdCliente, string Tipo, string Motivo, ReservaFsDetalleRow Reserva);

/// <summary>
/// Una fila agregada del informe "Viajes por Choferes": chofer × día, con viajes, turismo
/// (origen='T'), cabecera (origen='P'), km, primer/último horario del día y pax. La fila
/// "franco" (día sin viajes entre el primero y último trabajado del chofer) se calcula en
/// memoria en la página, como el form FoxPro.
/// </summary>
public record ViajesChoferRow(
    string IdChofer, string Chofer, string Localidad, string Tipo, DateOnly Fecha,
    int Viajes, int Turismo, int Cabecera, int Km, int Pax, string HoraInicio, string HoraFin);

/// <summary>Detalle uno-por-uno del informe de choferes. IdChofer/Fecha para filtrar en
/// memoria; el resto reusa ReservaFsDetalleRow (ReservasFsDetalleDialog + Zoom del Viaje).</summary>
public record ViajesChoferDetalleRow(string IdChofer, DateOnly Fecha, ReservaFsDetalleRow Reserva);

/// <summary>
/// Una fila del informe "Km Unidades vs Servicios": por vehículo (dominio), km de servicio
/// (SUM viaje.km), km recorrido real (odómetro), km vacío (recorrido − servicio), días
/// trabajados y consumo. TieneOdometro = false cuando el odómetro del período está abierto
/// (km_fin sin cargar) → recorrido/vacío/% quedan sin dato confiable.
/// </summary>
public record KmUnidadRow(
    string Dominio, int Interno, string TipoVeh, int Consumo, int Servicios,
    long KmServicio, long KmRecorrido, long KmVacio, int DiasTrabajados, bool TieneOdometro);

/// <summary>Detalle uno-por-uno del informe de km. Dominio/Km para filtrar en memoria;
/// el resto reusa ReservaFsDetalleRow (ReservasFsDetalleDialog + Zoom del Viaje).</summary>
public record KmUnidadDetalleRow(string Dominio, int Km, ReservaFsDetalleRow Reserva);

public record ReservaFechaServicioRow(
    DateOnly Fecha,
    string CodServicio,
    string Servicio,
    int Reservas,
    int Canceladas,
    int Pax);

/// <summary>Una reserva individual del informe "Reservas por fecha y servicio" (drill-down + Excel).</summary>
public record ReservaFsDetalleRow(
    int IdViaje,
    DateOnly Fecha,
    string Hora,
    string CodServicio,
    string Servicio,
    string Cliente,
    string Recorrido,
    int Pax,
    string Estado,
    string Chofer,
    int? Interno,
    string Origen,
    string Grupo);

// ── DTOs del módulo Reservas: Plantillas ────────────────────────────────────────
// (Reservas Especiales reusa ReservaFsDetalleRow de arriba — misma forma de viaje.)

/// <summary>Resumen de una plantilla en el Mantenimiento (una fila por id_reserva).</summary>
public record PlantillaResumenRow(
    string IdReserva, int Filas, string HoraDesde, string HoraHasta, int PaxTotal);

/// <summary>Una fila de una plantilla (reserva_plantilla) — grilla del Mantenimiento y del Armado.</summary>
public record PlantillaFilaRow(
    int Id, string IdReserva, string HoraIni, string HoraFin, string IdServicio, string Servicio,
    string TipoVeh, string Desde, string Hasta, int Pax, int Km, int Hs, string Cabecera,
    string Guia, string GuiaDueno, string EmpresaDestino, string Recorrido, string Provincia,
    string Comentario, string Cronograma, string Adicionales);

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
    // Largo del tramo ORIGEN dentro de Recorrido ("DESDE a HASTA"). Permite a la UI
    // partir el recorrido en el ' a ' correcto para pintarlo como "DESDE ➜ HASTA":
    // los datos reales tienen ' a ' adentro de los tramos ("VER A CIRUJANO",
    // "...A LAS 10 HS"), así que parsear el string en la UI sería ambiguo. 0 = sin
    // tramo HASTA (Recorrido se muestra tal cual, sin flecha).
    int RecorridoDesdeLen,
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
    string Estado,
    // ── Datos para "Ubicar en GPS" (réplica de ubicar_gps de trafico2.scx) ──
    // Interno numérico del vehículo (viaje.interno). El FoxPro arma el código de bus GPS
    // con los últimos 2 dígitos del interno; si es 0 prueba con Val(cronograma)/Val(cronograma2).
    int? Interno,
    // hs_inicio / hs_fin crudos: la URL "Recorrido en WEB" (Maps.aspx) los pasa como
    // desde/hasta en formato YYYYMMDDHHMMSS (Ttoc(...,1) en FoxPro). hs_fin vacío → ahora.
    DateTime? HsInicio,
    DateTime? HsFin,
    // ── Claves para "Ver Datos Extras" (réplica del submenú de trafico2.scx) ──
    // Códigos completos que esperan los diálogos de ficha (ChoferDetalleDialog,
    // VehiculoDetalleDialog). El cliente usa el campo Cliente (= id_cliente). Estos NO se
    // muestran en la grilla: la columna Veh está truncada a 4 chars y Chofer es el nombre.
    // OJO: la PK del vehículo es viaje.id_vehicu2 (= dominio/patente completa, ej "AE512LG"),
    // NO viaje.id_vehicul (ese guarda solo el TIPO: BUS/VAN). El dominio matchea con
    // vehiculo.id_vehicul (= vehiculo.dominio), que es lo que busca GetVehiculoDetalleAsync.
    string IdChofer,
    string IdVehiculo,
    // ── Claves para el submenú "Ver Datos Extras" (réplica de verdatosex de trafico2.scx) ──
    // id_operado → código del operador del cliente (PK lógica de cliente_operador) para
    // "Ver Datos Operador". gps_cod → código de cabecera de recorrido (cabecera.codigo) para
    // "Ver Recorrido". Adjunto → ruta del archivo adjunto (viaje.file, ej O:\...\x.pdf) para
    // "Ver Adjunto". "Ver Adicionales" no necesita campo nuevo: filtra viaje_adicional por IdViaje.
    string IdOperador,
    string GpsCod,
    string Adjunto)
{
    /// <summary>
    /// Estado tal como lo PINTA la grilla, no el que guarda la base. Réplica exacta de
    /// arma_grid (trafico2.scx, líneas 356-368): CURSO y CHEQUEO NO existen como dato en
    /// viaje.estado_via — el FoxPro los deriva en memoria al armar el grid con un Replace
    /// sobre el cursor local. Reglas fieles:
    ///   • ASIGNADO   + hs_inicio &lt;= ahora  → CURSO   (el servicio ya arrancó)
    ///   • SIN ASIGNAR + chequeo &gt; 0        → CHEQUEO (unidad chequeada, aún sin asignar)
    /// El resto de estados se muestra tal cual. Usar SOLO para display (columna Estado y
    /// color de fila de la grilla de servicios). Los filtros server-side, el Excel y el
    /// Zoom siguen usando <see cref="Estado"/> crudo, que es lo que dice la tabla.
    /// </summary>
    public string EstadoDisplay =>
        Estado == "ASIGNADO"   && HsInicio is { } hi && hi <= DateTime.Now ? "CURSO"
      : Estado == "SIN ASIGNAR" && (Chq ?? 0) > 0                          ? "CHEQUEO"
      : Estado;
}

/// <summary>
/// Tipo de filtro server-side de la planilla de Tráfico (rama "Aplicar Filtros" del menú
/// contextual de trafico2.scx). Cada valor corresponde a un Case de arma_grid_viaje.
/// </summary>
public enum TraficoFiltroTipo
{
    Fecha,          // FECHA          — rango de fechas (fundacional)
    TipoReserva,    // TIPO_RESERVA   — origen 'T'/'P'
    Fletero,        // (agregado en el exe productivo) — viaje.fletero
    Choferes,       // CHOFERES       — id_chofer
    Interno,        // INTERNO        — interno
    Estado,         // TIPO_ESTADO    — estado_via
    Vuelo,          // VUELO          — vuelo
    Cliente,        // CLIENTE        — id_cliente [+ grupo]
    ClienteGrupo,   // CLIENTE_GRUPO  — cliente + varios grupos
    Reserva,        // RESERVA        — id_viaje (sin rango de fechas)
    ReservaRuta     // RESERVA_RUTA   — id_viaje_int (sin rango de fechas)
}

/// <summary>
/// Descriptor de un filtro server-side de Tráfico. Desde/Hasta acotan el rango (réplica de
/// xFecha1/xFecha2 de arma_grid_viaje). Texto/Numero portan el criterio según el tipo
/// (p. ej. Numero = Nº de reserva o interno; Texto = vuelo, cliente, estado…).
/// </summary>
public record TraficoFiltro(
    TraficoFiltroTipo Tipo,
    DateOnly Desde,
    DateOnly Hasta,
    string? Texto = null,
    int? Numero = null);

/// <summary>
/// Opción de conductor para el combo del filtro "Conductores" de Tráfico (réplica del cursor
/// cursorTraficoChofer de trafico_filtro_chofer.scx). Codigo = chofer.id_chofer (lo que guarda
/// viaje.id_chofer); Nombre = chofer.nombre desnormalizado (apellido + nombres).
/// </summary>
public record ChoferOpcion(string Codigo, string Nombre);

/// <summary>
/// Opción de unidad de la flota para el combo del filtro "Nº de Interno" de Tráfico.
/// Codigo = vehiculo.cronograma = el CÓDIGO de unidad (NT0044, AG0001…) que se ve en la grilla y
/// guarda viaje.id_interno (es por lo que se filtra). Interno = el número suelto vehiculo.interno
/// (se repite entre unidades, solo informativo). Dominio = patente, para referencia.
/// </summary>
public record InternoOpcion(string Codigo, long Interno, string Dominio);

/// <summary>
/// Listas para los combos de unidades de la pantalla de Tráfico (trafico2.scx):
/// Programadas = "interno por empresas" (filtra U/Pr), Asignadas = "todos los internos" (filtra U/Cb).
/// Cada unidad asignada lleva su Empresa (fletero.id_contrat) para la cascada empresa → internos:
/// al elegir una empresa en el combo 1, el combo 2 se achica a sus internos (en memoria).
/// </summary>
public record CombosUnidadesTrafico(List<string> Programadas, List<UnidadAsignadaCombo> Asignadas);

/// <summary>Ítem del combo U/Asignada: el interno (vehiculo.cronograma) + su empresa.</summary>
public record UnidadAsignadaCombo(string Interno, string Empresa);

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
    // Largo del tramo origen dentro de Recorrido — ver PlanillaTraficoRow.RecorridoDesdeLen.
    int RecorridoDesdeLen,
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
// Codigo = id_adicion (truncado de id_adicional en la réplica) — columna "Codigo" de la
// grilla FoxPro de adicionales (trafico_zoom_adicional). El Zoom del Viaje no lo usa; el
// diálogo "Ver Adicionales" del menú de Tráfico sí. Importe se calcula en la vista (Cantidad×Precio).
public record AdicionalViajeRow(string Nombre, int Cantidad, decimal Precio, string Codigo = "");

/// <summary>
/// Ficha del operador de un viaje — "Ver Datos Operador" (cliente_operador en modo consulta).
/// Campos del form FoxPro cliente_operador_abm: código, nombre, cliente, teléfono, celular,
/// nextel, interno, email, comentario. Todo solo lectura.
/// </summary>
public record OperadorDetalleDto(
    string IdOperador, string IdCliente, string Nombre, string Telefono,
    string Celular, string Nextel, string Interno, string Email, string Comentario);

/// <summary>
/// Recorrido de cabecera de un viaje — "Ver Recorrido" (cabecera en modo consulta). El form
/// FoxPro cabecera_recorrido_abm_zoom solo mostraba cabecera.recorrido; acá se agregan también
/// código y los 3 nombres de la cabecera para dar contexto. Todo solo lectura.
/// </summary>
public record RecorridoCabeceraDto(
    string Codigo, string Nombre, string Nombre1, string Nombre2, string Recorrido);

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
    public int? Chequeo { get; set; }                 // viaje.chequeo (para derivar CHEQUEO en display)
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

    /// <summary>Estado tal como lo pinta la grilla de Tráfico (CURSO/CHEQUEO derivados en
    /// memoria como el FoxPro), para que el Zoom coincida con la fila que se clickeó. Misma
    /// regla que PlanillaTraficoRow.EstadoDisplay: ASIGNADO+hs_inicio&lt;=ahora→CURSO;
    /// SIN ASIGNAR+chequeo&gt;0→CHEQUEO. No toca la base (v.estado_via sigue crudo).</summary>
    public string EstadoDisplay =>
        Estado == "ASIGNADO"   && HsInicio is { } hi && hi <= DateTime.Now ? "CURSO"
      : Estado == "SIN ASIGNAR" && (Chequeo ?? 0) > 0                       ? "CHEQUEO"
      : Estado;
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

// ── Tráfico: Historial del viaje (trafico_historial.scx) ──

/// <summary>
/// Una línea de la bitácora de un viaje (tabla `viaje_log`) — una columna por cada celda
/// de la grilla del form FoxPro `trafico_historial.scx`. <c>Hora</c> trae fecha + hora
/// (el form la rotula "Hora" pero el dato es datetime). InternoOrig/InternoNuevo son los
/// números de interno antes/después del movimiento; CronogramaNuevo la unidad nueva.
/// </summary>
public record HistorialViajeRow(
    DateTime? Hora, string Usuario, string Motivo, string Chofer,
    string Cronograma, string CronogramaNuevo,
    int? InternoOrig, int? InternoNuevo, string Comentario);

/// <summary>
/// Historial completo de una reserva para el modal "Historial del viaje" (solo lectura).
/// Espeja `trafico_historial.scx`: la cabecera de auditoría (Creó/Eliminó/Modificó, sale de
/// `viaje`) + la lista de movimientos (<see cref="HistorialViajeRow"/>, sale de `viaje_log`).
/// </summary>
public class HistorialViajeDto
{
    public int IdViaje { get; set; }

    // Cabecera (recuadro gris del form): 3 pares usuario + fecha.
    public string UsuarioCreo { get; set; } = "";
    public DateOnly? FechaCreo { get; set; }
    public string UsuarioElimino { get; set; } = "";
    public DateOnly? FechaElimino { get; set; }
    public string UsuarioModifico { get; set; } = "";
    public DateOnly? FechaModifico { get; set; }

    public List<HistorialViajeRow> Movimientos { get; } = new();
}

/// <summary>
/// Una novedad del libro ligada a un viaje, para el modal "Novedad sobre el viaje" (solo
/// lectura). Espeja una fila de `libro_novedad` con id_viaje = X. El FoxPro daba de alta
/// (libro_novedad_abm "alta"); acá solo se listan las ya cargadas.
/// </summary>
public record NovedadViajeRow(
    int Id, DateTime? FCarga, string Asunto, string Mensaje, string UsuarioCarga, bool Finalizo);

/// <summary>Un pasajero de la planilla CNRT (`viaje_pasajero_detalle`).</summary>
public record PasajeroRow(
    string ApeYNom, string Tdoc, string Ndoc,
    string Nacionalidad, string Profesion, string Sexo, DateOnly? FNac);

/// <summary>
/// Planilla de pasajeros (manifiesto CNRT) de un viaje, para el modal "Lista de pasajeros"
/// (solo lectura). Espeja `viaje_pasajero` (cabecera: cliente/empresa/destino/choferes/
/// vehículos) + `viaje_pasajero_detalle` (los pasajeros). El FoxPro
/// (trafico_pasajero_planilla.scx) la cargaba y la imprimía en PDF; acá solo se muestra.
/// </summary>
public class PasajerosViajeDto
{
    public int IdViaje { get; set; }

    // Datos del servicio / cliente
    public string RazonSocial { get; set; } = "";
    public string IdCliente { get; set; } = "";
    public string Domicilio { get; set; } = "";
    public string Cuit { get; set; } = "";
    public string Legajo { get; set; } = "";
    public string Desde { get; set; } = "";
    public string Hasta { get; set; } = "";
    public string Clase { get; set; } = "";
    public DateOnly? FInicio { get; set; }
    public DateOnly? FFin { get; set; }
    public string Hora { get; set; } = "";
    public long? Km { get; set; }

    // Empresa transportista (datos para el manifiesto CNRT)
    public string EmpresaNom { get; set; } = "";
    public string EmpresaDir { get; set; } = "";
    public string EmpresaCuit { get; set; } = "";

    // Choferes asignados (el FoxPro tiene hasta 3; mostramos los 2 primeros con dato)
    public string Chofer1 { get; set; } = "";
    public string Doc1 { get; set; } = "";
    public string Chofer2 { get; set; } = "";
    public string Doc2 { get; set; } = "";

    public List<PasajeroRow> Pasajeros { get; } = new();
}

// ════════════════════════════════════════════════════════════════════════════════
//  VEHÍCULOS Y CHOFERES — Fleteros, Tipo de Vehículos, Agenda de Vencimientos
//  (métodos de lectura — la escritura de los dos ABMs vive en AbmService)
//  Extendemos ReportService con estos tres bloques en un partial para no engordar el
//  archivo principal; comparten _dbFactory / _cache por ser la misma clase.
// ════════════════════════════════════════════════════════════════════════════════
public partial class ReportService
{
    // ── FLETEROS (fletero.scx / fletero_abm.scx) ────────────────────────────────
    //  Transportistas contratados. PK física id (int, NO identity), PK lógica id_contrat
    //  (nvarchar 15). f_delete cargado = egresado (amarillo). Cat. compartido con Facturación.

    /// <summary>
    /// Lista de fleteros para la GRILLA del ABM (réplica de fletero.scx): ORDER BY orden, nombre.
    /// Egresado = f_delete con valor (se muestra en amarillo, no se oculta). Distinto de
    /// <see cref="GetFleterosAsync"/> (que devuelve solo códigos para el combo de Tráfico).
    /// </summary>
    public async Task<List<FleteroRow>> GetFleterosListaAsync()
    {
        return await _cache.GetOrCreateAsync("fleteros-lista", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    id                              AS Id,
                    RTRIM(ISNULL(id_contrat, '')) AS IdContrat,
                    RTRIM(ISNULL(razon_soci, '')) AS RazonSocial,
                    RTRIM(ISNULL(nombre,     '')) AS Nombre,
                    ISNULL(orden, 0)                AS Orden,
                    RTRIM(ISNULL(cuit,       '')) AS Cuit,
                    RTRIM(ISNULL(localidad,  '')) AS Localidad,
                    RTRIM(ISNULL(telefono,   '')) AS Telefono,
                    RTRIM(ISNULL(email,      '')) AS Email,
                    f_delete                        AS FDelete
                FROM fletero
                WHERE _deleted = 0
                ORDER BY ISNULL(orden, 0), nombre
                """;
            var result = new List<FleteroRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new FleteroRow(
                    rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.GetInt64(4), rd.GetString(5), rd.GetString(6), rd.GetString(7), rd.GetString(8),
                    rd.IsDBNull(9) ? null : DateOnly.FromDateTime(rd.GetDateTime(9))));
            return result;
        }) ?? new();
    }

    /// <summary>Ficha completa de un fletero (fletero_abm.scx) para la vista/edición.</summary>
    public async Task<FleteroDetalleDto?> GetFleteroDetalleAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                id, RTRIM(ISNULL(id_contrat,'')), RTRIM(ISNULL(razon_soci,'')),
                RTRIM(ISNULL(nombre,'')), ISNULL(orden,0),
                RTRIM(ISNULL(cuit,'')), RTRIM(ISNULL(tipo_resp,'')),
                RTRIM(ISNULL(domicilio,'')), RTRIM(ISNULL(localidad,'')), RTRIM(ISNULL(postal,'')),
                RTRIM(ISNULL(provincia,'')), RTRIM(ISNULL(telefono,'')), RTRIM(ISNULL(celular,'')),
                RTRIM(ISNULL(email,'')), RTRIM(ISNULL(contacto,'')),
                RTRIM(ISNULL(id_lista_p,'')), RTRIM(ISNULL(id_lista_2,'')),
                RTRIM(ISNULL(modo_liq,'')), RTRIM(ISNULL(fc_prefere,'')), ISNULL(diagrama,0),
                f_create, f_modify, f_delete
            FROM fletero
            WHERE id = @id AND _deleted = 0
            """;
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new FleteroDetalleDto
        {
            Id = rd.GetInt32(0),
            IdContrat = rd.GetString(1),
            RazonSocial = rd.GetString(2),
            Nombre = rd.GetString(3),
            Orden = rd.GetInt64(4),
            Cuit = rd.GetString(5),
            TipoResp = rd.GetString(6),
            Domicilio = rd.GetString(7),
            Localidad = rd.GetString(8),
            Postal = rd.GetString(9),
            Provincia = rd.GetString(10),
            Telefono = rd.GetString(11),
            Celular = rd.GetString(12),
            Email = rd.GetString(13),
            Contacto = rd.GetString(14),
            IdListaP = rd.GetString(15),
            IdLista2 = rd.GetString(16),
            ModoLiq = rd.GetString(17),
            FcPrefere = rd.GetString(18),
            Diagrama = rd.GetBoolean(19),
            FCreate = rd.IsDBNull(20) ? null : DateOnly.FromDateTime(rd.GetDateTime(20)),
            FModify = rd.IsDBNull(21) ? null : DateOnly.FromDateTime(rd.GetDateTime(21)),
            FDelete = rd.IsDBNull(22) ? null : DateOnly.FromDateTime(rd.GetDateTime(22)),
        };
    }

    // ── TIPO DE VEHÍCULOS (vehiculo_tipo.scx / vehiculo_tipo_abm.scx) ────────────
    //  Catálogo de categorías de la flota (6 filas). PK física id (int, NO identity),
    //  PK lógica id_vehicul (nvarchar 15). OJO: la PK se llama id_vehicul igual que en
    //  `vehiculo`, pero es la PK del TIPO (BUS/VAN/MINI/…), no del vehículo.

    /// <summary>Lista de tipos de vehículo (réplica de vehiculo_tipo.scx). ORDER BY id_vehicul.</summary>
    public async Task<List<TipoVehiculoRow>> GetTiposVehiculoAsync()
    {
        return await _cache.GetOrCreateAsync("tipos-vehiculo-lista", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    id                              AS Id,
                    RTRIM(ISNULL(id_vehicul, '')) AS Codigo,
                    RTRIM(ISNULL(nombre,     '')) AS Nombre,
                    ISNULL(pax, 0)                  AS Pax,
                    RTRIM(ISNULL(id_vehicu2, '')) AS Subtipo,
                    consumo_mi                      AS ConsumoMin,
                    consumo_ma                      AS ConsumoMax,
                    ISNULL(vende, 0)                AS Vende,
                    RTRIM(ISNULL(dir_dibujo, '')) AS DirDibujo,
                    f_delete                        AS FDelete
                FROM vehiculo_tipo
                WHERE _deleted = 0
                ORDER BY id_vehicul
                """;
            var result = new List<TipoVehiculoRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new TipoVehiculoRow(
                    rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3), rd.GetString(4),
                    rd.IsDBNull(5) ? null : rd.GetDecimal(5),
                    rd.IsDBNull(6) ? null : rd.GetDecimal(6),
                    rd.GetBoolean(7), rd.GetString(8),
                    rd.IsDBNull(9) ? null : DateOnly.FromDateTime(rd.GetDateTime(9))));
            return result;
        }) ?? new();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MÓDULO RESERVAS — CATÁLOGOS: Operadores · Grupos · Destinos (06/07/2026)
    //  Vistas de solo lectura (lista + editor) con andamiaje de ABM. La escritura vive en
    //  AbmService y está apagada por AbmFeatureFlags hasta el día D (regla strangler).
    //  Planos: docs/PlanoFoxPro/catalogos/CLIENTE_OPERADOR_ABM.md · CLIENTE_GRUPO_ABM.md ·
    //  DESTINO_ABM.md. Columnas verificadas contra sys.columns (06/07/2026).
    // ═══════════════════════════════════════════════════════════════════════════

    // ── OPERADORES (cliente_operador.scx / cliente_operador_abm.scx) ────────────
    //  Contacto (operadora de la agencia) dentro de un cliente. id_operado = PK lógica GLOBAL
    //  (no por cliente). Baja FÍSICA (sin f_delete). El FoxPro hace INNER JOIN a cliente → un
    //  operador con cliente inexistente desaparece de la lista; acá usamos LEFT JOIN para no
    //  esconder datos (mostramos "—" en razón social) — mejora sobre el FoxPro.

    /// <summary>Lista de operadores (réplica de cliente_operador.scx). ORDER BY nombre.</summary>
    public async Task<List<OperadorRow>> GetOperadoresListaAsync()
    {
        return await _cache.GetOrCreateAsync("operadores-lista", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    o.id                              AS Id,
                    RTRIM(ISNULL(o.id_operado, '')) AS IdOperador,
                    RTRIM(ISNULL(o.id_cliente, '')) AS IdCliente,
                    RTRIM(ISNULL(c.razon_soci, '')) AS RazonSocial,
                    RTRIM(ISNULL(o.nombre,     '')) AS Nombre,
                    RTRIM(ISNULL(o.telefono,   '')) AS Telefono,
                    RTRIM(ISNULL(o.celular,    '')) AS Celular,
                    RTRIM(ISNULL(o.interno,    '')) AS Interno,
                    RTRIM(ISNULL(o.email,      '')) AS Email,
                    RTRIM(ISNULL(o.comentario, '')) AS Comentario
                FROM cliente_operador o
                LEFT JOIN cliente c ON LTRIM(RTRIM(c.id_cliente)) = LTRIM(RTRIM(o.id_cliente)) AND c._deleted = 0
                WHERE o._deleted = 0
                ORDER BY o.nombre
                """;
            var result = new List<OperadorRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new OperadorRow(
                    rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
                    rd.GetString(8), rd.GetString(9)));
            return result;
        }) ?? new();
    }

    /// <summary>Ficha de un operador para la vista/edición (cliente_operador_abm.scx).</summary>
    public async Task<OperadorRow?> GetOperadorRowAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                o.id, RTRIM(ISNULL(o.id_operado,'')), RTRIM(ISNULL(o.id_cliente,'')),
                RTRIM(ISNULL(c.razon_soci,'')), RTRIM(ISNULL(o.nombre,'')),
                RTRIM(ISNULL(o.telefono,'')), RTRIM(ISNULL(o.celular,'')),
                RTRIM(ISNULL(o.interno,'')), RTRIM(ISNULL(o.email,'')), RTRIM(ISNULL(o.comentario,''))
            FROM cliente_operador o
            LEFT JOIN cliente c ON LTRIM(RTRIM(c.id_cliente)) = LTRIM(RTRIM(o.id_cliente)) AND c._deleted = 0
            WHERE o.id = @id AND o._deleted = 0
            """;
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new OperadorRow(
            rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
            rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
            rd.GetString(8), rd.GetString(9));
    }

    // ── GRUPOS (cliente_grupo.scx / cliente_grupo_abm.scx) ──────────────────────
    //  Agrupa viajes de un cliente para facturarlos juntos. La dupla (id_cliente, nombre) es la
    //  clave lógica. f_grupo_fc con valor = grupo CERRADO (facturado → candado). Columnas
    //  truncadas: f_grupo_fi (fin), f_grupo_in (inicio), f_grupo_fc (facturó), liquidacio.
    //  11.272 filas → la grilla usa <Virtualize>. El FoxPro NO filtra f_delete en el arma_grid
    //  activo; acá filtramos _deleted=0 (convención réplica). Baja = DELETE físico.

    /// <summary>Lista de grupos (réplica de cliente_grupo.scx). Filtro por f_grupo_fi vs hoy:
    /// 0=No finalizados (>=hoy, default), 1=Finalizados (&lt;hoy), 2=Todos. INNER JOIN a cliente.</summary>
    public async Task<List<GrupoRow>> GetGruposListaAsync(int filtro)
    {
        var key = $"grupos-lista|{filtro}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            var where = filtro switch
            {
                1 => "AND g.f_grupo_fi <  CAST(GETDATE() AS date)",   // Finalizados
                2 => "",                                              // Todos
                _ => "AND g.f_grupo_fi >= CAST(GETDATE() AS date)",   // No finalizados (default)
            };
            cmd.CommandText = $"""
                SELECT
                    g.id                              AS Id,
                    RTRIM(ISNULL(g.id_cliente, '')) AS IdCliente,
                    RTRIM(ISNULL(c.razon_soci, '')) AS RazonSocial,
                    RTRIM(ISNULL(g.nombre,     '')) AS Nombre,
                    g.f_grupo_in                      AS FInicio,
                    g.f_grupo_fi                      AS FFin,
                    g.f_grupo_fc                      AS FFacturo
                FROM cliente_grupo g
                INNER JOIN cliente c ON LTRIM(RTRIM(c.id_cliente)) = LTRIM(RTRIM(g.id_cliente)) AND c._deleted = 0
                WHERE g._deleted = 0 {where}
                ORDER BY c.razon_soci, g.nombre
                """;
            var result = new List<GrupoRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new GrupoRow(
                    rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.IsDBNull(4) ? null : DateOnly.FromDateTime(rd.GetDateTime(4)),
                    rd.IsDBNull(5) ? null : DateOnly.FromDateTime(rd.GetDateTime(5)),
                    rd.IsDBNull(6) ? null : DateOnly.FromDateTime(rd.GetDateTime(6))));
            return result;
        }) ?? new();
    }

    /// <summary>Ficha de un grupo para vista/edición (cliente_grupo_abm.scx).</summary>
    public async Task<GrupoRow?> GetGrupoRowAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                g.id, RTRIM(ISNULL(g.id_cliente,'')), RTRIM(ISNULL(c.razon_soci,'')),
                RTRIM(ISNULL(g.nombre,'')), g.f_grupo_in, g.f_grupo_fi, g.f_grupo_fc
            FROM cliente_grupo g
            LEFT JOIN cliente c ON LTRIM(RTRIM(c.id_cliente)) = LTRIM(RTRIM(g.id_cliente)) AND c._deleted = 0
            WHERE g.id = @id AND g._deleted = 0
            """;
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new GrupoRow(
            rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
            rd.IsDBNull(4) ? null : DateOnly.FromDateTime(rd.GetDateTime(4)),
            rd.IsDBNull(5) ? null : DateOnly.FromDateTime(rd.GetDateTime(5)),
            rd.IsDBNull(6) ? null : DateOnly.FromDateTime(rd.GetDateTime(6)));
    }

    /// <summary>Conteo de viajes del grupo agrupados por estado (para el aviso de la baja/modifica
    /// en cascada del editor de Grupos). Réplica del SELECT ... GROUP BY estado_viaje del FoxPro,
    /// buscando por la dupla desnormalizada (id_cliente, grupo) igual que el form.</summary>
    public async Task<Dictionary<string, int>> GetViajesGrupoPorEstadoAsync(string idCliente, string nombreGrupo)
    {
        var res = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT RTRIM(ISNULL(estado_via,'')) AS Estado, COUNT(*) AS Cnt
            FROM viaje
            WHERE _deleted = 0
              AND LTRIM(RTRIM(id_cliente)) = @cli
              AND LTRIM(RTRIM(grupo))      = @grp
            GROUP BY estado_via
            """;
        var pc = cmd.CreateParameter(); pc.ParameterName = "@cli"; pc.Value = (idCliente ?? "").Trim(); cmd.Parameters.Add(pc);
        var pg = cmd.CreateParameter(); pg.ParameterName = "@grp"; pg.Value = (nombreGrupo ?? "").Trim(); cmd.Parameters.Add(pg);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            res[rd.GetString(0)] = rd.GetInt32(1);
        return res;
    }

    // ── DESTINOS (destino.scx / destino_abm.scx) ────────────────────────────────
    //  Catálogo de lugares origen/destino. Alimenta el autocomplete Desde/Hasta de reservas.
    //  destino = nombre (clave lógica, se graba en MAYÚSCULAS). mas100km = recargo por distancia.
    //  Baja FÍSICA (sin f_delete). 398 filas. destino_localidad = catálogo satélite del combo.

    /// <summary>Lista de destinos (réplica de destino.scx). ORDER BY destino.</summary>
    public async Task<List<DestinoRow>> GetDestinosListaAsync()
    {
        return await _cache.GetOrCreateAsync("destinos-lista", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    id                              AS Id,
                    RTRIM(ISNULL(destino,   '')) AS Destino,
                    RTRIM(ISNULL(direccion, '')) AS Direccion,
                    RTRIM(ISNULL(localidad, '')) AS Localidad,
                    RTRIM(ISNULL(telefono,  '')) AS Telefono,
                    RTRIM(ISNULL(correo,    '')) AS Correo,
                    RTRIM(ISNULL(contacto,  '')) AS Contacto,
                    RTRIM(ISNULL(cabecera,  '')) AS Cabecera,
                    ISNULL(mas100km, 0)             AS Mas100Km
                FROM destino
                WHERE _deleted = 0
                ORDER BY destino
                """;
            var result = new List<DestinoRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new DestinoRow(
                    rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
                    rd.GetBoolean(8)));
            return result;
        }) ?? new();
    }

    /// <summary>Ficha de un destino para vista/edición (destino_abm.scx).</summary>
    public async Task<DestinoRow?> GetDestinoRowAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, RTRIM(ISNULL(destino,'')), RTRIM(ISNULL(direccion,'')), RTRIM(ISNULL(localidad,'')),
                   RTRIM(ISNULL(telefono,'')), RTRIM(ISNULL(correo,'')), RTRIM(ISNULL(contacto,'')),
                   RTRIM(ISNULL(cabecera,'')), ISNULL(mas100km,0)
            FROM destino WHERE id = @id AND _deleted = 0
            """;
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new DestinoRow(
            rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
            rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7), rd.GetBoolean(8));
    }

    /// <summary>Localidades del combo del editor de Destinos (tabla satélite destino_localidad).</summary>
    public async Task<List<string>> GetDestinoLocalidadesAsync()
    {
        return await _cache.GetOrCreateAsync("destino-localidades", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT RTRIM(ISNULL(localidad,'')) AS Localidad
                FROM destino_localidad
                WHERE _deleted = 0 AND RTRIM(ISNULL(localidad,'')) <> ''
                ORDER BY Localidad
                """;
            var result = new List<string>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(rd.GetString(0));
            return result;
        }) ?? new();
    }

    // ── AGENDA DE VENCIMIENTOS (agenda_vencimiento.scx) ─────────────────────────
    //  INFORME (no ABM): cruza chofer (registro/CNRT/AEP) + vehiculo propio (VTV/matafuegos)
    //  con días de anticipación. No tiene tabla propia. Reusa la lógica del TableroDto del Home.

    /// <summary>
    /// Choferes con registro / CNRT / AEP vencido o por vencer dentro de <paramref name="dias"/>.
    /// Espeja el 1er cursor de agenda_vencimiento.scx. registro_vto→registro_v,
    /// registro_vto_cnrt→registro_3, registro_vto_aeo→registro_4. NULL = sin fecha (se trata
    /// como vencido, igual que el FoxPro con EMPTY()).
    /// </summary>
    public async Task<List<ChoferVtoRow>> GetChoferesPorVencerAsync(int dias)
    {
        var key = $"agenda-venc|cho|{dias}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // límite = HOY + días. NULL (sin fecha) cuenta como vencido → ISNULL a una fecha mínima.
            cmd.CommandText = $"""
                DECLARE @lim date = DATEADD(day, {dias}, CAST(GETDATE() AS date));
                SELECT
                    RTRIM(ISNULL(id_chofer, '')) AS IdChofer,
                    RTRIM(ISNULL(nombre,    '')) AS Nombre,
                    RTRIM(ISNULL(fletero,   '')) AS Fletero,
                    RTRIM(ISNULL(registro_n,'')) AS RegistroNro,
                    registro_v                     AS RegistroVto,
                    registro_3                     AS CnrtVto,
                    registro_4                     AS AepVto
                FROM chofer
                WHERE _deleted = 0 AND f_delete IS NULL
                  AND (ISNULL(registro_v, '1900-01-01') <= @lim
                    OR ISNULL(registro_3, '1900-01-01') <= @lim
                    OR ISNULL(registro_4, '1900-01-01') <= @lim)
                ORDER BY id_chofer
                """;
            var result = new List<ChoferVtoRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new ChoferVtoRow(
                    rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.IsDBNull(4) ? null : DateOnly.FromDateTime(rd.GetDateTime(4)),
                    rd.IsDBNull(5) ? null : DateOnly.FromDateTime(rd.GetDateTime(5)),
                    rd.IsDBNull(6) ? null : DateOnly.FromDateTime(rd.GetDateTime(6))));
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Vehículos de flota PROPIA con VTV / matafuegos vencido o por vencer. Espeja el 2do cursor
    /// de agenda_vencimiento.scx (uso='PROPIO'). El FoxPro usa DOS umbrales distintos: VTV con
    /// nDiasDifVeh y matafuegos con nDiasDifMat (parametro.aviso_veh / aviso_mat). Un vehículo
    /// entra si CUALQUIERA de los dos vence dentro de su propio umbral.
    /// verificacion_vto (VTV)→verificac2, vencimiento_mat (matafuegos)→vencimient.
    /// </summary>
    public async Task<List<VehiculoVtoRow>> GetVehiculosPorVencerAsync(int diasVtv, int diasMat)
    {
        var key = $"agenda-venc|veh|{diasVtv}|{diasMat}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                DECLARE @limVtv date = DATEADD(day, {diasVtv}, CAST(GETDATE() AS date));
                DECLARE @limMat date = DATEADD(day, {diasMat}, CAST(GETDATE() AS date));
                SELECT
                    ISNULL(interno, 0)              AS Interno,
                    RTRIM(ISNULL(dominio,   '')) AS Dominio,
                    verificac2                     AS VtvVto,
                    vencimient                     AS MatafuegoVto,
                    poliza_vto                     AS PolizaVto,
                    habilitac2                     AS HabilitacionVto
                FROM vehiculo
                WHERE _deleted = 0 AND f_delete IS NULL AND uso = 'PROPIO'
                  AND (ISNULL(verificac2, '1900-01-01') <= @limVtv
                    OR ISNULL(vencimient, '1900-01-01') <= @limMat)
                ORDER BY interno
                """;
            var result = new List<VehiculoVtoRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new VehiculoVtoRow(
                    (int)rd.GetInt64(0), rd.GetString(1),
                    rd.IsDBNull(2) ? null : DateOnly.FromDateTime(rd.GetDateTime(2)),
                    rd.IsDBNull(3) ? null : DateOnly.FromDateTime(rd.GetDateTime(3)),
                    rd.IsDBNull(4) ? null : DateOnly.FromDateTime(rd.GetDateTime(4)),
                    rd.IsDBNull(5) ? null : DateOnly.FromDateTime(rd.GetDateTime(5))));
            return result;
        }) ?? new();
    }

    /// <summary>Umbrales de anticipación de la Agenda de Vencimientos (parametro.aviso_cho/veh/mat).
    /// El FoxPro los usa como default del informe. Defaults de fallback: 30 / 7 / 10.</summary>
    public async Task<(int Chofer, int Vtv, int Matafuego)> GetParametrosAvisoAsync()
    {
        return await _cache.GetOrCreateAsync("agenda-venc|parametros", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // aviso_cho/veh/mat son bigint en la réplica → CAST a int en el SELECT para leer con GetInt32.
            cmd.CommandText = """
                SELECT TOP 1
                    CAST(ISNULL(aviso_cho, 30) AS int),
                    CAST(ISNULL(aviso_veh, 7)  AS int),
                    CAST(ISNULL(aviso_mat, 10) AS int)
                FROM parametro
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            if (await rd.ReadAsync())
                return (rd.GetInt32(0), rd.GetInt32(1), rd.GetInt32(2));
            return (30, 7, 10);
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    //  TRÁFICO — Cabeceras/Recorridos · Francos · Viáticos
    //  Los 3 ítems del menú Tráfico migrados en solo lectura + andamiaje ABM
    //  (05/07/2026). Tablas: cabecera, chofer_franco, chofer_viatico y sus 2
    //  catálogos. ⚠ Estas tablas hacen BAJA FÍSICA en FoxPro (no tienen
    //  f_delete/f_create) — solo _deleted de la réplica. Ver skill modulo-trafico
    //  y docs/PlanoFoxPro/trafico/. ⚠ Las 5 tablas están en el server VIEJO pero
    //  NO en el nuevo (172.25.69.217) → replicar antes del día D.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Lista de Cabeceras/Recorridos (cabecera_recorrido.scx → arma_grid).
    /// codigo = PK lógica; nombre/nombre1/nombre2 = las 3 descripciones; recorrido = texto largo.</summary>
    public async Task<List<CabeceraRow>> GetCabecerasAsync()
    {
        return await _cache.GetOrCreateAsync("cabeceras-lista", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    id                            AS Id,
                    RTRIM(ISNULL(codigo,   '')) AS Codigo,
                    RTRIM(ISNULL(nombre,   '')) AS Nombre,
                    RTRIM(ISNULL(nombre1,  '')) AS Nombre1,
                    RTRIM(ISNULL(nombre2,  '')) AS Nombre2,
                    ISNULL(recorrido, '')        AS Recorrido
                FROM cabecera
                WHERE _deleted = 0
                ORDER BY nombre
                """;
            var result = new List<CabeceraRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new CabeceraRow(
                    rd.GetInt32(0), rd.GetString(1), rd.GetString(2),
                    rd.GetString(3), rd.GetString(4), rd.GetString(5)));
            return result;
        }) ?? new();
    }

    /// <summary>Mantenimiento de Francos (chofer_franco.scx → arma_grid). JOIN a chofer por
    /// id_chofer para el nombre. Filtro por rango de fechas (obligatorio: 71k filas) + código de
    /// motivo opcional. Acota fechas al rango válido (hay fechas corruptas como 9201-03-03).</summary>
    public async Task<List<FrancoRow>> GetFrancosAsync(DateOnly desde, DateOnly hasta, string? codigoMotivo)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var cod = (codigoMotivo ?? "").Trim();
        var key = $"francos|{d:yyyyMMdd}|{h:yyyyMMdd}|{cod}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var where = $"a._deleted = 0 AND a.fecha BETWEEN '{d:yyyyMMdd}' AND '{h:yyyyMMdd}'";
            if (!string.IsNullOrWhiteSpace(cod))
                where += $" AND a.codigo = '{cod.Replace("'", "''")}'";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    a.id                            AS Id,
                    RTRIM(ISNULL(a.id_chofer, '')) AS IdChofer,
                    RTRIM(ISNULL(b.nombre,    '')) AS Nombre,
                    RTRIM(ISNULL(a.codigo,    '')) AS Codigo,
                    RTRIM(ISNULL(a.motivo,    '')) AS Motivo,
                    a.fecha                          AS Fecha,
                    ISNULL(a.trabajo, 0)            AS Trabajo
                FROM chofer_franco a
                INNER JOIN chofer b ON a.id_chofer = b.id_chofer
                WHERE {where}
                ORDER BY b.nombre, a.fecha
                """;
            var result = new List<FrancoRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new FrancoRow(
                    rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.GetString(4), DateOnly.FromDateTime(rd.GetDateTime(5)), rd.GetBoolean(6)));
            return result;
        }) ?? new();
    }

    /// <summary>Motivos de franco distintos presentes en la data (para el combo de filtro).
    /// Devuelve (codigo, "codigo — motivo"). Excluye vacíos.</summary>
    public async Task<List<(string Codigo, string Texto)>> GetFrancoMotivosAsync()
    {
        return await _cache.GetOrCreateAsync("francos-motivos", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT RTRIM(ISNULL(codigo,'')) AS Codigo, MAX(RTRIM(ISNULL(motivo,''))) AS Motivo
                FROM chofer_franco
                WHERE _deleted = 0 AND RTRIM(ISNULL(codigo,'')) <> ''
                GROUP BY RTRIM(ISNULL(codigo,''))
                ORDER BY Codigo
                """;
            var result = new List<(string, string)>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var c = rd.GetString(0);
                var m = rd.GetString(1);
                result.Add((c, string.IsNullOrEmpty(m) ? c : $"{c} — {m}"));
            }
            return result;
        }) ?? new();
    }

    /// <summary>Auditoría de Francos (chofer_franco_auditoria.scx → bValoriza). Réplica del cruce
    /// del FoxPro: por cada chofer, para el MES/AÑO elegido, marca cada día como "trb" (trabajó ese
    /// día — hay un viaje FINALIZADO/FACTURADO PROPIO donde es id_chofer o id_chofer2), el código
    /// del franco cargado (en minúscula), o "DUP" si hay franco Y trabajo el mismo día. Cuenta días
    /// trabajados y "problemas" (DUP). Opción de excluir a los auditores (chofer.auditor=1). Todo
    /// en memoria sobre 3 queries.</summary>
    public async Task<List<FrancoAuditoriaRow>> GetFrancoAuditoriaAsync(int mes, int ano, bool excluyeAuditores)
    {
        var desde = new DateOnly(ano, mes, 1);
        var hasta = new DateOnly(ano, mes, DateTime.DaysInMonth(ano, mes));
        var dias = hasta.Day;
        var key = $"franco-audit|{ano:D4}{mes:D2}|{(excluyeAuditores ? 1 : 0)}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var dStr = desde.ToString("yyyy-MM-dd");
            var hStr = hasta.ToString("yyyy-MM-dd");

            // acumulador chofer → (nombre, matriz de días[1..n])
            var mapa = new Dictionary<string, (string Nombre, string[] Dias)>();
            string[] NuevaFila() { var a = new string[dias + 1]; return a; } // índice 1..dias

            void MarcarTrabajo(string idCho, string nombre, int diaNum)
            {
                if (!mapa.TryGetValue(idCho, out var e))
                {
                    e = (nombre, NuevaFila());
                    mapa[idCho] = e;
                }
                if (diaNum >= 1 && diaNum <= dias) e.Dias[diaNum] = "trb";
            }

            // 1) viajes trabajados (chofer titular) — PROPIO + FINALIZADO/FACTURADO
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT RTRIM(ISNULL(id_chofer,'')) AS IdCho, MAX(RTRIM(ISNULL(nombre_cho,''))) AS Nombre, DAY(f_reserva) AS Dia
                    FROM viaje
                    WHERE _deleted = 0 AND f_reserva BETWEEN '{dStr}' AND '{hStr}'
                      AND (estado_via = 'FINALIZADO' OR estado_via = 'FACTURADO')
                      AND RTRIM(ISNULL(id_chofer,'')) <> '' AND tipo_chofe = 'PROPIO'
                    GROUP BY RTRIM(ISNULL(id_chofer,'')), DAY(f_reserva)
                    """;
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    MarcarTrabajo(rd.GetString(0), rd.GetString(1), rd.GetInt32(2));
            }

            // 2) viajes como 2º chofer (id_chofer2) — el nombre se resuelve luego si hace falta
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT RTRIM(ISNULL(v.id_chofer2,'')) AS IdCho, MAX(RTRIM(ISNULL(c.nombre,''))) AS Nombre, DAY(v.f_reserva) AS Dia
                    FROM viaje v
                    LEFT JOIN chofer c ON v.id_chofer2 = c.id_chofer
                    WHERE v._deleted = 0 AND v.f_reserva BETWEEN '{dStr}' AND '{hStr}'
                      AND (v.estado_via = 'FINALIZADO' OR v.estado_via = 'FACTURADO')
                      AND RTRIM(ISNULL(v.id_chofer2,'')) <> '' AND v.tipo_chofe = 'PROPIO'
                    GROUP BY RTRIM(ISNULL(v.id_chofer2,'')), DAY(v.f_reserva)
                    """;
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    MarcarTrabajo(rd.GetString(0), rd.IsDBNull(1) ? "CHOFER NO ENCONTRADO" : rd.GetString(1), rd.GetInt32(2));
            }

            // 3) francos cargados del mes (excluye código "F" solo, como el FoxPro) → marca/DUP
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT RTRIM(ISNULL(a.id_chofer,'')) AS IdCho, RTRIM(ISNULL(b.nombre,'')) AS Nombre,
                           RTRIM(ISNULL(a.codigo,'')) AS Codigo, DAY(a.fecha) AS Dia
                    FROM chofer_franco a
                    LEFT JOIN chofer b ON a.id_chofer = b.id_chofer
                    WHERE a._deleted = 0 AND a.fecha BETWEEN '{dStr}' AND '{hStr}'
                      AND RTRIM(ISNULL(a.codigo,'')) <> 'F'
                    """;
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var idCho = rd.GetString(0);
                    var nombre = rd.IsDBNull(1) ? "CONDUCTOR NO ENCONTRADO" : rd.GetString(1);
                    var codigo = rd.GetString(2).ToLowerInvariant();
                    var dia = rd.GetInt32(3);
                    if (!mapa.TryGetValue(idCho, out var e))
                    {
                        e = (string.IsNullOrEmpty(nombre) ? "CONDUCTOR NO ENCONTRADO" : nombre, NuevaFila());
                        mapa[idCho] = e;
                    }
                    if (dia >= 1 && dia <= dias)
                        e.Dias[dia] = string.IsNullOrEmpty(e.Dias[dia]) ? codigo : "DUP";
                }
            }

            // 4) opcional: excluir auditores (chofer.auditor = 1)
            var auditores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excluyeAuditores)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT RTRIM(ISNULL(id_chofer,'')) FROM chofer WHERE ISNULL(auditor,0) = 1";
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync()) auditores.Add(rd.GetString(0));
            }

            var result = new List<FrancoAuditoriaRow>();
            foreach (var kv in mapa)
            {
                if (excluyeAuditores && auditores.Contains(kv.Key)) continue;
                var d = kv.Value.Dias;
                int trab = 0, prob = 0;
                for (var i = 1; i <= dias; i++)
                {
                    var c = d[i] ?? "";
                    if (c == "DUP" || c == "PRO") prob++;
                    else if (!string.IsNullOrEmpty(c)) trab++;
                }
                result.Add(new FrancoAuditoriaRow(kv.Key, kv.Value.Nombre, d, trab, prob));
            }
            return result.OrderBy(r => r.Nombre).ToList();
        }) ?? new();
    }

    /// <summary>Viáticos (chofer_viatico.scx → bFiltro). JOINs a chofer_viatico_liquida, chofer y
    /// chofer_viatico_motivo. Filtro por rango de fechas + chofer opcional. Tabla vacía hoy (0 filas).</summary>
    public async Task<List<ViaticoRow>> GetViaticosAsync(DateOnly desde, DateOnly hasta, string? idChofer)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var cho = (idChofer ?? "").Trim();
        var key = $"viaticos|{d:yyyyMMdd}|{h:yyyyMMdd}|{cho}";
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var where = $"v._deleted = 0 AND v.fecha BETWEEN '{d:yyyyMMdd}' AND '{h:yyyyMMdd}'";
            if (!string.IsNullOrWhiteSpace(cho))
                where += $" AND v.id_chofer = '{cho.Replace("'", "''")}'";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    v.id                            AS Id,
                    v.fecha                          AS Fecha,
                    RTRIM(ISNULL(v.id_chofer, '')) AS IdChofer,
                    RTRIM(ISNULL(c.nombre,    '')) AS Conductor,
                    RTRIM(ISNULL(m.nombre,    '')) AS Motivo,
                    RTRIM(ISNULL(l.nombre,    '')) AS FormaLiquida,
                    RTRIM(ISNULL(v.forma_pago,'')) AS FormaPago,
                    ISNULL(v.importe, 0)            AS Importe,
                    v.f_pago                         AS FPago
                FROM chofer_viatico v
                LEFT JOIN chofer c                 ON v.id_chofer  = c.id_chofer
                LEFT JOIN chofer_viatico_motivo m  ON v.id_motivo  = m.id
                LEFT JOIN chofer_viatico_liquida l ON v.id_liquida = l.id
                WHERE {where}
                ORDER BY v.fecha, l.nombre, c.nombre
                """;
            var result = new List<ViaticoRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new ViaticoRow(
                    rd.GetInt32(0), DateOnly.FromDateTime(rd.GetDateTime(1)), rd.GetString(2),
                    rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6),
                    rd.GetDecimal(7),
                    rd.IsDBNull(8) ? null : DateOnly.FromDateTime(rd.GetDateTime(8))));
            return result;
        }) ?? new();
    }

    /// <summary>Catálogo de Motivos de Viático (chofer_viatico_motivo.scx). Vacío hoy.</summary>
    public Task<List<CatalogoSimpleRow>> GetViaticoMotivosAsync() =>
        GetCatalogoSimpleAsync("chofer_viatico_motivo", "viatico-motivos");

    /// <summary>Catálogo de Formas de Liquidación de Viático (chofer_viatico_liquida.scx). Vacío hoy.</summary>
    public Task<List<CatalogoSimpleRow>> GetViaticoLiquidaAsync() =>
        GetCatalogoSimpleAsync("chofer_viatico_liquida", "viatico-liquida");

    /// <summary>Lector genérico de un catálogo (id, nombre) — sirve para motivo y forma de liquidación.
    /// El nombre de tabla NO viene del usuario (es constante del código) → seguro de concatenar.</summary>
    private async Task<List<CatalogoSimpleRow>> GetCatalogoSimpleAsync(string tabla, string cacheKey)
    {
        return await _cache.GetOrCreateAsync($"catalogo|{cacheKey}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT id AS Id, RTRIM(ISNULL(nombre, '')) AS Nombre
                FROM {tabla}
                WHERE _deleted = 0
                ORDER BY nombre
                """;
            var result = new List<CatalogoSimpleRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new CatalogoSimpleRow(rd.GetInt32(0), rd.GetString(1)));
            return result;
        }) ?? new();
    }

    /// <summary>Choferes activos para los combos (id_chofer + nombre). Espeja el cursor de
    /// chofer_viatico_abm / chofer_franco_abm (Empty(f_delete) → acá _deleted = 0 + f_delete IS NULL).</summary>
    public async Task<List<CatalogoSimpleRow>> GetChoferesComboAsync()
    {
        return await _cache.GetOrCreateAsync("choferes-combo", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT RTRIM(ISNULL(id_chofer,'')) AS Id, RTRIM(ISNULL(nombre,'')) AS Nombre
                FROM chofer
                WHERE _deleted = 0 AND f_delete IS NULL AND RTRIM(ISNULL(id_chofer,'')) <> ''
                ORDER BY nombre
                """;
            var result = new List<CatalogoSimpleRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new CatalogoSimpleRow(0, rd.GetString(1)) { IdChofer = rd.GetString(0) });
            return result;
        }) ?? new();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MÓDULO TRÁFICO — Voucher Recepción · Guardia · Contactos · Lista pasajeros
    //  (06/07/2026, solo lectura + andamiaje ABM)
    //  🐛 Truncados verificados en la réplica: viaje.voucher_nr / voucher_re / hs_s_inici;
    //  viaje_guardia.id_vehicul / nombre_cho; estacion.control_sa / cairo_codi (rubro = bigint FK).
    // ═══════════════════════════════════════════════════════════════════════

    // ── Voucher Recepción (trafico_voucher.scx) ──────────────────────────────
    //  Auditoría de vouchers de recepción del pasajero: 3 modos de consulta sobre `viaje`.
    //  Modo 1 = rango de nº de voucher (voucher_nr), 2 = rango de fecha de recepción (voucher_re),
    //  3 = "sin recepcionar" (voucher_re NULL AND voucher_nr > 0). Sin caché (consulta puntual).

    /// <summary>Consulta de auditoría de vouchers (trafico_voucher.scx). <paramref name="modo"/>:
    /// "voucher" | "fecha" | "sin". Acota fechas al rango válido. Filtra _deleted = 0.</summary>
    public async Task<List<VoucherRow>> GetVoucherAuditoriaAsync(
        string modo, long dVoucher, long hVoucher, DateOnly dFecha, DateOnly hFecha, DateOnly hastaFecha)
    {
        string filtro = modo switch
        {
            "voucher" => "v.voucher_nr BETWEEN @dv AND @hv",
            "fecha"   => "v.voucher_re BETWEEN @df AND @hf",
            _         => "v.voucher_re IS NULL AND v.voucher_nr > 0",  // "sin"
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                ISNULL(v.voucher_nr, 0)                          AS VoucherNro,
                v.voucher_re                                     AS VoucherRecep,
                CAST(v.id_viaje AS bigint)                       AS IdViaje,
                v.f_reserva                                      AS FReserva,
                RTRIM(ISNULL(v.hs_s_inici, ''))                AS Hora,
                CAST(ISNULL(v.interno, 0) AS int)               AS Interno,
                LTRIM(RTRIM(ISNULL(v.d_destino,'') + ' a ' + ISNULL(v.h_destino,''))) AS Destino,
                RTRIM(ISNULL(v.id_chofer, ''))                 AS IdChofer,
                LEFT(RTRIM(ISNULL(v.id_vehicul, '')), 4)       AS Vehiculo,
                RTRIM(ISNULL(v.id_cliente, ''))                AS IdCliente,
                RTRIM(ISNULL(v.comentario, ''))                AS Comentario
            FROM viaje v
            WHERE v._deleted = 0
              AND v.f_reserva BETWEEN '{FechaMinValida:yyyyMMdd}' AND '{FechaMaxValida:yyyyMMdd}'
              AND ({filtro})
            ORDER BY v.id_viaje
            """;
        cmd.Parameters.Add(NuevoParam(cmd, "@dv", dVoucher));
        cmd.Parameters.Add(NuevoParam(cmd, "@hv", hVoucher));
        cmd.Parameters.Add(NuevoParam(cmd, "@df", dFecha.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(NuevoParam(cmd, "@hf", hFecha.ToDateTime(TimeOnly.MinValue)));

        var result = new List<VoucherRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            result.Add(new VoucherRow(
                rd.GetInt64(0),
                rd.IsDBNull(1) ? null : DateOnly.FromDateTime(rd.GetDateTime(1)),
                rd.GetInt64(2),
                rd.IsDBNull(3) ? null : DateOnly.FromDateTime(rd.GetDateTime(3)),
                rd.GetString(4), rd.GetInt32(5), rd.GetString(6), rd.GetString(7),
                rd.GetString(8), rd.GetString(9), rd.GetString(10)));
        return result;
    }

    // ── Guardia (trafico_guardia.scx + _abm) ─────────────────────────────────
    //  ABM sobre viaje_guardia (registro de guardias de choferes/unidades). Baja FÍSICA.

    /// <summary>Guardias en un rango de fechas (trafico_guardia.scx). ORDER BY id_chofer.</summary>
    public async Task<List<GuardiaRow>> GetGuardiasAsync(DateOnly desde, DateOnly hasta)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                id                                 AS Id,
                ISNULL(interno, 0)                 AS Interno,
                RTRIM(ISNULL(id_vehicul, ''))    AS IdVehiculo,
                RTRIM(ISNULL(id_chofer, ''))     AS IdChofer,
                RTRIM(ISNULL(nombre_cho, ''))    AS Nombre,
                ISNULL(franco, 0)                  AS Franco,
                fecha                              AS Fecha,
                hs_inicio                          AS HsInicio,
                hs_fin                             AS HsFin,
                fpago                              AS FPago
            FROM viaje_guardia
            WHERE _deleted = 0 AND fecha BETWEEN @d AND @h
            ORDER BY id_chofer, fecha
            """;
        cmd.Parameters.Add(NuevoParam(cmd, "@d", desde.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(NuevoParam(cmd, "@h", hasta.ToDateTime(TimeOnly.MinValue)));
        var result = new List<GuardiaRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            result.Add(new GuardiaRow(
                rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2), rd.GetString(3), rd.GetString(4),
                rd.GetBoolean(5),
                rd.IsDBNull(6) ? null : DateOnly.FromDateTime(rd.GetDateTime(6)),
                rd.IsDBNull(7) ? null : rd.GetDateTime(7),
                rd.IsDBNull(8) ? null : rd.GetDateTime(8),
                rd.IsDBNull(9) ? null : DateOnly.FromDateTime(rd.GetDateTime(9))));
        return result;
    }

    /// <summary>Una guardia por id (para el editor).</summary>
    public async Task<GuardiaRow?> GetGuardiaRowAsync(int id)
    {
        var lista = await GetGuardiasAsync(FechaMinValida, FechaMaxValida);
        return lista.FirstOrDefault(g => g.Id == id);
    }

    // ── Contactos y Proveedores (estacion.scx + _abm) ────────────────────────
    //  ⚠ `estacion` es el catálogo de PROVEEDORES de toda la empresa (COMPARTIDO con Combustible).
    //  rubro es bigint (FK a estacion_rubro.id). Baja FÍSICA.

    /// <summary>Lista de contactos/proveedores (estacion.scx). <paramref name="campo"/> del combo del
    /// FoxPro: "razon" | "direccion" | "localidad" | "telefono". rubroId null/0 = todos los rubros.</summary>
    public async Task<List<ContactoRow>> GetContactosListaAsync(long? rubroId, string campo, string texto)
    {
        return await _cache.GetOrCreateAsync($"contactos|{rubroId}|{campo}|{texto}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var wheres = new List<string> { "e._deleted = 0" };
            if (rubroId is > 0) wheres.Add("e.rubro = @rubro");
            if (!string.IsNullOrWhiteSpace(texto))
            {
                string col = campo switch
                {
                    "direccion" => "e.domicilio",
                    "localidad" => "e.localidad",
                    "telefono"  => "e.telefono",
                    _           => "e.nombre",
                };
                wheres.Add($"{col} LIKE @txt");
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    e.id                               AS Id,
                    e.rubro                            AS RubroId,
                    RTRIM(ISNULL(r.rubro, ''))       AS Rubro,
                    RTRIM(ISNULL(e.nombre, ''))      AS Nombre,
                    RTRIM(ISNULL(e.domicilio, ''))   AS Domicilio,
                    RTRIM(ISNULL(e.localidad, ''))   AS Localidad,
                    RTRIM(ISNULL(e.provincia, ''))   AS Provincia,
                    RTRIM(ISNULL(e.telefono, ''))    AS Telefono,
                    RTRIM(ISNULL(e.celular, ''))     AS Celular,
                    RTRIM(ISNULL(e.radio, ''))       AS Radio,
                    RTRIM(ISNULL(e.contacto1, ''))   AS Contacto1,
                    RTRIM(ISNULL(e.contacto2, ''))   AS Contacto2
                FROM estacion e
                LEFT JOIN estacion_rubro r ON r.id = e.rubro AND r._deleted = 0
                WHERE {string.Join(" AND ", wheres)}
                ORDER BY e.nombre
                """;
            if (rubroId is > 0) cmd.Parameters.Add(NuevoParam(cmd, "@rubro", rubroId.Value));
            if (!string.IsNullOrWhiteSpace(texto)) cmd.Parameters.Add(NuevoParam(cmd, "@txt", "%" + texto.Trim() + "%"));

            var result = new List<ContactoRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new ContactoRow(
                    rd.GetInt32(0), rd.GetInt64(1), rd.GetString(2), rd.GetString(3), rd.GetString(4),
                    rd.GetString(5), rd.GetString(6), rd.GetString(7), rd.GetString(8), rd.GetString(9),
                    rd.GetString(10), rd.GetString(11)));
            return result;
        }) ?? new();
    }

    /// <summary>Ficha completa de un contacto (estacion_abm.scx) para la vista/edición.</summary>
    public async Task<ContactoDetalleDto?> GetContactoRowAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                id, ISNULL(rubro, 0), RTRIM(ISNULL(nombre,'')), RTRIM(ISNULL(domicilio,'')),
                RTRIM(ISNULL(localidad,'')), RTRIM(ISNULL(provincia,'')), RTRIM(ISNULL(telefono,'')),
                RTRIM(ISNULL(celular,'')), RTRIM(ISNULL(radio,'')), RTRIM(ISNULL(email,'')),
                RTRIM(ISNULL(contacto1,'')), RTRIM(ISNULL(contacto2,'')), RTRIM(ISNULL(medio_pago,'')),
                ISNULL(control_sa,0), ISNULL(ult_lote,0), RTRIM(ISNULL(cairo_codi,'')),
                RTRIM(ISNULL(cairo_iibb,'')), ISNULL(ypf_ruta,0), ISNULL(esso_card,0), ISNULL(cta_cte,0)
            FROM estacion
            WHERE id = @id AND _deleted = 0
            """;
        cmd.Parameters.Add(NuevoParam(cmd, "@id", id));
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new ContactoDetalleDto
        {
            Id = rd.GetInt32(0),
            RubroId = rd.GetInt64(1),
            Nombre = rd.GetString(2),
            Domicilio = rd.GetString(3),
            Localidad = rd.GetString(4),
            Provincia = rd.GetString(5),
            Telefono = rd.GetString(6),
            Celular = rd.GetString(7),
            Radio = rd.GetString(8),
            Email = rd.GetString(9),
            Contacto1 = rd.GetString(10),
            Contacto2 = rd.GetString(11),
            MedioPago = rd.GetString(12),
            ControlSaldo = rd.GetBoolean(13),
            UltLote = rd.GetInt64(14),
            CairoCodigo = rd.GetString(15),
            CairoIibb = rd.GetString(16),
            YpfRuta = rd.GetBoolean(17),
            EssoCard = rd.GetBoolean(18),
            CtaCte = rd.GetBoolean(19),
        };
    }

    /// <summary>Rubros de contacto (estacion_rubro.scx) — id + nombre (+ flag audita). ORDER BY rubro.</summary>
    public async Task<List<RubroContactoRow>> GetRubrosContactoAsync()
    {
        return await _cache.GetOrCreateAsync("rubros-contacto", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id AS Id, RTRIM(ISNULL(rubro, '')) AS Rubro, ISNULL(audita, 0) AS Audita
                FROM estacion_rubro
                WHERE _deleted = 0
                ORDER BY rubro
                """;
            var result = new List<RubroContactoRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new RubroContactoRow(rd.GetInt32(0), rd.GetString(1), rd.GetBoolean(2)));
            return result;
        }) ?? new();
    }

    // ── Lista de pasajeros (trafico_pasajero_planilla.scx) ───────────────────
    //  El dialog de pasajeros ya existe (GetPasajerosViajeAsync). Falta el buscador de viaje:
    //  elegís una fecha + texto y ves los viajes de ese día para abrir su lista de pasajeros.

    /// <summary>Buscador de viajes de una fecha (para la pantalla Lista de pasajeros). Filtra por
    /// interno / servicio / cliente / destino con <paramref name="texto"/>. Acota a viajes reales.</summary>
    public async Task<List<ViajeBuscadorRow>> GetViajesParaBuscadorAsync(DateOnly fecha, string? texto)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        var filtroTexto = string.IsNullOrWhiteSpace(texto)
            ? ""
            : """
              AND (CAST(v.interno AS varchar) LIKE @txt
                   OR v.id_servici LIKE @txt OR v.nombre_cli LIKE @txt
                   OR v.d_destino LIKE @txt OR v.h_destino LIKE @txt)
              """;
        cmd.CommandText = $"""
            SELECT
                CAST(v.id_viaje AS bigint)                       AS IdViaje,
                v.f_reserva                                      AS FReserva,
                CAST(ISNULL(v.interno, 0) AS int)               AS Interno,
                RTRIM(ISNULL(v.id_servici, ''))                AS Servicio,
                RTRIM(ISNULL(v.nombre_cli, ''))                AS Cliente,
                LTRIM(RTRIM(ISNULL(v.d_destino,'') + ' a ' + ISNULL(v.h_destino,''))) AS Destino,
                RTRIM(ISNULL(v.hs_s_inici, ''))                AS Hora,
                RTRIM(ISNULL(v.estado_via, ''))                AS Estado
            FROM viaje v
            WHERE v._deleted = 0 AND v.f_reserva = @f {filtroTexto}
            ORDER BY v.hs_s_inici, v.interno
            """;
        cmd.Parameters.Add(NuevoParam(cmd, "@f", fecha.ToDateTime(TimeOnly.MinValue)));
        if (!string.IsNullOrWhiteSpace(texto))
            cmd.Parameters.Add(NuevoParam(cmd, "@txt", "%" + texto.Trim() + "%"));
        var result = new List<ViajeBuscadorRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            result.Add(new ViajeBuscadorRow(
                rd.GetInt64(0),
                rd.IsDBNull(1) ? null : DateOnly.FromDateTime(rd.GetDateTime(1)),
                rd.GetInt32(2), rd.GetString(3), rd.GetString(4), rd.GetString(5),
                rd.GetString(6), rd.GetString(7)));
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  MÓDULO COMBUSTIBLE (07/07/2026) — solo lectura + andamiaje ABM
    //  Tabla viva: vehiculo_sobre (era 2, conciliación por lote). Trampas verificadas en
    //  docs/PlanoFoxPro/combustible/COMBUSTIBLE_ABM_MENU.md: estacion_n (truncado),
    //  idrubro/interno/odometro/n_sobre/estacion son bigint, f_carga es la fecha operativa,
    //  n_sobre=0 = sin conciliar. Filtrar SIEMPRE f_carga entre 2009 y 2027 (años corruptos).
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>Rango sano de f_carga del módulo combustible (evita los años corruptos, ej. 4202).</summary>
    private const string CombRangoFCarga = "a.f_carga BETWEEN '2009-01-01' AND '2027-12-31'";

    // El módulo combustible tiene datos desde 2010 (saldos históricos 2013-2017), fuera del rango
    // de `viaje` (2021+). Por eso NO usa ClampFecha (que acota a 2021): usa su propio rango sano.
    private static readonly DateOnly CombFechaMin = new(2009, 1, 1);
    private static readonly DateOnly CombFechaMax = new(2027, 12, 31);
    private static DateOnly ClampComb(DateOnly d) => d < CombFechaMin ? CombFechaMin : (d > CombFechaMax ? CombFechaMax : d);

    /// <summary>Promedio de Consumos (vehiculo_combustible_consumo). Trae las cargas del rubro
    /// combustible (parametro.rubro_comb) del período, ordenadas por dominio+fecha+hora, para que el
    /// cálculo l/100km se haga EN MEMORIA en la página entre cargas LLENO (método correcto: Σlitros/Σkm
    /// por tramo, no carga a carga). dominio null = toda la flota propia.</summary>
    public async Task<List<CargaConsumoRow>> GetPromedioConsumosAsync(string? dominio, DateOnly desde, DateOnly hasta)
    {
        desde = ClampComb(desde); hasta = ClampComb(hasta);
        var dom = (dominio ?? "").Trim().ToUpperInvariant();
        return await _cache.GetOrCreateAsync($"comb-consumo|{dom}|{desde:yyyyMMdd}|{hasta:yyyyMMdd}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var wheres = new List<string>
            {
                CombRangoFCarga,
                "a.f_carga BETWEEN @desde AND @hasta",
                "a.idrubro = (SELECT ISNULL(rubro_comb, 1) FROM parametro)",
                "ISNULL(a._deleted, 0) = 0",
            };
            if (dom.Length > 0) wheres.Add("a.dominio = @dom");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    RTRIM(ISNULL(a.dominio, ''))          AS Dominio,
                    CAST(ISNULL(a.interno, 0) AS int)     AS Interno,
                    a.f_carga                             AS FCarga,
                    RTRIM(ISNULL(a.hora, ''))             AS Hora,
                    RTRIM(ISNULL(a.chofer, ''))           AS Chofer,
                    RTRIM(ISNULL(a.estacion_n, ''))       AS Estacion,
                    RTRIM(ISNULL(a.tipo_carga, ''))       AS TipoCarga,
                    CAST(ISNULL(a.odometro, 0) AS bigint) AS Odometro,
                    ISNULL(a.litros, 0)                   AS Litros,
                    ISNULL(a.importe, 0)                  AS Importe,
                    ISNULL(a.lleno, 0)                    AS Lleno
                FROM vehiculo_sobre a
                WHERE {string.Join(" AND ", wheres)}
                ORDER BY a.dominio, a.f_carga, a.hora
                """;
            cmd.Parameters.Add(NuevoParam(cmd, "@desde", desde.ToDateTime(TimeOnly.MinValue)));
            cmd.Parameters.Add(NuevoParam(cmd, "@hasta", hasta.ToDateTime(TimeOnly.MinValue)));
            if (dom.Length > 0) cmd.Parameters.Add(NuevoParam(cmd, "@dom", dom));

            var result = new List<CargaConsumoRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new CargaConsumoRow(
                    rd.GetString(0), rd.GetInt32(1),
                    DateOnly.FromDateTime(rd.GetDateTime(2)),
                    rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6),
                    rd.GetInt64(7), rd.GetDecimal(8), rd.GetDecimal(9), rd.GetBoolean(10)));
            return result;
        }) ?? new();
    }

    /// <summary>Cargas de combustible para la grilla del conciliador (vehiculo_combustible_mant_sobre_lote).
    /// Filtro FoxPro: TODOS / DOMINIO / LOTE / ESTACION. Con "lote" se IGNORAN las fechas (como el fuente).
    /// Devuelve todas las cargas del criterio ordenadas por f_carga, dominio. n_sobre≠0 = conciliada.</summary>
    public async Task<List<CargaCombustibleRow>> GetCargasCombustibleAsync(
        string filtro, string? valor, DateOnly desde, DateOnly hasta)
    {
        filtro = (filtro ?? "todos").Trim().ToLowerInvariant();
        var val = (valor ?? "").Trim();
        desde = ClampComb(desde); hasta = ClampComb(hasta);
        return await _cache.GetOrCreateAsync($"comb-cargas|{filtro}|{val}|{desde:yyyyMMdd}|{hasta:yyyyMMdd}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var wheres = new List<string> { CombRangoFCarga, "ISNULL(a._deleted, 0) = 0" };
            // El filtro LOTE ignora fechas (fiel al FoxPro); el resto acota por período.
            if (filtro != "lote")
                wheres.Add("a.f_carga BETWEEN @desde AND @hasta");
            switch (filtro)
            {
                case "dominio":  wheres.Add("a.dominio = @val"); break;
                case "estacion": wheres.Add("a.estacion_n = @val"); break;
                case "lote":     wheres.Add("a.n_sobre = @lote"); break;
                // "todos": sin filtro extra
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    a.id                                  AS Id,
                    a.f_carga                             AS FCarga,
                    RTRIM(ISNULL(a.hora, ''))             AS Hora,
                    RTRIM(ISNULL(a.estacion_n, ''))       AS Estacion,
                    RTRIM(ISNULL(a.tipo_carga, ''))       AS TipoCarga,
                    RTRIM(ISNULL(a.dominio, ''))          AS Dominio,
                    CAST(ISNULL(a.interno, 0) AS int)     AS Interno,
                    CAST(ISNULL(a.odometro, 0) AS bigint) AS Odometro,
                    ISNULL(a.lleno, 0)                    AS Lleno,
                    ISNULL(a.litros, 0)                   AS Litros,
                    ISNULL(a.importe, 0)                  AS Importe,
                    CAST(ISNULL(a.n_sobre, 0) AS bigint)  AS NSobre,
                    RTRIM(ISNULL(a.f_pago, ''))           AS FPago,
                    RTRIM(ISNULL(a.chofer, ''))           AS Chofer,
                    RTRIM(ISNULL(b.rubro, ''))            AS Rubro
                FROM vehiculo_sobre a
                LEFT JOIN estacion_rubro b ON b.id = a.idrubro AND b._deleted = 0
                WHERE {string.Join(" AND ", wheres)}
                ORDER BY a.f_carga, a.dominio
                """;
            if (filtro != "lote")
            {
                cmd.Parameters.Add(NuevoParam(cmd, "@desde", desde.ToDateTime(TimeOnly.MinValue)));
                cmd.Parameters.Add(NuevoParam(cmd, "@hasta", hasta.ToDateTime(TimeOnly.MinValue)));
            }
            if (filtro is "dominio" or "estacion") cmd.Parameters.Add(NuevoParam(cmd, "@val", val));
            if (filtro == "lote") cmd.Parameters.Add(NuevoParam(cmd, "@lote", long.TryParse(val, out var l) ? l : -1L));

            var result = new List<CargaCombustibleRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new CargaCombustibleRow(
                    rd.GetInt32(0),
                    DateOnly.FromDateTime(rd.GetDateTime(1)),
                    rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5),
                    rd.GetInt32(6), rd.GetInt64(7), rd.GetBoolean(8),
                    rd.GetDecimal(9), rd.GetDecimal(10), rd.GetInt64(11),
                    rd.GetString(12), rd.GetString(13), rd.GetString(14)));
            return result;
        }) ?? new();
    }

    /// <summary>Ficha de una carga de combustible (para el editor/ficha del conciliador). Trae todos los
    /// campos editables del alta FoxPro (vehiculo_combustible_carga_sobre).</summary>
    public async Task<CargaCombustibleDetalleDto?> GetCargaCombustibleRowAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                a.id, CAST(ISNULL(a.interno,0) AS int), RTRIM(ISNULL(a.dominio,'')),
                CAST(ISNULL(a.idrubro,0) AS int), CAST(ISNULL(a.estacion,0) AS int),
                RTRIM(ISNULL(a.estacion_n,'')), RTRIM(ISNULL(a.tipo_carga,'')),
                a.f_carga, RTRIM(ISNULL(a.hora,'')), RTRIM(ISNULL(a.chofer,'')),
                CAST(ISNULL(a.odometro,0) AS bigint), ISNULL(a.litros,0), ISNULL(a.importe,0),
                ISNULL(a.p_x_ltr,0), ISNULL(a.lleno,0), ISNULL(a.dos_carga,0),
                RTRIM(ISNULL(a.f_pago,'')), CAST(ISNULL(a.n_sobre,0) AS bigint),
                RTRIM(ISNULL(a.u_create,'')), a.f_create, RTRIM(ISNULL(a.u_modify,'')), a.f_modify
            FROM vehiculo_sobre a
            WHERE a.id = @id AND ISNULL(a._deleted,0) = 0
            """;
        cmd.Parameters.Add(NuevoParam(cmd, "@id", id));
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new CargaCombustibleDetalleDto
        {
            Id = rd.GetInt32(0),
            Interno = rd.GetInt32(1),
            Dominio = rd.GetString(2),
            IdRubro = rd.GetInt32(3),
            EstacionId = rd.GetInt32(4),
            Estacion = rd.GetString(5),
            TipoCarga = rd.GetString(6),
            FCarga = DateOnly.FromDateTime(rd.GetDateTime(7)),
            Hora = rd.GetString(8),
            Chofer = rd.GetString(9),
            Odometro = rd.GetInt64(10),
            Litros = rd.GetDecimal(11),
            Importe = rd.GetDecimal(12),
            PxLtr = rd.GetDecimal(13),
            Lleno = rd.GetBoolean(14),
            DosCarga = rd.GetBoolean(15),
            FPago = rd.GetString(16),
            NSobre = rd.GetInt64(17),
            UCreate = rd.GetString(18),
            FCreate = rd.IsDBNull(19) ? null : rd.GetDateTime(19),
            UModify = rd.GetString(20),
            FModify = rd.IsDBNull(21) ? null : rd.GetDateTime(21),
        };
    }

    /// <summary>Lista de lotes existentes (n_sobre distintos ≠ 0) con su conteo/litros/importe, para el
    /// panel de conciliación (combo "Agregar a lote existente" y resumen). Ordena por lote descendente.</summary>
    public async Task<List<LoteCombustibleRow>> GetLotesCombustibleAsync()
    {
        return await _cache.GetOrCreateAsync("comb-lotes", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    CAST(a.n_sobre AS bigint)  AS Lote,
                    COUNT(*)                   AS Cargas,
                    ISNULL(SUM(a.litros), 0)   AS Litros,
                    ISNULL(SUM(a.importe), 0)  AS Importe
                FROM vehiculo_sobre a
                WHERE ISNULL(a.n_sobre, 0) <> 0
                  AND a.f_carga BETWEEN '2009-01-01' AND '2027-12-31'
                  AND ISNULL(a._deleted, 0) = 0
                GROUP BY a.n_sobre
                ORDER BY a.n_sobre DESC
                """;
            var result = new List<LoteCombustibleRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new LoteCombustibleRow(rd.GetInt64(0), rd.GetInt32(1), rd.GetDecimal(2), rd.GetDecimal(3)));
            return result;
        }) ?? new();
    }

    /// <summary>Estaciones de servicio del combustible (estacion WHERE rubro = parametro.rubro_comb) —
    /// para los combos de filtro por estación y del editor de carga.</summary>
    public async Task<List<EstacionCombustibleRow>> GetEstacionesCombustibleAsync()
    {
        return await _cache.GetOrCreateAsync("comb-estaciones", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.id, RTRIM(ISNULL(e.nombre,'')), ISNULL(e.control_sa, 0)
                FROM estacion e
                WHERE e.rubro = (SELECT ISNULL(rubro_comb, 1) FROM parametro)
                  AND ISNULL(e._deleted, 0) = 0
                ORDER BY e.nombre
                """;
            var result = new List<EstacionCombustibleRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new EstacionCombustibleRow(rd.GetInt32(0), rd.GetString(1), rd.GetBoolean(2)));
            return result;
        }) ?? new();
    }

    // ── Saldos y Depósitos de estaciones (circuito 2013-2017, sin uso — histórico) ──

    /// <summary>Saldos por estación (vehiculo_estacion_saldo, informe arma_saldo). Debe = depósitos
    /// (vehiculo_estacion_saldo, importe positivo); Haber = consumos (vehiculo_sobre.importe) de las
    /// estaciones con control_sa; Saldo = Debe − Haber. Solo estaciones con control de saldo.</summary>
    public async Task<List<SaldoEstacionRow>> GetSaldosEstacionesAsync(DateOnly desde, DateOnly hasta)
    {
        desde = ClampComb(desde); hasta = ClampComb(hasta);
        return await _cache.GetOrCreateAsync($"comb-saldos|{desde:yyyyMMdd}|{hasta:yyyyMMdd}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // Debe: depósitos (los egresos ya vienen negativos, así que SUM es el neto de depósitos).
            // Haber: consumos a importe real de vehiculo_sobre por la estación (join por NOMBRE, como el FoxPro).
            cmd.CommandText = """
                SELECT
                    e.id                                                     AS EstacionId,
                    RTRIM(ISNULL(e.nombre, ''))                            AS Estacion,
                    ISNULL(dep.Debe, 0)                                      AS Debe,
                    ISNULL(con.Haber, 0)                                     AS Haber,
                    ISNULL(dep.Debe, 0) - ISNULL(con.Haber, 0)               AS Saldo
                FROM estacion e
                OUTER APPLY (
                    SELECT SUM(s.importe) AS Debe
                    FROM vehiculo_estacion_saldo s
                    WHERE s.estacion = e.id
                      AND s.fecha BETWEEN @desde AND @hasta
                      AND ISNULL(s._deleted, 0) = 0
                ) dep
                OUTER APPLY (
                    SELECT SUM(a.importe) AS Haber
                    FROM vehiculo_sobre a
                    WHERE RTRIM(a.estacion_n) = RTRIM(e.nombre)   -- join por NOMBRE (trampa FoxPro)
                      AND a.f_carga BETWEEN @desde AND @hasta
                      AND ISNULL(a._deleted, 0) = 0
                ) con
                WHERE e.rubro = (SELECT ISNULL(rubro_comb, 1) FROM parametro)
                  AND ISNULL(e.control_sa, 0) = 1
                  AND ISNULL(e._deleted, 0) = 0
                ORDER BY e.nombre
                """;
            cmd.Parameters.Add(NuevoParam(cmd, "@desde", desde.ToDateTime(TimeOnly.MinValue)));
            cmd.Parameters.Add(NuevoParam(cmd, "@hasta", hasta.ToDateTime(TimeOnly.MinValue)));
            var result = new List<SaldoEstacionRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new SaldoEstacionRow(
                    rd.GetInt32(0), rd.GetString(1), rd.GetDecimal(2), rd.GetDecimal(3), rd.GetDecimal(4)));
            return result;
        }) ?? new();
    }

    /// <summary>Depósitos cargados (vehiculo_estacion_saldo) para la grilla de Carga / Mantenimiento.
    /// estacionId null/0 = todas. Egreso = importe negativo. Baja física (sin f_delete).</summary>
    public async Task<List<DepositoEstacionRow>> GetDepositosEstacionAsync(int? estacionId, DateOnly desde, DateOnly hasta)
    {
        desde = ClampComb(desde); hasta = ClampComb(hasta);
        return await _cache.GetOrCreateAsync($"comb-depositos|{estacionId ?? 0}|{desde:yyyyMMdd}|{hasta:yyyyMMdd}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            var wheres = new List<string> { "s.fecha BETWEEN @desde AND @hasta", "ISNULL(s._deleted, 0) = 0" };
            if (estacionId is > 0) wheres.Add("s.estacion = @est");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    s.id                                 AS Id,
                    CAST(ISNULL(s.estacion, 0) AS int)   AS EstacionId,
                    RTRIM(ISNULL(s.estacion_n, ''))      AS Estacion,
                    s.fecha                              AS Fecha,
                    RTRIM(ISNULL(s.forma_pago, ''))      AS FormaPago,
                    RTRIM(ISNULL(s.usuario, ''))         AS Usuario,
                    ISNULL(s.importe, 0)                 AS Importe,
                    RTRIM(ISNULL(s.comentario, ''))      AS Comentario
                FROM vehiculo_estacion_saldo s
                WHERE {string.Join(" AND ", wheres)}
                ORDER BY s.fecha DESC, s.id DESC
                """;
            cmd.Parameters.Add(NuevoParam(cmd, "@desde", desde.ToDateTime(TimeOnly.MinValue)));
            cmd.Parameters.Add(NuevoParam(cmd, "@hasta", hasta.ToDateTime(TimeOnly.MinValue)));
            if (estacionId is > 0) cmd.Parameters.Add(NuevoParam(cmd, "@est", estacionId.Value));
            var result = new List<DepositoEstacionRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new DepositoEstacionRow(
                    rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2),
                    DateOnly.FromDateTime(rd.GetDateTime(3)),
                    rd.GetString(4), rd.GetString(5), rd.GetDecimal(6), rd.GetString(7)));
            return result;
        }) ?? new();
    }

    /// <summary>Control de cargas / días sin cargar (trafico_vehiculo_combustible). Para cada vehículo
    /// PROPIO activo con al menos una carga: última carga, odómetro de esa carga, y días transcurridos
    /// desde entonces (DATEDIFF a hoy). Ordena por días desc (los más atrasados arriba). soloSinCargar
    /// = solo los que hace ≥1 día no cargan (el check "Filtra por Vehículos Sin Carga" del FoxPro).</summary>
    public async Task<List<ControlCargaRow>> GetControlCargasAsync(bool soloSinCargar, int diasUmbral)
    {
        return await _cache.GetOrCreateAsync($"comb-control|{soloSinCargar}|{diasUmbral}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // Última carga por unidad (SQL 2012: sin usar funciones nuevas). El odómetro que se muestra
            // es el de esa última carga (subconsulta TOP 1 por f_carga+hora). Link por dominio.
            cmd.CommandText = $"""
                SELECT
                    CAST(ISNULL(v.interno, 0) AS int)                        AS Interno,
                    RTRIM(ISNULL(v.dominio, ''))                           AS Dominio,
                    u.UltCarga                                               AS UltCarga,
                    DATEDIFF(DAY, u.UltCarga, CAST(GETDATE() AS date))       AS Dias,
                    ISNULL(oc.Odometro, 0)                                   AS Odometro
                FROM vehiculo v
                CROSS APPLY (
                    SELECT MAX(b.f_carga) AS UltCarga
                    FROM vehiculo_sobre b
                    WHERE RTRIM(b.dominio) = RTRIM(v.id_vehicul)
                      AND b.f_carga BETWEEN '2009-01-01' AND '2027-12-31'
                      AND ISNULL(b._deleted, 0) = 0
                ) u
                OUTER APPLY (
                    SELECT TOP 1 CAST(ISNULL(b2.odometro, 0) AS bigint) AS Odometro
                    FROM vehiculo_sobre b2
                    WHERE RTRIM(b2.dominio) = RTRIM(v.id_vehicul)
                      AND b2.f_carga = u.UltCarga
                      AND ISNULL(b2._deleted, 0) = 0
                    ORDER BY b2.hora DESC
                ) oc
                WHERE v.activo = 1 AND v.uso = 'PROPIO'
                  AND u.UltCarga IS NOT NULL
                  {(soloSinCargar ? "AND DATEDIFF(DAY, u.UltCarga, CAST(GETDATE() AS date)) >= @umbral" : "")}
                ORDER BY Dias DESC, Interno
                """;
            if (soloSinCargar) cmd.Parameters.Add(NuevoParam(cmd, "@umbral", Math.Max(1, diasUmbral)));
            var result = new List<ControlCargaRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new ControlCargaRow(
                    rd.GetInt32(0), rd.GetString(1),
                    DateOnly.FromDateTime(rd.GetDateTime(2)), rd.GetInt32(3), rd.GetInt64(4)));
            return result;
        }) ?? new();
    }

    /// <summary>Consumo mensual (litros) — informe nuevo que NO existe en FoxPro. Litros por
    /// mes × unidad × estación × tipo de combustible (el importe viene 0 con tarjeta prepaga, por eso
    /// la métrica es litros, no costo). Devuelve el detalle agregado; la página pivotea en memoria.</summary>
    public async Task<List<ConsumoMensualRow>> GetConsumoMensualAsync(DateOnly desde, DateOnly hasta)
    {
        desde = ClampComb(desde); hasta = ClampComb(hasta);
        return await _cache.GetOrCreateAsync($"comb-mensual|{desde:yyyyMMdd}|{hasta:yyyyMMdd}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    CONVERT(char(7), a.f_carga, 120)                         AS Mes,
                    RTRIM(ISNULL(a.dominio, ''))                           AS Dominio,
                    CAST(ISNULL(a.interno, 0) AS int)                        AS Interno,
                    RTRIM(ISNULL(a.estacion_n, ''))                        AS Estacion,
                    RTRIM(ISNULL(a.tipo_carga, ''))                        AS TipoCarga,
                    COUNT(*)                                                 AS Cargas,
                    ISNULL(SUM(a.litros), 0)                                 AS Litros,
                    ISNULL(SUM(a.importe), 0)                                AS Importe
                FROM vehiculo_sobre a
                WHERE a.f_carga BETWEEN @desde AND @hasta
                  AND a.idrubro = (SELECT ISNULL(rubro_comb, 1) FROM parametro)
                  AND ISNULL(a._deleted, 0) = 0
                GROUP BY CONVERT(char(7), a.f_carga, 120), a.dominio, a.interno, a.estacion_n, a.tipo_carga
                ORDER BY Mes, Dominio
                """;
            cmd.Parameters.Add(NuevoParam(cmd, "@desde", desde.ToDateTime(TimeOnly.MinValue)));
            cmd.Parameters.Add(NuevoParam(cmd, "@hasta", hasta.ToDateTime(TimeOnly.MinValue)));
            var result = new List<ConsumoMensualRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new ConsumoMensualRow(
                    rd.GetString(0), rd.GetString(1), rd.GetInt32(2), rd.GetString(3), rd.GetString(4),
                    rd.GetInt32(5), rd.GetDecimal(6), rd.GetDecimal(7)));
            return result;
        }) ?? new();
    }

    /// <summary>Artículos por rubro de consumo (estacion_rubro_articulo.scx). Para el rubro combustible
    /// (=1) son los tipos de combustible del combo de la carga (DIESEL 500 / EURO-DIESEL). rubroId
    /// null/0 = todos los rubros. INNER JOIN a estacion_rubro para el nombre del rubro.</summary>
    public async Task<List<ArticuloRubroRow>> GetArticulosRubroAsync(long? rubroId)
    {
        return await _cache.GetOrCreateAsync($"comb-articulos|{rubroId ?? 0}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            var wheres = new List<string> { "ISNULL(a._deleted, 0) = 0" };
            if (rubroId is > 0) wheres.Add("a.idrubro = @rub");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    a.id                              AS Id,
                    CAST(ISNULL(a.idrubro, 0) AS int) AS RubroId,
                    RTRIM(ISNULL(r.rubro, ''))      AS Rubro,
                    RTRIM(ISNULL(a.nombre, ''))     AS Nombre
                FROM estacion_rubro_articulo a
                LEFT JOIN estacion_rubro r ON r.id = a.idrubro AND r._deleted = 0
                WHERE {string.Join(" AND ", wheres)}
                ORDER BY r.rubro, a.nombre
                """;
            if (rubroId is > 0) cmd.Parameters.Add(NuevoParam(cmd, "@rub", rubroId.Value));
            var result = new List<ArticuloRubroRow>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new ArticuloRubroRow(rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2), rd.GetString(3)));
            return result;
        }) ?? new();
    }

    /// <summary>Ficha de un artículo (para el editor). Sin caché (registro puntual).</summary>
    public async Task<ArticuloRubroRow?> GetArticuloRubroRowAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, CAST(ISNULL(a.idrubro,0) AS int), RTRIM(ISNULL(r.rubro,'')), RTRIM(ISNULL(a.nombre,''))
            FROM estacion_rubro_articulo a
            LEFT JOIN estacion_rubro r ON r.id = a.idrubro AND r._deleted = 0
            WHERE a.id = @id AND ISNULL(a._deleted,0) = 0
            """;
        cmd.Parameters.Add(NuevoParam(cmd, "@id", id));
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new ArticuloRubroRow(rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2), rd.GetString(3));
    }

    /// <summary>Ficha de un depósito (para el editor). Sin caché (registro puntual).</summary>
    public async Task<DepositoEstacionRow?> GetDepositoEstacionRowAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, CAST(ISNULL(s.estacion,0) AS int), RTRIM(ISNULL(s.estacion_n,'')),
                   s.fecha, RTRIM(ISNULL(s.forma_pago,'')), RTRIM(ISNULL(s.usuario,'')),
                   ISNULL(s.importe,0), RTRIM(ISNULL(s.comentario,''))
            FROM vehiculo_estacion_saldo s
            WHERE s.id = @id AND ISNULL(s._deleted,0) = 0
            """;
        cmd.Parameters.Add(NuevoParam(cmd, "@id", id));
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new DepositoEstacionRow(
            rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2),
            DateOnly.FromDateTime(rd.GetDateTime(3)),
            rd.GetString(4), rd.GetString(5), rd.GetDecimal(6), rd.GetString(7));
    }

    /// <summary>Helper: crea un SqlParameter con nombre y valor (evita repetir el patrón CreateParameter).</summary>
    private static System.Data.Common.DbParameter NuevoParam(System.Data.Common.DbCommand cmd, string nombre, object? valor)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = nombre;
        p.Value = valor ?? DBNull.Value;
        return p;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MÓDULO RESERVAS — Reservas Especiales (viaje origen 'T') y Plantillas
    //  (reserva_plantilla + armado a viaje origen 'P'). Solo lectura + andamiaje ABM.
    //  Planos: docs/PlanoFoxPro/reservas/RESERVA_TRANSPORTACION.md y RESERVA_PLANTILLAS.md.
    //  🐛 Trampa: en `viaje` son bigint id_grupo/id_plantil/id_viaje_i/interno/km/voucher_nr;
    //  en `reserva_plantilla` son bigint hs/km/km_real/pax/adi_can_1..5 → CAST(... AS int).
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Grilla de Reservas Especiales: las reservas cargadas manualmente (viaje.origen='T').
    /// Réplica de consulta del alta manual (reserva_transportacion_con_adicional.scx) — acá se
    /// LISTAN las ya cargadas para consultar; el alta nueva es el andamiaje (dialog + AbmService).
    /// Rango de fechas obligatorio (~12-19k filas/año) + búsqueda + estados. Reusa el DTO del
    /// informe de reservas (drill-down al Zoom del Viaje comparte ReservaFsDetalleRow).
    /// </summary>
    public async Task<List<ReservaFsDetalleRow>> GetReservasEspecialesAsync(
        DateOnly desde, DateOnly hasta,
        IReadOnlyCollection<string> estadosSel, string? busqueda)
    {
        desde = ClampFecha(desde);
        hasta = ClampFecha(hasta);
        var estKey = estadosSel.Count == 0 ? "all" : string.Join(",", estadosSel.OrderBy(x => x));
        var busKey = string.IsNullOrWhiteSpace(busqueda) ? "" : busqueda.Trim().ToUpperInvariant();
        var key = $"resesp|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{estKey}|{busKey}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // Filtro de estados (misma convención del proyecto: lista vacía = todos).
            var estWhere = "";
            if (estadosSel.Count > 0)
            {
                var lista = string.Join(",", estadosSel.Select(e => "'" + e.Replace("'", "''") + "'"));
                estWhere = $" AND v.estado_via IN ({lista})";
            }
            // Búsqueda libre (cliente / servicio / destinos / grupo).
            var busWhere = "";
            if (!string.IsNullOrWhiteSpace(busqueda))
            {
                var b = busqueda.Trim().Replace("'", "''");
                busWhere =
                    " AND (UPPER(COALESCE(v.nombre_cli,'')) LIKE '%" + b + "%'" +
                    " OR UPPER(COALESCE(v.id_cliente,'')) LIKE '%" + b + "%'" +
                    " OR UPPER(COALESCE(v.id_servici,'')) LIKE '%" + b + "%'" +
                    " OR UPPER(COALESCE(v.d_destino,'')) LIKE '%" + b + "%'" +
                    " OR UPPER(COALESCE(v.h_destino,'')) LIKE '%" + b + "%'" +
                    " OR UPPER(COALESCE(v.grupo,'')) LIKE '%" + b + "%')";
            }

            // v.id_viaje/v.pax son int; v.interno es bigint → Convert.ToInt32 al leer.
            var sql = $"""
                SELECT
                    v.id_viaje                                            AS IdViaje,
                    v.f_reserva                                           AS Fecha,
                    COALESCE(CONVERT(varchar(5), v.hs_inicio, 108), '')   AS Hora,
                    COALESCE(v.id_servici, '')                            AS CodServicio,
                    COALESCE(s.nombre, v.id_servici, '')                  AS Servicio,
                    COALESCE(NULLIF(LTRIM(RTRIM(v.nombre_cli)), ''), v.id_cliente, '') AS Cliente,
                    LTRIM(RTRIM(COALESCE(v.d_destino, ''))) +
                        CASE WHEN LTRIM(RTRIM(COALESCE(v.h_destino, ''))) <> ''
                             THEN ' a ' + LTRIM(RTRIM(v.h_destino)) ELSE '' END AS Recorrido,
                    COALESCE(v.pax, 0)                                    AS Pax,
                    COALESCE(v.estado_via, '')                            AS Estado,
                    COALESCE(v.nombre_cho, '')                            AS Chofer,
                    v.interno                                             AS Interno,
                    COALESCE(v.origen, '')                                AS Origen,
                    COALESCE(v.grupo, '')                                 AS Grupo
                FROM viaje v
                LEFT JOIN servicio s ON v.id_servici = s.id_servici
                WHERE v._deleted = 0
                  AND v.origen = 'T'
                  AND v.f_reserva BETWEEN '{desde:yyyyMMdd}' AND '{hasta:yyyyMMdd}'
                  {estWhere}
                  {busWhere}
                ORDER BY v.f_reserva DESC, v.hs_inicio, v.id_viaje
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = new List<ReservaFsDetalleRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new ReservaFsDetalleRow(
                    IdViaje: reader.GetInt32(0),
                    Fecha: DateOnly.FromDateTime(reader.GetDateTime(1)),
                    Hora: reader.GetString(2),
                    CodServicio: reader.GetString(3).Trim(),
                    Servicio: reader.GetString(4).Trim(),
                    Cliente: reader.GetString(5).Trim(),
                    Recorrido: reader.GetString(6).Trim(),
                    Pax: reader.GetInt32(7),
                    Estado: reader.GetString(8).Trim(),
                    Chofer: reader.GetString(9).Trim(),
                    Interno: reader.IsDBNull(10) ? null : Convert.ToInt32(reader.GetValue(10)),
                    Origen: reader.GetString(11).Trim(),
                    Grupo: reader.GetString(12).Trim()));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Resumen de plantillas (Mantenimiento de Plantillas — reserva_plantilla_mantenimiento.scx):
    /// una fila por plantilla (id_reserva = nombre = clave de agrupación) con cantidad de filas y
    /// rango horario. Baja física (sin f_delete), así que solo se filtra _deleted de la réplica.
    /// </summary>
    public async Task<List<PlantillaResumenRow>> GetPlantillasResumenAsync()
    {
        return await _cache.GetOrCreateAsync("plantillas-resumen", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    RTRIM(id_reserva)          AS IdReserva,
                    COUNT(*)                   AS Filas,
                    MIN(RTRIM(ISNULL(hs_inicio,''))) AS HoraDesde,
                    MAX(RTRIM(ISNULL(hs_inicio,''))) AS HoraHasta,
                    SUM(CAST(ISNULL(pax,0) AS int))  AS PaxTotal
                FROM reserva_plantilla
                WHERE _deleted = 0
                GROUP BY RTRIM(id_reserva)
                ORDER BY RTRIM(id_reserva)
                """;
            var result = new List<PlantillaResumenRow>();
            using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                result.Add(new PlantillaResumenRow(
                    rd.GetString(0).Trim(), rd.GetInt32(1),
                    rd.GetString(2).Trim(), rd.GetString(3).Trim(), rd.GetInt32(4)));
            return result;
        }) ?? new();
    }

    /// <summary>Solo los nombres de plantilla (combo del armado y del Mantenimiento).</summary>
    public async Task<List<string>> GetPlantillasComboAsync()
    {
        var resumen = await GetPlantillasResumenAsync();
        return resumen.Select(p => p.IdReserva).ToList();
    }

    /// <summary>
    /// Filas de una plantilla (la grilla del Mantenimiento y del Armado). Los 5 slots de
    /// adicionales se colapsan a un texto legible ("NOMBRE×cant, …"). Orden por hs_inicio como
    /// el FoxPro. Trampa: hs/km/pax/adi_can_* son bigint → CAST(... AS int).
    /// </summary>
    public async Task<List<PlantillaFilaRow>> GetPlantillaFilasAsync(string idReserva)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = PlantillaFilaSelect
            + " WHERE p._deleted = 0 AND RTRIM(p.id_reserva) = @id"
            + " ORDER BY RTRIM(ISNULL(p.hs_inicio,'')), p.id";
        cmd.Parameters.Add(NuevoParam(cmd, "@id", idReserva));
        var result = new List<PlantillaFilaRow>();
        using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            result.Add(MapPlantillaFila(rd));
        return result;
    }

    /// <summary>Ficha de una fila de plantilla (para ver/modifica/baja). Sin caché.</summary>
    public async Task<PlantillaFilaRow?> GetPlantillaFilaRowAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = PlantillaFilaSelect + " WHERE p._deleted = 0 AND p.id = @id";
        cmd.Parameters.Add(NuevoParam(cmd, "@id", id));
        using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return MapPlantillaFila(rd);
    }

    // Proyección compartida de una fila de plantilla (mantener en sync con MapPlantillaFila).
    private const string PlantillaFilaSelect = """
        SELECT
            p.id                                       AS Id,
            RTRIM(ISNULL(p.id_reserva,''))             AS IdReserva,
            RTRIM(ISNULL(p.hs_inicio,''))              AS HoraIni,
            RTRIM(ISNULL(p.hs_fin,''))                 AS HoraFin,
            RTRIM(ISNULL(p.id_servici,''))             AS IdServicio,
            RTRIM(ISNULL(s.nombre, p.id_servici))      AS Servicio,
            RTRIM(ISNULL(p.id_vehicul,''))             AS TipoVeh,
            RTRIM(ISNULL(p.desde,''))                  AS Desde,
            RTRIM(ISNULL(p.hasta,''))                  AS Hasta,
            CAST(ISNULL(p.pax,0) AS int)               AS Pax,
            CAST(ISNULL(p.km,0) AS int)                AS Km,
            CAST(ISNULL(p.hs,0) AS int)                AS Hs,
            RTRIM(ISNULL(p.cabecera,''))               AS Cabecera,
            RTRIM(ISNULL(p.nombre_gui,''))             AS Guia,
            RTRIM(ISNULL(p.guia_dueno,''))             AS GuiaDueno,
            RTRIM(ISNULL(p.empresa_de,''))             AS EmpresaDestino,
            RTRIM(ISNULL(p.recorrido_,''))             AS Recorrido,
            RTRIM(ISNULL(p.d_destino_,''))             AS Provincia,
            RTRIM(ISNULL(p.comentario,''))             AS Comentario,
            RTRIM(ISNULL(p.cronograma,''))             AS Cronograma,
            RTRIM(ISNULL(p.adi_nom_1,'')) AS An1, CAST(ISNULL(p.adi_can_1,0) AS int) AS Ac1,
            RTRIM(ISNULL(p.adi_nom_2,'')) AS An2, CAST(ISNULL(p.adi_can_2,0) AS int) AS Ac2,
            RTRIM(ISNULL(p.adi_nom_3,'')) AS An3, CAST(ISNULL(p.adi_can_3,0) AS int) AS Ac3,
            RTRIM(ISNULL(p.adi_nom_4,'')) AS An4, CAST(ISNULL(p.adi_can_4,0) AS int) AS Ac4,
            RTRIM(ISNULL(p.adi_nom_5,'')) AS An5, CAST(ISNULL(p.adi_can_5,0) AS int) AS Ac5
        FROM reserva_plantilla p
        LEFT JOIN servicio s ON p.id_servici = s.id_servici
        """;

    private static PlantillaFilaRow MapPlantillaFila(System.Data.Common.DbDataReader rd)
    {
        // Colapsa los 5 slots de adicionales a texto "NOMBRE×cant, …" (ignora los vacíos).
        var adics = new List<string>();
        for (int i = 20; i <= 28; i += 2)
        {
            var nom = rd.GetString(i).Trim();
            var can = rd.GetInt32(i + 1);
            if (!string.IsNullOrWhiteSpace(nom))
                adics.Add(can > 1 ? $"{nom}×{can}" : nom);
        }
        return new PlantillaFilaRow(
            Id: rd.GetInt32(0),
            IdReserva: rd.GetString(1).Trim(),
            HoraIni: rd.GetString(2).Trim(),
            HoraFin: rd.GetString(3).Trim(),
            IdServicio: rd.GetString(4).Trim(),
            Servicio: rd.GetString(5).Trim(),
            TipoVeh: rd.GetString(6).Trim(),
            Desde: rd.GetString(7).Trim(),
            Hasta: rd.GetString(8).Trim(),
            Pax: rd.GetInt32(9),
            Km: rd.GetInt32(10),
            Hs: rd.GetInt32(11),
            Cabecera: rd.GetString(12).Trim(),
            Guia: rd.GetString(13).Trim(),
            GuiaDueno: rd.GetString(14).Trim(),
            EmpresaDestino: rd.GetString(15).Trim(),
            Recorrido: rd.GetString(16).Trim(),
            Provincia: rd.GetString(17).Trim(),
            Comentario: rd.GetString(18).Trim(),
            Cronograma: rd.GetString(19).Trim(),
            Adicionales: string.Join(", ", adics));
    }

    /// <summary>
    /// Feriados en un rango (tabla `feriado`) — el armado los usa para excluir/incluir generación.
    /// ⚠ Hoy hay 0 feriados de 2026 cargados (alerta documentada en FERIADO_ABM.md): el armado
    /// debe avisar como el FoxPro. Sin caché (poca data, rango variable).
    /// </summary>
    public async Task<List<DateOnly>> GetFeriadosRangoAsync(DateOnly desde, DateOnly hasta)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fecha FROM feriado WHERE _deleted = 0 AND fecha BETWEEN @d AND @h ORDER BY fecha";
        cmd.Parameters.Add(NuevoParam(cmd, "@d", desde.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(NuevoParam(cmd, "@h", hasta.ToDateTime(TimeOnly.MinValue)));
        var result = new List<DateOnly>();
        using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            if (!rd.IsDBNull(0)) result.Add(DateOnly.FromDateTime(rd.GetDateTime(0)));
        return result;
    }
}

// ── DTOs de Fleteros ─────────────────────────────────────────────────────────────

/// <summary>Fila de la grilla de Fleteros (fletero.scx). Egresado = FDelete con valor.</summary>
public record FleteroRow(
    int Id, string IdContrat, string RazonSocial, string Nombre, long Orden,
    string Cuit, string Localidad, string Telefono, string Email, DateOnly? FDelete);

/// <summary>Ficha completa de un fletero (fletero_abm.scx).</summary>
public class FleteroDetalleDto
{
    public int Id { get; set; }
    public string IdContrat { get; set; } = "";
    public string RazonSocial { get; set; } = "";
    public string Nombre { get; set; } = "";
    public long Orden { get; set; }
    public string Cuit { get; set; } = "";
    public string TipoResp { get; set; } = "";
    public string Domicilio { get; set; } = "";
    public string Localidad { get; set; } = "";
    public string Postal { get; set; } = "";
    public string Provincia { get; set; } = "";
    public string Telefono { get; set; } = "";
    public string Celular { get; set; } = "";
    public string Email { get; set; } = "";
    public string Contacto { get; set; } = "";
    public string IdListaP { get; set; } = "";
    public string IdLista2 { get; set; } = "";
    public string ModoLiq { get; set; } = "";
    public string FcPrefere { get; set; } = "";
    public bool Diagrama { get; set; }
    public DateOnly? FCreate { get; set; }
    public DateOnly? FModify { get; set; }
    public DateOnly? FDelete { get; set; }
}

// ── DTOs de Tipo de Vehículos ────────────────────────────────────────────────────

/// <summary>Fila de la grilla de Tipo de Vehículos (vehiculo_tipo.scx). Codigo = id_vehicul
/// (PK lógica del TIPO: BUS/VAN/MINI/…). Egresado = FDelete con valor.</summary>
public record TipoVehiculoRow(
    int Id, string Codigo, string Nombre, int Pax, string Subtipo,
    decimal? ConsumoMin, decimal? ConsumoMax, bool Vende, string DirDibujo, DateOnly? FDelete);

// ── DTOs del módulo Reservas: Operadores · Grupos · Destinos ─────────────────────

/// <summary>Fila de la grilla de Operadores (cliente_operador.scx). IdOperador = PK lógica global.
/// RazonSocial sale del LEFT JOIN a cliente ("" si el cliente no existe). Sin f_delete (baja física).</summary>
public record OperadorRow(
    int Id, string IdOperador, string IdCliente, string RazonSocial, string Nombre,
    string Telefono, string Celular, string Interno, string Email, string Comentario);

/// <summary>Fila de la grilla de Grupos (cliente_grupo.scx). La dupla (IdCliente, Nombre) es la clave
/// lógica. FFacturo con valor = grupo CERRADO (candado: no se renombra ni cambia fecha). Sin papelera
/// (baja = DELETE físico). Truncados: f_grupo_in→FInicio, f_grupo_fi→FFin, f_grupo_fc→FFacturo.</summary>
public record GrupoRow(
    int Id, string IdCliente, string RazonSocial, string Nombre,
    DateOnly? FInicio, DateOnly? FFin, DateOnly? FFacturo)
{
    /// <summary>Grupo facturado (candado): tiene fecha de facturación.</summary>
    public bool Cerrado => FFacturo is not null;
}

/// <summary>Fila de la grilla de Destinos (destino.scx). Destino = nombre (clave lógica, MAYÚSCULAS).
/// Mas100Km = recargo por distancia que se copia a viaje.mas100km. Sin f_delete (baja física).</summary>
public record DestinoRow(
    int Id, string Destino, string Direccion, string Localidad, string Telefono,
    string Correo, string Contacto, string Cabecera, bool Mas100Km);

// ── DTOs de Agenda de Vencimientos ───────────────────────────────────────────────

/// <summary>Chofer con vencimientos de registro/CNRT/AEP (agenda_vencimiento.scx, 1er cursor).</summary>
public record ChoferVtoRow(
    string IdChofer, string Nombre, string Fletero, string RegistroNro,
    DateOnly? RegistroVto, DateOnly? CnrtVto, DateOnly? AepVto);

/// <summary>Vehículo propio con vencimientos de VTV/matafuegos (agenda_vencimiento.scx, 2do cursor).</summary>
public record VehiculoVtoRow(
    int Interno, string Dominio,
    DateOnly? VtvVto, DateOnly? MatafuegoVto, DateOnly? PolizaVto, DateOnly? HabilitacionVto)
{
    /// <summary>Interno con formato de la Planilla de Tráfico: NT + 4 dígitos.</summary>
    public string InternoNT => Interno == 0 ? "—" : "NT" + Interno.ToString("D4");
}

// ── DTOs de Tráfico: Cabeceras · Francos · Viáticos ─────────────────────────────

/// <summary>Fila de Cabeceras/Recorridos (cabecera_recorrido.scx). codigo = PK lógica;
/// nombre/nombre1/nombre2 = las 3 descripciones; recorrido = texto largo (detalle del itinerario).</summary>
public record CabeceraRow(int Id, string Codigo, string Nombre, string Nombre1, string Nombre2, string Recorrido);

/// <summary>Fila del Mantenimiento de Francos (chofer_franco.scx). Trabajo = flag de si trabajó igual.</summary>
public record FrancoRow(
    int Id, string IdChofer, string Nombre, string Codigo, string Motivo, DateOnly Fecha, bool Trabajo);

/// <summary>Fila de la Auditoría de Francos (chofer_franco_auditoria.scx). Dias es un arreglo 1..N
/// (índice 0 sin usar) donde cada celda es "" (nada), "trb" (trabajó), el código del franco en
/// minúscula, o "DUP" (franco + trabajo el mismo día = problema).</summary>
public record FrancoAuditoriaRow(string IdChofer, string Nombre, string[] Dias, int DiasTrab, int Problemas);

/// <summary>Fila de Viáticos (chofer_viatico.scx).</summary>
public record ViaticoRow(
    int Id, DateOnly Fecha, string IdChofer, string Conductor, string Motivo,
    string FormaLiquida, string FormaPago, decimal Importe, DateOnly? FPago);

/// <summary>Fila de un catálogo simple id+nombre (chofer_viatico_motivo / chofer_viatico_liquida).
/// IdChofer se usa solo cuando el catálogo es la lista de choferes para combos.</summary>
public record CatalogoSimpleRow(int Id, string Nombre)
{
    public string IdChofer { get; init; } = "";
}

// ── DTOs de Tráfico: Voucher · Guardia · Contactos · Lista de pasajeros (06/07/2026) ──

/// <summary>Fila de la auditoría de vouchers (trafico_voucher.scx). VoucherRecep con valor = recibido.
/// Truncados: voucher_nr→VoucherNro, voucher_re→VoucherRecep, hs_s_inici→Hora.</summary>
public record VoucherRow(
    long VoucherNro, DateOnly? VoucherRecep, long IdViaje, DateOnly? FReserva, string Hora,
    int Interno, string Destino, string IdChofer, string Vehiculo, string IdCliente, string Comentario)
{
    /// <summary>Interno con formato de la Planilla de Tráfico (NT + 4 dígitos).</summary>
    public string InternoNT => Interno == 0 ? "—" : "NT" + Interno.ToString("D4");
    /// <summary>true = ya se recepcionó el voucher (tiene fecha).</summary>
    public bool Recepcionado => VoucherRecep is not null;
}

/// <summary>Fila de la grilla de Guardias (trafico_guardia.scx). FPago con valor = guardia ya pagada
/// (candado del modifica). Truncados: id_vehicul→IdVehiculo, nombre_cho→Nombre. Baja física.</summary>
public record GuardiaRow(
    int Id, int Interno, string IdVehiculo, string IdChofer, string Nombre, bool Franco,
    DateOnly? Fecha, DateTime? HsInicio, DateTime? HsFin, DateOnly? FPago)
{
    /// <summary>Interno con formato NT + 4 dígitos.</summary>
    public string InternoNT => Interno == 0 ? "—" : "NT" + Interno.ToString("D4");
    /// <summary>Guardia pagada (candado): tiene fecha de pago.</summary>
    public bool Pagada => FPago is not null;
}

/// <summary>Fila de la grilla de Contactos/Proveedores (estacion.scx). RubroId = FK a estacion_rubro.
/// Rubro = nombre del rubro (LEFT JOIN). Sin f_delete (baja física).</summary>
public record ContactoRow(
    int Id, long RubroId, string Rubro, string Nombre, string Domicilio, string Localidad,
    string Provincia, string Telefono, string Celular, string Radio, string Contacto1, string Contacto2);

/// <summary>Ficha completa de un contacto (estacion_abm.scx). Los campos control_sa/ult_lote/ypf_ruta/
/// esso_card/cta_cte/cairo_* son legacy del módulo Combustible (la mayoría de proveedores no los usa).</summary>
public class ContactoDetalleDto
{
    public int Id { get; set; }
    public long RubroId { get; set; }
    public string Nombre { get; set; } = "";
    public string Domicilio { get; set; } = "";
    public string Localidad { get; set; } = "";
    public string Provincia { get; set; } = "";
    public string Telefono { get; set; } = "";
    public string Celular { get; set; } = "";
    public string Radio { get; set; } = "";
    public string Email { get; set; } = "";
    public string Contacto1 { get; set; } = "";
    public string Contacto2 { get; set; } = "";
    public string MedioPago { get; set; } = "";
    public bool ControlSaldo { get; set; }
    public long UltLote { get; set; }
    public string CairoCodigo { get; set; } = "";
    public string CairoIibb { get; set; } = "";
    public bool YpfRuta { get; set; }
    public bool EssoCard { get; set; }
    public bool CtaCte { get; set; }
}

/// <summary>Fila de la grilla de Rubros de contacto (estacion_rubro.scx). audita = flag que activa
/// validaciones extra en la carga de combustible (hoy apagado en casi todos). Baja física.</summary>
public record RubroContactoRow(int Id, string Rubro, bool Audita);

/// <summary>Fila del buscador de viajes de la pantalla Lista de pasajeros. Punto de entrada al
/// ListaPasajerosDialog ya existente.</summary>
public record ViajeBuscadorRow(
    long IdViaje, DateOnly? FReserva, int Interno, string Servicio, string Cliente,
    string Destino, string Hora, string Estado)
{
    /// <summary>Interno con formato NT + 4 dígitos.</summary>
    public string InternoNT => Interno == 0 ? "—" : "NT" + Interno.ToString("D4");
}

// ── DTOs del módulo Combustible (07/07/2026) ─────────────────────────────────────

/// <summary>Una carga de combustible para el cálculo de consumos (vehiculo_combustible_consumo).
/// El l/100km NO se calcula acá: se hace en memoria en la página entre cargas LLENO. Lleno=false =
/// carga parcial (no cierra tramo). Importe llega en 0 en datos recientes (tarjeta prepaga).</summary>
public record CargaConsumoRow(
    string Dominio, int Interno, DateOnly FCarga, string Hora, string Chofer, string Estacion,
    string TipoCarga, long Odometro, decimal Litros, decimal Importe, bool Lleno)
{
    /// <summary>Interno con formato de la Planilla de Tráfico (NT + 4 dígitos).</summary>
    public string InternoNT => Interno == 0 ? "—" : "NT" + Interno.ToString("D4");
}

/// <summary>Fila de la grilla del conciliador (vehiculo_combustible_mant_sobre_lote). NSobre≠0 =
/// carga ya conciliada (fila amarilla). Rubro = nombre del rubro (LEFT JOIN estacion_rubro).</summary>
public record CargaCombustibleRow(
    int Id, DateOnly FCarga, string Hora, string Estacion, string TipoCarga, string Dominio,
    int Interno, long Odometro, bool Lleno, decimal Litros, decimal Importe, long NSobre,
    string FPago, string Chofer, string Rubro)
{
    /// <summary>Interno con formato NT + 4 dígitos.</summary>
    public string InternoNT => Interno == 0 ? "—" : "NT" + Interno.ToString("D4");
    /// <summary>Carga ya conciliada (asignada a un lote).</summary>
    public bool Conciliada => NSobre != 0;
}

/// <summary>Ficha completa de una carga (vehiculo_combustible_carga_sobre) para el editor/andamiaje.</summary>
public class CargaCombustibleDetalleDto
{
    public int Id { get; set; }
    public int Interno { get; set; }
    public string Dominio { get; set; } = "";
    public int IdRubro { get; set; }
    public int EstacionId { get; set; }
    public string Estacion { get; set; } = "";
    public string TipoCarga { get; set; } = "";
    public DateOnly FCarga { get; set; }
    public string Hora { get; set; } = "";
    public string Chofer { get; set; } = "";
    public long Odometro { get; set; }
    public decimal Litros { get; set; }
    public decimal Importe { get; set; }
    public decimal PxLtr { get; set; }
    public bool Lleno { get; set; }
    public bool DosCarga { get; set; }
    public string FPago { get; set; } = "";
    public long NSobre { get; set; }
    public string UCreate { get; set; } = "";
    public DateTime? FCreate { get; set; }
    public string UModify { get; set; } = "";
    public DateTime? FModify { get; set; }

    public string InternoNT => Interno == 0 ? "—" : "NT" + Interno.ToString("D4");
    public bool Conciliada => NSobre != 0;
}

/// <summary>Resumen de un lote de conciliación (n_sobre agrupado): cargas, litros e importe.</summary>
public record LoteCombustibleRow(long Lote, int Cargas, decimal Litros, decimal Importe);

/// <summary>Consumo agregado por unidad (calculado en memoria en la página con el método correcto:
/// Σlitros/Σkm entre cargas LLENO). KmRecorridos = suma de tramos válidos; L100 = litros/km×100 del
/// total; CostoKm = importe/km (null si no hay importe — tarjeta prepaga). Cargas = total de cargas.</summary>
public record ConsumoUnidadRow(
    string Dominio, int Interno, int Cargas, long KmRecorridos, decimal Litros,
    decimal Importe, double? L100, decimal? CostoKm)
{
    public string InternoNT => Interno == 0 ? "—" : "NT" + Interno.ToString("D4");
}

/// <summary>Estación de servicio del combustible (estacion WHERE rubro = rubro_comb).
/// ControlSaldo = participa del control de saldos/depósitos (solo 3 YPF históricamente).</summary>
public record EstacionCombustibleRow(int Id, string Nombre, bool ControlSaldo);

/// <summary>Saldo por estación (vehiculo_estacion_saldo, informe histórico 2013-2017).
/// Debe = depósitos; Haber = consumos; Saldo = Debe − Haber.</summary>
public record SaldoEstacionRow(int EstacionId, string Estacion, decimal Debe, decimal Haber, decimal Saldo);

/// <summary>Un movimiento de depósito (vehiculo_estacion_saldo). Importe negativo = egreso.
/// Sin f_delete → baja física.</summary>
public record DepositoEstacionRow(
    int Id, int EstacionId, string Estacion, DateOnly Fecha, string FormaPago,
    string Usuario, decimal Importe, string Comentario)
{
    /// <summary>true = egreso (importe negativo); false = ingreso/depósito.</summary>
    public bool EsEgreso => Importe < 0;
}

/// <summary>Fila del control de días sin cargar (trafico_vehiculo_combustible). Dias = días desde
/// la última carga hasta hoy. Odometro = odómetro de esa última carga.</summary>
public record ControlCargaRow(int Interno, string Dominio, DateOnly UltCarga, int Dias, long Odometro)
{
    public string InternoNT => Interno == 0 ? "—" : "NT" + Interno.ToString("D4");
}

/// <summary>Fila del consumo mensual (litros por mes × unidad × estación × tipo). Mes = "yyyy-MM".
/// Importe viene 0 con tarjeta prepaga → la métrica del informe es Litros, no Costo.</summary>
public record ConsumoMensualRow(
    string Mes, string Dominio, int Interno, string Estacion, string TipoCarga,
    int Cargas, decimal Litros, decimal Importe)
{
    public string InternoNT => Interno == 0 ? "—" : "NT" + Interno.ToString("D4");
    /// <summary>Mes como "MM/yyyy" para mostrar.</summary>
    public string MesTexto => Mes.Length == 7 ? $"{Mes.Substring(5, 2)}/{Mes.Substring(0, 4)}" : Mes;
}

/// <summary>Fila de Artículos por rubro de consumo (estacion_rubro_articulo.scx). RubroId = FK a
/// estacion_rubro; Rubro = su nombre. Para rubro 1 (combustible) son los tipos de combustible.
/// Sin f_delete → baja física.</summary>
public record ArticuloRubroRow(int Id, int RubroId, string Rubro, string Nombre);
