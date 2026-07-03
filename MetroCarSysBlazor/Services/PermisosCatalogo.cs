namespace MetroCarSysBlazor.Services;

/// <summary>
/// Catálogo de los 16 módulos/permisos del string <c>usuario.acceso</c> — la fuente única de
/// verdad para el ABM de Usuarios. El ORDEN de esta lista ES el orden de concatenación del
/// string en el FoxPro (<c>usuario_abm.scx</c>): S R T C D V L F A E U B H X N M. Al grabar,
/// el string se arma recorriendo esta lista en orden y agregando la letra de cada checkbox tildado.
/// </summary>
public static class PermisosCatalogo
{
    /// <summary>Un permiso: letra del string acceso, etiqueta de la UI (igual al FoxPro) y reglas.</summary>
    public record Permiso(
        char Letra,
        string Etiqueta,
        string Descripcion,
        bool DependeDeTrafico = false,   // 'C' (Avisos de chequeos): solo tiene sentido con 'T'
        bool SoloSupervisor = false);    // 'X' (Tablero de comando): solo el SUPERVISOR puede otorgarlo

    /// <summary>Los 16 permisos EN EL ORDEN de concatenación del string acceso (no reordenar).</summary>
    public static readonly IReadOnlyList<Permiso> Todos = new List<Permiso>
    {
        new('S', "Usuarios y Password",     "ABM de usuarios (Sistema → Accesos)"),
        new('R', "Reservas",                "Menú Reservas completo"),
        new('T', "Trafico",                 "Menú Tráfico completo"),
        new('C', "Avisos de chequeos",      "Timer de avisos en Tráfico (requiere Tráfico)", DependeDeTrafico: true),
        new('D', "Diagramador",             "Funciones de diagramador en Tráfico"),
        new('V', "Vehiculos",               "Menú Vehículos y Choferes + chequeo de vencimientos"),
        new('L', "Taller",                  "Menú Taller"),
        new('F', "Facturacion",             "Menú Facturación + visibilidad de precios e importes"),
        new('A', "ABM del Sistema",         "Menú ABM del sistema (catálogos)"),
        new('E', "Estadisticas",            "Reservado (sin uso en el sistema actual)"),
        new('U', "Utilitarios",             "Menú Utilitarios"),
        new('B', "Back-Up",                 "Usuario de servicio: abre el Backup al ingresar"),
        new('H', "Scheduler",               "Utilitarios → Scheduler"),
        new('X', "Tablero de comando",      "Sistema → Tablero de Control (solo SUPERVISOR)", SoloSupervisor: true),
        new('N', "Cuentas Corrientes",      "Facturación → Cuentas Corrientes"),
        new('M', "Combustible y Consumos",  "Menú Combustible"),
    };

    /// <summary>¿El string acceso incluye esta letra? (equivalente a "letra" $ acceso en FoxPro).</summary>
    public static bool Tiene(string? acceso, char letra) =>
        (acceso ?? "").ToUpperInvariant().Contains(char.ToUpperInvariant(letra));

    /// <summary>
    /// Arma el string <c>acceso</c> a partir del set de letras tildadas, respetando el orden fijo
    /// del catálogo. Filtra cualquier letra desconocida. Es la operación inversa a decodificar.
    /// </summary>
    public static string Construir(IEnumerable<char> letrasTildadas)
    {
        var set = new HashSet<char>(letrasTildadas.Select(char.ToUpperInvariant));
        return string.Concat(Todos.Where(p => set.Contains(p.Letra)).Select(p => p.Letra));
    }
}
