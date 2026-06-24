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
