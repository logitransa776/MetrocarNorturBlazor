# Lógica FoxPro — Filtros de la grilla de Tráfico (`trafico2.scx`)

> Extraído del binario DBF/SCT con lector Python (`scx_dump.py`).
> Fuente: `C:\MetroCarSys\Forms\trafico2.scx` — el form productivo: el menú llama
> `OpenForm("trafico2","SUPERVISOR",.t.)` (`MENU_PRINCIPAL.MPR` línea 150).
> `trafico3.scx` es una copia casi idéntica; `trafico.scx` es una versión vieja.

Documenta los **combos de unidades**, el botón **S/C** y el botón **Cxl (cancelados)**
de la toolbar, migrados a `PlanillaTrafico.razor` en junio 2026.

---

## Mapa de controles de la toolbar

| Control FoxPro | UI | Qué hace |
|---|---|---|
| `cronogramaCbio` (combobox, Left=8) | Combo 1 | "Unidad Programada" — interno por empresas |
| `cronograma` (combobox, Left=84) | Combo 2 | "Unidad Asignada" — todos los internos |
| `bSinCronograma` (Caption `\<S/C`) | Botón S/C | Servicios **sin unidad asignada** |
| `Command3` (Caption `C\<xl`) | Botón Cxl | Vista de servicios **CANCELADOS** del día |
| `bRefresh` | Ref | Refresh (filtro REFRESH) |
| `bFiltroEmp` / `bFiltroTur` | Emp / Tur | Tipo de ingreso (origen P / T) |
| `bAnterior` / `bSiguiente` | << / >> | Día anterior / siguiente |
| `verBuses` / `verchequeo` / `chkNortur` | checkboxes | Buses / Chequeo / NORTUR |

---

## Columnas U/Pr, U/Cb, U/As de la grilla (sup_ref)

**Fuente exacta:** `arma_grid_viaje_sup_ref` (vista normal) y `arma_grid_viaje_sup_cnl` (cancelados).

| Columna | Tooltip FoxPro | Campo FoxPro | Columna en réplica SQL |
|---|---|---|---|
| **U/Pr** | "Unidad Programada" | `cronogramacbio` | **`cronogram2`** (nombre truncado a 10) |
| **U/Cb** | "Unidad Asignada" | `cronograma` | `cronograma` |
| **U/As** | "Unidad Cumple Servicio" | `id_interno` (normal) / `interno` (cancelados) | `id_interno` / `interno` |
| **Veh** | — | `LEFT(id_vehiculo_tipo, 4)` | `id_vehicu2` |

> ⚠️ La réplica trunca los nombres FoxPro a 10 caracteres:
> `cronogramacbio` → `cronogram2`, `id_vehiculo_tipo` → `id_vehicu2`, `nombre_guia` → `nombre_gui`.

---

## Combo 1 — Unidad Programada (interno por empresas)

**Carga (Init del form):**

```foxpro
Select Iif( b.diagrama , a.cronograma , b.id_contratado ) As cronograma , b.orden
From vehiculo a INNER Join fletero b On a.fletero = b.id_contratado
Group By 1 , 2 Order By b.orden Into Cursor cursorCronogramaDiagrama
```

- Si el fletero tiene flag **`diagrama`** → lista cada interno individual (caso NORTUR: NT0001…NT9999).
- Si no → **una sola entrada por empresa** (`id_contratado`: TEDESCHI, NEUQUEN, MTL, VANSQ, REMIS…).
- En la réplica: `fletero.id_contrat`, `fletero.orden`, `fletero.diagrama`. ~124 ítems.

**Filtro (`InteractiveChange` → `aplica_filtro("_CRONOGRAMACBIO")` → `arma_grid_viaje`):**

```foxpro
* cCronogramaFiltro se padea a 10 chars: Left(Alltrim(valor)+Space(10),10)
Select ... From viaje Where str_f_reserva + cronogramacbio = xFecha1 + cCronogramaFiltro
```

Es decir: **`viaje.cronogramacbio = valor`** (réplica: `cronogram2`) para el día activo.
El `GotFocus` del combo **limpia el otro combo** (exclusión mutua).

## Combo 2 — Unidad Asignada (todos los internos)

**Carga (Init del form):**

```foxpro
Select a.cronograma From vehiculo a INNER Join fletero b On a.fletero = b.id_contratado
Where Empty(a.f_delete) .And. a.activo
Order By b.orden , a.interno Into Cursor cursorCronogramaTrafico
```

- **Todos los internos activos** (vehículo no borrado y activo), ordenados por empresa y nº de interno. ~204 ítems.

**Filtro (`aplica_filtro("CRONOGRAMA")`):** `viaje.cronograma = valor` para el día activo.

## Botón S/C — Sin Cronograma

```foxpro
PROCEDURE bSinCronograma.Click
  cCronogramaFiltro = "S/C"
  cFiltroActivo     = "CRONOGRAMA"
  Thisform.cronograma.Value = ""
  Thisform.aplica_filtro(cFiltroActivo)
```

= filtra **`viaje.cronograma = 'S/C'`**: los servicios que **no tienen unidad asignada**
(el sistema graba el literal `'S/C'` en `cronograma`/`cronogramacbio` cuando no hay asignación).
Limpia los combos al activarse.

## Botón Cxl — Servicios cancelados

```foxpro
PROCEDURE Command3.Click
  * (warning solo si el rango de fechas es 1900-2100)
  thisform.cronograma.Value     = ""
  thisform.cronogramaCbio.Value = ""
  cTipoReservaFiltro            = ""
  cFiltroActivo                 = "CANCELADO"
  * apaga verBuses y verchequeo
  thisform.aplica_filtro(cFiltroActivo)
```

**Query (caso CANCELADO de `arma_grid_viaje`):**

```foxpro
Select a.* , <destino> , b.motivo As motivo_cancela
From viaje a , viaje_motivo_cancela b
Where str_f_reserva = xFecha1 .And. a.id_motivo = b.Id
Order By hs_inicio
* después borra del cursor todo lo que NO sea estado_viaje = "CANCELADO"
```

- Caption: `"... - Cantidad de Servicios : N SERVICIOS CANCELADOS"`.
- Grilla cambia a `arma_grid_viaje_sup_cnl` (22 columnas): agrega **Ob** ('C' si hay comentario),
  **Ad** ('A' si hay adicionales `adi_cod_1..5`) y **Motivo** (`viaje_motivo_cancela.motivo`);
  quita H.Pre, Fletero, Chofer, Agua.
- Deshabilita: combos, S/C, Emp, Tur, Chequeo, Asignar, Reasignar, Liberar, Franco.
- Navegación de día (<< / >>) **queda habilitada**.
- En Blazor se usa **LEFT JOIN** en lugar del inner join (un cancelado sin motivo no desaparece).

---

## Checkbox Buses — panel de flota (`verBuses` → `grid2` / `arma_grid_vehiculo`)

**Click del checkbox:**

```foxpro
PROCEDURE Click   && verBuses
IF this.Value
    thisform.arma_grid_vehiculo()
    Thisform.grid2.Visible = .t.
    thisform.grid1.Width   = thisform.grid2.Left - 15      && la planilla se achica
    thisform.grid2.width   = thisform.Width - thisform.grid1.Width - 15
ELSE
    Thisform.grid2.Visible = .F.
    thisform.grid1.Width   = thisform.Width - 15
ENDIF
```

**Query de `arma_grid_vehiculo` (estado vivo de la flota — NO depende del día navegado):**

```foxpro
Select a.* , SPACE(6) as trabaja , b.cronograma as codFletero , b.orden
From vehiculo a INNER Join fletero b On a.fletero = b.id_contratado
Where activo And Empty(a.f_delete)
Order By b.orden , a.interno
Into Cursor cursorVehiculoTrafico Readwrite
```

**Post-procesamiento por fila:**

1. Si `interno < 999`: busca `chofer_franco` con `id_chofer = vehiculo.id_chofer`
   y **`fecha = Date()`** (HOY, no el día navegado) → columna **Franco** = `codigo`.
2. `estado = "ASIGNADO"` con `hs_inicio <= Datetime()` → se **muestra** `CURSO` (solo display).

**Las 12 columnas de grid2:** Fletero, Interno, Chofer (`id_chofer`), 2ª Chofer (`id_chofer2`),
Franco (`trabaja`), Estado, Nº Viaje (`id_viaje`), Zona (`id_zona`), Nextel, Pax,
Vehiculo (`id_vehiculo` → réplica `id_vehicul`), Hs. Inicio (`hs_inicio`).

**Colores** (`grid_color_vehiculo`, funcion.prg:952): `ASIGNADO` → RGB(255,255,128) amarillo,
`CURSO` → RGB(255,128,255) rosa, resto blanco.

**Otros comportamientos:**

- Headers clickeables para ordenar (`ClickMHeader2` via `Bindevent`).
- El botón **Cxl apaga verBuses** al entrar a la vista de cancelados.
- `arma_grid_vehiculo()` se rearma tras cada asignación/liberación (el panel es "vivo").
- La tabla `vehiculo` guarda el último estado/viaje/chofer de cada unidad
  (campos `estado`, `id_viaje`, `pax`, `hs_inicio` se pisan en cada asignación).

**Migración a Blazor:** `ReportService.GetPanelBusesAsync()` (OUTER APPLY TOP 1 sobre
`chofer_franco` reemplaza el lookup por fila) + panel lateral `.buses-panel` en
`PlanillaTrafico.razor` (botón Buses en la toolbar, junto a Nortur).

---

## Post-procesamiento del cursor (vista normal — `arma_grid_viaje`)

Tras cualquier filtro que NO sea CANCELADO, el FoxPro recorre el cursor y:

1. **Borra** las filas `estado_viaje = "CANCELADO"` (por eso la vista normal nunca las muestra).
2. Si `chkNortur` está **destildado**: borra filas donde `cronograma`, `cronogramacbio`
   o `id_chofer` = `parametro.id_cliente_prueba` (= `"NORTUR"`).
3. `ASIGNADO` con `hs_inicio <= ahora` → se **muestra** como `CURSO` (solo display).
4. `SIN ASIGNAR` con `chequeo > 0` → se **muestra** como `CHEQUEO` (solo display).
5. Cuenta filas → caption `"... - Cantidad de Servicios : N"`.

---

## Migración a Blazor (PlanillaTrafico.razor) — decisiones

| Tema | FoxPro | Blazor |
|---|---|---|
| Filtro de combos / S/C | Re-consulta la base | Filtro en memoria sobre las filas del día (mismo resultado, día único) |
| Combos | Listas crudas con duplicados/vacíos | `Distinct()` + sin vacíos |
| Cancelados sin motivo | Desaparecen (inner join) | Quedan con Motivo vacío (LEFT JOIN) |
| Gps Cod / Lote / Nº Viaje | Columnas visibles | Omitidas (consistente con la vista normal ya migrada) |
| Combos service | `Init` del form | `ReportService.GetCombosUnidadesTraficoAsync()` (caché 5 min) |
| Cxl service | `arma_grid_viaje` CANCELADO | `ReportService.GetTraficoCanceladosAsync(dia)` |
| Export cancelados | Excel del grid | `ExcelExportService.TraficoCancelados()` |
