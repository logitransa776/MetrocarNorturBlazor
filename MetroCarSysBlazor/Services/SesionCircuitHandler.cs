using Microsoft.AspNetCore.Components.Server.Circuits;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Engancha el ciclo de vida del circuito SignalR con
/// <see cref="SesionCircuitoTracker"/>: mientras el circuito vive, la sesión está en uso;
/// cuando se cae (cerró el navegador / la pestaña / se fue la red), avisa al tracker para
/// que arranque la gracia y cierre la sesión si nadie vuelve.
///
/// El session_id no se lee acá (dentro del circuito no hay HttpContext): lo deja el layout
/// en <see cref="SesionCircuitoContexto"/>, que es scoped igual que este handler → misma
/// instancia para el mismo circuito.
/// </summary>
public sealed class SesionCircuitHandler : CircuitHandler
{
    private readonly SesionCircuitoContexto _ctx;
    private readonly SesionCircuitoTracker _tracker;

    public SesionCircuitHandler(SesionCircuitoContexto ctx, SesionCircuitoTracker tracker)
    {
        _ctx = ctx;
        _tracker = tracker;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _ctx.CircuitId = circuit.Id;
        return Task.CompletedTask;
    }

    // Reconexión tras un corte breve: el circuito es el mismo, vuelve a contar como vivo.
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_ctx.Listo) _tracker.Conectar(_ctx.SessionId, _ctx.Usuario, _ctx.CircuitId);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_ctx.Listo) _tracker.Desconectar(_ctx.SessionId, _ctx.CircuitId);
        return Task.CompletedTask;
    }

    // El circuito se descarta definitivamente (el server lo dio de baja tras el timeout
    // de reconexión). Desconectar es idempotente, así que llamarlo de nuevo no molesta.
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_ctx.Listo) _tracker.Desconectar(_ctx.SessionId, _ctx.CircuitId);
        return Task.CompletedTask;
    }
}
