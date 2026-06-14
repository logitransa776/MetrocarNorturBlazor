---
name: modulo-combustible
description: Conocimiento del módulo Combustible de Metrocar — cargas de combustible de la flota (tabla viva vehiculo_sobre, NO replicada a SQL), conciliación por sobre/lote, promedio de consumos (litros/100km), control de días sin cargar, saldos/depósitos por estación (sin uso desde 2017), catálogo estacion (que en realidad es el catálogo de PROVEEDORES). Usar SIEMPRE que se trabaje con combustible, cargas, litros, odómetro, consumos de la flota, tablas vehiculo_sobre/vehiculo_combustible/vehiculo_estacion_saldo/vehiculo_combustible_precio/estacion/estacion_rubro*, lotes o sobres de conciliación, parametro.lote_sobre/rubro_comb, estaciones de servicio YPF, o cuando se construyan informes o ABMs de este módulo en Blazor. Incluye el gap crítico de réplica y las dos eras del módulo.
---

# Módulo Combustible — mapa de conocimiento

Registra las **cargas de combustible de la flota propia** (~8.000/año desde 2013, activo hoy),
las concilia en **lotes/sobres** numerados contra los resúmenes de las estaciones, y calcula el
**promedio de consumo** (litros cada 100 km) por vehículo vía odómetro.

**Doc detallado (leer ANTES de codear):** `docs/logica-foxpro/COMBUSTIBLE.md`
— menú completo, lógica método por método, validaciones, datos reales y trampas.

## ✅ vehiculo_sobre está replicada en el servidor activo (confirmado 12/06/2026)

```
vehiculo_sobre (172.25.69.217)  109.624 registros, última carga 08/06/2026  ✅ REPLICADA
vehiculo_combustible (SQL)       36K registros, CONGELADA en 2016            ✅ replicada (engaña)
```

**Los informes de combustible se pueden construir ya desde `replicaVPF` (172.25.69.217).**
El paso 0 (replicar vehiculo_sobre) ya está cumplido en este servidor.

Filtrar siempre `f_carga BETWEEN '2009-01-01' AND '2027-12-31'` — existen registros con año
corrupto (MAX sin filtro = 4202). Con filtro sano: 109.571 filas, 3.730 cargas en 2026.

> El gap documentado antes era válido para el servidor viejo (DESKTOP-CV6LF0O).

## Las dos eras

- **Era 1 (2009–2016)** — `vehiculo_combustible`: control por factura/remito, importación
  Excel ESSO. Forms `vehiculo_combustible*` legacy, ya no alcanzables del menú. NO migrar.
- **Era 2 (2013–hoy)** — `vehiculo_sobre`: conciliación por **sobre/lote** (numerador global
  `parametro.lote_sobre`, hoy 1767). 2013–2017 hubo además control de saldos/depósitos y
  tarifario por estación (3 YPF con `control_saldo`); desde 2018 se paga con TARJETA PREPAGO
  y solo siguen vivas la carga y la conciliación.

## Flujo operativo actual

```
CARGA:  Planilla de Tráfico → menú contextual del vehículo → "Carga de combustible"
        → vehiculo_combustible_carga_sobre_trafico (modo "trafico", prellenado)
        → INSERT INTO vehiculo_sobre (p_x_ltr = importe/litros, NO tarifario)
        Validaciones si estacion_rubro.audita: litros ≤ vehiculo.litro_tanque,
        delta odómetro 0..1000 km (forzable). Chofer o literal "SIN CHOFER".

CONCILIACIÓN (menú Combustible → ABM y Conciliación cargas, permiso "M" $ acceso):
        vehiculo_combustible_mant_sobre_lote: filtra TODOS/DOMINIO/LOTE/ESTACION,
        filas amarillas = conciliadas (n_sobre ≠ 0).
        Nuevo lote = parametro.lote_sobre + 1 (UPDATE parametro) →
        Marca/MarcaTodo = UPDATE vehiculo_sobre SET n_sobre = lote WHERE id.
        30.853 cargas sin conciliar (n_sobre = 0).

INFORME: vehiculo_combustible_consumo — km_promedio = litros/Δodómetro×100 por carga,
        tot_promedio = media de medias. (Fallas conocidas: no filtra cargas PARCIAL;
        al migrar usar tramos entre cargas LLENO y Σlitros/Σkm.)

CONTROL: trafico_vehiculo_combustible (desde la planilla) — días sin cargar por
        vehículo activo, botón Carga directo.
```

## Tablas (con trampas)

| Tabla | Filas | Trampas |
| --- | --- | --- |
| **`vehiculo_sobre`** | 109K | ❌ no replicada. `n_sobre`=lote (0=sin conciliar), `idrubro`, `f_pago` texto libre C(30), `lleno` L, `dos_carga`+`litro_2`, `hora` C(5), auditoría `u_create/f_create/u_modify/f_modify` |
| `vehiculo_combustible` | 36K SQL | era 1 congelada 2016 — no confundir con la viva |
| `vehiculo_estacion_saldo` | 787 | depósitos 2013–2017 (sin uso). Egreso = importe negativo. Fuente actual hardcodea `empresa = "PATAGONIA"` (histórico dice NORTUR) |
| `vehiculo_combustible_precio` | 363 | tarifario estación×tipo×vigencia, muerto desde 2017. `p_x_ltr` real se deriva importe/litros |
| `estacion` | 178 | ⚠️ **es el catálogo de PROVEEDORES de toda la empresa** (8 rubros: estaciones, gomerías, grúas, fleteros, cristales...). Solo 5 son estaciones (rubro=1), 3 con `control_sa`. También se abre desde menú Contactos |
| `estacion_rubro` / `_articulo` | 8 / 2 | flag `audita` activa validaciones de carga (hoy apagado). Artículos rubro 1 = DIESEL 500, EURO-DIESEL = combo tipo de combustible |
| `vehiculo_tipo_combustible` | 4 | catálogo viejo, la carga actual no lo usa |
| `parametro` | 1 | **`lote_sobre`** (numerador global), **`rubro_comb`**=1, **`dcombsaldo`**=2013-08-01 |
| `vehiculo` | — | `litro_tanque`, `activo`, `uso`="PROPIO", `id_vehiculo`=dominio |

## Reglas y trampas no obvias

1. **Join de estaciones por NOMBRE desnormalizado** (`estacion_nombre = estacion.nombre`),
   no por id — renombrar una estación rompe la historia.
2. El numerador de lotes es `parametro.lote_sobre` (global), NO `estacion.ult_lote`
   (viejo, en desuso). Cancelar la asignación no devuelve el número (quedan huecos).
3. **Bajas físicas** (`DELETE FROM`) en todo el módulo.
4. Saldo estación = depósitos − consumos **a importe real** (el `arma_precio` que
   re-valoriza por tarifario es código muerto copy-pasteado en 4 forms + procesos.prg,
   incluido `chofer_viatico` — los viáticos NO dependen de combustible).
5. Datos recientes pueden tener odómetros incoherentes (flag `audita` apagado):
   sanitizar deltas (descartar ≤0 o >1000 km) en cualquier cálculo de consumo.
6. Permiso del pad: letra **M** en `usuario.acceso` (confirma skill `seguridad-nortur`);
   columnas de precio en exports = permiso F.
7. Strangler (skill `abm-metrocar`): `vehiculo_sobre`, `parametro` y `estacion` tienen
   dueño FoxPro → **solo lectura desde Blazor** hasta migrar el ABM con puente inverso.

## Reportes FoxPro de referencia

`vehiculo_combustible.frx` (listado cargas), `vehiculo_combustible_consumo.frx` (consumos,
también PDF), `vehiculo_estacion_saldo.frx`, `vehiculo_combustible_saldo_fc.frx` (era 1).
En `C:\MetroCarSys\Reports`.

## Informes candidatos para Blazor (orden sugerido)

0. **Replicar `vehiculo_sobre` a SQL** — prerrequisito de todo.
1. **Dashboard de Consumos** — litros/100km entre cargas LLENO, ranking de flota,
   evolución mensual, costo por km.
2. **Control de cargas** — días sin cargar + cargas sin conciliar.
3. **Costo mensual de combustible** por vehículo/estación/tipo (hoy no existe en FoxPro).
4. **Conciliación por lote** (escritura) — recién con la regla strangler cumplida.
