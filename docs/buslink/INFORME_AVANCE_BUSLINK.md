# BUSLINK — Informe de migración paso a paso

**Empresa:** NORTUR (Metrocar) — transporte, transfers y turismo
**Corte del informe:** 02 de julio de 2026
**Tipo:** guía de ejecución — para leer y seguir durante toda la migración
**Basado en:** `docs/buslink/PLAN_MIGRACION_BUSLINK.md` (el plan técnico) — este documento lo
convierte en pasos concretos y ordenados, con la recomendación de ejecución optimizada.

---

## Cómo leer este informe

Este documento es la **hoja de ruta operativa** de la migración: cada fase está
desglosada en pasos numerados con (a) qué hay que hacer, (b) cómo se hace, (c) cómo se
verifica que quedó bien, y (d) cuál es el criterio para darlo por terminado y pasar al
siguiente. Está pensado para volver a él cada semana: buscar la fase en curso, ver el
paso siguiente, ejecutarlo, tildar.

La lógica general en una frase: **primero se cierran las incógnitas (Fase 0), después
se practica con lo chico (Fase 1), después se construye el motor (Fase 2), recién ahí
las pantallas grandes (Fases 3-5), se ensaya todo como si fuera real (Fase 6), y se
corta en un solo día (Fase 7).**

### Estado al día de hoy

| Hito | Estado | Fecha |
| --- | --- | --- |
| Sistema de lectura completo (11 pantallas) | ✅ Hecho | jun 2026 |
| Motor de tarifas migrado y validado (99,4%) | ✅ Hecho | 22/06/2026 |
| Primer ABM de escritura (Usuarios y Permisos) | ✅ Hecho | 01/07/2026 |
| Especificación de escritura del despacho (toolbar Tráfico) | ✅ Hecho | 02/07/2026 |
| Plan de migración completo aprobado | ✅ Hecho | 02/07/2026 |
| **Fase 0 — cierre de gaps** | 🟡 En curso (1 de 7 ítems) | — |
| Fases 1 a 8 | 🔲 Pendientes | — |

---

## La estrategia en 5 ideas (para no perderse en el detalle)

1. **La regla madre:** los datos tienen UN dueño por tabla. Mientras el dueño sea
   FoxPro, Buslink solo lee. Una tabla cambia de dueño cuando su pantalla Buslink está
   lista, el ABM FoxPro se bloquea y la sincronización de esa tabla se apaga. Ya se
   hizo una vez (tabla `usuario`) y funcionó.
2. **Un solo día D.** Las 12 tablas del circuito de viajes cambian de dueño todas
   juntas, en una ventana nocturna planificada. Nada de convivencia a medias sobre la
   tabla `viaje`: eso sería la receta del desastre (dos sistemas escribiendo lo mismo).
3. **Todo se construye y prueba contra el servidor LOCAL** (la réplica de desarrollo),
   nunca contra los datos productivos. Los datos de prueba llevan el prefijo `ZZTEST` y
   se borran al terminar.
4. **Validación con dos señales, siempre:** una operación de escritura se da por buena
   cuando la pantalla muestra el cambio **y** una consulta directa a la base lo
   confirma. Nunca con una sola señal.
5. **Entregas de 1 a 3 días.** Ninguna pieza tarda semanas en poder probarse. Si un
   paso no se puede verificar en pocos días, hay que partirlo en pasos más chicos.

---

# FASE 0 — Cerrar las incógnitas (1-2 semanas) 🟡 EN CURSO

**Objetivo:** que no quede ninguna pregunta abierta que pueda frenar la construcción o
—peor— aparecer de sorpresa el día del corte. Esta fase casi no escribe código: es
investigación, documentación y UNA entrega de código chica (el paso 0.6).

**Por qué va primero:** cada ítem de esta fase es barato hoy y carísimo si se descubre
tarde. Ejemplo concreto: si el día D se descubre que Buslink no sabe generar el
identificador interno `_sync_id` de la tabla `viaje`, el corte se cancela esa misma
noche. Resolverlo hoy cuesta una consulta a la base.

### Paso 0.1 — ✅ HECHO: Especificación de la toolbar de Tráfico

Ya está: el documento `TRAFICO2_TOOLBAR.md` captura exactamente qué hace cada botón del
despacho del sistema viejo (asignar, reasignar, liberar/finalizar, chequeo, francos),
con el SQL exacto y las validaciones. Este era el gap que bloqueaba todo el diseño de
la Fase 3. De la extracción salieron 3 sorpresas que ya se incorporaron al plan:

- El GPS se notifica en **4 operaciones** (asignar, reasignar, finalizar, cancelar), no
  solo al cancelar.
- El botón "Libe" no libera la unidad: **finaliza el viaje completo** (hora de fin,
  pasajeros reales, voucher, odómetros) y de paso libera la unidad con su zona nueva.
- Asignar una unidad **también escribe el odómetro mensual** (`vehiculo_km`) — por eso
  las tablas del día D ahora son 12 y no 9.

### Paso 0.2 — Documentar la integración GPS (`gps_xlm`) y decidir qué hacer

**Qué es:** cada vez que el despacho asigna/reasigna/finaliza/cancela un viaje, el
sistema viejo llama a una función que le avisa al proveedor de GPS. Si Buslink no
replica esto, el GPS deja de enterarse de los cambios el día D.

**Cómo se hace, en orden:**

1. Buscar la función `gps_xlm` en el código fuente FoxPro (`C:\MetroCarSys\Progs\`,
   empezando por `funcion.prg`).
2. Leerla y responder tres preguntas: ¿genera un archivo XML en una carpeta? ¿llama a
   un webservice? ¿o está muerta (apunta a algo que ya no existe)?
3. Documentar el hallazgo en `docs/PlanoFoxPro/trafico/GPS_XLM.md`.
4. **Reunión con el dueño** para firmar UNA de estas tres decisiones:
   - **Replicar** (si es un archivo XML en una carpeta, es trivial desde .NET);
   - **Proceso manual temporal** (alguien avisa al GPS a mano las primeras semanas);
   - **Confirmar que está muerta** y no hacer nada.
5. Si la decisión es "replicar": probar con el proveedor de GPS ANTES del día D que la
   versión Buslink le llega igual.

**Criterio de terminado:** decisión firmada por el dueño + (si aplica) prueba con el
proveedor OK.

### Paso 0.3 — Documentar el interruptor de la sincronización

**Qué es:** hoy un proceso copia los datos de FoxPro (archivos DBF) hacia SQL Server.
El día D hay que **apagarlo tabla por tabla**. Este paso averigua cómo.

**Cómo se hace, en orden:**

1. Identificar el proceso de sync: ¿es un job de Windows? ¿un servicio? ¿quién lo
   administra? (probablemente el proveedor que armó la réplica — conseguir el contacto).
2. Responder por escrito estas preguntas, que son las que importan:
   - ¿Se puede apagar la sincronización de UNA tabla sin tocar las demás?
   - ¿Cuánto tarda en hacer efecto?
   - **La pregunta crítica:** si Buslink escribe una fila nueva en SQL y la sync se
     vuelve a encender, ¿qué pasa con esa fila? ¿la borra porque no existe en FoxPro?
     ¿la ignora? (esto define si el plan de vuelta atrás es seguro).
3. Probar el apagado y re-encendido **en una copia**, nunca en el productivo.
4. Documentar todo en un procedimiento paso a paso que cualquiera pueda ejecutar el
   día D.

**Criterio de terminado:** procedimiento escrito + probado en copia + respuesta
confirmada a la pregunta crítica.

### Paso 0.4 — Documentar el bloqueo de FoxPro

**Qué es:** el día D, FoxPro tiene que quedar "solo consulta". Hay que saber
exactamente cómo se logra.

**Cómo se hace:**

1. Identificar el mecanismo: en FoxPro los permisos de alta/modificación/baja son los
   dígitos 2/3/4 del campo `nivel` de cada usuario, y los menús se pueden quitar.
   Decidir cuál de los dos caminos (o ambos) se usa.
2. Probar en una copia de FoxPro: quitarle los permisos a un usuario de prueba y
   verificar que efectivamente no puede grabar nada en Reservas, Tráfico ni
   Facturación.
3. Escribir la lista exacta de cambios por usuario real (son 7 usuarios) para ejecutar
   el día D.

**Criterio de terminado:** probado en copia + lista por usuario escrita.

### Paso 0.5 — Mapear campo por campo las 12 tablas del circuito

**Qué es:** el inventario definitivo de cada columna de las 12 tablas que cambian de
dueño: tipo de dato, si acepta nulos, valores por defecto, y —lo más importante—
**cómo se generan los identificadores**.

**Cómo se hace:**

1. Correr sobre el servidor local las consultas de esquema (`INFORMATION_SCHEMA.COLUMNS`
   + verificación de columnas identity) para las 12 tablas.
2. Resolver las tres preguntas de identificadores:
   - `viaje._sync_id` (la clave primaria física): ¿es autonumérica o la asigna el
     proceso de sync? Si la asigna la sync, Buslink deberá generarla — definir cómo.
   - `viaje.id_viaje` (el número de viaje del negocio): confirmar que sale del contador
     de la tabla `parametro` y documentar cuál campo es.
   - Los contadores de `parametro` (número de viaje, número de lote): confirmar valores
     actuales y cómo se incrementan.
3. Verificar los nombres de columna reales (la réplica trunca los nombres a 10
   caracteres — ya nos pasó de escribir SQL contra columnas que no existían).
4. Volcar todo en un documento de mapeo que la Fase 2 va a usar como plano de
   construcción.

**Criterio de terminado:** documento de mapeo completo + las 3 preguntas de
identificadores respondidas con evidencia (no con suposiciones).

### Paso 0.6 — Primera entrega de código: la regla del permiso F (precios)

**Qué es:** en el sistema viejo, solo los usuarios con el permiso "F" ven precios e
importes. En Buslink esa regla todavía no se aplicó: hoy cualquier usuario que abre el
detalle de un viaje ve el importe. Es una fuga de información y además es requisito de
las pantallas de edición que vienen.

**Cómo se hace:**

1. Repasar qué pantallas muestran importes: el Zoom del Viaje (detalle), las pantallas
   de Facturación y cualquier grilla con columna de precio.
2. Envolver cada campo/columna de importe con la verificación de permiso
   (`Permisos.Tiene('F')` — el servicio de permisos ya existe, es solo aplicarlo).
3. Probar con la matriz de usuarios reales: DAMIAN y LUCIO no tienen la letra F → no
   deben ver ningún importe; ALEJANDRA y SUPERVISOR sí la tienen → deben verlos igual
   que antes.
4. Sacar capturas de ambos casos como evidencia + correr la suite de tests para
   confirmar que nada se rompió.

**Criterio de terminado:** matriz de 4 usuarios verificada con capturas + tests verdes.

### Paso 0.7 — Re-plantear el índice de base de datos al cliente

**Qué es:** la tabla `viaje` (520.000 filas) no tiene índice por número de viaje. Para
leer ya lo esquivamos, pero para ESCRIBIR es más serio: cada actualización sin índice
recorre toda la tabla y bloquea a los demás usuarios mientras tanto.

**Cómo se hace:**

1. Presentar al cliente el argumento nuevo (antes lo declinó, pero era solo lectura):
   "cada asignación de unidad va a demorar y va a trabar la pantalla de los demás
   despachantes si no creamos este índice".
2. Si acepta: crear los índices (`viaje.id_viaje` y `viaje_adicional.id_viaje`) en una
   ventana de bajo uso. Es una operación de minutos.
3. Si vuelve a declinar: queda la regla obligatoria de programación (ya definida) de
   que toda escritura filtre también por fecha de reserva — funciona, pero es la opción
   B.

**Criterio de terminado:** decisión registrada (índice creado, o regla B confirmada).

> **Recomendación del analista para la Fase 0:** hacer 0.2 (GPS) y 0.3 (interruptor de
> sync) **primero y en paralelo**, porque ambos dependen de terceros (el proveedor de
> GPS, el administrador de la réplica) y los tiempos de respuesta de terceros no los
> controlamos — hay que dispararlos ya. Mientras se espera respuesta, avanzar con 0.5
> (mapeo, es trabajo propio) y 0.6 (permiso F, código propio). 0.4 y 0.7 son
> conversaciones cortas que se acomodan donde haya hueco.

---

# FASE 1 — Catálogos: los ABMs de práctica (2-3 semanas)

**Objetivo:** doble. (1) Achicar el día D: cada catálogo que migra antes es una tabla
menos que cortar esa noche. (2) **Practicar**: repetir 5 veces el patrón de ABM probado
con Usuarios, para que cuando toquemos la tabla `viaje` el equipo tenga el proceso
automatizado en la mano.

### Los dos grupos (no mezclarlos)

| Grupo | Tablas | Cuándo cambia el dueño |
| --- | --- | --- |
| **A — corte temprano** | motivos de cancelación, feriados, destinos, operadores, clientes | Al terminar cada ABM (son tablas que el circuito de viajes solo LEE) |
| **B — se construye ahora, corta el día D** | guías, grupos de clientes, francos, plantillas de reserva | El propio circuito FoxPro las escribe hasta el final — apagar su sync antes rompería FoxPro |

### El checklist de CADA catálogo del grupo A (repetir 5 veces, en este orden)

Para cada tabla — orden recomendado: **motivos de cancelación → feriados → destinos →
operadores → clientes** (de la más chica y sin riesgo a la más grande):

1. **Extraer la lógica FoxPro** del formulario correspondiente (si no está ya
   documentada — destinos, operadores y clientes ya tienen su documento).
2. **Construir la capa de escritura**: métodos de alta / modificación / baja en el
   servicio de escritura, calcando el patrón del ABM de Usuarios (transacción +
   parámetros seguros + validación de clave duplicada).
3. **Construir la pantalla**: lista + diálogo de edición de 4 modos
   (ver/alta/modifica/baja), calcando la pantalla de Usuarios.
4. **Aplicar permisos**: qué usuarios pueden ver el ABM y qué dígitos (2/3/4)
   habilitan cada botón.
5. **Probar con protocolo completo** sobre el servidor local: alta de un registro
   `ZZTEST` (dos señales) → intento de duplicado (debe rechazar) → modificación →
   baja lógica (fila amarilla) → limpieza física.
6. **Correr la suite de tests** — ninguna pantalla existente se rompió.
7. **El cutover** (esto es lo nuevo respecto de Usuarios — coordinar con el cliente):
   - Bloquear el ABM correspondiente en FoxPro (procedimiento del paso 0.4).
   - Apagar la sincronización de esa tabla (procedimiento del paso 0.3).
   - Escribir una fila centinela desde Buslink y verificar 24-48 h después que la
     sync no la pisó.
8. **Anunciar al equipo de NORTUR**: "los [feriados] ahora se cargan en Buslink".

**Precauciones específicas ya identificadas:**

- **Feriados:** antes de apagar su sync, cargar en FoxPro todos los feriados que
  quedan del año — el armado de plantillas de FoxPro los va a seguir leyendo de su
  lado hasta el día D.
- **Destinos y operadores:** su política histórica es baja física (se borra de
  verdad), no baja lógica — respetarla, y no copiar el bug documentado del campo
  contacto.
- **Clientes:** es el maestro grande (muchos campos) — por eso va último, con el
  patrón ya aceitado por los 4 anteriores. Regla operativa: cortar clientes lo más
  cerca posible del día D, porque un cliente dado de alta en Buslink no lo ve el alta
  de reservas de FoxPro (acordar con el equipo el procedimiento para "cliente nuevo
  urgente" durante esa ventana).

### Los ABMs del grupo B (construir, no cortar)

Se construyen con el mismo checklist pero **se frenan en el paso 6**: la escritura
queda detrás del interruptor general (`EscrituraViaje`, ver Fase 6) apagado hasta el
día D. Incluye el caso especial de **grupos de clientes**, cuya "baja" en realidad
cancela en cascada todos los viajes del grupo — probarlo a fondo en local.

> **Recomendación del analista:** no hacer la Fase 1 entera de corrido. Intercalarla
> con la Fase 2: un catálogo, después un pedazo del motor, después otro catálogo. Dos
> razones: (1) el motor es trabajo pesado sin nada visible — intercalar catálogos
> mantiene entregas visibles todas las semanas; (2) cada catálogo es un ensayo del
> cutover, y conviene espaciarlos para ir puliendo el procedimiento con calma.

---

# FASE 2 — El motor de escritura del circuito (2 semanas)

**Objetivo:** construir UNA sola vez la maquinaria que Reservas, Tráfico y Facturación
comparten (`ViajeAbmService`). Es la fase menos vistosa y la más importante: acá se
decide si el sistema va a ser consistente o va a acumular datos corruptos.

**Por qué existe:** insertar un viaje toca 35+ campos con varios valores calculados;
cada cambio de estado tiene que actualizar el viaje, el estado vivo del vehículo Y la
bitácora **como una sola operación indivisible** (si falla una parte, no se graba
nada). El sistema viejo NO hacía esto (sin transacciones) — le funcionaba porque era
efectivamente monousuario. Buslink es multiusuario web: sin esta maquinaria, dos
despachantes tocando lo mismo a la vez corrompen datos.

### Orden de construcción recomendado (cada paso se prueba antes de seguir)

1. **La bitácora primero** (`LogViajeAsync`): el registro en `viaje_log` de cada
   operación (quién, cuándo, qué). Va primero porque TODO lo demás la usa, y porque la
   bitácora es la red de seguridad del día D (si hay que volver atrás, la bitácora es
   la lista exacta de lo que se hizo). Incluye el motor de comparación campo por campo
   para las modificaciones.
2. **Los contadores seguros**: el número de viaje y el número de lote salen de la
   tabla `parametro`. Implementar el patrón atómico (incrementar y leer en una sola
   instrucción, dentro de la transacción) que garantiza que dos usuarios simultáneos
   jamás obtienen el mismo número. **Prohibido** el "leer el máximo y sumarle 1" del
   sistema viejo.
3. **El INSERT del viaje** (`InsertarViajeAsync`): los 35+ campos, con los 4 campos
   calculados (fecha en texto, hora de inicio en texto, nombre del cliente copiado,
   estado del importe) resueltos en UN solo lugar del código — así ninguna pantalla
   futura puede insertarlos distinto.
4. **Las transiciones de estado, de a pares reversibles:**
   - `AsignarAsync` + `LiberarAsync` (asignar unidad ↔ volver a sin asignar): la más
     delicada — toca viaje + vehículo + bitácora + odómetro mensual + francos, con la
     validación anti-doble-asignación (si otro usuario ya asignó ese viaje o esa
     unidad, error claro en vez de pisar).
   - `ReasignarAsync` (cambio de unidad con motivo).
   - `FinalizarAsync` (el "Libe": cierre del viaje con datos reales + generación de
     adicionales con precio).
   - `CancelarAsync` + `ReactivarAsync` (con motivo, cascada sobre el grupo, y el
     aviso GPS según la decisión del paso 0.2).
5. **Las cascadas de grupo**: crear/extender un grupo de cliente arrastra la fecha a
   todos los viajes del grupo; alta automática de guías.
6. **El soporte de rutas** (viajes de varios tramos): toda operación pega a TODOS los
   tramos, con las reglas especiales extraídas del sistema viejo.

### El hito que cierra la fase (no negociable)

Un **script de humo** que corre contra el servidor local y ejecuta el ciclo de vida
completo sobre un cliente `ZZTEST`:

```
alta → asignar → reasignar → finalizar → cancelar → reactivar → cancelar
```

verificando en cada paso las dos señales, que la bitácora esté completa, que el estado
del vehículo sea consistente, y limpiando todo al final. **Este script se guarda: es
exactamente lo que se va a correr la noche del día D sobre producción.**

Además: un **test de concurrencia** explícito — dos "usuarios" simultáneos asignando
la misma unidad y generando lotes a la vez, sin duplicados ni corrupción.

---

# FASE 3 — Tráfico en escritura: el despacho operable (3 semanas) ⭐ LA PRIORIDAD

**Objetivo:** que la Planilla de Tráfico deje de ser un visor y se convierta en la
herramienta de despacho: **acá es donde se cargan los internos**. Cada operación es una
entrega independiente que se construye, se prueba y se muestra antes de pasar a la
siguiente.

### El orden de las 10 operaciones y por qué

| # | Operación | Qué es | Por qué en este orden |
| --- | --- | --- | --- |
| 1 | **Chequeo** | Marcar que un servicio fue verificado (un contador + bitácora) | El "hola mundo": la escritura más simple posible. Valida toda la tubería (botón → grabación → refresco de la grilla → destello del cambio) con riesgo casi nulo. |
| 2 | **Asignar unidad/chofer** | El corazón del despacho | Máximo valor para el negocio. Se hace segundo (no primero) para no depurar la tubería y la lógica compleja a la vez. |
| 3 | **Liberar (volver a sin asignar)** | La inversa de asignar | Cierra el par: desde acá se puede probar asignar↔liberar infinitas veces sin ensuciar datos. |
| 4 | **Reasignar (otra unidad)** | Cambio de unidad con motivo | Reusa casi todo lo de asignar+liberar. |
| 5 | **Finalizar** | Cierre del viaje con datos reales (pasajeros, voucher, odómetros) | Necesaria para que Facturación tenga qué liquidar. Ojo: es más grande de lo que parece (genera adicionales con precio). |
| 6 | **Cancelar con motivo** | La operación destructiva | Recién acá, con el motor ya maduro: tiene cascada sobre grupos y aviso GPS. |
| 7 | **Reactivar** | La inversa de cancelar | Otro par reversible para probar sin riesgo. |
| 8 | **Francos** | Días de descanso de los choferes | Tabla aparte, no toca viajes — pero obligatoria: con FoxPro bloqueado, los francos se tienen que poder cargar en Buslink. |
| 9 | **Zoom del Viaje en edición** | El detalle completo editable (~35 campos) | A propósito al final: la mayor superficie de pantalla, depende del permiso F, del motor de diferencias de la bitácora y de que todo lo anterior esté estable. |
| 10 | **Duplicar + valor del servicio** | Utilidades del Zoom | Cierran el alcance del día 1. |

### El mini-checklist de CADA operación (repetir 10 veces)

1. Releer la sección correspondiente de la especificación (`TRAFICO2_TOOLBAR.md` /
   `TRAFICO_ZOOM.md`) — el SQL y las validaciones exactas ya están escritos ahí.
2. Construir el diálogo de confirmación (calca el patrón multi-modo de Usuarios) y el
   botón en la planilla, habilitado según permisos.
3. Conectar al método del motor (Fase 2) — la pantalla NO escribe SQL propio, solo
   llama al motor.
4. Probar en local con `ZZTEST`: la operación + su inversa si la tiene + los casos de
   error (asignar una unidad ya asignada, finalizar sin datos obligatorios, etc.).
5. Verificar las dos señales + la fila de bitácora + el refresco automático de la
   grilla.
6. Test funcional de Playwright para esa operación (se suma a la suite).
7. Demo corta al dueño → siguiente operación.

**Detalle de experiencia de usuario a cuidar** (aprendido del sistema viejo): los
despachantes trabajan rápido y en simultáneo. Los botones se deshabilitan al primer
click (evitar doble ejecución), los errores se muestran en lenguaje claro ("La unidad
15 ya fue asignada al viaje 88123 por otro usuario"), y la grilla se refresca sola al
grabar (eso ya existe y funciona).

---

# FASE 4 — Reservas: que los viajes nazcan en Buslink (3-4 semanas)

**Objetivo:** las tres "puertas" por donde entran viajes al sistema. Es la fase más
grande en pantalla; se hace después de Tráfico porque Tráfico ejercita lo más riesgoso
(las transiciones) con menos superficie de UI.

### Puerta 1 — Alta manual de reservas (en 7 tajadas, cada una verificable)

1. **Alta simple**: un día × un servicio, con las 14 validaciones documentadas del
   sistema viejo y transacción (mejora: el viejo podía dejar mitades grabadas).
2. **Multiplicación**: varios días × varios servicios en una carga.
3. **Modo ruta**: viajes de varios tramos con numeración interna atómica.
4. **Grupos**: crear o extender el grupo del cliente con arrastre de fechas.
5. **Guías**: alta automática del guía si no existe.
6. **Adicionales**: solo en la tabla de adicionales (se abandona el formato viejo de
   campos sueltos — decisión ya firmada).
7. **Valor especial**: el precio manual, visible solo con permiso F (por eso el paso
   0.6 era prerrequisito).

### Puerta 2 — Plantillas (la carga masiva de servicios repetitivos)

1. Mantenimiento de plantillas (el CRUD, con su cabecera de 16 posiciones).
2. **Armar**: la generación masiva (rango de fechas × días de la semana × feriados),
   con **dos mejoras obligatorias** sobre el sistema viejo:
   - **Vista previa** de lo que se va a generar ANTES de insertar (el viejo insertaba
     a ciegas);
   - **Transacción por lote**: o se genera todo el lote o nada.
3. **Deshacer lote**: el botón de emergencia que elimina una tanda completa mal
   generada. Se migra ANTES del día D, no después — es el extintor de incendios.

### Puerta 3 — Importación desde Excel

La carga de planillas de 28 columnas con validación en 3 etapas. **Es el candidato
explícito a posponer** si el cronograma aprieta: el día 1 se puede vivir con alta
manual + plantillas. **Decidirlo con el dueño ANTES del día D**, no descubrirlo esa
semana.

**Regla de la fase:** el sistema viejo tiene 3 bugs documentados en estas pantallas
(están en los documentos de extracción) — **no copiarlos**.

---

# FASE 5 — Facturación: el botón Grabar (1-2 semanas)

**Objetivo:** cerrar el circuito económico. El 95% ya está: el motor que calcula
cuánto se le cobra a cada cliente está migrado y validado al 99,4%. Falta la
escritura.

### Pasos

1. **Completar el motor**: los 3 casos de borde pendientes (servicios segundo/tercero,
   valorización de rutas, ajuste manual con motivo).
2. **`GrabarLiquidacionAsync`**: en UNA transacción — grabar la liquidación y su
   detalle, marcar los viajes como FACTURADOS, cerrar el grupo. (El viejo hacía esto
   en pasos sueltos; si se cortaba a la mitad, quedaba inconsistente.)
3. **Revertir corregido**: el "deshacer liquidación" del sistema viejo tiene una
   asimetría documentada (no limpia todo lo que el grabar tocó) — la versión Buslink
   la corrige.
4. **El diálogo de cotización** (cuando la moneda no es pesos) como confirmación real.
5. **El test de cuadre** (la validación estrella de la fase): tomar las últimas 3
   liquidaciones reales hechas en FoxPro, re-generarlas en Buslink local, y comparar
   total por total, renglón por renglón. Tienen que dar idénticas.

> **Nota de secuencia:** esta fase es independiente de Tráfico y Reservas en código —
> si en algún momento conviene una victoria rápida para mostrar, puede adelantarse.
> Pero su prueba completa de punta a punta necesita viajes finalizados creados por el
> circuito nuevo.

---

# FASE 6 — Ensayo general (2 semanas)

**Objetivo:** demostrar con evidencia —no con confianza— que Buslink opera igual que
el sistema real, y ensayar el corte completo incluida la vuelta atrás.

### Pasos

1. **El interruptor general** (`EscrituraViaje` en la configuración): permite instalar
   Buslink completo en el servidor de producción ANTES del corte, con toda la
   escritura apagada. El día D "prender el sistema" es cambiar un valor y reiniciar —
   y la vuelta atrás es igual de simple.
2. **La operación sombra (3 a 5 días hábiles)** — la prueba reina:
   - Cada mañana, restaurar en el servidor local un respaldo fresco de producción.
   - Durante el día, replicar en Buslink-local cada operación que el despachante real
     hace en FoxPro (asignaciones, cancelaciones, cierres).
   - Al cierre, comparar automáticamente el día completo: estados, unidades, choferes,
     importes — Buslink local contra la réplica real.
   - **Cero diferencias sin explicación = paridad demostrada con datos reales y riesgo
     cero.** Si aparecen diferencias, se corrigen y se repite el ensayo.
3. **El test de gemelos**: cargar la misma reserva en FoxPro y en Buslink-local y
   comparar la fila resultante columna por columna. Deben ser idénticas.
4. **Ensayo de la vuelta atrás**: en local, apagar la sync → escribir desde Buslink →
   re-encender la sync → observar qué pasa. La respuesta (que el paso 0.3 anticipó en
   papel) acá se confirma con evidencia.
5. **Capacitación** a los 4 usuarios operativos reales, cada uno con su matriz de
   permisos, con una guía de "dónde está cada botón ahora". Suite de tests completa en
   verde.

---

# FASE 7 — El Día D (una noche + un día)

**Elegir la fecha:** el día de menor volumen histórico de viajes (se consulta a la
base), con congelamiento de cambios de código desde 3 días antes.

### La secuencia del corte (resumen operativo — el runbook completo está en el plan técnico)

**La noche anterior (ej. 22:00 → 06:00):**

1. Congelar la operación en FoxPro: nadie carga ni asigna hasta la mañana.
2. Última sincronización completa + verificación tabla por tabla de que SQL está
   idéntico a FoxPro (conteos, últimas actualizaciones, contadores).
3. Respaldo completo de ambos lados (este es el punto de restauración si todo falla).
4. Apagar la sincronización de las 12 tablas — checklist tabla por tabla, con doble
   verificación.
5. Bloquear la escritura en FoxPro y **verificar entrando con CADA usuario real** que
   no quedó ninguna puerta abierta.
6. Encender la escritura en Buslink (el interruptor de la Fase 6).
7. Correr el script de humo (el de la Fase 2) sobre producción con datos `ZZTEST`:
   ciclo completo + verificación + limpieza total. Única vez autorizada de datos de
   prueba en producción, documentada.

**La mañana del día D:**

8. La primera reserva real y la primera asignación real las hace el operador **con el
   desarrollador al lado**, verificando las dos señales.
9. Registrar la hora exacta del corte (desde ese momento, toda la bitácora nace de
   Buslink — es la marca de auditoría).
10. Anunciar "vivo". FoxPro queda solo de consulta.

### La vuelta atrás (por si acaso)

- **Se activa si:** aparece un bug bloqueante en una operación central sin arreglo
  posible en ~2 horas.
- **Cómo:** apagar el interruptor → la bitácora da la lista EXACTA de operaciones
  hechas en Buslink para re-ingresarlas a mano en FoxPro → reactivar FoxPro →
  re-encender la sync (comportamiento ya ensayado, no improvisado).
- **Punto de no retorno:** el fin del primer día. Después de eso, solo se avanza con
  arreglos — el costo de volver supera al de cualquier corrección.

---

# FASE 8 — Después del corte

**Semanas 1-2:** monitoreo intensivo con un script de salud diario (bitácora completa,
consistencia viaje↔vehículo, contadores sin saltos, archivos DBF congelados —
detectaría a alguien escribiendo en FoxPro por costumbre), reunión diaria de 10
minutos con despacho y facturación, y la primera liquidación real de la semana grabada
con supervisión y cuadrada contra la factura manual.

**Después, los siguientes anillos** (en orden sugerido): liquidación a fleteros,
informes pendientes, Taller y Combustible en escritura, tarifarios — hasta poder
apagar la sincronización entera y jubilar FoxPro como archivo histórico.

---

## Calendario tentativo (16 semanas desde el 07/07/2026)

| Semanas | Qué pasa | Hito visible al final |
| --- | --- | --- |
| 1-2 | Fase 0 completa (GPS, sync, bloqueo, mapeo, permiso F, índice) | Los precios se ocultan según permiso; cero incógnitas abiertas |
| 3-5 | Fase 1 + Fase 2 intercaladas | 5 catálogos operando en Buslink; el motor pasa su script de humo |
| 6-8 | Fase 3 (Tráfico) | El despacho completo operable en el ambiente local |
| 9-12 | Fase 4 (Reservas) | Las reservas nacen en Buslink (local) |
| 12-13 | Fase 5 (Facturación) | El test de cuadre da idéntico |
| 14-15 | Fase 6 (ensayo general) | Operación sombra sin diferencias; rollback ensayado |
| 16 | **Día D** | NORTUR opera en Buslink |

> Es un calendario de trabajo sostenido de un dev asistido por IA. Los riesgos de
> agenda más probables son los que dependen de terceros (proveedor GPS, administrador
> de la sync) — por eso la recomendación de dispararlos la semana 1 — y la ventana de
> descope acordada es la Puerta 3 de Reservas (importación Excel).

---

## Las 10 reglas de oro (imprimir y tener a la vista)

1. Los datos de prueba SIEMPRE con prefijo `ZZTEST`, SIEMPRE en el servidor local,
   SIEMPRE se borran al terminar.
2. Toda escritura se valida con **dos señales**: la pantalla lo muestra Y la consulta
   a la base lo confirma.
3. Ninguna pantalla escribe SQL propio: todo pasa por el motor (una sola fuente de
   verdad para cada operación).
4. Toda operación multi-tabla va en UNA transacción: o se graba todo o nada.
5. Los números correlativos salen del contador atómico — jamás "máximo + 1".
6. Toda búsqueda o actualización de un viaje filtra también por fecha de reserva
   (mientras no exista el índice).
7. Antes de escribir SQL contra una tabla, verificar los nombres reales de las
   columnas (la réplica los trunca a 10 caracteres).
8. Toda operación deja su fila en la bitácora — sin excepción (es la red de seguridad
   del rollback).
9. Los bugs documentados del sistema viejo NO se copian; las mejoras acordadas
   (transacciones, vista previa, deshacer lote) NO se negocian.
10. Cada corrección aprendida se anota en la documentación del proyecto el mismo día —
    el conocimiento se acumula, no se repite.

---

*Este informe se actualiza al cierre de cada fase. Detalle técnico completo:
`docs/buslink/PLAN_MIGRACION_BUSLINK.md`. Estado general del sistema:
`docs/buslink/ANALISIS_SISTEMA_BUSLINK.md`.*
