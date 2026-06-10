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
        string metrica /* "Reservas" | "Pax" */)
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
    /// Exporta el informe de Reservas por Banda Horaria.
    /// Hoja 1: detalle plano. Hoja 2: pivote fecha × banda. Hoja 3: resumen por vehículo.
    /// </summary>
    public byte[] BandaHoraria(IReadOnlyList<BandaHorariaRow> filas)
    {
        using var wb = new XLWorkbook();

        // Hoja 1: Detalle
        var wsDet = wb.Worksheets.Add("Detalle");
        wsDet.Cell(1, 1).Value = "Fecha";
        wsDet.Cell(1, 2).Value = "Tipo vehículo";
        wsDet.Cell(1, 3).Value = "Banda horaria";
        wsDet.Cell(1, 4).Value = "Viajes";
        var r = 2;
        foreach (var f in filas)
        {
            wsDet.Cell(r, 1).Value = f.Fecha.ToDateTime(TimeOnly.MinValue);
            wsDet.Cell(r, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            wsDet.Cell(r, 2).Value = f.TipoVehiculo;
            wsDet.Cell(r, 3).Value = f.Banda;
            wsDet.Cell(r, 4).Value = f.Reservas;
            r++;
        }
        wsDet.Row(1).Style.Font.Bold = true;
        wsDet.Columns().AdjustToContents();

        // Hoja 2: Pivote fecha × banda
        string[] bandas = { "00:00-00:01", "00:02-06:29", "06:30-08:29", "08:30-14:00", "14:01-18:00", "18:01-23:59" };
        var fechas = filas.Select(f => f.Fecha).Distinct().OrderBy(d => d).ToList();
        var mapa = filas
            .GroupBy(f => (f.Fecha, f.Banda))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Reservas));

        var wsPiv = wb.Worksheets.Add("Pivote por banda");
        wsPiv.Cell(1, 1).Value = "Fecha";
        for (var c = 0; c < bandas.Length; c++)
            wsPiv.Cell(1, c + 2).Value = bandas[c];
        wsPiv.Cell(1, bandas.Length + 2).Value = "TOTAL";

        for (var i = 0; i < fechas.Count; i++)
        {
            wsPiv.Cell(i + 2, 1).Value = fechas[i].ToDateTime(TimeOnly.MinValue);
            wsPiv.Cell(i + 2, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            var tot = 0;
            for (var c = 0; c < bandas.Length; c++)
            {
                var v = mapa.TryGetValue((fechas[i], bandas[c]), out var x) ? x : 0;
                wsPiv.Cell(i + 2, c + 2).Value = v;
                tot += v;
            }
            wsPiv.Cell(i + 2, bandas.Length + 2).Value = tot;
        }
        wsPiv.Row(1).Style.Font.Bold = true;
        wsPiv.Columns().AdjustToContents();

        // Hoja 3: Resumen por vehículo y banda
        var wsVeh = wb.Worksheets.Add("Resumen por vehículo");
        wsVeh.Cell(1, 1).Value = "Tipo vehículo";
        for (var c = 0; c < bandas.Length; c++)
            wsVeh.Cell(1, c + 2).Value = bandas[c];
        wsVeh.Cell(1, bandas.Length + 2).Value = "TOTAL";

        var vehiculos = filas.Select(f => f.TipoVehiculo).Distinct().OrderBy(v => v).ToList();
        var mapaVeh = filas
            .GroupBy(f => (f.TipoVehiculo, f.Banda))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Reservas));

        for (var i = 0; i < vehiculos.Count; i++)
        {
            wsVeh.Cell(i + 2, 1).Value = vehiculos[i];
            var tot = 0;
            for (var c = 0; c < bandas.Length; c++)
            {
                var v = mapaVeh.TryGetValue((vehiculos[i], bandas[c]), out var x) ? x : 0;
                wsVeh.Cell(i + 2, c + 2).Value = v;
                tot += v;
            }
            wsVeh.Cell(i + 2, bandas.Length + 2).Value = tot;
        }
        wsVeh.Row(1).Style.Font.Bold = true;
        wsVeh.Columns().AdjustToContents();

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
