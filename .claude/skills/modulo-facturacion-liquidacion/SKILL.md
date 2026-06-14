---
name: modulo-facturacion-liquidacion
description: Conocimiento del módulo Facturación y Liquidación de Metrocar — liquidación a clientes (facturacion_cliente_nueva), a fleteros y a choferes, resumen/revertir/carga de comprobante, tarifarios de venta y de pago (lista_precio, lista_precio_chofer, adicional_lista_precio/pago), cuenta corriente (ctacte, programada pero sin uso), monedas y cotizaciones. Usar SIEMPRE que se trabaje con liquidaciones, facturación, tarifas/precios, tablas liquidacion/liquidacion_detalle/lista_precio*/ctacte*, estados FACTURADO o LIQUIDADO, viaje.liquidacio/liquidaci2, importes convenidos, horas extra, adelantos de choferes, o cuando se construyan informes o ABMs de este módulo en Blazor. Mapa de tablas, flujo de valorización, cascadas de precios y descuentos, y trampas de nombres truncados.
---

# Módulo Facturación y Liquidación — mapa de conocimiento

Es el módulo que **convierte viajes FINALIZADOS en plata**: valoriza contra tarifarios,
arma la liquidación (pre-factura interna), y marca los viajes como FACTURADO (venta) o
LIQUIDADO (pago a fletero). La factura fiscal real se hace FUERA del sistema y se anota
a mano sobre la liquidación.

**Doc detallado (leer ANTES de codear):** `docs/logica-foxpro/FACTURACION_LIQUIDACION.md`
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
| `liquidacion` | 4.197 | PK **`idliquidac`**; `tipo` CLIENTE/PROVEEDOR; `id_cliente` = cliente o `fletero.id_contrat`; `retencion_`=IVA, `retencion2`=IIBB, `retencion3`=GCIA, `retencion4`=SUSS; `total` NULL en PROVEEDOR; factura manual en `tcp/lcp/ncp/fcomp` |
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

## Reportes FoxPro de referencia

`viaje_adicional.frx` + `viaje_adicional_total.frx` (liquidación formato RESUMEN, van
encadenados con FoxyPreviewer), `viaje_personal.frx` (ABIERTO), `viaje_factura_problema.frx`
(errores de tarifa). En `C:\MetroCarSys\Reports`.

## Informes candidatos para Blazor (orden sugerido)

1. **Resumen de Liquidaciones** (browser + detalle) — réplica directa de
   `liquidacion_cliente.scx`, solo lectura, alto valor.
2. **Facturación estimada / proyección** — mejora del `facturacion_cliente_estimada`.
3. **Control pre-liquidación** — viajes FINALIZADOS con grupo vencido sin liquidar +
   errores de tarifa (hoy el usuario los descubre recién al valorizar).
4. **Liquidación a clientes** (escritura) — recién con la regla strangler cumplida.
