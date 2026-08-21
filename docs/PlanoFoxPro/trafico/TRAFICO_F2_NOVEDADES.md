# TRAFICO F2 — Alta de Novedades (libro de guardia) · y el F1/F3

> Extraído el 04/08/2026 de `libro_novedad_abm.scx` (Init + `aceptar.Click` + `audita_carga`) y
> `trafico2.scx` (`libro_novedad_alta`, `libro_novedad_alta_veh`). Datos de uso medidos contra
> **producción** (172.25.80.234). Cierra la serie de teclas de la grilla junto con
> `TRAFICO_F4_AVISO.md` y `TRAFICO_CRONOGRAMA.md`.

---

## 1. Qué es

El **libro de guardia** de la mesa de tráfico. Una novedad es una anotación de turno: lo que
pasó con un servicio o con una unidad, para que el turno siguiente se entere.

Ejemplos reales de producción (04/08/2026):

```text
int: 8 dom: AD255RA chof: OJEDA HUGO ORLANDO
  CHOFER INFORMA QUE TIENE LA PIERNA INFLAMADA, SE LE CUBRE EL PRIMER SERVICIO,
  DEJA UNIDAD EN FLORES Y VA A LABORAL

int: 67 dom: AF021PY chof: LIMOLE CARLOS FERNANDO
  SERVICIO 8:10 OBELISCO-TORRE YPF, SE COMPLETO EN CATEDRAL DEJANDO PASAJEROS ABAJO
```

**Volumen:** 1.594 novedades en 2026 (~5 por día), ~3.000/año desde 2013. Las cargan los mismos
operadores que mueven el cronograma: DAMIAN (420), MAURO (415), PSTELE (398), RICARDO (272).

---

## 2. El hallazgo: la tabla tiene 20 columnas, el alta usa 5

`libro_novedad` arrastra campos de una versión anterior que **hoy no se usan**. Medido sobre las
1.594 filas de 2026:

| Columna | Estado real |
| --- | --- |
| `f_carga`, `asunto`, `mensaje`, `usuario_cr`, `id_viaje` | ✅ las 5 que escribe el alta |
| `prioridad` | vacía en las 1.594 |
| `f_aviso`, `f_aviso_si`, `avisar_en`, `aviso` | vacías — sistema de avisos propio, muerto |
| `telefono`, `radio` | vacías |
| `usuario_de`, `usuario_mo` | vacías |
| `finalizo` | 0 finalizadas en 2026 (el flag existe y la lista lo pinta, pero nadie lo usa) |
| `f_envio` | ⚠️ poblada en 1.593 de 1.594 — **pero NO la escribe el alta** (ver §5) |

El INSERT literal del FoxPro:

```foxpro
Insert Into libro_novedad ( f_carga , asunto , mensaje , usuario_create , id_viaje ) ;
    values ( Datetime() , cAsunto , cMensaje , cUsuario , nId_viaje )
```

> Truncados de la réplica: `usuario_create` → **`usuario_cr`**. `id_viaje` es **bigint** acá (en
> `viaje` es int). `id` **NO es identity** → `MAX(id)+1` (el último de producción era 50154).

**Validación** (`audita_carga`): asunto y mensaje obligatorios. Nada más.

---

## 3. De dónde sale el asunto — tres puntos de entrada

El `Init` precarga el asunto según con qué parámetro lo llamen:

| Llamada | `id_viaje` | Asunto precargado | ¿Correo? |
| --- | --- | --- | --- |
| `libro_novedad_alta` (F2 sobre una fila) | el del viaje | `viaje.nombre_cliente` | ✅ habilitado |
| `libro_novedad_alta_veh` (panel Buses) | `-1` → graba 0 | `int: 8 dom: AD255RA chof: OJEDA` | ❌ |
| Desde el menú Libro de Novedades | `0` | el nombre de la empresa | ❌ |

En producción se reparten casi mitad y mitad: **842 ligadas a un viaje, 752 sueltas** (2026).

---

## 4. El envío de correo al cliente (NO migrado)

Cuando la novedad cuelga de una reserva, el alta puede mandarle un mail automático al cliente:

- Levanta `contacto1..10` / `email1..10` de la ficha del **cliente** del viaje y los muestra con
  un tilde cada uno (precargados en `.T.`). Si el cliente no tiene contactos, deshabilita el
  envío y muestra el cartel "sin correo".
- Arma un cuerpo automático: fecha y hora, número de reserva, recorrido y hora del servicio, y
  el mensaje en MAYÚSCULAS, con el encabezado *"IMPORTANTE NO RESPONDER A ESTA DIRECCION"*.
- Manda con `envio_correo_gmail()` uno por uno y reporta al final "Correo para X: OK" o
  "CON PROBLEMAS".

> **Decisión del usuario (04/08/2026): NO se migra por ahora.** Es una acción hacia afuera de la
> empresa y no conviene que salga de un sistema todavía en prueba. Buslink graba la novedad; el
> correo se sigue mandando desde el Metrocar. Se puede agregar después sin rehacer nada.

---

## 5. Dos cosas rotas en el fuente (no copiar)

**1. La baja no borra nada.** En `aceptar.Click`, rama `"baja"`:

```foxpro
Case cTipoMov = "baja"
    nAnswer = Messagebox(" ¿ Esta totalmente seguro de procesar la baja ? ", ...)
    If nAnswer = 6
        cCodigo = Alltrim(Thisform.codigo.Value)
*Delete From agenda Where id_agenda = cCodigo      && ← COMENTADO, y apunta a `agenda`
        Thisform.lokey = .T.
```

El `DELETE` está comentado **y encima pega a la tabla `agenda`** — copy-paste de otro ABM, el
mismo patrón que el Modificar roto de `viaje_motivo_cambio_abm`. Confirmás la baja, la ventana se
cierra y la novedad sigue ahí.

**2. `f_envio` la escribe otro proceso.** El alta no la toca, pero 1.593 de 1.594 filas de 2026
la tienen cargada (una `date`, no `datetime`, que coincide con el día de `f_carga`). La novedad
de las 08:18 del 04/08 todavía la tenía en NULL cuando se relevó → **se llena después**,
probablemente desde `libro_novedad_envia_correo.scx`. **Ese proceso todavía no se relevó** y es
lo que falta para entender el circuito completo de avisos al cliente.

---

## 6. Estado de la implementación (04/08/2026)

Decisiones del usuario: **solo el alta** (sin correo, sin modificar, sin baja) · desde la **fila
de la grilla (F2)** y **suelta**; el panel Buses queda afuera por ahora.

| Pieza | Archivo | Estado |
| --- | --- | --- |
| Alta (INSERT de los 5 campos, `MAX(id)+1`, transacción) | `AbmService.AltaNovedadAsync` | 🔒 andamiaje (`NovedadesAbmActivo`) |
| Formulario dentro del libro | `Components/Shared/NovedadViajeDialog.razor` | ✅ abre · Grabar bloqueado |
| Novedades sueltas (últimos 7 días, TOP 50) | `ReportService.GetNovedadesViajeAsync(0)` | ✅ |
| Tecla F2 + ítem de menú con atajo | `PlanillaTrafico.razor` | ✅ |
| Botón "Libro de novedades" de la franja de KPIs (suelta) | `PlanillaTrafico.razor` | ✅ |

> **Dónde vive la puerta a la novedad SUELTA — se movió dos veces.** Nació como botón "Novedad"
> de la franja de KPIs (04/08/2026) → pasó al menú contextual de la grilla como *"Novedad sin
> reserva"* (06/08/2026, para descargar la franja) → **volvió a la franja el 19/08/2026 como
> botón "Libro de novedades"**, al lado de Ocupación, y el ítem del menú se sacó. Motivo del
> último cambio: el menú de botón derecho actúa sobre la fila clickeada, y la novedad suelta
> justamente NO cuelga de ninguna fila — estaba fuera de lugar ahí. Todas las variantes llaman
> al mismo método `AbrirNovedadSuelta()` → `AbrirNovedades(null, enAlta: true)`.

**Verificado en pantalla** (Playwright, usuario DAMIAN, contra producción en solo lectura): el
formulario abre con el asunto precargado, los dos campos obligatorios deshabilitan el botón, se
ve el candado del andamiaje, y debajo aparecen las **23 novedades sueltas reales** de los últimos
7 días con su usuario y hora.

> 💡 **Esta tabla podría cortar ANTES del día D.** A diferencia del resto de Tráfico,
> `libro_novedad` **no es del circuito `viaje`**: es propia y autocontenida. El cutover sería
> bloquear el alta en FoxPro + apagar la sync de esta sola tabla. Se dejó apagada por
> consistencia, pero es la candidata más barata para estrenar escritura real.

**Pendiente:** el envío de correo, el modificar (solo mensaje), una baja de verdad (lógica y
auditada, no la rota del FoxPro), el alta desde el panel Buses, y relevar
`libro_novedad_envia_correo.scx`.

---

## 7. Las otras dos teclas de la tanda

### F1 — Ayuda de comandos

La ventana **"Zoon"** del `.exe` con un editbox de texto plano. **No está en el fuente en disco**
(ni el texto "Ayuda de comandos" aparece en ningún `.prg` ni `.sct`) → es del `.exe`, así que la
única fuente es la captura del usuario. Lista F2…F9 sin decir qué hace ninguna.

En Buslink se rehizo como `AyudaTeclasDialog.razor`: las teclas **agrupadas por familia** (ver /
anotar / cambiar la unidad prevista — el cuadro del FoxPro no deja ver que cinco de las nueve
hacen lo mismo), cada una **colapsada en una línea** y desplegable con tres datos: qué hace,
cuándo se usa, y un badge **"escribe"** si modifica la reserva. El contenido **se filtra por
permiso**: quien no tiene `D` no ve el F5 de rango de fechas, quien no tiene `C` no ve el F4.
Formato elegido por el usuario sobre 4 propuestas.

### F3 — Refresh

`bRefresh.Click`, recarga manual. **Ya estaba migrado y mejorado**: Buslink recarga solo cada 60 s
comparando un token de versión, resalta lo que cambió y muestra la hora de la última
actualización. Se mapeó la tecla igual para quien viene del Metrocar y la busca donde estaba.

⚠️ **F1 y F3 están tomadas por el navegador** (ayuda del navegador y "buscar siguiente"). Las dos
se interceptan con `preventDefault`, que funciona en Chrome/Edge/Firefox. F5 y F11 se le siguen
dejando al navegador — ver la regla del mapa de teclas en `TRAFICO_F4_AVISO.md` §5.2.

---

## 8. Forms FoxPro de esta capa

| Archivo | Rol |
| --- | --- |
| `Forms/libro_novedad_abm.scx` | **El ABM** (alta / modifica / baja) — el del F2 |
| `Forms/libro_novedad.scx` | La lista del libro (menú propio, aún sin migrar) |
| `Forms/libro_novedad_envia_correo.scx` | ⚠️ El proceso que llena `f_envio` — **sin relevar** |
| `Forms/libro_novedad_view.scx` · `_view_viaje.scx` | Vistas de consulta |
| `Forms/libro_novedad_parametro.scx` | Parámetros del libro |
| `trafico2.scx` | `libro_novedad_alta` (F2) y `libro_novedad_alta_veh` (panel Buses) |
