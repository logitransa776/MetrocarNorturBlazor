# Lógica FoxPro — Reserva de Transportación (`reserva_transportacion_con_adicional.scx`)

> Menú: **Reservas → Reservas Especiales**.
> Form principal de carga manual de reservas (origen `'T'`). 89 objetos.
> Subdialogs: `reserva_transportacion_valor.scx` (Valor Especial),
> `reserva_transportacion_cantidad_servicio.scx` (Cnt Servicios),
> `reserva_transportacion_con_adicional_abm.scx` (alta de 1 adicional),
> `reserva_transportacion_adicional.scx` (dialog legacy de 5 slots — lo usa el botón
> `bAdicional` de este form y `reserva_plantilla_crear`).
> Extraído del binario con `foxpro-extract` (12/06/2026).

---

## Contexto

Es el alta manual de reservas "especiales" (no de plantilla): traslados aeropuerto, city
tours, cenas show, servicios a medida. Una corrida puede generar **muchas filas en `viaje`**:

```
filas insertadas = días (f_reserva → f_fin "Duplica servicios hasta el")
                 × cantidad de servicios contratados (botón Cnt Servicios)
```

Si "Servicio de varios días" está activo, en cambio, genera **una fila por día del rango**
(ida → vuelta) que comparten un mismo `id_viaje_i` (número interno de servicio de ruta).

**No hay modo modificación en este form**: solo alta. La modificación de una reserva ya
grabada se hace desde Tráfico (`trafico_zoom.scx`, ver `../trafico/TRAFICO_ZOOM.md`).

---

## Campos del form (→ columna real en SQL `replicaVPF`)

| Control | Tabla.columna SQL | Notas |
|---|---|---|
| Pedido (`f_pedido`) | `viaje.f_pedido` | default hoy; ≤ f_reserva |
| Inicio (`f_reserva`) + hh:mm | `viaje.f_reserva`, `viaje.hs_inicio`, `viaje.hs_s_inici` | `hs_inicio` datetime; `hs_s_inici` string "HH:MM" |
| Presentación (combo) | `viaje.hs_present` | offset sobre hs_inicio (ver tabla abajo) |
| Duplica servicios hasta el (`f_fin`) | — (control de loop) | genera 1 fila por día |
| Servicio de varios días (`vario_dia` + `f_vuelve` hh:mm) | `viaje.hs_ini_rut`, `viaje.hs_fin_rut`, `viaje.id_viaje_i` | modo "coche a la ruta" |
| Cliente (`cliente`) | `viaje.id_cliente`, `viaje.nombre_cli` | F5 → `cliente_busca` |
| (readonly) razón social, CUIT | de `cliente.razon_soci`, `cliente.ncuit` | cliente sin CUIT **bloquea** |
| Operador | `viaje.id_operado` | F5/binocular → `cliente_operador_busca`; se habilita al validar cliente |
| Servicio 1º/2º/3º | `viaje.id_servici`, `viaje.id_servic2`, `viaje.id_servic3` | combo de `servicio` con `transporta=1` y `f_delete` vacío |
| Hs. fin (hh:mm) | `viaje.hs_fin_apr` | autocalculada = inicio + Σ duración de los servicios |
| T. Vehículo | `viaje.id_vehicu2` | combo `vehiculo_tipo`; al elegir copia `pax` máximo |
| Pax | `viaje.pax` | obligatorio > 0 |
| Aguas | `viaje.agua` | numérico suelto (la lógica de adicional-agua está comentada) |
| KM | `viaje.km` | autocalculado = Σ `servicio.km`; editable |
| Voucher | `viaje.voucher_nr` | — |
| Sin Grupo / Grupo Nuevo / Agrega Servicio | `viaje.grupo`, `viaje.f_grupo_fi`, `viaje.id_grupo` | ver sección Grupos |
| F. Partida (`f_grupo_fin`) | `cliente_grupo.f_grupo_fi` | — |
| F. Facturo (`f_grupo_fc`, readonly) | `cliente_grupo.f_grupo_fc` | si tiene valor → grupo cerrado, **bloquea** |
| Vuelo | `viaje.vuelo` | default "SIN VUELO" |
| Guía + Teléfono | `viaje.nombre_gui` | graba "NOMBRE : TELEFONO" (23 chars + tel); default "SIN GUIA" |
| Traslado de Guía (`trasladoGuia`) | — | modo especial post-grabación (ver abajo) |
| Desde / Hasta | `viaje.d_destino`, `viaje.h_destino` | autocomplete desde tabla `destino`; F5 → `destino_busca` |
| Districto Inicio (`provincia`) | `viaje.d_destino_` | array global `aProvincia`; autollenado por localidad del destino |
| Mas de 100 Km (`mas100km`) | `viaje.mas100km` | autollenado desde `destino.mas100km` del "Hasta" |
| Agregar Destino (botón) | — | abre `destino_abm` en modo "alta" |
| Comentario | `viaje.comentario` | máx 254 |
| Adicionales (grilla) | `viaje_adicional` | cursor en memoria hasta grabar |
| Documento (botón + `file`) | `viaje.file` | GETFILE() — guarda el path del adjunto |

**Offsets de Presentación** (combo `h_presentacion`, default "en hora", deshabilitado hasta
cargar hora): en hora → `hs_present` vacío; 5/15/30/45 min, 1 hora, 2 horas antes →
`hs_inicio - n*60 segundos`.

---

## Init del form

- Cursores: `servicio` (solo `transportacion = .T.` y no borrados, orden por id),
  `cliente` (no borrados, orden razón social), `vehiculo_tipo` (no borrados),
  `guia` (no borrados), `parametro` (→ `tpParametro`).
- Autocomplete nativo VFP para Desde/Hasta (tabla `destino`) y Guía (tabla `guia`).
- Combos hh (0–23) y mm (0–59).
- `f_pedido = f_reserva = f_grupo_fin = hoy`.
- **Permiso del botón Valor Especial: `"F" $ cAcceso`** (letra F del usuario — ver skill
  `seguridad-nortur`). Sin F, el botón queda deshabilitado; igualmente requiere
  cliente+servicio+vehículo cargados (`valida_boton_precio`).
- Cursor vacío `cursorViajeAdicional` (`WHERE 1=0`) para la grilla de adicionales.

---

## Validaciones de `audita_carga` (orden exacto, cada una bloquea)

1. `f_reserva ≥ f_pedido` — "La fecha del pedido no puede ser superior al día de la reserva".
2. Hora y minutos de inicio cargados.
3. `f_reserva ≤ f_fin` (duplicación).
4. Si `vario_dia`: `f_reserva ≤ f_vuelve` + hora/min de regreso cargados.
5. Cliente cargado **y existente** (lookup en vivo contra `cliente` sin borrados).
6. **Cliente con CUIT** — si `cuit` vacío: "Cliente sin Nº de CUIT… El registro se grabara,
   pero debe resolver el problema" → en realidad **limpia el cliente y bloquea** (el texto
   miente: no graba).
7. Servicio 1º cargado.
8. Si Grupo Nuevo o Agrega Servicio: nombre de grupo + `f_grupo_fin` cargados.
9. `pax > 0`.
10. Desde y Hasta cargados.
11. **Grupo Nuevo → el grupo NO debe existir** en `cliente_grupo` (id_cliente + nombre).
12. **Agrega Servicio → el grupo SÍ debe existir**.
13. `f_grupo_fc` vacío — "Grupo cerrado y facturado. No se puede cargar mas servicios".
14. `provincia` (Districto Inicio) cargado.

Además, en el `Valid` de `f_reserva`: si la fecha es anterior a hoy pide confirmación
("¿Esta totalmente seguro?"); si acepta, **copia esa fecha a `f_pedido`**.

---

## Grabación (`graba_viaje`) — paso a paso

Llamada por **Graba y Limpia** (`graba_viaje("todo")`) o **Graba y No Limpia**
(`graba_viaje("mismoGrupo")`). Todo corre **sin transacción** (el TRY/CATCH está comentado).

### 1. Resolución del grupo

```
nId_Grupo = 0
Si grupo ≠ "SIN GRUPO":
    buscar en cliente_grupo (id_cliente, nombre)
    no existe → INSERT cliente_grupo (id_cliente, nombre, f_grupo_ini = f_reserva,
                                      f_grupo_fin) → nId_Grupo = nuevo id
    existe    → nId_Grupo = id encontrado
                si f_reserva > f_grupo_fin del grupo:
                    UPDATE cliente_grupo SET f_grupo_fin = f_reserva
                    UPDATE viaje SET f_grupo_fin = f_reserva WHERE id_grupo = nId_Grupo
                    (extiende el grupo y arrastra TODOS sus viajes)
Si "SIN GRUPO": grupo = "SIN GRUPO", f_grupo_fin = f_reserva
```

### 2A. Modo normal (sin `vario_dia`)

Loop `dF_Control = f_reserva … f_fin` (un día por vuelta) × loop `i = 1 … nServicioContratado`:

- `hs_inicio` = datetime(día, hh, mm). `hs_fin_apr` = datetime con hora fin; **si hora fin
  < hora inicio se asume día siguiente** (`dF_Control + 1`).
- `hs_present` según combo Presentación.
- `INSERT INTO viaje` — campos (nombres FoxPro → ver mapeo SQL arriba):
  `str_f_reserva = DTOS(fecha)` (YYYYMMDD), `origen = 'T'`, `estado_viaje = 'SIN ASIGNAR'`,
  `cronograma = cronogramacbio = 'S/C'`, f_pedido, f_reserva, hs_presentacion, hs_inicio,
  hs_s_inicio, hs_fin_aprox, pax, nombre_guia, grupo, f_grupo_fin, vuelo, id_servicio(1/2/3),
  id_cliente, nombre_cliente, comentario, `f_create = DATE()`, `u_create = cUsuario`,
  d_destino, h_destino, d_destino_prov, id_vehiculo_tipo, moneda_convenida,
  importe_convenido, sin_cargo, descuento_convenido, moneda_pago, importe_pago,
  sin_cargo_pago, id_operador, id_grupo, km, voucher_nro, agua, file, mas100km.
- `nID_viaje = viaje.id_viaje` (autoinc).
- `INSERT INTO viaje_log (id_viaje, usuario, motivo='ALTA', hora=DATETIME(), cronograma='',
  id_chofer='', interno_ori=0, interno_new=0, comentario='CARGA DE RESERVA')`.
- `graba_adicional()`: scan de `cursorViajeAdicional` → `INSERT INTO viaje_adicional
  (id_viaje, id_adicional, nombre, cantidad, precio)` por cada fila.

### 2B. Modo "varios días" (`vario_dia` — coche a la ruta)

- Prefija el comentario: `"SERV. RUTA " + comentario`.
- **Toma número de serie**: `parametro.id_viaje_int + 1` → `UPDATE parametro` → todas las
  filas del rango comparten `id_viaje_i`.
- Una fila por día entre `f_reserva` y `f_vuelve`:
  - Día 1: hs_inicio real, `hs_fin_apr = 23:59`, `hs_ini_rut` = salida real,
    `hs_fin_rut` = datetime de regreso (f_vuelve + hh:mm vuelta). Presentación según combo.
  - Días intermedios: `00:00 → 23:59`.
  - Último día: `00:00 → hora de regreso`.
- `f_grupo_fin` de estas filas = `f_vuelve`. Mismo INSERT + `viaje_log` + adicionales
  (los adicionales se graban una sola vez al final, sobre el último id — *bug heredado:
  en multi-servicio quedan colgados del último viaje*).

### 3. Alta/actualización de guía (tabla `guia`)

Si el nombre de guía no existe → `INSERT INTO guia (nombre, telefono)`.
Si existe con teléfono distinto → `UPDATE guia SET telefono = …`.

### 4. Mensaje final y reseteo

"Servicios grabados con éxito… Desde reserva Nº X hasta Nº Y, cantidad N" →
`nueva_carga(modo)`.

---

## Modos de `nueva_carga` (reset post-grabación)

| Modo | Qué conserva | Uso |
|---|---|---|
| `"todo"` (Graba y Limpia) | nada — limpia todo, fechas = hoy | reserva siguiente independiente |
| `"mismoGrupo"` (Graba y No Limpia) | cliente, operador, grupo, f_pedido (deshabilitados); pasa el optiongroup a **Agrega Servicio** | cargar más servicios al mismo grupo |
| `"servicioGuia"` (checkbox Traslado de Guía) | cliente = `parametro.cliente_ad` (cliente interno de prueba), pax=1, vehículo="AUTO", limpia servicios 2/3 | genera el traslado del guía como reserva aparte |

En todos los modos: `nServicioContratado` vuelve a 1, botones vuelven a gris,
`cursorViajeAdicional` se vacía. El Nº de la última reserva queda visible en `nIdViaje`
("Nº de Reserva Anterior").

---

## Subdialogs

### Valor Especial (`reserva_transportacion_valor.scx`) — permiso "F"

Setea variables públicas que después viajan en el INSERT:

| Variable | Columna `viaje` | UI |
|---|---|---|
| `cMoneda` / `nImporte_convenido` | `moneda_con` / `importe_co` | "Valores especiales a Facturar" |
| `lSin_cargo` | `sin_cargo` | check "Sin Cargo para el Cliente" |
| `nPorcentaje_Descuento` | `descuento_` | 0–100 (validado) |
| `cMoneda_pago` / `nImporte_pago` | `moneda_pag` / `importe_pa` | "Valores especiales a Pagar" (costo empresa) |
| `lSin_cargo_pago` | `sin_cargo_` | check "Sin Cargo para la Empresa" |

Validaciones del Aceptar: lista de precio y convenido son excluyentes; SIN CARGO excluye
importe convenido; importe convenido exige moneda. `cEstado_Importe`: "SIN CARGO" /
"PRECIO FIJADO" / "" (en el form padre, si `nImporte_convenido > 0` graba
`estado_importe = "FIJADO"`). Toda la lógica de cálculo automático desde
`lista_precio`/`cliente_tarifa` está **comentada** (quedó manual).
Si se aceptó, el botón del padre se pinta (negro/azul) como señal visual.

### Cantidad de Servicios (`reserva_transportacion_cantidad_servicio.scx`)

Spinner mínimo 1. Devuelve `nServicioContratado` — multiplicador de inserts por día.
Cancelar vuelve a 1. Si ≠ 1 el botón del padre se pinta.

### Adicionales

- **Agregar** (`bAgregarAdic`) → `reserva_transportacion_con_adicional_abm.scx` con "alta":
  combo de tabla `adicional` (id, nombre, precio), cantidad (spinner, obligatoria > 0),
  importe fijado opcional. Anti-duplicado por `id_adicional` contra el cursor.
- **Eliminar** → confirma y borra la fila del cursor.
- El botón viejo `bAdicional` ("Sin Adicionales") abre el dialog legacy de **5 slots fijos**
  (`reserva_transportacion_adicional.scx`): 5 combos con validación de duplicados
  (`valida_dato`), totales por fila (importe × cantidad) y total general. Con parámetro
  `lValidaPrecio = .F.` los importes quedan deshabilitados (se definen al facturar).
  Es la pieza que sigue usando `reserva_plantilla_crear`.

### Lista Pax

El botón "Lista Pax" del screenshot no está en el `.scx` del disco (fuente desactualizado
vs exe productivo — verificar con el usuario al migrar; presumiblemente abre
`viaje_pasajero`).

---

## Reglas no obvias (para replicar en Blazor)

1. **El alta es multiplicativa**: días × cantidad de servicios. El usuario confirma una
   sola vez y pueden salir decenas de filas. Mostrar preview del total antes de grabar.
2. **`str_f_rese` (YYYYMMDD) debe grabarse siempre** sincronizado con `f_reserva` — es el
   campo por el que filtran los informes FoxPro (`BETWEEN(str_f_reserva, …)`).
3. `cronograma` y `cronogram2` nacen ambos en `'S/C'` (sin cronograma); Tráfico los pisa
   al asignar unidad.
4. El estado inicial es **siempre `SIN ASIGNAR`**; ningún otro estado se graba desde acá.
5. Extender la fecha fin de un grupo existente **arrastra todos los viajes del grupo**
   (UPDATE masivo de `f_grupo_fi` por `id_grupo`).
6. La hora fin que cruza medianoche suma un día a `hs_fin_apr`.
7. `nombre_gui` es un campo desnormalizado "NOMBRE : TELEFONO" — no hay FK a `guia` en el
   alta manual (sí hay `id_guia` en plantillas).
8. Sin transacción: si el insert N falla a mitad de un lote, quedan los N−1 anteriores.
   En Blazor esto debe ser una transacción.
9. El permiso del precio convenido es la **letra F** en `usuario.acceso`; los permisos
   ABM estándar (dígitos 2/3/4 de `cNivel`) acá no aplican porque el form es solo alta
   y se abre directo del menú.
10. `viaje_log` es obligatorio en cada alta (motivo "ALTA", comentario "CARGA DE RESERVA").
