# Tráfico — Menú contextual del panel BUSES (clic derecho sobre `Grid2`)

> Extraído el 04/08/2026 de `C:\MetroCarSys\Menus\MENU_VIAJE_VEHICULO.MPR` +
> `Forms\trafico2.scx` (métodos del form) + los 8 sub-forms que abre.
> Estado de migración: **solo lectura + andamiaje** (ver § Mapa de migración).

El panel Buses de `trafico2.scx` es el `Grid2` (flota viva, una fila por unidad activa,
armado por `arma_grid_vehiculo()` sobre el cursor `cursorVehiculoTrafico`). Su
`RightClick` dispara `DO menu_viaje_vehiculo.mpr`.

**No confundir con el menú de la grilla de viajes** (`Grid1.RightClick →
menu_viaje_reserva.mpr`, ya migrado): son dos menús distintos, con dos popups
`verdatosex` distintos, sobre dos entidades distintas. Este opera sobre la **unidad**
(`vehiculo`), no sobre el viaje.

---

## El cursor sobre el que opera todo: `cursorVehiculoTrafico`

```foxpro
Select a.* , SPACE(6) as trabaja , b.cronograma as codFletero , b.orden
  From vehiculo a INNER Join fletero b On a.fletero = b.id_contratado
  Where activo And Empty(a.f_delete)
  Order By b.orden , a.interno
  INTO Cursor cursorVehiculoTrafico Readwrite
```

Post-proceso fila por fila (el `Do While` de `arma_grid_vehiculo`):

1. Si `interno < 999` → busca `chofer_franco` del chofer para **hoy** y copia
   `codigo` en la columna calculada `trabaja` (columna "Franco" de la grilla).
2. Si `estado = "ASIGNADO"` y `hs_inicio <= Datetime()` → el estado **de display**
   pasa a `"CURSO"` (no se graba, igual que en la grilla de viajes).

> Por eso **todos los ítems del menú leen `cursorVehiculoTrafico.*`, que es
> `vehiculo.*` completo** — no solo las 12 columnas visibles. Varios ítems usan
> campos que la grilla NO muestra: `id_vehiculo` (código/dominio), `nombre_chofer`,
> `id_viaje_int`, `uso`, `fletero`, `ypf_*`, `esso_*`, `tac_au_oeste`,
> `verificacion_vto`, `poliza_vto`.

### Nombres truncados por la réplica (verificado en `sys.columns`)

| FoxPro (DBF) | SQL `replicaVPF` |
| --- | --- |
| `id_vehiculo` | `id_vehicul` |
| `id_vehiculo_tipo` | `id_vehicu2` |
| `nombre_chofer` | `nombre_cho` |
| `id_viaje_int` | `id_viaje_i` |
| `tac_au_oeste` | `tac_au_oes` |
| `verificacion_vto` | `verificac2` |
| `id_contratado` (fletero) | `id_contrat` |

---

## Las 24 barras del popup `shortcut`

`\-` = separador. El submenú `verdatosex` cuelga de la barra 24.

| Bar | Prompt | Método de `trafico2.scx` / acción |
| --- | --- | --- |
| 1 | Ubicar en GPS | `ubicar_gps("vehiculo")` |
| 3 | Refresh | `arma_grid_vehiculo()` |
| 4 | Novedad sobre la unidad | `libro_novedad_alta_veh()` |
| 6 | Carga de combustible | `carga_combustible()` |
| 7 | Adicional al servicio | `carga_adicional()` |
| 8 | Orden de trabajo | `DO FORM taller_orden_trabajo WITH 0, id_vehiculo, interno, id_chofer` |
| 10 | Logonear | validación inline + `logonea_conductor("primero", fletero)` |
| 11 | DesLogonear | validación inline + `deslogonea_conductor("primero")` |
| 12 | Viatico | `DO FORM chofer_viatico_abm WITH "trafico", id_chofer` |
| 14 | Logonear 2º Conductor | validación inline + `logonea_conductor("segundo", fletero)` |
| 15 | DesLogonear 2º Conductor | `deslogonea_conductor("segundo")` |
| 16 | Viatico 2º Conductor | `DO FORM chofer_viatico_abm WITH "trafico", id_chofer2` |
| 18 | Toma Franco | **INSERT inline en `chofer_franco`** (código completo abajo) |
| 20 | Ir al Viaje | `buscar_viaje()` |
| 22 | Liberar unidad: pasa a Sin Asignar | `liberar_viaje_x_problema()` → `DO FORM trafico_vehiculo_libera` |
| 24 | Ver Datos Extras ▸ | popup `verdatosex` (4 ítems) |

### Submenú `verdatosex` (¡distinto al de la grilla de viajes!)

| Bar | Prompt | Acción | Guarda |
| --- | --- | --- | --- |
| 1 | Logoneo / Deslogineo | `DO FORM vehiculo_logoneo_history WITH id_vehiculo` | exige `id_vehiculo` no vacío |
| 2 | Vehiculo | `DO FORM vehiculo_abm WITH "consulta", id_vehiculo` | ídem |
| 3 | Chofer | `DO FORM chofer_abm WITH "consulta", id_chofer` | exige `id_chofer` no vacío |
| 4 | Tarjetas | `DO FORM vehiculo_tarjeta WITH "consulta", id_vehiculo, id_chofer` | exige `id_vehiculo` no vacío |

---

## Ítem por ítem — lógica exacta

### 1 · Ubicar en GPS

`ubicar_gps("vehiculo")` con el caso `lpTipoConsulta = "vehiculo"`:

```foxpro
nInternoLp     = cursorVehiculoTrafico.interno
cGps_Recorrido = ""
cTipo_consulta = "UBICAR"
...
cBusGps = Right("00" + Alltrim(Str(nInternoLp)), 2)
lcClave = "http://embedded.sytes.net:8085/movil.aspx?cod=94&int=" + cBusGps
o = CREATEOBJECT("Shell.Application")
o.Open(lcClave)
```

- A diferencia del menú de viajes, acá **no hay cascada** `interno → cronograma →
  cronogramaCbio`: la unidad ES la fila, se usa su `interno` directo.
- Si `interno = 0` → cartel "El servicio no tiene ninguno de los datos validos".
- ⚠ `Right(..., 2)` trunca el interno a **2 dígitos** — con internos de 3+ cifras
  el FoxPro arma mal la URL. Es un bug del original.

### 3 · Refresh

`arma_grid_vehiculo()` — re-arma el cursor de la flota desde cero (query + post-proceso
de franco/CURSO) y re-posiciona en `nInternoBuscar` si venía seteado.

### 4 · Novedad sobre la unidad

```foxpro
DO FORM libro_novedad_abm WITH "alta" , "" , -1 , cursorVehiculoTrafico.interno
```

El `-1` en `lpId_viaje` es la **marca de "novedad de unidad"**. En `libro_novedad_abm.Init`:

```foxpro
Case lpId_viaje = -1  && cargo interno del vehiculo
    Select * From vehiculo Where interno = lpInterno .And. activo Into Cursor _tmp_vehiculo
    Thisform.asunto.Value = "int: " + Alltrim(Str(_tmp_vehiculo.interno)) + ;
                            " dom: " + Alltrim(_tmp_vehiculo.dominio) + ;
                            Iif(!Empty(_tmp_vehiculo.id_chofer), " chof:" + Left(Alltrim(_tmp_vehiculo.nombre_chofer),30), "")
```

y al grabar:

```foxpro
Insert Into libro_novedad (f_carga, asunto, mensaje, usuario_create, id_viaje)
    values (Datetime(), cAsunto, cMensaje, cUsuario, nId_viaje)
```

> 🔴 **Trampa clave: `libro_novedad` NO tiene columna `interno`.** La unidad queda
> embebida como TEXTO en `asunto`, con el formato `"int: N dom: XXX chof:YYY"`.
> Además el `-1` **no llega a la base**: las filas quedan con `id_viaje = 0`
> (verificado: 0 filas con `id_viaje = -1`, 17.983 con `asunto LIKE 'int:%'`).
>
> Para listar las novedades de una unidad hay que matchear por texto:
> `WHERE id_viaje = 0 AND asunto LIKE 'int: <N> %'` — **con el espacio final
> obligatorio**, si no `int: 3` traería también `int: 32`, `int: 300`, etc.

Volumen (04/08/2026): 17.983 novedades de unidad, 1.358 en los últimos 12 meses.
Circuito **vivo**.

### 6 · Carga de combustible

```foxpro
DO FORM vehiculo_combustible_carga_sobre_trafico WITH "trafico" , 0 , id_vehiculo , interno , nombre_chofer , id_chofer
```

Alta de una carga en `vehiculo_sobre` (tabla viva del módulo Combustible, ~8.000
cargas/año). Lógica completa del módulo: skill `modulo-combustible`.

### 7 · Adicional al servicio

```foxpro
DO FORM adicional_stock_abm WITH "trafico" , 0 , id_vehiculo , interno , nombre_chofer , id_chofer
```

ABM de `adicional_stock` — el stock de adicionales (agua, etc.) **entregado a la
unidad**. No confundir con `viaje_adicional` (los adicionales facturables del viaje).

```foxpro
Insert Into adicional_stock (id_adicional, dominio, fecha, hora, f_carga, tmov,
                             interno, id_cliente, razon_social, id_chofer,
                             nombre_chofer, cantidad) ...
```

Tiene columna `interno` propia → el filtrado por unidad es directo (a diferencia de
`libro_novedad`). Circuito **vivo**: 1.768 movimientos en 2026.

### 8 · Orden de trabajo

```foxpro
KEYBOARD '{ENTER}'
DO FORM taller_orden_trabajo WITH 0 , id_vehiculo , interno , id_chofer
```

Da de alta una OT en `taller_service` + N filas en `taller_service_item`, y además
inserta una novedad automática:

```foxpro
INSERT INTO libro_novedad (f_carga, asunto, mensaje, usuario_create)
    VALUES (DATETIME(), cMensajeLN, "ORDEN DE TRABAJO Nº : " + ... , cUsuario)
```

> ⚠ **Circuito discontinuado.** `taller_service` por año de `d_create`:
> 2016→1.317, 2017→736, 2018→552, 2019→2.544, 2020→1.020, **2021→2, después nada**.
> NORTUR dejó de usar el módulo Taller hace 5 años.

### 10 / 14 · Logonear (1er y 2º conductor)

Validación en el propio menú, antes de abrir nada:

```foxpro
* Bar 10 — primer conductor
If estado = "LIBERADO"
    If Empty(id_chofer)  → logonea_conductor("primero", fletero)
    Else                 → "¡ Esa unidad ya tiene asignado un conductor !"
Else                     → "¡ La unidad se encuentra en servicio !"

* Bar 14 — segundo conductor
If estado = "LIBERADO"
    If !Empty(id_chofer)
        If Empty(id_chofer2) → logonea_conductor("segundo", fletero)
        Else                 → "¡ Esa unidad ya tiene un 2º conductor !"
    Else                     → "¡ No tiene asignado el primer conductor !"
Else                         → "¡ La unidad se encuentra en servicio !"
```

Segunda capa de validación en `logonea_conductor()`:

```foxpro
If estado = "TALLER"  → "La unidad se encuentra fuera de servicio"
If !Empty(id_chofer) And Empty(lpCualConductor) → "Unidad ya logoneada con <id>"
Do Form trafico_logonear With lpCualConductor , lpFletero To lOkey
If lOkey → arma_grid_vehiculo() + reposicionar en el interno
```

`trafico_logonear.scx` — el combo de choferes es la regla de negocio interesante:

```foxpro
Select * From chofer a
  Where Empty(f_delete) .And. fletero = lpFletero
    And !Exists(Select * From vehiculo b Where a.id_chofer = b.id_chofer)
    And !Exists(Select * From vehiculo b Where a.id_chofer = b.id_chofer2)
  Order By a.id_chofer Into Cursor cursorTraficoChofer
```

→ **solo choferes del mismo fletero que NO estén ya logoneados en otra unidad**
(ni como 1º ni como 2º). Además lista los francos del fletero en el rango y, si el
chofer elegido tiene franco, pide confirmación ("El chofer tiene franco .... ¿Lo
asigna igualmente?") y graba `vehiculo.franco = .T.`.

**Escritura (botón Graba):**

```foxpro
If cTipoConductor = "primero"
    Update vehiculo Set id_chofer = cChofer , nombre_chofer = cNombre_chofer ,
                        franco = lFranco , id_zona = cZona          Where Id = nId
Else
    Update vehiculo Set id_chofer2 = cChofer                        Where Id = nId
Endif
Insert Into viaje_log_chofer (id_chofer, id_vehiculo, franco, interno, fecha, zona,
                              usuario, hora, operacion, tipo_chofer)
    values (cChofer, cId_vehiculo, lFranco, nInterno, Date(), cZona, cUsuario,
            dtHora, "LOGONEO", cTipoChofer)
```

- `tipo_chofer` = `"PRINCIPAL"` o `"ACOMPAÑANTE"`.
- `dtHora` se arma con la fecha + hora/minuto que el operador puede **editar** en el
  form (no es `Datetime()` forzado).
- El 2º conductor **no** toca zona ni franco.

### 11 / 15 · DesLogonear (1er y 2º conductor)

```foxpro
* Bar 11 — no se puede deslogonear el 1º si hay 2º logoneado
If !Empty(id_chofer2) → "¡ Deslogonee al 2º conductor !" ; RETURN
deslogonea_conductor("primero")

* Bar 15
If !Empty(id_chofer2) → deslogonea_conductor("segundo")
Else                  → "¡ No tiene asignado segundo conductor !"
```

`deslogonea_conductor()`:

```foxpro
If estado = "TALLER"   → "La unidad se encuentra fuera de servicio"
If estado = "GUARDIA"  → "La unidad se encuentra en GUARDIA. ¡ Hay que liberarla !"
If primero  And Empty(id_chofer)  → "Unidad se encuentra sin logoneadar"
If segundo  And Empty(id_chofer2) → "Unidad se encuentra sin 2º conductor logoneado"
If id_viaje # 0        → "La Unidad se encuentra realizando un viaje"
```

**Escritura:** el form exige **zona de detención** (`zona_new`, obligatoria) y graba:

```foxpro
If primero
    Update vehiculo Set id_chofer = "" , franco = .F. , id_zona = cZona_New Where Id = nId
Else
    Update vehiculo Set id_chofer2 = ""                                     Where Id = nId
Endif
Insert Into viaje_log_chofer (...) values (..., "DESLOGONEO", cTipoChofer)
```

> Ojo: en el log de DESLOGONEO se graba `zona = cZona` (la zona **vieja**), mientras
> que `vehiculo.id_zona` queda con la **nueva**.

### 12 / 16 · Viatico (1er y 2º conductor)

```foxpro
If Empty(id_chofer)  → "¡ Unidad no logoneada !"        ; RETURN
DO FORM chofer_viatico_abm WITH "trafico" , id_chofer

If Empty(id_chofer2) → "¡ Unidad sin 2º conductor !"    ; RETURN
DO FORM chofer_viatico_abm WITH "trafico" , id_chofer2
```

Es el mismo ABM de viáticos ya migrado (`/viaticos`), abierto con el chofer
pre-cargado. `chofer_viatico` está **vacía** en producción (circuito sin uso).

### 18 · Toma Franco — la única escritura inline del menú

```foxpro
If estado = "LIBERADO" .or. !EMPTY(trabaja)
    * confirmación con nombre del chofer + fecha de hoy
    nAnswer = Messagebox("Esta totalmente seguro de procesar el franco para: ..." , 4+32+256 , ...)
    If nAnswer = 6
        Select * From chofer_franco Where id_chofer = <chofer> .And. fecha = Date() Into Cursor tpFCh
        If _Tally = 0
            Insert Into chofer_franco (id_chofer, codigo, motivo, fecha, trabajo, valido)
                Values (<chofer>, "F", "FRANCO", Date(), .F., .T.)
            → "Franco generado con exito" + arma_grid_vehiculo()
        Else
            → "Ese Franco ya esta cargado en ese chofer"
        Endif
    Endif
Else
    → "El chofer no se encuentra en estado LIBERADO o ya esta de franco...."
Endif
```

- Franco **siempre de hoy** (`Date()`), código `"F"`, motivo `"FRANCO"`, `trabajo = .F.`,
  `valido = .T.`. No hay elección de fecha ni de motivo.
- La guarda `estado = "LIBERADO" .or. !Empty(trabaja)` deja pasar también a los que **ya
  están de franco** — y ahí el chequeo de duplicado es el que corta.
- ⚠ Si la unidad no tiene chofer logoneado, `id_chofer` viene vacío y el FoxPro
  **insertaría un franco con `id_chofer = ''`**. El original no lo valida.

### 20 · Ir al Viaje

```foxpro
If id_viaje = 0 → "¡ No tiene viaje asignado !" ; RETURN

If id_viaje_int <> 0                      && el viaje es un tramo de RUTA
    cFiltroActivo = "VARIO_RUTA"
    Thisform.aplica_filtro(cFiltroActivo)
Else
    Select cursorViajeReserva
    Locate For id_viaje = cursorVehiculoTrafico.id_viaje
    If !Found()                           && no está en el día que se está mirando
        nAns = Messagebox("Registro no encontrado en las reservas actuales" + ;
                          "¿ Desea aplicar filtro con ese Nº de reserva ?" , 4+32+256)
        If nAns = 6
            nId_viaje_filtro = cursorVehiculoTrafico.id_viaje
            cFiltroActivo = "RESERVA_SELECCION"
            Thisform.aplica_filtro(cFiltroActivo)
        Endif
    Endif
Endif
Thisform.grid1.SetFocus
```

Es el **puente entre las dos grillas**: salta de la unidad al servicio que está haciendo.

### 22 · Liberar unidad: pasa a Sin Asignar

```foxpro
liberar_viaje_x_problema()  →  DO FORM trafico_vehiculo_libera
```

Botón `Libera` del form:

```foxpro
If Thisform.estado_viaje2.Value # "LIBERADO"
    Select * From vehiculo Where cronograma = cCronograma .And. activo Into Cursor tpVhId
    If _Tally = 1
        Update vehiculo Set estado = "LIBERADO" , hs_inicio = {//::} , id_viaje = 0 Where Id = nId
    Else
        → "Atención ... se encontro un problema con los vehiculos"
    Endif
Endif
```

> 🔴 **El nombre del ítem MIENTE.** "pasa a Sin Asignar" sugiere que cambia
> `viaje.estado_via`, pero **el código que tocaba `viaje` está comentado**
> (todo el bloque `*!*` que hacía `Update viaje Set estado_viaje = "ASIGNADO"...`,
> `viaje_log`, `vehiculo_km`). Lo único vivo es el `UPDATE vehiculo`.
>
> Es una **liberación de emergencia de la UNIDAD**: la despega del viaje para poder
> reasignarla. El viaje queda exactamente como estaba. No confundir con:
> - el botón **"Libe"** de la toolbar = FINALIZAR el viaje (ver `TRAFICO2_TOOLBAR.md`);
> - el **"Sin Asignar"** del Zoom del Viaje = ese sí revierte el estado del viaje.
>
> Además busca la unidad por `vehiculo.cronograma = cCronograma` (no por `Id`), y si
> el match no es exactamente 1 fila, aborta.

### VDE 1 · Logoneo / Deslogineo

```foxpro
SELECT Viaje_log_chofer.*, Chofer.nombre
  FROM viaje_log_chofer INNER JOIN chofer ON Viaje_log_chofer.id_chofer = Chofer.id_chofer
  WHERE id_vehiculo = lpVehiculo
  ORDER BY hora DESC
```

Grilla de 8 columnas: Interno · Id. Chofer · Nombre · Fecha y Hora (`hora`) ·
Movimiento (`operacion`) · T. Chofer (`tipo_chofer`) · Zona · Usuario.

> ⛔ **`viaje_log_chofer` NO está replicada en SQL** (verificado 04/08/2026 en
> 172.25.80.234: no aparece en `sys.tables`). En el DBF tiene **75.001 registros**.
> Estructura (`C:\MetroCarSys\Nortur\viaje_log_chofer.dbf`):
> `ID_CHOFER C(15)`, `FRANCO L`, `ID_VEHICUL C(15)`, `INTERNO N(10)`, `FECHA D`,
> `ZONA C(15)`, `USUARIO C(15)`, `HORA T`, `OPERACION C(10)`, `TIPO_CHOFE C(20)`.
>
> **Pendiente para el cliente: sumarla a la réplica antes del día D.**

### VDE 2 · Vehiculo / VDE 3 · Chofer

Fichas de consulta estándar (`vehiculo_abm` / `chofer_abm` con `"consulta"`). Ya
migradas como `VehiculoDetalleDialog` / `ChoferDetalleDialog`.

### VDE 4 · Tarjetas

`vehiculo_tarjeta.scx` — form de **solo consulta** (todos los campos `Enabled = .f.`,
botón "Consulta"). No lee tabla nueva: arma la ficha con campos de `vehiculo` (los de
la fila) + dos campos de `chofer`:

| Campo del form | Origen |
| --- | --- |
| Interno | `vehiculo.interno` |
| Chofer | `vehiculo.nombre_chofer` |
| Nextel | `vehiculo.nextel` |
| Telepase AU Oeste | `vehiculo.tac_au_oeste` |
| YPF: tarjeta / vto / PIN | `vehiculo.ypf_tar` · `ypf_venc` · `ypf_pin` |
| ESSO: tarjeta / vto / PIN | `vehiculo.esso_tar` · `esso_venc` · `esso_pin` |
| PIN YPF del conductor | `chofer.ypf_pin` (por `id_chofer` de la fila) |
| PIN ESSO del conductor | `chofer.esso_pin` |

Si el chofer no se encuentra, los dos PIN del conductor quedan vacíos.

---

## `controla_vencimiento()` — regla presente pero NO usada por este menú

`trafico2.scx` tiene el método, que devuelve el texto de error si hay vencimientos:

- `vehiculo.verificacion_vto < Date()` → "VERIFICACION TECNICA Vencida"
- `vehiculo.poliza_vto < Date()` → "POLIZA DE SEGURO Vencida"
- Si hay `id_chofer` **y** `vehiculo.uso = "PROPIO"`: `chofer.registro_vto` y
  `chofer.registro_vto_cnrt` vencidos (ídem para `id_chofer2`, con prefijo "2º CONDUCTOR").

Ningún ítem de este menú lo llama (lo usa el circuito de asignación). Se documenta
acá porque es la misma regla del tablero de alertas de Buslink y porque es la
candidata natural a mostrarse como advertencia al logonear.

---

## Mapa de migración a Buslink

| Ítem FoxPro | Buslink | Estado |
| --- | --- | --- |
| Ubicar en GPS | ítem del menú de Buses → misma URL GPS que la grilla de viajes | ✅ lectura |
| Refresh | recarga el panel (invalida caché de `GetPanelBusesAsync`) | ✅ lectura |
| Novedad sobre la unidad | `NovedadUnidadDialog` — lista `libro_novedad` por `asunto LIKE 'int: N %'` | ✅ lectura + andamiaje alta |
| Carga de combustible | `CargasUnidadDialog` — últimas cargas de `vehiculo_sobre` | ✅ lectura |
| Adicional al servicio | `AdicionalStockUnidadDialog` — `adicional_stock` por `interno` | ✅ lectura |
| Orden de trabajo | `OrdenesTrabajoUnidadDialog` — historial `taller_service` (circuito muerto) | ✅ lectura |
| Logonear / DesLogonear (×2) | `LogoneoDialog` con las validaciones exactas | 🔒 andamiaje (`LogoneoAbmActivo = false`) |
| Viatico (×2) | abre el ABM de viáticos ya migrado con el chofer pre-cargado | ✅ lectura |
| Toma Franco | confirmación + chequeo de duplicado, reusa `AbmService` de francos | 🔒 andamiaje (`TomaFrancoActivo = false`) |
| Ir al Viaje | selecciona la fila en la grilla; si no está, filtra por Nº reserva / ruta | ✅ lectura |
| Liberar unidad | `LiberarUnidadDialog` — UPDATE `vehiculo` (fiel al FoxPro: no toca `viaje`) | 🔒 andamiaje (`LiberarUnidadActivo = false`) |
| VDE · Logoneo/Deslogoneo | ítem deshabilitado — **falta replicar `viaje_log_chofer`** | ⛔ bloqueado |
| VDE · Vehiculo | `VehiculoDetalleDialog` (ya existía) | ✅ lectura |
| VDE · Chofer | `ChoferDetalleDialog` (ya existía) | ✅ lectura |
| VDE · Tarjetas | `TarjetasUnidadDialog` (nuevo, 100% lectura) | ✅ lectura |

### Regla de escritura

`vehiculo` es tabla del **circuito viaje** (la pisa la asignación de Tráfico) → todo lo
que la escribe (Logoneo, Deslogoneo, Liberar unidad) cambia de dueño el **día D**, con
el resto del circuito. `chofer_franco` es autocontenida y ya tiene ABM migrado, pero se
deja apagada por consistencia (decisión del 04/08/2026). Ver
`docs/buslink/PLAN_MIGRACION_BUSLINK.md`.
