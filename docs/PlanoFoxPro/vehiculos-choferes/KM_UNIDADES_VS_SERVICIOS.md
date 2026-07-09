# Km Unidades vs Servicios — plano FoxPro

- **Menú FoxPro:** Utilitarios → Km Unidades Vs Servicios (`MENU_PRINCIPAL.MPR` BAR 18 → `openForm("viaje_analisis_km")`)
- **Form:** `viaje_analisis_km.scx` (17 objetos, un solo botón "Resultados en Excel")
- **Salida original:** cursor `tmp_km` volcado a Excel por OLE, sin grilla en pantalla
- **Extracción:** 04/07/2026
- **Migrado:** ✅ `KmUnidadesServicios.razor` (`/km-unidades-servicios`), menú Vehículos y Choferes → Informes de Flota, permiso `'V'`

## Concepto

Eficiencia de cada unidad (vehículo) en un período: compara los **km recorridos reales**
(del odómetro `vehiculo_km` del mes) contra los **km de servicio** (suma de `viaje.km`), para
obtener el **km vacío** (recorrido − servicio) y el **% vacío**. También cuenta servicios, días
trabajados, promedio vacío/día y el consumo (autonomía del vehículo).

## Filtro (form original)

Idéntico a "Viajes por Choferes": combo Mes + spinner Año → un solo mes/año. El odómetro se
busca por `ano_y_mes = cAnoMes` (formato `AAAAMM`).

## Universo y armado (queries del botón Click)

```foxpro
* Km de servicio por vehículo:
SELECT id_vehiculo, Count(km) AS cnt_servicio, Sum(km) AS km_servicio FROM viaje a
WHERE Between(str_f_reserva, dFechaSTR, hFechaSTR) AND estado_viaje # "CANCELADO"
  AND interno > 0 AND id_cliente # cID_cliente_prueba AND tipo_chofer = "PROPIO"
GROUP BY id_vehiculo

* Días trabajados por vehículo (f_reserva distintas):
SELECT f_reserva, id_vehiculo, 1 AS cnt_dia FROM viaje ... GROUP BY f_reserva, id_vehiculo

* Por cada vehículo: odómetro del mes
SELECT * FROM vehiculo_km WHERE cId_vehiculo = dominio AND ano_y_mes = cAnoMes
nRecorrido  = km_fin - km_inicio
nKm_vacio   = (km_fin - km_inicio) - km_servicio
nPorcentaje = (nKm_vacio / nRecorrido) * 100
```

Columnas `tmp_km`: `id_vehiculo, interno, tipo_veh, consumo (autonomia), km_inicial, km_fin,
recorrio, cnt_servicio, km_servicio, km_vacio, cnt_dia, porcentaje, prom_vacio`.

## 🐛 Trampa CRÍTICA de la réplica: campos de vehículo cruzados

En el DBF FoxPro, `viaje.id_vehiculo` = **dominio/patente** de la unidad. **En la réplica SQL
los campos están cruzados:**

| Concepto | FoxPro | SQL réplica |
| --- | --- | --- |
| **Tipo** de vehículo (BUS/VAN/MINI) | `id_vehiculo_tipo` | **`viaje.id_vehicul`** |
| **Dominio/patente** de la unidad | `id_vehiculo` | **`viaje.id_vehicu2`** |

Verificado: `viaje.id_vehicul` guarda BUS/VAN/MINI/AUTO/HIACE; `viaje.id_vehicu2` guarda la
patente (AD463TO…). El odómetro `vehiculo_km.dominio` = `viaje.id_vehicu2` = `vehiculo.dominio`.
`ano_y_mes` es `'AAAAMM'`. `id_cliente_prueba` no existe → usar `parametro.id_cliente` (NORTUR).

## 🐛 Bug heredado corregido en Blazor: % vacío absurdo

El FoxPro calcula `% = km_vacio / recorrido * 100` **sin proteger** contra odómetro incoherente.
Cuando el odómetro tiene `km_fin` apenas mayor a `km_inicio` (recorrido de 1-100 km) pero el km
de servicio es mucho mayor (miles), el km vacío sale **negativo gigante** y el % explota
(-355.800% en un caso real, mayo 2026, unidad AG107DU). Físicamente imposible: no se puede
recorrer menos de lo que se hizo en servicio. **Mejora sobre el FoxPro:** si `recorrido <
km_servicio`, el odómetro está mal cargado → esa unidad se trata como **sin odómetro confiable**
(km recorrido/vacío/% se muestran "—", no se suman a los totales). Condición en el service:
`tieneOdometro = km_fin > km_inicio AND km_inicio > 0 AND recorrido >= km_servicio`.

## Trampa de datos: el odómetro va un mes por detrás

El `km_fin` del odómetro se carga al cerrar el mes. El **mes en curso** tiene `km_fin=0` para
todas las unidades (junio 2026 al 04/07: 99 filas, 0 cerradas). Por eso el default del informe
Blazor es **dos meses atrás** (mayo, con odómetro completo), no el mes anterior — así el km
vacío se ve de entrada. Un aviso informa cuántas unidades quedaron sin odómetro del período.

## Nota de negocio: % vacío global alto

El % vacío global sale alto (~90%) porque el odómetro mide **TODO** el km del mes de la unidad,
mientras que km de servicio solo cuenta los viajes **filtrados** (PROPIO, sin NORTUR, sin
cancelados). Es una limitación heredada del FoxPro (compara peras con manzanas a nivel global);
el % **por unidad** sí es interpretable cuando la unidad hace casi solo servicios filtrados.

## Decisiones de la migración Blazor (04/07/2026)

- **Rango libre**, default dos meses atrás (odómetro cerrado). Switch Internos + Incluir
  contratados. Métrica Km servicio ↔ Km vacío ↔ Servicios.
- Dashboard: KPIs (km servicio, km recorridos, km vacío + % , unidad líder), barras top-N por
  unidad, donut por tipo de vehículo, tabla por unidad con **% vacío coloreado** (verde <30% /
  ámbar 30-50% / rojo ≥50%), drill-down al Zoom, Excel y **cross-filter unidad AND tipo**.

## Validación (mayo 2026, PROPIO, sin NORTUR)

Total km servicio 203.522 (102 unidades) · km recorridos 1.985.855 · km vacío 1.809.356 · 91%
global — idéntico al SQL directo. AC750TS (líder) 4.709 km servicio, 27,9% vacío. Las dos
unidades con odómetro incoherente (AG935CT, AG107DU) muestran "—" (bug corregido). Smoke test
en la suite.
