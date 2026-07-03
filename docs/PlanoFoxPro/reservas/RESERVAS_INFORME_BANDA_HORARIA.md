# Lógica FoxPro — Informe Reservas por fecha y banda horaria (`trafico_resumen_horario.scx`)

> Menú: **Reservas → Informes sobre Reservas → Reservas por fecha y banda horaria**.
> ABM de bandas: **… → Bandas Horarios** (`trafico_resumen_horario_banda.scx` +
> `trafico_resumen_horario_banda_abm.scx`). Extraído 12/06/2026.
> Es el **Informe 2 pendiente** del dashboard Blazor (ver CLAUDE.md).

---

## Qué hace

Cuenta reservas por **fecha × banda horaria × tipo de vehículo** y lo entrega como
**tabla dinámica de Excel** (PivotTableWizard por OLE): filas = fecha,
columnas = banda horaria, página/filtro = vehículo, dato = COUNT.

## Filtros de la pantalla

| Filtro | Detalle |
|---|---|
| Desde / Hasta fecha | default hoy; F5 calendario; valida desde ≤ hasta |
| Tipos de vehículo | checklist de `vehiculo_tipo` con `vende = .T.` y no borrados — **default ninguno marcado** (obliga a elegir) |
| Bandas horarias | checklist de `viaje_horario` (las 6 bandas) — default **todas** marcadas |
| Tipo de servicio (combo) | "Servicios de transportación" / "de plantilla" / "Todos" — **decorativo: la lógica está comentada y el WHERE fija `origen = 'T'`** |

## Lógica de cálculo (botón consultar)

```sql
SELECT * FROM viaje
WHERE BETWEEN(str_f_reserva, DTOS(desde), DTOS(hasta))   -- filtra por el char YYYYMMDD
  AND origen = 'T'                                        -- hardcodeado (combo ignorado)
ORDER BY hs_inicio
```

Luego, fila por fila descarta:
- `id_cliente = 'NORTUR'` (viajes internos);
- `estado_viaje = 'CANCELADO'`;
- banda: la **hora de inicio string** (`hs_s_inici` "HH:MM") debe caer
  `BETWEEN(dHorario, hHorario)` de alguna banda **marcada** (comparación de strings);
- vehículo: `id_vehicu2` debe estar entre los marcados.

Cada viaje que pasa inserta `(fecha, vehiculo, rango "dHorario - hHorario", 1)` en un
cursor `trafico_informe`; el botón **análisis** agrupa
`GROUP BY fecha, rango_horario, veh` con `SUM` y arma el pivot en Excel
(`Trafico_resumen.xls` en el dir corriente).

## Bandas horarias (`viaje_horario` — 6 filas)

`00:00-00:01`, `00:02-06:29`, `06:30-08:29`, `08:30-14:00`, `14:01-18:00`, `18:01-23:59`.
ABM mínimo (`trafico_resumen_horario_banda*.scx`): alta y baja de bandas
(dhorario/hhorario char(5)). Sin validación de solapamiento.

## ✅ Migrado a Blazor (02/07/2026) — `ReservasBandaHoraria.razor`

Rearmado con el mismo formato que **Reservas por fecha y servicio** (barra de filtros
horizontal arriba, KPIs flex, gráficos ApexCharts sin animación, tabla pivote sticky +
`Virtualize` + drill-down + Excel multi-hoja). Decisiones tomadas con el usuario:

- **Filtro de Estados** (multiselect de los 5, igual que fecha/servicio). **Default = todos
  MENOS CANCELADO** (así el número por defecto reproduce el FoxPro, que hardcodea
  `estado <> CANCELADO`, pero el usuario puede sumar cancelados o acotar a SIN ASIGNAR/ASIGNADO).
  Corrige el subtítulo viejo, que decía "SIN ASIGNAR / ASIGNADO" pero contaba todo lo no cancelado.
- **Toggle Viajes / Pax** (recálculo en memoria, sin re-query — la query agregada trae
  `COUNT(*)` y `SUM(pax)` juntos).
- **Drill-down**: click en celda / total de fila / encabezado o total de columna abre
  `ReservasFsDetalleDialog` (reuso del informe de fecha/servicio) con los viajes uno por uno →
  click en fila → Zoom del Viaje.
- **Origen** sigue fijo en `'T'` (transportación), como el FoxPro. El 'P' (plantilla) queda
  afuera — no se implementó el combo muerto (decisión: no agrega valor operativo hoy).
- Clasificación de banda por `CAST(hs_inicio AS TIME)` con `BETWEEN` de strings "HH:mm"
  (bordes inclusivos) — idéntica al FoxPro. Fragmento SQL compartido (`BandaCaseSql`) entre la
  vista agregada (`GetReservasPorBandaHorariaAsync`) y el detalle (`GetReservasBandaHorariaDetalleAsync`)
  para que ambas den la misma banda.

Validado al dígito (período 02/06–02/07/2026, todos menos cancelado): 339 viajes / 8.755 pax;
por banda 15/17/54/145/33/75 viajes.

---

## Lógica original FoxPro (referencia)

1. Replicar el conteo con un solo GROUP BY:
   ```sql
   SELECT v.f_reserva, h.dhorario + '-' + h.hhorario AS banda, v.id_vehicu2, COUNT(*)
   FROM viaje v JOIN viaje_horario h
     ON v.hs_s_inici BETWEEN h.dhorario AND h.hhorario
   WHERE v.f_reserva BETWEEN @d AND @h
     AND v._deleted = 0 AND v.estado_via <> 'CANCELADO'
     AND v.id_cliente <> 'NORTUR' AND v.origen = 'T'
   GROUP BY v.f_reserva, h.dhorario, h.hhorario, v.id_vehicu2
   ```
2. Decidir con el usuario si el filtro de origen (T/P/Todos) se implementa de verdad
   (en FoxPro quedó muerto — y el 86% del volumen es 'P', que hoy queda afuera).
3. La banda `00:00-00:01` existe para aislar los viajes sin hora real (medianoche exacta).
4. El pivot fecha × banda es la tabla principal; vehículo como filtro/serie.
   `viaje_horario` editable a futuro (ABM trivial).
5. Comparación de horas como **strings "HH:MM"** — mantenerla (no parsear a time) para
   reproducir resultados idénticos, incluido el borde inclusivo BETWEEN.
