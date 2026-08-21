using MudBlazor;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Módulos bajo los que se agrupan los informes en el hub /informes.
/// Cada uno mapea a una letra de `usuario.acceso` (ver skill seguridad-nortur).
/// </summary>
public enum ModuloInforme
{
    Reservas,
    Trafico,
    Flota,
    Facturacion,
    Combustible,
    Sistema,

    /// <summary>
    /// Módulo VIRTUAL (11/08/2026). No es un módulo del sistema: es la sala de espera donde
    /// viven los informes marcados con <c>EnDesarrollo: true</c> hasta que el cliente los
    /// aprueba. Ningún <see cref="InformeItem"/> declara este módulo — cada informe declara
    /// SIEMPRE su módulo de destino final, y el flag lo desvía acá mientras se cocina.
    /// </summary>
    EnDesarrollo,
}

/// <summary>Disponible = la pantalla existe. Proximamente = está en el FoxPro y falta migrar.</summary>
public enum EstadoInforme
{
    Disponible,
    Proximamente,
}

/// <summary>Metadatos de presentación de un módulo (nombre, ícono, color, letra de acceso).</summary>
public sealed record ModuloInfo(
    ModuloInforme Modulo,
    string Nombre,
    char Permiso,
    string Icono,
    string Color);

/// <summary>
/// Una entrada del catálogo de informes. `Pregunta` es lo que se muestra al usuario final
/// debajo del título: la pregunta de negocio que el informe contesta, no el nombre técnico.
/// </summary>
/// <param name="Ruta">Sin barra inicial ("reservas-fecha-servicio"). Es también la clave del título de la barra.</param>
/// <param name="Etiquetas">Términos extra para el buscador (sinónimos que el usuario tipearía).</param>
/// <param name="EnDesarrollo">
/// <c>true</c> = el informe está en pruebas: se muestra SOLO bajo "En desarrollo" (permiso
/// <c>S</c>), nunca en su módulo de destino, aunque el usuario tenga la letra de ese módulo.
/// Para graduarlo cuando el cliente lo aprueba se borra este flag y nada más: el informe cae
/// solo en el módulo que ya declara en <paramref name="Modulo"/>.
/// </param>
public sealed record InformeItem(
    string Ruta,
    string Titulo,
    string Pregunta,
    ModuloInforme Modulo,
    string Icono,
    EstadoInforme Estado = EstadoInforme.Disponible,
    string Etiquetas = "",
    bool EnDesarrollo = false);

/// <summary>
/// FUENTE ÚNICA DE VERDAD de los informes del sistema.
///
/// De acá se alimentan: el hub /informes, la sección "Informes" del drawer y el
/// título de la barra superior (MainLayout). Un informe nuevo se da de alta UNA vez,
/// acá, y aparece solo en los tres lados con el permiso que corresponde.
///
/// Criterio de qué entra (decidido 10/08/2026): es informe si el usuario lo abre para
/// responder una pregunta sobre un rango de fechas que él elige y llevarse el resultado
/// (Excel / impresión). Si lo abre para CAMBIAR algo, es pantalla operativa y va solo en
/// su módulo (Odómetros, Siniestros, Saldos de Estaciones, Viáticos, ABMs y catálogos).
///
/// Desde el 18/08/2026 el hub /informes (y la sección "Informes" del drawer) es la
/// ÚNICA entrada para un informe: los links viejos que vivían duplicados dentro de
/// cada módulo del drawer se eliminaron para no tener el mismo informe en dos lados.
/// </summary>
public static class InformesCatalogo
{
    /// <summary>Orden de presentación de los módulos en el hub y en el drawer.</summary>
    public static readonly IReadOnlyList<ModuloInfo> Modulos = new List<ModuloInfo>
    {
        // ⚠ Estos hex son el CÓDIGO DE COLOR del sistema (11/08/2026): no pintan solo las
        // tarjetas del hub, también el menú lateral y el filete de la barra de título. Tocar
        // uno acá lo cambia en los tres lugares a la vez — que es exactamente la idea.
        // Dos hex tienen historia y no conviene revertirlos sin leer esto:
        //   · Reservas era #003AA0 = el azul institucional de la marca (header, logo, footer).
        //     En el menú no se leía como "módulo Reservas" sino como "el color del sistema".
        //   · Combustible era #B45309, demasiado cerca del #F99410 de Tráfico de reojo.
        //     Corrido a terracota para que los dos naranjas no se confundan.
        new(ModuloInforme.Reservas,    "Reservas",     'R', Icons.Material.Filled.EventNote,          "#2563EB"),
        new(ModuloInforme.Trafico,     "Tráfico",      'T', Icons.Material.Filled.DirectionsBus,      "#F99410"),
        new(ModuloInforme.Flota,       "Flota",        'V', Icons.Material.Filled.LocalShipping,      "#16A34A"),
        new(ModuloInforme.Facturacion, "Facturación",  'F', Icons.Material.Filled.ReceiptLong,        "#0891B2"),
        new(ModuloInforme.Combustible, "Combustible",  'M', Icons.Material.Filled.LocalGasStation,    "#C2410C"),
        new(ModuloInforme.Sistema,     "Sistema",      'S', Icons.Material.Filled.AdminPanelSettings, "#7C3AED"),

        // Va ÚLTIMO a propósito: es la sala de espera, no un módulo del negocio. Permiso 'S'
        // (Usuarios y Password) para que los informes en prueba no lleguen a la operación
        // hasta que el cliente los apruebe.
        new(ModuloInforme.EnDesarrollo, "En desarrollo", 'S', Icons.Material.Filled.Science, "#64748B"),
    };

    /// <summary>Todos los informes del sistema, migrados y pendientes.</summary>
    public static readonly IReadOnlyList<InformeItem> Todos = new List<InformeItem>
    {
        // ── RESERVAS (letra R) ─────────────────────────────────────────────
        new("reservas-fecha-servicio",
            "Reservas por fecha y servicio",
            "¿Qué servicios mueven más reservas y pasajeros en el período?",
            ModuloInforme.Reservas,
            Icons.Material.Filled.CalendarMonth,
            Etiquetas: "servicios pax pasajeros volumen cancelaciones estados"),

        new("reservas-banda-horaria",
            "Reservas por fecha y banda horaria",
            "¿En qué franjas del día se concentra la demanda?",
            ModuloInforme.Reservas,
            Icons.Material.Filled.Schedule,
            Etiquetas: "horarios franja hora pico demanda turismo empresa"),

        new("reservas-por-cliente",
            "Reservas por cliente",
            "¿Qué clientes crecen o caen mes a mes?",
            ModuloInforme.Reservas,
            Icons.Material.Filled.Groups,
            Etiquetas: "clientes ranking tendencia variación cancelaciones motivos"),

        new("reservas-bandas-listado",
            "Bandas horarias (listado)",
            "El listado clásico de bandas horarias del Metrocar.",
            ModuloInforme.Reservas,
            Icons.Material.Filled.AccessTime,
            EstadoInforme.Proximamente,
            "bandas horarios listado"),

        // ── TRÁFICO (letra T) ──────────────────────────────────────────────
        new("francos-auditoria",
            "Auditoría de Francos",
            "¿Qué francos tomó cada chofer, día por día?",
            ModuloInforme.Trafico,
            Icons.Material.Filled.FactCheck,
            Etiquetas: "francos choferes matriz calendario descanso"),

        new("lista-pasajeros",
            "Lista de pasajeros",
            "¿Quiénes viajan en cada servicio?",
            ModuloInforme.Trafico,
            Icons.Material.Filled.People,
            Etiquetas: "pasajeros nómina viaje documento"),

        // El libro de guardia de la mesa. Entra al hub porque es lo que el criterio del catálogo
        // llama informe: se abre para consultar un rango de fechas y llevarse el resultado.
        // (Su botonera de alta/baja/modificación existe pero está en andamiaje: el día que se
        // active la escritura habrá que revisar si sigue siendo "informe" o pasa a operativa.)
        new("libro-novedades",
            "Libro de Novedades",
            "¿Qué anotó cada turno de la mesa de tráfico?",
            ModuloInforme.Trafico,
            Icons.Material.Filled.MenuBook,
            Etiquetas: "novedades libro guardia turno mesa anotaciones incidencias operadores"),

        new("trafico-imprime",
            "Planilla de Tráfico (impresión)",
            "El parte diario de servicios para imprimir y repartir.",
            ModuloInforme.Trafico,
            Icons.Material.Filled.Print,
            EstadoInforme.Proximamente,
            "parte diario planilla imprimir despacho"),

        new("viatico-resumen",
            "Resumen de Viáticos por chofer",
            "¿Cuánto se le debe de viáticos a cada chofer en el período?",
            ModuloInforme.Trafico,
            Icons.Material.Filled.Payments,
            EstadoInforme.Proximamente,
            "viaticos choferes resumen liquidar"),

        // ── FLOTA / VEHÍCULOS Y CHOFERES (letra V) ─────────────────────────
        // Informe NUEVO (10/08/2026): no tiene gemelo en el FoxPro. Nació de una pregunta que
        // hoy solo se puede contestar abriendo la tabla `vehiculo` a mano.
        new("panel-flota",
            "Panel de Flota",
            "¿Qué unidades tengo, de qué tipo, y de cuáles me faltan?",
            ModuloInforme.Flota,
            Icons.Material.Filled.Dashboard,
            Etiquetas: "flota vehiculos unidades tipo cantidad butacas capacidad antiguedad titular composicion faltante ociosas",
            EnDesarrollo: true),

        // Informe NUEVO (11/08/2026) — EN DESARROLLO. Destino: Flota ('V').
        new("panel-tercerizacion",
            "Panel de Tercerización",
            "¿Cuánto de la operación se da a fleteros y cuánto podría absorber la flota propia?",
            ModuloInforme.Flota,
            Icons.Material.Filled.CompareArrows,
            Etiquetas: "tercerizacion terceros fleteros contratado propio nortur flota reparto "
                     + "subcontratacion proveedores vansq mvtravel neuquen teb",
            EnDesarrollo: true),

        new("viajes-por-chofer",
            "Viajes por chofer",
            "¿Cómo se reparte la carga de trabajo entre los choferes?",
            ModuloInforme.Flota,
            Icons.Material.Filled.Person,
            Etiquetas: "choferes viajes carga trabajo productividad"),

        new("km-unidades-servicios",
            "Km Unidades vs Servicios",
            "¿Cuántos kilómetros hizo cada unidad y en qué servicios?",
            ModuloInforme.Flota,
            Icons.Material.Filled.Speed,
            Etiquetas: "kilometros km unidades internos odometro servicios"),

        new("agenda-vencimientos",
            "Agenda de Vencimientos",
            "¿Qué vence pronto: VTV, matafuegos, registros, CNRT, AEP?",
            ModuloInforme.Flota,
            Icons.Material.Filled.EventBusy,
            Etiquetas: "vencimientos vtv matafuego registro cnrt aep alertas"),

        // NOTA: "Apercibimientos por chofer" (chofer_sancion.frx) NO figura acá a propósito:
        // la auditoría del 09/08/2026 midió `chofer_sancion` con 0 filas → módulo muerto,
        // propuesto para descarte. No prometer en el hub lo que no se va a migrar.

        // ── FACTURACIÓN (letra F) ──────────────────────────────────────────
        new("resumen-liquidaciones",
            "Resumen de Liquidaciones",
            "¿Cuánto se liquidó a cada cliente en el período?",
            ModuloInforme.Facturacion,
            Icons.Material.Filled.Summarize,
            Etiquetas: "liquidaciones clientes facturado importes resumen"),

        new("liquidacion-clientes",
            "Liquidación a Clientes",
            "El detalle valorizado, viaje por viaje, de un cliente.",
            ModuloInforme.Facturacion,
            Icons.Material.Filled.RequestQuote,
            Etiquetas: "liquidacion cliente detalle tarifas importes comprobante"),

        // Informe NUEVO (10/08/2026): no tiene gemelo en el FoxPro. Cruza padrón + actividad +
        // facturación, que hoy viven en tres pantallas que no se hablan entre sí.
        new("panel-clientes",
            "Panel de Clientes",
            "¿Qué clientes sostienen el negocio y cuánto vale cada uno?",
            ModuloInforme.Facturacion,
            Icons.Material.Filled.Diversity3,
            Etiquetas: "clientes cartera ranking facturacion concentracion pareto participacion tipo turismo personal segmento padron cuenta",
            EnDesarrollo: true),

        new("facturacion-estimada",
            "Liquidaciones estimadas",
            "¿Cuánto se va a facturar antes de cerrar la liquidación?",
            ModuloInforme.Facturacion,
            Icons.Material.Filled.TrendingUp,
            Etiquetas: "estimado proyección facturación pendiente previo"),

        // NOTA: "Liquidación a Fleteros" tampoco figura: última liquidación tipo PROVEEDOR
        // del 21/12/2023 (auditoría 09/08/2026) → módulo muerto, propuesto para descarte.

        new("liquidacion-choferes",
            "Liquidación a Choferes",
            "¿Cuánto hay que pagarle a cada chofer?",
            ModuloInforme.Facturacion,
            Icons.Material.Filled.AccountBalanceWallet,
            EstadoInforme.Proximamente,
            "choferes pagos liquidacion adelantos"),

        // ── COMBUSTIBLE (letra M) ──────────────────────────────────────────
        new("promedio-consumos",
            "Promedio de Consumos",
            "¿Cuántos litros cada 100 km hace cada unidad?",
            ModuloInforme.Combustible,
            Icons.Material.Filled.LocalGasStation,
            Etiquetas: "litros consumo promedio rendimiento unidades km"),

        new("consumo-mensual",
            "Consumo Mensual",
            "¿Cómo evoluciona el gasto de combustible mes a mes?",
            ModuloInforme.Combustible,
            Icons.Material.Filled.BarChart,
            Etiquetas: "consumo mensual gasto litros evolución tendencia"),

        new("control-cargas",
            "Control de cargas",
            "¿Qué unidades hace días que no cargan combustible?",
            ModuloInforme.Combustible,
            Icons.Material.Filled.ReportProblem,
            Etiquetas: "control cargas dias sin cargar alertas faltantes"),

        // ── SISTEMA (letra S) ──────────────────────────────────────────────
        // Informe NUEVO (11/08/2026) — EN DESARROLLO: se ve solo bajo "En desarrollo" hasta
        // que el cliente lo apruebe. Su destino ya está declarado (Sistema): al graduarlo se
        // borra `EnDesarrollo: true` y cae solo en su módulo.
        new("panel-operador",
            "Panel del Operador",
            "¿Quién carga el trabajo, cuándo, y quién modifica lo que cargó otro?",
            ModuloInforme.Sistema,
            Icons.Material.Filled.ManageAccounts,
            Etiquetas: "operador operadores carga altas reservas usuario auditoria quien cargo "
                     + "modificaciones concentracion productividad administrativo alta",
            EnDesarrollo: true),

        new("auditoria-accesos",
            "Auditoría de accesos",
            "¿Quién entró al sistema, desde dónde y cuándo?",
            ModuloInforme.Sistema,
            Icons.Material.Filled.History,
            Etiquetas: "accesos ingresos logins seguridad usuarios sesiones ip"),
    };

    private static readonly Dictionary<ModuloInforme, ModuloInfo> _porModulo =
        Modulos.ToDictionary(m => m.Modulo);

    /// <summary>Metadatos (nombre, ícono, color, letra) de un módulo.</summary>
    public static ModuloInfo Info(ModuloInforme modulo) => _porModulo[modulo];

    /// <summary>
    /// Color de lo que NO es un módulo del negocio: la sección "Informes" del menú (que es
    /// transversal — adentro viven los seis módulos con su propio color) y las pantallas
    /// sueltas sin módulo (el hub, la ayuda). Gris pizarra a propósito: un séptimo color
    /// fuerte le agregaría al usuario un código que no corresponde a ningún módulo real.
    /// </summary>
    public const string ColorNeutro = "#64748B";

    /// <summary>
    /// El hex del módulo, para pintar el menú lateral y el filete de la barra de título.
    /// <c>null</c> = sin módulo → devuelve <see cref="ColorNeutro"/>.
    /// </summary>
    public static string Color(ModuloInforme? modulo) =>
        modulo is null ? ColorNeutro : _porModulo[modulo.Value].Color;

    /// <summary>
    /// Letra de `usuario.acceso` que habilita el informe. Un informe EN DESARROLLO pide
    /// SIEMPRE la letra de "En desarrollo" ('S'), no la de su módulo de destino: mientras
    /// está en prueba no lo ve quien tiene el módulo, solo quien administra el sistema.
    /// </summary>
    public static char Permiso(this InformeItem informe) =>
        _porModulo[informe.EnDesarrollo ? ModuloInforme.EnDesarrollo : informe.Modulo].Permiso;

    /// <summary>Módulo bajo el que se AGRUPA hoy (la sala de espera si está en desarrollo).</summary>
    public static ModuloInforme ModuloVisible(this InformeItem informe) =>
        informe.EnDesarrollo ? ModuloInforme.EnDesarrollo : informe.Modulo;

    /// <summary>Ruta absoluta para navegar ("/reservas-fecha-servicio").</summary>
    public static string Href(this InformeItem informe) => "/" + informe.Ruta;

    /// <summary>
    /// ¿A qué módulo pertenece esta ruta, si es la de un informe? Lo usa el filete de color
    /// de la barra de título. Devuelve el módulo VISIBLE (un informe en desarrollo pinta con
    /// el neutro de su sala de espera, igual que en el menú y en el hub). <c>null</c> = la
    /// ruta no es un informe; el que llama resuelve por su cuenta.
    /// </summary>
    public static ModuloInforme? ModuloDeRuta(string ruta)
    {
        var informe = Todos.FirstOrDefault(i =>
            string.Equals(i.Ruta, ruta.Trim('/'), StringComparison.OrdinalIgnoreCase));
        return informe is null ? null : informe.ModuloVisible();
    }

    /// <summary>
    /// Informes que el usuario puede ver, en el orden de <see cref="Modulos"/>.
    /// Mismo criterio que el SKIP FOR del menú FoxPro: si no tiene la letra, el informe
    /// no existe para él (se oculta, no se deshabilita).
    /// </summary>
    public static IEnumerable<InformeItem> Visibles(IPermissionService permisos, bool incluirProximamente = true) =>
        Todos.Where(i => permisos.Tiene(i.Permiso())
                      && (incluirProximamente || i.Estado == EstadoInforme.Disponible))
             .OrderBy(i => _orden[i.ModuloVisible()]);

    /// <summary>
    /// ¿Este usuario puede abrir la ruta de este informe? Lo usa cada página como guarda,
    /// para que un informe en desarrollo no se abra tipeando la URL. Devuelve <c>true</c>
    /// para rutas que no son informes (no es asunto de este catálogo).
    /// </summary>
    public static bool PuedeVerRuta(IPermissionService permisos, string ruta)
    {
        var informe = Todos.FirstOrDefault(i =>
            string.Equals(i.Ruta, ruta.Trim('/'), StringComparison.OrdinalIgnoreCase));
        return informe is null || permisos.Tiene(informe.Permiso());
    }

    private static readonly Dictionary<ModuloInforme, int> _orden =
        Modulos.Select((m, i) => (m.Modulo, i)).ToDictionary(x => x.Modulo, x => x.i);

    /// <summary>Los informes visibles agrupados por módulo, sin módulos vacíos.</summary>
    public static IEnumerable<(ModuloInfo Modulo, List<InformeItem> Informes)> PorModulo(
        IPermissionService permisos, bool incluirProximamente = true)
    {
        foreach (var m in Modulos)
        {
            if (!permisos.Tiene(m.Permiso)) continue;

            var items = Todos
                .Where(i => i.ModuloVisible() == m.Modulo
                         && (incluirProximamente || i.Estado == EstadoInforme.Disponible))
                .ToList();

            if (items.Count > 0)
                yield return (m, items);
        }
    }

    /// <summary>¿El usuario tiene al menos un informe? (para mostrar u ocultar la sección del drawer).</summary>
    public static bool TieneAlguno(IPermissionService permisos) =>
        Todos.Any(i => permisos.Tiene(i.Permiso()));

    /// <summary>
    /// Título de cada informe indexado por ruta — lo consume el mapa de títulos de la
    /// barra superior para no repetir los nombres en dos archivos.
    /// </summary>
    public static IReadOnlyDictionary<string, string> TitulosPorRuta { get; } =
        Todos.Where(i => i.Estado == EstadoInforme.Disponible)
             .ToDictionary(i => i.Ruta, i => i.Titulo);

    /// <summary>
    /// ¿Coincide con lo que tipeó el usuario? Busca en título, pregunta y etiquetas,
    /// sin distinguir mayúsculas ni acentos (el usuario escribe "trafico", no "tráfico").
    /// </summary>
    public static bool Coincide(this InformeItem informe, string? termino)
    {
        if (string.IsNullOrWhiteSpace(termino)) return true;

        var aguja = Normalizar(termino);
        var pajar = Normalizar($"{informe.Titulo} {informe.Pregunta} {informe.Etiquetas}");

        // Cada palabra tipeada tiene que aparecer (AND) — "km chofer" filtra de verdad.
        return aguja.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .All(palabra => pajar.Contains(palabra, StringComparison.Ordinal));
    }

    private static string Normalizar(string texto)
    {
        var sb = new System.Text.StringBuilder(texto.Length);
        foreach (var c in texto.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
