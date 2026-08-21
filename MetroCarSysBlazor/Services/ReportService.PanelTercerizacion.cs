using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MetroCarSysBlazor.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  Panel de Tercerización — Nortur vs Fleteros (informe NUEVO, no existe en FoxPro).
//
//  Contesta cuánto de la operación se presta con flota propia y cuánto se da a
//  terceros, abierto por cliente, servicio, tipo de unidad y mes; y dónde hubo
//  unidades propias sin trabajar el mismo día en que se contrató afuera.
//
//  ── EL MODELO DE DATOS, VERIFICADO (11/08/2026) ────────────────────────────
//
//  🔴 `viaje.fletero` ES LA FUENTE DE VERDAD de quién prestó el servicio, y
//  `vehiculo.fletero` (el TITULAR de la unidad) coincide con él al 100%.
//
//  🔴 `vehiculo.uso` (PROPIO / CONTRATADO) **NO** dice si la unidad es de NORTUR:
//  describe la relación de la unidad con SU PROPIO TITULAR. Hay 11 unidades
//  "PROPIO" cuyo titular es VANSQ, 14 de MVTRAVEL, etc. Clasificar la flota por
//  `uso` da 5.636 viajes de terceros contados como propios (58% de lo tercerizado
//  de 2026). Esto REFINA la regla de la memoria `contratado-no-es-interno-1000`:
//  el campo bueno es `fletero`, no `uso`.
//
//  🔴 `viaje.fletero` VACÍO no es "no tercerizado": es un viaje que nunca tuvo
//  unidad. Medido en 2026: los 7.533 vacíos son exactamente 2.274 CANCELADOS +
//  5.259 SIN ASIGNAR. El campo se llena al asignar. Por eso el universo del
//  informe son los viajes CON fletero, y los sin asignar se muestran aparte como
//  demanda no cubierta — nunca sumados al denominador del % tercerizado.
//
//  ⚠ "Unidad propia ociosa" ≠ "unidad disponible". Una unidad sin viajes ese día
//  puede estar en taller, sin chofer o de franco; y un feriado la flota entera
//  figura ociosa (el día con más ociosas de 2026 es el 1 de enero). Por eso el
//  cruce de oportunidad se abre POR TIPO DE VEHÍCULO —tercerizar una VAN no se
//  cubre con un BUS parado— y la pantalla declara la salvedad.
// ─────────────────────────────────────────────────────────────────────────────

public partial class ReportService
{
    /// <summary>El titular que representa a la flota propia en `viaje.fletero` / `vehiculo.fletero`.</summary>
    public const string TitularPropio = "NORTUR";

    /// <summary>Las tres dimensiones por las que se puede abrir el Panel de Tercerización.</summary>
    public const string DimTercCliente = "Cliente";
    public const string DimTercServicio = "Servicio";
    public const string DimTercTipo = "Tipo de unidad";

    public static readonly IReadOnlyList<string> DimensionesTercerizacion =
        new[] { DimTercCliente, DimTercServicio, DimTercTipo };

    /// <summary>
    /// Una fila por prestador (NORTUR + cada fletero) con su volumen del período. Son ~12
    /// filas: los KPIs, el ranking y el cross-filter se resuelven en memoria.
    /// </summary>
    public async Task<List<TercerizacionPrestadorRow>> GetTercerizacionPrestadoresAsync(
        DateOnly desde, DateOnly hasta)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var key = $"terc-prest|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            // La razón social sale del catálogo `fletero` (match 100% por id_contrat, verificado):
            // el operativo ve "VANSQ" pero el dueño quiere leer "VANS QUILMES SRL".
            return await db.Database.SqlQuery<TercerizacionPrestadorRow>($"""
                SELECT
                    RTRIM(v.fletero)                                          AS Prestador,
                    ISNULL(MAX(RTRIM(f.nombre)), RTRIM(v.fletero))            AS RazonSocial,
                    COUNT(*)                                                  AS Viajes,
                    SUM(CAST(ISNULL(v.pax, 0) AS int))                        AS Pax,
                    SUM(CAST(ISNULL(v.km, 0) AS bigint))                      AS Km,
                    COUNT(DISTINCT NULLIF(LTRIM(RTRIM(v.id_vehicu2)), ''))    AS Unidades,
                    COUNT(DISTINCT NULLIF(LTRIM(RTRIM(v.nombre_cli)), ''))    AS Clientes,
                    COUNT(DISTINCT NULLIF(LTRIM(RTRIM(v.id_servici)), ''))    AS Servicios,
                    COUNT(DISTINCT v.f_reserva)                               AS Dias
                FROM viaje v
                LEFT JOIN fletero f ON RTRIM(f.id_contrat) = RTRIM(v.fletero) AND f._deleted = 0
                WHERE v._deleted = 0
                  AND v.f_reserva BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                  AND v.estado_via <> 'CANCELADO'
                  AND NULLIF(LTRIM(RTRIM(v.fletero)), '') IS NOT NULL
                GROUP BY RTRIM(v.fletero)
                ORDER BY COUNT(*) DESC
                """).ToListAsync();
        }) ?? new();
    }

    /// <summary>
    /// Volumen mes a mes de propio vs tercerizado, más los viajes que quedaron SIN ASIGNAR
    /// (demanda no cubierta: no entran en el % tercerizado, se muestran al lado).
    /// </summary>
    public async Task<List<TercerizacionMesRow>> GetTercerizacionEvolucionAsync(
        DateOnly desde, DateOnly hasta)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var key = $"terc-evol|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Database.SqlQuery<TercerizacionMesRow>($"""
                SELECT
                    CONVERT(varchar(7), v.f_reserva, 120)                                 AS Mes,
                    SUM(CASE WHEN RTRIM(v.fletero) = {TitularPropio} THEN 1 ELSE 0 END) AS Propios,
                    SUM(CASE WHEN NULLIF(LTRIM(RTRIM(v.fletero)), '') IS NOT NULL
                              AND RTRIM(v.fletero) <> {TitularPropio} THEN 1 ELSE 0 END) AS Tercerizados,
                    SUM(CASE WHEN v.estado_via = 'SIN ASIGNAR' THEN 1 ELSE 0 END)         AS SinAsignar
                FROM viaje v
                WHERE v._deleted = 0
                  AND v.f_reserva BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                  AND v.estado_via <> 'CANCELADO'
                GROUP BY CONVERT(varchar(7), v.f_reserva, 120)
                ORDER BY 1
                """).ToListAsync();
        }) ?? new();
    }

    /// <summary>
    /// El detalle por dimensión (cliente / servicio / tipo de unidad) CRUZADO con el prestador.
    /// Una sola consulta para las tres dimensiones: la página corta en memoria y el cross-filter
    /// por fletero no vuelve a la base.
    /// </summary>
    public async Task<List<TercerizacionDetalleRow>> GetTercerizacionDetalleAsync(
        DateOnly desde, DateOnly hasta)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var key = $"terc-det|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            // UNION ALL de las tres dimensiones: SQL Server 2012 no tiene GROUPING SETS cómodos
            // acá y así cada bloque queda legible. El volumen es chico (~3.000 filas).
            return await db.Database.SqlQuery<TercerizacionDetalleRow>($"""
                SELECT {DimTercCliente} AS Dimension,
                       CASE WHEN NULLIF(LTRIM(RTRIM(v.nombre_cli)), '') IS NULL
                            THEN 'Sin cliente' ELSE RTRIM(v.nombre_cli) END AS Categoria,
                       RTRIM(v.fletero) AS Prestador,
                       COUNT(*) AS Viajes, SUM(CAST(ISNULL(v.pax,0) AS int)) AS Pax
                FROM viaje v
                WHERE v._deleted = 0 AND v.estado_via <> 'CANCELADO'
                  AND v.f_reserva BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                  AND NULLIF(LTRIM(RTRIM(v.fletero)), '') IS NOT NULL
                GROUP BY CASE WHEN NULLIF(LTRIM(RTRIM(v.nombre_cli)), '') IS NULL
                              THEN 'Sin cliente' ELSE RTRIM(v.nombre_cli) END, RTRIM(v.fletero)

                UNION ALL

                SELECT {DimTercServicio},
                       CASE WHEN NULLIF(LTRIM(RTRIM(v.id_servici)), '') IS NULL
                            THEN 'Sin servicio' ELSE RTRIM(v.id_servici) END,
                       RTRIM(v.fletero), COUNT(*), SUM(CAST(ISNULL(v.pax,0) AS int))
                FROM viaje v
                WHERE v._deleted = 0 AND v.estado_via <> 'CANCELADO'
                  AND v.f_reserva BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                  AND NULLIF(LTRIM(RTRIM(v.fletero)), '') IS NOT NULL
                GROUP BY CASE WHEN NULLIF(LTRIM(RTRIM(v.id_servici)), '') IS NULL
                              THEN 'Sin servicio' ELSE RTRIM(v.id_servici) END, RTRIM(v.fletero)

                UNION ALL

                SELECT {DimTercTipo},
                       CASE WHEN NULLIF(LTRIM(RTRIM(v.id_vehicul)), '') IS NULL
                            THEN 'Sin tipo' ELSE RTRIM(v.id_vehicul) END,
                       RTRIM(v.fletero), COUNT(*), SUM(CAST(ISNULL(v.pax,0) AS int))
                FROM viaje v
                WHERE v._deleted = 0 AND v.estado_via <> 'CANCELADO'
                  AND v.f_reserva BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                  AND NULLIF(LTRIM(RTRIM(v.fletero)), '') IS NOT NULL
                GROUP BY CASE WHEN NULLIF(LTRIM(RTRIM(v.id_vehicul)), '') IS NULL
                              THEN 'Sin tipo' ELSE RTRIM(v.id_vehicul) END, RTRIM(v.fletero)
                """).ToListAsync();
        }) ?? new();
    }

    /// <summary>
    /// El cruce de oportunidad, POR TIPO DE VEHÍCULO: cuántos viajes de ese tipo se tercerizaron
    /// y cuántos "días-unidad" de flota propia de ese mismo tipo no registraron viaje.
    /// </summary>
    /// <remarks>
    /// 🔴 Un día-unidad ocioso NO es capacidad disponible: la unidad puede estar en taller, sin
    /// chofer o de franco, y en un feriado la flota entera figura ociosa. El número sirve para
    /// PREGUNTAR ("¿por qué se contrató VAN si había VANs sin salir?"), no para concluir. La
    /// pantalla lo dice con todas las letras.
    /// El tipo del viaje sale de `viaje.id_vehicul` (el tipo PEDIDO) y el de la unidad de
    /// `vehiculo.id_vehicu2` — están cruzados entre las dos tablas (ver Panel de Flota).
    /// </remarks>
    public async Task<List<TercerizacionOportunidadRow>> GetTercerizacionOportunidadAsync(
        DateOnly desde, DateOnly hasta)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var key = $"terc-oport|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Database.SqlQuery<TercerizacionOportunidadRow>($"""
                WITH dias AS (
                    SELECT DISTINCT f_reserva AS F
                    FROM viaje
                    WHERE _deleted = 0 AND estado_via <> 'CANCELADO'
                      AND f_reserva BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                ),
                propias AS (
                    SELECT RTRIM(ISNULL(id_vehicu2, '')) AS Tipo, RTRIM(dominio) AS Dom
                    FROM vehiculo
                    WHERE _deleted = 0 AND activo = 1 AND f_delete IS NULL
                      AND RTRIM(ISNULL(fletero, '')) = {TitularPropio}
                ),
                trabajo AS (
                    SELECT DISTINCT v.f_reserva AS F, RTRIM(v.id_vehicu2) AS Dom
                    FROM viaje v
                    WHERE v._deleted = 0 AND v.estado_via <> 'CANCELADO'
                      AND v.f_reserva BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                      AND RTRIM(v.fletero) = {TitularPropio}
                ),
                ocio AS (
                    SELECT p.Tipo, COUNT(*) AS DiasUnidadOciosos
                    FROM dias di
                    CROSS JOIN propias p
                    LEFT JOIN trabajo t ON t.F = di.F AND t.Dom = p.Dom
                    WHERE t.Dom IS NULL
                    GROUP BY p.Tipo
                ),
                viajes AS (
                    SELECT CASE WHEN NULLIF(LTRIM(RTRIM(v.id_vehicul)), '') IS NULL
                                THEN 'Sin tipo' ELSE RTRIM(v.id_vehicul) END AS Tipo,
                           SUM(CASE WHEN RTRIM(v.fletero) = {TitularPropio} THEN 1 ELSE 0 END) AS Propios,
                           SUM(CASE WHEN NULLIF(LTRIM(RTRIM(v.fletero)), '') IS NOT NULL
                                     AND RTRIM(v.fletero) <> {TitularPropio} THEN 1 ELSE 0 END) AS Tercerizados,
                           SUM(CASE WHEN v.estado_via = 'SIN ASIGNAR' THEN 1 ELSE 0 END) AS SinAsignar
                    FROM viaje v
                    WHERE v._deleted = 0 AND v.estado_via <> 'CANCELADO'
                      AND v.f_reserva BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                    GROUP BY CASE WHEN NULLIF(LTRIM(RTRIM(v.id_vehicul)), '') IS NULL
                                  THEN 'Sin tipo' ELSE RTRIM(v.id_vehicul) END
                )
                SELECT
                    vi.Tipo                                    AS Tipo,
                    vi.Propios                                 AS Propios,
                    vi.Tercerizados                            AS Tercerizados,
                    vi.SinAsignar                              AS SinAsignar,
                    ISNULL(uni.Unidades, 0)                    AS UnidadesPropias,
                    ISNULL(oc.DiasUnidadOciosos, 0)            AS DiasUnidadOciosos
                FROM viajes vi
                LEFT JOIN ocio oc ON oc.Tipo = vi.Tipo
                LEFT JOIN (SELECT Tipo, COUNT(*) AS Unidades FROM propias GROUP BY Tipo) uni
                       ON uni.Tipo = vi.Tipo
                WHERE vi.Propios + vi.Tercerizados + vi.SinAsignar > 0
                ORDER BY vi.Tercerizados DESC
                """).ToListAsync();
        }) ?? new();
    }
}

// ── DTOs del Panel de Tercerización ─────────────────────────────────────────

/// <summary>Volumen de un prestador (NORTUR o un fletero) en el período.</summary>
public record TercerizacionPrestadorRow(
    string Prestador,
    string RazonSocial,
    int Viajes,
    int Pax,
    long Km,
    int Unidades,
    int Clientes,
    int Servicios,
    int Dias)
{
    /// <summary>La flota propia. Todo lo demás es tercerizado.</summary>
    public bool EsPropio => string.Equals(Prestador, ReportService.TitularPropio, StringComparison.OrdinalIgnoreCase);

    /// <summary>Nombre para mostrar: la razón social si el catálogo la tiene, si no el código.</summary>
    public string Nombre => string.IsNullOrWhiteSpace(RazonSocial) ? Prestador : RazonSocial;

    public double ViajesPorUnidad => Unidades > 0 ? (double)Viajes / Unidades : 0;
    public double PaxPorViaje => Viajes > 0 ? (double)Pax / Viajes : 0;
}

/// <summary>Un mes de la evolución propio / tercerizado / sin cubrir.</summary>
public record TercerizacionMesRow(string Mes, int Propios, int Tercerizados, int SinAsignar)
{
    public int Asignados => Propios + Tercerizados;

    /// <summary>% tercerizado sobre lo EFECTIVAMENTE asignado (los sin asignar no van al denominador).</summary>
    public double PctTercerizado => Asignados > 0 ? Tercerizados * 100.0 / Asignados : 0;

    /// <summary>"2026-03" → "03/2026", para el eje del gráfico.</summary>
    public string Etiqueta => Mes.Length == 7 ? $"{Mes[5..]}/{Mes[..4]}" : Mes;
}

/// <summary>Viajes de un prestador dentro de una categoría de una dimensión.</summary>
public record TercerizacionDetalleRow(
    string Dimension,
    string Categoria,
    string Prestador,
    int Viajes,
    int Pax)
{
    public bool EsPropio => string.Equals(Prestador, ReportService.TitularPropio, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Cruce oferta propia ↔ tercerización para un tipo de vehículo.</summary>
public record TercerizacionOportunidadRow(
    string Tipo,
    int Propios,
    int Tercerizados,
    int SinAsignar,
    int UnidadesPropias,
    int DiasUnidadOciosos)
{
    public int Asignados => Propios + Tercerizados;
    public double PctTercerizado => Asignados > 0 ? Tercerizados * 100.0 / Asignados : 0;

    /// <summary>
    /// ¿Hay algo para preguntar acá? Se tercerizó de un tipo del que además hay flota propia
    /// parada. NO es una conclusión: ver la salvedad del service (taller, chofer, feriados).
    /// </summary>
    public bool HayPregunta => Tercerizados > 0 && UnidadesPropias > 0 && DiasUnidadOciosos > 0;
}
