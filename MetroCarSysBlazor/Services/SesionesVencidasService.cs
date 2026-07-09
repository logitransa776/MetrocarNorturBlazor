namespace MetroCarSysBlazor.Services;

/// <summary>
/// Barrido periódico de sesiones vencidas. Cada <see cref="Intervalo"/> cierra en
/// <c>usuario_sesion</c> las sesiones que siguen activas pero superaron las 8 hs
/// (<see cref="AbmService.DuracionSesion"/>) y registra un evento VENCIDA por cada una
/// en <c>usuarios_logs</c>.
///
/// Por qué existe: en web no hay un momento exacto donde el server "sepa" que una cookie
/// venció (el navegador simplemente deja de mandarla). La detección en el próximo login
/// ya cubre el caso del usuario que vuelve; este barrido cubre al usuario que NO vuelve
/// (cerró el navegador y no reingresa) para que su sesión no quede "activa" para siempre.
///
/// AbmService es scoped → se resuelve dentro de un scope por tick. Nunca tumba la app:
/// CerrarSesionesVencidasAsync ya traga sus propios errores.
/// </summary>
public sealed class SesionesVencidasService : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SesionesVencidasService> _logger;

    public SesionesVencidasService(IServiceScopeFactory scopeFactory, ILogger<SesionesVencidasService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Primer barrido con un pequeño retraso para no competir con el arranque.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var abm = scope.ServiceProvider.GetRequiredService<AbmService>();
                var cerradas = await abm.CerrarSesionesVencidasAsync();
                if (cerradas > 0)
                    _logger.LogInformation("Barrido de sesiones: {N} vencidas cerradas.", cerradas);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error en el barrido de sesiones vencidas (se reintenta al próximo tick).");
            }

            try { await Task.Delay(Intervalo, stoppingToken); } catch (TaskCanceledException) { break; }
        }
    }
}
