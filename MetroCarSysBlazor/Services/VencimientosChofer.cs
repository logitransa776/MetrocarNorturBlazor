namespace MetroCarSysBlazor.Services;

/// <summary>Estado de UN documento de un chofer frente a la ventana de aviso.</summary>
public enum DocEstado
{
    /// <summary>El documento no le corresponde a este chofer (AEP sin fecha).</summary>
    NoAplica = 0,
    AlDia = 1,
    PorVencer = 2,
    Vencido = 3
}

/// <summary>
/// Un chofer con actividad en el período, con su documentación ya clasificada. Es lo que
/// consumen el aviso de <c>ViajesPorChofer</c>, el modal <c>ChoferesVencimientosDialog</c> y
/// <c>ExcelExportService.ChoferesVencimientos</c> — una sola clasificación para los tres, así
/// el Excel nunca discrepa de lo que se ve en pantalla.
/// </summary>
/// <param name="Doc">Fila cruda de <c>chofer</c> (misma que usa la Agenda de Vencimientos).</param>
/// <param name="Viajes">Viajes del chofer en el período filtrado del informe.</param>
/// <param name="Peor">El estado más grave de sus tres documentos.</param>
/// <param name="DocCritico">Qué documento manda ("Registro" / "CNRT" / "AEP"); vacío si está al día.</param>
/// <param name="FechaCritica">Vencimiento de ese documento; null = sin fecha cargada.</param>
/// <param name="DiasCritico">Días hasta ese vencimiento (negativo = ya venció); null si no hay fecha.</param>
public record ChoferDocVista(
    ChoferVtoRow Doc,
    int Viajes,
    DocEstado Registro,
    DocEstado Cnrt,
    DocEstado Aep,
    DocEstado Peor,
    string DocCritico,
    DateOnly? FechaCritica,
    int? DiasCritico)
{
    public string IdChofer => Doc.IdChofer;
    public string Nombre => Doc.Nombre;

    /// <summary>Chofer de fletero (contratado) vs. propio de NORTUR.
    /// ⚠ <c>chofer.fletero</c> NUNCA viene vacío: los propios llevan literalmente "NORTUR"
    /// (92 de 254 activos). Preguntar solo por "no vacío" marca a todo el padrón como
    /// contratado.</summary>
    public bool EsContratado =>
        Doc.Fletero.Length > 0 && !Doc.Fletero.Equals("NORTUR", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hay algo para hacer con este chofer (vencido o por vencer).</summary>
    public bool Alerta => Peor is DocEstado.Vencido or DocEstado.PorVencer;
}

/// <summary>
/// Reglas de vencimiento de la documentación de choferes (registro, CNRT, AEP).
///
/// <para><b>Regla del FoxPro (aviso_agenda.prg):</b> un documento <b>sin fecha cargada cuenta
/// como vencido</b>. Se respeta para <b>registro y CNRT</b>, que son obligatorios para todos.</para>
///
/// <para><b>Excepción del AEP (20/08/2026):</b> la habilitación aeroportuaria la tienen solo los
/// choferes que entran al aeropuerto — en la base, <b>227 de 254 choferes activos la tienen en
/// blanco</b>. Tratar ese blanco como "vencido" (lo que hace la query
/// <c>GetChoferesPorVencerAsync</c> con su <c>ISNULL(...,'1900-01-01')</c>) marcaba en rojo a
/// casi todo el padrón y era la causa de que el aviso dijera "101 choferes" y el detalle
/// mostrara la mitad de los renglones vacíos. Acá el AEP sin fecha es <b>NoAplica</b>: no
/// alerta y se muestra como "—".</para>
/// </summary>
public static class VencimientosChofer
{
    /// <summary>Clasifica un documento. <paramref name="sinFechaEsVencido"/> = regla FoxPro
    /// (registro y CNRT); en false, la falta de fecha significa "no le corresponde" (AEP).</summary>
    public static DocEstado Estado(DateOnly? f, DateOnly hoy, int dias, bool sinFechaEsVencido)
    {
        if (f is null) return sinFechaEsVencido ? DocEstado.Vencido : DocEstado.NoAplica;
        if (f.Value <= hoy) return DocEstado.Vencido;
        return f.Value <= hoy.AddDays(dias) ? DocEstado.PorVencer : DocEstado.AlDia;
    }

    /// <summary>Clasifica los tres documentos de un chofer y resuelve cuál es el crítico:
    /// el de peor estado y, a igual estado, el que vence antes (sin fecha = lo más urgente).</summary>
    public static ChoferDocVista Clasificar(ChoferVtoRow c, int viajes, DateOnly hoy, int dias)
    {
        var reg = Estado(c.RegistroVto, hoy, dias, sinFechaEsVencido: true);
        var cnrt = Estado(c.CnrtVto, hoy, dias, sinFechaEsVencido: true);
        var aep = Estado(c.AepVto, hoy, dias, sinFechaEsVencido: false);

        var docs = new (string Nombre, DocEstado Est, DateOnly? Fecha)[]
        {
            ("Registro", reg,  c.RegistroVto),
            ("CNRT",     cnrt, c.CnrtVto),
            ("AEP",      aep,  c.AepVto),
        };

        var critico = docs
            .Where(d => d.Est is DocEstado.Vencido or DocEstado.PorVencer)
            .OrderByDescending(d => d.Est)
            .ThenBy(d => d.Fecha ?? DateOnly.MinValue)
            .FirstOrDefault();

        var peor = critico.Nombre is null
            ? (docs.Any(d => d.Est == DocEstado.AlDia) ? DocEstado.AlDia : DocEstado.NoAplica)
            : critico.Est;

        int? diasCritico = critico.Fecha is DateOnly f ? f.DayNumber - hoy.DayNumber : null;

        return new ChoferDocVista(c, viajes, reg, cnrt, aep,
            peor, critico.Nombre ?? "", critico.Fecha, diasCritico);
    }

    /// <summary>Orden operativo: primero lo vencido, dentro de cada grupo lo que venció/vence
    /// antes, y a igualdad por nombre. Es el orden por defecto de la grilla y del Excel.</summary>
    public static IEnumerable<ChoferDocVista> OrdenarPorUrgencia(IEnumerable<ChoferDocVista> filas) =>
        filas.OrderByDescending(f => f.Peor)
             .ThenBy(f => f.FechaCritica ?? DateOnly.MinValue)
             .ThenBy(f => f.Nombre);

    /// <summary>Texto corto del estado de un documento, para grillas y Excel.</summary>
    public static string Texto(DocEstado est, DateOnly? f) => est switch
    {
        DocEstado.NoAplica => "—",
        _ when f is null => "sin fecha",
        _ => f!.Value.ToString("dd/MM/yyyy")
    };
}
