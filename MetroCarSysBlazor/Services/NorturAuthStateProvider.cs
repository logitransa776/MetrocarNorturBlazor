using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Estado de sesión del dashboard, basado en la COOKIE de autenticación de
/// ASP.NET Core (no en estado del circuito). La cookie la envía el navegador en
/// cada petición HTTP — incluida abrir una pestaña nueva o un F5 — así que el
/// servidor conoce al usuario ANTES de renderizar. Esto resuelve el caso de
/// "abrir varias pestañas del mismo sistema sin re-loguear" de raíz, sin carreras.
///
/// La escritura/borrado de la cookie NO ocurre acá (en un circuito interactivo el
/// response HTTP ya se envió): se hace en endpoints HTTP /auth/login y
/// /auth/logout (ver Program.cs) que sí corren en contexto de request.
///
/// Hereda de ServerAuthenticationStateProvider: Blazor ya hidrata el
/// ClaimsPrincipal desde la cookie al iniciar el circuito. Acá solo exponemos
/// helpers (UsuarioActual / Principal) para PermissionService y el layout.
/// </summary>
public class NorturAuthStateProvider : ServerAuthenticationStateProvider
{
    private ClaimsPrincipal _current = new(new ClaimsIdentity());

    public string? UsuarioActual =>
        _current.Identity?.IsAuthenticated == true ? _current.Identity!.Name : null;

    /// <summary>Principal vigente — lo lee PermissionService para chequear claims.</summary>
    public ClaimsPrincipal Principal => _current;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var state = await base.GetAuthenticationStateAsync();
        _current = state.User;   // cachea para los accesos síncronos de PermissionService
        return state;
    }

    /// <summary>
    /// Chequeo autoritativo y asíncrono usado por el guard de las páginas: fuerza
    /// la lectura del estado (cookie) antes de decidir, sin depender del orden de
    /// montaje de componentes. Refresca el cache de Principal de paso.
    /// </summary>
    public async Task<bool> EstaAutenticadoAsync()
    {
        var state = await GetAuthenticationStateAsync();
        return state.User.Identity?.IsAuthenticated == true;
    }

    /// <summary>
    /// Lo llama el endpoint de login tras SignInAsync para que el circuito refleje
    /// la nueva identidad sin esperar a la próxima request. El principal real ya
    /// viene de la cookie; esto solo notifica a los componentes.
    /// </summary>
    public void NotificarCambio(ClaimsPrincipal principal)
    {
        _current = principal;
        SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
    }
}

/// <summary>
/// Construye los claims NORTUR (un claim por letra de `acceso`, `nivel`,
/// `operador`) a partir del resultado del login. Se usa tanto en el endpoint de
/// login (para la cookie) como en cualquier lugar que necesite el principal.
/// Espejo del login.scx del FoxPro.
/// </summary>
public static class NorturIdentityFactory
{
    public const string AuthScheme = "NorturAuth";

    public static ClaimsPrincipal Crear(
        string usuario, string acceso, string nivel, bool operador, Guid? sesion = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, usuario) };

        foreach (var letra in (acceso ?? "").ToUpperInvariant().Where(char.IsLetter).Distinct())
            claims.Add(new Claim(NorturClaims.Modulo, letra.ToString()));

        claims.Add(new Claim(NorturClaims.Nivel, nivel ?? ""));

        if (operador)
            claims.Add(new Claim(NorturClaims.Operador, "1"));

        // GUID de la sesión (para cruzar el logout con su LOGIN en la bitácora usuarios_logs).
        if (sesion is not null)
            claims.Add(new Claim(NorturClaims.Sesion, sesion.Value.ToString()));

        var identity = new ClaimsIdentity(claims, AuthScheme);
        return new ClaimsPrincipal(identity);
    }
}
