using System.Data;
using Microsoft.Data.SqlClient;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Diagnóstico del SQL Server EXTERNO del sistema de GPS (Sistema → Parámetros → solapa GPS,
/// migración de <c>parametro_sql_server.scx</c>). Es el destino de la vía 2 de
/// <c>gps_xlm()</c>: por cada asignación / reasignación / finalización / cancelación, FoxPro
/// hace INSERT o UPDATE de un viaje en la tabla configurada (hoy <c>Servicios</c>).
///
/// 🔴 <b>Esta integración está VIVA</b> (verificado 12/08/2026: <c>parametro.sql_gps = 1</c> en
/// los dos servers productivos, apuntando a <c>192.168.0.8</c> / <c>MetroCarSQL</c>).
/// Afecta al 93 % de los viajes — ver <c>docs/PlanoFoxPro/trafico/GPS_XLM.md</c>.
/// Buslink todavía NO implementa el envío: este servicio solo diagnostica.
///
/// Correcciones sobre el FoxPro:
///  - No se replica <c>SQL_instalado()</c>: recorría por WMI los servicios de la máquina
///    LOCAL buscando alguno con "SQL" en el nombre, lo cual no dice nada del server remoto.
///  - El <b>Truncate</b> del original estaba roto y era peligroso: usaba dos variables nunca
///    definidas en su método (<c>lnHandle</c> y <c>cSql_tabla</c>) y después ejecutaba un
///    <c>DELETE FROM servicios_nortur</c> con el nombre de tabla <b>hardcodeado</b>, distinto
///    del configurado. Acá se usa la conexión y la tabla configuradas, y nada más.
/// </summary>
public class GpsSqlService
{
    /// <summary>Datos de conexión al SQL del GPS, tal como están en <c>parametro</c>.</summary>
    public sealed record Config(string Servidor, string Base, string Usuario, string Password, string Tabla);

    public sealed record Resultado(bool Ok, string Mensaje);

    /// <summary>Una fila de la tabla del GPS, para la vista previa (nombre de columna → valor).</summary>
    public sealed record Preview(bool Ok, string Mensaje, List<string> Columnas, List<List<string>> Filas, long? Total);

    private const int TimeoutSegundos = 15;

    /// <summary>
    /// Arma la connection string. Se fuerza <c>TrustServerCertificate</c> porque el server del
    /// GPS es un SQL viejo de la LAN sin certificado válido (igual que <c>replicaVPF</c>), y un
    /// timeout corto para que el diagnóstico no cuelgue la pantalla.
    /// </summary>
    private static string ConnectionString(Config cfg) => new SqlConnectionStringBuilder
    {
        DataSource = cfg.Servidor.Trim(),
        InitialCatalog = cfg.Base.Trim(),
        UserID = cfg.Usuario.Trim(),
        Password = cfg.Password ?? "",
        TrustServerCertificate = true,
        Encrypt = false,
        ConnectTimeout = TimeoutSegundos,
        CommandTimeout = TimeoutSegundos,
        Pooling = false,          // diagnóstico ocasional contra un server ajeno: sin pool
    }.ConnectionString;

    private static string? ValidarConfig(Config cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Servidor)) return "Falta el servidor del SQL de GPS.";
        if (string.IsNullOrWhiteSpace(cfg.Base)) return "Falta el nombre de la base.";
        if (string.IsNullOrWhiteSpace(cfg.Usuario)) return "Falta el usuario.";
        return null;
    }

    /// <summary>
    /// Botón <b>Conexión</b>: prueba que el SQL del GPS responda con esas credenciales.
    /// Es la forma de confirmar, desde el servidor de Buslink, si el feed de
    /// <c>gps_xlm()</c> tiene a dónde llegar (no se pudo verificar desde la PC de desarrollo:
    /// el host responde ping pero su puerto SQL no es accesible desde ahí).
    /// </summary>
    public async Task<Resultado> ProbarConexionAsync(Config cfg)
    {
        var err = ValidarConfig(cfg);
        if (err is not null) return new(false, err);

        try
        {
            await using var conn = new SqlConnection(ConnectionString(cfg));
            await conn.OpenAsync();

            // Además de conectar, confirmamos que la tabla destino exista: conectar y que la
            // tabla no esté es exactamente el modo en que este feed fallaría en silencio.
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = TimeoutSegundos;
            cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = @t";
            cmd.Parameters.Add(new SqlParameter("@t", cfg.Tabla.Trim()));
            var existe = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;

            return existe
                ? new(true, $"Conexión OK con {cfg.Servidor} · base {cfg.Base}. La tabla «{cfg.Tabla}» existe.")
                : new(false, $"Conectó a {cfg.Servidor} · base {cfg.Base}, pero NO existe la tabla «{cfg.Tabla}». " +
                             "El envío de GPS estaría fallando en silencio.");
        }
        catch (Exception ex)
        {
            return new(false, $"No se pudo conectar a {cfg.Servidor}: {(ex.InnerException?.Message ?? ex.Message).Trim()}");
        }
    }

    /// <summary>
    /// Botón <b>Select</b>: en el FoxPro abría un <c>Browse</c> con toda la tabla. Acá se
    /// traen las últimas <paramref name="filas"/> filas más el total, que es lo que sirve para
    /// contestar la pregunta real: <i>¿el feed está entrando y cuál fue el último viaje?</i>
    /// </summary>
    public async Task<Preview> UltimasFilasAsync(Config cfg, int filas = 20)
    {
        var err = ValidarConfig(cfg);
        if (err is not null) return new(false, err, new(), new(), null);
        if (string.IsNullOrWhiteSpace(cfg.Tabla)) return new(false, "Falta el nombre de la tabla.", new(), new(), null);

        var tabla = cfg.Tabla.Trim();
        // El nombre de tabla no puede ir parametrizado → se valida como identificador simple
        // y se corchetea. Sin esto sería inyección SQL contra un server de terceros.
        if (!tabla.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return new(false, $"El nombre de tabla «{tabla}» no es un identificador válido.", new(), new(), null);

        try
        {
            await using var conn = new SqlConnection(ConnectionString(cfg));
            await conn.OpenAsync();

            long total;
            await using (var cnt = conn.CreateCommand())
            {
                cnt.CommandTimeout = TimeoutSegundos;
                cnt.CommandText = $"SELECT COUNT_BIG(*) FROM [{tabla}]";
                total = Convert.ToInt64(await cnt.ExecuteScalarAsync() ?? 0L);
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = TimeoutSegundos;
            // Sin saber el esquema exacto de la tabla ajena, se ordena por su columna de
            // identidad si la tiene; si no, se traen las primeras N sin ordenar.
            cmd.CommandText = $"""
                DECLARE @ident sysname = (
                    SELECT TOP 1 c.name FROM sys.columns c
                    WHERE c.object_id = OBJECT_ID('[{tabla}]') AND c.is_identity = 1);
                DECLARE @sql nvarchar(max) = 'SELECT TOP {filas} * FROM [{tabla}]'
                    + CASE WHEN @ident IS NULL THEN '' ELSE ' ORDER BY [' + @ident + '] DESC' END;
                EXEC sp_executesql @sql;
                """;

            var columnas = new List<string>();
            var datos = new List<List<string>>();
            await using var rd = await cmd.ExecuteReaderAsync();
            for (int i = 0; i < rd.FieldCount; i++) columnas.Add(rd.GetName(i));
            while (await rd.ReadAsync())
            {
                var fila = new List<string>(rd.FieldCount);
                for (int i = 0; i < rd.FieldCount; i++)
                    fila.Add(rd.IsDBNull(i) ? "" : (rd.GetValue(i)?.ToString() ?? "").Trim());
                datos.Add(fila);
            }

            return new(true, $"{total:N0} filas en «{tabla}» (se muestran las últimas {datos.Count}).",
                       columnas, datos, total);
        }
        catch (Exception ex)
        {
            return new(false, $"No se pudo leer «{tabla}»: {(ex.InnerException?.Message ?? ex.Message).Trim()}",
                       new(), new(), null);
        }
    }

    /// <summary>
    /// Botón <b>Truncate</b>, en su versión corregida: vacía la tabla CONFIGURADA usando la
    /// conexión configurada. El original no hacía esto (ver el comentario de la clase).
    ///
    /// ⛔ Protegido por <see cref="AbmFeatureFlags.GpsTruncateActivo"/>: vaciar esta tabla
    /// borra el estado del sistema de GPS de <b>136 clientes</b> (entre ellos AEROLINEAS) en
    /// un servidor que <b>no es de Buslink</b>. Queda construido pero desarmado.
    /// </summary>
    public async Task<Resultado> VaciarTablaAsync(Config cfg)
    {
        if (!AbmFeatureFlags.GpsTruncateActivo)
            return new(false,
                "Vaciar la tabla del GPS está deshabilitado en Buslink. Es una operación destructiva " +
                "sobre un servidor de terceros que hoy alimenta el seguimiento de 136 clientes. " +
                "Se habilita con AbmFeatureFlags.GpsTruncateActivo previa autorización explícita.");

        var err = ValidarConfig(cfg);
        if (err is not null) return new(false, err);

        var tabla = (cfg.Tabla ?? "").Trim();
        if (!tabla.All(c => char.IsLetterOrDigit(c) || c == '_') || tabla.Length == 0)
            return new(false, $"El nombre de tabla «{tabla}» no es un identificador válido.");

        try
        {
            await using var conn = new SqlConnection(ConnectionString(cfg));
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = TimeoutSegundos;
            // TRUNCATE puede fallar si la tabla tiene FKs; en ese caso se cae a DELETE, que es
            // lo que el FoxPro terminaba haciendo (pero contra la tabla correcta).
            cmd.CommandText = $"""
                BEGIN TRY
                    TRUNCATE TABLE [{tabla}];
                END TRY
                BEGIN CATCH
                    DELETE FROM [{tabla}];
                END CATCH
                """;
            await cmd.ExecuteNonQueryAsync();
            return new(true, $"Se vació la tabla «{tabla}» en {cfg.Servidor}.");
        }
        catch (Exception ex)
        {
            return new(false, $"No se pudo vaciar «{tabla}»: {(ex.InnerException?.Message ?? ex.Message).Trim()}");
        }
    }
}
