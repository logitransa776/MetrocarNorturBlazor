using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MetroCarSysBlazor.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  Panel de Clientes (informe NUEVO — no existe en el Metrocar/FoxPro).
//
//  Cruza las tres caras del cliente, que hoy viven en pantallas separadas:
//    · PADRÓN     → quién está cargado (`cliente`), con qué tarifa y qué datos.
//    · ACTIVIDAD  → cuántos viajes y pasajeros movió (`viaje`).
//    · PLATA      → cuánto facturó (`liquidacion` + `liquidacion_detalle`).
//
//  ── DECISIONES DE CÁLCULO (acordadas con el usuario el 10/08/2026) ──────────
//
//  1. DEVENGADO AL MES DEL VIAJE. La plata se imputa al mes en que se prestó el
//     servicio, no al de la liquidación: se baja por `liquidacion_detalle.id_viaje`
//     hasta `viaje.f_reserva`. Así viajes y facturación comparten eje y el
//     "facturado por viaje" es exacto. El join cierra al 100% (0 huérfanos).
//
//  2. LA PLATA SE CALCULA DESDE EL DETALLE, NO DESDE LA CABECERA. Fórmula por
//     línea: `importe + incremento − descuento`, con la MONEDA DE LA LÍNEA (una
//     misma liquidación mezcla SERVICIO en USS con ADICIONAL en PESOS) convertida
//     con el `t_cambio` de su cabecera. Verificado contra `liquidacion.total`:
//     reconstruye el 99,97% de 2026 (1 desajuste en 545 liquidaciones).
//     ⚠ Y ADEMÁS ES MÁS CONFIABLE que la cabecera: `liquidacion.total` tiene
//     cargas corruptas del FoxPro (la liquidación 2364 de feb-2024 declara
//     $22.200.310.104 cuando su propio detalle da $6,9 M). Por eso el informe NO
//     usa `total`.
//     🔴 El `incremento` de la línea NO está dentro de `importe`: omitirlo
//     subestima ~9% la facturación de AEROLINEAS, que es el 57% de la plata.
//
//  3. IMPORTES EN PESOS CORRIENTES. `moneda_cotizacion` está congelada en 2019
//     (dólar a $38) → no sirve para dolarizar. Lo que sí hay es el `t_cambio` de
//     cada liquidación en dólares. Se reportan por separado los pesos facturados
//     en pesos y los dólares facturados en USD, sin inventar conversiones: el
//     total en pesos es la suma real que cobró la empresa. Comparar períodos de
//     años distintos en pesos corrientes es engañoso por inflación — la pantalla
//     lo avisa.
//
//  4. NO HAY DATOS DE COBRANZA. `liquidacion.f_pago` está vacío en el 100% de las
//     filas desde 2024 → no se puede medir deuda ni mora. No prometerlo.
// ─────────────────────────────────────────────────────────────────────────────

public partial class ReportService
{
    /// <summary>Las cuatro dimensiones por las que se puede abrir el Panel de Clientes.</summary>
    /// <remarks>
    /// La tabla `cliente` NO tiene un campo "tipo de cliente": `empresa_fc` vale NORTUR en los
    /// 414 registros (constante inútil). Las cuatro dimensiones de acá son las que sí separan
    /// la cartera, y tres de las cuatro se DERIVAN de la operación, no del padrón.
    /// </remarks>
    public const string DimCliLinea = "Linea";
    public const string DimCliMoneda = "Moneda";
    public const string DimCliFiscal = "Fiscal";
    public const string DimCliActividad = "Actividad";

    public static readonly IReadOnlyList<string> DimensionesClientes =
        new[] { DimCliLinea, DimCliMoneda, DimCliFiscal, DimCliActividad };

    /// <summary>Las métricas con las que se puede leer el tablero.</summary>
    public const string MetCliFacturado = "Facturado";
    public const string MetCliViajes = "Viajes";
    public const string MetCliPax = "Pax";

    public static readonly IReadOnlyList<string> MetricasClientes =
        new[] { MetCliFacturado, MetCliViajes, MetCliPax };

    /// <summary>
    /// Tipo de cambio mínimo para creer en `liquidacion.t_cambio`. Hay 18 liquidaciones en
    /// dólares desde 2024 con t_cambio 0 o 1 (error de carga): sus líneas en USS no se pueden
    /// pasar a pesos y se cuentan aparte en vez de sumarse como si fueran pesos.
    /// </summary>
    private const decimal TipoCambioMinimoValido = 10m;

    /// <summary>
    /// Actividad y facturación de cada cliente, mes a mes, en el período. Una fila por
    /// cliente × mes (~330 filas en medio año): el resto del tablero —dimensiones, métricas,
    /// cross-filter, ranking, pivote— se resuelve en memoria sin volver a la base.
    /// </summary>
    public async Task<List<ClienteMesRow>> GetClientesActividadAsync(DateOnly desde, DateOnly hasta)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var key = $"panel-clientes-actividad|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // ── 1) Actividad: viajes, pax, cancelados y mix de línea de negocio ──
            // El LEFT JOIN a `servicio` trae `modo_uso` ('T' turismo / 'P' transporte de
            // personal / 'A' ambos), que es de donde sale la línea de negocio del cliente:
            // el padrón no la tiene. Los viajes con id_servici fuera del catálogo caen en 'A'.
            var acumulado = new Dictionary<(string, string), ClienteMesAcum>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT
                        RTRIM(ISNULL(v.id_cliente, ''))                                     AS Cli,
                        CONVERT(char(7), v.f_reserva, 126)                                  AS Mes,
                        COUNT(*)                                                            AS Viajes,
                        SUM(CASE WHEN RTRIM(v.estado_via) = 'CANCELADO' THEN 1 ELSE 0 END)  AS Cancelados,
                        SUM(CAST(ISNULL(v.pax, 0) AS int))                                  AS Pax,
                        SUM(CASE WHEN ISNULL(RTRIM(s.modo_uso), 'A') = 'T' THEN 1 ELSE 0 END) AS VjTurismo,
                        SUM(CASE WHEN ISNULL(RTRIM(s.modo_uso), 'A') = 'P' THEN 1 ELSE 0 END) AS VjPersonal
                    FROM viaje v
                    LEFT JOIN servicio s ON RTRIM(s.id_servici) = RTRIM(v.id_servici)
                    WHERE v._deleted = 0
                      AND v.f_reserva BETWEEN '{d:yyyyMMdd}' AND '{h:yyyyMMdd}'
                    GROUP BY RTRIM(ISNULL(v.id_cliente, '')), CONVERT(char(7), v.f_reserva, 126)
                    """;
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var clave = (rd.GetString(0).Trim(), rd.GetString(1).Trim());
                    acumulado[clave] = new ClienteMesAcum
                    {
                        Viajes = rd.GetInt32(2),
                        Cancelados = rd.GetInt32(3),
                        Pax = rd.GetInt32(4),
                        VjTurismo = rd.GetInt32(5),
                        VjPersonal = rd.GetInt32(6),
                    };
                }
            }

            // ── 2) Facturación devengada al mes del viaje ──
            // El cliente sale del VIAJE (no de la liquidación) para que las dos consultas
            // agrupen por la misma clave y el merge no invente filas.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT
                        RTRIM(ISNULL(v.id_cliente, ''))    AS Cli,
                        CONVERT(char(7), v.f_reserva, 126) AS Mes,
                        SUM(CASE WHEN RTRIM(d.moneda) = 'USS' THEN 0
                                 ELSE d.importe + ISNULL(d.incremento, 0) - ISNULL(d.descuento, 0) END) AS Pesos,
                        SUM(CASE WHEN RTRIM(d.moneda) = 'USS'
                                 THEN d.importe + ISNULL(d.incremento, 0) - ISNULL(d.descuento, 0) ELSE 0 END) AS Usd,
                        SUM(CASE WHEN RTRIM(d.moneda) = 'USS' AND l.t_cambio > {TipoCambioMinimoValido}
                                 THEN (d.importe + ISNULL(d.incremento, 0) - ISNULL(d.descuento, 0)) * l.t_cambio
                                 ELSE 0 END)                                                            AS UsdEnPesos,
                        SUM(CASE WHEN RTRIM(d.moneda) = 'USS'
                                  AND (l.t_cambio IS NULL OR l.t_cambio <= {TipoCambioMinimoValido})
                                 THEN 1 ELSE 0 END)                                                     AS LineasSinTc,
                        COUNT(DISTINCT d.id_viaje)                                                      AS ViajesFacturados
                    FROM liquidacion_detalle d
                    JOIN liquidacion l ON l.idliquidac = d.idliquidac AND l._deleted = 0
                    JOIN viaje v       ON v.id_viaje   = d.id_viaje   AND v._deleted = 0
                    WHERE d._deleted = 0
                      AND v.f_reserva BETWEEN '{d:yyyyMMdd}' AND '{h:yyyyMMdd}'
                    GROUP BY RTRIM(ISNULL(v.id_cliente, '')), CONVERT(char(7), v.f_reserva, 126)
                    """;
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var clave = (rd.GetString(0).Trim(), rd.GetString(1).Trim());
                    if (!acumulado.TryGetValue(clave, out var acum))
                    {
                        // Facturación sin actividad en el período: no debería pasar (la plata
                        // se devenga al mes del propio viaje), pero si pasa NO se descarta.
                        acum = new ClienteMesAcum();
                        acumulado[clave] = acum;
                    }
                    acum.FactPesos = rd.GetDecimal(2);
                    acum.FactUsd = rd.GetDecimal(3);
                    acum.FactUsdEnPesos = rd.GetDecimal(4);
                    acum.LineasSinTipoCambio = rd.GetInt32(5);
                    acum.ViajesFacturados = rd.GetInt32(6);
                }
            }

            return acumulado
                .Select(kv => new ClienteMesRow(
                    IdCliente: kv.Key.Item1,
                    Mes: kv.Key.Item2,
                    Viajes: kv.Value.Viajes,
                    Cancelados: kv.Value.Cancelados,
                    Pax: kv.Value.Pax,
                    ViajesTurismo: kv.Value.VjTurismo,
                    ViajesPersonal: kv.Value.VjPersonal,
                    FacturadoPesos: kv.Value.FactPesos,
                    FacturadoUsd: kv.Value.FactUsd,
                    FacturadoUsdEnPesos: kv.Value.FactUsdEnPesos,
                    ViajesFacturados: kv.Value.ViajesFacturados,
                    LineasSinTipoCambio: kv.Value.LineasSinTipoCambio))
                .OrderBy(r => r.IdCliente).ThenBy(r => r.Mes)
                .ToList();
        }) ?? new();
    }

    /// <summary>
    /// El padrón completo de clientes (414 filas) con su historia pegada: primera y última
    /// reserva y viajes de toda la vida. De acá salen el segmento de actividad, el tipo fiscal
    /// y la lista de datos faltantes. No depende del período → se cachea por su cuenta.
    /// </summary>
    public async Task<List<ClientePadronRow>> GetClientesPadronAsync()
    {
        return await _cache.GetOrCreateAsync("panel-clientes-padron", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // El tope de la subconsulta es HOY: `viaje` tiene reservas cargadas hasta 2027 (las
            // plantillas se arman con anticipación) y una reserva futura no dice que el cliente
            // esté activo hoy — la "última reserva" del segmento es la última YA OCURRIDA.
            var hoy = DateOnly.FromDateTime(DateTime.Today);
            var sql = $"""
                SELECT
                    RTRIM(ISNULL(c.id_cliente, ''))   AS IdCliente,
                    RTRIM(ISNULL(c.razon_soci, ''))   AS Nombre,
                    RTRIM(ISNULL(c.tipo_resp, ''))    AS TipoResp,
                    -- Subconsulta con TOP 1, NO un LEFT JOIN: `responsable_tipo` tiene el
                    -- código EXT cargado DOS VECES (verificado 10/08/2026), y el join
                    -- duplicaba los 56 clientes del exterior.
                    -- El ISNULL EXTERNO es imprescindible: si el cliente no tiene tipo fiscal
                    -- (16 casos) la subconsulta no devuelve fila y el escalar sale NULL.
                    ISNULL((SELECT TOP 1 RTRIM(ISNULL(rt.nombre, ''))
                              FROM responsable_tipo rt
                             WHERE RTRIM(rt.id_respons) = RTRIM(ISNULL(c.tipo_resp, ''))
                               AND rt._deleted = 0), '') AS TipoRespDesc,
                    RTRIM(ISNULL(c.id_lista_p, ''))   AS ListaPrecio,
                    RTRIM(ISNULL(c.ob_precio, ''))    AS ObPrecio,
                    ISNULL(c.descuento, 0)            AS Descuento,
                    ISNULL(c.incremento, 0)           AS Incremento,
                    RTRIM(ISNULL(c.localidad, ''))    AS Localidad,
                    RTRIM(ISNULL(c.provincia, ''))    AS Provincia,
                    RTRIM(ISNULL(c.ncuit, ''))        AS Cuit,
                    RTRIM(ISNULL(c.email, ''))        AS Email,
                    RTRIM(ISNULL(c.telefono, ''))     AS Telefono,
                    RTRIM(ISNULL(c.contacto1, ''))    AS Contacto,
                    c.f_create                        AS FAlta,
                    c.f_delete                        AS FBaja,
                    -- ¿Tiene tarifario propio cargado? Es la otra mitad de `ob_precio`: con
                    -- 'CLIENTE' el precio sale de `cliente_tarifa`, y si esa tabla no tiene
                    -- filas el cliente se queda SIN forma de valorizar (hoy: 9 casos).
                    CASE WHEN EXISTS (SELECT 1 FROM cliente_tarifa t
                                       WHERE RTRIM(t.id_cliente) = RTRIM(ISNULL(c.id_cliente, ''))
                                         AND t._deleted = 0)
                         THEN 1 ELSE 0 END            AS TieneTarifaPropia,
                    a.Primero                         AS PrimeraReserva,
                    a.Ultimo                          AS UltimaReserva,
                    ISNULL(a.Viajes, 0)               AS ViajesHistoricos
                FROM cliente c
                LEFT JOIN (
                    SELECT RTRIM(ISNULL(id_cliente, '')) AS Cli,
                           MIN(f_reserva)                AS Primero,
                           MAX(f_reserva)                AS Ultimo,
                           COUNT(*)                      AS Viajes
                    FROM viaje
                    WHERE _deleted = 0
                      AND f_reserva BETWEEN '20100101' AND '{hoy:yyyyMMdd}'
                    GROUP BY RTRIM(ISNULL(id_cliente, ''))
                ) a ON a.Cli = RTRIM(ISNULL(c.id_cliente, ''))
                WHERE c._deleted = 0
                ORDER BY c.razon_soci
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = new List<ClientePadronRow>();
            using var rd = await cmd.ExecuteReaderAsync();
            // Lectura defensiva: el padrón es de carga manual desde 2006 y una sola columna
            // NULL inesperada no puede tumbar la pantalla entera (mismo criterio que
            // MapPlanillaRow en la Planilla de Tráfico).
            string S(int i) => rd.IsDBNull(i) ? "" : rd.GetValue(i).ToString()!.Trim();
            decimal D(int i) => rd.IsDBNull(i) ? 0m : Convert.ToDecimal(rd.GetValue(i));
            while (await rd.ReadAsync())
            {
                result.Add(new ClientePadronRow(
                    IdCliente: S(0),
                    Nombre: S(1),
                    TipoResp: S(2),
                    TipoRespDesc: S(3),
                    ListaPrecio: S(4),
                    ObPrecio: S(5),
                    Descuento: D(6),
                    Incremento: D(7),
                    Localidad: S(8),
                    Provincia: S(9),
                    Cuit: S(10),
                    Email: S(11),
                    Telefono: S(12),
                    Contacto: S(13),
                    FAlta: rd.IsDBNull(14) ? null : DateOnly.FromDateTime(rd.GetDateTime(14)),
                    FBaja: rd.IsDBNull(15) ? null : DateOnly.FromDateTime(rd.GetDateTime(15)),
                    TieneTarifaPropia: !rd.IsDBNull(16) && Convert.ToInt32(rd.GetValue(16)) == 1,
                    PrimeraReserva: rd.IsDBNull(17) ? null : DateOnly.FromDateTime(rd.GetDateTime(17)),
                    UltimaReserva: rd.IsDBNull(18) ? null : DateOnly.FromDateTime(rd.GetDateTime(18)),
                    ViajesHistoricos: rd.IsDBNull(19) ? 0 : Convert.ToInt32(rd.GetValue(19))));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// La fecha del último viaje YA OCURRIDO que hay en la base. Es la referencia contra la que
    /// se mide la recencia de cada cliente: usar "hoy" a secas daría a todos por dormidos cuando
    /// se trabaja sobre una réplica que quedó atrasada.
    /// </summary>
    public async Task<DateOnly> GetFechaCorteDatosAsync()
    {
        return await _cache.GetOrCreateAsync("panel-clientes-corte", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtlAbm;
            var hoy = DateOnly.FromDateTime(DateTime.Today);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT MAX(f_reserva) FROM viaje
                WHERE _deleted = 0 AND f_reserva BETWEEN '20100101' AND '{hoy:yyyyMMdd}'
                """;
            var o = await cmd.ExecuteScalarAsync();
            return o is DateTime dt ? DateOnly.FromDateTime(dt) : hoy;
        });
    }

    /// <summary>
    /// Todo lo que hizo un cliente en un período, para la pestaña Actividad de su ficha: serie
    /// mensual, qué servicios y recorridos usa, sus operadores y grupos, y sus últimas
    /// liquidaciones. Seis consultas chicas sobre un solo cliente — se abre a pedido, no en la
    /// carga de ninguna grilla.
    /// </summary>
    public async Task<ClienteActividadDto> GetClienteActividadAsync(
        string idCliente, DateOnly desde, DateOnly hasta)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var id = idCliente.Replace("'", "''");
        var key = $"cliente-actividad|{id}|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var dto = new ClienteActividadDto { Desde = d, Hasta = h };
            var filtroViaje = $"v._deleted = 0 AND RTRIM(ISNULL(v.id_cliente,'')) = '{id}' "
                            + $"AND v.f_reserva BETWEEN '{d:yyyyMMdd}' AND '{h:yyyyMMdd}'";

            // 1) Serie mensual de viajes y pax
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT CONVERT(char(7), v.f_reserva, 126) AS Mes,
                           COUNT(*)                           AS Viajes,
                           SUM(CASE WHEN RTRIM(v.estado_via) = 'CANCELADO' THEN 1 ELSE 0 END) AS Cancelados,
                           SUM(CAST(ISNULL(v.pax, 0) AS int)) AS Pax
                    FROM viaje v
                    WHERE {filtroViaje}
                    GROUP BY CONVERT(char(7), v.f_reserva, 126)
                    ORDER BY 1
                    """;
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    dto.Meses.Add(new ClienteActividadMes(
                        rd.GetString(0).Trim(), rd.GetInt32(1), rd.GetInt32(2), rd.GetInt32(3), 0));
            }

            // 2) Facturación devengada del mismo período, para pegarla a la serie
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT CONVERT(char(7), v.f_reserva, 126) AS Mes,
                           SUM(CASE WHEN RTRIM(dt.moneda) = 'USS' THEN 0
                                    ELSE dt.importe + ISNULL(dt.incremento,0) - ISNULL(dt.descuento,0) END)
                         + SUM(CASE WHEN RTRIM(dt.moneda) = 'USS' AND l.t_cambio > {TipoCambioMinimoValido}
                                    THEN (dt.importe + ISNULL(dt.incremento,0) - ISNULL(dt.descuento,0)) * l.t_cambio
                                    ELSE 0 END) AS Pesos
                    FROM liquidacion_detalle dt
                    JOIN liquidacion l ON l.idliquidac = dt.idliquidac AND l._deleted = 0
                    JOIN viaje v       ON v.id_viaje   = dt.id_viaje   AND v._deleted = 0
                    WHERE dt._deleted = 0 AND {filtroViaje}
                    GROUP BY CONVERT(char(7), v.f_reserva, 126)
                    """;
                using var rd = await cmd.ExecuteReaderAsync();
                var plata = new Dictionary<string, decimal>();
                while (await rd.ReadAsync())
                    plata[rd.GetString(0).Trim()] = rd.IsDBNull(1) ? 0 : rd.GetDecimal(1);
                for (var i = 0; i < dto.Meses.Count; i++)
                    if (plata.TryGetValue(dto.Meses[i].Mes, out var p))
                        dto.Meses[i] = dto.Meses[i] with { Facturado = p };
            }

            // 3) Servicios más pedidos
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT TOP 8 RTRIM(ISNULL(v.id_servici,'')) AS Serv,
                           COUNT(*) AS Viajes, SUM(CAST(ISNULL(v.pax,0) AS int)) AS Pax
                    FROM viaje v
                    WHERE {filtroViaje} AND NULLIF(LTRIM(RTRIM(v.id_servici)),'') IS NOT NULL
                    GROUP BY RTRIM(ISNULL(v.id_servici,''))
                    ORDER BY COUNT(*) DESC
                    """;
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    dto.Servicios.Add(new ClienteActividadItem(rd.GetString(0).Trim(), rd.GetInt32(1), rd.GetInt32(2)));
            }

            // 4) Recorridos más frecuentes (desde → hasta)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT TOP 8 RTRIM(ISNULL(v.d_destino,'')) AS Desde_,
                           RTRIM(ISNULL(v.h_destino,''))       AS Hasta_,
                           COUNT(*) AS Viajes, SUM(CAST(ISNULL(v.pax,0) AS int)) AS Pax
                    FROM viaje v
                    WHERE {filtroViaje}
                    GROUP BY RTRIM(ISNULL(v.d_destino,'')), RTRIM(ISNULL(v.h_destino,''))
                    ORDER BY COUNT(*) DESC
                    """;
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var de = rd.GetString(0).Trim();
                    var a = rd.GetString(1).Trim();
                    var etiqueta = string.IsNullOrWhiteSpace(de) && string.IsNullOrWhiteSpace(a)
                        ? "(sin recorrido cargado)"
                        : $"{(string.IsNullOrWhiteSpace(de) ? "—" : de)} → {(string.IsNullOrWhiteSpace(a) ? "—" : a)}";
                    dto.Recorridos.Add(new ClienteActividadItem(etiqueta, rd.GetInt32(2), rd.GetInt32(3)));
                }
            }

            // 5) Operadores del cliente (contactos que cargan las reservas)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT RTRIM(ISNULL(nombre,'')) FROM cliente_operador
                    WHERE _deleted = 0 AND RTRIM(ISNULL(id_cliente,'')) = '{id}'
                    ORDER BY nombre
                    """;
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    if (!rd.IsDBNull(0)) dto.Operadores.Add(rd.GetString(0).Trim());
            }

            // 6) Últimas liquidaciones (cabeceras) — el historial de facturación tal como se emitió
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT TOP 8 idliquidac, fecha, RTRIM(ISNULL(moneda,'')), ISNULL(subtotal,0), ISNULL(total,0)
                    FROM liquidacion
                    WHERE _deleted = 0 AND RTRIM(ISNULL(id_cliente,'')) = '{id}'
                    ORDER BY fecha DESC, idliquidac DESC
                    """;
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    dto.Liquidaciones.Add(new ClienteLiquidacionItem(
                        rd.GetInt32(0),
                        rd.IsDBNull(1) ? null : DateOnly.FromDateTime(rd.GetDateTime(1)),
                        rd.GetString(2).Trim(),
                        rd.GetDecimal(3),
                        rd.GetDecimal(4)));
            }

            return dto;
        }) ?? new ClienteActividadDto { Desde = d, Hasta = h };
    }

    /// <summary>Acumulador mutable del merge de las dos consultas (actividad + plata).</summary>
    private sealed class ClienteMesAcum
    {
        public int Viajes, Cancelados, Pax, VjTurismo, VjPersonal, ViajesFacturados, LineasSinTipoCambio;
        public decimal FactPesos, FactUsd, FactUsdEnPesos;
    }
}

// ── DTOs del Panel de Clientes ──────────────────────────────────────────────

/// <summary>
/// Actividad y facturación de un cliente en un mes ("yyyy-MM"). La plata está devengada al mes
/// del VIAJE, no al de la liquidación.
/// </summary>
/// <param name="FacturadoUsd">Lo facturado en dólares, EN dólares (no convertido).</param>
/// <param name="FacturadoUsdEnPesos">Eso mismo pasado a pesos con el t_cambio de cada liquidación.</param>
/// <param name="LineasSinTipoCambio">Líneas en dólares cuya liquidación no tiene t_cambio creíble:
/// quedan FUERA del total en pesos y se muestran como advertencia de calidad.</param>
public record ClienteMesRow(
    string IdCliente,
    string Mes,
    int Viajes,
    int Cancelados,
    int Pax,
    int ViajesTurismo,
    int ViajesPersonal,
    decimal FacturadoPesos,
    decimal FacturadoUsd,
    decimal FacturadoUsdEnPesos,
    int ViajesFacturados,
    int LineasSinTipoCambio)
{
    /// <summary>Facturación total del mes en pesos (lo facturado en pesos + los dólares convertidos).</summary>
    public decimal Facturado => FacturadoPesos + FacturadoUsdEnPesos;

    /// <summary>Viajes que efectivamente prestaron servicio (los cancelados no se facturan).</summary>
    public int ViajesActivos => Viajes - Cancelados;
}

/// <summary>
/// Un cliente del padrón con su historia. Las propiedades calculadas (segmento, faltantes)
/// viven acá para que la pantalla, el Excel y la ficha corten igual.
/// </summary>
public record ClientePadronRow(
    string IdCliente,
    string Nombre,
    string TipoResp,
    string TipoRespDesc,
    string ListaPrecio,
    string ObPrecio,
    decimal Descuento,
    decimal Incremento,
    string Localidad,
    string Provincia,
    string Cuit,
    string Email,
    string Telefono,
    string Contacto,
    DateOnly? FAlta,
    DateOnly? FBaja,
    bool TieneTarifaPropia,
    DateOnly? PrimeraReserva,
    DateOnly? UltimaReserva,
    int ViajesHistoricos)
{
    /// <summary>Dado de baja en el ABM del FoxPro (`f_delete`). Hoy: ninguno de los 414.</summary>
    public bool EsEgresado => FBaja is not null;

    /// <summary>Nombre para mostrar; si la razón social vino vacía, al menos el código.</summary>
    public string Display => string.IsNullOrWhiteSpace(Nombre) ? IdCliente : Nombre;

    /// <summary>Tipo fiscal legible ("RESPONSABLE INSCRIPTO"), con el código como respaldo.</summary>
    public string Fiscal =>
        !string.IsNullOrWhiteSpace(TipoRespDesc) ? TipoRespDesc
        : !string.IsNullOrWhiteSpace(TipoResp) ? TipoResp
        : "Sin tipo fiscal";

    /// <summary>
    /// Segmento por recencia, medido contra la fecha de corte de los datos (no contra "hoy").
    /// Los cortes son los que separan la cartera real de la histórica: 3 meses, 1 año y 2 años.
    /// </summary>
    public string Segmento(DateOnly corte)
    {
        if (UltimaReserva is null) return SegNunca;
        var dias = corte.DayNumber - UltimaReserva.Value.DayNumber;
        return dias <= 90 ? SegActivo
             : dias <= 365 ? SegTibio
             : dias <= 730 ? SegDormido
             : SegInactivo;
    }

    public const string SegActivo = "Activo";
    public const string SegTibio = "Tibio";
    public const string SegDormido = "Dormido";
    public const string SegInactivo = "Inactivo";
    public const string SegNunca = "Nunca operó";

    /// <summary>Orden natural del segmento (de más vivo a más muerto), para las tablas.</summary>
    public static int OrdenSegmento(string seg) => seg switch
    {
        SegActivo => 0, SegTibio => 1, SegDormido => 2, SegInactivo => 3, _ => 4,
    };

    /// <summary>
    /// Qué datos de contacto/fiscales le faltan al registro. Es la materia prima de la vista de
    /// depuración del padrón; se calcula acá para que pantalla y Excel digan lo mismo.
    /// </summary>
    public List<string> Faltantes()
    {
        var f = new List<string>();
        if (string.IsNullOrWhiteSpace(Contacto)) f.Add("contacto");
        if (string.IsNullOrWhiteSpace(Telefono)) f.Add("teléfono");
        if (string.IsNullOrWhiteSpace(Email)) f.Add("e-mail");
        if (string.IsNullOrWhiteSpace(Cuit)) f.Add("CUIT");
        if (string.IsNullOrWhiteSpace(TipoResp)) f.Add("tipo fiscal");
        if (SinPrecio) f.Add("precios");
        return f;
    }

    /// <summary>
    /// El cliente no tiene de dónde sacar un precio: ni lista modelo ni tarifario propio.
    /// Regla del ABM FoxPro (`cliente_abm.scx`): `ob_precio = 'LISTA PRECIO'` exige
    /// `id_lista_p`; `ob_precio = 'CLIENTE'` exige filas en `cliente_tarifa`.
    /// </summary>
    public bool SinPrecio => ObPrecio.Equals("CLIENTE", StringComparison.OrdinalIgnoreCase)
        ? !TieneTarifaPropia
        : string.IsNullOrWhiteSpace(ListaPrecio);

    /// <summary>
    /// Los CUIT genéricos que la AFIP usa para el exterior (55-… y 51-…) se repiten a propósito
    /// entre clientes distintos: NO indican que el cliente esté cargado dos veces.
    /// </summary>
    public bool CuitGenericoExterior =>
        Cuit.StartsWith("55", StringComparison.Ordinal) || Cuit.StartsWith("51", StringComparison.Ordinal);

    /// <summary>Sin una sola reserva en toda la historia: candidato #1 a depurar.</summary>
    public bool NuncaOpero => UltimaReserva is null;

    /// <summary>
    /// Candidato a darle la baja del ABM (`f_delete`): nunca operó, o hace más de dos años que
    /// no pide un servicio. Es una SUGERENCIA para revisar, no una baja automática.
    /// </summary>
    public bool CandidatoBaja(DateOnly corte) =>
        !EsEgresado && (NuncaOpero || Segmento(corte) == SegInactivo);
}

/// <summary>Lo que hizo un cliente en un período — alimenta la pestaña Actividad de su ficha.</summary>
public sealed class ClienteActividadDto
{
    public DateOnly Desde { get; set; }
    public DateOnly Hasta { get; set; }
    public List<ClienteActividadMes> Meses { get; } = new();
    public List<ClienteActividadItem> Servicios { get; } = new();
    public List<ClienteActividadItem> Recorridos { get; } = new();
    public List<string> Operadores { get; } = new();
    public List<ClienteLiquidacionItem> Liquidaciones { get; } = new();

    public int TotalViajes => Meses.Sum(m => m.Viajes);
    public int TotalCancelados => Meses.Sum(m => m.Cancelados);
    public int TotalPax => Meses.Sum(m => m.Pax);
    public decimal TotalFacturado => Meses.Sum(m => m.Facturado);
    public bool SinDatos => Meses.Count == 0;

    /// <summary>Facturado por viaje efectivamente prestado (los cancelados no se facturan).</summary>
    public decimal? PorViaje =>
        TotalViajes - TotalCancelados > 0 ? TotalFacturado / (TotalViajes - TotalCancelados) : null;
}

/// <summary>Un mes de la serie del cliente ("yyyy-MM").</summary>
public record ClienteActividadMes(string Mes, int Viajes, int Cancelados, int Pax, decimal Facturado)
{
    public string MesTexto => Mes.Length == 7 ? $"{Mes.Substring(5, 2)}/{Mes.Substring(2, 2)}" : Mes;
}

/// <summary>Un servicio o recorrido del cliente, con cuánto lo usa.</summary>
public record ClienteActividadItem(string Nombre, int Viajes, int Pax);

/// <summary>Una liquidación emitida al cliente (cabecera).</summary>
public record ClienteLiquidacionItem(int Id, DateOnly? Fecha, string Moneda, decimal Subtotal, decimal Total);

/// <summary>
/// Una fila de la vista de Retención: el mismo cliente medido en dos períodos. La arma la
/// pantalla en memoria; vive acá —y no en el componente— para que el Excel exporte exactamente
/// la misma forma, igual que el resto de los informes del proyecto.
/// </summary>
/// <param name="Clase">Importancia ABC por Pareto de la métrica: A el primer 80 %, B hasta el 95 %, C la cola.</param>
/// <param name="Estado">FUGA · CAIDA · ESTABLE · SUBE · NUEVO.</param>
public record RetencionRow(
    string IdCliente,
    string Nombre,
    string Clase,
    string Estado,
    string Segmento,
    decimal Actual,
    decimal Base,
    decimal Delta,
    double? Pct,
    int Viajes,
    int Cancelados,
    DateOnly? UltimaReserva)
{
    public double PctCancelado => Viajes > 0 ? Cancelados * 100.0 / Viajes : 0;

    /// <summary>Los cinco estados posibles de un cliente entre los dos períodos.</summary>
    public const string EstFuga = "FUGA";
    public const string EstCaida = "CAIDA";
    public const string EstEstable = "ESTABLE";
    public const string EstSube = "SUBE";
    public const string EstNuevo = "NUEVO";

    public string EstadoTexto => Estado switch
    {
        EstFuga => "Se fue",
        EstCaida => "Cayó",
        EstSube => "Creció",
        EstNuevo => "Nuevo",
        _ => "Estable",
    };
}

/// <summary>
/// Una fila del ranking de clientes: el cruce padrón × actividad × plata ya resuelto. La arma la
/// página en memoria y la consume también el Excel, para que no haya dos versiones del número.
/// </summary>
public record ClienteResumenRow(
    string IdCliente,
    string Nombre,
    string Categoria,
    int Viajes,
    int Cancelados,
    int Pax,
    decimal Facturado,
    decimal FacturadoUsd,
    DateOnly? UltimaReserva,
    string Segmento,
    string Fiscal,
    string ListaPrecio)
{
    /// <summary>Viajes que prestaron servicio (base de la facturación por viaje).</summary>
    public int ViajesActivos => Viajes - Cancelados;

    /// <summary>Cuánto deja cada viaje efectivamente prestado. null si no hubo viajes activos.</summary>
    public decimal? FacturadoPorViaje => ViajesActivos > 0 ? Facturado / ViajesActivos : null;

    /// <summary>Qué proporción de sus viajes se cayó.</summary>
    public double PctCancelado => Viajes > 0 ? Cancelados * 100.0 / Viajes : 0;
}
