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

---

## Menú de botón derecho → "Novedad sobre el viaje" y "Lista de pasajeros" (solo lectura)

Dos ítems más del menú contextual (`menu_viaje_reserva.mpr`), migrados en **solo lectura**
(30/06/2026). En FoxPro ambos eran de **escritura**; acá se muestran los datos ya cargados
(la escritura sigue en FoxPro — estrategia strangler).

### "Novedad sobre el viaje" (BAR 4 → `libro_novedad_alta`)

```foxpro
PROCEDURE libro_novedad_alta
DO FORM libro_novedad_abm WITH "alta" , "" , cursorViajeReserva.id_viaje TO lOkey
```

- El FoxPro **DA DE ALTA** una novedad en `libro_novedad` ligada al viaje. El form
  (`libro_novedad_abm.scx`) pide **Asunto** (precargado con `viaje.nombre_cliente`) +
  **Mensaje**, y graba:
  `INSERT INTO libro_novedad (f_carga, asunto, mensaje, usuario_create, id_viaje) VALUES (...)`.
- Opcionalmente **envía un correo automático** a los contactos/emails del cliente
  (`envio_correo_gmail`, hasta 10 contactos) — esa parte NO se migra.
- **Blazor (solo lectura):** `ReportService.GetNovedadesViajeAsync(idViaje)` lista las
  novedades ya cargadas de ese viaje. Diálogo `NovedadViajeDialog.razor` (tarjetas con
  asunto/fecha/usuario/mensaje; badge FINALIZADA si `finalizo=1`).
- **Tabla `libro_novedad`** (48.160 filas, 19.877 con `id_viaje > 0`). Trampas de réplica:
  `usuario_create` → **`usuario_cr`** (truncado a 10; ídem `usuario_de`/`usuario_mo`);
  `id_viaje` es **bigint** (en `viaje` es int). Siempre `_deleted = 0`.

### "Lista de pasajeros" (form `trafico_pasajero_planilla.scx`)

> ⚠️ En el `.mpr` de disco (jun 2021) este ítem **no está** en el menú contextual; sí está
> en la captura del `.exe` productivo y en el menú principal (`MENU_PRINCIPAL.MPR` BAR 16,
> "Lista de pasajeros"). Otra evidencia de que el `.exe` está más actualizado que el fuente.

- El FoxPro es un **ABM completo** de la planilla CNRT (manifiesto): carga pasajeros con
  DNI/nacionalidad/profesión/sexo/fecha nac. + datos de empresa transportista + choferes +
  vehículos, y la **imprime en PDF** (`REPORT FORM viaje_pasajero`). 6 pestañas, 235 objetos.
- **Blazor (solo lectura):** `ReportService.GetPasajerosViajeAsync(idViaje)`. Si el viaje
  tiene planilla generada (`viaje_pasajero`) la muestra con cabecera + grilla de pasajeros
  (`viaje_pasajero_detalle`); si no, avisa "sin lista generada" (réplica del cartel rojo
  "Lista de pasajeros No Generada Aun"). Diálogo `ListaPasajerosDialog.razor`.
- **Realidad de la réplica:** `viaje_pasajero` = **1 fila** (un servicio de 2009),
  `viaje_pasajero_detalle` = **0 filas** → casi siempre dirá "sin lista". Se migra por
  completitud del menú, no por volumen. Trampas: `empresa_nom`→`empresa_no`,
  `razon_social`→`razon_soci`, `nacionalidad`→`nacionalid`; `id_viaje` bigint.

> El menú contextual existe **solo en la vista normal** de la grilla (las filas de la vista de
> cancelados — botón Cxl — no tienen `@oncontextmenu`, fiel al FoxPro que deshabilita acciones
> sobre cancelados). En `PlanillaTrafico.razor`: handlers `VerNovedadesContexto` /
> `VerListaPasajerosContexto`.

---

## Menú de botón derecho → submenú "Aplicar Filtros" (server-side)

El click derecho sobre la grilla corre `DO menu_viaje_reserva.mpr` (`Grid1.RightClick`).
Ese menú tiene **dos** submenús de filtro que **no hay que confundir**:

- **Aplicar Filtros s/viaje** — filtra tomando valores de la **fila clickeada** (`cursorViajeReserva.*`):
  `Cronograma`→`CRONOGRAMA_SELECCION`, `Cliente y Grupo`→`CLIENTE_SELECCION`,
  `Cliente y Dia`→`CLIENTE_SELECCION_DIA`, `Cliente y Mes`→`CLIENTE_SELECCION_MES`,
  `Servicio Varios Dias (Ruta)`→`VARIO_DIA`, `Hora Inicio`→`HS_INICIO_SELECCION`.
- **Aplicar Filtros** — **abre un diálogo que pide los datos** y re-consulta `viaje`.

> ⚠️ **Cambio de arquitectura:** TODOS los "Aplicar Filtros" re-consultan `viaje` sobre un
> **rango de fechas** `xFecha1..xFecha2` (variables `dFechaViaje`/`hFechaViaje`, o
> `dfechadesde/hastadiagramador` si `lRangoFechaAsignado`). La planilla Blazor era de **un
> solo día** con filtrado en memoria → "Aplicar Filtros" introduce un **modo filtro
> server-side**. Cuando el rango ≠ un día, el FoxPro **deshabilita la navegación `<< >>`**
> (`lRangoFechaAsignado` / `bAnterior/bSiguiente.Enabled = .F.`).

### Mapeo etiqueta → código → SQL (`arma_grid_viaje`, verificado en `trafico2.scx`)

Todos llevan implícito `_deleted = 0` (réplica) y el post-proceso descarta `estado_viaje =
'CANCELADO'` salvo la vista Cxl. `BETWEEN(f_reserva, x1, x2)` = `Between(str_f_reserva,xFecha1,xFecha2)`.

| Etiqueta | Código | Diálogo FoxPro | WHERE sobre `viaje` |
|---|---|---|---|
| **Rango de Fechas** | `FECHA` | `trafico_cambia_rango_trabajo` | `BETWEEN(f_reserva, x1, x2)` |
| **Tipo de Reserva** | `TIPO_RESERVA` | `trafico_filtro_tipo_reserva` | `… AND origen = 'T'/'P'` ✅ migrado |
| **Fleteros** | *(no en el .mnx de disco; agregado en el .exe)* | "Buscar reservas por Fletero" (rango + Hoy/Todo + combo) | `… AND fletero = X` ✅ migrado |
| **Conductores** | `CHOFERES` | `trafico_filtro_chofer` | `… AND id_chofer = X` ✅ migrado |
| **Nº Interno** | `INTERNO` | `trafico_filtro_interno` | `… AND interno = N` ✅ migrado |
| **Estados de la Reserva** | `TIPO_ESTADO` | `trafico_filtro_tipo_estado` | `… AND estado_via = X` ✅ migrado |
| **Números de Vuelos** | `VUELO` | `trafico_filtro_vuelo` | `… AND vuelo = X` ✅ migrado |
| **Cliente y Grupo** | `CLIENTE` | `trafico_filtro` | `… AND id_cliente [+ grupo] = X` |
| **Cliente y Varios Grupos** | `CLIENTE_GRUPO` | `trafico_filtro_grupo` | cliente + varios grupos |
| **Nº Reserva** | `RESERVA` | `trafico_nro_reserva` | `id_viaje = N` (**sin rango de fechas**) ✅ migrado |
| **Nº Reserva En Ruta** | `RESERVA_RUTA` | `trafico_nro_reserva` | `id_viaje_i = N` (**sin rango**) ✅ migrado |

**Trampas / pendientes para migrar los que faltan:**
- `Nº Reserva`/`Nº Reserva En Ruta` **ignoran el rango** y filtran por `id_viaje`/`id_viaje_i`
  (réplica fiel: el FoxPro no aplica `Between` en estas dos ramas). **NO descartan `CANCELADO`**:
  si el operador busca un nº puntual lo quiere ver aunque esté cancelado.
  ⚠️ **No hay índice sobre `viaje.id_viaje`** → `WHERE id_viaje = X` hace scan completo
  (~84K lecturas, ver skill `modulo-trafico`); aceptable porque es **1 disparo manual** del
  usuario. Igual para `id_viaje_i`.
- **Columna real en la réplica: `id_viaje_i` (bigint), truncada de `id_viaje_int`** — NO `id_viaje_int`.
  Verificado contra la base (29/06/2026).
- **`id_viaje_i` es el correlativo que agrupa los viajes (días) de una reserva "en ruta"** (modo ruta
  multi-día del módulo Reservas): un mismo número devuelve los N días de esa ruta. En la réplica
  productiva (jun 2026) hay **solo 14 filas con `id_viaje_i > 0`** (valores 6–10), todas de rutas
  multi-día canceladas — el campo casi no se usa, pero el filtro es fiel al FoxPro. `id_viaje` va de
  1.000.000 a 1.521.277. Ambos caben en `int`, aunque `id_viaje_i` sea `bigint`.
- `interno` (numérico) **no está en la proyección actual** (`TraficoProjection`): el filtro `INTERNO`
  se migró por el CÓDIGO de unidad (`id_interno`), no por el número suelto (ver fila Nº Interno).
- `Fleteros` no está en el fuente de disco (jun 2021) → confirmar contra el `.exe` productivo;
  por analogía es `fletero = X` con selector de fleteros.
- Cliente/Conductor/Fletero/Grupo necesitan **diálogos selectores con datos** (catálogos).

### Migración a Blazor (jun 2026) — modo filtro

- **Datos:** `ReportService.GetPlanillaTraficoFiltradaAsync(TraficoFiltro)` — misma proyección
  (`TraficoProjection`) y mapeo (`MapPlanillaRow`) que la vista de día; WHERE por rango.
  Tipos `TraficoFiltro` / `TraficoFiltroTipo`. **Implementados: `Fecha`, `TipoReserva`, `Fletero`,
  `Conductores`, `Interno`, `Estado`, `Vuelo`, `Reserva` y `ReservaRuta`** (los dos últimos NO usan rango).
- **UI:** submenú "Aplicar Filtros" en el menú contextual de `PlanillaTrafico.razor`
  (9 filtros activos; Cliente y Grupo, Cliente y Varios Grupos siguen
  deshabilitados). Banner de "Filtro activo" con "Quitar filtro";
  navegación de día y auto-refresh **se apagan** mientras el filtro está activo (fiel al FoxPro).
  Los filtros en memoria (Emp/Tur/Nortur/unidades/buscador) siguen operando sobre el conjunto
  filtrado. Cada filtro tiene su diálogo en `Components/Shared/`:

| Filtro | Diálogo Blazor | Datos / combo | Notas de migración |
|---|---|---|---|
| Fecha | `TraficoFiltroRangoDialog.razor` | — | Rango + Hoy/Todo. |
| Tipo de Reserva | `TraficoFiltroTipoReservaDialog.razor` | — | Rango + Hoy/Todo + radio **Ambos / Transportación / Plantilla** (`viaje.origen` 'T'/'P'; `null`=ambos). `origen` = cómo se cargó la reserva (T=turismo/transfer alta manual, P=plantilla de empresa), mismo eje que los botones Tur/Emp de la toolbar pero server-side por rango. El FoxPro era excluyente (`optiongroup`); acá se agregó "Ambos". Default **Ambos + Hoy**. Validado: 27/05/2026 → 298 P + 47 T = 345 ambos. |
| Fletero | `TraficoFiltroFleteroDialog.razor` | `GetFleterosAsync` | Combo de fleteros; NORTUR preseleccionado. |
| Conductores | `TraficoFiltroConductorDialog.razor` | `GetChoferesParaFiltroAsync(soloActivos)` | Combo cód+nombre + checkbox "solo activos" (`f_delete IS NULL`, réplica de `Check1`). El form FoxPro permitía chofer **o** fletero; acá solo chofer (fletero tiene su propio diálogo). |
| Nº Interno | `TraficoFiltroInternoDialog.razor` | `GetInternosParaFiltroAsync` | **Filtra por el CÓDIGO de unidad (`viaje.id_interno` = NT0044…), no por el número suelto `viaje.interno`.** El número se repite entre vehículos (162 distintos en 406 filas) y no coincide con la grilla; el código (`vehiculo.cronograma`) es único entre activos y es lo que la operadora ve. El combo lista la nómina activa (`activo=1`) con código + nº interno + dominio. Autocomplete por cualquiera de los tres. (El FoxPro original filtraba por el número — acá se mejora.) |
| Estados de la Reserva | `TraficoFiltroEstadoDialog.razor` | combo fijo | 5 estados: SIN ASIGNAR/ASIGNADO/FINALIZADO/**FACTURADO**/CANCELADO (el FoxPro listaba solo 4; se agregó FACTURADO, el 93% de la base). Default SIN ASIGNAR. **Única rama que NO descarta CANCELADO** (si no, ese estado nunca devolvería filas). |
| Números de Vuelos | `TraficoFiltroVueloDialog.razor` | `GetVuelosEnRangoAsync(desde,hasta)` | 3 modos (radio) Sin Vuelo / Con Vuelo / A confirmar (réplica de `Optiongroup1`). En "Con Vuelo" un combo con los vuelos reales del rango (el FoxPro era textbox libre); recarga al cambiar las fechas. |
| Nº Reserva | `TraficoFiltroReservaDialog.razor` (Modo=`Reserva`) | — | Input numérico (`MudNumericField`). Filtra `id_viaje = N` en **toda la base, SIN rango** (réplica fiel). Devuelve 1 viaje. NO descarta CANCELADO. |
| Nº Reserva En Ruta | `TraficoFiltroReservaDialog.razor` (Modo=`ReservaRuta`) | — | Mismo diálogo, otro modo. Filtra `id_viaje_i = N` (correlativo de ruta multi-día) **sin rango**: devuelve los N días de la ruta. NO descarta CANCELADO. |

- **Performance:** el índice `ix_viaje_f_reserva (f_reserva, _deleted, estado_via)` cubre el
  predicado de rango de todos los filtros (Estado además aprovecha `estado_via`). Para
  Interno/Vuelo/Conductor el rango acota primero y el resto es filtro residual — mismo patrón
  que la vista de día. El diálogo avisa cuando el rango supera 31 días.
- **Nº Reserva / Nº Reserva En Ruta no usan rango** → no se benefician de `ix_viaje_f_reserva`.
  Como no hay índice por `id_viaje` ni `id_viaje_i`, hacen **scan completo** de `viaje` (~84K
  lecturas). Es aceptable porque es un disparo manual y puntual del usuario (1 búsqueda, no un
  polling). Si en el futuro molesta, crear `ix_viaje_id_viaje` (lo declinó el cliente jun 2026).
- **El mismo diálogo `TraficoFiltroReservaDialog.razor` sirve para ambos** (parámetro `Modo`):
  cambia etiqueta, ayuda y el tipo de filtro emitido. Validado E2E (Playwright, 29/06/2026):
  `id_viaje=1178999` → 1 fila; `id_viaje_i=9` → 3 filas (los 3 días de la ruta).
