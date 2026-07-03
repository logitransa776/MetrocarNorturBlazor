# Matriz de escritura del circuito `viaje` — referencia para `ViajeAbmService`

> Consolidado de `TRAFICO2_TOOLBAR.md` (toolbar, 02/07/2026) + `TRAFICO_ZOOM.md` (Zoom) +
> `RESERVA_TRANSPORTACION.md` / `RESERVA_PLANTILLAS.md` / `IMPORTA_EXCEL_VIAJE.md` (altas) +
> `FACTURACION_LIQUIDACION.md` (Graba). Fuente de verdad para diseñar los métodos del futuro
> `Services/ViajeAbmService.cs` (Fase 2 del plan `docs/buslink/PLAN_MIGRACION_BUSLINK.md`).

## 1. Matriz operación → escritura

| # | Operación (origen) | `viaje` | `vehiculo` | `viaje_log` motivo | Otras tablas |
| --- | --- | --- | --- | --- | --- |
| A1 | **Alta manual** (Reservas) | INSERT 35+ campos, `origen='T'`, estado SIN ASIGNAR, `cronograma='S/C'` | — | ALTA ("CARGA DE RESERVA") | `viaje_adicional`, `cliente_grupo` (crea/extiende+arrastre), `guia` (upsert), `parametro.id_viaje_int` (rutas) |
| A2 | **Armar plantillas** (Reservas) | INSERT por fecha×fila, `origen='P'`, `lote` | — | ALTA ("CARGA DE PLANTILLA") | `viaje_adicional`, `parametro.lote_plant`, gps |
| A3 | **Importa Excel** (Reservas) | INSERT transaccional, `origen='T'`, adicionales INLINE (slots `adi_*`) → **unificar a tabla en Buslink** | — | (FoxPro NO loguea — Buslink SÍ) | `parametro.lote_plant` |
| T1 | **Chequeo** (toolbar) | `chequeo+1` (estado NO cambia) | — | CHEQUEO ("CHEQUEO UNIDAD") | — |
| T2 | **Asignar** (toolbar, `trafico_asigna` SIN/CON) | estado ASIGNADO + odometro, id_chofer, nombre_cho, franco, tipo_chofer, id_vehicul, interno, id_chofer2, id_interno, fletero | ASIGNADO + hs_inicio + id_viaje | ASIGNO (comentario=motivo en modo CON) | `vehiculo_km` (1er odómetro del mes + cierre mes anterior), `chofer_franco` (Cbia/Trabaja franco), gps |
| T3 | **Reasignar** (toolbar, `trafico_reasigna`) | ídem T2 + `chequeo=0` | nueva ASIGNADO / vieja LIBERADO+id_viaje=0 | RE-ASIGNO (interno_or/ne + cronograma/cronogram2) | gps |
| T4 | **Liberar=FINALIZAR** (toolbar, `trafico_liberar`) | estado FINALIZADO + hs_fin, duracion, pax, voucher_nro, odometro, odometro_fin, km_recorrido (+chofer/vehículo en cierre manual) | LIBERADO + id_viaje=0 + id_viaje_int=0 + **id_zona nueva** | FINALIZO (comentario=motivo hs. extra) | `viaje_adicional` (agua/hs extra/stock CON precio), gps |
| Z1 | **Modificar** (Zoom, solo SIN ASIGNAR/CHEQUEO) | UPDATE ~35 campos + desnormalizados | — | MODIFICO (**diff campo por campo** "campo: viejo → nuevo") | `cliente_grupo` (extiende f_grupo_fin + arrastre) |
| Z2 | **Cancelar** (Zoom) | estado CANCELADO + id_motivo + limpia interno/vehículo/chofer/franco | — | CANCELO (comentario=texto motivo) | `cliente_grupo` (**DELETE** si todo el grupo cancelado), gps |
| Z3 | **Sin Asignar** (Zoom, desde ASIGNADO/CURSO) | estado SIN ASIGNAR + chequeo=0 + limpia interno/vehículo/chofer/franco/fletero/id_interno | LIBERADO + id_viaje=0 | MODIFICO ("PASO A SIN ASIGNAR") | — |
| Z4 | **Reactivar** (Zoom, desde CANCELADO) | estado SIN ASIGNAR + id_motivo=NULL + limpia | — | REACTIVAR | — |
| Z5 | **Duplicar** (Zoom) | INSERT copia (N veces) | — | ALTA ("DUPLICO RESERVA") | `viaje_adicional` (si copia), `cliente_grupo` |
| Z6 | **Valor servicio** (Zoom, FINALIZADO/FACTURADO) | sin_cargo, importe_convenido, moneda_convenida, importe_pago, moneda_pago, sin_cargo_pago, descuento_convenido | — | — | — |
| F1 | **Graba liquidación** (Facturación) | estado FACTURADO + `liquidacio`=FK | — | (no documentado) | `liquidacion` + `liquidacion_detalle` (INSERT), `cliente_grupo.f_grupo_fc` (cierra) |
| FR | **Francos** (`chofer_franco*`) | — | — | — | `chofer_franco`: INSERT masivo (chofer×día, valida no-duplicado y no-trabajó), DELETE físico (permiso '4'), UPDATE FT |

## 2. Permisos por estado (Zoom) — tabla botón × estado

| Estado | Modificar | Cancelar | SinAsignar | ValorServicio | Reactivar |
| --- | :-: | :-: | :-: | :-: | :-: |
| SIN ASIGNAR / CHEQUEO | ✅ | ✅ | ❌ | ❌ | ❌ |
| ASIGNADO / CURSO | ❌ | ❌ | ✅ | ❌ | ❌ |
| FINALIZADO | ❌ | ❌ | ❌ | ✅ | ❌ |
| FACTURADO | ❌ | ❌ | ❌ | ✅ | ❌ |
| CANCELADO | ❌ | ❌ | ❌ | ❌ | ✅ |

Toolbar: Asignar solo sobre SIN ASIGNAR/CHEQUEO; Reasignar solo sobre ASIGNADO/CURSO;
Liberar solo sobre unidad EN CURSO (ASIGNADO sin iniciar → error).

## 3. Desnormalizados que TODO INSERT/UPDATE debe mantener

| Campo | Regla |
| --- | --- |
| `str_f_rese` | = `CONVERT(char(8), f_reserva, 112)` (YYYYMMDD) — SIEMPRE sincronizado |
| `hs_s_inici` | = "HH:MM" derivado de `hs_inicio` |
| `nombre_cli` | desnormalizado de `cliente.razon_soci` |
| `nombre_cho` | desnormalizado de `chofer.nombre` |
| `estado_importe` | 'FIJADO' si `importe_convenido > 0`, '' si no |
| `_deleted` | = 0 explícito en INSERT (los informes filtran por esto) |

## 4. Validaciones de asignación (las 10 de la toolbar)

Unidad: no CURSO, no TALLER, no GUARDIA, logoneada (id_chofer), libre (id_viaje=0),
chofer sin franco/licencia (`trabaja`). Viaje: tramos anteriores de la ruta FINALIZADOS.
Cruzadas: 2º conductor (exigido↔presente, con confirmación), `controla_vencimiento()`
(bloquea por vencimientos de chofer/vehículo), pax > capacidad (warning confirmable).
+ Anti-doble-asignación: releer viaje dentro de la transacción (`UPDLOCK`); si `id_chofer`
ya cargado → abortar.

## 5. Contadores de `parametro` (concurrencia)

FoxPro era efectivamente mono-usuario; la web NO. Patrón obligatorio (SQL 2012 OK):

```sql
UPDATE parametro SET lote_plant = lote_plant + 1 OUTPUT inserted.lote_plant;
-- ídem id_viaje_int; SIEMPRE dentro de la transacción de la operación (la fila = mutex)
```

Prohibido `SELECT MAX()+1` fuera de transacción para lotes/rutas.

## 6. Las 9+ tablas que cambian de dueño el día D

`viaje`, `viaje_log`, `viaje_adicional`, `cliente_grupo`, `vehiculo`, `guia`, `parametro`,
`liquidacion`, `liquidacion_detalle` + `chofer_franco`, `reserva_plantilla`, `vehiculo_km`.
Detalle del corte: `docs/buslink/PLAN_MIGRACION_BUSLINK.md` (Fase 7 — runbook).
