namespace MetroCarSysBlazor.Services;

/// <summary>
/// Resuelve el logo de la empresa (Parámetros Empresa → campo <c>parametro.logo</c>).
/// En FoxPro esa columna guarda una ruta con unidad de red mapeada
/// (<c>O:\METROCARSYS\GRAPHICS\LOGO\NORTUR-LOGO-AL-30.JPG</c>) que el servidor de Blazor
/// NO ve. Mismo problema y misma solución que los adjuntos de viaje: se reemplaza el
/// prefijo configurado (<c>Logo:PrefijoFoxPro</c>) por la carpeta REAL accesible por el
/// servidor (<c>Logo:BasePath</c>, una UNC del recurso compartido).
///
/// Se guarda SIEMPRE la ruta tal como la escribe el FoxPro, para no romperle el login ni
/// los reportes al sistema viejo mientras convivan los dos (decisión 12/08/2026).
/// Solo lectura: acá no se sube ni se escribe ningún archivo.
/// </summary>
public class LogoEmpresaService
{
    private readonly string _prefijoFoxPro;
    private readonly string _basePath;

    public LogoEmpresaService(IConfiguration config)
    {
        _prefijoFoxPro = (config["Logo:PrefijoFoxPro"] ?? "").Trim();
        _basePath = (config["Logo:BasePath"] ?? "").Trim();
    }

    /// <summary>True si la carpeta base del logo está configurada (BasePath no vacío).</summary>
    public bool Configurado => !string.IsNullOrWhiteSpace(_basePath);

    /// <summary>
    /// Resuelve la ruta cruda de <c>parametro.logo</c> a un archivo físico servible.
    /// Devuelve el motivo del fallo en texto legible (para mostrarlo en la pantalla).
    /// </summary>
    public AdjuntoService.Resultado Resolver(string rutaFoxPro)
    {
        if (string.IsNullOrWhiteSpace(rutaFoxPro))
            return new(false, null, null, "No hay ningún logo configurado.");

        if (!Configurado)
            return new(false, null, null,
                "La carpeta del logo no está configurada en el servidor. " +
                "Completá la ruta en appsettings.json (clave Logo:BasePath) con la UNC del recurso compartido.");

        return AdjuntoService.MapearRuta(rutaFoxPro, _prefijoFoxPro, _basePath, "logo de la empresa");
    }
}
