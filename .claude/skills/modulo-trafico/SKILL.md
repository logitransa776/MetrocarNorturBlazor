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
