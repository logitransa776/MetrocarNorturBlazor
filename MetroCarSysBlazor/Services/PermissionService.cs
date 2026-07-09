using System.Security.Claims;

namespace MetroCarSysBlazor.Services;

/// <summary>Claim types propios — espejo de cAcceso / cNivel / operador del FoxPro.</summary>
public static class NorturClaims
{
    public const string Modulo   = "nortur:modulo";
    public const string Nivel    = "nortur:nivel";
    public const string Operador = "nortur:operador";
    public const string Sesion   = "nortur:sesion";   // GUID de la sesión (bitácora usuarios_logs)
}

/// <summary>
/// Permisos del usuario logueado — equivalente a los chequeos `"X" $ cAcceso` y
/// `"2" $ cNivel` del Metrocar. Mapa completo de letras: skill seguridad-nortur
/// y docs/PlanoFoxPro/sistema/USUARIO_ACCESOS.md.
/// </summary>
public interface IPermissionService
{
    /// <summary>¿Tiene el módulo? (R=Reservas, T=Tráfico, V=Vehículos, F=Facturación,
    /// L=Taller, M=Combustible, A=ABM, U=Utilitarios, S=Usuarios, X=Tablero...)</summary>
    bool Tiene(char modulo);

    /// <summary>Operación ABM por dígito de `nivel`: 2=alta, 3=modifica, 4=baja.</summary>
    bool TieneABM(int operacion);

    /// <summary>Operador de la mesa de tráfico (campo `usuario.operador`).</summary>
    bool EsOperador { get; }

    /// <summary>Primera página de reportes que el usuario puede ver, o null si ninguna.</summary>
    string? DestinoInicial();
}

public class PermissionService : IPermissionService
{
    private readonly NorturAuthStateProvider _auth;

    public PermissionService(NorturAuthStateProvider auth) => _auth = auth;

    public bool Tiene(char modulo) =>
        _auth.Principal.HasClaim(NorturClaims.Modulo, char.ToUpperInvariant(modulo).ToString());

    public bool TieneABM(int operacion) =>
        (_auth.Principal.FindFirst(NorturClaims.Nivel)?.Value ?? "")
            .Contains((char)('0' + operacion));

    public bool EsOperador =>
        _auth.Principal.HasClaim(NorturClaims.Operador, "1");

    public string? DestinoInicial() =>
        Tiene('T') ? "/planilla-trafico"
        : Tiene('R') ? "/reservas-fecha-servicio"
        : null;
}
