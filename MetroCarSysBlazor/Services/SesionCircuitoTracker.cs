using System.Collections.Concurrent;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Detecta que el usuario CERRÓ EL NAVEGADOR para cerrar su sesión en
/// <c>usuario_sesion</c> y dejar el Egreso en la auditoría.
///
/// Por qué existe: en web el servidor no recibe ningún aviso de "me voy". Lo único
/// confiable en Blazor Server es el CIRCUITO SignalR: mientras hay una pestaña abierta
/// el circuito está vivo; cuando el usuario cierra el navegador se cae y no vuelve.
///
/// Se lleva el conjunto de circuitos vivos POR SESIÓN (no un contador): así el F5 y las
/// pestañas múltiples no generan falsos cierres — mientras quede un circuito de esa
/// sesión, no se cierra nada. Cuando cae el ÚLTIMO circuito arranca la
/// <see cref="Gracia"/>; si vuelve a aparecer un circuito de la misma sesión antes de
/// que termine (reconexión, F5, wifi que se cortó), se cancela el cierre.
///
/// Singleton: el estado tiene que sobrevivir a los circuitos que vigila. El cierre en
/// la base se hace resolviendo AbmService (scoped) en un scope propio.
/// </summary>
public sealed class SesionCircuitoTracker
{
    /// <summary>Cuánto se espera sin ningún circuito vivo antes de dar la sesión por cerrada.
    /// Cubre F5, cambio de red y microcortes de wifi sin cerrarle la sesión a alguien que sigue
    /// trabajando. Decisión del usuario (09/08/2026): 5 minutos.</summary>
    public static readonly TimeSpan Gracia = TimeSpan.FromMinutes(5);

    private sealed class Estado
    {
        public required string Usuario { get; init; }
        public readonly HashSet<string> Circuitos = new(StringComparer.Ordinal);
        public CancellationTokenSource? CierrePendiente;
    }

    private readonly ConcurrentDictionary<Guid, Estado> _sesiones = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SesionCircuitoTracker> _logger;

    public SesionCircuitoTracker(IServiceScopeFactory scopeFactory, ILogger<SesionCircuitoTracker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Un circuito de esta sesión está vivo (montaje del layout o reconexión).
    /// Cancela cualquier cierre pendiente. Idempotente: repetir el mismo circuito no suma.</summary>
    public void Conectar(Guid sessionId, string usuario, string circuitId)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(circuitId)) return;

        var estado = _sesiones.GetOrAdd(sessionId, _ => new Estado { Usuario = usuario });
        lock (estado)
        {
            estado.Circuitos.Add(circuitId);
            // Volvió alguien: el cierre programado ya no corresponde.
            estado.CierrePendiente?.Cancel();
            estado.CierrePendiente?.Dispose();
            estado.CierrePendiente = null;
        }
    }

    /// <summary>Se cayó un circuito de esta sesión. Si era el último, programa el cierre
    /// de la sesión dentro de <see cref="Gracia"/>.</summary>
    public void Desconectar(Guid sessionId, string circuitId)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(circuitId)) return;
        if (!_sesiones.TryGetValue(sessionId, out var estado)) return;

        CancellationToken token;
        string usuario;
        lock (estado)
        {
            estado.Circuitos.Remove(circuitId);
            if (estado.Circuitos.Count > 0) return;      // quedan pestañas abiertas

            estado.CierrePendiente?.Cancel();
            estado.CierrePendiente?.Dispose();
            estado.CierrePendiente = new CancellationTokenSource();
            token = estado.CierrePendiente.Token;
            usuario = estado.Usuario;
        }

        _ = CerrarTrasGraciaAsync(sessionId, usuario, token);
    }

    private async Task CerrarTrasGraciaAsync(Guid sessionId, string usuario, CancellationToken token)
    {
        try { await Task.Delay(Gracia, token); }
        catch (OperationCanceledException) { return; }   // volvió a conectarse: no se cierra nada

        // Última verificación por si un circuito entró justo en el borde.
        if (_sesiones.TryGetValue(sessionId, out var estado))
        {
            lock (estado)
            {
                if (estado.Circuitos.Count > 0) return;
            }
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var abm = scope.ServiceProvider.GetRequiredService<AbmService>();
            // Solo hace algo si la sesión sigue activa=1 (si el usuario ya había apretado
            // Cerrar sesión, o el barrido la venció, no se registra un segundo Egreso).
            if (await abm.RegistrarCierrePorNavegadorAsync(usuario, sessionId))
                _logger.LogInformation("Sesión de {Usuario} cerrada: se cerró el navegador.", usuario);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo cerrar la sesión de {Usuario} tras cerrarse el navegador.", usuario);
        }
        finally
        {
            _sesiones.TryRemove(sessionId, out _);
        }
    }
}

/// <summary>
/// Datos de la sesión del circuito actual. SCOPED = una instancia por circuito, así que
/// el layout (que conoce los claims) y el <see cref="SesionCircuitHandler"/> (que ve caer
/// la conexión) comparten la misma instancia.
///
/// Por qué no se leen los claims directo en el handler: dentro del circuito ya no hay
/// HttpContext y el estado de autenticación puede no estar listo cuando el circuito abre.
/// El layout lo llena cuando ya tiene el usuario resuelto — determinístico.
/// </summary>
public sealed class SesionCircuitoContexto
{
    public string CircuitId { get; set; } = "";
    public Guid SessionId { get; set; }
    public string Usuario { get; set; } = "";

    public bool Listo => SessionId != Guid.Empty && CircuitId.Length > 0;
}
