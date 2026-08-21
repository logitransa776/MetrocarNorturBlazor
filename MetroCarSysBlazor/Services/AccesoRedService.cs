using System.Net;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Control de acceso condicional por ORIGEN DE RED (LAN vs Internet). Decide, en el login,
/// si un usuario puede ingresar según de dónde viene la conexión:
///
///   • IP dentro de un rango interno confiable (RedInterna:RangosConfiables) → SIEMPRE entra.
///   • IP externa (Internet) → solo si el usuario tiene el permiso de acceso remoto (letra 'I').
///   • IP indeterminada → política fail-safe (RedInterna:SiNoSePuedeDeterminarLaIp).
///
/// Se activa solo con <c>RedInterna:Activo = true</c> (andamiaje: mientras la app es solo-LAN
/// el control está dormido y no bloquea a nadie). La fiabilidad depende de que el servidor vea
/// la IP REAL del cliente — por eso el proyecto usa ForwardedHeaders con proxies conocidos
/// (Program.cs): un cliente de Internet no puede falsificar su IP con un header.
///
/// Singleton: parsea los rangos CIDR una sola vez al arrancar.
/// </summary>
public sealed class AccesoRedService
{
    private readonly List<(IPAddress Net, int Prefix)> _rangos = new();
    private readonly bool _denegarSiIndeterminada;

    /// <summary>El control está encendido (RedInterna:Activo). Si es false, todo ingreso se permite.</summary>
    public bool Activo { get; }

    public AccesoRedService(IConfiguration config, ILogger<AccesoRedService> log)
    {
        var sec = config.GetSection("RedInterna");
        Activo = sec.GetValue("Activo", false);

        // Fail-safe por defecto: si no se puede determinar la IP, DENEGAR (a menos que se
        // configure explícitamente "permitir"). Es la opción segura.
        var pol = sec.GetValue<string>("SiNoSePuedeDeterminarLaIp") ?? "denegar";
        _denegarSiIndeterminada = !string.Equals(pol, "permitir", StringComparison.OrdinalIgnoreCase);

        foreach (var cidr in sec.GetSection("RangosConfiables").Get<string[]>() ?? Array.Empty<string>())
        {
            if (TryParseCidr(cidr, out var net, out var prefix))
                _rangos.Add((net, prefix));
            else
                log.LogWarning("RedInterna: rango CIDR inválido, se ignora: {Cidr}", cidr);
        }

        if (Activo)
            log.LogInformation("Control de acceso por red ACTIVO — {N} rango(s) interno(s), fail-safe={Pol}.",
                _rangos.Count, _denegarSiIndeterminada ? "denegar" : "permitir");
    }

    /// <summary>
    /// ¿Se permite el ingreso desde <paramref name="ip"/> a un usuario con permiso
    /// <paramref name="accesoWeb"/>? Regla: con permiso web entra de cualquier origen; sin
    /// permiso web, solo desde la red interna. IP ilegible → política fail-safe.
    /// </summary>
    public bool PermitirIngreso(string? ip, bool accesoWeb)
    {
        if (!Activo) return true;              // control dormido (solo-LAN)
        if (accesoWeb) return true;            // habilitado para cualquier origen
        if (EsInterna(ip)) return true;        // sin permiso web, pero viene de adentro
        if (Normalizar(ip) is null)            // no se pudo determinar la IP
            return !_denegarSiIndeterminada;
        return false;                          // IP externa clara + sin permiso web → denegar
    }

    /// <summary>¿La IP cae dentro de algún rango interno confiable?</summary>
    public bool EsInterna(string? ip)
    {
        var a = Normalizar(ip);
        if (a is null) return false;
        foreach (var (net, prefix) in _rangos)
            if (EnRango(a, net, prefix)) return true;
        return false;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    // Parsea la IP y normaliza IPv4-mapped-IPv6 (::ffff:192.168.0.8) a IPv4 puro, para que
    // "192.168.0.8" matchee aunque Kestrel la reciba como IPv6 mapeada (socket dual-stack).
    private static IPAddress? Normalizar(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        if (!IPAddress.TryParse(ip.Trim(), out var a)) return null;
        return a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
    }

    private static bool TryParseCidr(string? cidr, out IPAddress net, out int prefix)
    {
        net = IPAddress.None; prefix = 0;
        if (string.IsNullOrWhiteSpace(cidr)) return false;
        var parts = cidr.Trim().Split('/');
        if (!IPAddress.TryParse(parts[0], out var a)) return false;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();

        int maxBits = a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        // Sin "/N" → IP suelta = host único (/32 o /128).
        prefix = maxBits;
        if (parts.Length > 1 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > maxBits))
            return false;

        net = a;
        return true;
    }

    // ¿ip pertenece a net/prefix? Compara los primeros `prefix` bits (por bytes, con máscara).
    private static bool EnRango(IPAddress ip, IPAddress net, int prefix)
    {
        if (ip.AddressFamily != net.AddressFamily) return false;
        var ipB = ip.GetAddressBytes();
        var netB = net.GetAddressBytes();
        if (ipB.Length != netB.Length) return false;

        int bits = prefix;
        for (int i = 0; i < ipB.Length && bits > 0; i++)
        {
            int take = Math.Min(8, bits);
            int mask = 0xFF << (8 - take) & 0xFF;
            if ((ipB[i] & mask) != (netB[i] & mask)) return false;
            bits -= 8;
        }
        return true;
    }
}
