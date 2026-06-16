---
name: modulo-trafico
description: Conocimiento del módulo Tráfico de Metrocar — la operación diaria de servicios (despacho). Usar SIEMPRE que se trabaje en la Planilla de Tráfico, panel Buses, Zoom del Viaje, servicios cancelados, asignación de unidades/choferes, estados del viaje (SIN ASIGNAR/ASIGNADO/CURSO/FINALIZADO/FACTURADO/CANCELADO), francos, chequeos, o cualquier pantalla/dato del módulo Tráfico, tanto para informes como para los futuros workflows de asignación. Mapa de tablas, estados, forms FoxPro, qué ya está migrado y qué falta.
---

# Módulo Tráfico — mapa de conocimiento

Tráfico es la pantalla central del Metrocar: el despachante ve los servicios del día y
asigna unidades/choferes. Es el módulo más usado y el primero en migración.

## Tablas del módulo

| Tabla | Rol | Detalle |
| --- | --- | --- |
| `viaje` | un servicio/reserva (512K filas) | columnas truncadas: `cronogram2` (=U/Pr), `cronograma` (=U/Cb), `id_vehicu2`, `nombre_gui` |
| `vehiculo` | **estado VIVO de cada unidad** | `estado`, `id_viaje`, `pax`, `hs_inicio`, `id_chofer`, `id_chofer2` se PISAN en cada asignación |
| `chofer_franco` | francos por chofer y fecha | `codigo`, `fecha`, `motivo` |
| `fletero` | empresas (orden de listado, flag `diagrama`) | `id_contrat`, `orden`, `cronograma` |
| `viaje_motivo_cancela` | motivos de cancelación | join por `viaje.id_motivo` |
| `cronograma` | cronogramas de servicio | ABM en menú sistema |
| `zona`, `servicio`, `cliente`, `chofer` | catálogos relacionados | — |

## Máquina de estados del viaje (`estado_via`)

```
SIN ASIGNAR ──asignar──► ASIGNADO ──fin──► FINALIZADO ──factura──► FACTURADO
     │                       │
     │ chequeo>0             │ hs_inicio <= ahora
     ▼ (solo display)        ▼ (solo display)
  CHEQUEO                  CURSO
     └──────── cancelar ──► CANCELADO (con motivo)
```

- CURSO y CHEQUEO **no se graban**: son conversiones de display al armar la grilla.
- Colores (de `funcion.prg`): ASIGNADO `#FFFF80` amarillo, CURSO `#FF80FF` rosa,
  FINALIZADO `#C0C0C0`, FACTURADO `#98C5BF`, CHEQUEO `#52CEFE`. Mismos colores en Blazor.
- La vista normal **nunca muestra CANCELADO** (vista propia con botón Cxl).

## Qué ya está migrado (NO rehacer)

| Pieza | Dónde |
| --- | --- |
| Planilla del día (grilla 25 col, colores, doble-click zoom) | `Components/Pages/PlanillaTrafico.razor` |
| Combos U/Pr / U/Cb + S/C + Emp/Tur/Nortur + buscador | ídem (filtros en memoria) |
| Vista cancelados (Cxl) con motivo | ídem + `GetTraficoCanceladosAsync` |
| **Panel Buses** (grid2: flota viva, franco, colores) | ídem + `GetPanelBusesAsync` |
| Zoom del Viaje (solo lectura) | `Components/Shared/ZoomViajeDialog.razor` |
| Export Excel (planilla y cancelados) | `ExcelExportService` |
| **Auto-refresh inteligente 60s** (token de versión + flash de cambios) | `PlanillaTrafico.razor` + `GetTraficoVersionAsync` |
| Grilla estilo "Ops Densa" (barra de estado + tinte, paleta desaturada) | clases `fila-estado--*` en `app.css` |

Queries del módulo en `ReportService.cs`: `GetPlanillaTraficoAsync`,
`GetTraficoCanceladosAsync`, `GetCombosUnidadesTraficoAsync`, `GetPanelBusesAsync`,
`GetTraficoVersionAsync` (liviana, sin caché) + `InvalidarCacheTrafico`.

### Auto-refresh de la planilla (patrón, jun 2026)

- Las queries de tráfico usan **TTL 55s** (`CacheTtlTrafico`), no los 5 min globales.
- `PlanillaTrafico.razor` corre un `PeriodicTimer` de 60s: pide `GetTraficoVersionAsync(dia)`
  (COUNT + MAX(_updated_at) de `viaje` del día + MAX(_updated_at) de `vehiculo`, SIN caché);
  si el token (record, igualdad por valor) no cambió, solo refresca el reloj de la leyenda.
- Si cambió: `InvalidarCacheTrafico(dia)` → recarga → diff por record-equality de
  `PlanillaTraficoRow` → set `_filasCambiadas` → clase `.fila-flash` (animación CSS 3s)
  → a los 3.5s se limpia el set para que un próximo cambio vuelva a destellar.
- Las filas usan `@key="f.IdViaje"` para que el diff de Blazor mueva nodos al reordenar.
- Los colores de estado van por clase (`EstadoCss(estado)` → `fila-estado--asignado` etc.),
  ya NO por style inline. La paleta desaturada vive en `app.css`.

### Performance — índices de `viaje` y reglas críticas (jun 2026)

Relevado en el SQL de producción (172.25.69.217, SQL Server 2012, `viaje` = 521K filas):

| Tabla | Clustered PK | Índices custom que existen |
| --- | --- | --- |
| `viaje` | `_sync_id` (¡NO `id_viaje`!) | `ix_viaje_f_reserva (f_reserva,_deleted,estado_via)`, `ix_viaje_hs_inicio` |
| `viaje_adicional` | `id` (sync) | ninguno por `id_viaje` |

- **NO hay índice sobre `viaje.id_viaje`.** Cualquier query `WHERE id_viaje = X` (p. ej. el
  detalle del Zoom) hace **scan paralelo completo: ~84.000 lecturas lógicas + 125 ms CPU por
  fila**, satura el SQL 2012 y rota el buffer pool. `id_viaje` es único (521230 distintos).
- **Regla del Zoom:** `GetDetalleViajeAsync(idViaje, fReserva)` filtra **también por
  `f_reserva`** (la fila de la planilla siempre la conoce → se pasa por `ZoomViajeDialog.FReserva`).
  Eso convierte el scan en un SEEK por `ix_viaje_f_reserva`: **84.442 → ~1.050 lecturas, 125 → 0 ms.**
  Cualquier lookup futuro por viaje DEBE acotar por `f_reserva` (o `_sync_id`) mientras no exista
  el índice por `id_viaje`. Pendiente recomendado (lo declinó el cliente jun 2026): crear
  `ix_viaje_id_viaje` y `ix_viaje_adicional_id_viaje` → bajaría a ~6 lecturas (seek directo).
- **Auto-refresh acotado a la ventana viva:** `PlanillaTrafico.EsFechaViva(dia)` = `dia >=
  hoy-15d` (incluye futuras). Las fechas más viejas están congeladas (Metrocar ya no las edita)
  → se cargan una vez y el `PeriodicTimer` NO las pollea (la leyenda muestra "histórico").
  Tráfico es **solo lectura**: Blazor nunca escribe; las "actualizaciones" son el polling + la
  réplica DBF→SQL de fondo.
- **Trampa:** el flag `Adj` de la planilla (basado en `adi_cod_1..5` de `viaje`) está **vacío en
  los viajes recientes** aunque tengan filas en `viaje_adicional` (540 casos jun 2026). NO sirve
  para saltear la query de adicionales ni como indicador confiable de adicionales en la grilla.

### Performance — render de la grilla en el navegador (jun 2026)

Síntoma: el Zoom tardaba **6-7s en abrir** sobre fechas con muchos servicios. Medido con
instrumentación (`Stopwatch` + log) **todo el lado servidor termina en ~55 ms** (datos 28-210 ms,
`ShowAsync` 7-111 ms) — NO era ni la base ni Blazor-servidor. El tiempo se iba en el **navegador**:
la planilla es un `<table>` con **las 365 filas siempre en el DOM** (~9.000 celdas) + headers
`position:sticky` dentro de `.trafico-wrap` (scroll). Al montar el overlay del diálogo encima, el
navegador re-pintaba toda la tabla detrás → 6-7s en máquinas modestas.

- **Fix (CSS, `app.css`):** `.trafico-grid tbody tr { content-visibility: auto;
  contain-intrinsic-size: auto 22px; }`. El navegador omite el layout/pintado de las filas fuera
  del viewport (solo renderiza las ~30 visibles). Cero cambios de C#/markup, reversible.
- Para diagnosticar "6-7s pero el SQL da 0 ms": el plan cache (`sys.dm_exec_query_stats`) mide
  *ejecución* de la query, NO la apertura de conexión ni el render. Si query=0 ms y conexión=0-180 ms
  (pool .NET reusa) pero el usuario espera segundos → es **render del navegador**, no la base.
- Si en el futuro la grilla crece o `content-visibility` desalinea columnas al scrollear, el paso
  siguiente es virtualizar (`<Virtualize SpacerElement="tr">`) o `MudTable Virtualize="true"`.

## Documentación de lógica FoxPro (leer cuando se necesite el detalle)

- `docs/logica-foxpro/TRAFICO2_FILTROS.md` — toolbar completa de `trafico2.scx`: combos,
  S/C, Cxl, **panel Buses (arma_grid_vehiculo)**, post-procesamiento del cursor.
- `docs/logica-foxpro/TRAFICO_ZOOM.md` — Zoom del Viaje: máquina de estados, validaciones,
  tablas tocadas.

## Qué falta (el "ABM de Tráfico" = workflow de asignación, NO CRUD)

Botones de `trafico2.scx` pendientes — cada uno **escribe en `viaje` Y actualiza el estado
vivo en `vehiculo`**, y el FoxPro rearma la grilla y el panel tras cada operación:

| Botón | Qué hace |
| --- | --- |
| Asig U/P | asigna unidad programada al viaje |
| Otra Unidad / Reas | reasigna unidad (pide motivo — `viaje_motivo_cambio`) |
| Libe | libera la unidad (vehiculo.estado = LIBERADO, id_viaje = 0) |
| Chequeo | incrementa `viaje.chequeo`; SIN ASIGNAR → muestra CHEQUEO |
| Cxl (cancelar un viaje) | estado CANCELADO + `id_motivo` |
| Franco | alta en `chofer_franco` (form `chofer_franco_abm.scx`) |
| Frc / Comb / GPS | franco rápido / combustible / modo GPS |

**Antes de construir cualquiera de estos:** son ESCRITURA → aplicar la skill `abm-metrocar`
(regla: SQL dueño tabla por tabla; `viaje`/`vehiculo` siguen siendo de FoxPro hasta que se
decida el cutover de Tráfico — el más delicado del sistema). Extraer la lógica exacta del
botón con `foxpro-extract` desde `trafico2.scx` y documentarla antes de codear.

## Forms FoxPro del módulo

`trafico2.scx` (productivo — el menú abre este), `trafico3.scx` (copia), `trafico.scx`
(viejo), `trafico_zoom.scx`, `trafico_guardia_servicio.scx`, `trafico_resumen_horario.scx`
(banda horaria), `chofer_franco_abm.scx`.
