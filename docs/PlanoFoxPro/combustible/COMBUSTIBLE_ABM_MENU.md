# Combustible — mapeo del menú a Blazor (1ª entrega)

> Migración de los 4 ítems del menú **Combustible** pedidos el 07/07/2026, en **solo lectura +
> andamiaje ABM** (patrón Fleteros/Contactos). El relevamiento FoxPro completo está en
> [`COMBUSTIBLE.md`](COMBUSTIBLE.md); acá solo el mapeo pantalla→Blazor y las trampas de columnas
> verificadas contra la base real (`replicaVPF`, server activo `DESKTOP-CV6LF0O\SQLEXPRESS`).

Permiso del módulo: letra **`M`** en `usuario.acceso` (Combustible — no confundir con `C` = avisos
de chequeo). El drawer ya tenía la sección Combustible con placeholders `--disabled`; esta entrega
los convierte en links reales y quita `nav-section--soon`.

---

## Ítems migrados

| # | Ítem del menú | Página (ruta) | Form FoxPro | Tipo | Estado |
| --- | --- | --- | --- | --- | --- |
| 1 | Promedio de Consumos | `PromedioConsumos.razor` (`/promedio-consumos`) | `vehiculo_combustible_consumo` | Informe analítico | ✅ |
| 2 | ABM y Conciliación cargas | `CombustibleConciliacion.razor` (`/combustible-conciliacion`) + `CargaCombustibleEditorDialog` | `vehiculo_combustible_mant_sobre_lote` | Grilla + conciliación (andamiaje) | ✅ |
| 3 | Saldos de Estaciones | `SaldosEstaciones.razor` (`/saldos-estaciones`) | `vehiculo_estacion_saldo` | Informe histórico | ✅ |
| 4a | Depósitos → Carga de Depósitos | `DepositosEstacion.razor` (`/depositos-estacion`) + `DepositoEstacionEditorDialog` | `vehiculo_estacion_saldo_carga` | ABM (andamiaje) | ✅ |
| 4b | Depósitos → Mantenimiento de Depósitos | `DepositosMantenimiento.razor` (`/depositos-mantenimiento`) | `vehiculo_estacion_saldo_mant` | Grilla + baja (andamiaje) | ✅ |

Los catálogos 8/9/10 del menú FoxPro (Estaciones / Rubro de Consumos / Artículos por Rubro) **no**
entran en esta entrega: "Estaciones" y "Rubro de Consumos" son la MISMA tabla que ya migró el
módulo Tráfico (`Contactos.razor` sobre `estacion`, `RubrosContacto.razor` sobre `estacion_rubro`).
Se linkean/reusan más adelante para no duplicar.

### 2ª entrega (07/07/2026) — Control de cargas + Consumo mensual

| Ítem | Página (ruta) | Form FoxPro | Tipo |
| --- | --- | --- | --- |
| Control de cargas | `ControlCargas.razor` (`/control-cargas`) | `trafico_vehiculo_combustible` | Informe (días sin cargar) |
| Consumo Mensual | `ConsumoMensual.razor` (`/consumo-mensual`) | — (nuevo, no existe en FoxPro) | Informe analítico (litros/mes) |

- **Control de cargas**: réplica de `trafico_vehiculo_combustible`. Por unidad PROPIA activa con al
  menos una carga: última carga, días desde entonces (`DATEDIFF` a hoy), odómetro de esa carga.
  Check "solo unidades atrasadas" con umbral (= el "Filtra por Vehículos Sin Carga" del FoxPro).
  KPIs (al día / por revisar / atrasadas), filas rojas ≥15 días, Excel. `GetControlCargasAsync`.
  Link `vehiculo.id_vehicul = vehiculo_sobre.dominio` (patente). **Ojo:** con la réplica congelada al
  08/06/2026, hoy todas salen "atrasada" (los datos de julio no se replicaron aún) — es real, no bug.
- **Consumo Mensual**: informe NUEVO (no existe en FoxPro). Litros por **mes × unidad × estación ×
  tipo** — la métrica es **litros, NO costo** (el importe viene 0 con tarjeta prepaga; confirmado:
  0 importe en 2020-2026 salvo 2 cargas). KPIs, barras litros/mes, donut por tipo (cross-filter 1D),
  pivote mes × unidad, Excel (pivote + detalle). `GetConsumoMensualAsync` (agrega con
  `CONVERT(char(7), f_carga, 120)` = SQL 2012-friendly). Validado: 529.096 litros 2026.

### 3ª entrega (07/07/2026) — los 3 catálogos del menú (Estaciones · Rubros · Artículos)

**Hallazgo (verificado en `MENU_PRINCIPAL.MPR` líneas 427-429):** los 3 ítems del pad Combustible
abren EXACTAMENTE los mismos forms FoxPro que otros menús — NO filtran por rubro:

| Ítem menú Combustible | Form FoxPro (BAR) | Ya migrado como |
| --- | --- | --- |
| Estaciones (BAR 8) | `estacion` | **`Contactos.razor`** (`/contactos`) — catálogo completo de 178 proveedores |
| Rubro de Consumos (BAR 9) | `estacion_rubro` | **`RubrosContacto.razor`** (`/rubros-contacto`) |
| Artículos por Rubro de Consumo (BAR 10) | `estacion_rubro_articulo` | **`ArticulosRubro.razor`** (`/articulos-rubro`) — NUEVO |

Decisión (validada con el usuario, 07/07/2026): **reutilizar** los 2 primeros (mismo form del FoxPro →
una sola pantalla mantenida, DRY); crear solo el 3º. Los 3 links del menú Combustible apuntan
respectivamente a `/contactos`, `/rubros-contacto` y `/articulos-rubro`.

**Artículos por Rubro de Consumo** (`estacion_rubro_articulo.scx` + `_abm.scx`, dumpeados 07/07/2026):
catálogo de los "artículos" de cada rubro. Para rubro 1 (combustible) son los **tipos de combustible**
del combo de la carga: DIESEL 500 / EURO-DIESEL (2 filas, `nombre` truncado a 30 → "EURO-DIESE").
Columnas: `id` (int, **no identity** → alta `MAX(id)+1`), `idrubro` (bigint, FK a `estacion_rubro`),
`nombre` (nvarchar 30). **Sin `f_delete` → baja FÍSICA** (DELETE, como toda la familia `estacion`).
Lógica del `_abm` (verificada): alta valida nombre y rubro no vacíos + `INSERT (idRubro, nombre)`;
modifica `UPDATE SET idRubro, nombre WHERE id`; baja `DELETE WHERE id` (con confirmación). El combo
de rubro sale de `SELECT rubro, id FROM estacion_rubro`. Migrado en **solo lectura + andamiaje**
(`ArticulosRubroAbmActivo=false`). `GetArticulosRubroAsync`/`GetArticuloRubroRowAsync`;
`Alta/Modifica/BajaArticuloRubroAsync`; export `ArticulosRubro`.

---

## Columnas reales verificadas (⚠ trampas de truncado y tipo)

### `vehiculo_sobre` (109.622 filas sanas, 2010→08/06/2026)

Truncados y tipos que rompen si se asumen mal:

- **`estacion_n`** = `estacion_nombre` truncado a 30 (nombre desnormalizado de la estación).
- **`idrubro`** (no `idRubro`), FK a `estacion_rubro.id`.
- **bigint**: `interno`, `odometro`, `n_sobre`, `estacion`, `n_factura`, `n_remito`
  → leer con `GetInt64` o `CAST(... AS int)` en el SELECT. `id` sí es `int`.
- **decimal**: `litros`, `p_x_ltr` (derivado = importe/litros al grabar), `importe`.
- **bit**: `lleno`, `dos_carga`.
- Dos fechas: **`f_carga`** (la operativa, la que se filtra) y `fecha` (redundante).
- `hora` = `nvarchar(5)` "HH:mm"; `f_pago` = `nvarchar(30)` texto libre; `chofer` `nvarchar(15)`.
- Auditoría de negocio: `u_create/f_create/u_modify/f_modify` (la era 2 sí la tiene).
- **Filtrar siempre `f_carga BETWEEN '2009-01-01' AND '2027-12-31'`** — hay años corruptos.
- **`n_sobre = 0` (o NULL) = carga SIN CONCILIAR** (31.515 hoy). ≠ 0 = ya en un lote (fila amarilla).

### `vehiculo_estacion_saldo` (787 filas, TODAS 2013-2017 — circuito sin uso)

- `id` int (PK). `estacion` bigint (FK a `estacion.id`), **`estacion_n`** = nombre truncado a 30.
- `importe` decimal — **egreso/consumo = importe NEGATIVO** (el alta lo graba × −1).
- `forma_pago` `nvarchar(25)`, `empresa` `nvarchar(15)` (histórico "NORTUR"; el fuente hardcodea
  "PATAGONIA" — bug latente, no reproducir: si algún día se reactiva el alta, grabar "NORTUR").
- **Sin `f_delete`** → baja FÍSICA (`DELETE`), como Contactos/Guardias. Solo `_deleted` de la réplica.

### `parametro` (campos del módulo)

- **`lote_sobre`** bigint = 1767 (numerador GLOBAL de lotes; el `estacion.ult_lote` por estación
  está muerto). **`rubro_comb`** bigint = 1 (rubro combustible). `dcombsaldo` date = 2013-08-01
  (fecha de arranque sugerida del control de saldos).

### `estacion` (catálogo de proveedores; combustible = `rubro = 1`)

- `id` int, `nombre`, `rubro` (FK a `estacion_rubro`), **`control_sa`** = control_saldo truncado,
  `ult_lote` (muerto). Estaciones de servicio hoy: AXION CARD, YPF EN RUTA, y las 3 con
  `control_sa=1` (YPF Larrazábal / Senillosa / Varela) — las únicas que participaron de saldos.
- `estacion_rubro`: `id`+`rubro`(nombre)+`audita` (bit, hoy 0 en los 8 rubros → sin validaciones
  de tanque/odómetro en la carga). `estacion_rubro_articulo`: `id`+`idrubro`+`nombre` (truncado 30
  → "EURO-DIESE"); rubro 1 = DIESEL 500 / EURO-DIESEL (combo tipo de combustible).

---

## Decisiones de esta entrega

1. **Consumo (ítem 1) con el método CORRECTO**, no el del FoxPro (que tiene 2 bugs documentados):
   - l/100km medido **entre cargas LLENO** (Σlitros del tramo / Σkm del tramo × 100), no carga a
     carga; promedio de flota = Σlitros / Σkm global, **no media de medias**.
   - **Sanitizar el delta de odómetro**: descartar tramos con Δkm ≤ 0 o > 1000 km (el flag
     `estacion_rubro.audita` está apagado → hay odómetros incoherentes en datos recientes).
   - Métrica principal = **l/100 km**. El **costo/km es NO confiable hoy**: desde 2018 se paga con
     tarjeta prepaga y `importe` viene 0.00 en las cargas → se muestra solo cuando hay importe > 0.
2. **Conciliación (ítem 2) como ANDAMIAJE** — la grilla de cargas y la barra de lotes (Nuevo lote /
   Marcar / Desmarcar / Marcar todo) están construidas pero la escritura está **apagada** por
   `AbmFeatureFlags.ConciliacionCombustibleAbmActivo = false` (doble candado, como Fleteros).
   Toca `vehiculo_sobre.n_sobre` + `parametro.lote_sobre` (numerador global) → dueño FoxPro hasta
   el día D. La alta/baja/modifica de una carga delega en `CargaCombustibleEditorDialog` (mismo flag).
3. **Saldos y Depósitos (ítems 3 y 4) migrados igual pero marcados como HISTÓRICO**: aviso visible
   "circuito sin uso desde 2017" y rango de fechas por defecto 2013-2017 (donde están los datos).
   El ABM de depósitos es andamiaje por `AbmFeatureFlags.DepositosCombustibleAbmActivo = false`.
   Baja física.

---

## Piezas creadas

- `ReportService`: `GetPromedioConsumosAsync`, `GetCargasCombustibleAsync`, `GetCargaCombustibleRowAsync`,
  `GetLotesCombustibleAsync`, `GetSaldosEstacionesAsync`, `GetDepositosEstacionAsync`,
  `GetDepositoEstacionRowAsync`, `GetEstacionesCombustibleAsync` + DTOs.
- `AbmService` (andamiaje): `NuevoLoteAsync`, `MarcarLoteAsync`/`DesmarcarLoteAsync`,
  `MarcarLoteMasivoAsync`, `Alta/Modifica/BajaCargaCombustibleAsync`, `Alta/BajaDepositoEstacionAsync`.
- `AbmFeatureFlags`: `ConciliacionCombustibleAbmActivo`, `DepositosCombustibleAbmActivo` (ambos `false`).
- `ExcelExportService`: `PromedioConsumos`, `CargasCombustible`, `SaldosEstaciones`, `DepositosEstacion`.
- Menú: sección Combustible con 5 links reales.

## 🐛 Trampas resueltas durante la migración (validadas contra la base)

- **`ClampFecha` NO sirve para combustible.** El helper `ReportService.ClampFecha` acota a
  `FechaMinValida = 2021-01-01` (protege a `viaje` de fechas corruptas). Aplicado a los métodos de
  combustible **vaciaba** los informes históricos: Saldos/Depósitos (datos 2013-2017) daban rango
  2021→2021 = 0 filas, y Consumos con filtro de fechas viejas también. Fix: helper propio `ClampComb`
  (rango 2009-2027) en los 4 métodos de combustible. Síntoma: KPIs y grilla en $0/0 pese a que el SQL
  directo devuelve los números. Validado: Saldos Larrazábal=1.620.555,90 / Senillosa=6.943.745,64
  (idéntico a SQL); Depósitos 787 movimientos / ingresos $50M.
- **Haber (consumos) = 0 en Saldos NO es bug, es dato real.** El `importe` de `vehiculo_sobre` para
  las estaciones YPF con control de saldo en 2013-2017 viene en 0 (se pagaba por depósito anticipado,
  no per-carga) → el "haber" del saldo es 0 y el saldo = todo el debe. Es fiel al FoxPro (su `arma_saldo`
  suma importes reales). No re-valorizar (el `arma_precio` por tarifario es código muerto).
- **Costo/km en Consumos casi siempre "—".** Desde 2018 se paga con tarjeta prepaga y el `importe`
  de la carga viene 0 → no hay costo por km confiable. Se muestra solo cuando hay importe > 0.
  La métrica sólida del informe es **l/100 km**.
- **Eje Y de ApexCharts sin decimales basura**: sin `Yaxis.Labels.Formatter`, muestra
  "60.0000000000". Fix con formatter JS (`v.toFixed(1)` en barras, `toLocaleString('es-AR')` en área).

## Pendientes / próximos (post 1ª entrega)

- Catálogos 8/9/10 (Estaciones / Rubro de Consumos / Artículos): linkear a los ya migrados de
  Tráfico (`Contactos` / `RubrosContacto`) filtrando por `rubro = 1`, o página propia si se quiere
  la vista "solo estaciones de servicio". `estacion_rubro_articulo` no tiene ABM propio aún.
- Control "días sin cargar" (`trafico_vehiculo_combustible`) — vive en la Planilla de Tráfico.
- Activar la escritura (conciliación + depósitos): checklist de `AbmFeatureFlags` + coordinar dueño
  de `estacion`/`vehiculo_sobre`/`parametro` (compartidas) + apagar sync. Es circuito del día D.
