# LIBRO DE NOVEDADES — el submenú completo (lista · envío de correos · destinatarios)

> Extraído el 19/08/2026 de `libro_novedad.scx`, `libro_novedad_envia_correo.scx`,
> `libro_novedad_parametro.scx`, `libro_novedad_abm.scx` y `funcion.prg`
> (`envio_correo_gmail`). Datos medidos contra la réplica **fresca** (`DESKTOP-CV6LF0O\SQLEXPRESS`
> y `172.25.80.234`, ambas al 19/08/2026 04:44).
>
> Complementa `TRAFICO_F2_NOVEDADES.md`, que cubre el **alta** de una novedad desde la grilla
> (tecla F2). Este documento cubre los **tres ítems del menú** `Tráfico → Libro de Novedades`,
> que hasta ahora estaban sin relevar.

---

## 0. Los tres ítems y qué es cada uno

```foxpro
DEFINE POPUP librodenov ...
DEFINE BAR 1 OF librodenov PROMPT "Libro de Novedades"              → libro_novedad.scx
DEFINE BAR 3 OF librodenov PROMPT "Envio de correos"                → libro_novedad_envia_correo.scx
DEFINE BAR 5 OF librodenov PROMPT "Correos Electronicos Parametros" → libro_novedad_parametro.scx
```

| Ítem | Qué es | Tablas que toca |
| --- | --- | --- |
| **Libro de Novedades** | La lista del libro de guardia, con Agregar / Eliminar / Modificar / Ver datos reserva | `libro_novedad` (lee, borra, actualiza) |
| **Envío de correos** | Un **batch**: junta lo que no salió, arma 4 correos de texto plano, los manda a la lista interna y estampa `f_envio` | `libro_novedad`, `siniestro`, `parametro`, `taller_service`, `vehiculo_sobre` |
| **Correos Electrónicos Parámetros** | El ABM de la **lista de distribución interna** (a quién le llega cada informe) | `libro_novedad_parametro` |

⚠️ **No confundir dos listas de correo distintas:**
- `libro_novedad_parametro` = **12 contactos internos de la empresa** (gerencia@, monitoreo1@,
  paulaalvarez@, proveedores@…). Es la de este submenú.
- `cliente.contacto1..10` / `email1..10` = los contactos **del cliente**, que reciben el aviso
  automático cuando se carga una novedad sobre su reserva (§4 de `TRAFICO_F2_NOVEDADES.md`).
  Ese circuito es otro y **no** se migró.

---

## 1. Libro de Novedades (`libro_novedad.scx`)

### 1.1 La grilla

```foxpro
Select * From libro_novedad ORDER BY f_carga DESC Into Cursor cursorLibroNovedad
```

Sin filtro y sin techo: **las 48.617 filas** (desde el 18/05/2012). Cuatro columnas — F. Carga,
Asunto, U. Creador, Nº Viaje — y a la derecha un `editbox` con el **mensaje completo** de la fila
parada, que se refresca en `AfterRowColChange`.

### 1.2 La botonera

| Botón | Qué hace realmente |
| --- | --- |
| **Agregar** | `do form libro_novedad_abm with "alta", "", 0` → novedad **suelta** (`id_viaje = 0`), asunto precargado con el nombre de la empresa |
| **Eliminar** | ✅ **Borra de verdad**: `DELETE FROM libro_novedad WHERE id = …` — **baja FÍSICA**, sin confirmación de auditoría, sin `f_delete` |
| **Modificar** | `libro_novedad_abm` modo "modifica" → **solo cambia el `mensaje`** |
| **Ver datos reserva** | Habilitado solo si `id_viaje ≠ 0` → abre `libro_novedad_view_viaje` |

> 🐛 **Ojo, hay DOS "Eliminar" y solo uno funciona.** El de **esta lista** borra. El del form
> `libro_novedad_abm` (el que se abre desde el F2) está **roto en el fuente**: su `DELETE` está
> comentado *y además apunta a la tabla `agenda`* (copy-paste de otro ABM). Confirmás la baja, la
> ventana se cierra y la novedad sigue ahí.

**Y sí se usa:** al 19/08/2026 hay **9 filas con `_deleted = 1`** en la réplica (todas de
julio/agosto de 2026, de RICARDO, DAMIAN, DMORENO y MAURO). O sea: alguien borra novedades desde
esta pantalla con regularidad. *(El doc anterior decía "no hay borradas hoy" — era cierto en
junio; ya no.)*

### 1.3 El modo "modifica" — literal

```foxpro
Case cTipoMov = "modifica"
    If !EMPTY(thisform.mensaje.Value)
        cMensaje = ALLTRIM(thisform.mensaje.value)
        UPDATE libro_novedad SET mensaje = cMensaje WHERE id = nLibroNovedadGoTo
```

**El asunto no se toca.** No es un descuido conveniente: en las novedades de unidad el asunto
(`"int: 8 dom: AD255RA chof:…"`) es el **único nexo** con el interno, porque la tabla no tiene
columna `interno`. Editarlo rompería el filtro de "Novedad sobre la unidad".

---

## 2. Envío de correos (`libro_novedad_envia_correo.scx`)

### 2.1 Qué junta el `Init`

| Bloque | Consulta | Condición para habilitarse |
| --- | --- | --- |
| **Novedades** | `libro_novedad WHERE Empty(f_envio)` | hay al menos una |
| **Siniestros** | `siniestro a, chofer b WHERE a.id_chofer = b.id_chofer AND Empty(f_envio)` | hay al menos uno |
| **Combustible** | `combustible_promedio(desde, hasta)` — rango default: últimos 15 días | `parametro.f_ult_envi < hoy` |
| **Taller** | `taller_service WHERE Between(Ttod(fecha_ini), desde, hasta)` | idem Combustible |

Además cuenta cuántos destinatarios tiene tildado cada informe y, si no hay ninguno o no hay
ninguna suscripción, deshabilita el botón Enviar con un cartel.

> ⚠️ **El INNER JOIN implícito de Siniestros esconde partes.** `FROM siniestro a , chofer b WHERE
> a.id_chofer = b.id_chofer`: un siniestro cuyo chofer ya no está en el padrón **no se envía
> nunca** y nadie se entera. Al 19/08/2026 la tanda está en cero (los 313 siniestros ya tienen
> `f_envio`), así que hoy no oculta nada — pero la trampa está viva.

### 2.2 El cuerpo del correo de NOVEDADES (formato exacto)

Por cada novedad, en orden `f_carga DESC`:

```text
yyyy-mm-dd a las hh:mm usuario: <usuario_cr>
asunto:  <asunto>[ Reserva: <id_viaje>]
[nº interno: <interno> - conductor: <nombre_cho>]        ← si interno ≠ 0
[unidad diagramada para el servicio: <cronograma>]       ← si interno = 0 ("S/C" → "SIN CRONOGRAMA")
[recorrido: <d_destino> / <h_destino> hora: <hs_s_inici>]  ← recortado a 70 caracteres
Mensaje: <mensaje>
----------------------------------------------------------------------
```

Asunto del mail: `"Novedades : dd/mm/aaaa a las hh:mm"`.
Las tres líneas entre corchetes solo aparecen si la novedad cuelga de una reserva **y** el viaje
existe.

### 2.3 El cuerpo del correo de SINIESTROS

La ficha completa del parte de accidente: fecha/hora + quién lo tomó, vehículo de la empresa e
interno, conductor, tipo de accidente, lugar, localidad y provincia, y después ~15 líneas
opcionales que solo salen si el campo está cargado (comisaría, daños, datos del tercero, seguro,
propietario, testigos 1-3). Cierra con la descripción en MAYÚSCULAS y los 70 guiones.

### 2.4 Los tres bugs del `envio.Click`

1. **`f_envio` se estampa aunque el envío falle.** Los `UPDATE … SET f_envio = Date()` están
   **fuera** del bucle de envío y no miran el resultado de `envio_correo_gmail()`. Si el SMTP
   está caído, las novedades quedan marcadas como enviadas y **nadie las vuelve a ver**.
   → Consecuencia práctica: **la columna `f_envio` no prueba que el correo haya llegado.**
2. **El log se pisa en el error.** El ramal OK concatena
   (`edit1.Value = edit1.Value + …`) pero el ramal de error **asigna** (`edit1.Value = …`):
   un solo fallo borra todo lo reportado antes en la pantalla.
3. **CDO con SSL implícito sobre el puerto 25** (ver §2.5). Es la misma combinación
   contradictoria que tenía el botón "Probar envio correo" de Parámetros Empresa.

### 2.5 Cómo manda — `envio_correo_gmail()` (`Progs/funcion.prg`)

Usa el COM `CDO.Message` de Windows con la config guardada en la fila única de `parametro`:

| Campo FoxPro | Columna en la réplica | Valor en producción (19/08/2026) |
| --- | --- | --- |
| `smtp_server` | `smtp_serve` | `mr.fibercorp.com.ar` |
| `smtp_puerto` | `smtp_puert` | `25` |
| `smtp_usuario` | `smtp_usuar` | `traficonortur@nrumbos.com.ar` |
| `smtp_password` | `smtp_passw` | (10 caracteres) |
| `smtp_nombre` | `smtp_nombr` | `Dto. Trafico Nortur SRL <traficonortur@nrumbos.com.ar>` |
| `f_ult_envio_comb` | `f_ult_envi` | `19/08/2026` |

Fija `smtpusessl = .T.` **con el puerto 25**, que es SSL implícito sobre un puerto que no lo
habla. Buslink ya resolvió esto en `CorreoPruebaService` (STARTTLS y, si el servidor no lo
soporta, en claro, informando cuál anduvo).

---

## 3. Correos Electrónicos Parámetros (`libro_novedad_parametro.scx`)

ABM clásico de 4 modos sobre `libro_novedad_parametro`. **No tiene columna `id`**: la PK lógica es
`contacto`, y por eso el campo se deshabilita en modifica y en baja.

| Campo | Largo del textbox | Columna en la réplica | Notas |
| --- | --- | --- | --- |
| `contacto` | 30, `Format = "!"` | `contacto` nvarchar(100) | **siempre en mayúsculas** |
| `email` | 70 | `email` nvarchar(140) | validado con `ValidarCuentaEmail()` (regex) |
| `novedad` · `siniestro` · `combustible` · `auditoria` · `taller` | checkbox | `novedad`, `siniestro`, **`combustibl`**, `auditoria`, `taller` | ⚠️ `combustible` viene **truncado** |

- **Alta:** rechaza el contacto duplicado ("Atención ... contacto ya existe").
- **Baja:** `DELETE FROM libro_novedad_parametro WHERE contacto = …` — **física**.
- **Modifica:** cambia email + los 5 tildes; el contacto queda fijo.

La tabla arrastra además `contacto_1..10` / `email_1..10` de una versión anterior: **ni el form ni
el envío los tocan** (están vacíos).

**Estado en producción (19/08/2026):** 12 destinatarios · 12 reciben Novedades · 8 Siniestros ·
7 Combustible · 6 Auditoría · 8 Taller.

> El tilde **Auditoría** no lo usa ningún bloque de envío del form: existe en la tabla, se cuenta
> en la pantalla (`text9`) y hay 6 personas suscriptas, pero **no hay código que mande un informe
> de auditoría**. Es una suscripción a un correo que no se envía.

---

## 4. Qué se migró a Buslink (19/08/2026)

| Pieza | Dónde vive | Estado |
| --- | --- | --- |
| Lista del libro (grilla + panel de mensaje + Ver datos reserva) | `/libro-novedades` (`Components/Pages/LibroNovedades.razor`) | ✅ solo lectura |
| Filtros por fecha / usuario / origen + buscador de texto + Excel | idem | ✅ **agregado** (no existe en el FoxPro) |
| Modificar (solo mensaje) y Eliminar (baja física) | `AbmService.ModificarNovedadAsync` / `BajaNovedadAsync` + `NovedadEditorDialog` | 🔒 andamiaje (`NovedadesAbmActivo`) |
| Envío de correos — bloques **Novedades** y **Siniestros** | `/envio-correos` + `CorreoNovedadesService` | 🔒 andamiaje (`EnvioCorreosActivo`): previsualiza, no manda |
| Envío — bloques **Combustible** y **Taller** (con adjunto) | — | ❌ no migrados (siguen en el Metrocar) |
| ABM de destinatarios | `/correos-destinatarios` + `DestinatarioCorreoEditorDialog` | 🔒 andamiaje (`DestinatariosCorreoAbmActivo`) |
| Aviso por correo **al cliente** al cargar una novedad (F2) | — | ❌ no migrado (decisión 04/08/2026) |

### Decisiones tomadas al migrar

1. **El envío no manda de verdad.** Decisión del usuario: es una acción hacia afuera y, si
   Buslink y el Metrocar mandaran los dos, cada destinatario recibiría todo duplicado. La
   pantalla arma el correo **exacto** y muestra a quién iría; el botón Enviar está bloqueado por
   flag. Encenderlo exige bloquear el ítem en FoxPro **el mismo día**.
2. **No se copian los 3 bugs de §2.4.** `f_envio` se estampa solo si al menos un destinatario
   recibió el correo; el log acumula los errores en vez de pisarlos; y el envío usa STARTTLS con
   fallback en claro.
3. **La lista no trae las 48.617 filas.** Rango default de 30 días, techo de 5.000 y filtros. La
   grilla usa `Virtualize` (regla de performance del proyecto).
4. **Se muestra la columna `Enviada` (`f_envio`)**, que en el FoxPro existe pero no se ve en
   ninguna pantalla — y la ayuda del informe aclara que *no prueba que el correo haya llegado*.
5. **Cuando no hay tanda pendiente**, la previsualización muestra un **ejemplo** con las últimas
   10 novedades ya enviadas, claramente rotulado. Sin eso el previsualizador queda en blanco
   cada vez que la corrida del día ya salió.
6. **El Libro entró al hub `/informes`** (módulo Tráfico), no al drawer de Tráfico: es una
   consulta con rango de fechas y Excel. Los dos ítems operativos (Envío y Destinatarios) sí
   quedaron en el drawer, bajo el submenú "Libro de Novedades".

### Para activar la escritura (día del corte)

Ninguna de las dos tablas (`libro_novedad`, `libro_novedad_parametro`) pertenece al circuito
`viaje`: son autocontenidas, así que **podrían cortar antes del día D** —igual que hicieron
`usuario` (01/07) y `parametro` (12/08)—. El cutover sería: poner el flag en `true`, sacar la
barra del menú en FoxPro y apagar la sync DBF→SQL de esa tabla.

⚠️ Las dos hacen **baja FÍSICA**: no hay forma de recuperar una novedad borrada desde Buslink.
