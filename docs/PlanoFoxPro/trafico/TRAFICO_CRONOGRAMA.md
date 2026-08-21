# TRAFICO_CRONOGRAMA — Diagramación / cambio de cronograma (teclas F de `trafico2.scx`)

> Extraído de `trafico2.scx` (KeyPress + `viaje_cambia_cronograma`) + `trafico_cambia_cronograma.scx`
> + `trafico_cambia_rango_trabajo.scx` + `trafico_aviso.scx` (21/07/2026, scx_dump).
> Completa la tríada de escritura de Tráfico con `TRAFICO2_TOOLBAR.md` (botones) y
> `TRAFICO_ZOOM.md` (Zoom). **Este doc documenta una capa de escritura que NO estaba en
> ningún plano previo: la _diagramación_ (armar el cronograma), previa a la asignación física.**

---

## 0. El descubrimiento — hay TRES unidades por viaje, no una

La grilla tiene tres columnas de "unidad" que se suelen confundir. Son tres momentos
distintos del ciclo de un servicio:

| Columna grilla | Campo FoxPro | Réplica SQL | Significado | Quién la escribe |
| --- | --- | --- | --- | --- |
| **U/Pr** | `cronogramacbio` | `cronogram2` | **Unidad Programada** — lo que planeó el *diagramador* | Diagramador (tecla, modo "diagramador") |
| **U/Cb** | `cronograma` | `cronograma` | **Unidad del Cronograma** — la unidad prevista vigente | Diagramador y Operador (teclas F6–F9, Ctrl+F8) |
| **U/As** | `id_interno` / `interno` | `id_interno` / `interno` | **Unidad que Cumple Servicio** — la real que sale a la calle | Asignación física (**Asig U/P**, `TRAFICO2_TOOLBAR.md`) |

El ciclo de vida de un servicio, de punta a punta:

```
Reserva NACE  ─►  Diagramación         ─►  Ajuste operativo      ─►  Asignación física  ─►  Servicio
(Reservas)        (arma el cronograma)     (cambia unidad prev.)     (Asig U/P)             (CURSO→FIN)
cronograma='S/C'  U/Pr + U/Cb = NT0049     U/Cb = NT0051            U/As = interno real
                  (modo diagramador)       (modo operador + log)    + chofer + estado
```

> **Clave de negocio:** el "cronograma" es la **planificación** de qué unidad debería cubrir
> cada servicio (la arma el diagramador la noche anterior / temprano). La **asignación** (Asig
> U/P) es la unidad+chofer real que efectivamente lo hace. Cambiar el cronograma **NO** asigna
> nada: solo mueve texto en `viaje.cronograma`/`cronogram2`. Por eso es la operación de
> escritura de **menor riesgo** de todo Tráfico (no toca `vehiculo`, ni odómetro, ni GPS).

---

## 1. Las 9 teclas de la grilla (pantalla de ayuda "Zoon" del `.exe`)

La pantalla de ayuda del EXE productivo (F1 / botón de ayuda) lista:

| Tecla | Caption en la ayuda | Qué hace | ¿Escribe? | Migrado |
| --- | --- | --- | --- | --- |
| **F2** | Alta de Novedades | `libro_novedad_alta` → alta en `libro_novedad` | ✅ `libro_novedad` | ✅ lectura (`NovedadViajeDialog`) |
| **F3** | Refresh | `bRefresh.Click` (recarga la grilla) | ❌ | ✅ (auto-refresh 60s) |
| **F4** | Aviso sobre el viaje | `viaje_hora_aviso` → form **`trafico_hs_aviso`**: fija la **hora de la alarma** del viaje. Plano completo: **`TRAFICO_F4_AVISO.md`** | ✅ `viaje.hs_aviso` | ❌ **PENDIENTE** |
| **F5** | Cambia Fechas de trabajo | `trafico_cambia_rango_trabajo` → fija rango de trabajo (deshabilita `<< >>`). **Requiere acceso `D`** | ❌ (solo filtro) | ✅ (filtro Rango de Fechas) |
| **F6** | Cambia Cronograma **S/C** | diálogo con opción **S/C** → `cronograma='S/C'` | ✅ `viaje.cronograma` | ❌ **PENDIENTE** |
| **F7** | Cambia Cronograma a Empresa: **NORTUR** | diálogo opción **NORTUR** → `cronograma='NORTUR'` | ✅ `viaje.cronograma` | ❌ **PENDIENTE** |
| **F8** | Cambia Cronograma a **Fletero** | diálogo opción Cronograma + fletero → `cronograma=fletero+interno` | ✅ `viaje.cronograma` | ❌ **PENDIENTE** |
| **Ctrl+F8** | Copia Cronograma del Diagramador | copia U/Pr (`cronogram2`) → U/Cb (`cronograma`) | ✅ `viaje.cronograma` | ❌ **PENDIENTE** |
| **F9** | Cambia Cronograma a **Flota propia** | diálogo opción Cronograma + NT → `cronograma='NT0049'` | ✅ `viaje.cronograma` | ❌ **PENDIENTE** |

> ⚠️ **El fuente en disco (jul 2021) tiene un binding VIEJO de las teclas** (en el `KeyPress` del
> `.scx`: F3=Refresh, F4=aviso, F5=rango, F8/F9=`ubicar_gps`, Ctrl+F8=toggle cancelados). El
> `.exe` productivo reorganizó las teclas a lo que muestra la ayuda (F6–F9 = atajos rápidos que
> pre-seleccionan la opción del diálogo). **El mecanismo de fondo es el mismo** en ambas:
> `viaje_cambia_cronograma()` + `trafico_cambia_cronograma.scx`. Migrar por el mecanismo, no por
> el número de tecla.

### El disparador real en el fuente (`KeyPress` de `trafico2`)

Cualquier tecla **alfanumérica** sobre la grilla (Enter, 0-9, A-Z) dispara el cambio de
cronograma, ruteado por permiso:

```foxpro
If nKeyCode = 13 .Or. Between(nKeyCode,48,57) .Or. Between(nKeyCode,65,90) .Or. Between(nKeyCode,97,122)
    Do Case
    Case "D" $ cAcceso                                    && diagramador
        Thisform.viaje_cambia_cronograma(cLetra,"diagramador")
    Case "T" $ cAcceso                                    && operador de tráfico
        Thisform.viaje_cambia_cronograma(cLetra,"operador")
    Endcase
Endif
```

- La `cLetra` tipeada se pasa al diálogo (para arrancar filtrando el combo de internos).
- **El permiso decide el modo** (ver §3): `D` = diagramador, `T` = operador. Un usuario con
  ambos entra por `D` (el `Do Case` corta en el primero).

---

## 2. El diálogo `trafico_cambia_cronograma.scx` (Imágenes 2 y 3 del usuario)

Caption: **"Cambia / Busca - Cronograma del Viaje"**. Es un modal chico (410×400).

**Parámetros de entrada:** `lpCronogramaAnterior` (cronograma actual), `lpLetra` (tecla que lo
disparó), `lpTipoCambio` (`"diagramador"`/`"operador"`), `lpIdVehiculoTipo` (tipo de unidad).

**Controles:**

| Control | En la imagen | Fuente de datos / rol |
| --- | --- | --- |
| `Text1` (caja azul) | **Cronograma** = `NT0049` | cronograma actual (solo lectura) |
| `Text3` (caja negra) | **Unidad Solicitada** = `BUS` | `viaje.id_vehiculo_tipo` (solo lectura) |
| `Text2` (banda roja) | título "**Cambio de Unidad**" (operador) / "Diagrama" (diagramador) | según modo |
| `fletero` (combo) | **Fletero** = `NT` | `SELECT cronograma,nombre,id_contratado,diagrama FROM fletero WHERE Empty(f_delete) ORDER BY orden` |
| `cronograma` (combo) | **Cronograma** (internos) | internos activos del fletero elegido: `SELECT interno FROM vehiculo a JOIN fletero b … WHERE a.activo AND b.cronograma=@fletero` |
| `Optiongroup1` (3 radios) | **Especial**: Cronograma · S/C · NORTUR | option3.Caption = `parametro.id_cliente_prueba` (=`NORTUR`) |
| `motivo` (combo) | **Motivo** = `CAMBIO DE SERVICIO` | `SELECT nombre FROM viaje_motivo_tarde ORDER BY nombre` (¡reusa el catálogo de "motivos de llegadas tarde"!) |
| `bAceptar` | **Reserva Actual** | devuelve `1` (cambia SOLO este viaje) |
| `bAceptarTodo` | **Todas las Reservas** | devuelve `2` (masivo; **solo diagramador**) |
| `bCancelar` | **Cancelar** | devuelve `0` |

**Cómo se arma `cCronogramaNuevo`** (según el radio "Especial", en `bAceptar.Click`):

```foxpro
Do Case
Case Optiongroup1.option1.Value = 1          && "Cronograma"
    If tipousuario = 1 (diagramador)
        If internoDiagrama                    && el fletero tiene flag diagrama (NT = flota propia)
            cCronogramaNuevo = Alltrim(fletero.Value) + Right("0000"+Alltrim(cronograma.Value),4)   && "NT"+"0049" = "NT0049"
        Else
            cCronogramaNuevo = id_contratado  && empresa entera: "TEDESCHI", "VANSQ", …
        Endif
    Else (operador)
        cCronogramaNuevo = Alltrim(fletero.Value) + Right("0000"+Alltrim(cronograma.Value),4)
    Endif
Case Optiongroup1.option2.Value = 1          && "S/C"
    cCronogramaNuevo = "S/C"
Case Optiongroup1.option3.Value = 1          && "NORTUR"
    cCronogramaNuevo = option3.Caption        && "NORTUR"
Endcase
cCronogramaMotivo = motivo.Value
```

**Validaciones del diálogo:**
1. Opción "Cronograma" + interno vacío (diagramador con flota que diagrama / operador siempre) → error "No se cargó el cronograma o no existe la unidad".
2. **Modo operador + motivo vacío → error "Debe cargar un motivo de cambio de unidad".** (En diagramador el motivo está **deshabilitado**.)
3. "Todas las Reservas" exige el combo de cronograma cargado.

**Mapeo tecla → radio pre-seleccionado (el `.exe`):** F6→S/C, F7→NORTUR, F8→Cronograma+fletero,
F9→Cronograma+flota propia (NT). Ctrl+F8 (Copia del Diagramador) es un atajo aparte que copia
`cronogram2`→`cronograma` sin abrir el diálogo.

---

## 3. Los dos modos — la diferencia CRÍTICA (qué escribe cada uno)

`viaje_cambia_cronograma(cLetra, lpTipoCambio)` en `trafico2.scx`. El `lpTipoCambio` cambia
por completo qué se escribe:

### 3.1 Modo "diagramador" (acceso `D`) — planifica

Escribe **las dos columnas** (U/Pr y U/Cb) y **NO deja log**. El diagramador arma el tablero.

```sql
-- viaje simple (id_viaje_int = 0):
UPDATE viaje SET cronogram2 = @nuevo, cronograma = @nuevo, chequeo = 0 WHERE id_viaje = @id;
-- ruta (id_viaje_int <> 0): mismo UPDATE por WHERE id_viaje_int = @idInt (todos los tramos)
-- SIN viaje_log
```

- Puede ser **masivo** ("Todas las Reservas", devuelve 2): recorre todos los viajes del día con
  el **mismo cronograma anterior Y `interno = 0`** (sin asignar todavía) y les pone el nuevo.
  Si el cambio afecta a >1 cronograma distinto → pide confirmación.
- El motivo está deshabilitado (no aplica).

### 3.2 Modo "operador" (acceso `T`) — ajuste con auditoría

Escribe **solo U/Cb** (`cronograma`), exige **motivo** y **deja log** `CBIO UNIDAD`.

```sql
-- viaje simple:
UPDATE viaje SET cronograma = @nuevo, chequeo = 0 WHERE id_viaje = @id;
INSERT INTO viaje_log (id_viaje, usuario, motivo, hora, cronograma, id_chofer,
                       interno_ori, interno_new, comentario)
VALUES (@id, @usuario, 'CBIO UNIDAD', GETDATE(), @nuevo, '', 0, 0, @motivoCambio);

-- ruta: UPDATE … WHERE id_viaje_int = @idInt; luego un INSERT viaje_log POR CADA tramo
```

- Solo "Reserva Actual" (no masivo).
- No toca U/Pr — respeta lo que planeó el diagramador; solo mueve la unidad vigente.

### 3.3 Resumen operación → escritura (para `ViajeAbmService`)

| Operación | `viaje` | `viaje_log` | Alcance |
| --- | --- | --- | --- |
| Cambio cronograma **diagramador** | `cronogram2` + `cronograma` + `chequeo=0` | — | simple / ruta / **masivo (mismo cron + interno=0)** |
| Cambio cronograma **operador** | `cronograma` + `chequeo=0` | `CBIO UNIDAD` (por tramo en ruta) | simple / ruta |
| **Copia del Diagramador** (Ctrl+F8) | `cronograma = cronogram2` | (a confirmar contra `.exe`) | fila actual |

> **Todo cambio de cronograma resetea `chequeo = 0`** (si cambió la unidad prevista, el chequeo
> anterior ya no vale).

---

## 4. Por qué esta capa es el PRIMER write-ABM ideal de Tráfico

Comparado con el resto del circuito (`TRAFICO2_TOOLBAR.md`), el cambio de cronograma es el de
menor superficie de impacto:

| Toca… | Cronograma (esta capa) | Asignar / Liberar (toolbar) |
| --- | :-: | :-: |
| `viaje` (2-3 campos) | ✅ | ✅ (10+ campos) |
| `vehiculo` (máquina de estados viva) | ❌ | ✅ |
| `vehiculo_km` (odómetro) | ❌ | ✅ |
| `viaje_adicional` (agua/hs extra) | ❌ | ✅ |
| `gps_xlm()` (notifica GPS) | ❌ | ✅ |
| `chofer_franco` (franco trabajado) | ❌ | ✅ |
| Anti-doble-asignación (relee viaje) | recomendable | **obligatorio** |

Es un `UPDATE` + `INSERT` de log autocontenido → calza directo en el patrón `AbmService`
(transacción + `SqlParameter`). Ejercita **una sola vez** toda la infraestructura difícil
(transacción, `f_reserva` en el WHERE por falta de índice, gating de permiso `D`/`T`, ruta
multi-tramo, `viaje_log`) sin el riesgo de la máquina de estados de `vehiculo`. Es el "ruedín"
antes de Asignar/Liberar.

**Dependencias de datos (ya disponibles):** catálogo `fletero` (`GetFleterosAsync` ya existe) +
internos por fletero (query nueva chica) + `viaje_motivo_tarde` (catálogo de motivos). Ninguna
tabla nueva.

**Reglas de oro al migrar (regla-madre `abm-metrocar` + `ESCRITURA_CIRCUITO.md`):**
1. `viaje` sigue siendo de FoxPro **hasta el día D** → construir tras `EscrituraViaje`/flag,
   probar contra el server LOCAL.
2. WHERE con `f_reserva` además de `id_viaje` (no hay índice por `id_viaje`).
3. Transacción única (viaje + log). FoxPro no la tiene — mejora obligatoria.
4. Ruta (`id_viaje_i > 0`): pegar a todos los tramos, log por tramo.
5. Gating de permiso: `D` habilita modo diagramador (masivo, sin motivo); `T` habilita modo
   operador (con motivo obligatorio). Ver skill `seguridad-nortur`.
6. Contra-concurrencia: releer con `UPDLOCK` dentro de la transacción (la web es multiusuario;
   FoxPro era efectivamente mono-usuario).

---

## 4-bis. Estado de la implementación (04/08/2026)

Construido y compilando. Decisiones del usuario (03/08/2026): disparadores = **menú contextual +
teclas F6-F9 Y tipear una letra** (las dos cosas) · masivo **con preview** · motivos **tal cual
el catálogo compartido** · escritura en **andamiaje**.

| Pieza | Archivo | Estado |
| --- | --- | --- |
| Catálogo de fleteros (prefijo/diagrama/id_contratado) | `ReportService.GetFleterosCronogramaAsync` | ✅ |
| Internos activos por fletero | `ReportService.GetInternosPorFleteroAsync` | ✅ |
| Motivos (`viaje_motivo_tarde`) | `ReportService.GetMotivosCambioUnidadAsync` | ✅ |
| `parametro.id_cliente_prueba` (el radio NORTUR) | `ReportService.GetClientePruebaAsync` | ✅ |
| Cambio simple (+ rutas multi-tramo) | `AbmService.CambiarCronogramaAsync` | 🔒 andamiaje (`CronogramaAbmActivo`) |
| Cambio masivo | `AbmService.CambiarCronogramaMasivoAsync` | 🔒 andamiaje |
| Ctrl+F8 copia U/Pr → U/Cb | `AbmService.CopiarCronogramaDiagramadorAsync` | 🔒 andamiaje |
| Diálogo | `Components/Shared/CronogramaDialog.razor` | ✅ abre · Grabar bloqueado |
| Submenú + teclas F6-F9 / Ctrl+F8 / letras | `PlanillaTrafico.razor` | ✅ |

**Verificado en pantalla (04/08/2026, capturas Playwright contra el server de producción, solo
lectura):**

- **Modo diagramador** (SUPERVISOR, tiene `D`): título "Diagrama", motivo **deshabilitado**,
  checkbox del masivo presente, preview `Queda como NT0001 · U/Pr y U/Cb`.
- **Modo operador** (DAMIAN, `TCVLA`): título "Cambio de Unidad", motivo **habilitado y
  obligatorio**, **sin** checkbox de masivo, preview `… · solo U/Cb (se respeta lo que programó
  el diagramador)`.
- El preview del masivo cuenta bien el alcance (dice explícitamente cuándo no hay otras filas).
- El submenú muestra los 5 ítems con su atajo (F9/F8/F7/F6/Ctrl+F8), y "Copiar el del
  diagramador" se deshabilita solo cuando U/Pr está vacío o ya coincide con U/Cb.

### 🐛 El bug del masivo en el fuente (NO copiar)

`bAceptarTodo.Click` graba `cCronogramaNuevo = thisform.cronograma.Value` — **el interno pelado**,
sin el prefijo del fletero ni el pad a 4 dígitos (`49` en vez de `NT0049`) — y además **ignora el
optiongroup** (S/C y NORTUR no se aplican nunca por esa vía). `bAceptar` (Reserva Actual) sí lo
arma bien.

Búsqueda del síntoma en la base: **0 cronogramas puramente numéricos en 512.876 filas**. O el
`.exe` productivo ya lo corrigió, o nadie usó nunca ese botón. En Buslink el masivo arma el
código con la misma función que el simple.

### Diferencias deliberadas con el FoxPro

| Cambio | Por qué |
| --- | --- |
| Preview del alcance ANTES de grabar | El FoxPro solo confirma si hay >1 cronograma distinto en el cursor — que es casi siempre, así que el cartel perdió sentido y se aprieta sin leer |
| Transacción única en el masivo | El FoxPro va fila por fila sin transacción: si falla a mitad, deja el tablero mezclado |
| Relectura con `UPDLOCK` | La web es multiusuario; FoxPro era efectivamente mono-usuario |
| `id_viaje_i` se lee en el servidor, no viene de la pantalla | La grilla no lo trae, y así se lee fresco dentro de la transacción |
| Ctrl+F8 loguea en modo operador | Coherente con el resto de la capa (el atajo no está en el fuente en disco — ver §1) |
| `Enter` NO abre el cambio de cronograma | En Buslink ya abre el Zoom del Viaje. El FoxPro lo incluye entre las teclas del cronograma; se prioriza lo que el usuario de Buslink ya tiene aprendido |

### Trampa de datos encontrada al implementar

`fletero` tiene **dos borrados distintos**: `f_delete` (baja de NEGOCIO, la que hace FoxPro) y
`_deleted` (baja de la RÉPLICA). Filtrando solo por `_deleted` salen **28** fleteros; con el
filtro del FoxPro son los **22** vigentes. Hay que poner los dos.
⚠️ `GetFleterosAsync` (el combo del filtro "Fleteros", ya existente) filtra solo por `_deleted`
→ arrastra el mismo problema. No se tocó en esta entrega.

---

## 5. Forms FoxPro de esta capa

`trafico2.scx` (`KeyPress` + `viaje_cambia_cronograma` + `cronograma_normaliza`),
`trafico_cambia_cronograma.scx` (el diálogo), `trafico_cambia_rango_trabajo.scx` (F5, solo
filtro), `trafico_aviso.scx` (F4, chequeo pendiente), `trafico_cronograma_normaliza.scx`
(normalización de cronogramas entre dos fechas — utilitario del diagramador, aún sin extraer
a fondo).
