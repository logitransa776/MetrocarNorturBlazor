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
    public byte[] ReservasFechaServicio(
        IReadOnlyList<ReservaFechaServicioRow> detalle,
        string metrica /* "Reservas" | "Pax" */,
        IReadOnlyList<ReservaFsDetalleRow>? reservas = null)
    {
        using var wb = new XLWorkbook();

        // --- Hoja 1: Detalle (fila por fecha+servicio) -----------------------
        var wsDet = wb.Worksheets.Add("Detalle");
        wsDet.Cell(1, 1).Value = "Fecha";
        wsDet.Cell(1, 2).Value = "Cod. servicio";
        wsDet.Cell(1, 3).Value = "Servicio";
        wsDet.Cell(1, 4).Value = "Reservas";
        wsDet.Cell(1, 5).Value = "Canceladas";
        wsDet.Cell(1, 6).Value = "Pax";
        var rDet = 2;
        foreach (var d in detalle)
        {
            wsDet.Cell(rDet, 1).Value = d.Fecha.ToDateTime(TimeOnly.MinValue);
            wsDet.Cell(rDet, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            wsDet.Cell(rDet, 2).Value = d.CodServicio;
            wsDet.Cell(rDet, 3).Value = d.Servicio;
            wsDet.Cell(rDet, 4).Value = d.Reservas;
            wsDet.Cell(rDet, 5).Value = d.Canceladas;
            wsDet.Cell(rDet, 6).Value = d.Pax;
            rDet++;
        }
        wsDet.Row(1).Style.Font.Bold = true;
        wsDet.Columns().AdjustToContents();

        // --- Hoja 2: Pivote fecha x servicio (valor = métrica elegida) -------
        var wsPiv = wb.Worksheets.Add("Pivote");
        var fechas = detalle.Select(d => d.Fecha).Distinct().OrderBy(f => f).ToList();
        var servicios = detalle.Select(d => d.Servicio).Distinct().OrderBy(s => s).ToList();
        Func<ReservaFechaServicioRow, int> val = metrica == "Pax" ? r => r.Pax : r => r.Reservas;
        var mapa = detalle.ToDictionary(d => (d.Fecha, d.Servicio), val);

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

        // --- Hoja 3: Ranking por servicio ------------------------------------
        var wsRk = wb.Worksheets.Add("Ranking");
        wsRk.Cell(1, 1).Value = "Servicio";
        wsRk.Cell(1, 2).Value = "Reservas";
        wsRk.Cell(1, 3).Value = "Pax";
        wsRk.Cell(1, 4).Value = "Canceladas";
        var ranking = detalle
            .GroupBy(d => d.Servicio)
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

        string[] headers =
        {
            "Reserva","H.Pre","H.Ini","H.Fin","H.Avi","H.Cie","U/Pr","U/Cb","U/As",
            "Chq","Ag","Recorrido","Fletero","Chofer","Veh","Cliente","Pax","Agua",
            "Adj","Comentario","Grupo","Vuelo","Guia","Estado"
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
            ws.Cell(r, 11).Value = f.Ag;
            ws.Cell(r, 12).Value = f.Recorrido;
            ws.Cell(r, 13).Value = f.Fletero;
            ws.Cell(r, 14).Value = f.Chofer;
            ws.Cell(r, 15).Value = f.Veh;
            ws.Cell(r, 16).Value = f.Cliente;
            ws.Cell(r, 17).Value = f.Pax;
            ws.Cell(r, 18).Value = f.Agua;
            ws.Cell(r, 19).Value = f.Adj;
            ws.Cell(r, 20).Value = f.Comentario;
            ws.Cell(r, 21).Value = f.Grupo;
            ws.Cell(r, 22).Value = f.Vuelo;
            ws.Cell(r, 23).Value = f.Guia;
            ws.Cell(r, 24).Value = f.Estado;

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
        IReadOnlyList<BandaHorariaDetalleRow>? viajes = null)
    {
        // La métrica elige qué número va en pivote/resumen (viajes o pax).
        Func<BandaHorariaRow, int> val = metrica == "Pax" ? f => f.Pax : f => f.Reservas;
        var etiquetaMetrica = metrica == "Pax" ? "Pax" : "Viajes";
        var bandas = ReportService.BandasHorarias;

        using var wb = new XLWorkbook();

        // --- Hoja 1: Detalle agregado (fecha × veh × banda, con viajes y pax) ----
        var wsDet = wb.Worksheets.Add("Detalle");
        wsDet.Cell(1, 1).Value = "Fecha";
        wsDet.Cell(1, 2).Value = "Tipo vehículo";
        wsDet.Cell(1, 3).Value = "Banda horaria";
        wsDet.Cell(1, 4).Value = "Viajes";
        wsDet.Cell(1, 5).Value = "Pax";
        var r = 2;
        foreach (var f in filas)
        {
            wsDet.Cell(r, 1).Value = f.Fecha.ToDateTime(TimeOnly.MinValue);
            wsDet.Cell(r, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            wsDet.Cell(r, 2).Value = f.TipoVehiculo;
            wsDet.Cell(r, 3).Value = f.Banda;
            wsDet.Cell(r, 4).Value = f.Reservas;
            wsDet.Cell(r, 5).Value = f.Pax;
            r++;
        }
        wsDet.Row(1).Style.Font.Bold = true;
        wsDet.Columns().AdjustToContents();

        // --- Hoja 2: Pivote fecha × banda (valor = métrica elegida) --------------
        var fechas = filas.Select(f => f.Fecha).Distinct().OrderBy(d => d).ToList();
        var mapa = filas
            .GroupBy(f => (f.Fecha, f.Banda))
            .ToDictionary(g => g.Key, g => g.Sum(val));

        var wsPiv = wb.Worksheets.Add($"Pivote ({etiquetaMetrica})");
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
            .GroupBy(f => (f.TipoVehiculo, f.Banda))
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

        // --- Hoja 4: Viajes uno por uno (drill-down) -----------------------------
        if (viajes is { Count: > 0 })
        {
            var wsV = wb.Worksheets.Add("Viajes");
            string[] cab =
            {
                "Nº Reserva", "Fecha", "Hora", "Banda", "Tipo vehículo", "Servicio", "Cliente",
                "Recorrido", "Pax", "Estado", "Interno", "Chofer", "Grupo", "Origen"
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
                wsV.Cell(rV, 5).Value = d.TipoVehiculo;
                wsV.Cell(rV, 6).Value = v.Servicio;
                wsV.Cell(rV, 7).Value = v.Cliente;
                wsV.Cell(rV, 8).Value = v.Recorrido;
                wsV.Cell(rV, 9).Value = v.Pax;
                wsV.Cell(rV, 10).Value = v.Estado;
                if (v.Interno.HasValue) wsV.Cell(rV, 11).Value = v.Interno.Value;
                wsV.Cell(rV, 12).Value = v.Chofer;
                wsV.Cell(rV, 13).Value = v.Grupo;
                wsV.Cell(rV, 14).Value = v.Origen == "P" ? "Plantilla" : "Transportación";
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
    /// Hoja 1: detalle agregado (mes × cliente × tipo). Hoja 2: pivote cliente × mes con la
    /// métrica elegida. Hoja 3: resumen tipo × mes. Hoja 4 (opcional): los viajes uno por uno.
    /// </summary>
    public byte[] ReservasPorCliente(
        IReadOnlyList<ReservaClienteRow> filas,
        string metrica /* "Reservas" | "Pax" */,
        IReadOnlyList<ReservaClienteDetalleRow>? viajes = null)
    {
        Func<ReservaClienteRow, int> val = metrica == "Pax" ? f => f.Pax : f => f.Viajes;
        var etiquetaMetrica = metrica == "Pax" ? "Pax" : "Viajes";
        var meses = filas.Select(f => f.Mes).Distinct().OrderBy(m => m).ToList();
        var tipos = ReportService.TiposReservaCliente;

        using var wb = new XLWorkbook();

        // --- Hoja 1: Detalle agregado (mes × cliente × tipo, con viajes y pax) ---
        var wsDet = wb.Worksheets.Add("Detalle");
        wsDet.Cell(1, 1).Value = "Mes";
        wsDet.Cell(1, 2).Value = "Código";
        wsDet.Cell(1, 3).Value = "Cliente";
        wsDet.Cell(1, 4).Value = "Tipo";
        wsDet.Cell(1, 5).Value = "Viajes";
        wsDet.Cell(1, 6).Value = "Pax";
        var r = 2;
        foreach (var f in filas)
        {
            wsDet.Cell(r, 1).Value = f.Mes;
            wsDet.Cell(r, 2).Value = f.IdCliente;
            wsDet.Cell(r, 3).Value = f.Cliente;
            wsDet.Cell(r, 4).Value = f.Tipo;
            wsDet.Cell(r, 5).Value = f.Viajes;
            wsDet.Cell(r, 6).Value = f.Pax;
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

        var wsPiv = wb.Worksheets.Add($"Pivote ({etiquetaMetrica})");
        wsPiv.Cell(1, 1).Value = "Cliente";
        for (var c = 0; c < meses.Count; c++)
            wsPiv.Cell(1, c + 2).Value = meses[c];
        wsPiv.Cell(1, meses.Count + 2).Value = "TOTAL";

        for (var i = 0; i < clientes.Count; i++)
        {
            wsPiv.Cell(i + 2, 1).Value = clientes[i].Nombre;
            for (var c = 0; c < meses.Count; c++)
                wsPiv.Cell(i + 2, c + 2).Value =
                    mapa.TryGetValue((clientes[i].Id, meses[c]), out var x) ? x : 0;
            wsPiv.Cell(i + 2, meses.Count + 2).Value = clientes[i].Total;
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
        wsPiv.Cell(rTot, meses.Count + 2).Value = granTot;
        wsPiv.Row(1).Style.Font.Bold = true;
        wsPiv.Row(rTot).Style.Font.Bold = true;
        wsPiv.Column(meses.Count + 2).Style.Font.Bold = true;
        wsPiv.SheetView.FreezeRows(1);
        wsPiv.Columns().AdjustToContents(1, Math.Min(clientes.Count + 2, 500));

        // --- Hoja 3: Resumen tipo × mes (la "página" del pivot FoxPro, abierta) -----
        var wsTipo = wb.Worksheets.Add("Resumen por tipo");
        wsTipo.Cell(1, 1).Value = "Tipo";
        for (var c = 0; c < meses.Count; c++)
            wsTipo.Cell(1, c + 2).Value = meses[c];
        wsTipo.Cell(1, meses.Count + 2).Value = "TOTAL";

        var mapaTipo = filas
            .GroupBy(f => (f.Tipo, f.Mes))
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

        // --- Hoja 4: Viajes uno por uno (drill-down) --------------------------------
        if (viajes is { Count: > 0 })
        {
            var wsV = wb.Worksheets.Add("Viajes");
            string[] cab =
            {
                "Nº Reserva", "Fecha", "Hora", "Cliente", "Tipo", "Servicio", "Recorrido",
                "Pax", "Estado", "Motivo cancelación", "Interno", "Chofer", "Grupo", "Origen"
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
                wsV.Cell(rV, 5).Value = d.Tipo;
                wsV.Cell(rV, 6).Value = v.Servicio;
                wsV.Cell(rV, 7).Value = v.Recorrido;
                wsV.Cell(rV, 8).Value = v.Pax;
                wsV.Cell(rV, 9).Value = v.Estado;
                wsV.Cell(rV, 10).Value = d.Motivo;
                if (v.Interno.HasValue) wsV.Cell(rV, 11).Value = v.Interno.Value;
                wsV.Cell(rV, 12).Value = v.Chofer;
                wsV.Cell(rV, 13).Value = v.Grupo;
                wsV.Cell(rV, 14).Value = v.Origen == "P" ? "Plantilla" : "Transportación";
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
    /// Hoja 1: Resumen por chofer. Hoja 2: Pivote chofer × día (con francos, como el FoxPro).
    /// Hoja 3: Viajes uno por uno (drill-down).
    /// </summary>
    public byte[] ViajesPorChofer(
        IReadOnlyList<ViajesChoferRow> filas,
        string metrica /* "Viajes" | "Km" | "Pax" */,
        DateOnly desde,
        DateOnly hasta,
        IReadOnlyList<ViajesChoferDetalleRow>? viajes = null)
    {
        Func<ViajesChoferRow, int> val = metrica switch
        {
            "Km" => f => f.Km,
            "Pax" => f => f.Pax,
            _ => f => f.Viajes
        };
        var etiqueta = metrica switch { "Km" => "Km", "Pax" => "Pax", _ => "Viajes" };

        using var wb = new XLWorkbook();

        // --- Hoja 1: Resumen por chofer -------------------------------------
        var wsR = wb.Worksheets.Add("Resumen por chofer");
        string[] hR = { "Código", "Chofer", "Localidad", "Tipo", "Viajes", "Turismo", "Cabecera", "Km", "Pax", "Días con actividad" };
        for (var c = 0; c < hR.Length; c++) wsR.Cell(1, c + 1).Value = hR[c];
        var porChofer = filas
            .GroupBy(f => f.IdChofer)
            .Select(g => new
            {
                Id = g.Key,
                Nombre = g.First().Chofer,
                Localidad = g.First().Localidad,
                Tipo = g.First().Tipo,
                Viajes = g.Sum(x => x.Viajes),
                Turismo = g.Sum(x => x.Turismo),
                Cabecera = g.Sum(x => x.Cabecera),
                Km = g.Sum(x => x.Km),
                Pax = g.Sum(x => x.Pax),
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
            wsR.Cell(r, 6).Value = c.Turismo;
            wsR.Cell(r, 7).Value = c.Cabecera;
            wsR.Cell(r, 8).Value = c.Km;
            wsR.Cell(r, 9).Value = c.Pax;
            wsR.Cell(r, 10).Value = c.Dias;
            r++;
        }
        wsR.Row(1).Style.Font.Bold = true;
        wsR.SheetView.FreezeRows(1);
        wsR.Columns().AdjustToContents(1, Math.Min(porChofer.Count + 1, 500));

        // --- Hoja 2: Pivote chofer × día (con francos) ----------------------
        var dias = new List<DateOnly>();
        for (var d = desde; d <= hasta; d = d.AddDays(1)) dias.Add(d);
        var mapa = filas.GroupBy(f => (f.IdChofer, f.Fecha)).ToDictionary(g => g.Key, g => g.Sum(val));

        var wsP = wb.Worksheets.Add($"Pivote ({etiqueta})");
        wsP.Cell(1, 1).Value = "Chofer";
        for (var c = 0; c < dias.Count; c++) wsP.Cell(1, c + 2).Value = dias[c].ToString("dd/MM");
        wsP.Cell(1, dias.Count + 2).Value = "TOTAL";

        var choferes = filas.GroupBy(f => f.IdChofer)
            .Select(g => (Id: g.Key, Nombre: g.First().Chofer,
                Primero: g.Min(x => x.Fecha), Ultimo: g.Max(x => x.Fecha),
                Total: g.Sum(val)))
            .OrderByDescending(x => x.Total).ThenBy(x => x.Nombre)
            .ToList();
        for (var i = 0; i < choferes.Count; i++)
        {
            var ch = choferes[i];
            wsP.Cell(i + 2, 1).Value = ch.Nombre;
            for (var c = 0; c < dias.Count; c++)
            {
                var dia = dias[c];
                if (mapa.TryGetValue((ch.Id, dia), out var v))
                    wsP.Cell(i + 2, c + 2).Value = v;
                else if (dia >= ch.Primero && dia <= ch.Ultimo)
                    wsP.Cell(i + 2, c + 2).Value = "F";   // franco (día sin viajes entre 1º y último)
            }
            wsP.Cell(i + 2, dias.Count + 2).Value = ch.Total;
        }
        wsP.Row(1).Style.Font.Bold = true;
        wsP.Column(dias.Count + 2).Style.Font.Bold = true;
        wsP.SheetView.FreezeRows(1);
        wsP.Columns().AdjustToContents(1, Math.Min(choferes.Count + 1, 500));

        // --- Hoja 3: Viajes uno por uno -------------------------------------
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
