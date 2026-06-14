# Módulo Combustible — relevamiento completo del FoxPro

> Relevado el 12/06/2026 leyendo los fuentes de `C:\MetroCarSys` (menús `.mpr`, forms `.scx/.sct`,
> reportes `.frx`) y los datos vivos de los DBF (`C:\MetroCarSys\Nortur`) + réplica SQL `replicaVPF`.
> Forms dumpeados con `scx_dump.py` (skill `foxpro-extract`).

---

## 0. El hallazgo más importante: el módulo está VIVO

> **Actualización 12/06/2026 — análisis de performance de `replicaVPF` (servidor 172.25.69.217):**
> `vehiculo_sobre` **SÍ está replicada en el servidor nuevo** con 109.624 filas y cargas hasta
> el 08/06/2026 (3.730 en 2026). El paso 0 del módulo ya está cumplido en este servidor.
> El gap documentado abajo era válido para el servidor viejo (DESKTOP-CV6LF0O).

El módulo registra **~8.000 cargas de combustible por año, de forma ininterrumpida desde 2013
hasta hoy** (3.730 cargas en 2026 al 08/06/2026). Vive en la tabla **`vehiculo_sobre`**
(109.624 registros en el servidor nuevo).

Lo que sí está replicado en ambos servidores es la tabla **vieja** `vehiculo_combustible`
(36.142 filas), congelada en 2016. Por eso un análisis hecho solo sobre el servidor viejo
concluía —erróneamente— que el módulo está muerto.

| Tabla | Registros | Última actividad | ¿En réplica SQL 172.25.69.217? |
| --- | --- | --- | --- |
| `vehiculo_sobre` | 109.624 | **08/06/2026** | ✅ **SÍ (confirmado 12/06/2026)** |
| `vehiculo_combustible` | 36.142 SQL | 2016 (restos 2018) | ✅ |

**Consecuencia para Blazor:** los informes de combustible se pueden construir **ya** desde
`replicaVPF` (servidor 172.25.69.217). Atención: filtrar `f_carga BETWEEN '2009-01-01' AND '2027-12-31'`
porque existen registros con año corrupto (máximo sin filtro = 4202).

### Las dos eras del módulo

```
ERA 1 (2009–2013, restos hasta 2018) — tabla vehiculo_combustible
  Control administrativo por FACTURA/REMITO de la estación.
  Forms: vehiculo_combustible(_3), _mant(_abm), _carga, _carga_importa (Excel ESSO),
         _prorrateo, _saldo_fc. TODOS legacy, ya no alcanzables desde el menú.

ERA 2 (2013 → HOY) — tabla vehiculo_sobre
  Control por SOBRE/LOTE: las cargas se concilian agrupándolas en lotes numerados
  (numerador global parametro.lote_sobre, hoy = 1767).
  2013–2017: además control de saldos por estación (depósitos vs consumos valorizados).
  2018+    : pago con TARJETA PREPAGO (YPF en Ruta) → el control de saldos/precios
             quedó sin uso, pero la CARGA y la CONCILIACIÓN POR LOTE siguen activas.
```

Datos reales de `vehiculo_sobre` (12/06/2026):

- Años: 2013: 6.533 · 2014: 8.813 · 2015: 8.644 · 2016: 8.297 · 2017: 7.735 · 2018: 8.899 ·
  2019: 9.502 · 2020: 7.016 (pandemia) · 2021: 7.703 · 2022: 7.865 · 2023: 8.657 ·
  2024: 8.230 · 2025: 7.994 · 2026: 3.064
- `tipo_carga`: EURO-DIESEL 62.437 · DIESEL 500 46.467 · SUPER 32 · GAS-OIL 20
- `f_pago`: TARJETA PREPAGO 93.373 · CUENTA CORRIENTE 11.083 · ESSO CARD 4.179 · AUDITOR 276
- Sin conciliar (`n_sobre = 0`): 30.853 cargas

---

## 1. Menú Combustible (pad con permiso `"M" $ cAcceso`)

Idéntico en `MENU_PRINCIPAL.MPR` y `MENU_PRINCIPAL_C_CC.MPR`. La letra **M** del campo
`usuario.acceso` habilita el pad (confirma la skill `seguridad-nortur`: M = Combustible).

| # | Ítem | Form | Estado |
| --- | --- | --- | --- |
| 1 | Promedio de Consumos | `vehiculo_combustible_consumo` | ACTIVO |
| 3 | ABM y Conciliación cargas | `vehiculo_combustible_mant_sobre_lote` (modal) | ACTIVO — corazón del módulo |
| 5 | Saldos de Estaciones | `vehiculo_estacion_saldo` | sin uso desde 2017 |
| 6a | Depósitos → Carga de Depósitos | `vehiculo_estacion_saldo_carga` | sin uso desde 2017 |
| 6b | Depósitos → Mantenimiento de Depósitos | `vehiculo_estacion_saldo_mant` | sin uso desde 2017 |
| 8 | Estaciones | `estacion` → `estacion_abm` | ACTIVO (catálogo) |
| 9 | Rubro de Consumos | `estacion_rubro` → `_abm` | catálogo chico |
| 10 | Artículos por Rubro de Consumo | `estacion_rubro_articulo` → `_abm` | catálogo chico |

**Entradas extra desde Tráfico** (sin pasar por el pad Combustible):

- Planilla de Tráfico (`trafico2`/`trafico3`), menú contextual del vehículo
  (`MENU_VIAJE_VEHICULO.MPR` ítem "Carga de combustible") → `carga_combustible()` →
  `DO FORM vehiculo_combustible_carga_sobre_trafico WITH "trafico", 0, id_vehiculo, interno,
  nombre_chofer, id_chofer` — alta rápida con vehículo y chofer prellenados.
- Botón en la planilla → `trafico_vehiculo_combustible` — panel de control "días sin cargar".

---

## 2. ABM y Conciliación de cargas — `vehiculo_combustible_mant_sobre_lote`

Caption "Cargas de Combustibles". Es el browser + conciliador. Pantalla maximizada con grid
de 19 columnas sobre el cursor `cursorVehiculoCombustible`.

### Filtros (`bFiltro.Click`)

Combo `Filtrar por`: **TODOS / DOMINIO / LOTE / ESTACION**, más rubro + artículo
(combo dependiente `estacion_rubro_articulo WHERE idRubro`) y rango de fechas.

```foxpro
SELECT a.f_carga, a.hora, a.estacion_nombre, a.tipo_carga, a.dominio, a.interno,
       a.odometro, a.lleno, a.litros, a.importe, a.n_sobre, a.f_pago, a.chofer,
       a.idRubro, b.rubro, a.Id, a.estacion
  FROM vehiculo_sobre a INNER JOIN estacion_rubro b ON a.idRubro = b.Id
 WHERE &cBusca ORDER BY f_carga, dominio
```

- Filtro LOTE ignora las fechas (`cBusca = "n_sobre = nLote_sobre"`).
- **Filas amarillas** = ya conciliadas: `dynamicbackcolor IIF(n_sobre # 0, amarillo, blanco)`.
- Totales de litros e importe del resultado (`SUM importe, litros`).
- Orden por click en header de columna (indexa el cursor on-the-fly).

### Conciliación por sobre/lote (los 6 botones del panel inferior)

El "sobre" es el sobre físico con los tickets/remitos del período que la estación le entrega
a administración; el sistema le asigna un número de lote global:

1. **Nuevo** (`bSobre`): `parametro.lote_sobre + 1` → `UPDATE parametro SET lote_sobre = nUlt_lote`
   (numerador global consumido aunque después se cancele — quedan huecos).
2. **Agregar** (`bAgregarSobre`): permite tipear un número de lote ya existente
   (valida `SELECT * FROM vehiculo_sobre WHERE n_sobre = X`, si no existe rechaza).
3. **Marca / Desm**: `UPDATE vehiculo_sobre SET n_sobre = nLote (o 0) WHERE Id = nId_registro`
   — fila por fila, sobre el registro posicionado del grid.
4. **Marca Todo / Desm Todo**: el mismo UPDATE en loop sobre todo el cursor filtrado.
5. Tras cada marca recalcula litros/importe del lote (`SUM ... FOR n_sobre = nLote`).
6. **Cancela**: solo resetea la UI (el número de lote consumido no se devuelve).

### Alta / Baja / Modificación

Delegan en `DO FORM vehiculo_combustible_carga_sobre WITH "alta"|"baja"|"modifica", id`
(ver §3). El doble click del grid = Modificar.

### Otros botones

- **Imprimir/Pantalla**: `REPORT FORM vehiculo_combustible` (.frx).
- **Exportar a Excel**: `COPY TO ... XLS`, pregunta si incluye columnas de precio
  (mismo patrón de ocultar precios que el resto del sistema — permiso F).
- Init con parámetros (lo llama el tablero de control de vehículos): modo consulta
  bloqueado a un dominio + rango.

---

## 3. Alta de carga — `vehiculo_combustible_carga_sobre` (+ variante `_trafico`)

Dialog modal. Modos por parámetro: `alta`, `trafico`, `baja`, `modifica`, `consulta`.

### Campos

Interno (valida `vehiculo WHERE interno = X AND activo`), Dominio (valida contra `vehiculo`,
trae `interno` y `litro_tanque`), Rubro (combo `estacion_rubro`, default combustible),
Estación (combo `estacion WHERE rubro = idRubro`), Tipo de combustible (combo
`estacion_rubro_articulo WHERE idRubro` — por eso los "artículos" son DIESEL 500 y
EURO-DIESEL), Fecha + Hora (default ahora), Chofer (combo `chofer WHERE Empty(f_delete)`)
o check "Sin chofer", Odómetro, Litros (+ `litro_2` si check `dos_carga`), check Lleno,
Forma de pago, Importe.

Listas de forma de pago **distintas según la variante**:

- `carga_sobre` (desde el ABM): CUENTA CORRIENTE, EFECTIVO, TARJETA CREDITO, TARJETA DEBITO,
  YPF EN RUTA, AXION CARD, SHELL CARD, OTRA.
- `carga_sobre_trafico` (desde Tráfico): CUENTA CORRIENTE, **TARJETA PREPAGO**, TARJETA DE
  CREDITO, EFECTIVO, AUDITOR, OTRA. *(El 86% de los datos reales es TARJETA PREPAGO → la
  carga operativa entra por Tráfico.)*

En la variante trafico, las estaciones y artículos se filtran por
**`parametro.rubro_combustible`** (= 1) y el vehículo/chofer vienen prellenados de la planilla.

### Validaciones (`audita_carga`)

1. Interno, rubro, estación, tipo de combustible, odómetro > 0, litros > 0 obligatorios.
2. Chofer obligatorio salvo check "Sin chofer" (graba literal `"SIN CHOFER"`).
3. **Solo si `estacion_rubro.audita`** (flag del rubro):
   - `litros + litros_2 > vehiculo.litro_tanque` → rechaza (capacidad del tanque).
   - Delta odómetro vs última carga fuera de `0..1000 km` → advierte, el usuario puede
     forzar ("PROSIGA Y LUEGO REPARE EL ERROR").
4. El odómetro anterior se busca con
   `SELECT ... FROM vehiculo_sobre WHERE dominio = X AND fecha >= f_carga ORDER BY f_carga, hora` (último).

### Escritura (sin transacción, como todo el sistema)

```foxpro
* ALTA (modos alta y trafico) — n_factura y n_remito siempre 0 en la era 2:
INSERT INTO vehiculo_sobre ( n_factura, idRubro, n_remito, interno, dominio, chofer,
    estacion, odometro, litros, p_x_ltr, importe, f_carga, estacion_nombre, tipo_carga,
    hora, lleno, f_pago, dos_carga, u_create, f_create )
VALUES ( 0, nIdRubro, 0, nInterno, cDominio, cChofer, nEstacion, nOdometro, nLitros,
    nImporte / nLitros,           && ← precio por litro DERIVADO, no de lista
    nImporte, dFecha, cEstacion, cTipoComb, cHora, lLleno, cF_pago, lDos_carga,
    cUsuario, DATETIME() )

* BAJA: DELETE FROM vehiculo_sobre WHERE Id = nId        && físico (marca DBF)
* MODIFICA: UPDATE vehiculo_sobre SET <todos los campos>, u_modify, f_modify WHERE Id = nId
```

Auditoría propia: `u_create/f_create/u_modify/f_modify` (la era 1 no la tenía).

---

## 4. Promedio de Consumos — `vehiculo_combustible_consumo`

**El informe del módulo** (candidato #1 a Blazor). Filtro: rango de fechas + dominio opcional
(autocomplete sobre `vehiculo WHERE activo AND uso = "PROPIO"`, F5 = buscador). Siempre
`idrubro = parametro.rubro_combustible`.

### Algoritmo del consumo

Cursor ordenado por `interno, f_carga, hora`; loop por vehículo:

```
primer registro del vehículo  → km_recorrido = km_promedio = tot_promedio = 0
siguientes:
  km_recorrido = odometro - odometro_anterior
  si km_recorrido > 0:
      km_promedio  = litros / km_recorrido * 100      ← LITROS CADA 100 KM
      tot_promedio = promedio acumulado de km_promedio (media de medias)
  odometro_anterior = odometro
```

Columnas del grid: Dominio, Interno, Fecha, Hora, Chofer, Litros, Importe,
Lleno/Parcial, Odómetro, Km. Rec., **Km. Prom** (l/100km), **Tot. Prom**.

**Debilidades a corregir en la migración** (documentadas, no inventadas):
- No filtra cargas PARCIAL: una carga parcial subestima el consumo de su tramo y
  sobreestima el del siguiente. El método correcto es medir entre cargas LLENO.
- `tot_promedio` es media de medias (no litros totales / km totales).
- La valorización por lista de precios está comentada en el fuente (quedó el importe real).

Salidas: `REPORT FORM vehiculo_combustible_consumo` (impresora / preview / **PDF**
`OBJECT TYPE 10`), Excel.

---

## 5. Control "días sin cargar" — `trafico_vehiculo_combustible`

Se abre desde botón en la Planilla de Tráfico (trafico2/trafico3). Para cada vehículo activo:

```foxpro
SELECT a.dominio, a.interno,
       MAX(CTOT(DTOC(b.f_carga) + " " + b.hora)) AS f_carga,    && última carga
       b.odometro,
       ROUND((DATETIME() - MAX(...)) /86400, 0) AS d_carga      && días sin cargar
  FROM vehiculo a, vehiculo_sobre b
 WHERE a.id_vehiculo = b.dominio AND a.activo
 GROUP BY a.dominio, a.interno ORDER BY a.interno
```

Check "Filtra por Vehículos Sin Carga" agrega `HAVING d_carga > 0`. Botón **Carga** abre
`vehiculo_combustible_carga_sobre WITH "trafico"` con el vehículo seleccionado. Export Excel.

---

## 6. Saldos y Depósitos de Estaciones (circuito 2013–2017, hoy sin uso)

Cuenta corriente simplificada contra las estaciones YPF cuando se pagaba por depósito
anticipado. Solo 3 estaciones tienen `control_saldo`: YPF Larrazábal, YPF Senillosa,
YPF Varela. Depósitos cargados: 787 (2013–2017). Precios cargados: 363 vigencias (2013–2017).

### `vehiculo_estacion_saldo` (informe de saldos)

`arma_saldo(desde, hasta)` construye tabla temporal `tmpSaldo(estacion, debe, haber, saldo)`:

- **Debe** = `SUM(importe)` de `vehiculo_estacion_saldo` (depósitos) por estación con
  `control_saldo`, en el período.
- **Haber** = `SUM(importe)` de `vehiculo_sobre` (consumos a importe real) por estación.
- **Saldo = debe − haber**. Fecha de arranque sugerida: `parametro.dCombSaldo` (= 01/08/2013,
  el corte inicial del control).

*(El mensaje "problemas en la obtención de precios" del Click es código muerto: la versión
final de `arma_saldo` usa importes reales y siempre devuelve .F. La versión que re-valoriza
con `vehiculo_combustible_precio` quedó copy-pasteada como `arma_precio` —no llamada— en
`consumo`, `mant_sobre_lote`, `mant_sobre`, `chofer_viatico` y `procesos.prg`.)*

### `vehiculo_estacion_saldo_carga` (alta de depósitos)

Ingreso/Egreso (egreso = importe × −1). Formas de pago reales: CHEQUE PROPIO, TARJETA DE
CREDITO, TRANSFERENCIA BANCARIA, AJUSTE DE SALDOS, EFECTIVO, CHEQUE TERCERO, SALDO INICIAL.

```foxpro
cEmpresa = "PATAGONIA"        && ⚠️ HARDCODEADO en el fuente
INSERT INTO vehiculo_estacion_saldo (empresa, estacion, estacion_nombre, fecha,
    forma_pago, importe, usuario, comentario) VALUES (cEmpresa, ...)
```

⚠️ Los 787 registros históricos dicen `empresa = "NORTUR"` — el hardcode "PATAGONIA" es
posterior al último uso o difiere del exe productivo. Si se reactivara, grabaría mal la empresa.

### `vehiculo_estacion_saldo_mant` (mantenimiento)

Browser por estación + **DELETE físico** (`DELETE FROM vehiculo_estacion_saldo WHERE id`).

### Tarifario `vehiculo_combustible_precio` (+ `_abm`)

Precio por litro por **estación × tipo_comb × vigencia** (`dVigencia/hVigencia`).
El ABM tiene modos ALTA/BAJA/MODIFICA/**VIGENCIA** y el botón "Continúa el mismo precio"
(cierra la vigencia actual a hoy). El browser **no se abre desde ningún menú actual** —
quedó huérfano al abandonarse el control valorizado en 2017.

---

## 7. Catálogos

### `estacion` — ⚠️ NO es solo estaciones: es el catálogo de PROVEEDORES

La tabla/form `estacion` ("Rubros de los proveedores") guarda **todos los contactos
proveedores** clasificados por `estacion_rubro`. Rubros reales: 1 ESTACION SERVICIO,
2 CRISTALES, 3 GOMERIAS, 4 FLETEROS-MINIBUSES, 5 GRUAS, 6 FLETEROS-BUSES, 7 AUDIO,
8 CLIENTES. 178 filas activas; **solo 5 son estaciones de servicio** (rubro = 1) y 3 con
`control_saldo`. El mismo form se abre también desde el menú **Contactos** del sistema.

Campos del ABM: razón social, domicilio/localidad/provincia, teléfonos, email, 2 contactos,
radio, rubro, medio de pago, y los flags del circuito combustible:
`control_saldo` (participa del control de saldos), `ult_lote` (numerador de lote POR
ESTACIÓN — en desuso, hoy el numerador es global en `parametro.lote_sobre`),
`cairo_codigo/cairo_iibb` (códigos del sistema contable Cairo), `ypf_ruta`, `esso_card`,
`cta_cte` (qué medio de pago acepta). Baja = `DELETE FROM estacion WHERE Id` (físico).

### `estacion_rubro`

Rubro + flag **`audita`**: activa las validaciones de coherencia (tanque/odómetro) en la
carga. Hoy ningún rubro la tiene activa.

### `estacion_rubro_articulo`

Artículos por rubro. Para rubro 1: DIESEL 500 y EURO-DIESEL — son las opciones del combo
"tipo de combustible" de la carga.

### `vehiculo_tipo_combustible` (+ `_abm`)

Catálogo viejo de tipos (GAS-OIL, DIESEL 500, SUPER, EURO-DIESEL). La carga actual **ya no
lo usa** (usa los artículos del rubro); queda referenciado solo por forms de la era 1 y por
el ABM de precios.

---

## 8. Forms legacy (era 1) — NO migrar

Ninguno se abre desde los menús actuales; trabajan sobre la tabla congelada
`vehiculo_combustible` con el modelo factura/remito:

| Form | Qué era |
| --- | --- |
| `vehiculo_combustible` / `_3` | Browsers de cargas por dominio/fecha (con estadística odómetro) |
| `vehiculo_combustible_mant` + `_mant_abm` | Mantenimiento por estación + nº factura (con DELETE) |
| `vehiculo_combustible_carga` | Alta era-1 (factura/remito, total facturado por estación) |
| `vehiculo_combustible_carga_importa` | **Importación Excel "Estaciones ESSO"** (carga masiva tarjeta Esso) |
| `vehiculo_combustible_prorrateo` | Prorratea una diferencia de factura entre los renglones de la factura |
| `vehiculo_combustible_saldo_fc` | Totales por nº factura (litros/importe) — conciliación de facturas era-1 |
| `vehiculo_combustible_estadistica` | Form sin terminar (caption "Form1") |
| `vehiculo_combustible_odometro` | Corrección puntual de odómetro |
| `vehiculo_combustible_mant_sobre` / `_sobre_2` | Versiones previas del conciliador de lotes actual |

**Cruce con viáticos:** `chofer_viatico.scx` contiene una copia de `arma_precio`
(re-valorización por lista de precios) — es código muerto copy-pasteado, los viáticos no
dependen del combustible.

---

## 9. Tablas — referencia para la migración

| Tabla | Filas (DBF vivo) | Réplica SQL | Notas / trampas |
| --- | --- | --- | --- |
| **`vehiculo_sobre`** | 108.957 | ❌ **FALTA** | La tabla viva. `n_sobre` = lote (0 = sin conciliar), `idrubro`, `f_pago` C(30) texto libre, `lleno` L, `dos_carga`, `hora` C(5) "HH:mm", auditoría `u_create/f_create/u_modify/f_modify`. `p_x_ltr` derivado = importe/litros |
| `vehiculo_combustible` | 37.123 | ✅ 36.142 | Congelada 2016 (era 1). `n_sobre` existe pero casi sin uso; `lleno` C(1); sin auditoría |
| `vehiculo_estacion_saldo` | 961 (787 SQL) | ✅ | Depósitos 2013–2017. Egresos = importe negativo. `empresa` (histórico NORTUR; el fuente actual hardcodea "PATAGONIA") |
| `vehiculo_combustible_precio` | 376 (363 SQL) | ✅ | Precio × estación × `tipo_comb` × vigencia (2013–2017, sin uso) |
| `estacion` | 178 activas | ✅ | **Catálogo de PROVEEDORES**, no solo estaciones (8 rubros). `control_sa` = control_saldo truncado. Join por **nombre**, no por id (ver trampa 4) |
| `estacion_rubro` | 8 | ✅ | flag `audita` → validaciones de carga |
| `estacion_rubro_articulo` | 2 | ✅ | DIESEL 500, EURO-DIESEL (combo tipo de combustible) |
| `vehiculo_tipo_combustible` | 4 | ✅ | catálogo viejo, ya no lo usa la carga |
| `parametro` (campos del módulo) | 1 | ✅ | **`lote_sobre`** = 1767 (numerador global de lotes), **`rubro_comb`** = 1, **`dcombsaldo`** = 2013-08-01 |
| `vehiculo` (campos usados) | — | ✅ | `litro_tanque` (capacidad), `activo`, `uso` = "PROPIO", `interno`, `id_vehiculo` = dominio |

---

## 10. Reglas de oro y trampas para la migración

1. **Paso 0: replicar `vehiculo_sobre`** — sin eso no hay ningún informe de combustible
   posible en Blazor. Pedir que se agregue al job de réplica DBF→SQL.
2. **No mezclar eras**: histórico ≤2013 en `vehiculo_combustible` (replicada), 2013+ en
   `vehiculo_sobre`. Un dashboard histórico completo debe unir las dos con cuidado
   (campos distintos: `lleno` C/L, `hora` T/C, sin/con auditoría).
3. **El join de estaciones es por NOMBRE desnormalizado** (`a.estacion_nombre = b.nombre`),
   no por `estacion` (id). Renombrar una estación en el ABM rompe la historia de saldos.
4. **`estacion` es el catálogo de proveedores de toda la empresa** (gomerías, grúas,
   fleteros...). Cualquier ABM Blazor de "Estaciones" en realidad toca Contactos/Proveedores.
   Filtrar siempre `rubro = parametro.rubro_comb` para combustible.
5. **El numerador de lotes es `parametro.lote_sobre`** (global, hoy 1767) — no
   `estacion.ult_lote` (viejo numerador por estación, en desuso). Asignar lote = UPDATE de
   `parametro` + UPDATE masivo de `vehiculo_sobre.n_sobre`; cancelar no devuelve el número.
6. **Bajas físicas en todo el módulo** (`DELETE FROM`), no borrado lógico — en la réplica
   aparecerán como `_deleted = 1` si el job replica deletes.
7. `p_x_ltr` se calcula importe/litros al grabar; **no** sale del tarifario. El tarifario
   `vehiculo_combustible_precio` está muerto desde 2017.
8. Las validaciones de tanque/odómetro dependen del flag `estacion_rubro.audita`
   (hoy apagado — los datos recientes pueden tener odómetros incoherentes; sanitizar
   deltas en los informes: descartar `km_recorrido <= 0` o `> 1000` como hace el form).
9. El algoritmo de Promedio de Consumos tiene fallas conocidas (no filtra cargas PARCIAL,
   media de medias) — al migrar, calcular consumo entre cargas LLENO y promedio global
   = Σlitros/Σkm × 100, manteniendo la versión vieja como referencia de validación.
10. `f_pago` es texto libre C(30) con listas distintas según el form de origen — normalizar
    a catálogo en la migración (valores reales en §0).
11. El permiso del módulo es la letra **M** en `usuario.acceso`; el permiso **F** (precios)
    aplica al export Excel con columnas de importe.

---

## 11. Reportes FRX del módulo

En `C:\MetroCarSys\Reports`: `vehiculo_combustible.frx` (listado de cargas — lo usa el
conciliador), `vehiculo_combustible_consumo.frx` (promedio de consumos, también a PDF),
`vehiculo_estacion_saldo.frx` (saldos por estación), `vehiculo_combustible_saldo_fc.frx`
(era 1, por factura).

---

## 12. Candidatos a informes Blazor (orden sugerido)

0. **Replicar `vehiculo_sobre` a SQL** (prerrequisito de todo lo demás).
1. **Dashboard de Consumos**: litros/100 km por vehículo (entre cargas LLENO), ranking de
   la flota, evolución mensual, costo por km — mejora directa del Promedio de Consumos.
2. **Control de cargas**: días sin cargar por vehículo activo (réplica de
   `trafico_vehiculo_combustible`) + cargas sin conciliar (`n_sobre = 0`).
3. **Costo mensual de combustible** por vehículo/estación/tipo (los importes están cargados;
   hoy no existe ese informe en FoxPro).
4. **Conciliación por lote** (escritura) — recién cuando la regla strangler
   (skill `abm-metrocar`) esté cumplida para `vehiculo_sobre` y `parametro`.
