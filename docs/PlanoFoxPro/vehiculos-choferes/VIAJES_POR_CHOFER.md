# Viajes por Choferes — plano FoxPro

- **Menú FoxPro:** Utilitarios → Viajes por Choferes (`MENU_PRINCIPAL.MPR` BAR 17 → `openForm("viaje_analisis_chofer")`)
- **Form:** `viaje_analisis_chofer.scx` (17 objetos, un solo botón "Resultados en Excel")
- **Salida original:** cursor `tmp_chofer` volcado a Excel por OLE (`Exp2Excel`), sin grilla en pantalla
- **Extracción:** 04/07/2026
- **Migrado:** ✅ `ViajesPorChofer.razor` (`/viajes-por-chofer`), menú Vehículos y Choferes → Informes de Flota, permiso `'V'`

## Concepto

Actividad diaria de cada chofer en un período: cuántos viajes hizo por día, cuántos de
**turismo** (`origen='T'`) vs **cabecera** (`origen='P'`), km, el horario del primer y último
viaje del día y la duración trabajada. Además marca los días de **franco** (días sin viajes
intercalados entre el primer y el último día trabajado del chofer).

## Filtro (form original)

- Combo **Mes** + spinner **Año** → arma Desde = 1º del mes / Hasta = fin de mes (`arma_rango`).
  Los textbox Desde/Hasta están **deshabilitados** (`Enabled=.F.`): solo se elige mes+año.
- No hay más filtros: el universo es fijo (ver abajo).

## Universo (query `tmp_viaje` del botón Click)

```foxpro
SELECT ... FROM viaje a INNER JOIN chofer b ON a.id_chofer = b.id_chofer
WHERE Between(str_f_reserva, dFechaSTR, hFechaSTR)
  AND estado_viaje # "CANCELADO"
  AND interno > 0
  AND id_cliente # cID_cliente_prueba          && parametro.id_cliente_prueba (interno NORTUR)
  AND tipo_chofer = "PROPIO"                    && SOLO choferes propios
ORDER BY a.id_chofer, a.hs_inicio
```

También filtra implícitamente `!Empty(id_chofer)` y (en la versión comentada) `id_motivo = 0`.

## Lógica del armado (bucle FoxPro)

Recorre `tmp_viaje` ordenado por chofer y hora. Por cada chofer:
1. Arranca un contador de franco en `dFecha - 1`.
2. Por cada día del chofer: si el día actual ≠ día esperado consecutivo, inserta una fila
   **"FRANCO"** por cada día salteado.
3. Agrupa los viajes del día: cuenta viajes (`nCnt`), turismo/cabecera por `origen`, suma km,
   toma primer `hs_inicio` y último `hs_fin`, calcula duración (`Seg2Hor(dtFin - dtInicio)`).
4. Inserta la fila **"TRABAJO"** del día con esos agregados.

Columnas del cursor `tmp_chofer`: `id_chofer, nombre_chofer, localidad, estado (FRANCO/TRABAJO),
f_reserva, dia (día de semana), h_inicio, desde, h_fin, hasta, trabajo (duración), cnt_viaje,
turismo, cabecera, km`.

## Trampas de réplica SQL (nombres truncados a 10 chars)

| FoxPro | SQL real | Nota |
| --- | --- | --- |
| `str_f_reserva` | `str_f_rese` | mejor filtrar por `f_reserva` (date) directamente |
| `tipo_chofer` | `tipo_chofe` | valores: PROPIO (412k), CONTRATADO (74k), vacío (35k) |
| `estado_viaje` | `estado_via` | |
| `real_domicilio_loc` | `real_domi*` | localidad real truncada; se usa `chofer.localidad` como fallback |
| `id_cliente_prueba` | **no existe** | la réplica solo tiene `parametro.id_cliente` (=NORTUR); es el mismo cliente interno |

`id_motivo` viene **NULL** en la réplica donde el DBF tenía 0 → filtrar `ISNULL(id_motivo,0)=0`.

## Decisiones de la migración Blazor (mejoras acordadas 04/07/2026)

- **Rango de fechas libre** (Desde/Hasta) en vez del combo Mes+Año fijo. Default: **mes anterior
  completo** (el mes en curso puede estar vacío o a medio cargar en la réplica).
- **Switch "Incluir contratados"** (default OFF = solo PROPIO, fiel al FoxPro). Nunca incluye el
  `tipo_chofe` vacío (35k filas sin clasificar).
- **Switch "Internos"** (default OFF = excluye NORTUR, como todos los informes).
- **Métrica Viajes ↔ Km ↔ Pax** (toggle en memoria, sin re-query).
- Patrón dashboard completo: KPIs, evolución diaria (área turismo/cabecera), barras top-N por
  chofer, donut turismo/cabecera, pivote chofer × día con **francos en ámbar** ("F"), drill-down
  al Zoom del Viaje, Excel (Resumen + Pivote con francos + Viajes) y **cross-filter por chofer**.

## Validación (junio 2026, PROPIO, sin NORTUR)

97 choferes · 1.951 viajes · 125 turismo · 1.826 cabecera · 55.379 km — idéntico al SQL directo.
CORONEL HECTOR RAUL (líder) = 37 viajes, 6/31 turismo/cabecera. Smoke test en la suite.
