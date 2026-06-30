# Historial del viaje — `trafico_historial.scx`

Form que abre la opción **"Historia del viaje"** del menú contextual de Tráfico
(`trafico2.scx`). Muestra la **bitácora completa de una reserva**: quién la creó/eliminó/
modificó y cada movimiento operativo (alta, chequeo, asignación, cambio de unidad,
finalización, cancelación…). **Solo lectura** en el FoxPro también (no edita nada).

## Cómo se invoca

`trafico2.scx` → menú contextual sobre una fila → "Historia del viaje" →
`DO FORM trafico_historial WITH <id_viaje>`. El form recibe `lpId_viaje` como parámetro y
toma la cabecera de auditoría del cursor de la fila ya cargada (`cursorViajeReserva`).

## Estructura (del `Init`)

### Cabecera — recuadro gris (`Shape1`), 3 pares usuario + fecha

Salen de la tabla **`viaje`** (campos de auditoría de la réplica):

| Etiqueta | Usuario | Fecha |
| --- | --- | --- |
| **Creador** | `u_create` | `f_create` |
| **Elimino** | `u_delete` | `f_delete` |
| **Ult. Modifico** | `u_modify` | `f_modify` |

(En el FoxPro los textbox son `Enabled=.F.` con `DisabledForeColor` azul → de ahí el azul
del usuario en la réplica Blazor.)

### Grilla — `viaje_log WHERE id_viaje = lpId_viaje`

9 columnas, en este orden exacto:

| # | Caption FoxPro | Campo cursor (`cursorViajeLog`) | Campo réplica SQL | Notas |
| --- | --- | --- | --- | --- |
| 1 | Hora | `hora` | `hora` (datetime2) | El form la rotula "Hora" pero el dato es **fecha+hora** |
| 2 | Usuario | `usuario` | `usuario` | quién hizo el movimiento |
| 3 | Motivo | `motivo` | `motivo` | ALTA / CHEQUEO / ASIGNO / FINALIZO / CBIO UNIDAD / CANCELO… |
| 4 | Chofer | `id_chofer` | `id_chofer` | código de chofer (no nombre) |
| 5 | Cronograma | `cronograma` | `cronograma` | unidad/cronograma origen (alineación izq.) |
| 6 | Cron. Nuevo | `cronograma_new` | **`cronogram2`** | unidad/cronograma nuevo (truncado a 10 chars) |
| 7 | Int. Orig | `interno_ori` | **`interno_or`** | nº interno antes (alineación der.) |
| 8 | Int. Nuevo | `interno_new` | **`interno_ne`** | nº interno después (alineación der.) |
| 9 | Comentario | `comentario` | `comentario` | texto largo (nvarchar 508) |

> ⚠️ **Nombres truncados por la réplica DBF→SQL (10 chars):** `cronograma_new` → `cronogram2`,
> `interno_ori` → `interno_or`, `interno_new` → `interno_ne`. Verificado contra `sys.columns`.

### Botones

| Botón | Acción FoxPro | Equivalente Blazor |
| --- | --- | --- |
| **Zoon Motivo** (`bZoon`) | `DO FORM form_error WITH cursorViajeLog.comentario` — abre el comentario de la fila seleccionada en grande (textarea read-only) | `TextoZoomDialog` (sub-diálogo "Zoon") |
| doble-click en la grilla | `thisform.bZoon.Click()` (lo mismo que Zoon Motivo) | `@ondblclick` en la fila → abre el mismo sub-diálogo |
| **Cerrar** (`Command1`) | `thisform.release` | cierra el diálogo |

## Tablas tocadas

| Tabla | Uso | Índice usado |
| --- | --- | --- |
| `viaje` | cabecera de auditoría (1 fila) | `ix_viaje_f_reserva` (acotar por `f_reserva` para SEEK, igual que el Zoom) |
| `viaje_log` | bitácora de movimientos | **`IX_viaje_log_idviaje`** ✅ existe → seek directo por `id_viaje` pese a las 4,4M filas |

**Performance:** `viaje_log` SÍ tiene índice propio por `id_viaje` (a diferencia de `viaje`,
que no lo tiene), así que la grilla del historial es un seek barato sin necesidad de
`f_reserva`. La cabecera (sobre `viaje`) sí se acota por `f_reserva` para evitar el scan
completo. Detalle del problema de scan de `viaje`: skill `modulo-trafico` § Performance.

## Migrado a Blazor (29/06/2026) — solo lectura

- `Components/Shared/HistorialViajeDialog.razor` — el diálogo principal (cabecera + grilla).
- `Components/Shared/TextoZoomDialog.razor` — el sub-zoom "Zoon" (comentario ampliado),
  genérico y reutilizable.
- `ReportService.GetHistorialViajeAsync(idViaje, fReserva)` → `HistorialViajeDto` (cabecera +
  `List<HistorialViajeRow>`).
- `ExcelExportService.HistorialViaje(dto)` — export del historial.
- Enganchado en el ítem "Historia del viaje" del menú contextual de `PlanillaTrafico.razor`.
- **Valor agregado sobre el FoxPro:** motivo coloreado (badge por tipo de movimiento),
  exportar a Excel, fecha+hora completa en la columna Hora.
- **Validado** contra datos reales (reservas 1520084 y 1520095): cabecera y grilla coinciden
  1:1 con el FoxPro.
