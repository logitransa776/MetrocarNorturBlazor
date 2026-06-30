namespace MetroCarSysBlazor.Services;

/// <summary>
/// Resuelve la ruta del archivo adjunto de un viaje (submenú Tráfico → Ver Datos Extras →
/// Ver Adjunto). En FoxPro `viaje.file` guarda rutas tipo `O:\METROCARSYS\ADJUNTOS\x.pdf`
/// (unidad de red mapeada en las PCs del FoxPro). El servidor de Blazor no ve la unidad `O:`,
/// así que se reemplaza el prefijo configurado (Adjuntos:PrefijoFoxPro) por la carpeta REAL
/// que el servidor sí puede leer (Adjuntos:BasePath, una UNC del recurso compartido).
///
/// Seguridad: tras mapear, se valida que la ruta resultante quede DENTRO de BasePath (anti
/// path-traversal: una ruta con `..` o absoluta distinta se rechaza). Solo lectura.
/// </summary>
public class AdjuntoService
{
    private readonly string _prefijoFoxPro;
    private readonly string _basePath;

    public AdjuntoService(IConfiguration config)
    {
        _prefijoFoxPro = (config["Adjuntos:PrefijoFoxPro"] ?? "").Trim();
        _basePath = (config["Adjuntos:BasePath"] ?? "").Trim();
    }

    /// <summary>True si la carpeta base de adjuntos está configurada (BasePath no vacío).</summary>
    public bool Configurado => !string.IsNullOrWhiteSpace(_basePath);

    /// <summary>
    /// Resultado de resolver un adjunto: o bien la ruta física servible, o un motivo de fallo
    /// legible para mostrar al usuario (réplica del MessageBox del FoxPro).
    /// </summary>
    public sealed record Resultado(bool Ok, string? RutaFisica, string? NombreArchivo, string? Error);

    /// <summary>
    /// Resuelve la ruta cruda de la base (ej `O:\METROCARSYS\ADJUNTOS\x.pdf`) a un archivo
    /// físico servible, validando configuración, mapeo, contención bajo BasePath y existencia.
    /// </summary>
    public Resultado Resolver(string rutaFoxPro)
    {
        if (string.IsNullOrWhiteSpace(rutaFoxPro))
            return new(false, null, null, "Este viaje no tiene ningún archivo adjunto.");

        if (!Configurado)
            return new(false, null, null,
                "La carpeta de adjuntos no está configurada en el servidor. " +
                "Completá la ruta en appsettings.json (clave Adjuntos:BasePath) con la UNC del recurso compartido.");

        var ruta = rutaFoxPro.Trim();

        // Reemplazo del prefijo del FoxPro por la base real. Si el prefijo configurado no
        // aparece, tomamos solo el nombre de archivo y lo colgamos de BasePath (mejor que fallar).
        string relativo;
        if (!string.IsNullOrEmpty(_prefijoFoxPro) &&
            ruta.StartsWith(_prefijoFoxPro, StringComparison.OrdinalIgnoreCase))
        {
            relativo = ruta.Substring(_prefijoFoxPro.Length);
        }
        else
        {
            // Sin prefijo conocido: quedarnos con el nombre de archivo (último segmento).
            var idx = ruta.LastIndexOfAny(new[] { '\\', '/' });
            relativo = idx >= 0 ? ruta.Substring(idx + 1) : ruta;
        }

        // Normalizar separadores y limpiar el inicio para que Path.Combine no lo trate como absoluto.
        relativo = relativo.Replace('/', '\\').TrimStart('\\');

        string fisica;
        string baseCompleta;
        try
        {
            baseCompleta = Path.GetFullPath(_basePath);
            fisica = Path.GetFullPath(Path.Combine(baseCompleta, relativo));
        }
        catch
        {
            return new(false, null, null, "La ruta del archivo adjunto no es válida.");
        }

        // Anti path-traversal: el archivo resuelto DEBE quedar dentro de la carpeta base.
        var baseConSep = baseCompleta.EndsWith(Path.DirectorySeparatorChar)
            ? baseCompleta
            : baseCompleta + Path.DirectorySeparatorChar;
        if (!fisica.StartsWith(baseConSep, StringComparison.OrdinalIgnoreCase))
            return new(false, null, null, "La ruta del archivo adjunto está fuera de la carpeta permitida.");

        if (!File.Exists(fisica))
            return new(false, null, null,
                $"No se encontró el archivo adjunto en el servidor.\nRuta original: {rutaFoxPro}");

        return new(true, fisica, Path.GetFileName(fisica), null);
    }

    /// <summary>Content-Type básico por extensión (suficiente para PDF/imágenes/office comunes).</summary>
    public static string ContentType(string nombreArchivo) =>
        Path.GetExtension(nombreArchivo).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".txt" => "text/plain",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };
}
