namespace MetroCarSysBlazor.Services;

/// <summary>
/// Modelo del <b>Tablero de ocupación de flota</b> de la Planilla de Tráfico (31/07/2026).
/// Convierte los servicios que la grilla tiene a la vista en barras horarias por unidad.
///
/// <para>Vive acá, y no adentro del diálogo, porque lo consumen DOS lugares que tienen que
/// mostrar exactamente lo mismo: <c>OcupacionFlotaDialog</c> (pantalla) y
/// <c>ExcelExportService.OcupacionFlota</c> (el .xlsx con las celdas de 30 min). Una sola
/// regla de armado = la exportación nunca discrepa de lo que se ve.</para>
///
/// Reglas de negocio (elegidas por el usuario, 31/07/2026):
/// <list type="bullet">
/// <item>La fila es la <b>unidad que hizo el servicio</b>: U/As (asignada) → U/Cb (cabecera)
///       → U/Pr (programada), en ese orden de prioridad (19/08/2026) — ver
///       <see cref="PlanillaTraficoRow.UnidadCronograma"/>.</item>
/// <item>La barra va de <b>H.Ini</b> (<c>hs_inicio</c>) a <b>H.Fin</b> (<c>hs_fin_apr</c>).</item>
/// <item>Los servicios <b>sin cronograma</b> (S/C o vacío) SÍ se grafican (04/08/2026, pedido del
///       usuario: "mostrar todo, tenga o no tenga interno"): van juntos a una fila-pool ficticia
///       <see cref="UnidadSinAsignar"/> anclada arriba de todo. No suman horas ocupadas ni
///       conflictos — no hay unidad que ocupar y se pisan entre sí por definición.</item>
/// <item>El eje arranca a las 00:00 y se estira si algo cruza la medianoche, pero
///       <b>se corta a las 02:00</b> (<see cref="TopeEje"/>): más allá de esa hora los datos son
///       cierres mal cargados, no operación real. Lo que se pasa se dibuja cortado.</item>
/// <item>Dos servicios de la misma unidad que se pisan van en <b>sub-renglones</b> (pistas) y
///       se marcan como conflicto.</item>
/// </list>
/// </summary>
public static class OcupacionFlota
{
    /// <summary>Fin máximo del eje: 02:00 del día siguiente (26 h desde la medianoche).</summary>
    public const int TopeEje = 26 * 60;

    /// <summary>Piso del eje: el día completo, aunque el último servicio termine antes.</summary>
    public const int MinEje = 24 * 60;

    /// <summary>Duración a partir de la cual un servicio se considera dato sospechoso (12 h).</summary>
    private const int DuracionSospechosa = 12 * 60;

    /// <summary>Ancho mínimo que se le da a una barra sin duración utilizable (para que se vea).</summary>
    private const int DuracionMinima = 15;

    // Colores por tipo de servicio — mismo eje que los botones Empresa/Turismo/Nortur de la
    // barra superior de la planilla. Empresa y Turismo son los de la marca; Nortur (flota
    // propia diagramada sin interno concreto) va en verde azulado para no competir con ellos.
    public const string ColorEmpresa = "#003AA0";
    public const string ColorTurismo = "#F99410";
    public const string ColorNortur  = "#0E8F8F";

    /// <summary>Una barra del tablero: un servicio ubicado en el eje horario, en minutos desde las 00:00.</summary>
    /// <param name="Cortada">Terminaba después de las 02:00 y se dibujó recortada al tope del eje.</param>
    /// <param name="Dudosa">Duración fuera de rango (nula, negativa o &gt; 12 h): se marca en la UI.</param>
    public record Barra(PlanillaTraficoRow Fila, int Ini, int Fin, bool Cortada, bool Dudosa)
    {
        public int Duracion => Fin - Ini;
        public string Tipo => TipoDe(Fila);
        public string Color => ColorDe(Tipo);
    }

    /// <summary>Una unidad del tablero con sus servicios repartidos en pistas (sub-renglones).</summary>
    /// <param name="SinUnidad">
    /// Es la fila-pool de los servicios sin interno asignado, no una unidad real. Sus sub-renglones
    /// NO son conflictos de diagramación (son servicios pendientes que coinciden en horario) y sus
    /// minutos no cuentan como flota ocupada.
    /// </param>
    public record Unidad(string Nombre, List<List<Barra>> Pistas, int MinutosOcupados, int Servicios, int Solapes,
                         bool SinUnidad = false)
    {
        public IEnumerable<Barra> Barras => Pistas.SelectMany(p => p);
        public double Horas => MinutosOcupados / 60.0;
    }

    /// <summary>El tablero completo, listo para dibujar.</summary>
    /// <param name="SinCronograma">Servicios sin interno asignado: se grafican en la fila-pool.</param>
    /// <param name="SinHorario">Servicios sin hora de inicio utilizable: esos sí quedan fuera.</param>
    public record Tablero(
        List<Unidad> Unidades,
        int FinEje,
        int SinCronograma,
        int SinHorario,
        int Solapes,
        int Servicios,
        bool MultiDia)
    {
        /// <summary>Lo único que quedó FUERA del tablero (los S/C ya no se excluyen).</summary>
        public int Excluidos => SinHorario;

        /// <summary>Unidades de verdad — sin contar la fila-pool de los servicios sin interno.</summary>
        public int UnidadesReales => Unidades.Count(u => !u.SinUnidad);

        /// <summary>Servicios dibujados en la fila-pool (0 si el día está todo asignado).</summary>
        public int ServiciosSinUnidad => Unidades.Where(u => u.SinUnidad).Sum(u => u.Servicios);

        public int Renglones => Unidades.Sum(u => u.Pistas.Count);
    }

    public const string OrdenUnidad = "unidad";
    public const string OrdenHoras  = "horas";

    /// <summary>Nombre de la fila-pool que agrupa los servicios sin interno asignado.</summary>
    public const string UnidadSinAsignar = "S/C";

    /// <summary>
    /// Recorta el tablero a las unidades que tienen algún solape, <b>con todos sus servicios</b>
    /// (no solo las barras que se pisan): para resolver un cruce hay que ver contra qué choca y
    /// qué huecos libres le quedan a esa unidad. Decisión del usuario, 01/08/2026.
    /// <para><see cref="Tablero.FinEje"/> se mantiene a propósito: el eje horario NO tiene que
    /// saltar al prender o apagar el filtro.</para>
    /// </summary>
    public static Tablero SoloConflictos(Tablero t)
    {
        // La fila-pool se cae sola de este filtro: tiene Solapes = 0 a propósito (sus
        // sub-renglones son servicios pendientes, no cruces de diagramación).
        var unidades = t.Unidades.Where(u => u.Solapes > 0).ToList();
        return t with
        {
            Unidades = unidades,
            Servicios = unidades.Sum(u => u.Servicios)
            // Solapes no cambia: todos los conflictos del día viven en estas unidades.
        };
    }

    /// <summary>
    /// Recorta el tablero a la fila-pool: solo los servicios que todavía no tienen interno
    /// asignado, para trabajar la asignación sin el ruido de la flota ya diagramada
    /// (04/08/2026, pedido del usuario).
    /// </summary>
    public static Tablero SoloSinUnidad(Tablero t)
    {
        var unidades = t.Unidades.Where(u => u.SinUnidad).ToList();
        return t with
        {
            Unidades = unidades,
            Servicios = unidades.Sum(u => u.Servicios),
            Solapes = 0
        };
    }

    /// <summary>
    /// Arma el tablero a partir de las filas visibles de la planilla.
    /// </summary>
    /// <param name="real">
    /// false = duración <b>programada</b> (H.Fin, <c>hs_fin_apr</c>) — la que siempre está cargada.
    /// true = duración <b>real</b> (H.Cie, <c>hs_fin</c>) con caída a H.Fin cuando el servicio
    /// todavía no terminó. Existe porque H.Fin es un valor por defecto de 2 h en ~30% de los
    /// servicios (medido el 03/06/2026: 106 de 352), y eso infla la ocupación.
    /// </param>
    public static Tablero Armar(IEnumerable<PlanillaTraficoRow> filas, bool real, string orden = OrdenUnidad)
    {
        var lista = filas as IList<PlanillaTraficoRow> ?? filas.ToList();

        var sinCronograma = 0;
        var sinHorario = 0;
        var porUnidad = new Dictionary<string, List<Barra>>(StringComparer.OrdinalIgnoreCase);
        var pool = new List<Barra>();

        foreach (var f in lista)
        {
            // Sin hora de inicio no hay dónde ubicarlo: eso sí queda fuera (y se avisa),
            // tenga o no interno asignado.
            if (f.HsInicio is null) { sinHorario++; continue; }

            var barra = Ubicar(f, real);
            if (barra is null) { sinHorario++; continue; }

            if (f.SinCronograma) { sinCronograma++; pool.Add(barra); continue; }

            if (!porUnidad.TryGetValue(barra.Fila.UnidadCronograma, out var bolsa))
                porUnidad[barra.Fila.UnidadCronograma] = bolsa = new List<Barra>();
            bolsa.Add(barra);
        }

        var unidades = porUnidad
            .Select(kv => ArmarUnidad(kv.Key, kv.Value))
            .ToList();

        unidades = orden == OrdenHoras
            ? unidades.OrderByDescending(u => u.MinutosOcupados).ThenBy(u => u.Nombre, StringComparer.Ordinal).ToList()
            : unidades.OrderBy(u => u.Nombre, StringComparer.Ordinal).ToList();

        // La fila-pool va SIEMPRE primera, en los dos órdenes: es lo que hay que resolver, no
        // un renglón más para ir a buscar. Sus minutos y solapes se ponen en 0 a propósito
        // (ver Unidad.SinUnidad) para no ensuciar "horas ocupadas" ni el contador de conflictos.
        if (pool.Count > 0)
            unidades.Insert(0, ArmarUnidad(UnidadSinAsignar, pool) with
            {
                MinutosOcupados = 0,
                Solapes = 0,
                SinUnidad = true
            });

        // Fin del eje: el día completo como piso, el último cierre redondeado a la media hora
        // de arriba como techo, y nunca más allá de las 02:00.
        var ultimo = unidades.SelectMany(u => u.Barras).Select(b => b.Fin).DefaultIfEmpty(MinEje).Max();
        var finEje = Math.Clamp((int)(Math.Ceiling(ultimo / 30.0) * 30), MinEje, TopeEje);

        return new Tablero(
            Unidades: unidades,
            FinEje: finEje,
            SinCronograma: sinCronograma,
            SinHorario: sinHorario,
            Solapes: unidades.Sum(u => u.Solapes),
            Servicios: unidades.Sum(u => u.Servicios),
            MultiDia: lista.Select(f => f.Fecha).Distinct().Count() > 1);
    }

    /// <summary>Ubica un servicio en el eje (minutos desde las 00:00 de SU fecha de reserva).</summary>
    private static Barra? Ubicar(PlanillaTraficoRow f, bool real)
    {
        // El offset se mide contra la medianoche de la fecha de la PROPIA fila (no de un día
        // fijo): así el tablero también funciona con un filtro server-side de varios días, que
        // superpone las jornadas en un mismo eje en vez de romperse.
        var cero = f.Fecha.ToDateTime(TimeOnly.MinValue);
        int? Off(DateTime? dt) => dt is null ? null : (int)Math.Round((dt.Value - cero).TotalMinutes);

        var ini = Off(f.HsInicio);
        if (ini is null) return null;

        // Programado = H.Fin (hs_fin_apr); Real = H.Cie (hs_fin) y, si el servicio no terminó
        // todavía, cae al programado. OJO con los nombres: HsFin = hs_fin = columna "H. Cie",
        // HsCierre = hs_fin_apr = columna "H. Fin" (así lo etiqueta el FoxPro).
        var fin = real
            ? Off(f.HsFin) ?? Off(f.HsCierre)
            : Off(f.HsCierre) ?? Off(f.HsFin);

        var dudosa = false;

        // Inicio fuera de la ventana del día (dato corrupto de la réplica): se acota y se marca.
        var i = ini.Value;
        if (i < 0 || i > TopeEje - DuracionMinima) { dudosa = true; i = Math.Clamp(i, 0, TopeEje - DuracionMinima); }

        int fx;
        if (fin is null)                       { dudosa = true; fx = i + DuracionMinima; }  // sin cierre cargado
        else if (fin.Value <= i)               { dudosa = true; fx = i + DuracionMinima; }  // cierra antes de arrancar
        else                                   { fx = fin.Value; }

        if (fx - i > DuracionSospechosa) dudosa = true;

        var cortada = fx > TopeEje;
        if (cortada) fx = TopeEje;

        return new Barra(f, i, fx, cortada, dudosa);
    }

    /// <summary>
    /// Reparte los servicios de una unidad en pistas: cada uno entra en la primera pista cuyo
    /// último servicio ya terminó. Los que no entran en ninguna abren un sub-renglón nuevo — ahí
    /// se ve el conflicto de diagramación (una misma unidad en dos servicios a la misma hora).
    /// </summary>
    private static Unidad ArmarUnidad(string nombre, List<Barra> barras)
    {
        var pistas = new List<List<Barra>>();
        foreach (var b in barras.OrderBy(x => x.Ini).ThenBy(x => x.Fin))
        {
            var pista = pistas.FirstOrDefault(p => p[^1].Fin <= b.Ini);
            if (pista is null) { pista = new List<Barra>(); pistas.Add(pista); }
            pista.Add(b);
        }

        // Ocupación real = unión de los intervalos (no la suma): si dos servicios se pisan, el
        // tiempo compartido se cuenta UNA vez. Si no, una unidad con conflictos figuraría con
        // más horas ocupadas que horas tiene el día.
        var minutos = 0;
        var desde = -1;
        var hasta = -1;
        foreach (var b in barras.OrderBy(x => x.Ini))
        {
            if (desde < 0) { desde = b.Ini; hasta = b.Fin; continue; }
            if (b.Ini <= hasta) { hasta = Math.Max(hasta, b.Fin); continue; }
            minutos += hasta - desde;
            desde = b.Ini; hasta = b.Fin;
        }
        if (desde >= 0) minutos += hasta - desde;

        return new Unidad(
            Nombre: nombre,
            Pistas: pistas,
            MinutosOcupados: minutos,
            Servicios: barras.Count,
            Solapes: pistas.Skip(1).Sum(p => p.Count));
    }

    /// <summary>Tipo de servicio para el color: mismo eje que los botones Empresa/Turismo/Nortur.</summary>
    public static string TipoDe(PlanillaTraficoRow f) =>
        f.EsNortur ? "N" : (f.Origen == "T" ? "T" : "E");

    public static string ColorDe(string tipo) => tipo switch
    {
        "T" => ColorTurismo,
        "N" => ColorNortur,
        _   => ColorEmpresa
    };

    public static string NombreTipo(string tipo) => tipo switch
    {
        "T" => "Turismo",
        "N" => "Nortur",
        _   => "Empresa"
    };

    /// <summary>Minutos desde las 00:00 → "HH:mm" (26:30 se lee 02:30, del día siguiente).</summary>
    public static string Hhmm(int minutos) =>
        $"{(minutos / 60) % 24:00}:{minutos % 60:00}";

    /// <summary>Horas con una decimal, al estilo "8,2 h".</summary>
    public static string Horas(int minutos) =>
        (minutos / 60.0).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("es-AR")) + " h";
}
