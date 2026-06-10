using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Estado de sesión del dashboard (equivalente a st.session_state.logueado /
/// auth.requerir_login() del Streamlit). Mantiene el usuario logueado en el
/// circuito de Blazor Server. Para uso interno en LAN no usamos cookies; si más
/// adelante hace falta persistir sesión entre recargas, se migra a auth con
/// cookies/Identity.
/// </summary>
public class NorturAuthStateProvider : AuthenticationStateProvider
{
    private ClaimsPrincipal _current = new(new ClaimsIdentity());

    public string? UsuarioActual =>
        _current.Identity?.IsAuthenticated == true ? _current.Identity!.Name : null;

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(new AuthenticationState(_current));

    public void IniciarSesion(string usuario)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, usuario) },
            authenticationType: "NorturAuth");
        _current = new ClaimsPrincipal(identity);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public void CerrarSesion()
    {
        _current = new ClaimsPrincipal(new ClaimsIdentity());
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
