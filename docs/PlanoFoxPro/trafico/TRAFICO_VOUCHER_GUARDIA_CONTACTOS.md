# Tráfico — Voucher Recepción · Guardia · Contactos y Proveedores · Lista de pasajeros

> Los 4 ítems restantes del menú **Tráfico** del FoxPro, migrados a Buslink el **07/07/2026**
> en **solo lectura + andamiaje ABM** (patrón Fleteros/TipoVehiculo). Forms extraídos con
> `foxpro-extract`. Permiso del módulo: **`'T'`**.

## Ubicación en el menú FoxPro (`MENU_PRINCIPAL.MPR`, popup `trafico`)

| BAR | Ítem | Form | Tabla(s) |
| --- | --- | --- | --- |
| 10 | Voucher Recepción | `trafico_voucher` | `viaje` (`voucher_nr`, `voucher_re`) |
| 12 | Guardia | `trafico_guardia` + `trafico_guardia_abm` | `viaje_guardia` |
| 14 → popup `contactosy` | Contactos y Proveedores | — | — |
| · BAR 1 | Contactos | `estacion` + `estacion_abm` | `estacion` |
| · BAR 3 | Rubros de contactos | `estacion_rubro` + `estacion_rubro_abm` | `estacion_rubro` |
| 16 | Lista de pasajeros | `trafico_pasajero_planilla` | `viaje_pasajero(_detalle)` |

**Estado de replicación (server activo 172.25.69.217):** ✅ `viaje_guardia` (5), `estacion` (178),
`estacion_rubro` (8) están replicadas (a diferencia de las tablas de Francos/Viáticos).

---

## 1. Voucher Recepción (`trafico_voucher.scx`)

**Qué es:** auditoría del voucher (comprobante de recepción que firma el pasajero). NO tiene tabla
propia — lee y escribe sobre `viaje`. Blazor: `VoucherRecepcion.razor` (`/voucher-recepcion`).

**3 modos de consulta** (optiongroup del form):
1. **Rango de voucher** — `voucher_nr BETWEEN d AND h`.
2. **Rango de fechas** — `voucher_re BETWEEN d AND h` (fecha de recepción).
3. **Sin recepcionar** — `voucher_re IS NULL AND voucher_nr > 0` (demanda pendiente de firma).

**Grilla (11 col):** `voucher_nr`, `voucher_re`, `id_viaje`, `f_reserva`, `hs_s_inici` (hora),
`interno`, destino (`d_destino + ' a ' + h_destino`), `id_chofer`, tipo veh (`LEFT(id_vehicul,4)`),
`id_cliente`, `comentario`.

**Escritura (andamiaje):** los botones "1º Viaje" / "Todos los Viajes" / "Limpia recep" hacen
`UPDATE viaje SET voucher_re = fecha`. Como toca `viaje`, se enciende el **DÍA D** con el circuito.
`AbmService.MarcarRecepcionAsync` / `MarcarRecepcionLoteAsync` (WHERE por `id_viaje` + `f_reserva`,
regla de perf: no hay índice por `id_viaje`). Flag: `VoucherRecepcionActivo` (false).

**Datos reales:** 120 viajes con voucher, todos sin `voucher_re` en la réplica (el circuito de
recepción se usa poco / la réplica no trae la fecha).

---

## 2. Guardia (`trafico_guardia.scx` + `trafico_guardia_abm.scx`)

**Qué es:** ABM clásico de dos forms sobre `viaje_guardia` (registro de guardias de choferes/unidades).
Blazor: `Guardias.razor` (`/guardias`) + `GuardiaEditorDialog.razor`.

**Tabla `viaje_guardia`:** `id` (PK, no identity), `interno`, `id_vehicul` (dominio), `id_chofer`,
`nombre_cho`, `franco` (bit), `fecha` (date), `hs_inicio`/`hs_fin` (datetime), `fpago` (date).

**Reglas del `_abm`:**
- **Alta:** INSERT completo. Valida veh + chofer + fechas obligatorios, `hs_inicio < hs_fin`.
- **Baja FÍSICA:** `DELETE FROM viaje_guardia WHERE id = X` (no hay `f_delete`).
- **Modifica:** bloqueado si `fpago` cargado (guardia ya pagada). `fpago` NO se edita acá — lo
  escribe la Liquidación de choferes (en el form está `Enabled = .F.`).

**Trampas:** columnas truncadas `id_vehicul` (no id_vehiculo), `nombre_cho` (no nombre_chofer).
🐛 Bug del fuente: `Wher Id = nId` (typo, "Where" mal escrito en el UPDATE) — no copiar.

**Datos reales:** 5 guardias, todas de **2006-2008** (funcionalidad casi sin uso hoy). El default
de la pantalla arranca con rango amplio (20 años) para mostrar las que existan.
Flag: `GuardiaAbmActivo` (false).

---

## 3. Contactos y Proveedores (`estacion.scx` + `estacion_abm.scx`)

**Qué es:** ⚠️ `estacion` es el catálogo de **PROVEEDORES de toda la empresa** (178 filas: estaciones
de servicio, gomerías, grúas, fleteros, cristales, audio, clientes…), **COMPARTIDO con el módulo
Combustible** (ahí se abre como "estaciones de servicio"). Blazor: `Contactos.razor` (`/contactos`)
+ `ContactoEditorDialog.razor`.

**Filtros del form:** combo Rubro (con "TODOS LOS RUBROS") + combo Campo (Razón Social / Dirección /
Localidad / Teléfono) + texto de búsqueda incremental.

**Grilla (11 col):** `id`, rubro (nombre vía JOIN), `nombre`, `domicilio`, `localidad`, `provincia`,
`telefono`, `celular`, `radio`, `contacto1`, `contacto2`.

**Ficha (`estacion_abm`):** razón social, rubro (combo desde `estacion_rubro`), domicilio, localidad,
provincia, teléfono, celular, radio, email (valida `@`), contacto1/2, medio de pago (CUENTA CORRIENTE
/ TARJETA PREPAGO / TARJETA DE CREDITO / EFECTIVO / AUDITOR / OTRA), + flags legacy de Combustible
(Controla Saldos, Ult. Lote, YPF En Ruta, Esso Card, Cta. Cte., Cairo Código/IIBB).

**Reglas del `_abm`:**
- **Alta:** valida nombre + rubro obligatorios, no duplicar (nombre + rubro). id = MAX(id)+1.
- **Baja FÍSICA:** `DELETE FROM estacion WHERE id = X`.
- **Modifica:** UPDATE completo.

**Trampas:** truncados `control_sa` (no control_saldo), `cairo_codi` (no cairo_codigo). `rubro` es
**bigint** (FK a `estacion_rubro.id`). Hay una columna `codigo` extra que el form NO muestra.
⚠️ Al activar: **coordinar dueño único con Combustible** (como Fleteros con Facturación).
Flag: `ContactosAbmActivo` (false).

### 3b. Rubros de contactos (`estacion_rubro.scx` + `_abm.scx`)

Catálogo simple: `id` + `rubro` (nombre) + `audita` (bit — activa validaciones extra en la carga de
combustible, hoy apagado en casi todos). 8 filas. Blazor: `RubrosContacto.razor` (`/rubros-contacto`),
reusa `CatalogoSimpleEditorDialog` (extendido con el flag `audita` opcional). Baja física.
Flag: `RubrosContactoAbmActivo` (false).

---

## 4. Lista de pasajeros (`trafico_pasajero_planilla.scx`)

**Qué es:** la planilla CNRT de pasajeros de un viaje. **Ya estaba migrada** como dialog contextual
(`ListaPasajerosDialog.razor`, se abre desde una fila de la Planilla de Tráfico). El ítem del menú
suelto abre la misma planilla eligiendo un viaje.

**Blazor:** `ListaPasajeros.razor` (`/lista-pasajeros`) = **página buscadora**: elegís una fecha
(default hoy) + texto (interno / servicio / cliente / destino) → grilla de viajes de ese día → click
en "Pasajeros" abre el `ListaPasajerosDialog` existente. Sin escritura (el dialog es solo lectura).
Método nuevo: `GetViajesParaBuscadorAsync`. Tablas `viaje_pasajero`/`_detalle` casi vacías.

**🐛 Trampa CRÍTICA (corregida):** `viaje.interno` es **bigint** en la réplica → leerlo con
`GetInt32` tira `InvalidCastException`. Se castea en SQL con `CAST(ISNULL(v.interno,0) AS int)`
(los internos caben en int). El MISMO fix aplica a la query de Voucher.

---

## Resumen de piezas Blazor

| Pieza | Archivo |
| --- | --- |
| Lectura | `ReportService`: `GetVoucherAuditoriaAsync`, `GetGuardiasAsync`/`GetGuardiaRowAsync`, `GetContactosListaAsync`/`GetContactoRowAsync`, `GetRubrosContactoAsync`, `GetViajesParaBuscadorAsync` |
| Escritura (andamiaje) | `AbmService`: `*GuardiaAsync`, `*ContactoAsync`, `*RubroContactoAsync`, `MarcarRecepcion*Async`. Flags en `AbmFeatureFlags` (todos false) |
| Páginas | `VoucherRecepcion` · `Guardias` · `Contactos` · `RubrosContacto` · `ListaPasajeros` |
| Editores | `GuardiaEditorDialog` · `ContactoEditorDialog` · `CatalogoSimpleEditorDialog` (Rubros, +audita) |
| Excel | `ExcelExportService`: `Voucher`, `Guardias`, `Contactos`, `RubrosContacto` |
| Menú | `MainLayout.razor` — los 4 placeholders pasan a links reales |

**Validado (07/07/2026):** 25/25 smoke tests verdes; capturas a 1280×720 de las 5 pantallas + 3
editores; conteos al dígito (5 / 178 / 8 / 120).
