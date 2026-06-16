using Microsoft.Data.SqlClient;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Calienta el pool de conexiones SQL al arrancar la app (ítem 4 del diagnóstico
/// de performance del Zoom del Viaje).
///
/// El connection string usa Pooling=True con Min Pool Size=2. Sin warmup, esas 2
/// conexiones se crean recién cuando llega la primera query real — y la primera
/// query la dispara el operador al hacer doble clic, así que ÉL pagaría el ciclo
/// completo en frío: resolución de instancia (\SQLEXPRESS vía SQL Browser UDP 1434)
/// + handshake TLS (Encrypt=True) + login TDS (sa). Eso son los segundos de lag.
///
/// Este servicio paga ese costo UNA vez en el arranque del proceso, abriendo las 2
/// conexiones mínimas en paralelo y ejecutando un SELECT trivial en cada una para
/// forzar el login. Después quedan vivas en el pool, calientes y autenticadas —
/// equivalente al `@st.cache_resource` que mantenía el Streamlit viejo instantáneo.
///
/// Abre SqlConnection directo desde el connection string de IConfiguration (no vía
/// el DbContextFactory): en el arranque el factory puede entregar opciones a medio
/// inicializar (se veía "server '' database ''" → ConnectionString vacío). El pool
/// de SqlClient es global por connection string, así que calentar acá beneficia
/// igual a todas las conexiones que abre EF después.
///
/// No bloquea el arranque (StartAsync no espera el warmup) y nunca tumba la app si
/// la base no está disponible: loguea el error y sigue.
/// </summary>
public sealed class DbWarmupService : IHostedService
{
    private readonly string? _connectionString;
    private readonly ILogger<DbWarmupService> _logger;

    // Igual al Min Pool Size del connection string: pre-abrimos esa cantidad.
    private const int ConexionesACalentar = 2;

    public DbWarmupService(IConfiguration config, ILogger<DbWarmupService> logger)
    {
        _connectionString = config.GetConnectionString("DefaultConnection");
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget: no demoramos el arranque del servidor web esperando la base.
        _ = Task.Run(() => CalentarAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task CalentarAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogWarning("Warmup omitido: no hay connection string 'DefaultConnection'.");
            return;
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // En paralelo para que las dos conexiones se materialicen a la vez en el pool.
            var tareas = Enumerable.Range(0, ConexionesACalentar)
                .Select(_ => AbrirYPingAsync(ct));
            await Task.WhenAll(tareas);

            sw.Stop();
            _logger.LogInformation(
                "Pool de conexiones SQL calentado: {N} conexiones en {Ms} ms.",
                ConexionesACalentar, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Si la base no está disponible al arrancar, no es fatal: la app sigue y
            // la primera query real volverá a intentar la conexión.
            _logger.LogWarning(ex, "No se pudo calentar el pool de conexiones SQL al arrancar.");
        }
    }

    private async Task AbrirYPingAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        // SELECT 1 fuerza el login completo (TLS + auth) sin tocar ninguna tabla.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        _ = await cmd.ExecuteScalarAsync(ct);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
