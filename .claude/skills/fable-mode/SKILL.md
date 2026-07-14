---
name: fable-mode
description: Usar PROACTIVAMENTE en el momento en que notes que una tarea tiene varias capas — pasos dependientes, incógnitas que podrían cambiar el enfoque, debugging donde la primera teoría puede estar mal, o cualquier cosa que toque datos que todavía no miraste. En el proyecto Buslink/Metrocar esto es sobre todo: MIGRAR pantallas del FoxPro, construir ABMs (INSERT/UPDATE en replicaVPF), tocar el circuito viaje, o cualquier trabajo donde equivocarse corrompe datos. También usar cuando una tarea se traba o falla repetido, o cuando Claudio dice "modo Fable", "pensá bien esto", "hacelo bien de una", "pensá como Fable", "usá el método", "frená y hacelo como corresponde", o "primero pensalo". NO usar en tareas rápidas y de una sola capa (una edición de un archivo, una búsqueda, un ajuste de CSS) — esas van directo con las skills normales del proyecto (blazor-nortur, abm-metrocar, foxpro-extract, etc.). Este skill NO reemplaza a esas: se apoya en ellas y agrega la disciplina de razonamiento para no confundirse en lo difícil.
---

# Método Fable (adaptado a Buslink / Metrocar Nortur)

Disciplina de trabajo de Fable 5, escrita para que cualquier modelo la corra. Un archivo de skill no puede transferir la inteligencia cruda de Fable, pero sí cómo trabaja: cómo define el alcance, junta evidencia, ataca sus propias respuestas, verifica y reporta.

**Regla de oro (leer primero):** esto es para tareas DIFÍCILES, donde la primera idea puede estar mal — migraciones, ABMs, debugging, cualquier cosa que toque datos que todavía no miraste. Para una edición de un archivo, un ajuste de estilo o una búsqueda simple, **saltá los gates y hacé el trabajo con las skills normales** (`blazor-nortur`, `abm-metrocar`, `foxpro-extract`, `testing-nortur`). Forzar los 5 gates en una tarea de 2 minutos es su propio modo de falla.

## El bucle: cinco compuertas (gates), en orden

Toda tarea difícil pasa por 5 gates en orden. Un gate debe cerrar antes de abrir el siguiente. Cuando una tarea se traba o un resultado te sorprende, nombrá en qué gate estás y volvé a correrlo.

### Gate 1 — Alcance antes de trabajar

Decí cómo se ve "terminado" antes de tocar nada.

- Definí "terminado" en una o dos frases: qué artefacto existe al final, qué tiene que ser verdad de él, y **cómo vas a chequear que es verdad**. Si no podés escribir el chequeo, todavía no entendiste la tarea. En Buslink el chequeo suele ser un `SELECT` contra `replicaVPF` que da el número que la pantalla tiene que mostrar (validar al dígito).
- Revisá primero las reglas vigentes: `CLAUDE.md`, las skills del proyecto, la memoria y los planos FoxPro (`docs/PlanoFoxPro/`). No inventes un enfoque que el proyecto ya tiene resuelto.
- Separá lo conocido de lo asumido. La mayoría de las tareas difíciles tienen una a tres incógnitas que sostienen todo: hechos que, si están mal, cambian la forma entera de la solución. Nombralas. (Ej.: ¿esa columna es `bigint` o `int`? ¿la tabla está replicada en el server nuevo o solo en el viejo?)
- Si el pedido es ambiguo de un modo que cambia lo que construirías, hacé UNA pregunta, apuntada al hueco más grande. Si no, elegí el default sensato, decilo en una línea y seguí. Preguntar para cambiar el resultado, no para sentirse seguro.
- Ajustá el esfuerzo al tamaño. La profundidad del proceso se acomoda a lo que está en juego. El razonamiento profundo va en la planificación y la revisión, no en los pasos mecánicos.

### Gate 2 — Evidencia antes de razonar

Nunca diseñes de memoria de cómo un archivo, una API o una tabla "probablemente" se ve. Abrilo.

- Los archivos y la salida real de las herramientas son fuentes. La memoria de entrenamiento es solo un generador de hipótesis.
- Atacá primero las incógnitas que sostienen todo, con la sonda más barata. En este proyecto: `sys.columns` para el tipo real de una columna, un `SELECT TOP 1` para ver datos reales, el dump del `.scx` para la lógica FoxPro. 30 segundos de mirar el dato real le ganan a una hora construyendo sobre una suposición.
- Preferí una pasada fina de punta a punta antes que una primera etapa completa. Pasá UN registro por todo el circuito y verificalo antes de escalar a todos.
- Mantené un plan vivo para cualquier cosa de 3+ pasos. Cortá por dependencia, no por categoría: la salida de cada paso alimenta al siguiente. El plan es una hipótesis, no un contrato.

### Gate 3 — Razonar como adversario

Antes de comprometerte con una respuesta, cambiá de rol e intentá matarla.

- Atacá tu propia respuesta que va emergiendo como si fueras un revisor hostil: ¿qué input, estado o lectura la hace falsa? Probá ese caso de verdad; no lo imagines. (Ej. real de Buslink: ¿qué unidad rompe el cálculo de % vacío? La de odómetro incoherente → -355.800%.)
- Después reforzá lo que sobrevivió. Si la respuesta aguanta el ataque, te comprometés con confianza real, no con esperanza.
- **Reforzá lo que ya existe antes de cambiarlo.** Asumí que se construyó así por una razón y nombrá la razón; si hay una plausible, respetala. En FoxPro esto es CRÍTICO: distinguí el comportamiento a replicar del bug heredado que NO hay que copiar (ej.: `contacto = contacto` en Destinos, el % vacío sin protección, el Modificar roto de `viaje_motivo_cambio_abm`).
- Re-decidí después de cada resultado. Cada salida de herramienta confirma el plan o lo cambia; preguntate cuál, siempre. El modo de falla es la inercia: ejecutar el paso 4 de un plan que la salida del paso 2 ya invalidó.
- **Dos intentos fallidos del mismo fix = el diagnóstico está mal.** Dejá de parchar, encontrá la suposición debajo de ambos intentos y probala directo. (Ej.: el lag de grillas NO era SQL, era render de Blazor — dejar de optimizar la query.)

### Gate 4 — Verificar antes de declarar hecho

"Corrió" no es verificación. Verificá en la capa del claim.

- Si el claim es "el dato se grabó", mostrá el `SELECT` que lo encuentra — no que el INSERT no tiró error. Si el claim es "la página renderiza", mirá la página. Exit code 0 solo prueba la capa de abajo del claim.
- Usá evidencia que vos no generaste. Reabrí el archivo que escribiste. Corré el código. Sacá captura de la pantalla y leela (`captura()` de `testing-nortur`). Diff antes vs después. Contá lo que dijiste que contaste.
- **La regla de las DOS SEÑALES del proyecto es este gate:** para todo ABM de escritura, confirmá con UI (la grilla lo muestra) **Y** con `SELECT` (la fila está en la base). Nunca con una sola.
- Re-chequeá contra el pedido original y las reglas del Gate 1. ¿Construiste lo que se pidió y seguiste las reglas que cargaste?
- Muestreá las colas, no solo el medio: primer registro, último, el más raro. Los spot-checks del camino feliz esconden las fallas que importan.
- Tratá las buenas noticias como sospechosas. Un test que pasa demasiado fácil o un smoke test 33/33 sin esfuerzo significa que la verificación está rota hasta que puedas explicar por qué el resultado es real.
- Test de contexto cero para todo lo que ve el usuario: ¿alguien sin nada del contexto de esta sesión lo entendería y podría actuar?

### Gate 5 — Reportar calibrado

El reporte es parte del trabajo, no algo del final.

- Primero la respuesta, después el respaldo.
- Separá lo verificado de lo asumido, en voz alta. "Confirmé X corriendo Y; asumo Z porque no lo pude chequear."
- Citá evidencia con especificidad: rutas de archivo, números de línea, el comando que corriste, el número que viste.
- Reportá lo que observaste, no lo que quisiste hacer. Si los tests fallaron, decilo con la salida. Si salteaste un paso, decilo.
- Nunca suavices un problema real para caer bien. El desacuerdo con razones concretas le gana a la obediencia. Marcá el riesgo una vez, concreto, y después respetá la decisión de Claudio.
- Nunca afirmes como hecho lo que no verificaste esta sesión. "Hecho" significa que el chequeo del Gate 1 pasó y lo viste pasar.

## Hábitos permanentes (siempre activos, en todos los gates)

- Convertí lo relativo a absoluto: "mañana" pasa a una fecha, "la última versión" a un número de versión, "hace poco" a un mes.
- Sacá a la luz las restricciones de forma proactiva. Si ves un límite, riesgo o trade-off que Claudio no preguntó, decilo antes de que muerda.
- Elegí la próxima acción por información por unidad de costo: la sonda más barata de la incógnita más grande le gana al pedazo de trabajo más visible.
- Ordená las acciones por reversibilidad. Reversible y en alcance: hacelo. Irreversible, hacia afuera (mandar, publicar, borrar, pagar) o un cambio de alcance: frená y confirmá. **En Buslink: escribir en la base productiva es irreversible → los ABMs arrancan con `_abmActivo=false` (andamiaje) hasta el día D.**
- Desbloqueate solo antes de escalar: leé más, buscá más, probá otra ruta. Escalá solo por decisiones que Claudio realmente dueña, y agrupá las preguntas.
- El trabajo mecánico que se repite 3+ veces lleva un script, no razonamiento por instancia. El razonamiento es para el juicio; los scripts para la repetición.
- Preservá por defecto. Al editar algo que existe, tocá solo lo que la tarea requiere; borrar contenido sustancial necesita aprobación explícita.

## Olores que significan que se salteó un gate

- Estás construyendo algo y no abriste el dato/archivo/tabla real del que depende. (Gate 2)
- Acabás de decir o pensar "debería andar" sobre algo que podés probar ahora mismo. (Gate 4)
- Vas por el tercer intento del mismo fix. (Gate 3)
- Tus últimas tres acciones salieron del plan original sin chequear contra los resultados intermedios. (Gate 3)
- Estás por reportar "hecho" y la evidencia es tu intención, no una observación. (Gate 4)
- Un resultado volvió sorprendentemente limpio y seguiste sin preguntarte por qué. (Gate 4)
- No podés decir en una frase cómo se ve "terminado". (Gate 1)

Cualquiera de estos: frená, volvé a ese gate.

## Notas

- Este es un skill de MÉTODO, no de flujo. Cambia cómo ejecutás la tarea actual; no produce archivos propios.
- Se apila con las skills específicas del proyecto. Las skills de Buslink dicen QUÉ hacer y CÓMO técnicamente (`abm-metrocar` = cómo migrar escritura, `testing-nortur` = cómo validar con Playwright, `blazor-nortur` = patrones de UI, `foxpro-extract` = leer el FoxPro). Este skill dice CUÁNDO frenar y con qué disciplina. También se apila con `/verify` y `/code-review`.
- **No lo apliques a trabajo trivial.** Para una edición de un archivo, andá directo con las skills normales.
- Si una tarea sigue fallando bajo esta disciplina, esa es la señal para escalar a un modelo más fuerte, no para aflojar el proceso. Mantené la disciplina igual.
