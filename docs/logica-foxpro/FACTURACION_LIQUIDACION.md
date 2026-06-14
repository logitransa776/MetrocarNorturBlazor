# Facturación y Liquidación — relevamiento completo del módulo FoxPro

> Relevado el 12/06/2026 desde `C:\MetroCarSys` (menús `.mpr` + forms `.scx` dumpeados con
> `scx_dump.py`) y verificado contra la réplica SQL `replicaVPF` (172.25.69.217).
> Cubre el pad **Facturación** del menú principal, el submenú **ABM del sistema → Facturación**
> y el pad **Cuentas Corrientes** (solo en la variante de menú `MENU_PRINCIPAL_C_CC.MPR`).

---

## 1. Mapa del menú (qué abre qué)

Pad **Facturación** (`MENU_PRINCIPAL.MPR`, visible con letra **"F"** en `cAcceso`):

| Ítem de menú | Form que abre | Qué es |
| --- | --- | --- |
| Clientes → ABM | `cliente.scx` | catálogo (ver `CLIENTE_ABM.md`) |
| Clientes → Clientes Tarifas | `cliente_tarifa.scx` | qué lista de precio usa un cliente por vigencia |
| Clientes → Clientes Descuentos | `cliente_descuento.scx` | desc/incr % por período y tipo de vehículo |
| Clientes → Clientes - Empresa Facturación | `cliente_cambia_empresa_fc.scx` | cambio masivo de `cliente.empresa_fc` |
| Fleteros | `fletero.scx` | catálogo de prestadores externos |
| Grupos | `cliente_grupo.scx` | ver `CLIENTE_GRUPO_ABM.md` |
| Tarifario de Venta → Altas y Copias | `lista_precio_cliente.scx` | crea vigencias / copia tarifarios de venta |
| Tarifario de Venta → Mantenimiento | `lista_precio_cliente_mantenimiento.scx` (+`_abm`) | edita precios uno a uno |
| Tarifario de Venta → Definición | `lista_precio_modelo.scx` (+`_abm`) | cabecera de lista: nombre + moneda |
| Tarifario de Venta → Listadores | `lista_precio_tarifario_imprimir.scx` | impresión |
| Tarifario de Choferes → (los 4 ídem) | `lista_precio_chofer*` / `lista_precio_modelo_chofer*` | tarifario de PAGO a fleteros/choferes |
| Adicionales → Tarifarios Ventas | `adicional_lista_precio.scx` (+`_mantenimiento`) | precio de VENTA de adicionales |
| Adicionales → Tarifarios Pagos | `adicional_lista_pago.scx` (+`_mantenimiento`) | precio de PAGO de adicionales |
| Adicionales → Adicionales / Rubro | `adicional.scx` / `adicional_rubro.scx` | catálogos |
| **Resumen de Liquidaciones** | `liquidacion_cliente.scx` | browser de liquidaciones + revertir + factura |
| **Liquidación a Clientes** | `facturacion_cliente_nueva.scx` | ★ form núcleo de facturación |
| **Liquidación a Fleteros** | `liquidacion_fletero_nueva.scx` | ★ espejo para pagos a fleteros |
| Liquidación a Choferes → Genera | `liquidacion_chofer_por_hora.scx` | prejornal/horas extra choferes propios |
| Liquidación a Choferes → Parámetros | `liquidacion_chofer_por_hora_parametro.scx` | tabla `chofer_parametro_sueldo` |
| Liquidación a Choferes → Adelantos | `chofer_adelanto_abm` / `chofer_adelanto` / `chofer_adelanto_motivo` | vales |
| Liquidaciones estimadas | `facturacion_cliente_estimada.scx` | proyección de venta de un período |

Submenú **ABM del sistema → Facturación** (¡ojo con los nombres de tabla!):

| Ítem | Form | Tabla real que toca |
| --- | --- | --- |
| Empresas para Facturar | `empresa.scx` | `empresa` (1 sola columna `empresa`; hay 2 filas) |
| Tipo de Monedas | `moneda_tipo.scx` (+`_abm`) | `moneda_tipo` |
| Cotizaciones | `moneda_cotizacion.scx` (+`_abm`) | `moneda_cotizacion` (vigencia desde/hasta + valor) |
| Bancos | `ctacte_banco.scx` (+`_abm`) | **`liquidacion_banco`** (no "ctacte_banco") |
| Impuestos sobre Ventas | `ctacte_impuesto.scx` (+`_abm`) | **`liquidacion_impuesto_tasa`** (nombre, tasa, tipo_mov) |
| Tipos de Comprobantes | `ctacte_tipo_comprobante.scx` | **`ctacte_t_comp`** |

Pad **Cuentas Corrientes** (solo en `MENU_PRINCIPAL_C_CC.MPR` — usuarios "con CC"):

| Ítem | Form |
| --- | --- |
| Consultas → Cta. Cte. / Saldo Cliente / Saldos / Tipos Mov. | `ctacte` / `ctacte_saldo_cliente` / `ctacte_saldo` / `ctacte_tipo_movimiento` |
| Ingreso Movimientos | `ctacte_comprobante` |
| Cobranza - Pagos | `ctacte_cobranza` (+ `ctacte_cobranza_cheque_tercero`) |
| Valores a Depositar | `ctacte_cheque_deposito` / `ctacte_cheque_consulta` |
| Imprime Órdenes de Pago | `ctacte_imprime_orden` |
| Mantenimiento | `ctacte_cambia_comprobante` |

Menús contextuales (botón derecho sobre la grilla de servicios):
`MENU_FACTURA_CLIENTE.MPR` y `MENU_FACTURA_FLETERO.MPR` — delegan en métodos del form activo:
Edita Importes, Copiar Cabecera/Servicio, Servicio/Cliente Sin Extras, Errores de tarifa,
Imprime errores, Alta de cabecera/servicio a la Lista de Precio.

---

## 2. ⚠️ Hallazgo central: qué se usa de verdad (datos réplica al 12/06/2026)

| Tabla | Filas | Lectura |
| --- | --- | --- |
| `liquidacion` | 4.197 | **ACTIVA** — CLIENTE 4.184 (hasta 08/06/2026), PROVEEDOR 12, FLETERO 1 (legacy) |
| `liquidacion_detalle` | 819.423 | **ACTIVA** — SERVICIO 618.678 + ADICIONAL 200.745 |
| `viaje` con `liquidacio > 0` | 462.262 | consistente con los 454K FACTURADO |
| `viaje.estado_pag = 'LIQUIDADO'` | 344 | el circuito fletero casi no se usa |
| `ctacte` + TODAS sus satélites | **0** | **el módulo Cta. Cte. está programado pero NO se usa** |
| `liquidacion_fpago` / `liquidacion_impuesto` | 0 / 1 | impuestos y formas de pago: sin uso real |
| `liquidacion_banco` / `liquidacion_impuesto_tasa` | 0 / 1 | catálogos vacíos |
| `lista_precio` / `lista_precio_modelo` | 2.522 / 46 | **ACTIVA** (tarifario venta) |
| `lista_precio_chofer` / `lista_precio_modelo_chofer` | 3.742 / 25 | **ACTIVA** (tarifario pago) |
| `adicional_lista_precio` / `adicional_lista_pago` | 95 / 329 | activas |
| `cliente_tarifa` | 4 | casi sin uso — el 98% de clientes usa `ob_precio='LISTA PRECIO'` (404 vs 9 'CLIENTE') |
| `cliente_descuento` | 0 | sin uso |
| `chofer_adelanto` / `chofer_parametro_sueldo` | 375 / 177 | activas (vales y parámetros de sueldo) |
| `moneda_tipo` / `moneda_cotizacion` / `empresa` | 3 / 32 / 2 | catálogos chicos activos |

**Conclusión de negocio:** NORTUR usa Metrocar para **valorizar y liquidar** (pre-facturación);
la factura fiscal y la cobranza viven fuera del sistema (el comprobante se anota a mano sobre
la liquidación). La moneda dominante es **USS** con `t_cambio` manual (ej.: liq #4560:
subtotal 1.198 USS × ~1.550 = total 1.857.080 $). El IVA casi siempre queda en 0.

---

## 3. El flujo central: Liquidación a Clientes (`facturacion_cliente_nueva.scx`)

Form MDI con árbol (TreeView OCX) a la izquierda y 4 pestañas: **Servicios / Adicionales /
Cliente / Liquidación**. Variables públicas para el reporte. Cursores temporales:
`viaje_factura` (28 columnas), `viaje_factura_adicional`, `viaje_cabecera`, `viaje_detalle`.

### 3.1 Búsqueda (`bBusca`)

1. Modo `reserva_estado`: **POR ESTADO** (default) o **POR FECHA** (rango `f_reserva`).
2. `SELECT * FROM viaje WHERE estado_viaje = "FINALIZADO"` → depura:
   - excluye cliente de prueba (`parametro.id_cliente` → `cIdClientePrueba`),
   - excluye `TTOD(hs_fin_ruta) > hFechaFac`,
   - POR ESTADO: exige **`f_grupo_fin < HOY`** (el grupo tiene que estar vencido);
     POR FECHA: `BETWEEN(f_grupo_fin, desde, hasta)`.
3. Arma árbol cliente → grupos (`tmpGrupoFc`). Clic en nodo padre = facturar todo el
   cliente; clic en hijo = solo ese grupo.

> Regla clave: **solo se facturan viajes FINALIZADOS cuyo grupo ya terminó**. Es el
> candado temporal que evita facturar un grupo a medio correr.

### 3.2 Valorización (`arma_servicio` — el motor de tarifas)

Por cada viaje del cliente/grupo (si `id_viaje_int > 0` es una RUTA: toma el **último**
tramo y usa `hs_ini_ruta`/`hs_fin_ruta`; la ruta vale como un solo servicio):

```
ORDEN DE PRECEDENCIA DEL PRECIO (cascada, gana el primero):
1. viaje.importe_convenido > 0   → precio fijado a mano en la reserva/zoom
                                    (moneda = moneda_convenida; aplica descuento_convenido %)
2. viaje.sin_cargo               → importe 0, moneda PESOS
3. Tarifario:
   a. Si viaje.cabecera ≠ vacío Y cliente factura por cabecera (fc_preferencia='C')
      → obtiene_tarifa(cabecera) con tipo "C"
   b. Si no → por cada servicio cargado (id_servicio, id_servicio1, id_servicio2):
      → obtiene_tarifa(servicio) según servicio.modo_fac:
          S = servicio cerrado (precio directo)        [16 servicios]
          H = por horas (suma horas_duracion teóricas) [39 servicios]
          K = por km    (precio × km;  modo_km V=km del viaje / S=km del servicio)  [6]
```

`obtiene_tarifa(cliente, ob_precio, lista, servicio, vehiculo, fecha, tipo)`:

```
1. Si cliente.ob_precio = "CLIENTE" → busca cliente_tarifa vigente → saca id_lista_precio
   (si no hay → error "Cliente no tiene definida lista de precio", ckCliente)
2. lista_precio_modelo → moneda de la lista
3. lista_precio: filtra en cascada y reporta el PRIMER nivel que falla:
   - sin filas para la lista            → ckCliente
   - sin vigencia para la fecha         → ckVigencia
   - sin el servicio en esa vigencia    → ckServicio
   - sin el tipo de vehículo            → ckVehiculo
4. Devuelve precio, o -1 si falló (el viaje queda en la grilla con importe -1,
   pintado AMARILLO, y el detalle del error se acumula en thisform.errorProceso)
```

**Horas extra** (solo si algún servicio del viaje es modo H):
si `duración real > duración teórica`, la diferencia se valoriza con el servicio especial
`parametro.cliente_adicional` (columna SQL `cliente_ad`; es el código tipo "HORA DISPO"):

```
extra = horas_enteras × tarifa_hora_extra
minutos > parametro.fraccion_hora ?  entre fracción y 30' → +media hora
                                     más de 30'           → +hora completa
```

**Descuentos/incrementos por servicio** (cascada, gana el primero):
`viaje.descuento_convenido` % → `cliente.descuento` % → `cliente.incremento` % →
`cliente_descuento` vigente por período/vehículo (tipo D o I). *(en fleteros la cascada
es igual pero SIN el último nivel)*.

**Adicionales** (`obtiene_adicional`): por cada `viaje_adicional` del viaje:
- rubro vacío → estado `FTA RUBRO` (precio -1, bloquea)
- rubro en `cliente_adicional_excluido` → `EXCLUIDO` (precio 0, no se cobra)
- si no → `ABONA`: usa `viaje_adicional.precio` si > 0, sino busca en
  `adicional_lista_precio` (adicional × tipo de vehículo × vigencia). Sin precio → -1.

### 3.3 Pestaña Liquidación (`arma_liquidacion`)

```
subtotal    = Σ imp_serv_1+2+3          extra = Σ imp_serv_extra
descuento   = Σ imp_desc_1+2+3          incremento = Σ imp_incr_1+2+3
total       = subtotal + extra + incremento - descuento
```

Ajuste manual global (4 campos **mutuamente excluyentes**: cargar uno deshabilita los
otros 3): `porc_descuento`, `porc_incremento`, `imp_descuento`, `imp_incremento` —
**motivo obligatorio** si hay ajuste. Moneda: PESOS → `tipo_cambio = 1` bloqueado;
USS/USD/EURO → tipo de cambio manual editable (advierte si queda en 1).

```
total_final = total_ajustado × tipo_cambio        (pesifica)
iva         = total_final × pIva/100              (pIva default = parametro.piva)
total_liq   = total_final + iva + adicionales     (adicionales NO llevan IVA = "exentos")
```

### 3.4 Genera Resumen (`bGenera`) — validaciones + reporte

Bloquea si: hay checkboxes de error de tarifa prendidos / adicionales con precio < 0 /
total ≤ 0 / ajuste sin motivo. Arma `viaje_cabecera` + `viaje_detalle` (una fila por
servicio con nombre legible, la hora extra entra como servicio "HORA A DISPOSICION",
los adicionales como tipo ADICIONAL en PESOS). Tres formatos de salida:

| Formato | Reporte .frx |
| --- | --- |
| RESUMEN (default) | `viaje_adicional.frx` + `viaje_adicional_total.frx` (vía FoxyPreviewer, 2 reportes encadenados) |
| ABIERTO | `viaje_personal.frx` |
| ESPECIAL | `viaje.frx` (rama muerta: el combo nunca carga "ESPECIAL") |

Además: PDF (FoxyPreviewer OBJECT TYPE 10), Excel (`Exp2Excel`), e "Imprime errores"
(`viaje_factura_problema.frx` con los `imp_serv_1 <= 0`).

### 3.5 Graba (`bGraba`) — la escritura ★

Confirma con *"¡ Este proceso no tiene posibilidad de revertir !"* y ejecuta
**sin transacción** (orden real del código):

```sql
1. INSERT INTO liquidacion ( fecha=HOY, tipo='CLIENTE', id_cliente, moneda, subtotal,
     extra, t_cambio, adicional, motivo, piva, iva,
     total = ROUND((subtotal+extra)*t_cambio + iva + adicional, 2) )
   → nIdLiquidacion = GETAUTOINCVALUE()

2. INSERT INTO liquidacion_detalle (una fila por servicio/adicional de cada viaje:
     id_viaje, id_viaje_int, idLiquidacion, tipo SERVICIO|ADICIONAL, id_adicional,
     nombre, moneda, cantidad, precio, importe, descuento, incremento,
     d_destino_prov, km_recorrido)

3. UPDATE cliente_grupo SET f_grupo_fc = HOY, liquidacion = nId   -- CIERRA el grupo
     WHERE nombre = grupo AND id_cliente = cliente                 -- (candado de Reservas)

4. UPDATE viaje SET estado_viaje = 'FACTURADO', liquidacion = nId
     WHERE id_viaje_int = X   (toda la ruta junta)   -- si ruta
     WHERE id_viaje = X                              -- si viaje suelto
```

> El `motivo` del ajuste y el % de descuento manual **no se prorratean por viaje**: el
> ajuste global vive solo en la cabecera `liquidacion`. El detalle guarda los importes
> SIN el ajuste global. Cualquier reconstrucción del total desde el detalle debe sumar
> cabecera, no detalle.

### 3.6 Subdialogs y menú contextual

| Form | Qué hace |
| --- | --- |
| `facturacion_cliente_nueva_edita` | edita importes de UNA fila de `viaje_factura` (cursor, no toca `viaje`) |
| `facturacion_cliente_nueva_descuento` | ajuste global (recibe el total como parámetro) |
| `facturacion_cliente_nueva_vario_grupo` | selección múltiple de grupos (`tmpRzSc` en `depura_servicio`) |
| Doble clic en grilla | abre `trafico_zoom` en MODIFICA sobre ese viaje y revaloriza todo al volver |
| "Pegar Cabecera" (contextual) | ⚠️ **escribe en la base**: `UPDATE viaje SET cabecera = ...` |
| "Alta de servicio cabecera a la Lista de Precio" | si `imp_serv_1 = -1` y cliente con lista: `INSERT INTO lista_precio (..., precio=1, tipo='C')` en la vigencia activa — alta "en 1 peso" para normalizar después |

---

## 4. Liquidación a Fleteros (`liquidacion_fletero_nueva.scx`)

Espejo del form de clientes con estas diferencias:

| Aspecto | Cliente | Fletero |
| --- | --- | --- |
| Universo de viajes | `estado_viaje = 'FINALIZADO'` | `FINALIZADO` **o** `FACTURADO` (se le paga aunque ya se facturó al cliente) |
| Filtro adicional | — | `viaje.fletero = id_contratado` y **`estado_pago` vacío** |
| Precio convenido | `importe_convenido`/`moneda_convenida` | **`importe_pago` / `moneda_pago`** (campos espejo de pago en `viaje`) |
| Tarifario | `lista_precio` (modelo: `lista_precio_modelo`) | **`lista_precio_chofer`** (modelo: `lista_precio_modelo_chofer`), vía `fletero.id_lista_precio` |
| Adicionales | `adicional_lista_precio` + exclusiones `cliente_adicional_excluido` | **`adicional_lista_pago`** + exclusiones `vehiculo_contratado_adicional_excluido` |
| Cascada desc. | convenido > cliente.desc > cliente.incr > período | ídem **sin** nivel "período" |
| Graba: cabecera | `liquidacion.tipo = 'CLIENTE'` (con `total`) | `liquidacion.tipo = 'PROVEEDOR'`, `id_cliente = fletero.id_contratado` (**no graba `total`**) |
| Graba: viaje | `estado_viaje='FACTURADO'`, `liquidacion` | **`estado_pago='LIQUIDADO'`, `liquidacion_pago`** (no toca `estado_viaje`) |
| Graba: grupo | cierra `cliente_grupo.f_grupo_fc` | NO toca el grupo (código comentado) |

> En `liquidacion.tipo` conviven 3 valores históricos: `CLIENTE`, `PROVEEDOR` y un
> `FLETERO` viejo (1 fila de 2019). El Resumen junta PROVEEDOR con la tabla `fletero`
> por `id_contratado`.

---

## 5. Resumen de Liquidaciones (`liquidacion_cliente.scx`)

Browser maestro: filtra por Nº exacto **o** tipo (CLIENTE/PROVEEDOR) + rango fecha +
cliente. Grilla superior = cabeceras (`liquidacion` × `cliente`|`fletero`); grilla
inferior = `liquidacion_detalle` de la fila seleccionada. Columna calculada
`factura = tcp-lcp-ncp` si hay comprobante.

| Botón | Lógica |
| --- | --- |
| **Revertir** | habilitado **solo si la liquidación NO tiene factura asignada**. `DELETE FROM liquidacion / liquidacion_detalle / liquidacion_fpago / liquidacion_impuesto WHERE idLiquidacion = n` y pregunta si revivir los viajes: CLIENTE → `UPDATE viaje SET estado_viaje='FINALIZADO' WHERE liquidacion=n`; PROVEEDOR → `SET estado_pago='' WHERE liquidacion_pago=n`. ⚠️ NO limpia `viaje.liquidacion`/`liquidacion_pago` ni reabre `cliente_grupo.f_grupo_fc`. |
| **Factura** | abre `liquidacion_cliente_carga_comprobante` |
| **Excel** | exporta la grilla |

### `liquidacion_cliente_carga_comprobante` — anotar factura y pago

Dialog "Impuestos y Forma de Pago": tipo (FAC/NDB/NCR/REC) + letra (A/B/C/E/X) + fecha +
sucursal(4)+número(8) → `ncp` de 12. Pago: fecha, forma (EFECTIVO/TRANSFERENCIA/CHEQUE
TERCERO), banco (catálogo `liquidacion_banco`), nº, retenciones IVA/IIBB/SUSS,
`totalPago = totalGeneral - retenciones`. Valida pago ≤ total. Graba **un solo UPDATE**
sobre `liquidacion` (fcomp, tcp, lcp, ncp, f_pago, banco, n_pago, forma_pago,
retencion_iva/iibb/suss, pago). **No genera asiento en ctacte.**

*(Existe un form hermano `liquidacion_cliente_comprobante` — versión vieja que grababa
`liquidacion_impuesto` por tasa y `liquidacion_fpago` por renglón; hoy sin uso real.)*

---

## 6. Liquidación a Choferes por hora (`liquidacion_chofer_por_hora.scx`)

**Herramienta de cálculo de nómina — NO escribe en la base** (sale por Excel/reporte).

1. Parámetros del mes en pantalla (persistidos en `chofer_parametro_sueldo`: mes, año,
   bruto, antigüedad, valor hora extra BUS/MINIBUS/SPRINTER, francos/mes, $ franco,
   jornal en hs).
2. Universo: viajes FINALIZADO|FACTURADO del período, filtro `tipo_chofer`
   (PROPIO/CONTRATADO/TODOS). Si hay `id_chofer2` (doble conducción) **duplica el viaje**
   para el 2º chofer y parte maletas/propinas al 50%.
3. Por chofer × día arma jornada (primer inicio → último fin, vehículo = el del primer
   viaje del día): `hs_extra = hs_jornada - jornal`; minutos: hasta 30' = media hora,
   más = hora completa. Valor hora según tipo de unidad (BUS/MINI/TRAFIC).
4. Maletas: `viaje_adicional` con `parametro.adic_maleta`, valorizadas por
   `adicional_lista_precio`; propinas: adicional "PROPINA" a precio cargado. **Si falta
   precio ABORTA todo el proceso** (messagebox + return).
5. Cruza `chofer_franco` por código: FT (franco tomado — los no tomados se pagan:
   `(francos_mes - tomados) × $franco`), E, V, MT, PL, LC, LSG, OT.
6. Vales: `chofer_adelanto` del período (tipo_mov "D" resta, otro suma).
7. Antigüedad: `años = (hasta - f_ingreso)/360`, `importe = $antigüedad × años`.
8. Salida: resumen por chofer (bruto + antigüedad + extras + francos no tomados +
   maletas + propinas + vales) o abierto por día. Botón "doble monta" lista unidades
   con 2 choferes el mismo día.

---

## 7. Liquidaciones estimadas (`facturacion_cliente_estimada.scx`)

Proyección de venta: toma TODOS los viajes `origen='T'` del rango (sin importar estado),
los valoriza contra `lista_precio` vigente (sin horas extra ni adicionales) y agrupa por
mes. Convierte con `moneda_cotizacion`. Solo lectura/Excel.

---

## 8. Cuenta Corriente (programada, **sin uso en producción** — 0 filas en todo)

Modelo contable completo listo para activar. Conceptos:

- **`ctacte`**: un movimiento por comprobante. `tipo` CLIENTE|PROVEEDOR + `id_cliente`.
  `t_mov`: **A = aumenta deuda (debe) / D = disminuye (haber)**. Importes:
  `imp_ori_grav/no_grav` (moneda origen) × `t_cambio` → `imp_grav/no_grav`,
  `impuesto`, `total`, y **`saldo`** (saldo pendiente del comprobante, se va cancelando).
  `idLiquidacion` linkea con la liquidación de origen.
- **`ctacte_t_comp`** (catálogo "Tipos de Comprobantes"): por `tcp` define `tipo_mov`
  (A/D), `afecta` (S/N: puede imputarse contra otros comprobantes), `mod_saldo`
  (S/N: baja el saldo de lo afectado), `tipo_nro` (A = numeración automática con
  `ult_nro`), `aplicativo` (C = clientes / P = proveedores).
- **`ctacte_afectacion`**: imputación comprobante→comprobante (recibo X cancela factura Y).
  Cada cancelación hace `UPDATE ctacte SET saldo = saldo - cancela` sobre el afectado
  (salvo ND con `afecta=S, mod_saldo=N`). Todo comprobante "no imputable" se inserta
  auto-afectado como cabecera.
- **`ctacte_detalle`**: impuestos del movimiento por tasa (`liquidacion_impuesto_tasa`).
- **`ctacte_pago`**: formas de pago del movimiento (EFECTIVO/TRANSFERENCIA/CHEQUE
  PROPIO/CHEQUE TERCERO). Cheques: cartera = filas con `fDeposito` vacío; el depósito
  (`ctacte_cheque_deposito`) setea `fDeposito` + `destino` (banco).
- **`ctacte_comprobante`** ("Ingreso Movimientos"): alta manual; puede llamarse con una
  liquidación → precarga importes y motivo "LIQUIDACION Nº x", y al grabar devuelve el
  número a `liquidacion.tcp/lcp/ncp`. Valida comprobante duplicado por
  (tipo, cliente, tcp, lcp, ncp).
- **`ctacte_cobranza`**: recibo con imputación multi-factura (grilla de deuda con
  checkbox + importe a cancelar), impuestos tipo_mov="R" y formas de pago múltiples.
- Consultas: `ctacte` (mayor con saldo corrido D/H), `ctacte_saldo_cliente`
  (debe/haber/saldo por cliente), `ctacte_saldo` (saldo + vencido + por vencer).

> Para la migración: este circuito es la referencia funcional si NORTUR pide cuenta
> corriente en el sistema nuevo, pero **no hay datos legacy que migrar**.

---

## 9. Tarifarios — funcionamiento común

Estructura idéntica para venta (`lista_precio*`) y choferes (`lista_precio_chofer*`):

- **Modelo** (`lista_precio_modelo[_chofer]`): `id_lista_precio` + nombre + **moneda**
  (la moneda vive en el modelo, no en el precio). Baja lógica `f_delete`.
- **Matriz** (`lista_precio[_chofer]`): PK lógica = lista × servicio × tipo de vehículo ×
  vigencia (`f_vigencia_desde`/`hasta`). `precio` + `tipo` (**S** = servicio,
  **C** = cabecera — las cabeceras de recorrido se tarifan como si fueran servicios).
- **Altas y Copias** (`lista_precio_cliente.scx`): genera una vigencia nueva en blanco
  (cartesiano servicios × tipos de vehículo en 0) o **copia** otra vigencia/lista con
  % de aumento. ⚠️ Trampa de código: este form tiene queries de control que apuntan a
  `lista_precio_chofer` (copy-paste del form de choferes) aunque el INSERT final va a
  `lista_precio` — verificar contra el exe productivo antes de replicar la lógica.
- **Mantenimiento** (`*_mantenimiento` + `_abm`): edición de precios fila a fila.
- Adicionales: mismo patrón pero SIN modelo: `adicional_lista_precio` /
  `adicional_lista_pago` = adicional × `id_vehiculo` (tipo) × vigencia
  (`fDesdeVg`/`fHastaVg`) → precio.

---

## 10. Tablas y columnas REALES en la réplica SQL (truncadas a 10 chars)

| Tabla | Columnas trampa |
| --- | --- |
| `liquidacion` | **`idliquidac`** (PK autoinc), `tipo`, `fecha`, `id_cliente` (cliente O fletero), `moneda`, `subtotal`, `extra`, `t_cambio`, `adicional` (=exentos), `motivo`, `fcomp`, `tcp`/`lcp`/`ncp` (factura), `f_pago`, `forma_pago`, `banco`, `n_pago`, **`retencion_` = ret. IVA, `retencion2` = IIBB, `retencion3` = GCIA, `retencion4` = SUSS** (por orden de creación; verificar con datos), `pago`, `iva`, `piva`, `total` (NULL en tipo PROVEEDOR) |
| `liquidacion_detalle` | `idliquidac`, `id_viaje`, **`id_viaje_i`** (ruta), `tipo` (SERVICIO/ADICIONAL), **`id_adicion`** (código servicio o adicional), `nombre`, `moneda`, `cantidad`, `precio`, `importe`, `descuento`, `incremento`, **`d_destino_`** (provincia), **`km_recorri`** |
| `lista_precio` / `lista_precio_chofer` | **`id_lista_p`**, **`id_servici`**, **`id_vehicul`** (= tipo de vehículo), **`f_vigencia`** (desde), **`f_vigenci2`** (hasta), `precio`, `tipo` (S/C) |
| `lista_precio_modelo[_chofer]` | `id_lista_p`, `nombre`, **`id_moneda_`**, `f_create/f_delete/f_modify` |
| `cliente_tarifa` | `id_cliente`, `id_lista_p`, `dvigencia`/`hvigencia`, `obs` |
| `cliente_descuento` | (vacía) id_cliente, id_lista_p, dVigencia/hVigencia, id_vehiculo_tipo, porcentaje, tipo_mov D/I |
| `adicional_lista_precio` / `_pago` | **`id_adicion`**, **`id_vehicul`**, `fdesdevg`/`fhastavg`, `precio` |
| `chofer_adelanto` | `id_chofer`, **`nombre_cho`**, **`tipo_chofe`**, `fecha`, `importe`, `motivo`, `tipo_mov` (D resta) |
| `chofer_parametro_sueldo` | `mes`, `ano`, `bruto`, `antiguedad`, **`hs_extra_b`/`hs_extra_m`/`hs_extra_s`** (bus/mini/sprinter), `franco`, `imp_franco`, `jornal` |
| `fletero` | **`id_contrat`** (clave usada por liquidacion/viaje), `nombre`, `razon_soci`, **`id_lista_p`** (tarifario chofer), `id_lista_2`, `modo_liq`, `fc_prefere`, `cuit`, `f_delete` |
| `moneda_tipo` / `moneda_cotizacion` | `id_moneda`, `cotizacion`; cotización por vigencia `dinicio`/`dfin` |
| `viaje` (campos de facturación) | **`liquidacio`** = liquidación cliente, **`liquidaci2`** = liquidación pago fletero, `estado_via`, **`estado_pag`** ('' / LIQUIDADO), `f_grupo_fi`, **`importe_co`**/`moneda_con`/**`descuento_`** (convenido venta), **`importe_pa`**/`moneda_pag`/`sin_cargo`/`sin_cargo_` (pago), `fletero`, `cabecera`, `km`/`km_recorri`, `id_chofer2`, `tipo_chofe` |
| `servicio` (campos de facturación) | **`modo_fac`** (S/H/K), **`modo_km`** (V/S), **`horas_dura`**/`minutos_du`, `km`, `modo_liq`, `modo_uso` |
| `cliente` (campos de facturación) | **`ob_precio`** (LISTA PRECIO=404 / CLIENTE=9), `id_lista_p`, **`fc_prefere`** (C=cabecera 26 / S=servicio 355 / ''=32), `empresa_fc`, `descuento`, `incremento` |
| `parametro` (campos de facturación) | **`cliente_ad`** (servicio hora extra), **`fraccion_h`**, **`adic_malet`**, `id_cliente` (cliente prueba), `ley_liq_1`/`ley_liq_2` (leyendas reporte), **`piva`**, `logo` |
| `ctacte` y satélites | `ctacte`, `ctacte_afectacion`, `ctacte_detalle`, `ctacte_pago`, `ctacte_t_comp` — **todas vacías** |
| `empresa` | una sola columna `empresa` (2 filas) — sin PK, sin baja lógica |

---

## 11. Forms legacy / no referenciados por el menú (NO migrar)

`facturacion_cliente.scx`, `facturacion_cliente_v2`, `facturacion_cliente_arsa`,
`facturacion_cliente_view`, `facturacion_cliente_modifica_v2`,
`facturacion_cliente_grupo_modifica`, `facturacion_cliente_cotizacion`,
`liquidacion_chofer_mixto(_v2)`, `liquidacion_chofer_modifica`,
`liquidacion_chofer_por_hora_bk`, `liquidacion_resumen`, `liquidacion_resumen_chofer`
(estos dos últimos eran el resumen viejo). Ante la duda, regla del proyecto: el exe
productivo manda — verificar con el usuario qué pantalla ve.

---

## 12. Reglas de oro para la migración a Blazor

1. **Liquidar ≠ facturar fiscalmente.** La "liquidación" es la pre-factura interna; el
   comprobante AFIP se anota a mano después. No asumir correlatividad ni validez fiscal
   de `tcp/lcp/ncp`.
2. La pareja **(estado_viaje=FACTURADO, viaje.liquidacio)** es el vínculo viaje→liquidación
   cliente; **(estado_pag=LIQUIDADO, viaje.liquidaci2)** es el de pago a fletero. Son
   independientes entre sí.
3. **El grabado cierra el grupo** (`cliente_grupo.f_grupo_fc = HOY`): tocar liquidación
   implica coordinar con el módulo Reservas (candado documentado en `CLIENTE_GRUPO_ABM.md`).
4. El **revertir** del FoxPro deja residuos (no limpia `viaje.liquidacio` ni reabre el
   grupo): si se migra, corregir esa asimetría.
5. La cascada de precios (convenido → sin cargo → cabecera → servicios × modo_fac) y la
   cascada de descuentos son **el corazón del negocio** — replicarlas exactamente y
   testearlas contra liquidaciones históricas (hay 819K filas de detalle para validar).
6. Los importes del detalle NO incluyen el ajuste global de cabecera (motivo + % manual).
7. Proceso original **sin transacción**: en Blazor envolver INSERT cabecera + detalle +
   UPDATE grupo + UPDATE viajes en una transacción única.
8. Mientras rija el strangler (skill `abm-metrocar`): `liquidacion`, `liquidacion_detalle`,
   `viaje`, `cliente_grupo`, `lista_precio*` son tablas con dueño FoxPro → **solo lectura**
   desde Blazor hasta migrar el ABM completo con su puente inverso.
