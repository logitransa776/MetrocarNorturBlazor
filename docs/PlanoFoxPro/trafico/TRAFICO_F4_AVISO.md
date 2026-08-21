# TRAFICO F4 — Aviso sobre el viaje (alarma por hora) + motor de avisos

> Extraído el 03/08/2026 de `trafico_hs_aviso.scx` (el diálogo de la captura), `trafico2.scx`
> (`KeyPress`, `viaje_hora_aviso`, `arma_grid_viaje_chequeo`, `Timer1`, `Init`),
> `trafico_aviso.scx`, `Progs/funcion.prg:2272` (`formInicioServicio`) y `error.txt` (log del
> `.exe` productivo). Datos de uso medidos contra `replicaVPF` (server local, datos completos).
>
> Completa la tríada de escritura de Tráfico junto con `TRAFICO2_TOOLBAR.md` (botones),
> `TRAFICO_ZOOM.md` (Zoom) y `TRAFICO_CRONOGRAMA.md` (teclas F5-F9).

---

## 0. Corrección a un plano anterior

`TRAFICO_CRONOGRAMA.md` §1 decía: *"F4 → `viaje_hora_aviso` → form `trafico_aviso` (grid de
chequeos pendientes + botón que hace `chequeo+1`)"*. **Es incorrecto.** Lo verificado en el
fuente:

```foxpro
* trafico2.scx → KeyPress
If nKeyCode = -3 && F4
    Thisform.viaje_hora_aviso()
Endif

* trafico2.scx → PROCEDURE viaje_hora_aviso
If cursorViajeReserva.id_viaje # 0
    lOkey = .T.
    nViajeModifica = cursorViajeReserva.id_viaje
    Do Form trafico_hs_aviso To lOkey        && ← el diálogo de la captura
    If lOkey
        Thisform.arma_grid_viaje_chequeo()   && ← recalcula el cursor de alarmas
    Endif
Endif
```

**F4 no chequea nada: F4 pone la hora de la alarma** (`viaje.hs_aviso`) de la reserva
seleccionada. `trafico_aviso.scx` es otro form ("Aviso de Chequeo") que **no está vivo** en
producción (ver §3.3).

---

## 1. Qué resuelve el F4 (negocio)

El operador de tráfico necesita que el sistema le **golpee la puerta** un rato antes de que
salga cada servicio, para chequear que la unidad y el chofer estén. El sistema tiene dos
niveles:

| Nivel | De dónde sale la hora | Alcance |
| --- | --- | --- |
| **Automático** (default global) | `hs_inicio − parametro.aviso_tiempo` | TODOS los servicios del día |
| **Manual** (F4) | `viaje.hs_aviso` cargado a mano | Ese viaje puntual — **pisa** al automático |

**Regla del aviso efectivo** (literal del filtro `AVISO_HORA` de `trafico2.scx`, línea 215):

```foxpro
Iif( Empty(hs_aviso),
     Substr(Ttoc(hs_inicio - xSegundoAviso,2),1,5),   && automático: HH:MM
     Substr(Ttoc(hs_aviso,2),1,5) ) As dtAviso        && manual: HH:MM
```

O sea: **`aviso_efectivo = COALESCE(hs_aviso, hs_inicio − aviso_tiempo)`, comparado solo por
HH:MM.**

`xSegundoAviso = HOUR(parametro.aviso_tiempo)*3600 + MINUTE(parametro.aviso_tiempo)*60`.

---

## 2. Datos reales (medidos en `replicaVPF`, 03/08/2026)

### 2.1 Parámetro global

| Campo SQL (truncado a 10) | Campo FoxPro | Valor hoy | Significado |
| --- | --- | --- | --- |
| `parametro.aviso_cheq` | `aviso_chequeo` | **`'S'`** | Motor de avisos ENCENDIDO a nivel empresa |
| `parametro.aviso_tiem` | `aviso_tiempo` | **`1999-12-01 00:10:00`** | Solo importa la hora → **10 minutos** de antelación por defecto |
| `parametro.dir_sonido` | `dir_sonido_trafico` | `O:\METROCARSYS\SONIDOS\SD_SHUTDOWN_12.WAV` | Sonido de la campana (`Set Bell To`) |

### 2.2 La columna en `viaje`

| Columna | Tipo SQL | Nulos |
| --- | --- | --- |
| `viaje.hs_aviso` | `datetime2(6)` | sí |
| `viaje.chequeo` | `int` | sí (contador, no booleano) |
| `viaje.chequeo_ag` | `int` | sí (columna **Ag** de la grilla = chequeo de **agencia**, circuito aparte) |

La grilla de Buslink **ya muestra** `hs_aviso` en la columna **H.Avi**
(`ReportService.cs:494` → `CONVERT(varchar(5), v.hs_aviso, 108) AS HAvi`).

### 2.3 ¿Se usa? Sí, mucho

```
anio | total  | con hs_aviso | con chequeo>0
2021 |  32804 |         5493 |         31550
2022 | 101740 |        15076 |         96067
2023 | 110022 |        15340 |        101711
2024 | 112956 |        14708 |        106162
2025 | 111693 |        16445 |        102833
2026 |  60608 |         9341 |         53600
```

**~15% de las reservas llevan aviso manual** (9.341 en lo que va de 2026). No es una función
muerta: es rutina diaria del operador.

### 2.4 Cuánta antelación cargan a mano (2026, 9.341 casos)

```
0 min ..................     1
1-15 min ...............   100
16-60 min ..............  9155   ← el 98%
1-4 hs .................    84
4-24 hs ................     1
NEGATIVO (después del viaje):  0
```

Top de minutos exactos: **60 min (5.985)**, 40 (1.065), 30 (932), 45 (386), 50 (339), 25 (235).

> **Lectura:** el default global de 10 minutos les queda corto; cuando cargan a mano, casi
> siempre piden **una hora antes** (o 30-45 min). Esto justifica poner **chips de acceso
> rápido 30 / 45 / 60 min** en el diálogo nuevo, no solo un reloj vacío.

### 2.5 Quién ve las alarmas — permiso `C`

`Init` de `trafico2.scx`:

```foxpro
If tmpParametro.aviso_chequeo = "S"
    If "C" $ cAcceso
        Thisform.verchequeo.Value = .T.
        Thisform.timer1.Interval = 60000   && cada 1 minuto
    Else
        Thisform.timer1.Interval = 0       && apagado
    Endif
Else
    Thisform.timer1.Interval = 0
Endif
```

**13 de los 30 usuarios** tienen la letra `C` (CEBALLOS, CHRISTIAN, DIEGO, GÜESLER, LEONARDO,
MARCELO, MAURO, PAULA, PSTELE, RICARDO, SEBASTIAN, VALERIA, WESLER) — es decir, toda la mesa
de tráfico.

> ⚠️ **`SUPERVISOR` NO tiene `C`** (`SRTDVLFAEUXM`). Es el usuario con el que se prueba
> Buslink → **con SUPERVISOR la alarma nunca va a sonar**. Hay que tenerlo en cuenta al
> testear (o probar con un usuario que sí tenga `C`).

Ya está en el catálogo de Buslink: `PermisosCatalogo.cs:27` — `'C'`, *"Avisos de chequeos"*,
`DependeDeTrafico: true`.

---

## 3. Las tres piezas del circuito

### 3.1 El SETTER — `trafico_hs_aviso.scx` (el diálogo de la captura)

Form 401×372, `Caption = "Asignar Viaje"` (caption reciclado, no tiene nada que ver con
asignar).

**Init** — precarga:

```foxpro
Thisform.id_reserva.Value = cursorViajeReserva.id_viaje     && solo lectura, rojo
Thisform.hs_inicio.Value  = cursorViajeReserva.hs_inicio    && solo lectura, rojo
For i = 0 To 23 : h_reserva.AddItem(...) : Next            && combo horas 0..23
For i = 0 To 59 : m_reserva.AddItem(...) : Next            && combo minutos 0..59

IF EMPTY(cursorViajeReserva.hs_aviso)
    f_reserva.Value = Ttod(hs_inicio)                       && ← default: la FECHA Y HORA DEL VIAJE
    h_reserva.Value = hora de hs_inicio
    m_reserva.Value = minuto de hs_inicio
ELSE
    ... = hs_aviso                                          && edita el existente
ENDIF
```

> **Trampa:** cuando no hay aviso cargado, el diálogo **precarga la hora del viaje, no la hora
> del aviso automático**. Como la validación exige que el aviso sea *anterior* a `hs_inicio`,
> si el operador aprieta "Aviso" sin tocar nada, **da error**. Es un default incómodo heredado
> — en Buslink conviene precargar `hs_inicio − aviso_tiempo` (o el último preset usado).

**Botón "Aviso"** (`Command1.Click`) — la escritura completa:

```foxpro
IF !thisform.check1.Value                                   && "No Avisar" destildado
    dHs_aviso = Datetime(año, mes, día, hora, minuto)       && del date + los 2 combos
ELSE
    dHs_aviso = CTOT("  /  /   :  :  ")                     && ← datetime VACÍO = borra el aviso
ENDIF

IF thisform.hs_inicio.Value <= dHs_aviso                    && (1) validación
    MESSAGEBOX("Atención... no puede ser mayor a la fecha del servicio",64,"Lea atentamente")
    RETURN .t.
ENDIF

IF thisform.hs_inicio.Value - 2100 = dHs_aviso              && (2) regla rara — ver abajo
    dHs_aviso = dHs_aviso - 150
ENDIF

Update viaje Set hs_aviso = dHs_aviso Where id_viaje = nIdViaje
```

**Validación del textbox de fecha** (`f_reserva.Valid`): si la fecha es **anterior a hoy** →
*"No se pueden dar reservar antes del día de hoy"* y la resetea a `DATE()`.

**Reglas resultantes:**

| # | Regla | Comentario para Buslink |
| --- | --- | --- |
| 1 | El aviso debe ser **estrictamente anterior** a `hs_inicio` (`<=` da error, o sea el igual también se rechaza) | Replicar tal cual |
| 2 | La fecha del aviso no puede ser **anterior a hoy** | Replicar, pero ver caso borde: viaje de hoy 00:30 → no se puede avisar ayer 23:50 |
| 3 | **"No Avisar"** graba datetime vacío → vuelve al aviso automático (NO desactiva el aviso) | ⚠️ El label miente: no deja de avisar, vuelve al default de 10 min |
| 4 | `hs_inicio − 35 min` exacto → se le restan 2,5 min más (queda 37,5 min antes) | **Sin explicación en el fuente.** Propuesta: NO copiarla (ver §6, pregunta 4) |
| 5 | **No escribe `viaje_log`** — el cambio de hora de aviso no deja auditoría | Mejora sugerida: sí loguearlo en Buslink |
| 6 | Después de grabar llama a `arma_grid_viaje_chequeo()` → recalcula el cursor de alarmas | En Buslink: refrescar la lista de avisos en memoria |
| 7 | Cartel rojo fijo: *"¡¡¡IMPORTANRE!!! - Deberia de realizar un REFRESH al finalizar de cargar las horas de avisos"* | Es un parche de FoxPro (el cursor de alarmas no se refresca solo). **En Buslink no hace falta** — el refresh es automático |

### 3.2 El MOTOR — `Timer1` de `trafico2.scx` (cada 60 s)

```foxpro
* PROCEDURE arma_grid_viaje_chequeo  (se recalcula al abrir la planilla y tras cada F4)
xFecha1       = DTOS( DATE() )                              && ← SIEMPRE HOY, no la fecha de la grilla
xSegundoAviso = HOUR(aviso_tiempo)*3600 + MINUTE(aviso_tiempo)*60
Select hs_inicio - xSegundoAviso as dtAviso From viaje
   Where str_f_reserva = xFecha1 Order By hs_inicio Into Cursor cursorViajeAviso

* Timer1 — Interval = 60000
PROCEDURE Timer
dtHora = Substr(Ttoc(Datetime(),2),1,5)                     && "HH:MM" de ahora
Select * From cursorViajeAviso Where dtHora = Substr(Ttoc(dtAviso,2),1,5) Into Cursor cursor_chequeo
If _Tally # 0
    lo = Createobject("formInicioServicio","Inicio de " + Alltrim(Str(_Tally)) + " servicios",.T.)
    lo.Show(1)
    Release lo
Endif

* Destroy del form → thisform.timer1.Interval = 0
* Checkbox "Chequeo" del toolbar → Click: Value ? Interval=60000 : Interval=0
```

Detalles que importan:

- El cursor de alarmas es **siempre del día de HOY**, aunque la grilla esté mostrando otro día.
- El match es por **HH:MM exacto**: si el minuto pasa mientras el form estaba cerrado o el
  usuario estaba en otra pantalla, **la alarma se pierde para siempre** (no hay reintento).
- El aviso es **por lote, no por viaje**: dice *"Inicio de N servicios"*, no cuáles.
- El checkbox **"Chequeo"** del toolbar (visible en la captura, tildado) es el interruptor
  on/off del timer. **Buslink todavía no lo tiene.**
- ⚠️ **El fuente en disco (jul 2021) tiene un bug/versión vieja acá:** `arma_grid_viaje_chequeo`
  arma `dtAviso` **solo** con `hs_inicio − aviso_tiempo`, sin el `Iif(Empty(hs_aviso),…)` que sí
  está en el filtro `AVISO_HORA` de la misma pantalla. Tal como está en disco, el `hs_aviso`
  cargado por F4 **nunca dispararía la alarma**, y la llamada a `arma_grid_viaje_chequeo()`
  desde F4 no tendría sentido. → **La semántica correcta es la del `Iif`** (COALESCE), y así se
  implementa en Buslink. *(Verificado en el fuente; el comportamiento del `.exe` no se pudo
  leer — es la única pieza asumida de todo este plano.)*

### 3.3 El POPUP — `formInicioServicio` (`Progs/funcion.prg:2272`)

```foxpro
Define Class formInicioServicio As Form
    Caption = "Aviso de inicio de servicios"
    lblCartel  : FontSize 20 → "Inicio de N servicios"
    lblCierre  : "Este aviso se cierra en 10 segundos"
    btnCerrar  : "Cerrar"
    MiTimer    : INTERVAL = 10000 → Thisform.Release   && auto-cierre a los 10 s
    Init(lpTexto, lpSonido) → If lSonido : ?Chr(7)     && beep
Enddefine
```

Modal (`lo.Show(1)`), auto-cierra a los 10 segundos, suena una campana.

**Evidencia de que ESTE es el popup vivo:** `C:\MetroCarSys\error.txt` (log del `.exe`
productivo, entradas de dic-2025) registra ventanas abiertas
`FORMINICIOSERVICIO->caption: AVISO DE INICIO DE SERVICIOS`, y la cadena `TRAFICO_AVISO`
**aparece 0 veces** en todo el log.

**`trafico_aviso.scx` ("Aviso de Chequeo") — form MUERTO, pero vale leerlo:** era la versión
rica del popup (grilla de 7 columnas con los viajes que avisan, `ringin.wav`, botón **Chequeo**
que hace `chequeo+1` + `viaje_log`, array `aFormChequeo(10,2)` para hasta 10 ventanas
simultáneas). Nadie lo abre hoy, pero **es el diseño que el autor quería** y es exactamente lo
que conviene construir en Buslink: no un cartel que dice "3 servicios", sino la lista de los 3.

---

## 4. Mapa de escritura (fila nueva para `ESCRITURA_CIRCUITO.md`)

| Op | Acción | `viaje` | `vehiculo` | `viaje_log` | GPS |
| --- | --- | --- | --- | --- | --- |
| **T-F4** | **Aviso sobre el viaje** (F4 / menú contextual) | `hs_aviso` (o NULL si "No Avisar") | — | — *(mejora: sí)* | — |

Es **la escritura de menor superficie de todo el circuito `viaje`**: una sola columna, sin
máquina de estados, sin odómetro, sin cascadas, sin importes, sin GPS. Menor incluso que el
cambio de cronograma (que toca 3 columnas + log + reset de `chequeo`).

---

## 5. Diseño propuesto en Buslink

### 5.1 Las tres piezas, traducidas

| FoxPro | Buslink | Dónde |
| --- | --- | --- |
| `trafico_hs_aviso.scx` | `AvisoViajeDialog.razor` (modal chico, patrón `--nt-dlg-*`) | `Components/Shared/` |
| `Timer1` 60 s + `cursorViajeAviso` | Enganchado al `PeriodicTimer(60s)` que **ya existe** (`AutoRefreshLoop`, `PlanillaTrafico.razor:1072`) | `PlanillaTrafico.razor` |
| `formInicioServicio` | `AvisoDisparadoDialog.razor` — **con la grilla** de `trafico_aviso.scx` (lo que el autor quería) + sonido + auto-cierre | `Components/Shared/` |
| Checkbox "Chequeo" del toolbar | `MudSwitch`/toggle "Avisos" en la barra de la planilla, visible solo con permiso `C` | `PlanillaTrafico.razor` |

### 5.2 Disparadores (lo que pediste: misma tecla + menú contextual)

1. **Menú contextual de la fila** (el mismo `MudMenu` compartido, `PlanillaTrafico.razor:758`):
   ítem nuevo **"Aviso sobre el viaje"** con `Icons.Material.Filled.NotificationsActive` y el
   atajo `F4` a la derecha. Va junto a "Novedad sobre el viaje" (que es el F2) — quedan los dos
   hermanos del FoxPro juntos.
2. **Tecla F4** en `OnGridKeyDown` (`PlanillaTrafico.razor:1528`): agregar `"F4"` a la lista de
   teclas manejadas → abre el diálogo de la fila activa.

> ⚠️ **Conflicto de teclas F con el navegador** (dato duro para el plan "todos los F"):
> `F4` está libre en Chrome/Edge y `preventDefault()` la captura sin problema. Pero **`F5` es
> recargar la página**, `F6` mueve el foco a la barra de direcciones y `F11` es pantalla
> completa — se pueden interceptar con `preventDefault()`, pero es agresivo (el usuario pierde
> el F5 de recargar). **`F12` (DevTools) no se puede interceptar en Chrome.** Además el `.exe`
> ya reorganizó las teclas respecto del fuente. → El mapa de teclas de Buslink hay que
> **decidirlo, no heredarlo**. Ver pregunta 5.

### 5.3 El diálogo `AvisoViajeDialog` (mejorando el FoxPro sin traicionarlo)

```
┌─ Aviso sobre el viaje ───────────────────── Reserva 1529271 ─┐
│  Servicio:  lun 03/08/2026 18:30    HOTEL a CENA SHOW …      │
│                                                              │
│  Avisar:   [ 30 min ] [ 45 min ] [ 60 min ] antes            │
│            ────────── o una hora exacta ──────────           │
│            [ 03/08/2026 ]  [ 17 ] : [ 30 ]                   │
│                                                              │
│            → Avisa a las 17:30, 60 min antes del servicio    │
│                                                              │
│  [ ] Volver al aviso automático (10 min antes)               │
│                                                              │
│                                  [ Cancelar ]  [ Grabar ]    │
└──────────────────────────────────────────────────────────────┘
```

Diferencias deliberadas contra el FoxPro, todas justificadas por los datos de §2.4:

| Cambio | Por qué |
| --- | --- |
| Chips **30 / 45 / 60 min antes** | El 98% de los avisos manuales cae entre 16 y 60 min; 60 min es el 64% |
| Default precargado = `hs_inicio − aviso_tiempo` (no `hs_inicio`) | El default del FoxPro es inválido: si apretás Aviso sin tocar nada, da error |
| Línea "→ Avisa a las HH:MM, N min antes" | Feedback inmediato; en FoxPro no ves la antelación |
| "No Avisar" renombrado **"Volver al aviso automático"** | El label del FoxPro miente: no deja de avisar, vuelve al default de 10 min |
| Sin el cartel rojo del REFRESH | En Buslink el refresh es automático (timer de 60 s ya existente) |
| Se registra en `viaje_log` (motivo `AVISO`) | FoxPro no audita este cambio; Buslink sí puede |

### 5.4 El motor de alarmas en Blazor

Sobre el `AutoRefreshLoop` existente (mismo tick de 60 s, sin timer nuevo):

```csharp
// pseudo — dentro del while del PeriodicTimer, después del chequeo de versión
if (_avisosActivos && Permisos.Tiene('C'))
{
    var pendientes = await Reports.GetAvisosDelMinutoAsync(DateTime.Now);
    if (pendientes.Count > 0) await MostrarAviso(pendientes);
}
```

Query (SQL Server 2012 — sin `STRING_AGG`/`TRIM`; fechas en `yyyyMMdd`):

```sql
SELECT v.id_viaje, v.hs_inicio, v.cronograma, v.cronogram2, v.interno,
       v.id_chofer, v.nombre_cho, v.destino, v.chequeo, v.estado_via
FROM viaje v
WHERE v.f_reserva = @hoy            -- índice ix_viaje_f_reserva (f_reserva,_deleted,estado_via)
  AND v._deleted = 0
  AND v.estado_via NOT IN ('CANCELADO','FINALIZADO','FACTURADO')
  AND CONVERT(varchar(5), COALESCE(v.hs_aviso, DATEADD(minute, -@avisoMin, v.hs_inicio)), 108)
      = @hhmm
ORDER BY v.hs_inicio;
```

- `@avisoMin` sale de `parametro.aviso_tiem` (hoy 10), leído una vez por sesión.
- El filtro por estado **no está en el FoxPro** — ahí un servicio CANCELADO también dispara la
  alarma. Es una mejora; confirmar (pregunta 3).
- Ancla en `f_reserva` **siempre**: la tabla `viaje` **no tiene índice por `id_viaje`** (PK es
  `_sync_id`, de la réplica). Sin `f_reserva` en el WHERE, cada consulta escanea 512.876 filas.

#### 🐛 El bug de la medianoche (hallado al implementar, 03/08/2026)

Un servicio que sale **00:01** tiene su aviso automático a las **23:51 del día ANTERIOR**. No es
un caso raro: son **1.226 servicios en 2026** (los transfers de medianoche — el primero de la
grilla en la captura del usuario sale 00:01).

- **Qué hace el FoxPro:** arma el cursor con los viajes de HOY (`str_f_reserva = DTOS(DATE())`)
  y compara **solo HH:MM** → le hace sonar la alarma a las 23:51 **de hoy**, o sea **24 horas
  tarde**, cuando el servicio ya salió.
- **Qué hace Buslink:** el rango de `f_reserva` de la query llega hasta **el día siguiente**, y
  la comparación es por **datetime completo**. Así el aviso del servicio de mañana 00:01 suena
  hoy 23:51, que es cuando sirve.

```sql
WHERE v.f_reserva >= CAST(@desde AS date)
  AND v.f_reserva <= DATEADD(day, 1, CAST(@hasta AS date))   -- ← incluye la madrugada de mañana
  ...
  AND COALESCE(v.hs_aviso, DATEADD(minute, -@def, v.hs_inicio)) BETWEEN @desde AND @hasta
```

Sigue siendo un *index seek* sobre `ix_viaje_f_reserva` (rango de 2 días).

**Mejora fuerte que el FoxPro no puede dar — ventana de gracia:** el match por HH:MM exacto
pierde la alarma si el operador estaba en otra pantalla o recargó el navegador en ese minuto.
En Blazor conviene disparar los avisos **de los últimos N minutos que todavía no se mostraron**
(set de `id_viaje` ya avisados en la sesión). Ver pregunta 2.

### 5.5 El popup disparado

Réplica de lo que el autor quiso (`trafico_aviso.scx`) más que de lo que quedó vivo:

- Banda roja: **"Atención — Aviso de las 17:30 · 3 servicios"**.
- Grilla chica: Reserva · H.Ini · U/Pr · U/As · Recorrido · Chofer · Chq.
- Doble clic en una fila → abre el **Zoom del Viaje** (ya existe).
- Sonido: `Chr(7)` del FoxPro / `ringin.wav` → en web, un `<audio>` corto.
  ⚠️ **El navegador bloquea el audio automático hasta que el usuario interactúe con la página**
  (autoplay policy). En una pantalla que se usa todo el día no es problema real, pero hay que
  saberlo.
- Auto-cierre a los 10 s como el FoxPro, **pero** sin robar el foco si el operador está
  tipeando (en FoxPro es modal y te tapa lo que estés haciendo).

---

## 6. Estado / día D — lo que NO se puede activar todavía

`hs_aviso` es una columna de `viaje`, y **`viaje` sigue siendo de FoxPro hasta el día D**
(regla strangler, `abm-metrocar`). Consecuencia dura:

> Si Buslink escribiera `hs_aviso` hoy en SQL, **la próxima replicación DBF→SQL de esa fila lo
> pisa** con lo que tenga el DBF de FoxPro. La escritura se perdería sin aviso.

Por eso el F4 se construye con el patrón **andamiaje**: diálogo + servicio + SQL escritos y
compilando, pero con un flag `AvisoViajeActivo = false` en `AbmFeatureFlags` que aborta antes de
tocar la base. Se enciende el día D junto con el resto del circuito.

**Lo que SÍ se puede entregar y usar ya, sin esperar el día D:** todo el lado **lectura** —
el motor de alarmas, el popup con la lista, el toggle "Avisos" y el atajo. Las alarmas
funcionan con los `hs_aviso` que carga FoxPro y con el default de 10 min. Es decir: **el
operador ya recibe los avisos en Buslink aunque siga cargándolos desde el Metrocar.**

Orden sugerido:

1. **Ahora** — motor de alarmas + popup + toggle + permiso `C` (100% lectura, cero riesgo).
2. **Ahora** — diálogo F4 completo en modo **solo lectura** (muestra el aviso actual, botón
   Grabar deshabilitado con tooltip "se habilita el día D").
3. **Día D** — flag en `true` + bloquear el F4 en FoxPro.

---

## 7. Estado de la implementación (03/08/2026)

Construido y compilando. Decisiones tomadas por el usuario ese día: motor activo + diálogo con
andamiaje · ventana de gracia de 5 min · popup con la lista · solo las teclas F libres.

| Pieza | Archivo | Estado |
| --- | --- | --- |
| Config global (`parametro`) | `ReportService.GetAvisoConfigAsync` | ✅ activo |
| Motor de alarmas (query) | `ReportService.GetAvisosPendientesAsync` | ✅ activo |
| Datos del viaje para el F4 | `ReportService.GetAvisoViajeAsync` | ✅ activo |
| Escritura de `hs_aviso` | `AbmService.GrabarAvisoViajeAsync` | 🔒 andamiaje (`AvisoViajeActivo = false`) |
| Diálogo del F4 | `Components/Shared/AvisoViajeDialog.razor` | ✅ abre · Grabar bloqueado |
| Popup de la alarma | `Components/Shared/AvisosDisparadosDialog.razor` | ✅ activo |
| Campanita (WebAudio, sin archivo) | `wwwroot/descarga.js` → `traficoBeep()` | ✅ activo |
| Enganche del timer (60 s) | `PlanillaTrafico.AutoRefreshLoop` → `ChequearAvisos` | ✅ activo |
| Toggle "Avisos" (checkbox Chequeo) | `PlanillaTrafico` — visible solo con permiso `C` | ✅ activo |
| Ítem de menú contextual + tecla F4 | `PlanillaTrafico` | ✅ activo |

**Verificado** (03/08/2026, réplica local + capturas Playwright con el usuario CEBALLOS, que sí
tiene `C`):

- La query devuelve el servicio correcto en la ventana normal (viaje 1529271, sale 18:30, avisa
  18:20) y **los 14 servicios de medianoche** del 30/12 en la ventana del 29/12 a las 23:51.
- El diálogo del F4 abre desde el menú contextual, precarga el aviso automático, valida en vivo
  ("Esa hora ya pasó: el aviso no va a sonar") y muestra el candado del andamiaje.
- El popup dispara solo: con dos avisos sembrados a las 23:20 (dato de prueba revertido a NULL),
  saltó a las 23:24 con la lista de los 2 servicios — o sea la **ventana de gracia funciona**.

**Diferencias con el diseño original de §5:** el aviso que entra por la ventana de gracia se
marca **"atrasado"** en naranja (el operador tiene que saber que se le pasó), y el auto-cierre
del popup se cancela al pasar el mouse por encima (20 s en vez de los 10 s del FoxPro: hay una
lista para leer, no un cartel de una línea).

**Pendiente para el día D:** poner `AvisoViajeActivo = true`, bloquear el F4 en FoxPro y apagar
la sync de `viaje`. Nada más — el resto ya está.

---

## 8. Preguntas abiertas (para el dueño)

1. **¿El aviso automático de 10 minutos alcanza?** Los datos dicen que cuando cargan a mano
   piden 60. ¿Subimos `parametro.aviso_tiem`? (Es un cambio en FoxPro, afecta a los dos
   sistemas.)
2. **Ventana de gracia:** ¿el aviso se pierde si nadie estaba mirando (fiel al FoxPro) o se
   muestra atrasado hasta N minutos después?
3. **¿Un servicio CANCELADO debe disparar alarma?** Hoy en FoxPro sí (no filtra estado).
4. **La regla del `−2100 / −150`** (aviso a 35 min exactos → se corre a 37,5): no tiene
   explicación en el fuente. ¿Alguien la recuerda? Propuesta: no copiarla.
5. **Mapa de teclas F en la web:** F4 no molesta, pero F5 (recargar) y F11 (pantalla completa)
   sí. ¿Se interceptan igual por fidelidad, o Buslink usa otro esquema (ej. F-keys libres +
   menú contextual como camino principal)?
6. **"No Avisar":** ¿debería existir un aviso realmente apagado para un viaje puntual? Hoy no
   se puede: o hay hora manual, o rige el automático.

---

## 9. Forms y fuentes de esta capa

| Archivo | Rol |
| --- | --- |
| `Forms/trafico_hs_aviso.scx` | **El diálogo del F4** (setter de `hs_aviso`) |
| `Forms/trafico2.scx` | `KeyPress` (F4 = keycode −3), `viaje_hora_aviso`, `arma_grid_viaje_chequeo`, `Timer1`, `Init` (gating por `C`), filtro `AVISO_HORA` |
| `Progs/funcion.prg:2272` | `formInicioServicio` — el popup vivo |
| `Forms/trafico_aviso.scx` | "Aviso de Chequeo" — popup rico **muerto**; el diseño a copiar |
| `Forms/trafico_chequeo_agencia.scx` | Circuito **distinto** (`chequeo_ag`, columna **Ag**) — no confundir |
| `Forms/scheduler_aviso.scx` | Nada que ver (módulo Scheduler, permiso `H`, muerto) |
