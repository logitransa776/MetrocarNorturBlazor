---
name: modulo-facturacion-liquidacion
description: Conocimiento del módulo Facturación y Liquidación de Metrocar — liquidación a clientes (facturacion_cliente_nueva), a fleteros y a choferes, resumen/revertir/carga de comprobante, tarifarios de venta y de pago (lista_precio, lista_precio_chofer, adicional_lista_precio/pago), cuenta corriente (ctacte, programada pero sin uso), monedas y cotizaciones. Usar SIEMPRE que se trabaje con liquidaciones, facturación, tarifas/precios, tablas liquidacion/liquidacion_detalle/lista_precio*/ctacte*, estados FACTURADO o LIQUIDADO, viaje.liquidacio/liquidaci2, importes convenidos, horas extra, adelantos de choferes, o cuando se construyan informes o ABMs de este módulo en Blazor. Mapa de tablas, flujo de valorización, cascadas de precios y descuentos, y trampas de nombres truncados.
---

# Módulo Facturación y Liquidación — mapa de conocimiento

Es el módulo que **convierte viajes FINALIZADOS en plata**: valoriza contra tarifarios,
arma la liquidación (pre-factura interna), y marca los viajes como FACTURADO (venta) o
LIQUIDADO (pago a fletero). La factura fiscal real se hace FUERA del sistema y se anota
a mano sobre la liquidación.

**Doc detallado (leer ANTES de codear):** `docs/PlanoFoxPro/facturacion/FACTURACION_LIQUIDACION.md`
— menús completos, lógica método por método, validaciones, SQL real y datos de uso.

## Los 3 circuitos (y cuánto se usan — réplica 12/06/2026)

```
VENTA (cliente):   viaje FINALIZADO + grupo vencido → valoriza → liquidacion tipo=CLIENTE
                   → UPDATE viaje SET estado_viaje='FACTURADO', liquidacio=nId
                   → UPDATE cliente_grupo SET f_grupo_fc=HOY (¡cierra el grupo!)
                   ★ ACTIVO: 4.184 liquidaciones hasta 06/2026, 462K viajes facturados

PAGO (fletero):    viaje FINALIZADO|FACTURADO + estado_pag vacío + fletero=X
                   → liquidacion tipo=PROVEEDOR (tarifario lista_precio_chofer)
                   → UPDATE viaje SET estado_pag='LIQUIDADO', liquidaci2=nId
                   ◐ CASI SIN USO: 12 liquidaciones, 344 viajes

NÓMINA (chofer):   liquidacion_chofer_por_hora = calculadora de horas extra/francos/vales
                   → NO escribe en la base, sale por Excel
```

**Cuenta Corriente (`ctacte` + 4 satélites): programada completa pero con 0 filas — no se
usa en producción.** Solo existe en la variante de menú `MENU_PRINCIPAL_C_CC.MPR`.

## Flujo de valorización (el corazón — replicar EXACTO)

Cascada de precio por viaje (gana el primero):
1. `importe_co` > 0 (convenido en la reserva) → ese importe y `moneda_con`
2. `sin_cargo` → 0
3. Tarifario: si hay `cabecera` y `cliente.fc_prefere='C'` → tarifa la cabecera;
   si no, cada servicio según `servicio.modo_fac`: **S** cerrado / **H** por horas
   (excedente = horas extra al precio del servicio `parametro.cliente_ad`, fracción
   según `parametro.fraccion_h`: ≤30' media hora, >30' hora entera) / **K** por km.

`obtiene_tarifa`: `cliente.ob_precio` = 'LISTA PRECIO' (404 clientes) usa
`cliente.id_lista_p` directo; = 'CLIENTE' (9) pasa por `cliente_tarifa` por vigencia.
Lookup en `lista_precio`: lista × vigencia × servicio × tipo vehículo → 4 niveles de
error (cliente/vigencia/servicio/vehículo), precio -1 = fila amarilla sin tarifa.

Cascada de descuento por servicio (gana el primero): `descuento_` convenido del viaje →
`cliente.descuento` → `cliente.incremento` → `cliente_descuento` por período (vacía hoy).

Total: `(subtotal+extra ± ajuste manual con motivo) × t_cambio + IVA + adicionales`
(adicionales = exentos, sin IVA; moneda dominante USS con tipo de cambio manual).

## Tablas (nombres SQL reales — truncados)

| Tabla | Filas | Trampas |
| --- | --- | --- |
| `liquidacion` | 4.197 | PK **`idliquidac`**; `tipo` CLIENTE/PROVEEDOR; `id_cliente` = cliente o `fletero.id_contrat`; **`retencion_`=IVA, `retencion2`=IIBB, `retencion3`=SUSS** (verificado contra el form 18/06/2026; `retencion4` existe pero el comprobante NO la usa); `total` NULL en PROVEEDOR; factura manual en `tcp/lcp/ncp/fcomp` (la columna *Factura* del browser = `tcp-lcp-SUBSTR(ncp,1,4)-SUBSTR(ncp,5)`). El nombre sale de `cliente.razon_soci` o `fletero.razon_soci`/`fletero.nombre` (cliente NO tiene columna `nombre`) |
| `liquidacion_detalle` | 819K | `idliquidac`, `tipo` SERVICIO/ADICIONAL, **`id_adicion`** = código de servicio O adicional, `id_viaje_i` (ruta), `d_destino_`, `km_recorri`. ⚠️ NO incluye el ajuste global de cabecera |
| `lista_precio` / `lista_precio_chofer` | 2.5K / 3.7K | `id_lista_p`, `id_servici`, `id_vehicul` (=TIPO), **`f_vigencia`/`f_vigenci2`** (desde/hasta), `tipo` S=servicio C=cabecera |
| `lista_precio_modelo[_chofer]` | 46 / 25 | la MONEDA vive acá (`id_moneda_`), baja lógica |
| `adicional_lista_precio` / `_pago` | 95 / 329 | venta / pago; `id_adicion` × `id_vehicul` × `fdesdevg/fhastavg` |
| `cliente_tarifa` / `cliente_descuento` | 4 / 0 | casi sin uso |
| `chofer_adelanto` | 375 | vales; `tipo_mov` D resta; `nombre_cho`, `tipo_chofe` |
| `chofer_parametro_sueldo` | 177 | mes/año + `hs_extra_b/m/s` (bus/mini/sprinter), jornal, francos |
| `moneda_tipo` / `moneda_cotizacion` | 3 / 32 | cotización por vigencia `dinicio/dfin` |
| `empresa` | 2 | una columna, sin PK |
| `ctacte`, `ctacte_afectacion`, `ctacte_detalle`, `ctacte_pago`, `ctacte_t_comp` | **0** | modelo D/H completo sin uso (detalle en el doc §8) |
| `viaje` (campos del módulo) | — | **`liquidacio`** (venta) / **`liquidaci2`** (pago), `estado_pag`, `importe_co/moneda_con/descuento_`, `importe_pa/moneda_pag`, `sin_cargo`, `fletero`, `f_grupo_fi` |
| `servicio` | 61 | `modo_fac` S(16)/H(39)/K(6), `modo_km` V/S, `horas_dura/minutos_du` |
| `parametro` | 1 | `cliente_ad` (servicio hora extra), `fraccion_h`, `adic_malet`, `piva`, `ley_liq_1/2` |

Catálogos con nombre cruzado: form "Bancos" (`ctacte_banco`) escribe **`liquidacion_banco`**;
"Impuestos sobre Ventas" (`ctacte_impuesto`) escribe **`liquidacion_impuesto_tasa`**;
"Tipos de Comprobantes" escribe **`ctacte_t_comp`**.

## Reglas y trampas no obvias

1. **Candado temporal**: solo se liquidan viajes FINALIZADOS con `f_grupo_fi < HOY`
   (modo POR ESTADO). El grabado además CIERRA el grupo (`cliente_grupo.f_grupo_fc`).
2. **Rutas** (`id_viaje_i > 0`): se valoriza el ÚLTIMO tramo con `hs_ini_rut/hs_fin_rut`
   y el UPDATE de estado pega a TODOS los tramos por `id_viaje_int`.
3. **Revertir** (en Resumen): borra liquidación+detalle y revive viajes, pero NO limpia
   `viaje.liquidacio` ni reabre el grupo — corregir esa asimetría al migrar.
4. El grabado original es **sin transacción** (cabecera → detalle → grupo → viajes):
   en Blazor envolver todo en una transacción.
5. El menú contextual "Pegar Cabecera" hace `UPDATE viaje SET cabecera` (escritura real
   escondida en un form de "consulta").
6. `lista_precio_cliente.scx` tiene queries copy-paste que apuntan a `lista_precio_chofer`
   aunque inserta en `lista_precio` — verificar contra el exe productivo.
7. Doble conducción (`id_chofer2`): en nómina el viaje se duplica para el 2º chofer y
   maletas/propinas se parten al 50%.
8. Forms legacy que NO abre ningún menú (no migrar): `facturacion_cliente(_v2/_arsa/_view)`,
   `liquidacion_resumen(_chofer)`, `liquidacion_chofer_mixto*`, `*_bk` — lista en doc §11.
9. Strangler (skill `abm-metrocar`): todas las tablas del módulo tienen dueño FoxPro →
   **solo lectura desde Blazor** hasta migrar el ABM con puente inverso.
10. **`CABECERA_KM` / `CABECERA_SERV` NO son servicios de transporte, son MODOS de
    facturación** (por km / por servicio de cabecera; el destino real está en
    `viaje.d_destino`/`h_destino`). Son ~90% del volumen de reservas. En cualquier informe
    agrupado por servicio aplastan a los servicios reales → excluir por default (con opción
    reversible). Ver memoria `cabeceras-no-son-servicios` y skill `blazor-nortur`
    (§ Patrón de informe analítico → Trampa de negocio).

## Reportes FoxPro de referencia

`viaje_adicional.frx` + `viaje_adicional_total.frx` (liquidación formato RESUMEN, van
encadenados con FoxyPreviewer), `viaje_personal.frx` (ABIERTO), `viaje_factura_problema.frx`
(errores de tarifa). En `C:\MetroCarSys\Reports`.

## Informes migrados a Blazor (solo lectura)

| Informe | Página / ruta | Estado |
| --- | --- | --- |
| **Resumen de Liquidaciones** | `ResumenLiquidaciones.razor` (`/resumen-liquidaciones`) | ✅ 18/06/2026 — maestro-detalle, filtros (Nº/Tipo/fecha/cliente), comprobante en lectura (`LiquidacionComprobanteDialog`), Excel. Permiso `'F'` |
| **Liquidaciones estimadas** | `FacturacionEstimada.razor` (`/facturacion-estimada`) | ✅ 18/06/2026 — proyección por mes/cliente sobre `liquidacion_detalle` (no re-valoriza), KPIs + ApexCharts + Excel. Permiso `'F'` |
| **Liquidación a Clientes** | `LiquidacionClientes.razor` (`/liquidacion-clientes`) | ✅ 18/06/2026, **rehecho 20/06/2026** — réplica read-only de `facturacion_cliente_nueva.scx`: toolbar "Estado de las reservas" + botón `....`, **árbol cliente→grupo** (2 cajas azules), solapas Servicios/Cliente/Liquidacion. **NO valoriza en vivo** (motor de tarifas sin migrar). Click en una fila de servicios → abre el **Zoom del Viaje** (reusa `ZoomViajeDialog`). Permiso `'F'` |

Métodos en `ReportService`: **`GetViajesPendientesLiquidarAsync`** (árbol POR ESTADO/POR
FECHA, ver abajo) / `GetLiquidacionesAsync` / `GetLiquidacionDetalleAsync` /
**`GetLiquidacionCabeceraAsync`** (cabecera cruda para la solapa Liquidacion) /
`GetFacturacionEstimadaPorMesAsync` / `GetFacturacionEstimadaPorClienteAsync`. Export en
`ExcelExportService`: `ResumenLiquidaciones` / `FacturacionEstimada`.

### "Liquidación a Clientes": el árbol sale de VIAJES, no de liquidaciones (20/06/2026)

⚠️ Corrección de la versión 18/06. El árbol del FoxPro (`bBusca`) NO se arma desde
`liquidacion` (ya grabadas) sino desde **viajes pendientes de liquidar**, agrupados
**cliente → grupo** (`GetViajesPendientesLiquidarAsync`):

- **POR ESTADO** (default del combo, el más usado): `estado_via='FINALIZADO' AND
  f_grupo_fi < HOY` (grupo vencido), **ignora las fechas** → las cajas Desde/Hasta se
  **deshabilitan** en la UI. Trae las empresas reales con saldo a liquidar (AEROLINEAS,
  CONUAR, FURLONG, GATE1, INFORMATION BA, MAPFRE, MSD, SANCOR, TSA, YPF…).
- **POR FECHA**: ídem pero `f_grupo_fi BETWEEN desde AND hasta`.
- **Excluye el cliente de prueba** `parametro.id_cliente` (hoy = **NORTUR**) — por eso
  NORTUR no aparece en el árbol aunque tenga cientos de grupos.

La solapa Servicios lista esos viajes (servicio/cabecera, destino, vehículo, chofer, pax,
km, importe convenido si lo hay) **sin importes calculados** (sin motor de tarifas). La
solapa Liquidacion remite al Resumen de Liquidaciones para ver totales ya grabados.

**Trampa de tipos SQL (`GetViajesPendientesLiquidarAsync`):** en la réplica `viaje.id_viaje`
y `viaje.pax` son **`int`**, no `bigint`; `id_viaje_i` y `km_recorri` SÍ son `bigint`. Leer
un `int` con `SqlDataReader.GetInt64` tira `InvalidCastException` (ADO.NET no convierte
int→long). Solución usada: **`CAST(... AS bigint)` en el SELECT** para forzar el tipo y
desacoplar el getter del tipo subyacente.

### Reconstrucción de la solapa "Liquidacion" desde datos grabados (clave)

`bGraba` guarda en `liquidacion`: **`subtotal` = nSubtotal_ajustado (total NETO de
servicios)**, **`extra` = nExtra_ajustado (ajuste global manual, normalmente 0)**, no el
desglose por servicio. Por eso el visor reconstruye así (verificado contra liq 4560 y 4553):

- **Bloque superior "Totales por servicios"** se reconstruye de `liquidacion_detalle`
  (filas SERVICIO): las filas con `id_adicion='HORA DISPO'` (nombre "HORA A DISPOSICION")
  son los **Extras**; el resto, el **Subtotal del Servicio**; `descuento`/`incremento` por
  fila. `Subtotal Servicio + Extras == liquidacion.subtotal` (cuadra exacto cuando no hay
  desc/incr).
- **Bloque inferior** sale de la cabecera cruda: Subtotal=`subtotal`, Extras=`extra`,
  `Total a Facturar = ROUND((subtotal+extra)·t_cambio)`, `Total General = +iva`,
  `Total Liquidación = +adicional (exento)`. Coincide con `liquidacion.total`.
- **Diálogo cotización** (img 4 «¿la cotizacion es igual a UNO?») → en read-only es un
  `MudAlert` que aparece cuando `t_cambio=1 AND moneda≠PESOS` (la condición que lo disparaba
  en `bGenera`).

### Motor de valorización (`arma_servicio` + `arma_liquidacion`) — ✅ MIGRADO en vivo (22/06/2026)

El motor de tarifas YA está migrado como **cálculo en vivo de solo lectura** (NO graba):
`ReportService.ValorizarGrupoAsync` (precio por viaje, cascada `arma_servicio`) +
`CalcularTotalesLiquidacionAsync` (totales solapa Liquidación, `arma_liquidacion`). Enchufado
en `LiquidacionClientes.razor`: solapa **Servicios** muestra columna Importe por viaje (badge
S/TARIFA si falta precio) y subtotal; solapa **Liquidación** muestra las cajas de totales
idénticas a la pantalla FoxPro (Subtotal/Extras/Desc/Incr/Total/Cambio/IVA/Exento/Total
Liquidación). DTOs `ViajeValorizadoRow` / `LiquidacionTotalesRow`.

**Validado al peso** (GATE): 99,4% de 8.656 viajes históricos + el grupo de la captura
#2890197 (142807.34 / 38057.59 / 180864.93, los tres exactos).

**Trampas resueltas (no tocar sin releer el doc §3.2):**
- modo H duración teórica: el FoxPro suma `minutos_du` como **horas** (`*3600`, bug a replicar).
- horas extra: tarifa = `parametro.cliente_ad` (`'HORA ADICIONAL'`), fracción `fraccion_h`=25.
- `obtiene_tarifa` usa `cliente.id_lista_p`; moneda de `lista_precio_modelo.id_moneda_`.
- modo K: km = `km_recorri` o `servicio.km` si es 0.
- **adicionales:** si `viaje_adicional.precio > 0` se usa **ese**, no la tarifa (fix de
  `GetAdicionalesGrupoAsync` el 22/06; antes daba 31500 vs 38057.59 reales).
- tarifa retroactiva: liquidaciones viejas pueden no cuadrar (su tarifa fue pisada); para
  viajes PENDIENTES con la tarifa de hoy es correcto.

**Pendiente del motor:** servicios 2º/3º, rutas (`id_viaje_i`), ajuste global manual (motivo),
y el **Graba** real — **programado como FASE 5 del plan Buslink**
(`docs/buslink/PLAN_MIGRACION_BUSLINK.md`): `liquidacion`/`liquidacion_detalle` cambian de dueño el
**día D** junto con el circuito `viaje` (NO antes — Facturación escribe `viaje.estado_via`
y `cliente_grupo.f_grupo_fc`, es parte del circuito).

## Informes candidatos pendientes (orden sugerido)

1. **Control pre-liquidación** — viajes FINALIZADOS con grupo vencido sin liquidar +
   errores de tarifa (con el motor ya migrado, ahora se pueden listar los S/TARIFA antes
   de liquidar — la info ya la devuelve `ValorizarGrupoAsync`).
2. **Liquidación a clientes — modo escritura (Graba)** — **Fase 5 del plan Buslink.**
   La valorización en vivo YA está. El Graba: INSERT liquidacion+detalle, UPDATE
   viaje→FACTURADO (todos los tramos si es ruta), cierra el grupo (`f_grupo_fc`), TODO en
   una transacción (FoxPro no la tiene — mejora). Incluye el **Revertir corregido**
   (limpiar `viaje.liquidacio` + reabrir grupo — asimetría del FoxPro que NO se replica) y
   el test de cuadre con las últimas 3 liquidaciones reales.
