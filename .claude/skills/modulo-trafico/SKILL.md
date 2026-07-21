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
| `chofer_franco` | francos por chofer y fecha (71k filas) | `codigo`, `fecha`, `motivo`, `trabajo`. **Baja física**. ✅ solo lectura + andamiaje ABM (`/francos`) |
| `cabecera` | catálogo de cabeceras/recorridos (187 filas) | `codigo`, `nombre`, `nombre1`, `nombre2`, `recorrido`. **Baja física**. ✅ solo lectura + andamiaje ABM (`/cabeceras-recorridos`) |
| `chofer_viatico` + `_motivo`/`_liquida` | viáticos de conductores (VACÍAS, sin uso) | `id_motivo`/`id_liquida` bigint. **Baja física**. ✅ solo lectura + andamiaje ABM (`/viaticos`) |
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

### Filtro por estado (leyenda del pie clickeable, 20/07/2026)

La barra "Estados:" del pie de la grilla dejó de ser una referencia de colores pasiva:
**cada estado es un botón que filtra la grilla** (pedido del usuario, mismo espíritu que los
botones Empresa/Turismo/Nortur de arriba). Reglas ya decididas — no re-litigarlas:

- **Se filtra por `EstadoDisplay`, NUNCA por `estado_via` crudo.** Es la regla de oro acá: el
  botón tiene que filtrar lo que el COLOR está diciendo. Con el estado crudo, apretar ASIGNADO
  traería también las EN CURSO (que en la base son ASIGNADO) y SIN ASIGNAR traería las CHEQUEO.
- **Semántica: el clic SUMA/QUITA — los estados se ACUMULAN, sin Ctrl.** Desde "sin filtro",
  el 1er clic deja solo ese estado; el 2º clic en otro botón suma el segundo (FACTURADO +
  FINALIZADO juntos); reclic en uno activo lo saca; sacar el último = ver todo. Hay un botón
  **"Ver todos"** para limpiar de un golpe y un hint *"clic para sumar o quitar estados"* al
  lado de los botones cuando no hay filtro.
  > Se probó primero con **foco exclusivo + Ctrl+clic para sumar** y se descartó el mismo día
  > (20/07/2026): el usuario no descubrió el Ctrl y pidió explícitamente poder elegir dos
  > estados. Lección: en esta pantalla, **nada que dependa de una tecla modificadora se
  > descubre**. El costo aceptado es que saltar de un estado a otro son 2 clics.
- **6 botones**, no 5: se agregó **SIN ASIGNAR** (fila blanca). Como leyenda de colores no
  hacía falta; como filtro es el estado más operativo del día.
- **Contadores** calculados sobre el conjunto filtrado por TODO menos el estado
  (`baseSinEstado` en `RecalcularVisibles`) → **sin filtro de estado puesto**, la suma de los 6
  botones da exacto el total de la grilla; con estados elegidos, la grilla muestra la suma de
  los elegidos (los contadores NO cambian: siguen siendo "cuántos hay de cada uno"). Hay un
  smoke test que verifica la invariante. Verificado con el buscador "EZEIZA" en 06/05/2026:
  contadores 0+0+0+1+135+2 = 138 servicios que matchean; con FACTURADO+FINALIZADO = 136.
- El **KPI "Sin asignar"** también usa `EstadoDisplay` para que coincida con su botón (las
  chequeadas se cuentan en el botón CHEQUEO, que es como se ven en la grilla).
- Un estado con **0 servicios se deshabilita** (filtrarlo solo daría grilla vacía), salvo que
  esté enfocado (para poder sacarle el foco).
- El **chip de la columna Estado de cada fila también filtra** (clic = ver solo ese estado),
  con `stopPropagation` en click y dblclick para no disparar además la selección de fila ni el
  Zoom. El resto de la fila conserva intacto doble clic → Zoom y clic derecho → menú.
- El foco **persiste** al cambiar de día y al auto-refresh de 60s (como Emp/Tur/Nortur). Por
  eso hay un chip "Filtrado por estado: X ✕" arriba, al lado de los KPIs: si no, se puede
  mirar una grilla filtrada creyendo que es el día completo.
- Todo **en memoria** sobre el día ya cargado: cero SQL, cero re-query. Convive sin conflicto
  con el filtro server-side de "Aplicar Filtros" (ese trae el rango; este refina lo cargado).

## Qué ya está migrado (NO rehacer)

| Pieza | Dónde |
| --- | --- |
| Planilla del día (grilla 25 col, colores, doble-click zoom) | `Components/Pages/PlanillaTrafico.razor` |
| Combos U/Pr / U/Cb + S/C + Emp/Tur/Nortur + buscador | ídem (filtros en memoria) |
| **Filtro por ESTADO desde la leyenda del pie** (clic = solo ese estado, Ctrl+clic = sumar) | ídem (`_estadosFoco`, en memoria) — ver § Filtro por estado |
| Vista cancelados (Cxl) con motivo | ídem + `GetTraficoCanceladosAsync` |
| **Panel Buses** (grid2: flota viva, franco, colores) | ídem + `GetPanelBusesAsync` |
| Zoom del Viaje (solo lectura) | `Components/Shared/ZoomViajeDialog.razor` |
| **Historial del viaje** (bitácora `viaje_log` + auditoría, solo lectura) | `Components/Shared/HistorialViajeDialog.razor` (+ `TextoZoomDialog.razor` = el "Zoon Motivo") · doc: `docs/PlanoFoxPro/trafico/TRAFICO_HISTORIAL.md` |
| **Novedad sobre el viaje** (libro de novedades del viaje, solo lectura) | `Components/Shared/NovedadViajeDialog.razor` · `GetNovedadesViajeAsync` · tabla `libro_novedad` · doc: `TRAFICO2_FILTROS.md` |
| **Lista de pasajeros** (planilla CNRT del viaje, solo lectura) | `Components/Shared/ListaPasajerosDialog.razor` · `GetPasajerosViajeAsync` · tablas `viaje_pasajero`/`viaje_pasajero_detalle` · doc: `TRAFICO2_FILTROS.md` |
| **Menú contextual completo "Ver Datos Extras"** (los 7 ítems del popup `verdatosex`) | ver tabla abajo |
| Export Excel (planilla, cancelados, historial) | `ExcelExportService` |
| **Auto-refresh inteligente 60s** (token de versión + flash de cambios) | `PlanillaTrafico.razor` + `GetTraficoVersionAsync` |
| Grilla estilo "Ops Densa" (barra de estado + tinte, paleta desaturada) | clases `fila-estado--*` en `app.css` |
| **Cabeceras - Recorridos** (solo lectura + andamiaje ABM) | `CabecerasRecorridos.razor` (`/cabeceras-recorridos`) + `CabeceraEditorDialog` · doc: `trafico/CABECERA_RECORRIDO.md` |
| **Francos** (mantenimiento + ingreso masivo + auditoría) | `Francos.razor` (`/francos`) · `FrancosIngreso.razor` + `FrancoAltaDialog` · `FrancosAuditoria.razor` (informe matriz chofer×día) · doc: `trafico/CHOFER_FRANCO.md` |
| **Viáticos** (grilla + 2 catálogos, solo lectura + andamiaje ABM) | `Viaticos.razor` + `ViaticoEditorDialog` · `ViaticosMotivo`/`ViaticosFormaLiquidacion` + `CatalogoSimpleEditorDialog` · doc: `trafico/CHOFER_VIATICO.md` |
| **Voucher Recepción** (auditoría de vouchers, 3 modos) | `VoucherRecepcion.razor` (`/voucher-recepcion`) · escritura sobre `viaje` (`voucher_re`) → día D · doc: `trafico/TRAFICO_VOUCHER_GUARDIA_CONTACTOS.md` |
| **Guardia** (ABM `viaje_guardia`, solo lectura + andamiaje) | `Guardias.razor` (`/guardias`) + `GuardiaEditorDialog` · baja física · doc: ídem |
| **Contactos y Proveedores** (`estacion` + `estacion_rubro`) | `Contactos.razor` (`/contactos`) + `ContactoEditorDialog` · `RubrosContacto.razor` (`/rubros-contacto`) · **compartido con Combustible** · baja física · doc: ídem |
| **Lista de pasajeros** (buscador de viaje → dialog existente) | `ListaPasajeros.razor` (`/lista-pasajeros`) reusa `ListaPasajerosDialog` · `GetViajesParaBuscadorAsync` |

> **07/07/2026 — Voucher · Guardia · Contactos · Lista de pasajeros** migrados solo lectura +
> andamiaje ABM: los 4 ítems restantes del menú Tráfico (ya no quedan placeholders). Flags nuevos
> `GuardiaAbmActivo`, `ContactosAbmActivo`, `RubrosContactoAbmActivo`, `VoucherRecepcionActivo` (todos
> false). Las 3 tablas nuevas (`viaje_guardia`, `estacion`, `estacion_rubro`) **SÍ están replicadas**
> en el server activo. 🐛 **Bug corregido:** `viaje.interno` es bigint → `CAST(...AS int)` (Voucher +
> buscador de Lista de pasajeros). `estacion` compartida con Combustible → coordinar dueño al activar.
> Guardia: default de rango amplio (datos históricos 2006-2008). Doc: `trafico/TRAFICO_VOUCHER_GUARDIA_CONTACTOS.md`.

> **05/07/2026 — Cabeceras · Francos · Viáticos** migrados solo lectura + andamiaje ABM (patrón
> Fleteros/TipoVehiculo, flags en `AbmFeatureFlags` en `false`). **🐛 Baja FÍSICA** en las 5 tablas
> (`cabecera`, `chofer_franco`, `chofer_viatico`, `_motivo`, `_liquida`) — no tienen `f_delete`.
> **⚠️ Esas 5 tablas están en el server VIEJO pero NO en el nuevo (172.25.69.217)** → replicar antes
> del día D. Métodos nuevos: `GetCabecerasAsync`, `GetFrancosAsync`, `GetFrancoMotivosAsync`,
> `GetFrancoAuditoriaAsync`, `GetViaticosAsync`, `GetViaticoMotivosAsync`, `GetViaticoLiquidaAsync`,
> `GetChoferesComboAsync`. Escritura en `AbmService` (Cabecera, `AltaFrancosAsync` masivo + `BajaFrancoAsync`,
> Viático, catálogos). Auditoría validada jun 2026 = 98 choferes; Cabeceras = 187.

Queries del módulo en `ReportService.cs`: `GetPlanillaTraficoAsync`,
`GetTraficoCanceladosAsync`, `GetCombosUnidadesTraficoAsync`, `GetPanelBusesAsync`,
`GetTraficoVersionAsync` (liviana, sin caché) + `InvalidarCacheTrafico`,
`GetHistorialViajeAsync` (bitácora `viaje_log` + auditoría de `viaje`, sin caché),
`GetNovedadesViajeAsync` (libro de novedades del viaje, `libro_novedad`),
`GetPasajerosViajeAsync` (planilla CNRT, `viaje_pasajero` + `viaje_pasajero_detalle`),
`GetOperadorDetalleAsync`, `GetRecorridoCabeceraAsync`, `GetAdicionalesViajeAsync`,
`GetRutaAdjuntoViajeAsync` (Ver Datos Extras).

### Submenú "Ver Datos Extras" (popup `verdatosex` de `menu_viaje_reserva.mnx`) — los 7 ítems migrados (jun 2026)

Click derecho sobre una fila → "Ver Datos Extras". Cada ítem abre la ficha de solo lectura
de la entidad de la fila (todos reusan diálogos o el patrón de ficha del proyecto). Se
deshabilitan si la fila no tiene la clave. **Verificado contra `cursorViajeReserva` = `SELECT *
FROM viaje`: todas las claves son columnas de `viaje`.**

| Ítem | Form FoxPro origen | Clave (`viaje`) | Blazor | Tabla destino |
| --- | --- | --- | --- | --- |
| Ver Datos **Operador** | `cliente_operador_abm` (consulta) | `id_operado` | `OperadorDetalleDialog.razor` | `cliente_operador` por `id_operado` |
| Ver Datos **Vehículo** | `vehiculo_abm` (consulta) | `id_vehicu2` (=dominio) | `VehiculoDetalleDialog.razor` | `vehiculo` |
| Ver Datos **Chofer** | `chofer_abm` (consulta) | `id_chofer` | `ChoferDetalleDialog.razor` | `chofer` |
| Ver Datos **Cliente** | `cliente_abm` (consulta) | `id_cliente` | `ClienteDetalleDialog.razor` | `cliente` |
| Ver **Adjunto** | `Shell.ShellExecute(viaje.file)` | `file` | endpoint `/adjunto/{id}?f=fecha` + `AdjuntoService` | archivo físico (ver abajo) |
| Ver **Recorrido** | `cabecera_recorrido_abm_zoom` (consulta) | `gps_cod` | `RecorridoCabeceraDialog.razor` | `cabecera` por `codigo` (texto del circuito) |
| Ver **Adicionales** | `trafico_zoom_adicional` (consulta, `WITH id_viaje, .t.`) | `id_viaje` | `ViajeAdicionalesDialog.razor` | `viaje_adicional` por `id_viaje` |

**Trampas resueltas (jun 2026):**
- **`viaje.id_operado` (truncado de `id_operador`) → `cliente_operador`, NO `cliente`.** Verificado:
  de 1461 viajes con operador, los **1461 matchean** en `cliente_operador.id_operado`; solo 17
  matchean por casualidad en `cliente.id_cliente`. El "operador" es un **contacto/persona dentro
  de un cliente** (agencia), no el operador turístico. **El Zoom del Viaje tenía este bug** (hacía
  `id_operado → cliente.razon_soci`) — corregido a `cliente_operador.nombre` al migrar este lote.
- **`viaje_adicional.id_adicion`** (truncado de `id_adicional`) = la columna "Código" de la grilla
  de adicionales. `viaje_adicional` cols: `id, id_viaje, id_adicion, nombre, precio, cantidad`.
- **`cabecera`** cols: `codigo, nombre, nombre1, nombre2, recorrido`. `viaje.gps_cod` = `cabecera.codigo`.
  El FoxPro mostraba solo `recorrido` (editbox rojo); en Blazor se agrega código+nombres de contexto.
- **Adjunto = archivo de red.** `viaje.file` guarda rutas tipo `O:\METROCARSYS\ADJUNTOS\x.pdf`
  (unidad mapeada del FoxPro). El servidor de Blazor NO ve `O:` → `AdjuntoService` reemplaza el
  prefijo (`Adjuntos:PrefijoFoxPro`) por la UNC real (`Adjuntos:BasePath` en `appsettings.json`,
  **a completar con la UNC del recurso compartido**). El endpoint `/adjunto/{id}` exige sesión
  (401 sin cookie), valida contención bajo `BasePath` (anti path-traversal) y sirve inline
  (PDF/imagen se ven; el resto descarga). Solo 329 viajes tienen adjunto. Mientras `BasePath`
  esté vacío, el ítem avisa que falta configurar la ruta.

> **`viaje_log` SÍ tiene índice por `id_viaje`** (`IX_viaje_log_idviaje`) — a diferencia de
> `viaje`. Por eso el Historial filtra por `id_viaje` directo (seek barato pese a 4,4M filas).
> Columnas truncadas de `viaje_log`: `cronograma_new`→`cronogram2`, `interno_ori`→`interno_or`,
> `interno_new`→`interno_ne`.

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

- `docs/PlanoFoxPro/trafico/TRAFICO2_FILTROS.md` — toolbar completa de `trafico2.scx`: combos,
  S/C, Cxl, **panel Buses (arma_grid_vehiculo)**, post-procesamiento del cursor.
- `docs/PlanoFoxPro/trafico/TRAFICO_ZOOM.md` — Zoom del Viaje: máquina de estados, validaciones,
  tablas tocadas (Modificar/Cancelar/SinAsignar/Duplicar/ValorServicio).
- `docs/PlanoFoxPro/trafico/TRAFICO_HISTORIAL.md` — bitácora `viaje_log` (9 columnas, truncados).
- **`docs/PlanoFoxPro/trafico/TRAFICO2_TOOLBAR.md`** (02/07/2026) — **la especificación de ESCRITURA
  de la toolbar**: Chequeo, Asig U/P, Otra Unidad, Reas, Libe, Frc — SQL exacto, validaciones,
  forms `trafico_asigna`/`trafico_reasigna`/`trafico_liberar`/`chofer_franco*`. **Leer ANTES
  de codear cualquier operación de despacho.**
- **`docs/PlanoFoxPro/trafico/GPS_XLM.md`** (02/07/2026) — la integración GPS (`gps_xlm()` en
  `procesos.prg`, llamada en ASIGNO/RE-ASIGNO/FINALIZO/CANCELO/armar plantillas): 2 vías
  (XML file-drop + SQL Server externo, tabla `Servicios`). **Hoy es NO-OP** —
  `parametro.xml_envia = 0` y `sql_gps = 0` (verificado 02/07/2026); decisión final del
  dueño pendiente (Fase 0.2). El motivo de Reasignar es el catálogo
  `docs/PlanoFoxPro/catalogos/VIAJE_MOTIVO_CAMBIO_ABM.md`.

## ESCRITURA — el circuito de despacho (extraído 02/07/2026, listo para migrar)

El "ABM de Tráfico" es un **workflow de asignación, NO un CRUD**. La especificación completa
con SQL exacto está en `TRAFICO2_TOOLBAR.md` + `TRAFICO_ZOOM.md`; la matriz consolidada
operación → tablas → campos → log en **`references/ESCRITURA_CIRCUITO.md`** de esta skill.
El roadmap de cuándo/cómo migrar: `docs/buslink/PLAN_MIGRACION_BUSLINK.md` (Fase 3).

### Alcance día 1 (confirmado por el usuario, 02/07/2026)

Asignar/cambiar interno y chofer · estados del viaje · cancelar con motivo · Zoom del Viaje
en edición completa. Orden interno de construcción (por riesgo/valor, ver plan): 1-Chequeo →
2-Asignar → 3-Liberar → 4-Reasignar → 5-Finalizar → 6-Cancelar → 7-Reactivar → 8-Franco →
9-Zoom edición → 10-Duplicar/valor servicio.

### Descubrimientos de la extracción (no obvios — no violar)

1. **`gps_xlm(id_viaje)` se llama en ASIGNO, RE-ASIGNO, FINALIZO y CANCELO** — cada cambio
   operativo notifica al GPS. Resolver la "decisión GPS" (Fase 0 del plan) antes de codear.
2. **"Libe" (toolbar) = FINALIZAR** el viaje (hs_fin + duración + pax real + voucher +
   odómetros + km) y liberar la unidad **con zona nueva** (`vehiculo.id_zona`). Volver a
   SIN ASIGNAR es OTRO botón (Zoom → "Sin Asignar"). Solo se libera una unidad EN CURSO.
3. **Asignar también escribe `vehiculo_km`** (primer odómetro del mes → INSERT + cierra
   km_fin del mes anterior) **y puede escribir `chofer_franco`** (sub-flujo franco trabajado:
   "Cbia. Franco" mueve la fecha / "Trabaja Franco" marca `codigo='FT'`).
4. **Anti-doble-asignación optimista**: relee el viaje antes del UPDATE — si `id_chofer` ya
   está cargado → "ya fue asignado por otro usuario". En Buslink: `UPDLOCK` en la transacción.
5. **Reasignar resetea `chequeo = 0`** y loguea motivo **'RE-ASIGNO'** (interno Y cronograma
   ori→new en columnas dedicadas). La unidad viaje: nueva → ASIGNADO, vieja → LIBERADO.
6. **Rutas (`id_viaje_int > 0`)**: toda operación pega a TODOS los tramos y loguea/gps por
   tramo; asignar exige tramos anteriores FINALIZADOS; liberar solo desde el último tramo;
   tramos intermedios finalizan `hs_fin=23:59`.
7. El **Cxl de la toolbar es solo un filtro** — cancelar vive en el Zoom (con motivo de
   `viaje_motivo_cancela` + cascada DELETE de `cliente_grupo` si todo el grupo quedó cancelado).
8. Al finalizar se generan **adicionales con precio** en `viaje_adicional` (agua =
   `parametro.adic_agua` × `viaje.agua`, horas extra con motivo obligatorio, stock).
9. **FoxPro no usa transacciones en nada de esto** — Buslink DEBE envolver viaje+vehiculo+log
   en una transacción única (mejora obligatoria, patrón `AbmService`).
10. Estados de `vehiculo.estado`: ASIGNADO / LIBERADO / TALLER / GUARDIA (+ CURSO display);
    `vehiculo.trabaja` ≠ vacío = chofer de franco/licencia (bloquea asignación).

### Reglas de la regla-madre (abm-metrocar)

`viaje`/`vehiculo`/`viaje_log`/`cliente_grupo` siguen siendo de FoxPro **hasta el día D**
(plan Buslink): las pantallas de escritura se construyen y prueban contra el server LOCAL
detrás del feature flag `EscrituraViaje`, y cambian de dueño todas juntas en el corte.
Toda escritura: `SqlParameter` + transacción + WHERE con `f_reserva` además de `id_viaje`
(no hay índice por `id_viaje`).

## Forms FoxPro del módulo

`trafico2.scx` (productivo — el menú abre este), `trafico3.scx` (copia), `trafico.scx`
(viejo), `trafico_zoom.scx`, **`trafico_asigna.scx`** (asignación, modos SIN/CON),
**`trafico_reasigna.scx`**, **`trafico_liberar.scx`** (finalización + hora adicional),
`trafico_liberar_hora_adicional.scx`, `trafico_guardia_servicio.scx`,
`trafico_resumen_horario.scx` (banda horaria), `chofer_franco.scx` (lista/baja),
`chofer_franco_abm.scx` (alta masiva), `chofer_franco_modifica.scx`,
`chofer_franco_auditoria.scx`.
