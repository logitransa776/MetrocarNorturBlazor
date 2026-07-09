# Lógica FoxPro — Informe "Reservas por Cliente" (`viaje_analisis.scx`)

> Menú: **Utilitarios → Reservas por Cliente** (`MENU_PRINCIPAL.MPR` bar 16 →
> `openform("viaje_analisis")`). Extraído 03/07/2026, validado contra la réplica SQL.
> Sin restricción de permiso propia en el menú (el popup Utilitarios es de acceso general;
> solo Scheduler pide `H` y Conectados pide `S`).
>
> **Familia de 3 informes gemelos** en el mismo bloque del menú (mismo patrón de form:
> rango de fechas + Excel PivotTable por OLE):
> `viaje_analisis.scx` (este) · `viaje_analisis_chofer.scx` ("Viajes por Choferes") ·
> `viaje_analisis_km.scx` ("Km Unidades Vs Servicios") — los otros dos quedan pendientes
> de extraer cuando se decida migrarlos.

---

## Qué hace

Cuenta **viajes de transportación** (`origen = 'T'` — reservas especiales/manuales; las
plantillas y cabeceras quedan afuera porque son `origen = 'P'`) y entrega una **tabla
dinámica de Excel** (PivotTableWizard por OLE, archivo `viaje_resumen.xls` en el dir
corriente):

| Eje del pivot | Campo | Detalle |
|---|---|---|
| Filas | `nombre_cliente` | nombre **desnormalizado** del viaje, no join a `cliente` |
| Columnas | `periodo` | `YYYYMM` de `f_reserva` |
| Página (filtro) | `tipo` | SIN REALIZAR / PROPIO / CONTRATADO (ver clasificación) |
| Dato | `cnt_viaje` | siempre 1 → el pivot suma = **COUNT de viajes** (no pax, no importes) |

## Pantalla

Form mínimo "Form1" (sin caption real): Desde/Hasta (default **hoy-hoy**, F5 calendario),
checkbox **"Analisis de movimientos cancelados"** (default off), botón **Resultados en
Excel** y **Salir**. No hay grilla en pantalla — el único output es el Excel.

## Lógica exacta (Command1.Click)

**Modo normal** (checkbox off) — reservas NO canceladas del período:

```foxpro
SELECT id_cliente, nombre_cliente,
       ALLTRIM(STR(YEAR(f_reserva)))+RIGHT("00"+ALLTRIM(STR(MONTH(f_reserva))),2) AS periodo,
       IIF(interno<1000, IIF(interno=0,"SIN REALIZAR","PROPIO"), "CONTRATADO") AS tipo,
       1 AS cnt_viaje
FROM viaje
WHERE origen = "T" AND id_motivo = 0
  AND BETWEEN(str_f_reserva, DTOS(desde), DTOS(hasta))   && char YYYYMMDD
INTO CURSOR TMP_VIAJE
```

**Modo cancelados** (checkbox on):

```foxpro
SET DELETED OFF        && incluye filas borradas del DBF
SELECT ... FROM viaje WHERE origen = "T" AND id_motivo = 2 INTO CURSOR TMP_VIAJE
SET DELETED ON
```

⚠️ Tres cosas del modo cancelados que NO son obvias desde la pantalla:

1. **Ignora el rango de fechas por completo** — barre TODO el histórico (2006→hoy).
   Los textbox Desde/Hasta quedan decorativos en ese modo.
2. **Solo motivo 2 = "CANCELADO POR CLIENTE"** — no todos los cancelados. Deja afuera
   CANCELADO POR NORTUR (1), POR PRECIO (3), POR INCONVENIENTES (4), POR ERROR EN
   CARGA (5), CLIENTE OLVIDÓ DE CANCELAR (6). Coherente con el objetivo (medir qué
   clientes cancelan), pero es un hardcode.
3. **`SET DELETED OFF`** suma los viajes borrados físicamente del DBF. La réplica SQL
   **no tiene ninguna fila `_deleted = 1` en `viaje`** (verificado: 521.230 filas, todas
   `_deleted = 0`) → desde SQL no se pueden reproducir esos borrados; gap asumido menor.

## Clasificación `tipo` (regla de negocio del interno)

| Condición | Tipo | Significado |
|---|---|---|
| `interno = 0` (o NULL en SQL) | **SIN REALIZAR** | viaje sin unidad asignada aún (típico futuro) |
| `0 < interno < 1000` | **PROPIO** | flota propia (verificado: 373 vehículos con interno < 1000) |
| `interno >= 1000` | **CONTRATADO** | fletero/contratado (verificado: 33 vehículos con interno ≥ 1000) |

`viaje.interno` es el número de interno desnormalizado en el viaje (bigint en la réplica).

## Motivos de cancelación (`viaje_motivo_cancela`, 6 filas)

1 CANCELADO POR NORTUR · **2 CANCELADO POR CLIENTE** · 3 POR PRECIO · 4 POR
INCONVENIENTES · 5 POR ERROR EN CARGA · 6 CLIENTE OLVIDO DE CANCELAR.

Correlación verificada en la réplica: `id_motivo > 0` ⇔ `estado_via = 'CANCELADO'`
(una sola fila FINALIZADO con motivo 2 en 520 mil — ruido de datos).

## Trampas para la migración (verificadas contra `sys.columns` y datos, 03/07/2026)

| # | Trampa | Detalle |
|---|---|---|
| 1 | **`id_motivo` viene NULL en la réplica** | 63.819 NULL vs 266 con 0 (en DBF no hay NULL). El WHERE debe ser `ISNULL(id_motivo,0) = 0` / `= 2` |
| 2 | **`interno` también viene NULL** | 2.128 filas 2026 con NULL → tratarlas como 0 = SIN REALIZAR (`ISNULL(interno,0)`) |
| 3 | **Truncados a 10 chars** | `nombre_cliente` → `nombre_cli` · `str_f_reserva` → `str_f_rese` (formato `YYYYMMDD` confirmado). En SQL usar `f_reserva` (date) directo, no el char |
| 4 | **NO excluye el cliente interno NORTUR** | a diferencia del informe de banda horaria (que lo excluye hardcodeado). NORTUR = **3.034 de 10.045 viajes 2026 (~30%)**, casi todo TRASLADO (1.736) y GUARDIA8 (1.253) internos — infla fuerte el pivot original |
| 5 | **Agrupa por nombre desnormalizado** | un cliente renombrado se parte en 2 filas del pivot (15 clientes con `nombre_cli` divergente en el histórico). Mejor: agrupar por `id_cliente` y mostrar nombre canónico |
| 6 | **Fechas corruptas** | 7 filas `origen='T'` fuera de 2021–2027 → aplicar el clamp estándar (`FechaMinValida`/`FechaMaxValida`) |
| 7 | **Sin cabeceras por diseño** | `origen='T'` deja afuera CABECERA_KM/SERV (son `origen='P'`) — acá NO hace falta el switch de cabeceras del Informe 1 |

## Volúmenes de referencia (réplica local, 03/07/2026)

- `origen='T'` total: **81.461** viajes (vs 439.769 de plantilla).
- 2026 modo normal (`ISNULL(id_motivo,0)=0`): **10.045 viajes / 190.102 pax** —
  `pax` acá es confiable (0 NULL, solo 161 en cero) → la métrica Pax es viable.
- Top servicios 2026: TRASLADO 1.955 · AEROPARQUE 1.432 · EZEIZA 1.417 · GUARDIA8 1.253 ·
  CITY 778 · CENA SHOW 694.
- Cancelados por cliente (motivo 2) por año: 2021: 164 · 2022: 2.527 · 2023: 4.554 ·
  2024: 3.271 · 2025: 3.420 · 2026: 2.005 — volumen real todos los años; el análisis de
  cancelaciones tiene valor con rango de fechas aplicado (a diferencia del FoxPro).

## Migración a Blazor — decisiones tomadas con el usuario (03/07/2026)

Aplica la regla 7 de CLAUDE.md (patrón dashboard completo con cross-filter). Dimensiones:
**cliente** (alta cardinalidad → barras top-N + tabla) y **tipo** (3 valores → donut +
series apiladas), período en columnas mensuales. Las 4 decisiones (opción recomendada en
todas):

1. **Cliente interno NORTUR excluido por defecto** con switch "Internos" (como Informe 1).
   El número default NO coincide con el Excel FoxPro (que lo incluye) — con el switch
   prendido sí.
2. **Modo cancelados = filtro flexible**: toggle Activas/Canceladas + multiselect de los
   6 motivos (default solo "CANCELADO POR CLIENTE") y SIEMPRE respetando el período
   elegido — NO se replica el barrido histórico del checkbox FoxPro.
3. **Métrica Viajes ↔ Pax** sin re-query (pax confiable en este subconjunto).
4. **Menú: sección Reservas** ("Reservas por cliente"), permiso `'R'` — no se crea
   sección Utilitarios.

Página: `ReservasPorCliente.razor` (`/reservas-por-cliente`), cross-filter 2D
cliente AND tipo (variante dos dimensiones de la skill `blazor-nortur`).

## ✅ Migrado a Blazor (03/07/2026) — `ReservasPorCliente.razor`

Patrón dashboard completo, calcado de `ReservasBandaHoraria.razor` (la otra variante 2D):

- **Filtros**: período (default últimos 6 meses calendario), Ver = Activas/Canceladas
  (con multiselect de motivos cuando Canceladas), switch Internos, métrica Viajes↔Pax.
- **KPIs**: total métrica (+prom/mes), clientes con actividad, cliente líder, contratados (+%).
- **Gráficos**: barras apiladas mes × tipo (leyenda clickeable), barras horizontales top-N
  clientes (selector Mostrar 10/15/20/Todos, paleta 15 hues, distributed), donut por tipo
  (colores fijos: PROPIO azul `#2058D0`, CONTRATADO naranja `#F99410`, SIN REALIZAR gris).
- **Tabla pivote cliente × mes** (`Virtualize`): click en nombre de cliente = foco; celda =
  drill-down (reusa `ReservasFsDetalleDialog` + Zoom); footer por mes también drillea.
- **Cross-filter 2D** cliente AND tipo, con chips y update en el lugar (sin parpadeo).
- **Excel** (`ExcelExportService.ReservasPorCliente`): Detalle + Pivote + Resumen por tipo +
  Viajes uno por uno (con columna Motivo de cancelación).
- Service: `GetReservasPorClienteAsync` / `GetReservasPorClienteDetalleAsync` /
  `GetMotivosCancelacionAsync` + `TiposReservaCliente` en `ReportService`.

**Validado con dos señales (03/07/2026):** UI 02/2026–03/07/2026 sin internos = 4.632
viajes / 98 clientes / 65 contratados — SQL directo da exactamente lo mismo; celda GATE1
TRAVEL × 03/2026 = 335 verificada al dígito. Cross-filter probado con Playwright (foco
GATE1 TRAVEL + PROPIO → 857 viajes, chips en los 3 paneles). Smoke test agregado a la suite
(`smoke.spec.ts` → "Reservas por cliente — levanta con KPIs y pivote").
