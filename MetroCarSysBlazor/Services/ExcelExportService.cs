using ClosedXML.Excel;
using MetroCarSysBlazor.Services;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Exportación a Excel con ClosedXML — equivalente al bloque openpyxl de
/// reservas_fecha_servicio.py (3 hojas: Detalle, Pivote, Ranking).
/// </summary>
public class ExcelExportService
{
    /// <summary>
    /// Genera el Excel del informe Reservas por fecha y servicio.
    /// </summary>
    /// <param name="dimension">
    /// Cómo está desglosado el informe: "Servicio" (default) o "Cliente". Solo cambia los
    /// títulos de las columnas — las filas ya vienen agrupadas por esa dimensión.
    /// </param>
    public byte[] ReservasFechaServicio(
        IReadOnlyList<ReservaFechaServicioRow> detalle,
        string metrica /* "Reservas" | "Pax" */,
        IReadOnlyList<ReservaFsDetalleRow>? reservas = null,
        string dimension = ReportService.DimServicio)
    {
        using var wb = new XLWorkbook();
        var esCliente = dimension == ReportService.DimCliente;
        var tituloDim = esCliente ? "Cliente" : "Servicio";
        var tituloCod = esCliente ? "Cod. cliente" : "Cod. servicio";

        // --- Hoja 1: Detalle (fila por fecha + categoría del desglose) -------
        var wsDet = wb.Worksheets.Add("Detalle");
        wsDet.Cell(1, 1).Value = "Fecha";
        wsDet.Cell(1, 2).Value = tituloCod;
        wsDet.Cell(1, 3).Value = tituloDim;
        wsDet.Cell(1, 4).Value = "Reservas";
        wsDet.Cell(1, 5).Value = "Canceladas";
        wsDet.Cell(1, 6).Value = "Pax";
        var rDet = 2;
        foreach (var d in detalle)
        {
            wsDet.Cell(rDet, 1).Value = d.Fecha.ToDateTime(TimeOnly.MinValue);
            wsDet.Cell(rDet, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            wsDet.Cell(rDet, 2).Value = d.Cod;
            wsDet.Cell(rDet, 3).Value = d.Etiqueta;
            wsDet.Cell(rDet, 4).Value = d.Reservas;
            wsDet.Cell(rDet, 5).Value = d.Canceladas;
            wsDet.Cell(rDet, 6).Value = d.Pax;
            rDet++;
        }
        wsDet.Row(1).Style.Font.Bold = true;
        wsDet.Columns().AdjustToContents();

        // --- Hoja 2: Pivote fecha x categoría (valor = métrica elegida) ------
        var wsPiv = wb.Worksheets.Add("Pivote");
        var fechas = detalle.Select(d => d.Fecha).Distinct().OrderBy(f => f).ToList();
        var servicios = detalle.Select(d => d.Etiqueta).Distinct().OrderBy(s => s).ToList();
        Func<ReservaFechaServicioRow, int> val = metrica == "Pax" ? r => r.Pax : r => r.Reservas;
        var mapa = detalle.ToDictionary(d => (d.Fecha, d.Etiqueta), val);

        wsPiv.Cell(1, 1).Value = "Fecha";
        for (var c = 0; c < servicios.Count; c++)
            wsPiv.Cell(1, c + 2).Value = servicios[c];
        wsPiv.Cell(1, servicios.Count + 2).Value = "TOTAL";

        for (var i = 0; i < fechas.Count; i++)
        {
            wsPiv.Cell(i + 2, 1).Value = fechas[i].ToDateTime(TimeOnly.MinValue);
            wsPiv.Cell(i + 2, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            var totFila = 0;
            for (var c = 0; c < servicios.Count; c++)
            {
                var v = mapa.TryGetValue((fechas[i], servicios[c]), out var x) ? x : 0;
                wsPiv.Cell(i + 2, c + 2).Value = v;
                totFila += v;
            }
            wsPiv.Cell(i + 2, servicios.Count + 2).Value = totFila;
        }
        wsPiv.Row(1).Style.Font.Bold = true;
        wsPiv.Column(servicios.Count + 2).Style.Font.Bold = true;
        wsPiv.Columns().AdjustToContents();

        // --- Hoja 3: Ranking por categoría del desglose -----------------------
        var wsRk = wb.Worksheets.Add("Ranking");
        wsRk.Cell(1, 1).Value = tituloDim;
        wsRk.Cell(1, 2).Value = "Reservas";
        wsRk.Cell(1, 3).Value = "Pax";
        wsRk.Cell(1, 4).Value = "Canceladas";
        var ranking = detalle
            .GroupBy(d => d.Etiqueta)
            .Select(g => new
            {
                Servicio = g.Key,
                Reservas = g.Sum(x => x.Reservas),
                Pax = g.Sum(x => x.Pax),
                Canceladas = g.Sum(x => x.Canceladas),
            })
            .OrderByDescending(x => metrica == "Pax" ? x.Pax : x.Reservas)
            .ToList();
        var rRk = 2;
        foreach (var r in ranking)
        {
            wsRk.Cell(rRk, 1).Value = r.Servicio;
            wsRk.Cell(rRk, 2).Value = r.Reservas;
            wsRk.Cell(rRk, 3).Value = r.Pax;
            wsRk.Cell(rRk, 4).Value = r.Canceladas;
            rRk++;
        }
        wsRk.Row(1).Style.Font.Bold = true;
        wsRk.Columns().AdjustToContents();

        // --- Hoja 4: Reservas una por una (informe detallado) -----------------
        if (reservas is { Count: > 0 })
        {
            var wsRes = wb.Worksheets.Add("Reservas");
            var cab = new[]
            {
                "Nº Reserva", "Fecha", "Hora", "Servicio", "Cliente",
                "Recorrido", "Pax", "Estado", "Interno", "Chofer", "Grupo", "Origen"
            };
            for (var c = 0; c < cab.Length; c++)
                wsRes.Cell(1, c + 1).Value = cab[c];

            var rRes = 2;
            foreach (var v in reservas)
            {
                wsRes.Cell(rRes, 1).Value = v.IdViaje;
                wsRes.Cell(rRes, 2).Value = v.Fecha.ToDateTime(TimeOnly.MinValue);
                wsRes.Cell(rRes, 2).Style.DateFormat.Format = "dd/mm/yyyy";
                wsRes.Cell(rRes, 3).Value = v.Hora;
                wsRes.Cell(rRes, 4).Value = v.Servicio;
                wsRes.Cell(rRes, 5).Value = v.Cliente;
                wsRes.Cell(rRes, 6).Value = v.Recorrido;
                wsRes.Cell(rRes, 7).Value = v.Pax;
                wsRes.Cell(rRes, 8).Value = v.Estado;
                if (v.Interno.HasValue) wsRes.Cell(rRes, 9).Value = v.Interno.Value;
                wsRes.Cell(rRes, 10).Value = v.Chofer;
                wsRes.Cell(rRes, 11).Value = v.Grupo;
                wsRes.Cell(rRes, 12).Value = v.Origen == "P" ? "Plantilla" : "Transportación";
                rRes++;
            }
            wsRes.Row(1).Style.Font.Bold = true;
            wsRes.SheetView.FreezeRows(1);
            wsRes.Columns().AdjustToContents(1, Math.Min(reservas.Count + 1, 500));
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta la planilla de servicios del día (Operación de Tráfico) a una hoja,
    /// con las filas pintadas según el estado igual que la grilla del FoxPro.
    /// </summary>
    public byte[] PlanillaTrafico(IReadOnlyList<PlanillaTraficoRow> filas, DateOnly dia)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Planilla");

        // El orden ESPEJA el de la grilla de Tráfico (colgroup/thead de PlanillaTrafico.razor
        // + celdas de TraficoFilaRow.razor). 03/08/2026: Comentario se movió entre Fletero y
        // Chofer en pantalla y acá también, para que lo exportado se lea igual que lo visto.
        // 18/08/2026: Ag pasó a ser la última columna de datos, mismo criterio. (Estado va al
        // final del Excel porque en pantalla va PRIMERO como chip de color, que acá no existe.)
        // 20/08/2026: Adj se fue al final y "Nro. Reserva" ocupó su lugar, también igual que
        // la pantalla. Ojo: "Reserva" (col.1) es la FECHA; "Nro. Reserva" es el id del viaje.
        string[] headers =
        {
            "Reserva","H.Pre","H.Ini","H.Fin","H.Avi","H.Cie","U/Pr","U/Cb","U/As",
            "Chq","Recorrido","Fletero","Comentario","Chofer","Veh","Cliente","Pax","Agua",
            "Nro. Reserva","Grupo","Vuelo","Guia","Ag","Adj","Estado"
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        var r = 2;
        foreach (var f in filas)
        {
            ws.Cell(r, 1).Value = f.Fecha.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            ws.Cell(r, 2).Value = f.HPre;
            ws.Cell(r, 3).Value = f.HIni;
            ws.Cell(r, 4).Value = f.HFin;
            ws.Cell(r, 5).Value = f.HAvi;
            ws.Cell(r, 6).Value = f.HCie;
            ws.Cell(r, 7).Value = f.UPr;
            ws.Cell(r, 8).Value = f.UCb;
            ws.Cell(r, 9).Value = f.UAs;
            ws.Cell(r, 10).Value = f.Chq;
            ws.Cell(r, 11).Value = f.Recorrido;
            ws.Cell(r, 12).Value = f.Fletero;
            ws.Cell(r, 13).Value = f.Comentario;
            ws.Cell(r, 14).Value = f.Chofer;
            ws.Cell(r, 15).Value = f.Veh;
            ws.Cell(r, 16).Value = f.Cliente;
            ws.Cell(r, 17).Value = f.Pax;
            ws.Cell(r, 18).Value = f.Agua;
            ws.Cell(r, 19).Value = f.IdViaje;
            ws.Cell(r, 20).Value = f.Grupo;
            ws.Cell(r, 21).Value = f.Vuelo;
            ws.Cell(r, 22).Value = f.Guia;
            ws.Cell(r, 23).Value = f.Ag;
            ws.Cell(r, 24).Value = f.Adj;
            ws.Cell(r, 25).Value = f.Estado;

            // Color de fila según estado (misma paleta que grid_color_viaje del FoxPro)
            var hex = ColorEstado(f.Estado);
            if (hex is not null)
                ws.Range(r, 1, r, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml(hex);

            r++;
        }

        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        ws.Row(1).Style.Font.FontColor = XLColor.White;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta el <b>Tablero de ocupación de flota</b> (31/07/2026) con el formato que Nortur
    /// ya arma a mano en Excel: una fila por unidad, una columna por media hora, celda pintada
    /// cuando la unidad está ocupada (color según Empresa / Turismo / Nortur).
    ///
    /// <para>Recibe el tablero YA armado (<see cref="OcupacionFlota.Tablero"/>), el mismo objeto
    /// que dibuja el diálogo: así el archivo no puede discrepar de lo que se ve en pantalla —
    /// incluidos el corte del eje a las 02:00 y el modo de duración elegido.</para>
    ///
    /// Hoja 1 "Ocupación" = la matriz. Hoja 2 "Detalle" = un renglón por servicio graficado.
    /// </summary>
    /// <param name="real">Modo de duración con el que se armó el tablero (para la leyenda).</param>
    /// <param name="subtitulo">Día o filtro activo, tal como lo muestra el encabezado del diálogo.</param>
    public byte[] TableroOcupacion(OcupacionFlota.Tablero tablero, bool real, string subtitulo)
    {
        using var wb = new XLWorkbook();
        var cols = tablero.FinEje / 30;              // columnas de media hora
        const int ColBase = 3;                       // A = unidad, B = ocupado, C… = franjas

        // ── Hoja 1: la matriz de medias horas ────────────────────────────────────
        var ws = wb.Worksheets.Add("Ocupación");
        ws.Cell(1, 1).Value = "Ocupación de flota";
        ws.Cell(1, 3).Value = subtitulo;
        ws.Cell(2, 1).Value = real ? "Duración real (H.Cie)" : "Duración programada (H.Fin)";
        ws.Cell(2, 3).Value = $"{tablero.UnidadesReales} unidades · {tablero.Servicios} servicios"
                            + (tablero.ServiciosSinUnidad > 0 ? $" · {tablero.ServiciosSinUnidad} sin unidad asignada (fila S/C)" : "")
                            + (tablero.Solapes > 0 ? $" · {tablero.Solapes} en conflicto horario" : "")
                            + (tablero.Excluidos > 0 ? $" · {tablero.Excluidos} sin hora de inicio (no graficados)" : "");
        ws.Range(1, 1, 1, 3).Style.Font.Bold = true;
        ws.Cell(2, 1).Style.Font.Italic = true;

        const int FilaHead = 4;
        ws.Cell(FilaHead, 1).Value = "Unidad";
        ws.Cell(FilaHead, 2).Value = "Ocupado";
        for (var c = 0; c < cols; c++)
            ws.Cell(FilaHead, ColBase + c).Value = OcupacionFlota.Hhmm(c * 30);

        ws.Row(FilaHead).Style.Font.Bold = true;
        ws.Row(FilaHead).Style.Font.FontSize = 8;
        ws.Row(FilaHead).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        ws.Row(FilaHead).Style.Font.FontColor = XLColor.White;
        ws.Row(FilaHead).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        var r = FilaHead + 1;
        foreach (var u in tablero.Unidades)
        {
            // La fila-pool no es una unidad: se rotula distinto y en vez de horas ocupadas
            // lleva la cantidad de servicios pendientes de asignar (no hay flota que ocupar).
            if (u.SinUnidad)
            {
                ws.Cell(r, 1).Value = "S/C (sin unidad)";
                ws.Cell(r, 2).Value = $"{u.Servicios} serv.";
                ws.Range(r, 1, r, 2).Style.Font.Bold = true;
                ws.Range(r, 1, r, 2).Style.Font.FontColor = XLColor.FromHtml("#8A1C1C");
            }
            else
            {
                ws.Cell(r, 1).Value = u.Nombre;
                ws.Cell(r, 2).Value = Math.Round(u.MinutosOcupados / 60.0, 1);
                ws.Cell(r, 2).Style.NumberFormat.Format = "0.0 \"h\"";
            }

            // Una celda por franja: se pinta si algún servicio la toca. Si en la misma franja
            // hay servicios de distinto tipo, manda el que arrancó primero (igual que la
            // pantalla, donde la barra de abajo es la de la primera pista).
            for (var c = 0; c < cols; c++)
            {
                var desde = c * 30;
                var hasta = desde + 30;
                var b = u.Barras
                    .Where(x => x.Ini < hasta && x.Fin > desde)
                    .OrderBy(x => x.Ini)
                    .FirstOrDefault();
                if (b is null) continue;

                var celda = ws.Cell(r, ColBase + c);
                celda.Style.Fill.BackgroundColor = XLColor.FromHtml(b.Color);
                if (b.Dudosa)
                    celda.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
                if (b.Dudosa)
                    celda.Style.Border.OutsideBorderColor = XLColor.FromHtml("#DC2626");
            }
            r++;
        }

        ws.Column(1).Width = 10;
        ws.Column(2).Width = 8;
        for (var c = 0; c < cols; c++)
            ws.Column(ColBase + c).Width = 3.4;
        ws.SheetView.FreezeRows(FilaHead);
        ws.SheetView.FreezeColumns(2);

        // ── Hoja 2: el detalle de los servicios graficados ───────────────────────
        var wsDet = wb.Worksheets.Add("Detalle");
        string[] headers =
        {
            "Unidad","Reserva","Inicio","Cierre","Duración (h)","Tipo","Estado",
            "Recorrido","Cliente","Pax","Chofer","Nº viaje","Observación"
        };
        for (var c = 0; c < headers.Length; c++)
            wsDet.Cell(1, c + 1).Value = headers[c];

        var rd = 2;
        foreach (var u in tablero.Unidades)
        {
            foreach (var b in u.Barras.OrderBy(x => x.Ini))
            {
                var f = b.Fila;
                wsDet.Cell(rd, 1).Value = u.SinUnidad ? "S/C (sin unidad)" : u.Nombre;
                wsDet.Cell(rd, 2).Value = f.Fecha.ToDateTime(TimeOnly.MinValue);
                wsDet.Cell(rd, 2).Style.DateFormat.Format = "dd/mm/yyyy";
                wsDet.Cell(rd, 3).Value = OcupacionFlota.Hhmm(b.Ini);
                wsDet.Cell(rd, 4).Value = OcupacionFlota.Hhmm(b.Fin);
                wsDet.Cell(rd, 5).Value = Math.Round(b.Duracion / 60.0, 2);
                wsDet.Cell(rd, 6).Value = OcupacionFlota.NombreTipo(b.Tipo);
                wsDet.Cell(rd, 7).Value = f.EstadoDisplay;
                wsDet.Cell(rd, 8).Value = f.Recorrido;
                wsDet.Cell(rd, 9).Value = f.Cliente;
                wsDet.Cell(rd, 10).Value = f.Pax;
                wsDet.Cell(rd, 11).Value = f.Chofer;
                wsDet.Cell(rd, 12).Value = f.IdViaje;
                wsDet.Cell(rd, 13).Value =
                    b.Cortada && b.Dudosa ? "Cortado a las 02:00 · duración fuera de rango"
                  : b.Cortada             ? "Cortado a las 02:00"
                  : b.Dudosa              ? "Duración fuera de rango: revisar H.Ini / H.Fin / H.Cie"
                  : "";
                if (b.Dudosa || b.Cortada)
                    wsDet.Range(rd, 1, rd, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#FDECEC");
                rd++;
            }
        }

        wsDet.Row(1).Style.Font.Bold = true;
        wsDet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        wsDet.Row(1).Style.Font.FontColor = XLColor.White;
        wsDet.SheetView.FreezeRows(1);
        wsDet.Columns().AdjustToContents(1, Math.Min(rd, 500));

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta la vista de servicios CANCELADOS del día (botón "Cxl" del FoxPro).
    /// Mismas columnas que la grilla arma_grid_viaje_sup_cnl, incluida la de Motivo.
    /// </summary>
    public byte[] TraficoCancelados(IReadOnlyList<TraficoCanceladoRow> filas, DateOnly dia)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Cancelados");

        string[] headers =
        {
            "Ob","Ad","Reserva","H.Ini","H.Fin","H.Avi","H.Cie","U/Pr","U/Cb","U/As",
            "Chq","Recorrido","Motivo","Veh","Cliente","Pax","Comentario","Grupo","Vuelo","Guia"
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        var r = 2;
        foreach (var f in filas)
        {
            ws.Cell(r, 1).Value = f.Ob;
            ws.Cell(r, 2).Value = f.Ad;
            ws.Cell(r, 3).Value = f.Fecha.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 3).Style.DateFormat.Format = "dd/mm/yyyy";
            ws.Cell(r, 4).Value = f.HIni;
            ws.Cell(r, 5).Value = f.HFin;
            ws.Cell(r, 6).Value = f.HAvi;
            ws.Cell(r, 7).Value = f.HCie;
            ws.Cell(r, 8).Value = f.UPr;
            ws.Cell(r, 9).Value = f.UCb;
            ws.Cell(r, 10).Value = f.UAs;
            ws.Cell(r, 11).Value = f.Chq;
            ws.Cell(r, 12).Value = f.Recorrido;
            ws.Cell(r, 13).Value = f.Motivo;
            ws.Cell(r, 14).Value = f.Veh;
            ws.Cell(r, 15).Value = f.Cliente;
            ws.Cell(r, 16).Value = f.Pax;
            ws.Cell(r, 17).Value = f.Comentario;
            ws.Cell(r, 18).Value = f.Grupo;
            ws.Cell(r, 19).Value = f.Vuelo;
            ws.Cell(r, 20).Value = f.Guia;
            r++;
        }

        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#DC2626");
        ws.Row(1).Style.Font.FontColor = XLColor.White;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta el informe de Reservas por Banda Horaria (mismo estilo multi-hoja que
    /// ReservasFechaServicio). Hoja 1: detalle agregado. Hoja 2: pivote fecha × banda con la
    /// métrica elegida + fila TOTAL. Hoja 3: resumen por vehículo × banda. Hoja 4 (opcional):
    /// los viajes uno por uno (drill-down), cuando se pasa <paramref name="viajes"/>.
    /// </summary>
    public byte[] BandaHoraria(
        IReadOnlyList<BandaHorariaRow> filas,
        string metrica /* "Reservas" | "Pax" */,
        bool porHora = false,
        IReadOnlyList<BandaHorariaDetalleRow>? viajes = null)
    {
        // La métrica elige qué número va en pivote/resumen (viajes o pax).
        Func<BandaHorariaRow, int> val = metrica == "Pax" ? f => f.Pax : f => f.Reservas;
        var etiquetaMetrica = metrica == "Pax" ? "Pax" : "Viajes";

        // El Excel replica la agrupación que se está viendo en pantalla: las 6 bandas o las 24
        // horas de inicio (30/07/2026). `bandas` es el nombre histórico de la variable = las
        // columnas del pivote, sean bandas u horas.
        var bandas = porHora ? ReportService.HorasDelDia : ReportService.BandasHorarias;
        Func<BandaHorariaRow, string> colDe = porHora ? f => $"{f.Hora:00}:00" : f => f.Banda;
        var etiquetaCol = porHora ? "Hora de inicio" : "Banda horaria";

        using var wb = new XLWorkbook();

        // --- Hoja 1: Detalle agregado (fecha × veh × banda, con viajes y pax) ----
        var wsDet = wb.Worksheets.Add("Detalle");
        wsDet.Cell(1, 1).Value = "Fecha";
        wsDet.Cell(1, 2).Value = "Tipo de servicio";
        wsDet.Cell(1, 3).Value = "Tipo vehículo";
        wsDet.Cell(1, 4).Value = "Banda horaria";
        wsDet.Cell(1, 5).Value = "Hora de inicio";
        wsDet.Cell(1, 6).Value = "Viajes";
        wsDet.Cell(1, 7).Value = "Pax";
        var r = 2;
        foreach (var f in filas)
        {
            wsDet.Cell(r, 1).Value = f.Fecha.ToDateTime(TimeOnly.MinValue);
            wsDet.Cell(r, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            wsDet.Cell(r, 2).Value = f.Categoria;
            wsDet.Cell(r, 3).Value = f.TipoVehiculo;
            wsDet.Cell(r, 4).Value = f.Banda;
            wsDet.Cell(r, 5).Value = $"{f.Hora:00}:00";
            wsDet.Cell(r, 6).Value = f.Reservas;
            wsDet.Cell(r, 7).Value = f.Pax;
            r++;
        }
        wsDet.Row(1).Style.Font.Bold = true;
        wsDet.Columns().AdjustToContents();

        // --- Hoja 2: Pivote fecha × banda (valor = métrica elegida) --------------
        var fechas = filas.Select(f => f.Fecha).Distinct().OrderBy(d => d).ToList();
        var mapa = filas
            .GroupBy(f => (f.Fecha, Col: colDe(f)))
            .ToDictionary(g => g.Key, g => g.Sum(val));

        var wsPiv = wb.Worksheets.Add($"Pivote {etiquetaMetrica} x {(porHora ? "hora" : "banda")}");
        wsPiv.Cell(1, 1).Value = "Fecha";
        for (var c = 0; c < bandas.Count; c++)
            wsPiv.Cell(1, c + 2).Value = bandas[c];
        wsPiv.Cell(1, bandas.Count + 2).Value = "TOTAL";

        for (var i = 0; i < fechas.Count; i++)
        {
            wsPiv.Cell(i + 2, 1).Value = fechas[i].ToDateTime(TimeOnly.MinValue);
            wsPiv.Cell(i + 2, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            var tot = 0;
            for (var c = 0; c < bandas.Count; c++)
            {
                var v = mapa.TryGetValue((fechas[i], bandas[c]), out var x) ? x : 0;
                wsPiv.Cell(i + 2, c + 2).Value = v;
                tot += v;
            }
            wsPiv.Cell(i + 2, bandas.Count + 2).Value = tot;
        }
        // Fila TOTAL por columna
        var rTot = fechas.Count + 2;
        wsPiv.Cell(rTot, 1).Value = "TOTAL";
        var granTot = 0;
        for (var c = 0; c < bandas.Count; c++)
        {
            var colTot = fechas.Sum(f => mapa.TryGetValue((f, bandas[c]), out var x) ? x : 0);
            wsPiv.Cell(rTot, c + 2).Value = colTot;
            granTot += colTot;
        }
        wsPiv.Cell(rTot, bandas.Count + 2).Value = granTot;
        wsPiv.Row(1).Style.Font.Bold = true;
        wsPiv.Row(rTot).Style.Font.Bold = true;
        wsPiv.Column(bandas.Count + 2).Style.Font.Bold = true;
        wsPiv.Columns().AdjustToContents();

        // --- Hoja 3: Resumen por vehículo × banda (valor = métrica elegida) ------
        var wsVeh = wb.Worksheets.Add("Resumen por vehículo");
        wsVeh.Cell(1, 1).Value = "Tipo vehículo";
        for (var c = 0; c < bandas.Count; c++)
            wsVeh.Cell(1, c + 2).Value = bandas[c];
        wsVeh.Cell(1, bandas.Count + 2).Value = "TOTAL";

        var vehiculos = filas.Select(f => f.TipoVehiculo).Distinct().OrderBy(v => v).ToList();
        var mapaVeh = filas
            .GroupBy(f => (f.TipoVehiculo, Col: colDe(f)))
            .ToDictionary(g => g.Key, g => g.Sum(val));

        for (var i = 0; i < vehiculos.Count; i++)
        {
            wsVeh.Cell(i + 2, 1).Value = vehiculos[i];
            var tot = 0;
            for (var c = 0; c < bandas.Count; c++)
            {
                var v = mapaVeh.TryGetValue((vehiculos[i], bandas[c]), out var x) ? x : 0;
                wsVeh.Cell(i + 2, c + 2).Value = v;
                tot += v;
            }
            wsVeh.Cell(i + 2, bandas.Count + 2).Value = tot;
        }
        wsVeh.Row(1).Style.Font.Bold = true;
        wsVeh.Column(bandas.Count + 2).Style.Font.Bold = true;
        wsVeh.Columns().AdjustToContents();

        // --- Hoja 4: Resumen por tipo de servicio × columna (30/07/2026) ---------
        // Es la lectura que pidió el frente final: cuánto pesa Transporte de Personal (Empresa)
        // frente a Turismo en cada franja. Se listan las 3 categorías siempre, aunque den 0.
        var wsCat = wb.Worksheets.Add("Resumen por tipo de servicio");
        wsCat.Cell(1, 1).Value = etiquetaCol;
        for (var c = 0; c < ReportService.CategoriasServicio.Count; c++)
            wsCat.Cell(1, c + 2).Value = ReportService.CategoriasServicio[c];
        wsCat.Cell(1, ReportService.CategoriasServicio.Count + 2).Value = "TOTAL";

        var mapaCat = filas
            .GroupBy(f => (Col: colDe(f), f.Categoria))
            .ToDictionary(g => g.Key, g => g.Sum(val));

        for (var i = 0; i < bandas.Count; i++)
        {
            wsCat.Cell(i + 2, 1).Value = bandas[i];
            var tot = 0;
            for (var c = 0; c < ReportService.CategoriasServicio.Count; c++)
            {
                var v = mapaCat.TryGetValue((bandas[i], ReportService.CategoriasServicio[c]), out var x) ? x : 0;
                wsCat.Cell(i + 2, c + 2).Value = v;
                tot += v;
            }
            wsCat.Cell(i + 2, ReportService.CategoriasServicio.Count + 2).Value = tot;
        }
        wsCat.Row(1).Style.Font.Bold = true;
        wsCat.Column(ReportService.CategoriasServicio.Count + 2).Style.Font.Bold = true;
        wsCat.Columns().AdjustToContents();

        // --- Hoja 5: Viajes uno por uno (drill-down) -----------------------------
        if (viajes is { Count: > 0 })
        {
            var wsV = wb.Worksheets.Add("Viajes");
            string[] cab =
            {
                "Nº Reserva", "Fecha", "Hora", "Banda", "Tipo de servicio", "Tipo vehículo",
                "Servicio", "Cliente", "Recorrido", "Pax", "Estado", "Interno", "Chofer",
                "Grupo", "Origen"
            };
            for (var c = 0; c < cab.Length; c++)
                wsV.Cell(1, c + 1).Value = cab[c];

            var rV = 2;
            foreach (var d in viajes)
            {
                var v = d.Reserva;
                wsV.Cell(rV, 1).Value = v.IdViaje;
                wsV.Cell(rV, 2).Value = v.Fecha.ToDateTime(TimeOnly.MinValue);
                wsV.Cell(rV, 2).Style.DateFormat.Format = "dd/mm/yyyy";
                wsV.Cell(rV, 3).Value = v.Hora;
                wsV.Cell(rV, 4).Value = d.Banda;
                wsV.Cell(rV, 5).Value = d.Categoria;
                wsV.Cell(rV, 6).Value = d.TipoVehiculo;
                wsV.Cell(rV, 7).Value = v.Servicio;
                wsV.Cell(rV, 8).Value = v.Cliente;
                wsV.Cell(rV, 9).Value = v.Recorrido;
                wsV.Cell(rV, 10).Value = v.Pax;
                wsV.Cell(rV, 11).Value = v.Estado;
                if (v.Interno.HasValue) wsV.Cell(rV, 12).Value = v.Interno.Value;
                wsV.Cell(rV, 13).Value = v.Chofer;
                wsV.Cell(rV, 14).Value = v.Grupo;
                wsV.Cell(rV, 15).Value = v.Origen == "P" ? "Plantilla" : "Transportación";
                rV++;
            }
            wsV.Row(1).Style.Font.Bold = true;
            wsV.SheetView.FreezeRows(1);
            wsV.Columns().AdjustToContents(1, Math.Min(viajes.Count + 1, 500));
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta el informe "Reservas por cliente" (FoxPro: viaje_analisis.scx — su única salida
    /// era una tabla dinámica de Excel; acá replicamos el pivote cliente × mes y sumamos hojas).
    /// Hoja 1: detalle agregado (mes × cliente × tipo × motivo). Hoja 2: pivote cliente × mes
    /// con la métrica elegida (y las columnas del período de comparación si lo hay). Hoja 3:
    /// resumen tipo × mes. Hoja 4: resumen por motivo de cancelación. Hoja 5 (opcional): los
    /// viajes uno por uno, con la unidad y su dueño.
    /// </summary>
    public byte[] ReservasPorCliente(
        IReadOnlyList<ReservaClienteRow> filas,
        string metrica /* "Reservas" | "Pax" */,
        string criterio /* ReportService.CriterioUso | CriterioFletero */,
        IReadOnlyList<MotivoCancelaDto> motivos,
        IReadOnlyList<ReservaClienteDetalleRow>? viajes = null,
        IReadOnlyList<ReservaClienteRow>? filasBase = null,
        string baseLbl = "")
    {
        Func<ReservaClienteRow, int> val = metrica == "Pax" ? f => f.Pax : f => f.Viajes;
        var etiquetaMetrica = metrica == "Pax" ? "Pax" : "Viajes";
        var meses = filas.Select(f => f.Mes).Distinct().OrderBy(m => m).ToList();
        var tipos = ReportService.TiposReservaCliente;

        string MotivoDe(int id) =>
            id == 0 ? "" : motivos.FirstOrDefault(m => m.Id == id)?.Motivo ?? $"({id})";

        using var wb = new XLWorkbook();

        // --- Hoja 1: Detalle agregado (mes × cliente × tipo × motivo) ---
        var wsDet = wb.Worksheets.Add("Detalle");
        wsDet.Cell(1, 1).Value = "Mes";
        wsDet.Cell(1, 2).Value = "Código";
        wsDet.Cell(1, 3).Value = "Cliente";
        wsDet.Cell(1, 4).Value = "Tipo de unidad";
        wsDet.Cell(1, 5).Value = "Motivo cancelación";
        wsDet.Cell(1, 6).Value = "Viajes";
        wsDet.Cell(1, 7).Value = "Pax";
        var r = 2;
        foreach (var f in filas)
        {
            wsDet.Cell(r, 1).Value = f.Mes;
            wsDet.Cell(r, 2).Value = f.IdCliente;
            wsDet.Cell(r, 3).Value = f.Cliente;
            wsDet.Cell(r, 4).Value = f.TipoSegun(criterio);
            wsDet.Cell(r, 5).Value = MotivoDe(f.IdMotivo);
            wsDet.Cell(r, 6).Value = f.Viajes;
            wsDet.Cell(r, 7).Value = f.Pax;
            r++;
        }
        wsDet.Row(1).Style.Font.Bold = true;
        wsDet.Columns().AdjustToContents(1, Math.Min(filas.Count + 1, 500));

        // --- Hoja 2: Pivote cliente × mes (la tabla dinámica del FoxPro) ------------
        var clientes = filas
            .GroupBy(f => f.IdCliente)
            .Select(g => (Id: g.Key, Nombre: g.First().Cliente, Total: g.Sum(val)))
            .OrderByDescending(x => x.Total).ThenBy(x => x.Nombre)
            .ToList();
        var mapa = filas
            .GroupBy(f => (f.IdCliente, f.Mes))
            .ToDictionary(g => g.Key, g => g.Sum(val));

        // Totales del período de comparación por cliente (columnas extra "X vs X-1").
        var hayBase = filasBase is { Count: > 0 };
        var totalBaseCli = hayBase
            ? filasBase!.GroupBy(f => f.IdCliente).ToDictionary(g => g.Key, g => g.Sum(val))
            : new Dictionary<string, int>();

        var wsPiv = wb.Worksheets.Add($"Pivote ({etiquetaMetrica})");
        wsPiv.Cell(1, 1).Value = "Cliente";
        for (var c = 0; c < meses.Count; c++)
            wsPiv.Cell(1, c + 2).Value = meses[c];
        var colTotal = meses.Count + 2;
        wsPiv.Cell(1, colTotal).Value = "TOTAL";
        if (hayBase)
        {
            wsPiv.Cell(1, colTotal + 1).Value = $"TOTAL {baseLbl}".Trim();
            wsPiv.Cell(1, colTotal + 2).Value = "Variación";
            wsPiv.Cell(1, colTotal + 3).Value = "Variación %";
        }

        for (var i = 0; i < clientes.Count; i++)
        {
            wsPiv.Cell(i + 2, 1).Value = clientes[i].Nombre;
            for (var c = 0; c < meses.Count; c++)
                wsPiv.Cell(i + 2, c + 2).Value =
                    mapa.TryGetValue((clientes[i].Id, meses[c]), out var x) ? x : 0;
            wsPiv.Cell(i + 2, colTotal).Value = clientes[i].Total;
            if (hayBase)
            {
                var prev = totalBaseCli.TryGetValue(clientes[i].Id, out var p) ? p : 0;
                wsPiv.Cell(i + 2, colTotal + 1).Value = prev;
                wsPiv.Cell(i + 2, colTotal + 2).Value = clientes[i].Total - prev;
                if (prev > 0)
                {
                    wsPiv.Cell(i + 2, colTotal + 3).Value = (clientes[i].Total - prev) / (double)prev;
                    wsPiv.Cell(i + 2, colTotal + 3).Style.NumberFormat.Format = "+0.0%;-0.0%;0.0%";
                }
            }
        }
        var rTot = clientes.Count + 2;
        wsPiv.Cell(rTot, 1).Value = "TOTAL";
        var granTot = 0;
        for (var c = 0; c < meses.Count; c++)
        {
            var colTot = filas.Where(f => f.Mes == meses[c]).Sum(val);
            wsPiv.Cell(rTot, c + 2).Value = colTot;
            granTot += colTot;
        }
        wsPiv.Cell(rTot, colTotal).Value = granTot;
        if (hayBase)
        {
            var granBase = filasBase!.Sum(val);
            wsPiv.Cell(rTot, colTotal + 1).Value = granBase;
            wsPiv.Cell(rTot, colTotal + 2).Value = granTot - granBase;
            if (granBase > 0)
            {
                wsPiv.Cell(rTot, colTotal + 3).Value = (granTot - granBase) / (double)granBase;
                wsPiv.Cell(rTot, colTotal + 3).Style.NumberFormat.Format = "+0.0%;-0.0%;0.0%";
            }
        }
        wsPiv.Row(1).Style.Font.Bold = true;
        wsPiv.Row(rTot).Style.Font.Bold = true;
        wsPiv.Column(colTotal).Style.Font.Bold = true;
        wsPiv.SheetView.FreezeRows(1);
        wsPiv.Columns().AdjustToContents(1, Math.Min(clientes.Count + 2, 500));

        // --- Hoja 3: Resumen tipo × mes (la "página" del pivot FoxPro, abierta) -----
        var wsTipo = wb.Worksheets.Add("Resumen por tipo");
        wsTipo.Cell(1, 1).Value = "Tipo";
        for (var c = 0; c < meses.Count; c++)
            wsTipo.Cell(1, c + 2).Value = meses[c];
        wsTipo.Cell(1, meses.Count + 2).Value = "TOTAL";

        var mapaTipo = filas
            .GroupBy(f => (Tipo: f.TipoSegun(criterio), f.Mes))
            .ToDictionary(g => g.Key, g => g.Sum(val));
        for (var i = 0; i < tipos.Count; i++)
        {
            wsTipo.Cell(i + 2, 1).Value = tipos[i];
            var tot = 0;
            for (var c = 0; c < meses.Count; c++)
            {
                var v = mapaTipo.TryGetValue((tipos[i], meses[c]), out var x) ? x : 0;
                wsTipo.Cell(i + 2, c + 2).Value = v;
                tot += v;
            }
            wsTipo.Cell(i + 2, meses.Count + 2).Value = tot;
        }
        wsTipo.Row(1).Style.Font.Bold = true;
        wsTipo.Column(meses.Count + 2).Style.Font.Bold = true;
        wsTipo.Columns().AdjustToContents();

        // --- Hoja 4: Cancelaciones por motivo × mes (solo si hay canceladas) --------
        // Es lo que la clienta no encontraba: el motivo, cruzado con el cliente.
        if (filas.Any(f => f.IdMotivo > 0))
        {
            var wsMot = wb.Worksheets.Add("Motivos de cancelación");
            var motivosPresentes = filas.Where(f => f.IdMotivo > 0)
                .GroupBy(f => f.IdMotivo)
                .Select(g => (Id: g.Key, Nombre: MotivoDe(g.Key), Total: g.Sum(val)))
                .OrderByDescending(x => x.Total).ToList();

            wsMot.Cell(1, 1).Value = "Motivo";
            for (var c = 0; c < meses.Count; c++)
                wsMot.Cell(1, c + 2).Value = meses[c];
            wsMot.Cell(1, meses.Count + 2).Value = "TOTAL";

            var mapaMot = filas.Where(f => f.IdMotivo > 0)
                .GroupBy(f => (f.IdMotivo, f.Mes))
                .ToDictionary(g => g.Key, g => g.Sum(val));
            for (var i = 0; i < motivosPresentes.Count; i++)
            {
                wsMot.Cell(i + 2, 1).Value = motivosPresentes[i].Nombre;
                for (var c = 0; c < meses.Count; c++)
                    wsMot.Cell(i + 2, c + 2).Value =
                        mapaMot.TryGetValue((motivosPresentes[i].Id, meses[c]), out var x) ? x : 0;
                wsMot.Cell(i + 2, meses.Count + 2).Value = motivosPresentes[i].Total;
            }
            wsMot.Row(1).Style.Font.Bold = true;
            wsMot.Column(meses.Count + 2).Style.Font.Bold = true;
            wsMot.Columns().AdjustToContents();

            // Cruce cliente × motivo — la vista que existía en el Metrocar.
            var wsCliMot = wb.Worksheets.Add("Cliente x motivo");
            wsCliMot.Cell(1, 1).Value = "Cliente";
            for (var c = 0; c < motivosPresentes.Count; c++)
                wsCliMot.Cell(1, c + 2).Value = motivosPresentes[c].Nombre;
            wsCliMot.Cell(1, motivosPresentes.Count + 2).Value = "TOTAL";

            var mapaCliMot = filas.Where(f => f.IdMotivo > 0)
                .GroupBy(f => (f.IdCliente, f.IdMotivo))
                .ToDictionary(g => g.Key, g => g.Sum(val));
            var rowCm = 2;
            foreach (var cl in clientes)
            {
                wsCliMot.Cell(rowCm, 1).Value = cl.Nombre;
                for (var c = 0; c < motivosPresentes.Count; c++)
                    wsCliMot.Cell(rowCm, c + 2).Value =
                        mapaCliMot.TryGetValue((cl.Id, motivosPresentes[c].Id), out var x) ? x : 0;
                wsCliMot.Cell(rowCm, motivosPresentes.Count + 2).Value = cl.Total;
                rowCm++;
            }
            wsCliMot.Row(1).Style.Font.Bold = true;
            wsCliMot.Column(motivosPresentes.Count + 2).Style.Font.Bold = true;
            wsCliMot.SheetView.FreezeRows(1);
            wsCliMot.Columns().AdjustToContents(1, Math.Min(clientes.Count + 2, 500));
        }

        // --- Hoja final: Viajes uno por uno (drill-down) ----------------------------
        if (viajes is { Count: > 0 })
        {
            var wsV = wb.Worksheets.Add("Viajes");
            string[] cab =
            {
                "Nº Reserva", "Fecha", "Hora", "Cliente", "Tipo de unidad", "Servicio", "Recorrido",
                "Pax", "Estado", "Motivo cancelación", "Interno", "Dominio", "Dueño / fletero",
                "Uso", "Chofer", "Grupo", "Origen"
            };
            for (var c = 0; c < cab.Length; c++)
                wsV.Cell(1, c + 1).Value = cab[c];

            var rV = 2;
            foreach (var d in viajes)
            {
                var v = d.Reserva;
                wsV.Cell(rV, 1).Value = v.IdViaje;
                wsV.Cell(rV, 2).Value = v.Fecha.ToDateTime(TimeOnly.MinValue);
                wsV.Cell(rV, 2).Style.DateFormat.Format = "dd/mm/yyyy";
                wsV.Cell(rV, 3).Value = v.Hora;
                wsV.Cell(rV, 4).Value = v.Cliente;
                wsV.Cell(rV, 5).Value = d.TipoSegun(criterio);
                wsV.Cell(rV, 6).Value = v.Servicio;
                wsV.Cell(rV, 7).Value = v.Recorrido;
                wsV.Cell(rV, 8).Value = v.Pax;
                wsV.Cell(rV, 9).Value = v.Estado;
                wsV.Cell(rV, 10).Value = d.Motivo;
                if (v.Interno.HasValue) wsV.Cell(rV, 11).Value = v.Interno.Value;
                wsV.Cell(rV, 12).Value = d.Dominio;
                wsV.Cell(rV, 13).Value = d.Fletero;
                wsV.Cell(rV, 14).Value = d.Uso;
                wsV.Cell(rV, 15).Value = v.Chofer;
                wsV.Cell(rV, 16).Value = v.Grupo;
                wsV.Cell(rV, 17).Value = v.Origen == "P" ? "Plantilla" : "Transportación";
                rV++;
            }
            wsV.Row(1).Style.Font.Bold = true;
            wsV.SheetView.FreezeRows(1);
            wsV.Columns().AdjustToContents(1, Math.Min(viajes.Count + 1, 500));
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta el informe "Viajes por Choferes" (form viaje_analisis_chofer.scx).
    /// Hoja 1: Resumen por chofer. Hoja 2: Pivote chofer × día/mes (con francos, como el
    /// FoxPro). Hoja 3: Mix por tipo de servicio. Hoja 4: Viajes uno por uno (drill-down).
    /// </summary>
    /// <param name="porMes">Agrupación de las columnas del pivote: false = día, true = mes.</param>
    /// <param name="categoriaFoco">
    /// Tipo de servicio enfocado en el tablero (Empresa/Turismo/Nortur) o null = todos. Las
    /// hojas 1, 2 y 4 salen con ese recorte, para que el Excel diga lo mismo que la pantalla.
    /// La hoja 3 (mix) siempre usa el dataset COMPLETO: su razón de ser es comparar las
    /// categorías entre sí, y con el foco puesto quedaría una sola columna.
    /// </param>
    public byte[] ViajesPorChofer(
        IReadOnlyList<ViajesChoferRow> filas,
        string metrica /* "Viajes" | "Km" | "Pax" | "Horas" */,
        DateOnly desde,
        DateOnly hasta,
        bool porMes = false,
        string? categoriaFoco = null,
        IReadOnlyList<ViajesChoferDetalleRow>? viajes = null)
    {
        // Horas sale en horas DECIMALES (Minutos/60), no en minutos crudos — el resto de las
        // métricas son conteos enteros (viajes/km/pax) así que el pivote queda con "0.0" solo
        // en la columna/hoja de Horas (ver el NumberFormat más abajo).
        Func<ViajesChoferRow, double> val = metrica switch
        {
            "Km" => f => f.Km,
            "Pax" => f => f.Pax,
            "Horas" => f => f.Minutos / 60.0,
            _ => f => f.Viajes
        };
        var etiqueta = metrica switch { "Km" => "Km", "Pax" => "Pax", "Horas" => "Horas", _ => "Viajes" };
        var formatoNumero = metrica == "Horas" ? "0.0" : "#,##0";

        // Dataset con el foco de tipo de servicio aplicado (lo que se ve en pantalla).
        var datos = categoriaFoco is null
            ? filas
            : filas.Where(f => f.Categoria == categoriaFoco).ToList();

        // Clave y etiqueta de columna según la agrupación elegida.
        string ColKey(DateOnly d) => porMes ? d.ToString("yyyy-MM") : d.ToString("yyyy-MM-dd");
        var cultura = System.Globalization.CultureInfo.GetCultureInfo("es-AR");

        using var wb = new XLWorkbook();
        var sufijo = categoriaFoco is null ? "" : $" — {categoriaFoco}";

        // --- Hoja 1: Resumen por chofer -------------------------------------
        // Siempre en conteos/totales reales (viajes, km, pax, horas), sin importar qué métrica
        // esté elegida en la pantalla — igual que ya hacían Km y Pax.
        var wsR = wb.Worksheets.Add("Resumen por chofer");
        string[] hR = { "Código", "Chofer", "Localidad", "Tipo", "Viajes", "Empresa", "Turismo", "Nortur", "Km", "Pax", "Horas", "Días con actividad" };
        for (var c = 0; c < hR.Length; c++) wsR.Cell(1, c + 1).Value = hR[c];
        var porChofer = datos
            .GroupBy(f => f.IdChofer)
            .Select(g => new
            {
                Id = g.Key,
                Nombre = g.First().Chofer,
                Localidad = g.First().Localidad,
                Tipo = g.First().Tipo,
                Viajes = g.Sum(x => x.Viajes),
                Empresa = g.Where(x => x.Categoria == "Empresa").Sum(x => x.Viajes),
                Turismo = g.Where(x => x.Categoria == "Turismo").Sum(x => x.Viajes),
                Nortur = g.Where(x => x.Categoria == "Nortur").Sum(x => x.Viajes),
                Km = g.Sum(x => x.Km),
                Pax = g.Sum(x => x.Pax),
                Horas = Math.Round(g.Sum(x => x.Minutos) / 60.0, 1),
                Dias = g.Select(x => x.Fecha).Distinct().Count()
            })
            .OrderByDescending(x => x.Viajes).ThenBy(x => x.Nombre)
            .ToList();
        var r = 2;
        foreach (var c in porChofer)
        {
            wsR.Cell(r, 1).Value = c.Id;
            wsR.Cell(r, 2).Value = c.Nombre;
            wsR.Cell(r, 3).Value = c.Localidad;
            wsR.Cell(r, 4).Value = c.Tipo;
            wsR.Cell(r, 5).Value = c.Viajes;
            wsR.Cell(r, 6).Value = c.Empresa;
            wsR.Cell(r, 7).Value = c.Turismo;
            wsR.Cell(r, 8).Value = c.Nortur;
            wsR.Cell(r, 9).Value = c.Km;
            wsR.Cell(r, 10).Value = c.Pax;
            wsR.Cell(r, 11).Value = c.Horas;
            wsR.Cell(r, 12).Value = c.Dias;
            r++;
        }
        if (porChofer.Count > 0)
            wsR.Column(11).Style.NumberFormat.Format = "0.0";
        wsR.Row(1).Style.Font.Bold = true;
        wsR.SheetView.FreezeRows(1);
        wsR.Columns().AdjustToContents(1, Math.Min(porChofer.Count + 1, 500));

        // --- Hoja 2: Pivote chofer × día o mes (con francos) ----------------
        // Las columnas se arman recorriendo el rango COMPLETO (no solo los días con datos),
        // así el pivote muestra los huecos igual que la grilla de la pantalla.
        var cols = new List<(string Key, string Label)>();
        if (porMes)
        {
            for (var d = new DateOnly(desde.Year, desde.Month, 1); d <= hasta; d = d.AddMonths(1))
                cols.Add((d.ToString("yyyy-MM"), d.ToString("MMM yyyy", cultura)));
        }
        else
        {
            for (var d = desde; d <= hasta; d = d.AddDays(1))
                cols.Add((d.ToString("yyyy-MM-dd"), d.ToString("dd/MM")));
        }
        var idxCol = cols.Select((c, i) => (c.Key, i)).ToDictionary(t => t.Key, t => t.i);
        var mapa = datos.GroupBy(f => (f.IdChofer, Col: ColKey(f.Fecha)))
                        .ToDictionary(g => g.Key, g => g.Sum(val));

        var wsP = wb.Worksheets.Add($"Pivote {etiqueta} por {(porMes ? "mes" : "día")}");
        wsP.Cell(1, 1).Value = $"Chofer{sufijo}";
        for (var c = 0; c < cols.Count; c++) wsP.Cell(1, c + 2).Value = cols[c].Label;
        wsP.Cell(1, cols.Count + 2).Value = "TOTAL";

        var choferes = datos.GroupBy(f => f.IdChofer)
            .Select(g => (Id: g.Key, Nombre: g.First().Chofer, Total: g.Sum(val)))
            .OrderByDescending(x => x.Total).ThenBy(x => x.Nombre)
            .ToList();
        for (var i = 0; i < choferes.Count; i++)
        {
            var ch = choferes[i];
            wsP.Cell(i + 2, 1).Value = ch.Nombre;

            // Franco = columna sin actividad ENTRE la primera y la última con actividad del
            // chofer (mismo criterio que el FoxPro, generalizado a la columna elegida).
            var conDatos = cols.Where(c => mapa.ContainsKey((ch.Id, c.Key)))
                               .Select(c => idxCol[c.Key]).ToList();
            var primero = conDatos.Count > 0 ? conDatos.Min() : -1;
            var ultimo = conDatos.Count > 0 ? conDatos.Max() : -1;

            for (var c = 0; c < cols.Count; c++)
            {
                if (mapa.TryGetValue((ch.Id, cols[c].Key), out var v))
                    wsP.Cell(i + 2, c + 2).Value = v;
                else if (c >= primero && c <= ultimo)
                    wsP.Cell(i + 2, c + 2).Value = "F";
            }
            wsP.Cell(i + 2, cols.Count + 2).Value = ch.Total;
        }
        if (choferes.Count > 0)
            wsP.Range(2, 2, choferes.Count + 1, cols.Count + 2).Style.NumberFormat.Format = formatoNumero;
        wsP.Row(1).Style.Font.Bold = true;
        wsP.Column(cols.Count + 2).Style.Font.Bold = true;
        wsP.SheetView.FreezeRows(1);
        wsP.SheetView.FreezeColumns(1);
        wsP.Columns().AdjustToContents(1, Math.Min(choferes.Count + 1, 500));

        // --- Hoja 3: Mix por tipo de servicio -------------------------------
        // Qué hace cada chofer: viajes/km/pax por categoría + el % que representa cada una
        // sobre su total. Responde "¿este chofer es de empresa o de turismo?".
        // SIEMPRE sobre el dataset completo (ver el <param> de categoriaFoco).
        var wsM = wb.Worksheets.Add("Mix por tipo de servicio");
        string[] hM =
        {
            "Código", "Chofer", "Tipo",
            "Viajes Empresa", "Viajes Turismo", "Viajes Nortur", "Viajes TOTAL",
            "% Empresa", "% Turismo", "% Nortur",
            "Km Empresa", "Km Turismo", "Km Nortur", "Km TOTAL",
            "Pax Empresa", "Pax Turismo", "Pax Nortur", "Pax TOTAL",
            "Horas Empresa", "Horas Turismo", "Horas Nortur", "Horas TOTAL",
            "Perfil"
        };
        for (var c = 0; c < hM.Length; c++) wsM.Cell(1, c + 1).Value = hM[c];

        var mix = filas.GroupBy(f => f.IdChofer)
            .Select(g =>
            {
                int V(string cat) => g.Where(x => x.Categoria == cat).Sum(x => x.Viajes);
                int K(string cat) => g.Where(x => x.Categoria == cat).Sum(x => x.Km);
                int P(string cat) => g.Where(x => x.Categoria == cat).Sum(x => x.Pax);
                double H(string cat) => Math.Round(g.Where(x => x.Categoria == cat).Sum(x => x.Minutos) / 60.0, 1);
                var tot = g.Sum(x => x.Viajes);
                return new
                {
                    Id = g.Key,
                    Nombre = g.First().Chofer,
                    Tipo = g.First().Tipo,
                    VE = V("Empresa"), VT = V("Turismo"), VN = V("Nortur"), VTot = tot,
                    KE = K("Empresa"), KT = K("Turismo"), KN = K("Nortur"), KTot = g.Sum(x => x.Km),
                    PE = P("Empresa"), PT = P("Turismo"), PN = P("Nortur"), PTot = g.Sum(x => x.Pax),
                    HE = H("Empresa"), HT = H("Turismo"), HN = H("Nortur"), HTot = Math.Round(g.Sum(x => x.Minutos) / 60.0, 1),
                };
            })
            .OrderByDescending(x => x.VTot).ThenBy(x => x.Nombre)
            .ToList();

        r = 2;
        foreach (var m in mix)
        {
            double Pct(int n) => m.VTot > 0 ? (double)n / m.VTot : 0;
            // "Perfil": la categoría dominante, o Mixto si ninguna llega al 70% de sus viajes.
            var top = new[] { ("Empresa", m.VE), ("Turismo", m.VT), ("Nortur", m.VN) }
                .OrderByDescending(t => t.Item2).First();
            var perfil = m.VTot == 0 ? "—" : Pct(top.Item2) >= 0.70 ? top.Item1 : "Mixto";

            wsM.Cell(r, 1).Value = m.Id;
            wsM.Cell(r, 2).Value = m.Nombre;
            wsM.Cell(r, 3).Value = m.Tipo;
            wsM.Cell(r, 4).Value = m.VE;
            wsM.Cell(r, 5).Value = m.VT;
            wsM.Cell(r, 6).Value = m.VN;
            wsM.Cell(r, 7).Value = m.VTot;
            wsM.Cell(r, 8).Value = Pct(m.VE);
            wsM.Cell(r, 9).Value = Pct(m.VT);
            wsM.Cell(r, 10).Value = Pct(m.VN);
            wsM.Cell(r, 11).Value = m.KE;
            wsM.Cell(r, 12).Value = m.KT;
            wsM.Cell(r, 13).Value = m.KN;
            wsM.Cell(r, 14).Value = m.KTot;
            wsM.Cell(r, 15).Value = m.PE;
            wsM.Cell(r, 16).Value = m.PT;
            wsM.Cell(r, 17).Value = m.PN;
            wsM.Cell(r, 18).Value = m.PTot;
            wsM.Cell(r, 19).Value = m.HE;
            wsM.Cell(r, 20).Value = m.HT;
            wsM.Cell(r, 21).Value = m.HN;
            wsM.Cell(r, 22).Value = m.HTot;
            wsM.Cell(r, 23).Value = perfil;
            r++;
        }
        if (mix.Count > 0)
        {
            wsM.Range(2, 8, mix.Count + 1, 10).Style.NumberFormat.Format = "0 %";
            wsM.Range(2, 19, mix.Count + 1, 22).Style.NumberFormat.Format = "0.0";
        }
        wsM.Row(1).Style.Font.Bold = true;
        wsM.SheetView.FreezeRows(1);
        wsM.Columns().AdjustToContents(1, Math.Min(mix.Count + 1, 500));

        // --- Hoja 4: Viajes uno por uno -------------------------------------
        if (viajes is { Count: > 0 })
            AgregarHojaViajes(wb, viajes.Select(v => v.Reserva).ToList());

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta el informe "Km Unidades vs Servicios" (form viaje_analisis_km.scx).
    /// Hoja 1: por unidad (km servicio / recorrido / vacío / % vacío / días).
    /// Hoja 2: Viajes uno por uno (drill-down).
    /// </summary>
    public byte[] KmUnidadesServicios(
        IReadOnlyList<KmUnidadRow> filas,
        DateOnly desde,
        DateOnly hasta,
        IReadOnlyList<KmUnidadDetalleRow>? viajes = null)
    {
        using var wb = new XLWorkbook();

        var ws = wb.Worksheets.Add("Km por unidad");
        string[] h =
        {
            "Dominio", "Interno", "Tipo", "Servicios", "Km servicio",
            "Km recorrido", "Km vacío", "% vacío", "Días trabajados", "Consumo"
        };
        for (var c = 0; c < h.Length; c++) ws.Cell(1, c + 1).Value = h[c];

        var ordenadas = filas.OrderByDescending(f => f.KmServicio).ThenBy(f => f.Dominio).ToList();
        var r = 2;
        foreach (var u in ordenadas)
        {
            ws.Cell(r, 1).Value = u.Dominio;
            if (u.Interno > 0) ws.Cell(r, 2).Value = u.Interno;
            ws.Cell(r, 3).Value = u.TipoVeh;
            ws.Cell(r, 4).Value = u.Servicios;
            ws.Cell(r, 5).Value = u.KmServicio;
            if (u.TieneOdometro)
            {
                ws.Cell(r, 6).Value = u.KmRecorrido;
                ws.Cell(r, 7).Value = u.KmVacio;
                ws.Cell(r, 8).Value = u.KmRecorrido > 0 ? Math.Round(100.0 * u.KmVacio / u.KmRecorrido, 1) : 0;
                ws.Cell(r, 8).Style.NumberFormat.Format = "0.0";
            }
            else
            {
                ws.Cell(r, 6).Value = "—";
                ws.Cell(r, 7).Value = "—";
                ws.Cell(r, 8).Value = "—";
            }
            ws.Cell(r, 9).Value = u.DiasTrabajados;
            if (u.Consumo > 0) ws.Cell(r, 10).Value = u.Consumo;
            r++;
        }
        // Fila TOTAL
        ws.Cell(r, 1).Value = "TOTAL";
        ws.Cell(r, 4).Value = ordenadas.Sum(x => x.Servicios);
        ws.Cell(r, 5).Value = ordenadas.Sum(x => x.KmServicio);
        ws.Cell(r, 6).Value = ordenadas.Where(x => x.TieneOdometro).Sum(x => x.KmRecorrido);
        ws.Cell(r, 7).Value = ordenadas.Where(x => x.TieneOdometro).Sum(x => x.KmVacio);
        ws.Row(r).Style.Font.Bold = true;
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(ordenadas.Count + 2, 500));

        if (viajes is { Count: > 0 })
            AgregarHojaViajes(wb, viajes.Select(v => v.Reserva).ToList());

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // Hoja "Viajes" reutilizable (drill-down uno por uno) para los informes de flota.
    private static void AgregarHojaViajes(XLWorkbook wb, IReadOnlyList<ReservaFsDetalleRow> viajes)
    {
        var wsV = wb.Worksheets.Add("Viajes");
        string[] cab =
        {
            "Nº Reserva", "Fecha", "Hora", "Cliente", "Servicio", "Recorrido",
            "Pax", "Estado", "Interno", "Chofer", "Grupo", "Origen"
        };
        for (var c = 0; c < cab.Length; c++) wsV.Cell(1, c + 1).Value = cab[c];
        var rV = 2;
        foreach (var v in viajes)
        {
            wsV.Cell(rV, 1).Value = v.IdViaje;
            wsV.Cell(rV, 2).Value = v.Fecha.ToDateTime(TimeOnly.MinValue);
            wsV.Cell(rV, 2).Style.DateFormat.Format = "dd/mm/yyyy";
            wsV.Cell(rV, 3).Value = v.Hora;
            wsV.Cell(rV, 4).Value = v.Cliente;
            wsV.Cell(rV, 5).Value = v.Servicio;
            wsV.Cell(rV, 6).Value = v.Recorrido;
            wsV.Cell(rV, 7).Value = v.Pax;
            wsV.Cell(rV, 8).Value = v.Estado;
            if (v.Interno.HasValue) wsV.Cell(rV, 9).Value = v.Interno.Value;
            wsV.Cell(rV, 10).Value = v.Chofer;
            wsV.Cell(rV, 11).Value = v.Grupo;
            wsV.Cell(rV, 12).Value = v.Origen == "P" ? "Plantilla" : "Transportación";
            rV++;
        }
        wsV.Row(1).Style.Font.Bold = true;
        wsV.SheetView.FreezeRows(1);
        wsV.Columns().AdjustToContents(1, Math.Min(viajes.Count + 1, 500));
    }

    /// <summary>
    /// Exporta el Panel de Flota en tres hojas: el resumen por la dimensión abierta, el cruce
    /// oferta ↔ demanda por tipo y el padrón de unidades una por una (la hoja que se lleva el
    /// usuario para trabajar). Los agregados llegan ya calculados desde la pantalla, así el
    /// Excel muestra EXACTAMENTE los mismos números que se ven.
    /// </summary>
    public byte[] PanelFlota(
        IReadOnlyList<FlotaUnidadRow> unidades,
        IReadOnlyList<FlotaResumenCatRow> resumen,
        IReadOnlyList<FlotaOfertaDemandaRow> ofertaDemanda,
        string tituloDimension,
        DateOnly desde,
        DateOnly hasta)
    {
        using var wb = new XLWorkbook();

        // --- Hoja 1: resumen por la dimensión elegida ------------------------
        var wsR = wb.Worksheets.Add("Resumen");
        string[] hR =
        {
            tituloDimension, "Unidades", "Propias", "Contratadas", "De baja", "Butacas",
            "Antigüedad prom.", "Sin uso", "Viajes", "Km", "Unidades con km"
        };
        for (var c = 0; c < hR.Length; c++) wsR.Cell(1, c + 1).Value = hR[c];

        var r = 2;
        foreach (var x in resumen)
        {
            wsR.Cell(r, 1).Value = x.Cat;
            wsR.Cell(r, 2).Value = x.Unidades;
            wsR.Cell(r, 3).Value = x.Propias;
            wsR.Cell(r, 4).Value = x.Contratadas;
            wsR.Cell(r, 5).Value = x.Bajas;
            wsR.Cell(r, 6).Value = x.Butacas;
            if (x.AntiguedadProm is double a)
            {
                wsR.Cell(r, 7).Value = Math.Round(a, 1);
                wsR.Cell(r, 7).Style.NumberFormat.Format = "0.0";
            }
            wsR.Cell(r, 8).Value = x.Ociosas;
            wsR.Cell(r, 9).Value = x.Viajes;
            wsR.Cell(r, 10).Value = x.Km;
            wsR.Cell(r, 11).Value = x.UnidadesConKm;
            r++;
        }
        wsR.Cell(r, 1).Value = "TOTAL";
        wsR.Cell(r, 2).Value = resumen.Sum(x => x.Unidades);
        wsR.Cell(r, 3).Value = resumen.Sum(x => x.Propias);
        wsR.Cell(r, 4).Value = resumen.Sum(x => x.Contratadas);
        wsR.Cell(r, 5).Value = resumen.Sum(x => x.Bajas);
        wsR.Cell(r, 6).Value = resumen.Sum(x => x.Butacas);
        wsR.Cell(r, 8).Value = resumen.Sum(x => x.Ociosas);
        wsR.Cell(r, 9).Value = resumen.Sum(x => x.Viajes);
        wsR.Cell(r, 10).Value = resumen.Sum(x => x.Km);
        wsR.Row(r).Style.Font.Bold = true;

        // El período solo mide actividad: el plantel es la foto de hoy (`vehiculo` no guarda
        // historia). Va escrito en la hoja para que el Excel no se lea fuera de contexto.
        wsR.Cell(r + 2, 1).Value =
            $"Plantel a hoy ({DateTime.Today:dd/MM/yyyy}). Viajes, días y km corresponden al período "
            + $"{desde:dd/MM/yyyy} – {hasta:dd/MM/yyyy}.";
        wsR.Cell(r + 3, 1).Value =
            $"Los km excluyen las lecturas de odómetro que superan {ReportService.KmMaximoPorMes:#,0} km en un mes (errores de carga).";
        wsR.Row(1).Style.Font.Bold = true;
        wsR.SheetView.FreezeRows(1);
        wsR.Columns().AdjustToContents(1, Math.Min(resumen.Count + 4, 500));

        // --- Hoja 2: oferta vs demanda por tipo ------------------------------
        var wsD = wb.Worksheets.Add("Oferta vs demanda");
        string[] hD =
        {
            "Tipo", "Unidades activas", "Trabajaron", "Viajes pedidos", "Sin asignar",
            "% sin cubrir", "Viajes por unidad"
        };
        for (var c = 0; c < hD.Length; c++) wsD.Cell(1, c + 1).Value = hD[c];

        var rD = 2;
        foreach (var x in ofertaDemanda)
        {
            wsD.Cell(rD, 1).Value = x.Tipo;
            wsD.Cell(rD, 2).Value = x.Unidades;
            wsD.Cell(rD, 3).Value = x.Trabajaron;
            wsD.Cell(rD, 4).Value = x.Pedidos;
            wsD.Cell(rD, 5).Value = x.SinAsignar;
            if (x.Pedidos > 0)
            {
                wsD.Cell(rD, 6).Value = Math.Round(x.PctSinCubrir, 1);
                wsD.Cell(rD, 6).Style.NumberFormat.Format = "0.0";
            }
            if (x.Unidades > 0 && x.Pedidos > 0)
            {
                wsD.Cell(rD, 7).Value = Math.Round(x.ViajesPorUnidad, 1);
                wsD.Cell(rD, 7).Style.NumberFormat.Format = "0.0";
            }
            rD++;
        }
        wsD.Cell(rD, 1).Value = "TOTAL";
        wsD.Cell(rD, 2).Value = ofertaDemanda.Sum(x => x.Unidades);
        wsD.Cell(rD, 3).Value = ofertaDemanda.Sum(x => x.Trabajaron);
        wsD.Cell(rD, 4).Value = ofertaDemanda.Sum(x => x.Pedidos);
        wsD.Cell(rD, 5).Value = ofertaDemanda.Sum(x => x.SinAsignar);
        wsD.Row(rD).Style.Font.Bold = true;
        wsD.Cell(rD + 2, 1).Value =
            "Sin asignar = viajes que se pidieron de ese tipo de vehículo y quedaron sin unidad. "
            + "El tipo pedido sale de la reserva y puede no existir en la flota (y al revés).";
        wsD.Row(1).Style.Font.Bold = true;
        wsD.SheetView.FreezeRows(1);
        wsD.Columns().AdjustToContents(1, Math.Min(ofertaDemanda.Count + 3, 200));

        // --- Hoja 3: padrón de unidades, una por una -------------------------
        var wsU = wb.Worksheets.Add("Unidades");
        string[] hU =
        {
            "Interno", "Dominio", "Tipo", "Marca y modelo", "Año", "Butacas", "Antigüedad",
            "Uso", "Titular", "Situación", "Estado", "Viajes", "Pax transportados",
            "Días trabajados", "Km", "Meses odóm. OK", "Meses odóm. descartados", "Fecha de baja"
        };
        for (var c = 0; c < hU.Length; c++) wsU.Cell(1, c + 1).Value = hU[c];

        var rU = 2;
        foreach (var u in unidades.OrderBy(x => x.Interno == 0 ? int.MaxValue : x.Interno)
                                  .ThenBy(x => x.Dominio))
        {
            wsU.Cell(rU, 1).Value = u.InternoNT;
            wsU.Cell(rU, 2).Value = u.Dominio;
            wsU.Cell(rU, 3).Value = u.Tipo;
            wsU.Cell(rU, 4).Value = u.Marca;
            if (u.Antiguedad is not null) wsU.Cell(rU, 5).Value = u.Modelo;
            if (u.Pax > 0) wsU.Cell(rU, 6).Value = u.Pax;
            if (u.Antiguedad is int ant) wsU.Cell(rU, 7).Value = ant;
            wsU.Cell(rU, 8).Value = u.Uso;
            wsU.Cell(rU, 9).Value = u.Titular;
            wsU.Cell(rU, 10).Value = u.EsBaja ? "De baja" : u.EsOciosa ? "Sin uso" : "Operativa";
            wsU.Cell(rU, 11).Value = u.Estado;
            wsU.Cell(rU, 12).Value = u.Viajes;
            wsU.Cell(rU, 13).Value = u.PaxTransportados;
            wsU.Cell(rU, 14).Value = u.DiasTrabajados;
            if (u.TieneKm) wsU.Cell(rU, 15).Value = u.Km;
            wsU.Cell(rU, 16).Value = u.MesesOdometroOk;
            wsU.Cell(rU, 17).Value = u.MesesOdometroRaro;
            if (u.FBaja is DateOnly fb)
            {
                wsU.Cell(rU, 18).Value = fb.ToDateTime(TimeOnly.MinValue);
                wsU.Cell(rU, 18).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            rU++;
        }
        wsU.Row(1).Style.Font.Bold = true;
        wsU.SheetView.FreezeRows(1);
        wsU.Columns().AdjustToContents(1, Math.Min(unidades.Count + 1, 500));

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta el Panel de Clientes. Hoja 1: el ranking tal como se ve (con participación y
    /// Pareto). Hoja 2: el pivote cliente × mes de la métrica elegida. Hoja 3: el padrón
    /// completo con el segmento de actividad y los datos que le faltan a cada registro —
    /// esa hoja es la lista accionable para depurar la base.
    /// </summary>
    public byte[] PanelClientes(
        IReadOnlyList<ClienteResumenRow> ranking,
        IReadOnlyList<ClienteMesRow> filas,
        IReadOnlyList<string> meses,
        IReadOnlyList<ClientePadronRow> padron,
        string tituloDimension,
        string metrica,
        DateOnly corte,
        DateOnly desde,
        DateOnly hasta)
    {
        using var wb = new XLWorkbook();
        const string fmtPlata = "#,##0.00";

        // --- Hoja 1: ranking de clientes -------------------------------------
        var wsR = wb.Worksheets.Add("Ranking");
        string[] hR =
        {
            "Código", "Cliente", tituloDimension, "Facturado", "Facturado USD", "Viajes",
            "Cancelados", "% cancelado", "Pax", "Facturado por viaje", "% del total",
            "% acumulado", "Última reserva", "Segmento", "Tipo fiscal", "Lista de precios"
        };
        for (var c = 0; c < hR.Length; c++) wsR.Cell(1, c + 1).Value = hR[c];

        // El total y el acumulado se calculan sobre la MISMA métrica que se ve en pantalla.
        decimal Met(ClienteResumenRow x) => metrica switch
        {
            ReportService.MetCliViajes => x.Viajes,
            ReportService.MetCliPax => x.Pax,
            _ => x.Facturado,
        };
        var totalMet = ranking.Sum(Met);

        var r = 2;
        decimal corrido = 0;
        foreach (var x in ranking)
        {
            corrido += Met(x);
            wsR.Cell(r, 1).Value = x.IdCliente;
            wsR.Cell(r, 2).Value = x.Nombre;
            wsR.Cell(r, 3).Value = x.Categoria;
            wsR.Cell(r, 4).Value = x.Facturado; wsR.Cell(r, 4).Style.NumberFormat.Format = fmtPlata;
            if (x.FacturadoUsd > 0) { wsR.Cell(r, 5).Value = x.FacturadoUsd; wsR.Cell(r, 5).Style.NumberFormat.Format = fmtPlata; }
            wsR.Cell(r, 6).Value = x.Viajes;
            wsR.Cell(r, 7).Value = x.Cancelados;
            if (x.Viajes > 0) { wsR.Cell(r, 8).Value = Math.Round(x.PctCancelado, 1); wsR.Cell(r, 8).Style.NumberFormat.Format = "0.0"; }
            wsR.Cell(r, 9).Value = x.Pax;
            if (x.FacturadoPorViaje is decimal pv) { wsR.Cell(r, 10).Value = pv; wsR.Cell(r, 10).Style.NumberFormat.Format = fmtPlata; }
            if (totalMet > 0)
            {
                wsR.Cell(r, 11).Value = Math.Round((double)(Met(x) * 100 / totalMet), 2);
                wsR.Cell(r, 11).Style.NumberFormat.Format = "0.00";
                wsR.Cell(r, 12).Value = Math.Round((double)(corrido * 100 / totalMet), 2);
                wsR.Cell(r, 12).Style.NumberFormat.Format = "0.00";
            }
            if (x.UltimaReserva is DateOnly ur)
            {
                wsR.Cell(r, 13).Value = ur.ToDateTime(TimeOnly.MinValue);
                wsR.Cell(r, 13).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            wsR.Cell(r, 14).Value = x.Segmento;
            wsR.Cell(r, 15).Value = x.Fiscal;
            wsR.Cell(r, 16).Value = x.ListaPrecio;
            r++;
        }
        wsR.Cell(r, 1).Value = "TOTAL";
        wsR.Cell(r, 4).Value = ranking.Sum(x => x.Facturado); wsR.Cell(r, 4).Style.NumberFormat.Format = fmtPlata;
        wsR.Cell(r, 5).Value = ranking.Sum(x => x.FacturadoUsd); wsR.Cell(r, 5).Style.NumberFormat.Format = fmtPlata;
        wsR.Cell(r, 6).Value = ranking.Sum(x => x.Viajes);
        wsR.Cell(r, 7).Value = ranking.Sum(x => x.Cancelados);
        wsR.Cell(r, 9).Value = ranking.Sum(x => x.Pax);
        wsR.Row(r).Style.Font.Bold = true;

        // Las decisiones de cálculo van escritas en la hoja: el Excel se reenvía por mail y se
        // lee fuera de contexto.
        wsR.Cell(r + 2, 1).Value = $"Período {desde:dd/MM/yyyy} – {hasta:dd/MM/yyyy}. Métrica del ranking: {metrica}.";
        wsR.Cell(r + 3, 1).Value =
            "La facturación está DEVENGADA AL MES DEL VIAJE (no al de la liquidación) y sale del detalle "
            + "de liquidación (importe + incremento − descuento), no del total de la cabecera.";
        wsR.Cell(r + 4, 1).Value =
            "Importes en PESOS CORRIENTES: los facturados en dólares se convierten con el tipo de cambio "
            + "de cada liquidación. Comparar años distintos en pesos sobrestima el crecimiento por inflación.";
        wsR.Cell(r + 5, 1).Value = $"Segmento de actividad medido contra el último día con datos: {corte:dd/MM/yyyy}.";
        wsR.Row(1).Style.Font.Bold = true;
        wsR.SheetView.FreezeRows(1);
        wsR.Columns().AdjustToContents(1, Math.Min(ranking.Count + 1, 500));

        // --- Hoja 2: pivote cliente × mes ------------------------------------
        var wsP = wb.Worksheets.Add($"{metrica} por mes");
        wsP.Cell(1, 1).Value = "Código";
        wsP.Cell(1, 2).Value = "Cliente";
        for (var c = 0; c < meses.Count; c++)
        {
            var m = meses[c];
            wsP.Cell(1, c + 3).Value = m.Length == 7 ? $"{m.Substring(5, 2)}/{m.Substring(0, 4)}" : m;
        }
        wsP.Cell(1, meses.Count + 3).Value = "TOTAL";

        decimal MetMes(ClienteMesRow x) => metrica switch
        {
            ReportService.MetCliViajes => x.Viajes,
            ReportService.MetCliPax => x.Pax,
            _ => x.Facturado,
        };
        var mapa = filas.GroupBy(f => (f.IdCliente, f.Mes))
                        .ToDictionary(g => g.Key, g => g.Sum(MetMes));

        var rP = 2;
        foreach (var x in ranking)
        {
            wsP.Cell(rP, 1).Value = x.IdCliente;
            wsP.Cell(rP, 2).Value = x.Nombre;
            decimal totFila = 0;
            for (var c = 0; c < meses.Count; c++)
            {
                var v = mapa.TryGetValue((x.IdCliente, meses[c]), out var val) ? val : 0;
                totFila += v;
                if (v == 0) continue;
                wsP.Cell(rP, c + 3).Value = v;
                if (metrica == ReportService.MetCliFacturado) wsP.Cell(rP, c + 3).Style.NumberFormat.Format = fmtPlata;
            }
            wsP.Cell(rP, meses.Count + 3).Value = totFila;
            if (metrica == ReportService.MetCliFacturado) wsP.Cell(rP, meses.Count + 3).Style.NumberFormat.Format = fmtPlata;
            rP++;
        }
        wsP.Row(1).Style.Font.Bold = true;
        wsP.SheetView.FreezeRows(1);
        wsP.Columns().AdjustToContents(1, Math.Min(ranking.Count + 1, 500));

        // --- Hoja 3: padrón completo (la lista para depurar) ------------------
        var wsC = wb.Worksheets.Add("Padrón");
        string[] hC =
        {
            "Código", "Cliente", "Segmento", "Última reserva", "Primera reserva",
            "Viajes históricos", "Tipo fiscal", "CUIT", "Teléfono", "E-mail", "Contacto",
            "Localidad", "Provincia", "Lista de precios", "Obtención de precios",
            "Tarifario propio", "Sin precios", "Candidato a baja",
            "Descuento", "Incremento", "Alta", "Inhabilitado", "Datos que faltan"
        };
        for (var c = 0; c < hC.Length; c++) wsC.Cell(1, c + 1).Value = hC[c];

        var rC = 2;
        foreach (var p in padron.OrderBy(x => ClientePadronRow.OrdenSegmento(x.Segmento(corte)))
                                .ThenByDescending(x => x.ViajesHistoricos))
        {
            wsC.Cell(rC, 1).Value = p.IdCliente;
            wsC.Cell(rC, 2).Value = p.Display;
            wsC.Cell(rC, 3).Value = p.Segmento(corte);
            if (p.UltimaReserva is DateOnly u) { wsC.Cell(rC, 4).Value = u.ToDateTime(TimeOnly.MinValue); wsC.Cell(rC, 4).Style.DateFormat.Format = "dd/mm/yyyy"; }
            if (p.PrimeraReserva is DateOnly pr) { wsC.Cell(rC, 5).Value = pr.ToDateTime(TimeOnly.MinValue); wsC.Cell(rC, 5).Style.DateFormat.Format = "dd/mm/yyyy"; }
            wsC.Cell(rC, 6).Value = p.ViajesHistoricos;
            wsC.Cell(rC, 7).Value = p.Fiscal;
            wsC.Cell(rC, 8).Value = p.Cuit;
            wsC.Cell(rC, 9).Value = p.Telefono;
            wsC.Cell(rC, 10).Value = p.Email;
            wsC.Cell(rC, 11).Value = p.Contacto;
            wsC.Cell(rC, 12).Value = p.Localidad;
            wsC.Cell(rC, 13).Value = p.Provincia;
            wsC.Cell(rC, 14).Value = p.ListaPrecio;
            wsC.Cell(rC, 15).Value = p.ObPrecio;
            wsC.Cell(rC, 16).Value = p.TieneTarifaPropia ? "Sí" : "";
            wsC.Cell(rC, 17).Value = p.SinPrecio ? "SIN PRECIOS" : "";
            wsC.Cell(rC, 18).Value = p.CandidatoBaja(corte) ? "REVISAR" : "";
            if (p.Descuento != 0) wsC.Cell(rC, 19).Value = p.Descuento;
            if (p.Incremento != 0) wsC.Cell(rC, 20).Value = p.Incremento;
            if (p.FAlta is DateOnly fa) { wsC.Cell(rC, 21).Value = fa.ToDateTime(TimeOnly.MinValue); wsC.Cell(rC, 21).Style.DateFormat.Format = "dd/mm/yyyy"; }
            if (p.FBaja is DateOnly fb) { wsC.Cell(rC, 22).Value = fb.ToDateTime(TimeOnly.MinValue); wsC.Cell(rC, 22).Style.DateFormat.Format = "dd/mm/yyyy"; }
            wsC.Cell(rC, 23).Value = string.Join(", ", p.Faltantes());
            rC++;
        }
        wsC.Cell(rC + 1, 1).Value =
            $"Segmento medido contra el último día con datos ({corte:dd/MM/yyyy}): Activo ≤ 90 días, "
            + "Tibio ≤ 1 año, Dormido ≤ 2 años, Inactivo más de 2 años, Nunca operó = sin una sola reserva.";
        wsC.Cell(rC + 2, 1).Value =
            "«Sin precios» = no tiene ni lista de precios ni tarifario propio cargado: al facturarlo no hay "
            + "de dónde sacar el importe. «Candidato a baja» = nunca operó o hace más de dos años que no pide "
            + "un servicio (es una sugerencia para revisar, no una baja automática).";
        wsC.Row(1).Style.Font.Bold = true;
        wsC.SheetView.FreezeRows(1);
        wsC.Columns().AdjustToContents(1, Math.Min(padron.Count + 1, 500));

        using var msC = new MemoryStream();
        wb.SaveAs(msC);
        return msC.ToArray();
    }

    /// <summary>
    /// Exporta la vista de Retención y riesgo: un cliente por fila con su comparación entre
    /// los dos períodos, la clase ABC y en qué estado quedó (se fue / cayó / estable / creció /
    /// nuevo). Es la lista con la que se sale a recuperar clientes.
    /// </summary>
    public byte[] RetencionClientes(
        IReadOnlyList<RetencionRow> filas,
        string metrica,
        string periodoActual,
        string periodoBase)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Retención");
        var esPlata = metrica == ReportService.MetCliFacturado;
        var fmt = esPlata ? "#,##0.00" : "#,##0";

        string[] h =
        {
            "Código", "Cliente", "Clase ABC", "Estado", "Segmento",
            $"{metrica} período", $"{metrica} base", "Variación", "Variación %",
            "Viajes", "Cancelados", "% cancelado", "Última reserva"
        };
        for (var c = 0; c < h.Length; c++) ws.Cell(1, c + 1).Value = h[c];

        var r = 2;
        foreach (var x in filas)
        {
            ws.Cell(r, 1).Value = x.IdCliente;
            ws.Cell(r, 2).Value = x.Nombre;
            ws.Cell(r, 3).Value = x.Clase;
            ws.Cell(r, 4).Value = x.EstadoTexto;
            ws.Cell(r, 5).Value = x.Segmento;
            ws.Cell(r, 6).Value = x.Actual; ws.Cell(r, 6).Style.NumberFormat.Format = fmt;
            ws.Cell(r, 7).Value = x.Base;   ws.Cell(r, 7).Style.NumberFormat.Format = fmt;
            ws.Cell(r, 8).Value = x.Delta;  ws.Cell(r, 8).Style.NumberFormat.Format = fmt;
            if (x.Pct is double p) { ws.Cell(r, 9).Value = Math.Round(p, 1); ws.Cell(r, 9).Style.NumberFormat.Format = "0.0"; }
            ws.Cell(r, 10).Value = x.Viajes;
            ws.Cell(r, 11).Value = x.Cancelados;
            if (x.Viajes > 0) { ws.Cell(r, 12).Value = Math.Round(x.PctCancelado, 1); ws.Cell(r, 12).Style.NumberFormat.Format = "0.0"; }
            if (x.UltimaReserva is DateOnly u)
            {
                ws.Cell(r, 13).Value = u.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 13).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            r++;
        }
        ws.Cell(r, 1).Value = "TOTAL";
        ws.Cell(r, 6).Value = filas.Sum(x => x.Actual); ws.Cell(r, 6).Style.NumberFormat.Format = fmt;
        ws.Cell(r, 7).Value = filas.Sum(x => x.Base);   ws.Cell(r, 7).Style.NumberFormat.Format = fmt;
        ws.Cell(r, 8).Value = filas.Sum(x => x.Delta);  ws.Cell(r, 8).Style.NumberFormat.Format = fmt;
        ws.Cell(r, 10).Value = filas.Sum(x => x.Viajes);
        ws.Cell(r, 11).Value = filas.Sum(x => x.Cancelados);
        ws.Row(r).Style.Font.Bold = true;

        ws.Cell(r + 2, 1).Value = $"Período {periodoActual} comparado contra {periodoBase}. Métrica: {metrica}.";
        ws.Cell(r + 3, 1).Value =
            "Clase ABC = importancia por Pareto de la métrica: A son los clientes que hacen el primer 80 %, "
            + "B hasta el 95 %, C la cola. Los que se fueron conservan la clase que tenían en el período base.";
        ws.Cell(r + 4, 1).Value =
            "«Se fue» = tenía actividad en el período base y ninguna en este. «Cayó» / «Creció» = varió 40 % o más.";
        if (esPlata)
            ws.Cell(r + 5, 1).Value =
                "Importes en pesos corrientes: si los dos períodos caen en años distintos, parte de la variación es inflación.";
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(filas.Count + 1, 500));

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta el Resumen de Liquidaciones (botón Excel del form liquidacion_cliente.scx).
    /// Hoja 1: cabeceras (la grilla superior). Hoja 2: detalle de TODAS las liquidaciones
    /// filtradas (la grilla inferior, acumulada).
    /// </summary>
    public byte[] ResumenLiquidaciones(
        IReadOnlyList<LiquidacionRow> cabeceras,
        IReadOnlyDictionary<int, List<LiquidacionDetalleRow>> detallePorLiq)
    {
        using var wb = new XLWorkbook();

        // --- Hoja 1: Liquidaciones (cabeceras) -------------------------------
        var ws = wb.Worksheets.Add("Liquidaciones");
        string[] h =
        {
            "Idliquidacion","Tipo","Fecha","Codigo","Razon_social","Moneda","Subtotal",
            "Iva","Exento","Totalgral","Fcomp","Factura","F_pago","Forma_pago","Banco",
            "N_pago","Retencion_iva","Retencion_iibb","Retencion_suss","Pago"
        };
        for (var c = 0; c < h.Length; c++) ws.Cell(1, c + 1).Value = h[c];

        var r = 2;
        foreach (var l in cabeceras)
        {
            ws.Cell(r, 1).Value = l.IdLiquidacion;
            ws.Cell(r, 2).Value = l.Tipo;
            if (l.Fecha is not null) { ws.Cell(r, 3).Value = l.Fecha.Value.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 3).Style.DateFormat.Format = "dd/mm/yyyy"; }
            ws.Cell(r, 4).Value = l.Codigo;
            ws.Cell(r, 5).Value = l.RazonSocial;
            ws.Cell(r, 6).Value = l.Moneda;
            ws.Cell(r, 7).Value = l.Subtotal; ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(r, 8).Value = l.Iva;      ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(r, 9).Value = l.Exento;   ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(r, 10).Value = l.TotalGral; ws.Cell(r, 10).Style.NumberFormat.Format = "#,##0.00";
            if (l.Fcomp is not null) { ws.Cell(r, 11).Value = l.Fcomp.Value.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 11).Style.DateFormat.Format = "dd/mm/yyyy"; }
            ws.Cell(r, 12).Value = l.Factura;
            if (l.FPago is not null) { ws.Cell(r, 13).Value = l.FPago.Value.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 13).Style.DateFormat.Format = "dd/mm/yyyy"; }
            ws.Cell(r, 14).Value = l.FormaPago;
            ws.Cell(r, 15).Value = l.Banco;
            ws.Cell(r, 16).Value = l.NPago;
            ws.Cell(r, 17).Value = l.RetIva;  ws.Cell(r, 17).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(r, 18).Value = l.RetIibb; ws.Cell(r, 18).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(r, 19).Value = l.RetSuss; ws.Cell(r, 19).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(r, 20).Value = l.Pago;    ws.Cell(r, 20).Style.NumberFormat.Format = "#,##0.00";
            r++;
        }
        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        ws.Row(1).Style.Font.FontColor = XLColor.White;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        // --- Hoja 2: Detalle (todas las liquidaciones filtradas) -------------
        var wsD = wb.Worksheets.Add("Detalle");
        string[] hd =
        {
            "Idliquidacion","Id","Id_viaje","Tipo","Id_adicional","Nombre","Moneda",
            "Cantidad","Precio","Importe","D_destino_prov","Km_recorrido","Descuento",
            "Incremento","Id_viaje_int"
        };
        for (var c = 0; c < hd.Length; c++) wsD.Cell(1, c + 1).Value = hd[c];

        var rd = 2;
        foreach (var l in cabeceras)
        {
            if (!detallePorLiq.TryGetValue(l.IdLiquidacion, out var dets)) continue;
            foreach (var d in dets)
            {
                wsD.Cell(rd, 1).Value = l.IdLiquidacion;
                wsD.Cell(rd, 2).Value = d.Id;
                wsD.Cell(rd, 3).Value = d.IdViaje;
                wsD.Cell(rd, 4).Value = d.Tipo;
                wsD.Cell(rd, 5).Value = d.IdAdicional;
                wsD.Cell(rd, 6).Value = d.Nombre;
                wsD.Cell(rd, 7).Value = d.Moneda;
                wsD.Cell(rd, 8).Value = d.Cantidad;
                wsD.Cell(rd, 9).Value = d.Precio;   wsD.Cell(rd, 9).Style.NumberFormat.Format = "#,##0.00";
                wsD.Cell(rd, 10).Value = d.Importe; wsD.Cell(rd, 10).Style.NumberFormat.Format = "#,##0.00";
                wsD.Cell(rd, 11).Value = d.DDestinoProv;
                wsD.Cell(rd, 12).Value = d.KmRecorrido;
                wsD.Cell(rd, 13).Value = d.Descuento;
                wsD.Cell(rd, 14).Value = d.Incremento;
                wsD.Cell(rd, 15).Value = d.IdViajeInt;
                rd++;
            }
        }
        wsD.Row(1).Style.Font.Bold = true;
        wsD.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        wsD.Row(1).Style.Font.FontColor = XLColor.White;
        wsD.SheetView.FreezeRows(1);
        wsD.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta la proyección de Facturación estimada.
    /// Hoja 1: por mes. Hoja 2: por cliente.
    /// </summary>
    public byte[] FacturacionEstimada(
        IReadOnlyList<FacturacionEstimadaMesRow> porMes,
        IReadOnlyList<FacturacionEstimadaClienteRow> porCliente)
    {
        using var wb = new XLWorkbook();

        var wsM = wb.Worksheets.Add("Por mes");
        wsM.Cell(1, 1).Value = "Mes";
        wsM.Cell(1, 2).Value = "Liquidaciones";
        wsM.Cell(1, 3).Value = "Servicios";
        wsM.Cell(1, 4).Value = "Total estimado";
        var r = 2;
        foreach (var m in porMes)
        {
            wsM.Cell(r, 1).Value = m.Mes;
            wsM.Cell(r, 2).Value = m.Liquidaciones;
            wsM.Cell(r, 3).Value = m.Servicios;
            wsM.Cell(r, 4).Value = m.TotalEstimado; wsM.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
            r++;
        }
        wsM.Row(1).Style.Font.Bold = true;
        wsM.Columns().AdjustToContents();

        var wsC = wb.Worksheets.Add("Por cliente");
        wsC.Cell(1, 1).Value = "Código";
        wsC.Cell(1, 2).Value = "Razón social";
        wsC.Cell(1, 3).Value = "Liquidaciones";
        wsC.Cell(1, 4).Value = "Total estimado";
        var rc = 2;
        foreach (var c in porCliente)
        {
            wsC.Cell(rc, 1).Value = c.Codigo;
            wsC.Cell(rc, 2).Value = c.RazonSocial;
            wsC.Cell(rc, 3).Value = c.Liquidaciones;
            wsC.Cell(rc, 4).Value = c.TotalEstimado; wsC.Cell(rc, 4).Style.NumberFormat.Format = "#,##0.00";
            rc++;
        }
        wsC.Row(1).Style.Font.Bold = true;
        wsC.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta el comprobante de liquidación formato RESUMEN (botón "Liq Excel" /
    /// "Excel" del form FoxPro facturacion_cliente_nueva.scx, que vuelca el cursor
    /// tmpLiquidacion). Todo calculado en vivo, sin tocar la base.
    /// Hoja 1: Servicios (detalle por viaje) + cuadro de totales debajo.
    /// Hoja 2: Adicionales (detalle por viaje).
    /// </summary>
    public byte[] LiquidacionResumen(
        string grupo,
        string clienteNombre,
        IReadOnlyList<ViajeValorizadoRow> viajes,
        IReadOnlyList<ViajeAdicionalRow> adicionales,
        LiquidacionTotalesRow totales)
    {
        const string Money = "#,##0.00";
        using var wb = new XLWorkbook();

        // ── Hoja 1: Servicios + Totales ──────────────────────────────────────
        var ws = wb.Worksheets.Add("Servicios");

        ws.Cell(1, 1).Value = "Liquidación — Resumen";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = "Cliente:";   ws.Cell(2, 2).Value = clienteNombre;
        ws.Cell(3, 1).Value = "Grupo:";     ws.Cell(3, 2).Value = grupo;
        ws.Cell(4, 1).Value = "Emisión:";   ws.Cell(4, 2).Value = DateTime.Today; ws.Cell(4, 2).Style.DateFormat.Format = "dd/mm/yyyy";
        ws.Range(2, 1, 4, 1).Style.Font.Bold = true;

        var hr = 6;   // fila del encabezado de la grilla
        string[] h = { "Id Viaje", "Fecha/Hora", "Servicio/Cabecera", "Recorrido", "Vehículo", "Pax", "Km", "Importe" };
        for (var c = 0; c < h.Length; c++) ws.Cell(hr, c + 1).Value = h[c];
        ws.Row(hr).Style.Font.Bold = true;
        ws.Row(hr).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        ws.Row(hr).Style.Font.FontColor = XLColor.White;

        var r = hr + 1;
        foreach (var v in viajes)
        {
            if (v.IdViaje > 0) ws.Cell(r, 1).Value = v.IdViaje;
            if (v.HsInicio is not null) { ws.Cell(r, 2).Value = v.HsInicio.Value; ws.Cell(r, 2).Style.DateFormat.Format = "dd/mm/yyyy HH:mm"; }
            ws.Cell(r, 3).Value = string.IsNullOrWhiteSpace(v.Cabecera) ? v.Servicio : v.Cabecera;
            ws.Cell(r, 4).Value = v.Destino;
            ws.Cell(r, 5).Value = v.Vehiculo;
            if (v.Pax > 0) ws.Cell(r, 6).Value = v.Pax;
            if (v.Km > 0) ws.Cell(r, 7).Value = v.Km;
            if (v.SinTarifa) ws.Cell(r, 8).Value = "S/TARIFA";
            else { ws.Cell(r, 8).Value = v.ImporteNeto; ws.Cell(r, 8).Style.NumberFormat.Format = Money; }
            r++;
        }

        // Subtotal servicios
        ws.Cell(r, 5).Value = "Subtotal servicios";
        ws.Cell(r, 6).Value = viajes.Sum(v => v.Pax);
        ws.Cell(r, 7).Value = viajes.Sum(v => v.Km);
        ws.Cell(r, 8).Value = viajes.Where(v => !v.SinTarifa).Sum(v => v.ImporteNeto);
        ws.Cell(r, 8).Style.NumberFormat.Format = Money;
        ws.Range(r, 5, r, 8).Style.Font.Bold = true;

        // Cuadro de totales (réplica de tmpLiquidacionTotal), debajo del detalle.
        var t = r + 3;
        ws.Cell(t, 1).Value = "Totales de la liquidación";
        ws.Cell(t, 1).Style.Font.Bold = true;
        void Tot(string lbl, decimal val, bool bold = false)
        {
            t++;
            ws.Cell(t, 1).Value = lbl;
            ws.Cell(t, 2).Value = val; ws.Cell(t, 2).Style.NumberFormat.Format = Money;
            if (bold) ws.Range(t, 1, t, 2).Style.Font.Bold = true;
        }
        Tot($"Servicios {(totales.Moneda.Length > 0 ? totales.Moneda : "PESOS")}", totales.Total);
        if (totales.Extra != 0) Tot("Extras", totales.Extra);
        if (totales.Descuento != 0) Tot("Descuento", -totales.Descuento);
        if (totales.Incremento != 0) Tot("Incremento", totales.Incremento);
        if (totales.TipoCambio != 1m) Tot($"Total a facturar (× {totales.TipoCambio:#,##0.00})", totales.TotalFinal);
        if (totales.Iva > 0) { Tot($"IVA {totales.Piva:#,##0.00}%", totales.Iva); Tot("Total más IVA", totales.TotalConIva); }
        if (totales.Adicionales > 0) Tot("Adicionales exentos", totales.Adicionales);
        Tot("TOTAL LIQUIDACIÓN", totales.TotalLiquidacion, bold: true);

        ws.Columns().AdjustToContents();

        // ── Hoja 2: Adicionales (detalle por viaje) ──────────────────────────
        var wsA = wb.Worksheets.Add("Adicionales");
        string[] ha = { "Id Viaje", "Adicional", "Nombre", "Cantidad", "Precio", "Total", "Estado", "Inicio", "Vehículo", "Servicio/Cabecera", "Destino" };
        for (var c = 0; c < ha.Length; c++) wsA.Cell(1, c + 1).Value = ha[c];
        wsA.Row(1).Style.Font.Bold = true;
        wsA.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        wsA.Row(1).Style.Font.FontColor = XLColor.White;

        var ra = 2;
        foreach (var a in adicionales)
        {
            if (a.IdViaje > 0) wsA.Cell(ra, 1).Value = a.IdViaje;
            wsA.Cell(ra, 2).Value = a.IdAdicional;
            wsA.Cell(ra, 3).Value = a.Nombre;
            wsA.Cell(ra, 4).Value = a.Cantidad;
            if (a.Estado != "EXCLUIDO") { wsA.Cell(ra, 5).Value = a.Precio; wsA.Cell(ra, 5).Style.NumberFormat.Format = Money; }
            if (a.Estado != "EXCLUIDO") { wsA.Cell(ra, 6).Value = a.Total;  wsA.Cell(ra, 6).Style.NumberFormat.Format = Money; }
            wsA.Cell(ra, 7).Value = a.SinTarifa ? a.Estado + " · S/TARIFA" : a.Estado;
            if (a.Inicio is not null) { wsA.Cell(ra, 8).Value = a.Inicio.Value; wsA.Cell(ra, 8).Style.DateFormat.Format = "dd/mm/yyyy HH:mm"; }
            wsA.Cell(ra, 9).Value = a.Vehiculo;
            wsA.Cell(ra, 10).Value = string.IsNullOrWhiteSpace(a.Cabecera) ? a.Servicio : a.Cabecera;
            wsA.Cell(ra, 11).Value = a.Destino;
            ra++;
        }
        wsA.Cell(ra, 5).Value = "Total exento";
        wsA.Cell(ra, 6).Value = totales.Adicionales; wsA.Cell(ra, 6).Style.NumberFormat.Format = Money;
        wsA.Range(ra, 5, ra, 6).Style.Font.Bold = true;
        wsA.SheetView.FreezeRows(1);
        wsA.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta el "Historial del viaje" (form FoxPro trafico_historial.scx): un bloque de
    /// cabecera con la auditoría (Creó/Eliminó/Modificó) + la grilla de movimientos de
    /// viaje_log. El color por motivo replica el del diálogo (valor agregado, no FoxPro).
    /// </summary>
    public byte[] HistorialViaje(HistorialViajeDto h)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Historial");

        // ── Cabecera de auditoría ──
        ws.Cell(1, 1).Value = $"Historial sobre la reserva Nº {h.IdViaje}";
        ws.Range(1, 1, 1, 4).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 13;

        ws.Cell(2, 1).Value = "Creador";
        ws.Cell(2, 2).Value = h.UsuarioCreo;
        ws.Cell(2, 3).Value = h.FechaCreo?.ToString("dd/MM/yyyy") ?? "";
        ws.Cell(3, 1).Value = "Eliminó";
        ws.Cell(3, 2).Value = h.UsuarioElimino;
        ws.Cell(3, 3).Value = h.FechaElimino?.ToString("dd/MM/yyyy") ?? "";
        ws.Cell(4, 1).Value = "Últ. Modificó";
        ws.Cell(4, 2).Value = h.UsuarioModifico;
        ws.Cell(4, 3).Value = h.FechaModifico?.ToString("dd/MM/yyyy") ?? "";
        ws.Range(2, 1, 4, 1).Style.Font.Bold = true;

        // ── Grilla de movimientos (mismas 9 columnas del FoxPro) ──
        const int hr = 6;   // fila del header de la grilla
        string[] headers =
        {
            "Hora","Usuario","Motivo","Chofer","Cronograma",
            "Cron. Nuevo","Int. Orig","Int. Nuevo","Comentario"
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(hr, c + 1).Value = headers[c];

        var r = hr + 1;
        foreach (var m in h.Movimientos)
        {
            ws.Cell(r, 1).Value = m.Hora;
            ws.Cell(r, 1).Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
            ws.Cell(r, 2).Value = m.Usuario;
            ws.Cell(r, 3).Value = m.Motivo;
            ws.Cell(r, 4).Value = m.Chofer;
            ws.Cell(r, 5).Value = m.Cronograma;
            ws.Cell(r, 6).Value = m.CronogramaNuevo;
            if (m.InternoOrig is not null)  ws.Cell(r, 7).Value = m.InternoOrig.Value;
            if (m.InternoNuevo is not null) ws.Cell(r, 8).Value = m.InternoNuevo.Value;
            ws.Cell(r, 9).Value = m.Comentario;

            var hex = ColorMotivo(m.Motivo);
            if (hex is not null)
                ws.Cell(r, 3).Style.Fill.BackgroundColor = XLColor.FromHtml(hex);

            r++;
        }

        ws.Row(hr).Style.Font.Bold = true;
        ws.Row(hr).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        ws.Row(hr).Style.Font.FontColor = XLColor.White;
        ws.SheetView.FreezeRows(hr);
        ws.Columns().AdjustToContents();
        ws.Column(9).Width = 45;   // comentario: no estirar de más

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Color de fondo del motivo en el Excel del Historial (espeja MotivoStyle del
    /// diálogo: solo el fondo, el texto va negro para legibilidad en Excel).</summary>
    private static string? ColorMotivo(string motivo)
    {
        var m = (motivo ?? "").Trim().ToUpperInvariant();
        return m switch
        {
            "ALTA"                              => "#EAF7F3",
            "ASIGNO"                            => "#FFFBDD",
            "FINALIZO"                          => "#F2F2F2",
            "CHEQUEO"                           => "#E8F6FF",
            "CANCELO" or "CANCELADO" or "ANULO" => "#FFF0F0",
            _ when m.Contains("CBIO") || m.Contains("CAMBIO") || m.Contains("REASIGN") => "#FDF0FF",
            _ when m.Contains("LIBER")          => "#FFF4E6",
            _                                   => null
        };
    }

    // ── Odómetros (vehiculo_km) ─────────────────────────────────────────────
    /// <summary>Exporta las lecturas de odómetro tal como se ven en la grilla
    /// (réplica del botón Excel de vehiculo_km.scx).</summary>
    public byte[] Odometros(IReadOnlyList<OdometroRow> filas, DateTime desde, DateTime hasta)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Odometros");

        string[] headers =
        {
            "Interno","Dominio","Fecha","Año y Mes","Km. Inicio","Km. Fin","Km. Recorridos",
            "U. Creó","U. Modificó"
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        var r = 2;
        foreach (var f in filas)
        {
            ws.Cell(r, 1).Value = f.InternoNT;    // código NTxxxx, igual que la pantalla
            ws.Cell(r, 2).Value = f.Dominio;
            if (f.FCarga is DateOnly fc)
            {
                ws.Cell(r, 3).Value = fc.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 3).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            ws.Cell(r, 4).Value = f.AnoMes;
            if (f.KmInicio is long ki) ws.Cell(r, 5).Value = ki;
            if (f.KmFin is long kf) ws.Cell(r, 6).Value = kf;
            if (f.KmRecorridos is long kr) ws.Cell(r, 7).Value = kr;
            ws.Cell(r, 8).Value = f.UCreo;
            ws.Cell(r, 9).Value = f.UModifico;
            r++;
        }

        ws.Range(2, 5, Math.Max(2, r - 1), 7).Style.NumberFormat.Format = "#,##0";
        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        ws.Row(1).Style.Font.FontColor = XLColor.White;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ── Auditoría de accesos (usuarios_logs) ────────────────────────────────
    /// <summary>Exporta la bitácora de accesos (eventos LOGIN/LOGOUT/EXPIRADA/VENCIDA/LOGIN_FALLIDO).</summary>
    public byte[] AuditoriaAccesos(IReadOnlyList<AccesoLogRow> filas, DateTime desde, DateTime hasta)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Accesos");

        string[] headers =
        {
            "Fecha y hora","Evento","Usuario","Nivel","Acceso","IP","Equipo","Motivo","Sesión (id)"
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        var r = 2;
        foreach (var f in filas)
        {
            ws.Cell(r, 1).Value = f.FEvento;
            ws.Cell(r, 1).Style.DateFormat.Format = "dd/mm/yyyy hh:mm:ss";
            ws.Cell(r, 2).Value = f.Evento;
            ws.Cell(r, 3).Value = f.Usuario;
            ws.Cell(r, 4).Value = f.Nivel;
            ws.Cell(r, 5).Value = f.Acceso;
            ws.Cell(r, 6).Value = f.Ip;
            ws.Cell(r, 7).Value = f.Hostname;
            ws.Cell(r, 8).Value = f.Motivo;
            ws.Cell(r, 9).Value = f.SessionId?.ToString() ?? "";
            r++;
        }

        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        ws.Row(1).Style.Font.FontColor = XLColor.White;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ── Siniestros (siniestro) ──────────────────────────────────────────────
    /// <summary>Exporta la grilla de Siniestros (columnas del browser siniestro.scx).</summary>
    public byte[] Siniestros(IReadOnlyList<SiniestroRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Siniestros");

        string[] headers =
        {
            "Siniestro","Conductor","Dominio","Interno","Fecha","Lugar","Localidad",
            "Marca (tercero)","Tipo Acc."
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        var r = 2;
        foreach (var f in filas)
        {
            ws.Cell(r, 1).Value = f.Id;
            ws.Cell(r, 2).Value = f.Conductor;
            ws.Cell(r, 3).Value = f.Dominio;
            if (f.Interno != 0) ws.Cell(r, 4).Value = f.Interno;
            if (f.Fecha is DateOnly fc)
            {
                ws.Cell(r, 5).Value = fc.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 5).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            ws.Cell(r, 6).Value = f.Lugar;
            ws.Cell(r, 7).Value = f.Localidad;
            ws.Cell(r, 8).Value = f.Marca;
            ws.Cell(r, 9).Value = f.TipoAcc;
            r++;
        }

        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        ws.Row(1).Style.Font.FontColor = XLColor.White;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ── Fleteros (fletero) ──────────────────────────────────────────────────
    /// <summary>Exporta la grilla de Fleteros (columnas del browser fletero.scx).</summary>
    public byte[] Fleteros(IReadOnlyList<FleteroRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fleteros");

        string[] headers =
        {
            "Orden","Código","Razón social","Nombre","CUIT","Localidad","Teléfono","Email","Baja"
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        var r = 2;
        foreach (var f in filas)
        {
            ws.Cell(r, 1).Value = f.Orden;
            ws.Cell(r, 2).Value = f.IdContrat;
            ws.Cell(r, 3).Value = f.RazonSocial;
            ws.Cell(r, 4).Value = f.Nombre;
            ws.Cell(r, 5).Value = f.Cuit;
            ws.Cell(r, 6).Value = f.Localidad;
            ws.Cell(r, 7).Value = f.Telefono;
            ws.Cell(r, 8).Value = f.Email;
            if (f.FDelete is DateOnly fd)
            {
                ws.Cell(r, 9).Value = fd.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 9).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            r++;
        }

        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        ws.Row(1).Style.Font.FontColor = XLColor.White;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ── Tipo de Vehículos (vehiculo_tipo) ───────────────────────────────────
    /// <summary>Exporta la grilla de Tipo de Vehículos (columnas del browser vehiculo_tipo.scx).</summary>
    public byte[] TiposVehiculo(IReadOnlyList<TipoVehiculoRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Tipos de vehiculo");

        string[] headers =
        {
            "Código","Nombre","Pax","Subtipo","Consumo mín.","Consumo máx.","Vende","Baja"
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        var r = 2;
        foreach (var t in filas)
        {
            ws.Cell(r, 1).Value = t.Codigo;
            ws.Cell(r, 2).Value = t.Nombre;
            ws.Cell(r, 3).Value = t.Pax;
            ws.Cell(r, 4).Value = t.Subtipo;
            if (t.ConsumoMin is decimal cmin) ws.Cell(r, 5).Value = cmin;
            if (t.ConsumoMax is decimal cmax) ws.Cell(r, 6).Value = cmax;
            ws.Cell(r, 7).Value = t.Vende ? "Sí" : "No";
            if (t.FDelete is DateOnly fd)
            {
                ws.Cell(r, 8).Value = fd.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 8).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            r++;
        }

        ws.Range(2, 5, Math.Max(2, r - 1), 6).Style.NumberFormat.Format = "#,##0.00";
        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        ws.Row(1).Style.Font.FontColor = XLColor.White;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ── Agenda de Vencimientos (agenda_vencimiento) ─────────────────────────
    /// <summary>Exporta las dos grillas de vencimientos (choferes + vehículos) en 2 hojas.</summary>
    public byte[] AgendaVencimientos(
        IReadOnlyList<ChoferVtoRow> choferes, IReadOnlyList<VehiculoVtoRow> vehiculos, int dias)
    {
        using var wb = new XLWorkbook();
        var hoy = DateTime.Today;
        var lim = hoy.AddDays(dias);

        void PintarVto(IXLCell cell, DateOnly? f)
        {
            if (f is DateOnly d)
            {
                cell.Value = d.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "dd/mm/yyyy";
                if (d.ToDateTime(TimeOnly.MinValue) <= hoy)
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8CBCB"); // vencido
                else if (d.ToDateTime(TimeOnly.MinValue) <= lim)
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FCE3B4"); // por vencer
            }
            else
            {
                cell.Value = "sin fecha";
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8CBCB");
            }
        }

        // Hoja 1: Choferes
        var wsC = wb.Worksheets.Add("Choferes");
        string[] hC = { "Chofer", "Nombre", "Fletero", "Nº Registro", "Registro", "CNRT", "AEP" };
        for (var c = 0; c < hC.Length; c++) wsC.Cell(1, c + 1).Value = hC[c];
        var rc = 2;
        foreach (var ch in choferes)
        {
            wsC.Cell(rc, 1).Value = ch.IdChofer;
            wsC.Cell(rc, 2).Value = ch.Nombre;
            wsC.Cell(rc, 3).Value = ch.Fletero;
            wsC.Cell(rc, 4).Value = ch.RegistroNro;
            PintarVto(wsC.Cell(rc, 5), ch.RegistroVto);
            PintarVto(wsC.Cell(rc, 6), ch.CnrtVto);
            PintarVto(wsC.Cell(rc, 7), ch.AepVto);
            rc++;
        }
        EstiloHeaderAgenda(wsC);

        // Hoja 2: Vehículos propios
        var wsV = wb.Worksheets.Add("Vehiculos");
        string[] hV = { "Interno", "Dominio", "VTV", "Matafuegos", "Póliza", "Habilitación" };
        for (var c = 0; c < hV.Length; c++) wsV.Cell(1, c + 1).Value = hV[c];
        var rv = 2;
        foreach (var ve in vehiculos)
        {
            wsV.Cell(rv, 1).Value = ve.InternoNT;
            wsV.Cell(rv, 2).Value = ve.Dominio;
            PintarVto(wsV.Cell(rv, 3), ve.VtvVto);
            PintarVto(wsV.Cell(rv, 4), ve.MatafuegoVto);
            PintarVto(wsV.Cell(rv, 5), ve.PolizaVto);
            PintarVto(wsV.Cell(rv, 6), ve.HabilitacionVto);
            rv++;
        }
        EstiloHeaderAgenda(wsV);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Documentación de los choferes con actividad en el período (modal de ViajesPorChofer).
    /// Sale con la MISMA clasificación que la pantalla (<see cref="VencimientosChofer"/>) y en
    /// el mismo orden de urgencia, así el .xlsx no puede discrepar de lo que se ve.
    /// </summary>
    public byte[] ChoferesVencimientos(IReadOnlyList<ChoferDocVista> filas, int dias, string periodo)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Documentacion");

        void PintarDoc(IXLCell cell, DocEstado est, DateOnly? f)
        {
            if (f is DateOnly d)
            {
                cell.Value = d.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "dd/mm/yyyy";
            }
            else
            {
                cell.Value = est == DocEstado.NoAplica ? "no corresponde" : "sin fecha";
            }
            if (est == DocEstado.Vencido) cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8CBCB");
            else if (est == DocEstado.PorVencer) cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FCE3B4");
        }

        string[] h = { "Chofer", "Nombre", "Tipo", "Viajes", "Nº Registro",
                       "Registro", "CNRT", "AEP", "Estado", "Vence primero", "Plazo (días)" };
        for (var c = 0; c < h.Length; c++) ws.Cell(1, c + 1).Value = h[c];

        var r = 2;
        foreach (var f in VencimientosChofer.OrdenarPorUrgencia(filas))
        {
            ws.Cell(r, 1).Value = f.IdChofer;
            ws.Cell(r, 2).Value = f.Nombre;
            ws.Cell(r, 3).Value = f.EsContratado ? $"Contratado · {f.Doc.Fletero}" : "Nortur";
            ws.Cell(r, 4).Value = f.Viajes;
            ws.Cell(r, 5).Value = f.Doc.RegistroNro;
            PintarDoc(ws.Cell(r, 6), f.Registro, f.Doc.RegistroVto);
            PintarDoc(ws.Cell(r, 7), f.Cnrt, f.Doc.CnrtVto);
            PintarDoc(ws.Cell(r, 8), f.Aep, f.Doc.AepVto);
            ws.Cell(r, 9).Value = f.Peor == DocEstado.Vencido ? "VENCIDO" : "POR VENCER";
            ws.Cell(r, 10).Value = f.DocCritico;
            if (f.DiasCritico is int dd) ws.Cell(r, 11).Value = dd;
            r++;
        }

        // Pie con el criterio: sin esta línea los números no se pueden auditar contra el sistema.
        var pie = r + 1;
        ws.Cell(pie, 1).Value = $"Período: {periodo} · ventana de aviso: {dias} días · "
            + "registro y CNRT sin fecha = vencido (regla Metrocar); AEP sin fecha = no corresponde.";
        ws.Range(pie, 1, pie, h.Length).Merge();
        ws.Cell(pie, 1).Style.Font.Italic = true;
        ws.Cell(pie, 1).Style.Font.FontColor = XLColor.FromHtml("#5B7290");

        EstiloHeaderAgenda(ws);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void EstiloHeaderAgenda(IXLWorksheet ws)
    {
        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#112F5B");
        ws.Row(1).Style.Font.FontColor = XLColor.White;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  TRÁFICO — Cabeceras · Francos · Viáticos (05/07/2026, solo lectura)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Exporta la grilla de Cabeceras/Recorridos (cabecera_recorrido.scx).</summary>
    public byte[] Cabeceras(IReadOnlyList<CabeceraRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Cabeceras");
        string[] headers = { "Código", "Descripción 1", "Descripción 2", "Descripción 3", "Recorrido" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var f in filas)
        {
            ws.Cell(r, 1).Value = f.Codigo;
            ws.Cell(r, 2).Value = f.Nombre;
            ws.Cell(r, 3).Value = f.Nombre1;
            ws.Cell(r, 4).Value = f.Nombre2;
            ws.Cell(r, 5).Value = f.Recorrido;
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Exporta la grilla de Mantenimiento de Francos (chofer_franco.scx).</summary>
    public byte[] Francos(IReadOnlyList<FrancoRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Francos");
        string[] headers = { "Chofer", "Nombre", "Motivo", "Cód.", "Fecha", "Trabajó" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var f in filas)
        {
            ws.Cell(r, 1).Value = f.IdChofer;
            ws.Cell(r, 2).Value = f.Nombre;
            ws.Cell(r, 3).Value = f.Motivo;
            ws.Cell(r, 4).Value = f.Codigo;
            ws.Cell(r, 5).Value = f.Fecha.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 5).Style.DateFormat.Format = "dd/mm/yyyy";
            ws.Cell(r, 6).Value = f.Trabajo ? "Sí" : "No";
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Exporta la matriz de Auditoría de Francos (chofer_franco_auditoria.scx):
    /// chofer × día del mes + días trabajados + problemas.</summary>
    public byte[] FrancosAuditoria(IReadOnlyList<FrancoAuditoriaRow> filas, int mes, int ano)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add($"Auditoria {mes:D2}-{ano}");
        var dias = DateTime.DaysInMonth(ano, mes);

        ws.Cell(1, 1).Value = "Chofer";
        ws.Cell(1, 2).Value = "Nombre";
        for (var d = 1; d <= dias; d++) ws.Cell(1, 2 + d).Value = d;
        ws.Cell(1, 3 + dias).Value = "Trabajó";
        ws.Cell(1, 4 + dias).Value = "Problemas";

        var r = 2;
        foreach (var f in filas)
        {
            ws.Cell(r, 1).Value = f.IdChofer;
            ws.Cell(r, 2).Value = f.Nombre;
            for (var d = 1; d <= dias; d++)
            {
                var v = (d < f.Dias.Length ? f.Dias[d] : "") ?? "";
                ws.Cell(r, 2 + d).Value = v == "trb" ? "T" : v.ToUpperInvariant();
                if (v == "DUP") ws.Cell(r, 2 + d).Style.Fill.BackgroundColor = XLColor.FromHtml("#FDE68A");
            }
            ws.Cell(r, 3 + dias).Value = f.DiasTrab;
            ws.Cell(r, 4 + dias).Value = f.Problemas;
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Exporta la grilla de Viáticos (chofer_viatico.scx).</summary>
    public byte[] Viaticos(IReadOnlyList<ViaticoRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Viaticos");
        string[] headers = { "Fecha", "Conductor", "Motivo", "Liquida", "Forma Pago", "Importe", "F. Pago" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var f in filas)
        {
            ws.Cell(r, 1).Value = f.Fecha.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            ws.Cell(r, 2).Value = f.Conductor;
            ws.Cell(r, 3).Value = f.Motivo;
            ws.Cell(r, 4).Value = f.FormaLiquida;
            ws.Cell(r, 5).Value = f.FormaPago;
            ws.Cell(r, 6).Value = f.Importe;
            if (f.FPago is DateOnly fp)
            {
                ws.Cell(r, 7).Value = fp.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 7).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            r++;
        }
        ws.Range(2, 6, Math.Max(2, r - 1), 6).Style.NumberFormat.Format = "#,##0.00";
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  RESERVAS — Operadores · Grupos · Destinos (06/07/2026, solo lectura)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Exporta la grilla de Operadores (cliente_operador.scx).</summary>
    public byte[] Operadores(IReadOnlyList<OperadorRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Operadores");
        string[] headers = { "Nombre", "Cód. Operador", "Cliente", "Razón social", "Teléfono", "Interno", "Celular", "E-mail", "Comentario" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var o in filas)
        {
            ws.Cell(r, 1).Value = o.Nombre;
            ws.Cell(r, 2).Value = o.IdOperador;
            ws.Cell(r, 3).Value = o.IdCliente;
            ws.Cell(r, 4).Value = o.RazonSocial;
            ws.Cell(r, 5).Value = o.Telefono;
            ws.Cell(r, 6).Value = o.Interno;
            ws.Cell(r, 7).Value = o.Celular;
            ws.Cell(r, 8).Value = o.Email;
            ws.Cell(r, 9).Value = o.Comentario;
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Exporta la grilla de Grupos de clientes (cliente_grupo.scx).</summary>
    public byte[] Grupos(IReadOnlyList<GrupoRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Grupos");
        string[] headers = { "Razón social", "Cliente", "Grupo", "F. Inicio", "F. Fin", "F. Facturó", "Estado" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var g in filas)
        {
            ws.Cell(r, 1).Value = g.RazonSocial;
            ws.Cell(r, 2).Value = g.IdCliente;
            ws.Cell(r, 3).Value = g.Nombre;
            if (g.FInicio is DateOnly fi) { ws.Cell(r, 4).Value = fi.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 4).Style.DateFormat.Format = "dd/mm/yyyy"; }
            if (g.FFin is DateOnly ff) { ws.Cell(r, 5).Value = ff.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 5).Style.DateFormat.Format = "dd/mm/yyyy"; }
            if (g.FFacturo is DateOnly fc) { ws.Cell(r, 6).Value = fc.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 6).Style.DateFormat.Format = "dd/mm/yyyy"; }
            ws.Cell(r, 7).Value = g.Cerrado ? "Facturado" : "Abierto";
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Exporta la grilla de Destinos (destino.scx).</summary>
    public byte[] Destinos(IReadOnlyList<DestinoRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Destinos");
        string[] headers = { "Destino", "Dirección", "Localidad", "Teléfono", "Correo", "Contacto", "Cabecera", "+100 km" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var d in filas)
        {
            ws.Cell(r, 1).Value = d.Destino;
            ws.Cell(r, 2).Value = d.Direccion;
            ws.Cell(r, 3).Value = d.Localidad;
            ws.Cell(r, 4).Value = d.Telefono;
            ws.Cell(r, 5).Value = d.Correo;
            ws.Cell(r, 6).Value = d.Contacto;
            ws.Cell(r, 7).Value = d.Cabecera;
            ws.Cell(r, 8).Value = d.Mas100Km ? "Sí" : "No";
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  RESERVAS — Reservas Especiales · Plantillas (07/07/2026, solo lectura)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Exporta la grilla de Reservas Especiales (viaje origen 'T').</summary>
    public byte[] ReservasEspeciales(IReadOnlyList<ReservaFsDetalleRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Reservas Especiales");
        string[] headers = { "Nº", "Fecha", "Hora", "Cód. Serv.", "Servicio", "Cliente", "Recorrido", "Pax", "Estado", "Chofer", "Interno", "Grupo" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var v in filas)
        {
            ws.Cell(r, 1).Value = v.IdViaje;
            ws.Cell(r, 2).Value = v.Fecha.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 2).Style.DateFormat.Format = "dd/MM/yyyy";
            ws.Cell(r, 3).Value = v.Hora;
            ws.Cell(r, 4).Value = v.CodServicio;
            ws.Cell(r, 5).Value = v.Servicio;
            ws.Cell(r, 6).Value = v.Cliente;
            ws.Cell(r, 7).Value = v.Recorrido;
            ws.Cell(r, 8).Value = v.Pax;
            ws.Cell(r, 9).Value = v.Estado;
            ws.Cell(r, 10).Value = v.Chofer;
            ws.Cell(r, 11).Value = v.Interno ?? 0;
            ws.Cell(r, 12).Value = v.Grupo;
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Exporta las plantillas: hoja Resumen (una fila por plantilla) + hoja Filas de la
    /// plantilla seleccionada (reserva_plantilla).</summary>
    public byte[] Plantillas(IReadOnlyList<PlantillaResumenRow> resumen, IReadOnlyList<PlantillaFilaRow> filas, string plantillaSel)
    {
        using var wb = new XLWorkbook();

        var wr = wb.Worksheets.Add("Plantillas");
        string[] hr = { "Plantilla", "Filas", "Hora desde", "Hora hasta", "Pax total" };
        for (var c = 0; c < hr.Length; c++) wr.Cell(1, c + 1).Value = hr[c];
        var r = 2;
        foreach (var p in resumen)
        {
            wr.Cell(r, 1).Value = p.IdReserva;
            wr.Cell(r, 2).Value = p.Filas;
            wr.Cell(r, 3).Value = p.HoraDesde;
            wr.Cell(r, 4).Value = p.HoraHasta;
            wr.Cell(r, 5).Value = p.PaxTotal;
            r++;
        }
        EstiloHeaderAgenda(wr);

        var wf = wb.Worksheets.Add("Filas");
        string[] hf = { "Plantilla", "Hora ini", "Hora fin", "Servicio", "T. Veh", "Desde", "Hasta", "Pax", "Km", "Hs", "Cabecera", "Guía", "Empresa dest.", "Recorrido", "Provincia", "Adicionales", "Comentario" };
        for (var c = 0; c < hf.Length; c++) wf.Cell(1, c + 1).Value = hf[c];
        r = 2;
        foreach (var f in filas)
        {
            wf.Cell(r, 1).Value = f.IdReserva;
            wf.Cell(r, 2).Value = f.HoraIni;
            wf.Cell(r, 3).Value = f.HoraFin;
            wf.Cell(r, 4).Value = f.Servicio;
            wf.Cell(r, 5).Value = f.TipoVeh;
            wf.Cell(r, 6).Value = f.Desde;
            wf.Cell(r, 7).Value = f.Hasta;
            wf.Cell(r, 8).Value = f.Pax;
            wf.Cell(r, 9).Value = f.Km;
            wf.Cell(r, 10).Value = f.Hs;
            wf.Cell(r, 11).Value = f.Cabecera;
            wf.Cell(r, 12).Value = f.Guia;
            wf.Cell(r, 13).Value = f.EmpresaDestino;
            wf.Cell(r, 14).Value = f.Recorrido;
            wf.Cell(r, 15).Value = f.Provincia;
            wf.Cell(r, 16).Value = f.Adicionales;
            wf.Cell(r, 17).Value = f.Comentario;
            r++;
        }
        EstiloHeaderAgenda(wf);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  TRÁFICO — Voucher · Guardias · Contactos · Rubros (07/07/2026, solo lectura)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Exporta la auditoría de vouchers (trafico_voucher.scx).</summary>
    public byte[] Voucher(IReadOnlyList<VoucherRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Voucher");
        string[] headers = { "Voucher", "F. Voucher", "Nº Viaje", "Fecha", "Hora", "Interno", "Destino", "Chofer", "Veh.", "Cliente", "Comentario" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var v in filas)
        {
            ws.Cell(r, 1).Value = v.VoucherNro;
            if (v.VoucherRecep is DateOnly vr) { ws.Cell(r, 2).Value = vr.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 2).Style.DateFormat.Format = "dd/mm/yyyy"; }
            ws.Cell(r, 3).Value = v.IdViaje;
            if (v.FReserva is DateOnly fr) { ws.Cell(r, 4).Value = fr.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 4).Style.DateFormat.Format = "dd/mm/yyyy"; }
            ws.Cell(r, 5).Value = v.Hora;
            ws.Cell(r, 6).Value = v.InternoNT;
            ws.Cell(r, 7).Value = v.Destino;
            ws.Cell(r, 8).Value = v.IdChofer;
            ws.Cell(r, 9).Value = v.Vehiculo;
            ws.Cell(r, 10).Value = v.IdCliente;
            ws.Cell(r, 11).Value = v.Comentario;
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Exporta la grilla de Guardias (trafico_guardia.scx).</summary>
    /// <summary>Libro de Novedades (Tráfico → Libro de Novedades). El original FoxPro no
    /// exporta: la única salida era leer la grilla en pantalla.</summary>
    public byte[] LibroNovedades(IReadOnlyList<LibroNovedadRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Libro de Novedades");
        string[] headers = { "F. Carga", "Asunto", "Mensaje", "U. Creador", "Nº Viaje", "Cliente", "Origen", "Enviada" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var n in filas)
        {
            if (n.FCarga is DateTime f)
            {
                ws.Cell(r, 1).Value = f;
                ws.Cell(r, 1).Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
            }
            ws.Cell(r, 2).Value = n.Asunto;
            // El mensaje es texto libre multilínea: se deja tal cual y se activa el wrap.
            ws.Cell(r, 3).Value = n.Mensaje;
            ws.Cell(r, 3).Style.Alignment.WrapText = true;
            ws.Cell(r, 4).Value = n.Usuario;
            if (n.IdViaje > 0) ws.Cell(r, 5).Value = n.IdViaje;
            ws.Cell(r, 6).Value = n.Cliente;
            ws.Cell(r, 7).Value = n.Origen;
            if (n.FEnvio is DateOnly e)
            {
                ws.Cell(r, 8).Value = e.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 8).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            r++;
        }
        ws.Column(3).Width = 70;
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public byte[] Guardias(IReadOnlyList<GuardiaRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Guardias");
        string[] headers = { "Interno", "Vehículo", "Chofer", "Nombre", "Franco", "Fecha", "Hs. Inicio", "Hs. Fin", "F. Pago" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var g in filas)
        {
            ws.Cell(r, 1).Value = g.InternoNT;
            ws.Cell(r, 2).Value = g.IdVehiculo;
            ws.Cell(r, 3).Value = g.IdChofer;
            ws.Cell(r, 4).Value = g.Nombre;
            ws.Cell(r, 5).Value = g.Franco ? "Sí" : "No";
            if (g.Fecha is DateOnly f) { ws.Cell(r, 6).Value = f.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 6).Style.DateFormat.Format = "dd/mm/yyyy"; }
            if (g.HsInicio is DateTime hi) { ws.Cell(r, 7).Value = hi; ws.Cell(r, 7).Style.DateFormat.Format = "dd/mm/yyyy hh:mm"; }
            if (g.HsFin is DateTime hf) { ws.Cell(r, 8).Value = hf; ws.Cell(r, 8).Style.DateFormat.Format = "dd/mm/yyyy hh:mm"; }
            if (g.FPago is DateOnly fp) { ws.Cell(r, 9).Value = fp.ToDateTime(TimeOnly.MinValue); ws.Cell(r, 9).Style.DateFormat.Format = "dd/mm/yyyy"; }
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Exporta la grilla de Contactos/Proveedores (estacion.scx).</summary>
    public byte[] Contactos(IReadOnlyList<ContactoRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Contactos");
        string[] headers = { "Razón social", "Rubro", "Domicilio", "Localidad", "Provincia", "Teléfono", "Celular", "Radio", "Contacto 1", "Contacto 2" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var x in filas)
        {
            ws.Cell(r, 1).Value = x.Nombre;
            ws.Cell(r, 2).Value = x.Rubro;
            ws.Cell(r, 3).Value = x.Domicilio;
            ws.Cell(r, 4).Value = x.Localidad;
            ws.Cell(r, 5).Value = x.Provincia;
            ws.Cell(r, 6).Value = x.Telefono;
            ws.Cell(r, 7).Value = x.Celular;
            ws.Cell(r, 8).Value = x.Radio;
            ws.Cell(r, 9).Value = x.Contacto1;
            ws.Cell(r, 10).Value = x.Contacto2;
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Exporta la grilla de Rubros de contacto (estacion_rubro.scx).</summary>
    public byte[] RubrosContacto(IReadOnlyList<RubroContactoRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Rubros");
        string[] headers = { "Código", "Rubro", "Audita" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var x in filas)
        {
            ws.Cell(r, 1).Value = x.Id;
            ws.Cell(r, 2).Value = x.Rubro;
            ws.Cell(r, 3).Value = x.Audita ? "Sí" : "No";
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  MÓDULO COMBUSTIBLE (07/07/2026)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Promedio de Consumos: hoja Ranking por unidad (l/100km) + hoja Cargas (detalle).</summary>
    public byte[] PromedioConsumos(
        IReadOnlyList<ConsumoUnidadRow> ranking, IReadOnlyList<CargaConsumoRow> cargas)
    {
        using var wb = new XLWorkbook();

        // --- Hoja 1: Ranking por unidad ---
        var wsR = wb.Worksheets.Add("Consumo por unidad");
        string[] hR = { "Interno", "Dominio", "Cargas", "Km recorridos", "Litros", "l/100 km", "Importe", "Costo/km" };
        for (var c = 0; c < hR.Length; c++) wsR.Cell(1, c + 1).Value = hR[c];
        var r = 2;
        foreach (var x in ranking)
        {
            wsR.Cell(r, 1).Value = x.InternoNT;
            wsR.Cell(r, 2).Value = x.Dominio;
            wsR.Cell(r, 3).Value = x.Cargas;
            wsR.Cell(r, 4).Value = x.KmRecorridos;
            wsR.Cell(r, 5).Value = x.Litros;
            if (x.L100 is double l) wsR.Cell(r, 6).Value = l;
            wsR.Cell(r, 7).Value = x.Importe;
            if (x.CostoKm is decimal ck) wsR.Cell(r, 8).Value = ck;
            r++;
        }
        wsR.Range(2, 4, Math.Max(2, r - 1), 4).Style.NumberFormat.Format = "#,##0";
        wsR.Range(2, 5, Math.Max(2, r - 1), 5).Style.NumberFormat.Format = "#,##0.0";
        wsR.Range(2, 6, Math.Max(2, r - 1), 6).Style.NumberFormat.Format = "#,##0.00";
        wsR.Range(2, 7, Math.Max(2, r - 1), 8).Style.NumberFormat.Format = "#,##0.00";
        EstiloHeaderAgenda(wsR);

        // --- Hoja 2: Cargas (detalle) ---
        var wsC = wb.Worksheets.Add("Cargas");
        string[] hC = { "Dominio", "Interno", "Fecha", "Hora", "Chofer", "Estación", "Tipo", "Odómetro", "Litros", "Lleno", "Importe" };
        for (var c = 0; c < hC.Length; c++) wsC.Cell(1, c + 1).Value = hC[c];
        r = 2;
        foreach (var x in cargas)
        {
            wsC.Cell(r, 1).Value = x.Dominio;
            wsC.Cell(r, 2).Value = x.InternoNT;
            wsC.Cell(r, 3).Value = x.FCarga.ToDateTime(TimeOnly.MinValue);
            wsC.Cell(r, 3).Style.DateFormat.Format = "dd/mm/yyyy";
            wsC.Cell(r, 4).Value = x.Hora;
            wsC.Cell(r, 5).Value = x.Chofer;
            wsC.Cell(r, 6).Value = x.Estacion;
            wsC.Cell(r, 7).Value = x.TipoCarga;
            wsC.Cell(r, 8).Value = x.Odometro;
            wsC.Cell(r, 9).Value = x.Litros;
            wsC.Cell(r, 10).Value = x.Lleno ? "LLENO" : "PARCIAL";
            wsC.Cell(r, 11).Value = x.Importe;
            r++;
        }
        wsC.Range(2, 8, Math.Max(2, r - 1), 8).Style.NumberFormat.Format = "#,##0";
        wsC.Range(2, 9, Math.Max(2, r - 1), 9).Style.NumberFormat.Format = "#,##0.0";
        wsC.Range(2, 11, Math.Max(2, r - 1), 11).Style.NumberFormat.Format = "#,##0.00";
        EstiloHeaderAgenda(wsC);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Cargas de combustible (grilla del conciliador). Marca las conciliadas con el lote.</summary>
    public byte[] CargasCombustible(IReadOnlyList<CargaCombustibleRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Cargas");
        string[] headers =
        {
            "Fecha", "Hora", "Estación", "Tipo", "Dominio", "Interno", "Odómetro",
            "Lleno", "Litros", "Importe", "Lote", "Forma pago", "Chofer", "Rubro"
        };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var x in filas)
        {
            ws.Cell(r, 1).Value = x.FCarga.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            ws.Cell(r, 2).Value = x.Hora;
            ws.Cell(r, 3).Value = x.Estacion;
            ws.Cell(r, 4).Value = x.TipoCarga;
            ws.Cell(r, 5).Value = x.Dominio;
            ws.Cell(r, 6).Value = x.InternoNT;
            ws.Cell(r, 7).Value = x.Odometro;
            ws.Cell(r, 8).Value = x.Lleno ? "LLENO" : "PARCIAL";
            ws.Cell(r, 9).Value = x.Litros;
            ws.Cell(r, 10).Value = x.Importe;
            if (x.Conciliada) ws.Cell(r, 11).Value = x.NSobre;
            ws.Cell(r, 12).Value = x.FPago;
            ws.Cell(r, 13).Value = x.Chofer;
            ws.Cell(r, 14).Value = x.Rubro;
            if (x.Conciliada)  // fila amarilla = ya conciliada (fiel al FoxPro)
                ws.Range(r, 1, r, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF6CC");
            r++;
        }
        ws.Range(2, 7, Math.Max(2, r - 1), 7).Style.NumberFormat.Format = "#,##0";
        ws.Range(2, 9, Math.Max(2, r - 1), 9).Style.NumberFormat.Format = "#,##0.0";
        ws.Range(2, 10, Math.Max(2, r - 1), 10).Style.NumberFormat.Format = "#,##0.00";
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Saldos por estación (informe histórico debe/haber/saldo).</summary>
    public byte[] SaldosEstaciones(IReadOnlyList<SaldoEstacionRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Saldos");
        string[] headers = { "Estación", "Debe (depósitos)", "Haber (consumos)", "Saldo" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var x in filas)
        {
            ws.Cell(r, 1).Value = x.Estacion;
            ws.Cell(r, 2).Value = x.Debe;
            ws.Cell(r, 3).Value = x.Haber;
            ws.Cell(r, 4).Value = x.Saldo;
            r++;
        }
        ws.Range(2, 2, Math.Max(2, r - 1), 4).Style.NumberFormat.Format = "#,##0.00";
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Depósitos de estación (movimientos ingreso/egreso).</summary>
    public byte[] DepositosEstacion(IReadOnlyList<DepositoEstacionRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Depositos");
        string[] headers = { "Estación", "Fecha", "Tipo", "Forma de pago", "Importe", "Usuario", "Comentario" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var x in filas)
        {
            ws.Cell(r, 1).Value = x.Estacion;
            ws.Cell(r, 2).Value = x.Fecha.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 2).Style.DateFormat.Format = "dd/mm/yyyy";
            ws.Cell(r, 3).Value = x.EsEgreso ? "EGRESO" : "INGRESO";
            ws.Cell(r, 4).Value = x.FormaPago;
            ws.Cell(r, 5).Value = x.Importe;
            ws.Cell(r, 6).Value = x.Usuario;
            ws.Cell(r, 7).Value = x.Comentario;
            r++;
        }
        ws.Range(2, 5, Math.Max(2, r - 1), 5).Style.NumberFormat.Format = "#,##0.00";
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Control de días sin cargar (trafico_vehiculo_combustible).</summary>
    public byte[] ControlCargas(IReadOnlyList<ControlCargaRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Control de cargas");
        string[] headers = { "Interno", "Dominio", "Última carga", "Días sin cargar", "Odómetro" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var x in filas)
        {
            ws.Cell(r, 1).Value = x.InternoNT;
            ws.Cell(r, 2).Value = x.Dominio;
            ws.Cell(r, 3).Value = x.UltCarga.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 3).Style.DateFormat.Format = "dd/mm/yyyy";
            ws.Cell(r, 4).Value = x.Dias;
            ws.Cell(r, 5).Value = x.Odometro;
            // Rojo si hace mucho que no carga (mismo criterio visual que la pantalla).
            if (x.Dias >= 15)
                ws.Range(r, 1, r, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#FDE2E1");
            r++;
        }
        ws.Range(2, 5, Math.Max(2, r - 1), 5).Style.NumberFormat.Format = "#,##0";
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Consumo mensual: hoja Pivote (mes × unidad, litros) + hoja Detalle.</summary>
    public byte[] ConsumoMensual(IReadOnlyList<ConsumoMensualRow> detalle)
    {
        using var wb = new XLWorkbook();

        // --- Hoja 1: Pivote mes × unidad (litros) ---
        var wsP = wb.Worksheets.Add("Pivote mes x unidad");
        var meses = detalle.Select(d => d.Mes).Distinct().OrderBy(m => m).ToList();
        var unidades = detalle.Select(d => (d.Dominio, d.Interno)).Distinct().OrderBy(u => u.Interno).ToList();
        var mapa = new Dictionary<(string, string), decimal>();
        foreach (var d in detalle)
        {
            var k = (d.Mes, d.Dominio);
            mapa[k] = (mapa.TryGetValue(k, out var v) ? v : 0) + d.Litros;
        }
        wsP.Cell(1, 1).Value = "Interno";
        wsP.Cell(1, 2).Value = "Dominio";
        for (var c = 0; c < meses.Count; c++)
        {
            var mm = meses[c];
            wsP.Cell(1, c + 3).Value = mm.Length == 7 ? $"{mm.Substring(5, 2)}/{mm.Substring(0, 4)}" : mm;
        }
        wsP.Cell(1, meses.Count + 3).Value = "TOTAL";
        var rp = 2;
        foreach (var (dom, interno) in unidades)
        {
            wsP.Cell(rp, 1).Value = interno == 0 ? "—" : "NT" + interno.ToString("D4");
            wsP.Cell(rp, 2).Value = dom;
            decimal totFila = 0;
            for (var c = 0; c < meses.Count; c++)
            {
                var val = mapa.TryGetValue((meses[c], dom), out var v) ? v : 0;
                if (val != 0) wsP.Cell(rp, c + 3).Value = val;
                totFila += val;
            }
            wsP.Cell(rp, meses.Count + 3).Value = totFila;
            rp++;
        }
        wsP.Range(2, 3, Math.Max(2, rp - 1), meses.Count + 3).Style.NumberFormat.Format = "#,##0.0";
        EstiloHeaderAgenda(wsP);

        // --- Hoja 2: Detalle (mes × unidad × estación × tipo) ---
        var wsD = wb.Worksheets.Add("Detalle");
        string[] hD = { "Mes", "Interno", "Dominio", "Estación", "Tipo", "Cargas", "Litros" };
        for (var c = 0; c < hD.Length; c++) wsD.Cell(1, c + 1).Value = hD[c];
        var rd = 2;
        foreach (var d in detalle)
        {
            wsD.Cell(rd, 1).Value = d.MesTexto;
            wsD.Cell(rd, 2).Value = d.InternoNT;
            wsD.Cell(rd, 3).Value = d.Dominio;
            wsD.Cell(rd, 4).Value = d.Estacion;
            wsD.Cell(rd, 5).Value = d.TipoCarga;
            wsD.Cell(rd, 6).Value = d.Cargas;
            wsD.Cell(rd, 7).Value = d.Litros;
            rd++;
        }
        wsD.Range(2, 7, Math.Max(2, rd - 1), 7).Style.NumberFormat.Format = "#,##0.0";
        EstiloHeaderAgenda(wsD);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Exporta el Panel del Operador. Hoja 1: el perfil de cada operador tal como se ve en
    /// pantalla. Hoja 2: la matriz creador × modificador (quién toca lo de quién). Hoja 3:
    /// la carga día por día, que es la que sirve para pegar en un tablero propio.
    ///
    /// Las tres hojas llevan escritos al pie los límites del dato (el período es por fecha
    /// de CARGA, y `u_modify` guarda solo la última mano): un Excel se reenvía por mail y se
    /// lee sin la pantalla al lado, así que las salvedades tienen que viajar con él.
    /// </summary>
    public byte[] PanelOperador(
        IReadOnlyList<OperadorPerfilRow> perfiles,
        IReadOnlyList<OperadorMatrizRow> matriz,
        IReadOnlyList<OperadorDiaRow> evolucion,
        DateOnly desde,
        DateOnly hasta)
    {
        using var wb = new XLWorkbook();
        var periodo = $"Período de CARGA: {desde:dd/MM/yyyy} – {hasta:dd/MM/yyyy}";

        // --- Hoja 1: perfil por operador -------------------------------------
        var wsP = wb.Worksheets.Add("Operadores");
        string[] hP =
        {
            "Operador", "Estado del usuario", "Altas", "Pax", "Días con carga", "Altas por día",
            "Clientes distintos", "Canceladas", "% canceladas", "Sin asignar", "% sin asignar",
            "Antelación prom. (días)", "Cargas retroactivas", "Modificaciones hechas",
            "Modificó de otros", "Sus altas tocadas por otro", "Primera carga", "Última carga"
        };
        for (var c = 0; c < hP.Length; c++) wsP.Cell(1, c + 1).Value = hP[c];

        var r = 2;
        foreach (var p in perfiles)
        {
            wsP.Cell(r, 1).Value = p.Operador;
            wsP.Cell(r, 2).Value = p.EstadoTexto;
            wsP.Cell(r, 3).Value = p.Altas;
            wsP.Cell(r, 4).Value = p.Pax;
            wsP.Cell(r, 5).Value = p.DiasConCarga;
            if (p.DiasConCarga > 0)
            {
                wsP.Cell(r, 6).Value = Math.Round(p.AltasPorDia, 1);
                wsP.Cell(r, 6).Style.NumberFormat.Format = "0.0";
            }
            wsP.Cell(r, 7).Value = p.Clientes;
            wsP.Cell(r, 8).Value = p.Canceladas;
            if (p.Altas > 0)
            {
                wsP.Cell(r, 9).Value = Math.Round(p.PctCanceladas, 1);
                wsP.Cell(r, 9).Style.NumberFormat.Format = "0.0";
            }
            wsP.Cell(r, 10).Value = p.SinAsignar;
            if (p.Altas > 0)
            {
                wsP.Cell(r, 11).Value = Math.Round(p.PctSinAsignar, 1);
                wsP.Cell(r, 11).Style.NumberFormat.Format = "0.0";
            }
            if (p.AntelacionProm is double ant)
            {
                wsP.Cell(r, 12).Value = Math.Round(ant, 1);
                wsP.Cell(r, 12).Style.NumberFormat.Format = "0.0";
            }
            wsP.Cell(r, 13).Value = p.Retroactivas;
            wsP.Cell(r, 14).Value = p.Modificaciones;
            wsP.Cell(r, 15).Value = p.ModificoDeOtros;
            wsP.Cell(r, 16).Value = p.AltasTocadasPorOtro;
            if (p.PrimeraCarga is DateOnly pc)
            {
                wsP.Cell(r, 17).Value = pc.ToDateTime(TimeOnly.MinValue);
                wsP.Cell(r, 17).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            if (p.UltimaCarga is DateOnly uc)
            {
                wsP.Cell(r, 18).Value = uc.ToDateTime(TimeOnly.MinValue);
                wsP.Cell(r, 18).Style.DateFormat.Format = "dd/mm/yyyy";
            }

            // El operador fantasma se marca en el Excel igual que en pantalla: es el hallazgo
            // de control, y en una planilla sin colores pasaría de largo.
            if (p.EstadoUsuario != EstadoUsuarioOperador.Vigente)
                wsP.Range(r, 1, r, hP.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#FDECEA");
            r++;
        }
        wsP.Cell(r, 1).Value = "TOTAL";
        wsP.Cell(r, 3).Value = perfiles.Sum(x => x.Altas);
        wsP.Cell(r, 4).Value = perfiles.Sum(x => x.Pax);
        wsP.Cell(r, 8).Value = perfiles.Sum(x => x.Canceladas);
        wsP.Cell(r, 10).Value = perfiles.Sum(x => x.SinAsignar);
        wsP.Cell(r, 13).Value = perfiles.Sum(x => x.Retroactivas);
        wsP.Cell(r, 14).Value = perfiles.Sum(x => x.Modificaciones);
        wsP.Cell(r, 15).Value = perfiles.Sum(x => x.ModificoDeOtros);
        wsP.Cell(r, 16).Value = perfiles.Sum(x => x.AltasTocadasPorOtro);
        wsP.Row(r).Style.Font.Bold = true;

        wsP.Cell(r + 2, 1).Value = periodo + " — es la fecha en que se cargó la reserva "
            + "(`f_create`), NO la fecha del viaje. Una reserva cargada hoy para diciembre entra acá.";
        wsP.Cell(r + 3, 1).Value = "Antelación = días entre la carga y la fecha del viaje. "
            + "Negativa (retroactiva) = se cargó después de que el viaje ocurrió.";
        wsP.Cell(r + 4, 1).Value = "Modificaciones: `u_modify` guarda SOLO la última mano que tocó "
            + "cada reserva, así que son un piso, nunca el total.";
        wsP.Cell(r + 5, 1).Value = "Fila resaltada = el operador no está vigente en el padrón de "
            + "usuarios (dado de baja o inexistente).";
        wsP.Row(1).Style.Font.Bold = true;
        wsP.SheetView.FreezeRows(1);
        wsP.Columns().AdjustToContents(1, Math.Min(perfiles.Count + 1, 200));

        // --- Hoja 2: matriz creador × modificador ----------------------------
        var wsM = wb.Worksheets.Add("Quién modifica a quién");
        string[] hM = { "Cargó la reserva", "La modificó", "Cantidad", "Tipo" };
        for (var c = 0; c < hM.Length; c++) wsM.Cell(1, c + 1).Value = hM[c];

        var rM = 2;
        foreach (var m in matriz.OrderByDescending(x => x.Cantidad))
        {
            wsM.Cell(rM, 1).Value = m.Creador;
            wsM.Cell(rM, 2).Value = m.Modificador;
            wsM.Cell(rM, 3).Value = m.Cantidad;
            wsM.Cell(rM, 4).Value = m.EsPropia ? "Se corrigió a sí mismo" : "Modificó de otro";
            rM++;
        }
        wsM.Cell(rM, 1).Value = "TOTAL";
        wsM.Cell(rM, 3).Value = matriz.Sum(x => x.Cantidad);
        wsM.Row(rM).Style.Font.Bold = true;
        wsM.Cell(rM + 2, 1).Value = "La diagonal (se corrigió a sí mismo) es lo normal y suele ser "
            + "la mayoría. Lo que hay que leer es lo de afuera: quién entra a corregir lo del otro.";
        wsM.Cell(rM + 3, 1).Value = periodo;
        wsM.Row(1).Style.Font.Bold = true;
        wsM.SheetView.FreezeRows(1);
        wsM.Columns().AdjustToContents(1, Math.Min(matriz.Count + 1, 300));

        // --- Hoja 3: carga día por día ---------------------------------------
        var wsE = wb.Worksheets.Add("Carga diaria");
        string[] hE = { "Fecha de carga", "Día", "Operador", "Altas" };
        for (var c = 0; c < hE.Length; c++) wsE.Cell(1, c + 1).Value = hE[c];

        var rE = 2;
        foreach (var e in evolucion.OrderBy(x => x.Fecha).ThenByDescending(x => x.Altas))
        {
            wsE.Cell(rE, 1).Value = e.Fecha.ToDateTime(TimeOnly.MinValue);
            wsE.Cell(rE, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            wsE.Cell(rE, 2).Value = DiaSemana(e.Fecha);
            wsE.Cell(rE, 3).Value = e.Operador;
            wsE.Cell(rE, 4).Value = e.Altas;
            rE++;
        }
        wsE.Cell(rE, 1).Value = "TOTAL";
        wsE.Cell(rE, 4).Value = evolucion.Sum(x => x.Altas);
        wsE.Row(rE).Style.Font.Bold = true;
        wsE.Cell(rE + 2, 1).Value = periodo;
        wsE.Row(1).Style.Font.Bold = true;
        wsE.SheetView.FreezeRows(1);
        wsE.Columns().AdjustToContents(1, Math.Min(evolucion.Count + 1, 500));

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Nombre del día en castellano. Se calcula en C# a propósito: `DATEPART(weekday)` de SQL
    /// depende de `SET DATEFIRST`, que cambia con el idioma del login — el mismo informe daría
    /// distinto en el server local (español) que en otro en inglés.
    /// </summary>
    private static string DiaSemana(DateOnly f) => f.DayOfWeek switch
    {
        DayOfWeek.Monday => "Lunes",
        DayOfWeek.Tuesday => "Martes",
        DayOfWeek.Wednesday => "Miércoles",
        DayOfWeek.Thursday => "Jueves",
        DayOfWeek.Friday => "Viernes",
        DayOfWeek.Saturday => "Sábado",
        _ => "Domingo",
    };

    /// <summary>
    /// Exporta el Panel de Tercerización. Hoja 1: los prestadores. Hoja 2: la evolución mensual.
    /// Hoja 3: el desglose completo (las tres dimensiones, sin recortar al top de la pantalla).
    /// Hoja 4: el cruce de oportunidad por tipo.
    /// </summary>
    public byte[] PanelTercerizacion(
        IReadOnlyList<TercerizacionPrestadorRow> prestadores,
        IReadOnlyList<TercerizacionMesRow> meses,
        IReadOnlyList<TercerizacionDetalleRow> detalle,
        IReadOnlyList<TercerizacionOportunidadRow> oportunidad,
        DateOnly desde,
        DateOnly hasta)
    {
        using var wb = new XLWorkbook();
        var periodo = $"Período: {desde:dd/MM/yyyy} – {hasta:dd/MM/yyyy}";
        var totalViajes = prestadores.Sum(p => p.Viajes);

        // --- Hoja 1: prestadores ---------------------------------------------
        var wsP = wb.Worksheets.Add("Prestadores");
        string[] hP = { "Código", "Razón social", "Tipo", "Viajes", "% del total", "Pax", "Km",
                        "Unidades", "Clientes", "Servicios", "Días", "Viajes por unidad" };
        for (var c = 0; c < hP.Length; c++) wsP.Cell(1, c + 1).Value = hP[c];

        var r = 2;
        foreach (var p in prestadores)
        {
            wsP.Cell(r, 1).Value = p.Prestador;
            wsP.Cell(r, 2).Value = p.Nombre;
            wsP.Cell(r, 3).Value = p.EsPropio ? "Flota propia" : "Fletero";
            wsP.Cell(r, 4).Value = p.Viajes;
            if (totalViajes > 0)
            {
                wsP.Cell(r, 5).Value = Math.Round(p.Viajes * 100.0 / totalViajes, 1);
                wsP.Cell(r, 5).Style.NumberFormat.Format = "0.0";
            }
            wsP.Cell(r, 6).Value = p.Pax;
            wsP.Cell(r, 7).Value = p.Km;
            wsP.Cell(r, 8).Value = p.Unidades;
            wsP.Cell(r, 9).Value = p.Clientes;
            wsP.Cell(r, 10).Value = p.Servicios;
            wsP.Cell(r, 11).Value = p.Dias;
            if (p.Unidades > 0)
            {
                wsP.Cell(r, 12).Value = Math.Round(p.ViajesPorUnidad, 1);
                wsP.Cell(r, 12).Style.NumberFormat.Format = "0.0";
            }
            r++;
        }
        wsP.Cell(r, 1).Value = "TOTAL";
        wsP.Cell(r, 4).Value = totalViajes;
        wsP.Cell(r, 6).Value = prestadores.Sum(p => p.Pax);
        wsP.Cell(r, 7).Value = prestadores.Sum(p => p.Km);
        wsP.Row(r).Style.Font.Bold = true;
        wsP.Cell(r + 2, 1).Value = periodo + ". Solo viajes CON unidad asignada y no cancelados.";
        wsP.Cell(r + 3, 1).Value =
            "Quién presta el servicio sale del TITULAR de la unidad (viaje.fletero). Ojo: el campo "
            + "`uso` del padrón de vehículos (PROPIO/CONTRATADO) describe la relación de la unidad "
            + "con SU titular, no con NORTUR — clasificar por ahí cuenta viajes de terceros como propios.";
        wsP.Row(1).Style.Font.Bold = true;
        wsP.SheetView.FreezeRows(1);
        wsP.Columns().AdjustToContents(1, Math.Min(prestadores.Count + 1, 200));

        // --- Hoja 2: evolución mensual ---------------------------------------
        var wsM = wb.Worksheets.Add("Evolución mensual");
        string[] hM = { "Mes", "Propios", "Tercerizados", "Total prestados", "% tercerizado", "Sin cubrir" };
        for (var c = 0; c < hM.Length; c++) wsM.Cell(1, c + 1).Value = hM[c];

        var rM = 2;
        foreach (var m in meses)
        {
            wsM.Cell(rM, 1).Value = m.Etiqueta;
            wsM.Cell(rM, 2).Value = m.Propios;
            wsM.Cell(rM, 3).Value = m.Tercerizados;
            wsM.Cell(rM, 4).Value = m.Asignados;
            if (m.Asignados > 0)
            {
                wsM.Cell(rM, 5).Value = Math.Round(m.PctTercerizado, 1);
                wsM.Cell(rM, 5).Style.NumberFormat.Format = "0.0";
            }
            wsM.Cell(rM, 6).Value = m.SinAsignar;
            rM++;
        }
        wsM.Row(rM).Style.Font.Bold = true;
        wsM.Cell(rM, 1).Value = "TOTAL";
        wsM.Cell(rM, 2).Value = meses.Sum(m => m.Propios);
        wsM.Cell(rM, 3).Value = meses.Sum(m => m.Tercerizados);
        wsM.Cell(rM, 4).Value = meses.Sum(m => m.Asignados);
        wsM.Cell(rM, 6).Value = meses.Sum(m => m.SinAsignar);
        wsM.Cell(rM + 2, 1).Value =
            "«Sin cubrir» son viajes que nunca tuvieron unidad (SIN ASIGNAR): NO entran en el "
            + "denominador del % tercerizado, se muestran al lado como demanda no cubierta.";
        wsM.Row(1).Style.Font.Bold = true;
        wsM.SheetView.FreezeRows(1);
        wsM.Columns().AdjustToContents(1, Math.Min(meses.Count + 3, 200));

        // --- Hoja 3: desglose completo ---------------------------------------
        var wsD = wb.Worksheets.Add("Desglose");
        string[] hD = { "Dimensión", "Categoría", "Propios", "Tercerizados", "Total", "% tercerizado", "Pax terceros" };
        for (var c = 0; c < hD.Length; c++) wsD.Cell(1, c + 1).Value = hD[c];

        var rD = 2;
        foreach (var g in detalle.GroupBy(x => new { x.Dimension, x.Categoria })
                                 .OrderBy(g => g.Key.Dimension)
                                 .ThenByDescending(g => g.Where(x => !x.EsPropio).Sum(x => x.Viajes)))
        {
            var prop = g.Where(x => x.EsPropio).Sum(x => x.Viajes);
            var terc = g.Where(x => !x.EsPropio).Sum(x => x.Viajes);
            var tot = prop + terc;
            wsD.Cell(rD, 1).Value = g.Key.Dimension;
            wsD.Cell(rD, 2).Value = g.Key.Categoria;
            wsD.Cell(rD, 3).Value = prop;
            wsD.Cell(rD, 4).Value = terc;
            wsD.Cell(rD, 5).Value = tot;
            if (tot > 0)
            {
                wsD.Cell(rD, 6).Value = Math.Round(terc * 100.0 / tot, 1);
                wsD.Cell(rD, 6).Style.NumberFormat.Format = "0.0";
            }
            wsD.Cell(rD, 7).Value = g.Where(x => !x.EsPropio).Sum(x => x.Pax);
            rD++;
        }
        wsD.Row(1).Style.Font.Bold = true;
        wsD.SheetView.FreezeRows(1);
        wsD.Columns().AdjustToContents(1, Math.Min(rD, 500));

        // --- Hoja 4: oportunidad ---------------------------------------------
        var wsO = wb.Worksheets.Add("Oportunidad");
        string[] hO = { "Tipo pedido", "Propios", "Tercerizados", "% tercerizado", "Sin cubrir",
                        "Unidades propias del tipo", "Días-unidad sin salir", "¿Revisar?" };
        for (var c = 0; c < hO.Length; c++) wsO.Cell(1, c + 1).Value = hO[c];

        var rO = 2;
        foreach (var o in oportunidad)
        {
            wsO.Cell(rO, 1).Value = o.Tipo;
            wsO.Cell(rO, 2).Value = o.Propios;
            wsO.Cell(rO, 3).Value = o.Tercerizados;
            if (o.Asignados > 0)
            {
                wsO.Cell(rO, 4).Value = Math.Round(o.PctTercerizado, 1);
                wsO.Cell(rO, 4).Style.NumberFormat.Format = "0.0";
            }
            wsO.Cell(rO, 5).Value = o.SinAsignar;
            wsO.Cell(rO, 6).Value = o.UnidadesPropias;
            wsO.Cell(rO, 7).Value = o.DiasUnidadOciosos;
            wsO.Cell(rO, 8).Value = o.HayPregunta ? "SÍ" : "";
            if (o.HayPregunta)
                wsO.Range(rO, 1, rO, hO.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF4E3");
            rO++;
        }
        wsO.Cell(rO + 1, 1).Value =
            "ESTA HOJA SIRVE PARA PREGUNTAR, NO PARA CONCLUIR. Un «día-unidad sin salir» no es "
            + "capacidad disponible: la unidad pudo estar en taller, sin chofer o de franco, y en "
            + "un feriado figura parada toda la flota. Se abre por tipo porque tercerizar una VAN "
            + "no se cubre con un BUS parado.";
        wsO.Cell(rO + 2, 1).Value = periodo;
        wsO.Row(1).Style.Font.Bold = true;
        wsO.SheetView.FreezeRows(1);
        wsO.Columns().AdjustToContents(1, Math.Min(oportunidad.Count + 1, 200));

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Artículos por rubro de consumo (estacion_rubro_articulo).</summary>
    public byte[] ArticulosRubro(IReadOnlyList<ArticuloRubroRow> filas)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Articulos");
        string[] headers = { "Código", "Rubro", "Artículo" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var x in filas)
        {
            ws.Cell(r, 1).Value = x.Id;
            ws.Cell(r, 2).Value = x.Rubro;
            ws.Cell(r, 3).Value = x.Nombre;
            r++;
        }
        EstiloHeaderAgenda(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Paleta de colores por estado, idéntica a grid_color_viaje (funcion.prg).</summary>
    private static string? ColorEstado(string estado) => estado switch
    {
        "ASIGNADO"   => "#FFFF80", // RGB(255,255,128) amarillo claro
        "CURSO"      => "#FF80FF", // RGB(255,128,255) rosa
        "FINALIZADO" => "#C0C0C0", // RGB(192,192,192) gris claro
        "FACTURADO"  => "#98C5BF", // RGB(152,197,191) verde grisáceo
        "CHEQUEO"    => "#52CEFE", // RGB(82,206,254)  celeste
        _            => null       // blanco (sin relleno)
    };
}
