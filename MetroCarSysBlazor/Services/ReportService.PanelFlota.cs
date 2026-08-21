using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MetroCarSysBlazor.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  Panel de Flota (informe NUEVO — no existe en el Metrocar/FoxPro).
//
//  Contesta tres preguntas de negocio que hoy nadie puede responder sin abrir la
//  tabla `vehiculo` a mano:
//    1. ¿QUÉ TENGO?      cuántas unidades, de qué tipo, propias vs contratadas,
//                        cuántas butacas, de qué antigüedad, de qué titular.
//    2. ¿QUÉ NO TENGO?   demanda del período por tipo de vehículo pedido contra
//                        las unidades disponibles, medida por los viajes que
//                        quedaron SIN ASIGNAR.
//    3. ¿QUÉ NO USO?     unidades activas que no hicieron un solo viaje.
//
//  Todo el tablero sale de DOS consultas (una fila por vehículo + una fila por
//  tipo pedido). Con ~412 vehículos, el resto —cortes por dimensión, KPIs,
//  cross-filter, drill-down— se resuelve en memoria, sin volver a SQL.
//
//  ⚠ TRAMPA DE COLUMNAS (verificada 10/08/2026): en la tabla `vehiculo` es al
//  REVÉS que en `viaje`. Acá `id_vehicul` = DOMINIO y `id_vehicu2` = TIPO; en
//  `viaje`, `id_vehicul` = TIPO y `id_vehicu2` = DOMINIO. Cruzar la flota con los
//  viajes va SIEMPRE por `vehiculo.dominio = viaje.id_vehicu2`.
//
//  ⚠ EL PLANTEL ES "A HOY", EL PERÍODO SOLO MIDE ACTIVIDAD. `vehiculo` no guarda
//  historia (no hay "cuántos buses tenía en marzo"): las altas/bajas solo tienen
//  f_compra/f_venta/f_delete, cargadas en menos de la mitad de las filas. Por eso
//  el conteo de unidades es una foto del momento y el Desde–Hasta afecta
//  únicamente viajes, km, días trabajados y sin asignar. La pantalla lo aclara.
// ─────────────────────────────────────────────────────────────────────────────

public partial class ReportService
{
    /// <summary>Techo de km por unidad y por mes que se considera físicamente posible.</summary>
    /// <remarks>
    /// `vehiculo_km` se carga a mano y tiene errores de tipeo groseros: hay una unidad con
    /// km_inicio 200.000.000 y km_fin 3.000.000.000 en un solo mes (2.800 millones de km).
    /// Un mes cuyo recorrido supere este techo se DESCARTA de la suma y se cuenta aparte
    /// (MesesOdometroRaro) para mostrarlo como dato de calidad en vez de esconderlo.
    /// 30.000 km/mes ya es holgado: la unidad más intensa de la flota ronda los 4.600.
    /// </remarks>
    public const int KmMaximoPorMes = 30_000;

    /// <summary>Las cuatro dimensiones por las que se puede abrir el Panel de Flota.</summary>
    public const string DimFlotaTipo = "Tipo";
    public const string DimFlotaTitular = "Titular";
    public const string DimFlotaAntiguedad = "Antiguedad";
    public const string DimFlotaCapacidad = "Capacidad";

    public static readonly IReadOnlyList<string> DimensionesFlota =
        new[] { DimFlotaTipo, DimFlotaTitular, DimFlotaAntiguedad, DimFlotaCapacidad };

    /// <summary>
    /// Una fila por vehículo de la flota (todas las unidades, activas y de baja), con la
    /// actividad del período pegada. El filtro por universo (propias / contratadas / de baja)
    /// se aplica en memoria en la página: son ~400 filas y así los switches y el cross-filter
    /// responden sin volver a la base.
    /// </summary>
    public async Task<List<FlotaUnidadRow>> GetFlotaUnidadesAsync(DateOnly desde, DateOnly hasta)
    {
        var d = ClampFlota(desde);
        var h = ClampFlota(hasta);
        var key = $"flota-unidades|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // Meses 'AAAAMM' que toca el rango, para sumar el odómetro mes a mes (y no por
            // MIN/MAX del rango: un solo mes mal cargado envenenaría todo el período).
            var meses = new List<string>();
            for (var m = new DateOnly(d.Year, d.Month, 1); m <= h; m = m.AddMonths(1))
                meses.Add($"{m.Year:0000}{m.Month:00}");
            var mesesIn = string.Join(",", meses.Select(m => $"'{m}'"));

            // Los dos LEFT JOIN son subconsultas AGRUPADAS, no OUTER APPLY por vehículo:
            // con 412 unidades, el APPLY dispararía 412 recorridos sobre las 512k filas de
            // `viaje`. Así son dos agregados y un merge (medido: 0,5 s).
            var sql = $"""
                SELECT
                    RTRIM(ISNULL(v.dominio, ''))              AS Dominio,
                    CAST(ISNULL(v.interno, 0) AS int)         AS Interno,
                    RTRIM(ISNULL(v.id_vehicu2, ''))           AS Tipo,
                    RTRIM(ISNULL(v.marca_y_mo, ''))           AS Marca,
                    CAST(ISNULL(v.modelo, 0) AS int)          AS Modelo,
                    CAST(ISNULL(v.pax, 0) AS int)             AS Pax,
                    RTRIM(ISNULL(v.uso, ''))                  AS Uso,
                    RTRIM(ISNULL(v.fletero, ''))              AS Titular,
                    RTRIM(ISNULL(v.estado, ''))               AS Estado,
                    CAST(ISNULL(v.activo, 0) AS int)          AS Activo,
                    v.f_delete                                AS FBaja,
                    ISNULL(a.Viajes, 0)                       AS Viajes,
                    ISNULL(a.PaxTrans, 0)                     AS PaxTrans,
                    ISNULL(a.Dias, 0)                         AS Dias,
                    ISNULL(k.Km, 0)                           AS Km,
                    ISNULL(k.MesesOk, 0)                      AS MesesOk,
                    ISNULL(k.MesesRaros, 0)                   AS MesesRaros
                FROM vehiculo v
                LEFT JOIN (
                    SELECT RTRIM(ISNULL(t.id_vehicu2, ''))        AS Dom,
                           COUNT(*)                               AS Viajes,
                           SUM(CAST(ISNULL(t.pax, 0) AS int))     AS PaxTrans,
                           COUNT(DISTINCT t.f_reserva)            AS Dias
                    FROM viaje t
                    WHERE t._deleted = 0
                      AND t.estado_via <> 'CANCELADO'
                      AND t.f_reserva BETWEEN '{d:yyyyMMdd}' AND '{h:yyyyMMdd}'
                      AND NULLIF(LTRIM(RTRIM(t.id_vehicu2)), '') IS NOT NULL
                    GROUP BY RTRIM(ISNULL(t.id_vehicu2, ''))
                ) a ON a.Dom = RTRIM(ISNULL(v.dominio, ''))
                LEFT JOIN (
                    SELECT RTRIM(ISNULL(vk.dominio, '')) AS Dom,
                           SUM(CASE WHEN vk.km_fin > vk.km_inicio
                                     AND vk.km_fin - vk.km_inicio <= {KmMaximoPorMes}
                                    THEN vk.km_fin - vk.km_inicio ELSE 0 END) AS Km,
                           SUM(CASE WHEN vk.km_fin > vk.km_inicio
                                     AND vk.km_fin - vk.km_inicio <= {KmMaximoPorMes}
                                    THEN 1 ELSE 0 END)                        AS MesesOk,
                           SUM(CASE WHEN vk.km_fin > vk.km_inicio
                                     AND vk.km_fin - vk.km_inicio > {KmMaximoPorMes}
                                    THEN 1 ELSE 0 END)                        AS MesesRaros
                    FROM vehiculo_km vk
                    WHERE vk._deleted = 0 AND vk.ano_y_mes IN ({mesesIn})
                    GROUP BY RTRIM(ISNULL(vk.dominio, ''))
                ) k ON k.Dom = RTRIM(ISNULL(v.dominio, ''))
                WHERE v._deleted = 0
                ORDER BY v.interno, v.dominio
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = new List<FlotaUnidadRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new FlotaUnidadRow(
                    Dominio: reader.GetString(0).Trim(),
                    Interno: reader.GetInt32(1),
                    Tipo: reader.GetString(2).Trim(),
                    Marca: reader.GetString(3).Trim(),
                    Modelo: reader.GetInt32(4),
                    Pax: reader.GetInt32(5),
                    Uso: reader.GetString(6).Trim(),
                    Titular: reader.GetString(7).Trim(),
                    Estado: reader.GetString(8).Trim(),
                    Activo: reader.GetInt32(9) == 1,
                    FBaja: reader.IsDBNull(10) ? null : DateOnly.FromDateTime(reader.GetDateTime(10)),
                    Viajes: reader.GetInt32(11),
                    PaxTransportados: reader.GetInt32(12),
                    DiasTrabajados: reader.GetInt32(13),
                    Km: Convert.ToInt64(reader.GetValue(14)),
                    MesesOdometroOk: reader.GetInt32(15),
                    MesesOdometroRaro: reader.GetInt32(16)));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Demanda del período por TIPO DE VEHÍCULO PEDIDO (`viaje.id_vehicul`): cuántos viajes se
    /// pidieron de cada tipo y cuántos quedaron SIN ASIGNAR. Los sin asignar son la medida del
    /// faltante — servicios que entraron y no encontraron unidad.
    /// </summary>
    public async Task<List<FlotaDemandaRow>> GetFlotaDemandaAsync(DateOnly desde, DateOnly hasta)
    {
        var d = ClampFlota(desde);
        var h = ClampFlota(hasta);
        var key = $"flota-demanda|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Database.SqlQuery<FlotaDemandaRow>($"""
                SELECT
                    RTRIM(ISNULL(v.id_vehicul, ''))                                     AS Tipo,
                    COUNT(*)                                                            AS Viajes,
                    SUM(CASE WHEN v.estado_via = 'SIN ASIGNAR' THEN 1 ELSE 0 END)       AS SinAsignar,
                    SUM(CASE WHEN v.estado_via = 'CANCELADO'   THEN 1 ELSE 0 END)       AS Cancelados,
                    SUM(CAST(ISNULL(v.pax, 0) AS int))                                  AS Pax,
                    COUNT(DISTINCT NULLIF(LTRIM(RTRIM(v.id_vehicu2)), ''))              AS UnidadesUsadas
                FROM viaje v
                WHERE v._deleted = 0
                  AND v.f_reserva BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                  AND NULLIF(LTRIM(RTRIM(v.id_vehicul)), '') IS NOT NULL
                GROUP BY RTRIM(ISNULL(v.id_vehicul, ''))
                ORDER BY COUNT(*) DESC
                """).ToListAsync();
        }) ?? new();
    }

    /// <summary>
    /// Acota una fecha al rango con datos reales. `viaje` tiene fechas corruptas (año 309, 2252)
    /// y el informe se apoya en f_reserva, así que se usa la misma ventana del resto del sistema.
    /// </summary>
    private static DateOnly ClampFlota(DateOnly f) =>
        f < FechaMinValida ? FechaMinValida : f > FechaMaxValida ? FechaMaxValida : f;
}

// ── DTOs del Panel de Flota ─────────────────────────────────────────────────

/// <summary>
/// Una unidad de la flota con su actividad en el período. Las propiedades calculadas
/// (universo, antigüedad, tramos) viven acá para que la pantalla y el Excel corten
/// exactamente igual — un solo lugar donde se define qué es "de baja" o "0-3 años".
/// </summary>
public record FlotaUnidadRow(
    string Dominio,
    int Interno,
    string Tipo,
    string Marca,
    int Modelo,
    int Pax,
    string Uso,
    string Titular,
    string Estado,
    bool Activo,
    DateOnly? FBaja,
    int Viajes,
    int PaxTransportados,
    int DiasTrabajados,
    long Km,
    int MesesOdometroOk,
    int MesesOdometroRaro)
{
    /// <summary>
    /// De baja = vendida o desafectada. Doble condición, igual que la lista de Vehículos del
    /// FoxPro: `!activo OR f_delete` (distinto de `chofer`, que solo mira f_delete).
    /// </summary>
    public bool EsBaja => !Activo || FBaja is not null;

    public bool EsPropia => Uso == "PROPIO";
    public bool EsContratada => Uso == "CONTRATADO";

    /// <summary>Activa y sin un solo viaje en el período: la unidad que no trabajó.</summary>
    public bool EsOciosa => !EsBaja && Viajes == 0;

    /// <summary>El interno con el formato de todo el sistema (NT0220). Vacío si no tiene.</summary>
    public string InternoNT => Interno > 0 ? "NT" + Interno.ToString("D4") : "—";

    /// <summary>
    /// Años de antigüedad según el año de modelo, o null si el dato no sirve. `modelo` trae
    /// basura de carga (valores 19, 20 — años de dos dígitos) y ceros: por debajo de 1950 se
    /// considera sin dato en vez de inventar una antigüedad de 2.000 años.
    /// </summary>
    public int? Antiguedad =>
        Modelo >= 1950 && Modelo <= DateTime.Today.Year + 1 ? DateTime.Today.Year - Modelo : null;

    /// <summary>Tramo de antigüedad para el corte por dimensión.</summary>
    public string TramoAntiguedad => Antiguedad switch
    {
        null => "Sin dato",
        <= 3 => "0 a 3 años",
        <= 7 => "4 a 7 años",
        <= 12 => "8 a 12 años",
        _ => "13 años o más",
    };

    /// <summary>Tramo de capacidad (butacas) para el corte por dimensión.</summary>
    public string TramoCapacidad => Pax switch
    {
        <= 0 => "Sin dato",
        <= 5 => "1 a 5 butacas",
        <= 19 => "6 a 19 butacas",
        <= 29 => "20 a 29 butacas",
        <= 45 => "30 a 45 butacas",
        _ => "46 butacas o más",
    };

    /// <summary>El km del período es confiable solo si algún mes pasó el control de carga.</summary>
    public bool TieneKm => MesesOdometroOk > 0;

    /// <summary>Valor de esta unidad en la dimensión con la que está abierto el informe.</summary>
    public string Categoria(string dimension) => dimension switch
    {
        ReportService.DimFlotaTitular => string.IsNullOrWhiteSpace(Titular) ? "Sin titular" : Titular,
        ReportService.DimFlotaAntiguedad => TramoAntiguedad,
        ReportService.DimFlotaCapacidad => TramoCapacidad,
        _ => string.IsNullOrWhiteSpace(Tipo) ? "Sin tipo" : Tipo,
    };
}

/// <summary>
/// Demanda del período para un tipo de vehículo pedido. `UnidadesUsadas` son los dominios
/// distintos que efectivamente cubrieron esos viajes (puede ser menor que la flota de ese tipo).
/// </summary>
public record FlotaDemandaRow(
    string Tipo,
    int Viajes,
    int SinAsignar,
    int Cancelados,
    int Pax,
    int UnidadesUsadas);

/// <summary>
/// Una fila del resumen del Panel de Flota (una categoría de la dimensión abierta). Lo arma la
/// página en memoria; vive acá para que la pantalla y el Excel compartan exactamente la misma
/// forma y no haya dos versiones del mismo número.
/// </summary>
public record FlotaResumenCatRow(
    string Cat,
    int Unidades,
    int Propias,
    int Contratadas,
    int Bajas,
    int Butacas,
    double? AntiguedadProm,
    int Ociosas,
    int Viajes,
    long Km,
    int UnidadesConKm)
{
    public string KmTitulo => UnidadesConKm > 0
        ? $"{Km:#,0} km leídos del odómetro de {UnidadesConKm} unidad(es)"
        : "Ninguna unidad de esta categoría tiene lecturas de odómetro confiables en el período";
}

/// <summary>
/// Cruce oferta ↔ demanda de un tipo de vehículo: cuántas unidades activas hay, cuántas
/// trabajaron, cuántos viajes se pidieron y cuántos quedaron sin asignar.
/// </summary>
public record FlotaOfertaDemandaRow(
    string Tipo,
    int Unidades,
    int Trabajaron,
    int Pedidos,
    int SinAsignar)
{
    /// <summary>Qué porcentaje de lo pedido de este tipo quedó sin cubrir.</summary>
    public double PctSinCubrir => Pedidos > 0 ? SinAsignar * 100.0 / Pedidos : 0;

    /// <summary>Carga media: viajes pedidos por cada unidad activa del tipo.</summary>
    public double ViajesPorUnidad => Unidades > 0 ? (double)Pedidos / Unidades : 0;
}
