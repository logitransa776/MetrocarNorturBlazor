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

    /// <summary>Principal vigente — lo lee PermissionService para chequear claims.</summary>
    public ClaimsPrincipal Principal => _current;

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(new AuthenticationState(_current));

    /// <summary>
    /// Carga la sesión con los permisos del Metrocar: un claim por letra de
    /// `acceso` (módulos), `nivel` (dígitos ABM) y `operador` (mesa de tráfico).
    /// </summary>
    public void IniciarSesion(string usuario, string acceso, string nivel, bool operador)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, usuario) };

        foreach (var letra in (acceso ?? "").ToUpperInvariant().Where(char.IsLetter).Distinct())
            claims.Add(new Claim(NorturClaims.Modulo, letra.ToString()));

        claims.Add(new Claim(NorturClaims.Nivel, nivel ?? ""));

        if (operador)
            claims.Add(new Claim(NorturClaims.Operador, "1"));

        var identity = new ClaimsIdentity(claims, authenticationType: "NorturAuth");
        _current = new ClaimsPrincipal(identity);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public void CerrarSesion()
    {
        _current = new ClaimsPrincipal(new ClaimsIdentity());
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
