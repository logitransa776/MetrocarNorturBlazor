using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MetroCarSysBlazor.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  Panel del Operador (informe NUEVO — no existe en el Metrocar/FoxPro).
//
//  Contesta quién carga el trabajo administrativo, con qué anticipación, con qué
//  calidad, y quién modifica lo que cargó otro. Sale de los cuatro campos de
//  auditoría que `viaje` viene llenando desde 2021 y que hasta hoy nadie miró:
//  `u_create` / `f_create` / `u_modify` / `f_modify`.
//
//  Doble propósito (por eso se construyó primero):
//    · HOY, como gestión: mide la concentración de la carga en pocas personas.
//    · DESPUÉS DEL DÍA D, como control: cuando Buslink escriba el circuito
//      `viaje`, este informe es el que contesta "¿quién tocó esto?". Tenerlo
//      antes del corte deja la línea base de cómo se cargaba en FoxPro.
//
//  ⚠ EL PERÍODO FILTRA POR FECHA DE CARGA (`f_create`), NO por fecha del viaje.
//  Es el único informe del sistema que lo hace: acá la pregunta es "qué hizo el
//  operador esta semana", no "qué viajes salieron esta semana". Una reserva
//  cargada hoy para diciembre entra en el período de HOY. La pantalla lo aclara.
//
//  ⚠ LÍMITES DE LOS DATOS (medidos 11/08/2026, no supuestos):
//   · `f_create` es `date`, SIN hora, y `_created_at` es el timestamp de la
//     réplica (todas las filas comparten el mismo instante de importación) → NO
//     hay curva horaria de carga posible. No prometerla.
//   · `u_modify` guarda SOLO LA ÚLTIMA modificación y la pisa: si A y después B
//     tocan la misma reserva, solo queda B. Las modificaciones son entonces un
//     PISO, nunca el total, y la matriz creador×modificador subestima.
//   · No hay historial de estados (no existe "cuándo pasó a ASIGNADO"), así que
//     la latencia de asignación NO es medible. Lo que sí se mide es la
//     ANTELACIÓN: días entre que se cargó y la fecha del viaje.
//   · `u_delete` y `f_delete` están VACÍOS en `viaje` (0 filas): las bajas no se
//     auditan porque una reserva no se borra, se CANCELA.
// ─────────────────────────────────────────────────────────────────────────────

public partial class ReportService
{
    /// <summary>
    /// Una fila por operador con todo su perfil de carga del período. Son ~12 filas: el
    /// resto del tablero (KPIs, cross-filter, rankings) se resuelve en memoria.
    ///
    /// Es un FULL OUTER JOIN a propósito: un operador puede haber MODIFICADO reservas del
    /// período sin haber cargado ninguna (típico del supervisor que corrige), y esa persona
    /// tiene que aparecer igual en el panel.
    /// </summary>
    public async Task<List<OperadorPerfilRow>> GetOperadorPerfilAsync(DateOnly desde, DateOnly hasta)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var key = $"oper-perfil|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // `usuario` NO tiene columna `nombre` ni `activo` (verificado en sys.columns):
            // el único identificador es `usuario`, y la vigencia se lee de _deleted/f_delete.
            // Un operador que no matchea ninguna fila queda como FANTASMA — cargó reservas
            // con un usuario que hoy no existe en el padrón.
            var sql = $"""
                WITH altas AS (
                    SELECT
                        RTRIM(v.u_create)                                                 AS Oper,
                        COUNT(*)                                                          AS Altas,
                        COUNT(DISTINCT v.f_create)                                        AS DiasConCarga,
                        MIN(v.f_create)                                                   AS PrimeraCarga,
                        MAX(v.f_create)                                                   AS UltimaCarga,
                        COUNT(DISTINCT RTRIM(ISNULL(v.nombre_cli, '')))                   AS Clientes,
                        SUM(CASE WHEN v.estado_via = 'CANCELADO'   THEN 1 ELSE 0 END)     AS Canceladas,
                        SUM(CASE WHEN v.estado_via = 'SIN ASIGNAR' THEN 1 ELSE 0 END)     AS SinAsignar,
                        SUM(CAST(ISNULL(v.pax, 0) AS int))                                AS Pax,
                        AVG(CAST(DATEDIFF(day, v.f_create, v.f_reserva) AS float))        AS AntelacionProm,
                        SUM(CASE WHEN DATEDIFF(day, v.f_create, v.f_reserva) < 0
                                 THEN 1 ELSE 0 END)                                       AS Retroactivas,
                        SUM(CASE WHEN NULLIF(LTRIM(RTRIM(v.u_modify)), '') IS NOT NULL
                                  AND RTRIM(v.u_modify) <> RTRIM(v.u_create)
                                 THEN 1 ELSE 0 END)                                       AS AltasTocadasPorOtro
                    FROM viaje v
                    WHERE v._deleted = 0
                      AND v.f_create BETWEEN '{d:yyyyMMdd}' AND '{h:yyyyMMdd}'
                      AND NULLIF(LTRIM(RTRIM(v.u_create)), '') IS NOT NULL
                    GROUP BY RTRIM(v.u_create)
                ),
                modifs AS (
                    SELECT
                        RTRIM(v.u_modify)                                                 AS Oper,
                        COUNT(*)                                                          AS Modificaciones,
                        SUM(CASE WHEN RTRIM(v.u_modify) <> RTRIM(ISNULL(v.u_create, ''))
                                 THEN 1 ELSE 0 END)                                       AS ModificoDeOtros
                    FROM viaje v
                    WHERE v._deleted = 0
                      AND v.f_create BETWEEN '{d:yyyyMMdd}' AND '{h:yyyyMMdd}'
                      AND NULLIF(LTRIM(RTRIM(v.u_modify)), '') IS NOT NULL
                    GROUP BY RTRIM(v.u_modify)
                )
                SELECT
                    ISNULL(a.Oper, m.Oper)              AS Operador,
                    ISNULL(a.Altas, 0)                  AS Altas,
                    ISNULL(a.DiasConCarga, 0)           AS DiasConCarga,
                    a.PrimeraCarga                      AS PrimeraCarga,
                    a.UltimaCarga                       AS UltimaCarga,
                    ISNULL(a.Clientes, 0)               AS Clientes,
                    ISNULL(a.Canceladas, 0)             AS Canceladas,
                    ISNULL(a.SinAsignar, 0)             AS SinAsignar,
                    ISNULL(a.Pax, 0)                    AS Pax,
                    a.AntelacionProm                    AS AntelacionProm,
                    ISNULL(a.Retroactivas, 0)           AS Retroactivas,
                    ISNULL(a.AltasTocadasPorOtro, 0)    AS AltasTocadasPorOtro,
                    ISNULL(m.Modificaciones, 0)         AS Modificaciones,
                    ISNULL(m.ModificoDeOtros, 0)        AS ModificoDeOtros,
                    CASE WHEN u.usuario IS NULL THEN 2
                         WHEN u._deleted = 1 OR u.f_delete IS NOT NULL THEN 1
                         ELSE 0 END                     AS EstadoUsuario
                FROM altas a
                FULL OUTER JOIN modifs m ON m.Oper = a.Oper
                LEFT JOIN usuario u ON RTRIM(u.usuario) = ISNULL(a.Oper, m.Oper)
                ORDER BY ISNULL(a.Altas, 0) DESC, ISNULL(m.Modificaciones, 0) DESC
                """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = new List<OperadorPerfilRow>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new OperadorPerfilRow(
                    Operador: reader.GetString(0).Trim(),
                    Altas: reader.GetInt32(1),
                    DiasConCarga: reader.GetInt32(2),
                    PrimeraCarga: reader.IsDBNull(3) ? null : DateOnly.FromDateTime(reader.GetDateTime(3)),
                    UltimaCarga: reader.IsDBNull(4) ? null : DateOnly.FromDateTime(reader.GetDateTime(4)),
                    Clientes: reader.GetInt32(5),
                    Canceladas: reader.GetInt32(6),
                    SinAsignar: reader.GetInt32(7),
                    Pax: reader.GetInt32(8),
                    AntelacionProm: reader.IsDBNull(9) ? null : reader.GetDouble(9),
                    Retroactivas: reader.GetInt32(10),
                    AltasTocadasPorOtro: reader.GetInt32(11),
                    Modificaciones: reader.GetInt32(12),
                    ModificoDeOtros: reader.GetInt32(13),
                    EstadoUsuario: (EstadoUsuarioOperador)reader.GetInt32(14)));
            }
            return result;
        }) ?? new();
    }

    /// <summary>
    /// Matriz "quién modifica lo de quién": una fila por par (creador, modificador). La
    /// diagonal (se corrige a sí mismo) es la mayoría y NO es fricción; lo que se lee es
    /// lo de afuera de la diagonal.
    /// </summary>
    /// <remarks>
    /// Piso, no total: `u_modify` conserva solo la ÚLTIMA mano que tocó la reserva.
    /// </remarks>
    public async Task<List<OperadorMatrizRow>> GetOperadorMatrizAsync(DateOnly desde, DateOnly hasta)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var key = $"oper-matriz|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Database.SqlQuery<OperadorMatrizRow>($"""
                SELECT
                    RTRIM(v.u_create) AS Creador,
                    RTRIM(v.u_modify) AS Modificador,
                    COUNT(*)          AS Cantidad
                FROM viaje v
                WHERE v._deleted = 0
                  AND v.f_create BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                  AND NULLIF(LTRIM(RTRIM(v.u_create)), '') IS NOT NULL
                  AND NULLIF(LTRIM(RTRIM(v.u_modify)), '') IS NOT NULL
                GROUP BY RTRIM(v.u_create), RTRIM(v.u_modify)
                ORDER BY COUNT(*) DESC
                """).ToListAsync();
        }) ?? new();
    }

    /// <summary>
    /// Altas por día y operador. Alimenta la evolución temporal y —filtrada en memoria— el
    /// recálculo del cross-filter, sin volver a la base. Con ~10 operadores y un año de
    /// rango son ~3.500 filas: barato de traer y de recorrer.
    /// </summary>
    public async Task<List<OperadorDiaRow>> GetOperadorEvolucionAsync(DateOnly desde, DateOnly hasta)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var key = $"oper-evol|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Database.SqlQuery<OperadorDiaRow>($"""
                SELECT
                    v.f_create        AS Fecha,
                    RTRIM(v.u_create) AS Operador,
                    COUNT(*)          AS Altas
                FROM viaje v
                WHERE v._deleted = 0
                  AND v.f_create BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                  AND NULLIF(LTRIM(RTRIM(v.u_create)), '') IS NOT NULL
                GROUP BY v.f_create, RTRIM(v.u_create)
                ORDER BY v.f_create
                """).ToListAsync();
        }) ?? new();
    }

    /// <summary>
    /// Qué carga cada operador: sus principales clientes en el período. Es el dato que le
    /// pone contexto a la concentración — un operador puede tener el 77% de las altas
    /// simplemente porque le tocó el contrato grande y repetitivo.
    /// </summary>
    public async Task<List<OperadorClienteRow>> GetOperadorClientesAsync(DateOnly desde, DateOnly hasta)
    {
        var d = ClampFecha(desde);
        var h = ClampFecha(hasta);
        var key = $"oper-clientes|{d:yyyyMMdd}|{h:yyyyMMdd}";

        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Database.SqlQuery<OperadorClienteRow>($"""
                SELECT
                    RTRIM(v.u_create)                                        AS Operador,
                    CASE WHEN NULLIF(LTRIM(RTRIM(v.nombre_cli)), '') IS NULL
                         THEN 'Sin cliente' ELSE RTRIM(v.nombre_cli) END     AS Cliente,
                    COUNT(*)                                                 AS Altas
                FROM viaje v
                WHERE v._deleted = 0
                  AND v.f_create BETWEEN {d.ToString("yyyyMMdd")} AND {h.ToString("yyyyMMdd")}
                  AND NULLIF(LTRIM(RTRIM(v.u_create)), '') IS NOT NULL
                GROUP BY RTRIM(v.u_create),
                         CASE WHEN NULLIF(LTRIM(RTRIM(v.nombre_cli)), '') IS NULL
                              THEN 'Sin cliente' ELSE RTRIM(v.nombre_cli) END
                ORDER BY COUNT(*) DESC
                """).ToListAsync();
        }) ?? new();
    }
}

// ── DTOs del Panel del Operador ─────────────────────────────────────────────

/// <summary>Vigencia del operador en el padrón de usuarios del sistema.</summary>
public enum EstadoUsuarioOperador
{
    /// <summary>Existe en `usuario` y está vigente.</summary>
    Vigente = 0,

    /// <summary>Existe pero está dado de baja: cargó y después se lo dio de baja.</summary>
    DadoDeBaja = 1,

    /// <summary>
    /// FANTASMA: cargó reservas con un usuario que NO figura en `usuario`. Es un hallazgo
    /// de control, no un error del informe — hay que mirarlo caso por caso.
    /// </summary>
    NoExiste = 2,
}

/// <summary>
/// Perfil de carga de un operador en el período. Las propiedades calculadas viven acá para
/// que la pantalla y el Excel corten exactamente igual.
/// </summary>
public record OperadorPerfilRow(
    string Operador,
    int Altas,
    int DiasConCarga,
    DateOnly? PrimeraCarga,
    DateOnly? UltimaCarga,
    int Clientes,
    int Canceladas,
    int SinAsignar,
    int Pax,
    double? AntelacionProm,
    int Retroactivas,
    int AltasTocadasPorOtro,
    int Modificaciones,
    int ModificoDeOtros,
    EstadoUsuarioOperador EstadoUsuario)
{
    /// <summary>Altas por día efectivamente trabajado (no por día del calendario).</summary>
    public double AltasPorDia => DiasConCarga > 0 ? (double)Altas / DiasConCarga : 0;

    /// <summary>Qué porcentaje de lo que cargó terminó cancelado.</summary>
    public double PctCanceladas => Altas > 0 ? Canceladas * 100.0 / Altas : 0;

    /// <summary>Qué porcentaje de lo que cargó sigue sin unidad asignada.</summary>
    public double PctSinAsignar => Altas > 0 ? SinAsignar * 100.0 / Altas : 0;

    /// <summary>Modificó reservas pero no cargó ninguna: perfil de corrector/supervisor.</summary>
    public bool SoloModifica => Altas == 0 && Modificaciones > 0;

    /// <summary>Cargó al menos una reserva DESPUÉS de la fecha del viaje.</summary>
    public bool TieneRetroactivas => Retroactivas > 0;

    /// <summary>Etiqueta corta del estado del usuario, para la grilla y el Excel.</summary>
    public string EstadoTexto => EstadoUsuario switch
    {
        EstadoUsuarioOperador.NoExiste => "No existe en Usuarios",
        EstadoUsuarioOperador.DadoDeBaja => "Dado de baja",
        _ => "Vigente",
    };
}

/// <summary>Una celda de la matriz creador × modificador.</summary>
public record OperadorMatrizRow(string Creador, string Modificador, int Cantidad)
{
    /// <summary>La diagonal: se corrigió a sí mismo. No es fricción entre personas.</summary>
    public bool EsPropia => string.Equals(Creador, Modificador, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Altas de un operador en un día concreto de carga.</summary>
public record OperadorDiaRow(DateOnly Fecha, string Operador, int Altas);

/// <summary>Altas de un operador para un cliente en el período.</summary>
public record OperadorClienteRow(string Operador, string Cliente, int Altas);
