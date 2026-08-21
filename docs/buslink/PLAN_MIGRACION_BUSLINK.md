# Plan de migración BUSLINK — Circuito `viaje` (Metrocar FoxPro → Buslink)

> **Fecha:** 02/07/2026 · **Estado:** aprobado, en ejecución (Fase 0 arrancada)
> **Buslink** = nombre nuevo del sistema Blazor actual (mismo código `MetroCarSysBlazor`,
> misma base `replicaVPF`). El renombre es de documentación — la UI y el código no cambian
> en esta etapa.
>
> Documento hermano (análisis del estado del sistema): `docs/buslink/ANALISIS_SISTEMA_BUSLINK.md`.
> Conocimiento técnico por módulo: skills en `.claude/skills/` (índice en `CLAUDE.md`).

---

## 0. Decisiones marco (tomadas con el dueño, 02/07/2026)

| Decisión | Resolución |
| --- | --- |
| Estrategia de transición de `viaje` | **Circuito completo por etapas**: se construye TODO el circuito de escritura (Reservas alta + Tráfico operación + Facturación Graba), se prueba contra el server local, y hay **UN solo día D** en que las tablas cambian de dueño y FoxPro queda de consulta. |
| Alcance Tráfico día 1 | **Todo**: asignar/cambiar interno y chofer + estados del viaje + cancelar con motivo + Zoom del Viaje en edición completa. |
| Regla de propiedad de datos | La de siempre (skill `abm-metrocar`): SQL dueño tabla por tabla; Blazor no escribe tablas cuyo dueño sigue siendo FoxPro. Los catálogos migran temprano; el circuito `viaje` migra entero el día D. |
| Quién construye | Un solo dev asistido por IA. Cada fase se corta en entregas de 1-3 días verificables con el protocolo de dos señales (skill `testing-nortur`). Estimaciones gruesas en semanas calendario. |

### Correcciones de arquitectura (validadas por revisión de diseño, 02/07/2026)

Tres correcciones al enunciado original que **cambian el plan**:

1. **`cliente_grupo`, `guia` y `chofer_franco` NO pueden cambiar de dueño antes del día D**,
   aunque parezcan catálogos: los escribe el propio circuito FoxPro hasta el último día
   (Reservas crea/extiende grupos y da de alta guías automáticamente; la toolbar de
   `trafico2` escribe francos). Si se apaga su sync antes, FoxPro pierde escrituras.
   Sus ABMs **se construyen antes, pero el cutover es el día D**.
2. **`liquidacion` y `liquidacion_detalle` también cambian de dueño el día D**: el "Graba"
   de Facturación las inserta. No eran parte de la lista original.
3. **`viaje._sync_id` es la PK clustered y hoy la asigna el proceso de sync**: hay que
   verificar en Fase 0 si es identity o si Blazor debe generarla en el INSERT.
   Si esto se descubre el día D, es un show-stopper.

### Las tablas que cambian de dueño el día D (12)

`viaje` · `viaje_log` · `viaje_adicional` · `cliente_grupo` · `vehiculo` · `guia` ·
`parametro` · `liquidacion` · `liquidacion_detalle` · `chofer_franco` ·
`reserva_plantilla` · **`vehiculo_km`** (descubrimiento de la extracción de la toolbar,
02/07/2026: la asignación de unidad escribe el odómetro del mes).

---

## 1. Fases

### FASE 0 — Cierre de gaps de conocimiento e infraestructura de corte (~1-2 semanas)

**Objetivo:** eliminar toda incógnita que hoy impediría codear o cortar. Nada de esta fase
escribe datos (salvo el ítem 7, que es código de UI de solo lectura).

**Entregables:**

1. ✅ **`docs/PlanoFoxPro/trafico/TRAFICO2_TOOLBAR.md`** — HECHO (02/07/2026). La lógica de
   escritura de los botones Asig U/P, Otra Unidad, Reas, Libe, Chequeo, Frc de
   `trafico2.scx` + forms `trafico_asigna`/`trafico_reasigna`/`trafico_liberar`/
   `chofer_franco*`: SQL exacto contra `viaje`/`vehiculo`/`chofer_franco`/`vehiculo_km`,
   motivos de `viaje_log`, validaciones. Era el gap crítico que bloqueaba la Fase 3.
   Matriz consolidada: `.claude/skills/modulo-trafico/references/ESCRITURA_CIRCUITO.md`.
2. 🔴 **`gps_xlm()` — INTEGRACIÓN VIVA, entrega OBLIGATORIA antes del corte**
   (corregido 12/08/2026; doc: `docs/PlanoFoxPro/trafico/GPS_XLM.md`).
   La función tiene 2 vías: XML file-drop a `dir_xml` (**muerta**, `xml_envia = 0`) e
   **INSERT/UPDATE en un SQL Server externo** (`192.168.0.8` → `MetroCarSQL`, tabla
   `Servicios`), y **esa segunda está ENCENDIDA en los dos servers productivos**
   (`sql_gps = 1`). La versión anterior de este punto decía "NO-OP, confirmar muerto":
   **era un error**, salió de leer la réplica local, que es un snapshot viejo.
   **Volumen:** 136 clientes con `cliente.envia_gps = 1` (incluida AEROLINEAS) →
   **3.466 de 3.713 viajes del último mes (93 %)**. Se llama en ASIGNO, RE-ASIGNO,
   FINALIZO, CANCELO y el armado de plantillas.
   **Consecuencia:** si Buslink toma el circuito sin replicar la vía SQL, el feed de
   seguimiento de esos clientes se corta **sin que nadie reciba un error**. El hook
   `IGpsNotifier` de Fase 2 ya no puede ser un no-op: tiene que implementar el
   INSERT/UPDATE (~24 campos, con el mapeo de estado a `S`/`N`/`B`).
   **Pendiente de confirmar:** `192.168.0.8` responde ping pero su puerto SQL no es
   accesible desde la PC de desarrollo → está verificado que la bandera está en 1 y que
   el host vive, **no** que los INSERT estén entrando. Confirmarlo desde el servidor de
   Buslink con el botón **Conexión** de la solapa GPS de `/parametros`, o con el cliente.
3. **Doc del interruptor de sync** — cómo se apaga la réplica DBF→SQL **tabla por tabla**,
   quién lo opera, tiempo de propagación, y **qué hace la sync con filas que existen en SQL
   pero no en DBF** (¿las borra? ¿las ignora?). Es la palanca del día D y del rollback.
4. **Doc del bloqueo FoxPro** — mecanismo exacto para dejar FoxPro "solo consulta"
   (quitar barras de menú / dígitos 2-3-4 del `nivel` / permisos), probado en copia.
5. **Mapeo campo a campo de las 12 tablas** del circuito: `INFORMATION_SCHEMA.COLUMNS` +
   `COLUMNPROPERTY(...,'IsIdentity')` + defaults, incluyendo **cómo se asignan `_sync_id`,
   `id_viaje` y los contadores de `parametro`** en el INSERT. Nombres truncados verificados
   (lección de Choferes: siempre contra `sys.columns`, nunca desde el form FoxPro).
6. **Re-plantear al cliente el índice `ix_viaje_id_viaje` + `ix_viaje_adicional_id_viaje`**
   con el argumento nuevo: ahora habrá UPDATEs por `id_viaje`; un UPDATE con scan de 84K
   lecturas mantiene locks largos y bloquea la planilla de todos. Si lo vuelve a declinar:
   regla dura "todo WHERE de escritura lleva `f_reserva`" (firmada en el service, Fase 2).
7. **Regla del permiso `F`** implementada (deuda identificada en `seguridad-nortur`):
   ocultar importes en el Zoom actual y en grillas con precios vía `Permisos.Tiene('F')`.
   Es prerrequisito de Reservas (Valor Especial) y del Zoom en edición. Entregable chico e
   independiente — **ideal primera entrega de código**. Matriz de prueba: DAMIAN (`TCVLA`)
   y LUCIO (`TVM`) no deben ver importes.

8. **🔴 Pedir al cliente la réplica de las tablas que faltan en SQL.** Se van descubriendo al
   migrar y son bloqueantes silenciosos: la pantalla Blazor se construye igual, pero al
   encender su flag escribiría a ciegas o no leería nada. Inventario al 04/08/2026:
   - **`viaje_log_chofer`** (75.001 filas en el DBF) — bitácora de LOGONEO/DESLOGONEO del panel
     Buses. Sin ella, encender `LogoneoAbmActivo` graba el `UPDATE vehiculo` **sin auditoría**,
     y el ítem "Ver Datos Extras → Logoneo/Deslogoneo" no se puede migrar.
     Estructura y detalle: `docs/PlanoFoxPro/trafico/TRAFICO_BUSES_MENU.md`.
   - **`cabecera`, `chofer_franco`, `chofer_viatico`, `_motivo`, `_liquida`** — estaban en el
     server viejo pero no en el nuevo (relevado 05/07/2026): **re-verificar** contra el server
     activo antes del día D.

**Dependencias:** ninguna. **Paralelizable:** los ítems 2-6 y 8 son extracción/documentación
mientras se valida el ítem 7 — se pueden intercalar todos.

---

### FASE 1 — Catálogos: ABMs con cutover temprano (patrón Usuarios) (~2-3 semanas)

**Objetivo:** achicar el alcance del día D migrando de dueño, tabla por tabla, los
catálogos que el circuito solo LEE, y consolidar el patrón `AbmService` con 4-5
repeticiones más antes de tocar `viaje`.

**Clasificación:**

| Grupo | Tablas | Cutover |
| --- | --- | --- |
| **A — cutover temprano seguro** | `viaje_motivo_cancela`, `feriado`, `destino`, `cliente_operador`, `cliente` | Al terminar cada ABM (bloqueo FoxPro + sync off por tabla) |
| **B — ABM se construye ahora, cutover el día D** | `guia`, `cliente_grupo`, `chofer_franco`, `reserva_plantilla` | Día D (los escribe el circuito FoxPro hasta el corte) |

**Orden de entrega (riesgo creciente, una entrega por catálogo):**

1. `viaje_motivo_cancela` — mini catálogo, segundo ABM del proyecto, calca
   `UsuariosAbm.razor` + `UsuarioEditorDialog.razor`. Doc ✅
   (`PlanoFoxPro/catalogos/VIAJE_MOTIVO_CANCELA_ABM.md`, 02/07/2026): 6 motivos, baja
   lógica con rehabilitación, ⚠️ el FoxPro no chequea permisos 2/3/4 en este ABM
   (Buslink los aplica igual).
2. `feriado` — trivial (doc ✅ `PlanoFoxPro/catalogos/FERIADO_ABM.md`); **precaución de
   corte:** cargar en FoxPro todos los feriados restantes del año ANTES de apagar su sync
   (el "armar plantillas" FoxPro los seguirá leyendo del DBF hasta el día D).
   🚨 **Verificado 02/07/2026: hay CERO feriados de 2026 cargados** (15 filas, todas de
   años previos) — el armar plantillas actual genera viajes en feriados como si fueran
   días comunes. Cargarlos ya en FoxPro, sin esperar el cutover.
3. `destino` — doc ya existe (`DESTINO_ABM.md`); ojo: baja FÍSICA (política documentada),
   y no copiar el bug del contacto.
4. `cliente_operador` — doc existe; baja física; agregar validación de huérfanos.
5. `cliente` — el maestro grande (doc existe); muchos campos, sin cascadas peligrosas.
6. ABMs del grupo B (`cliente_grupo` con su baja=cancelación en cascada, `guia`,
   `chofer_franco`): construir y validar en local con ZZTEST, dejar la escritura detrás
   del feature flag (Fase 6) hasta el día D. Docs ✅: `catalogos/CLIENTE_GRUPO_ABM.md`,
   `catalogos/GUIA_ABM.md` (02/07/2026 — ojo: baja FÍSICA pese a tener `f_delete`; el alta
   manual no graba `f_create` ni `id_guia`), francos en `trafico/TRAFICO2_TOOLBAR.md` §5.
   El motivo de Reasignar (Fase 3.4) también quedó documentado:
   `catalogos/VIAJE_MOTIVO_CAMBIO_ABM.md` (🐛 su Modificar FoxPro pega a la tabla
   equivocada — implementar sano, no copiar).

**Regla operativa de la ventana** (aceptada por el dueño, pero explicitarla): desde el
cutover de un catálogo del grupo A, las altas nuevas viven SOLO en SQL y **FoxPro no las
ve** (su alta de reservas lee DBF). Mitigación: cortar `cliente` y `destino` lo más cerca
posible del día D (ideal: últimas 2-3 semanas) y acordar el procedimiento para el caso
"cliente nuevo urgente" durante la ventana.

**Dependencias:** Fase 0 ítems 3-4 (interruptor y bloqueo probados).
**Paralelizable con:** Fase 2 (códigos independientes; conviene intercalar un catálogo
entre entregas del motor para mantener ritmo de valor visible).

---

### FASE 2 — Motor de escritura del circuito: `ViajeAbmService` (~2 semanas)

**Objetivo:** construir UNA sola vez las primitivas que Reservas, Tráfico y Facturación
comparten. Evita reimplementar tres veces el INSERT de 35+ campos y la bitácora.
Todo contra el **servidor local**.

**Entregables** (clase nueva `Services/ViajeAbmService.cs`, mismo patrón de `AbmService`:
`SqlConnection` + `BeginTransaction` + `SqlParameter` + `AbmResult` + invalidación
`InvalidarCacheTrafico`):

1. **`InsertarViajeAsync`** — INSERT completo con los desnormalizados calculados en UN
   solo lugar: `str_f_rese` (= `CONVERT(char(8), f_reserva, 112)`), `hs_s_inici` ("HH:MM"),
   `nombre_cli`, `estado_importe`, `_deleted=0`, estado inicial `SIN ASIGNAR`,
   `cronograma='S/C'`.
2. **`LogViajeAsync`** — INSERT en `viaje_log` por motivo (ALTA/ASIGNO/RE-ASIGNO/
   CBIO UNIDAD/FINALIZO/CANCELO/MODIFICO/REACTIVAR) + **motor de diff campo-por-campo**
   para MODIFICO (réplica del FoxPro).
3. **Transiciones de estado** como métodos atómicos (viaje + vehiculo + log en LA MISMA
   transacción): `AsignarAsync` (incluye `vehiculo_km` + sub-flujo franco trabajado),
   `LiberarAsync` (volver a SIN ASIGNAR), `ReasignarAsync` (resetea `chequeo=0`),
   `FinalizarAsync` (el "Libe" de la toolbar: hs_fin/duración/pax/voucher/odómetros +
   unidad LIBERADO con `id_zona` nueva + adicionales con precio), `CancelarAsync`
   (+ cascada DELETE `cliente_grupo` si todo el grupo quedó cancelado), `ReactivarAsync`.
   Hook GPS según decisión de Fase 0, **aislado** para poder apagarlo sin tocar la
   transacción.
4. **Contadores atómicos de `parametro`**: `UPDATE parametro SET lote_plant = lote_plant + 1
   OUTPUT inserted.lote_plant` (SQL 2012 lo soporta) dentro de la transacción — la fila de
   `parametro` funciona de mutex natural. Ídem `id_viaje_int` y la asignación de `id_viaje`.
   **Prohibido `SELECT MAX()+1` fuera de transacción.**
5. **Cascadas de grupo**: crear/extender `f_grupo_fin` con UPDATE de arrastre a los viajes
   del grupo; upsert de `guia`.
6. **Política de adicionales unificada**: se escribe SOLO `viaje_adicional` (tabla); la
   lectura ya contempla ambas representaciones. Decisión firmada con el dueño (el Importa
   Excel deja de grabar inline en `adi_*`).
7. **Firma anti-scan obligatoria**: todos los métodos exigen `(idViaje, fReserva)` — el
   `f_reserva` en el WHERE no es opcional mientras no exista `ix_viaje_id_viaje`.
8. **Soporte de rutas (`id_viaje_int > 0`)**: toda operación pega a TODOS los tramos y
   loguea/notifica GPS por tramo; asignar exige tramos anteriores FINALIZADOS; finalizar
   solo desde el último tramo; tramos intermedios cierran con `hs_fin=23:59`
   (descubrimiento de la toolbar).

**Hito verificable de la fase:** script de humo que ejecuta en local, sobre un cliente
ZZTEST, el ciclo `alta → asigna → reasigna → finaliza → cancela → reactiva → cancela`,
con dos señales por paso (grilla + SELECT), `viaje_log` completo y consistencia `vehiculo`
verificada, y limpieza física final. **Este script se conserva: es la base del smoke del
día D.**

**Dependencias:** Fase 0 (ítems 2, 5, 6; el 1 ya está). **Paralelizable con:** Fase 1.

---

### FASE 3 — Tráfico en escritura (~3 semanas)

**Objetivo:** la Planilla de Tráfico existente (`PlanillaTrafico.razor`) pasa de
solo-lectura a **despacho operable** — acá es donde cargan los internos. Alcance día 1
confirmado (§0). Especificación por operación: `TRAFICO2_TOOLBAR.md` + `TRAFICO_ZOOM.md`
+ matriz `ESCRITURA_CIRCUITO.md`.

**Entregables (una operación = una entrega verificable, en el orden de la sección 2):**

- Botonera de operaciones en la planilla (habilitada por permiso `T` + dígitos 2/3/4) +
  diálogos de confirmación/motivo (calcan el patrón multi-modo de `UsuarioEditorDialog`).
- Chequeo → Asignar U/P → Liberar → Reasignar (con motivo) → Finalizar → Cancelar+motivo →
  Reactivar → Franco (`chofer_franco`) → Zoom del Viaje en modo edición (~35 campos +
  diff MODIFICO) → Duplicar y valor servicio.
- Tras cada operación: `InvalidarCacheTrafico(dia)` + recarga — el auto-refresh y el flash
  ya existentes muestran el cambio (el pipeline visual ya está construido).
- Tests funcionales Playwright del despacho (patrón `tests/clientes.spec.ts` +
  `irAInteractivo`).

**Dependencias:** Fase 2 completa + `TRAFICO2_TOOLBAR.md` (✅). **Paralelizable:** nada
dentro de la fase (cada operación reusa la anterior), pero los catálogos restantes de
Fase 1 pueden intercalarse.

---

### FASE 4 — Reservas: las 3 puertas de alta (~3-4 semanas, la fase más grande)

**Objetivo:** que los viajes puedan NACER en Buslink. Docs ya existentes:
`RESERVA_TRANSPORTACION.md`, `RESERVA_PLANTILLAS.md`, `IMPORTA_EXCEL_VIAJE.md`.

**Entregables en orden:**

1. **Puerta 1 — Alta manual** (`reserva_transportacion_con_adicional`), en sub-tajadas
   verificables:
   a. alta simple (1 día × 1 servicio) con las 14 validaciones y transacción (mejora sobre
      FoxPro, que no la tiene);
   b. multiplicación días × servicios;
   c. "varios días" (modo ruta, `id_viaje_int` atómico);
   d. grupos (crear/extender con arrastre);
   e. guías (upsert automático, `guia_dueno` N/C/S);
   f. adicionales (solo tabla);
   g. Valor Especial gated por permiso `F`.
2. **Puerta 2 — Plantillas**: mantener (CRUD de `reserva_plantilla`, cabecera de 16
   posiciones) y luego **Armar** (generación masiva rango × días de semana × feriados ×
   lote). Dos mejoras obligatorias sobre FoxPro: **preview dry-run** (mostrar qué se va a
   generar antes de insertar) y **transacción por lote**. Migrar también el **"deshacer
   lote"** (`reserva_plantilla_elimina_viaje`) — es el botón de emergencia del día 1.
3. **Puerta 3 — Importa Excel** (28 columnas, 3 etapas de validación, transaccional):
   upload + validador en Blazor, grabando adicionales en tabla (no inline).
   **Candidato explícito a descope si el cronograma aprieta** (workaround día 1: alta
   manual o plantilla; decidirlo con el dueño ANTES, no el día D).

**No copiar los bugs heredados documentados** (rubros excluidos sobre cliente equivocado,
INSERT de plantilla que omite campos, mensaje del CUIT).

**Dependencias:** Fase 2 (motor) + catálogos de Fase 1 en producción o al menos
construidos. Fase 3 NO es prerrequisito técnico — **Fases 3 y 4 son intercalables** si
conviene por valor de demo; se recomienda 3→4 porque Tráfico ejercita las transiciones
(más riesgo técnico) con menos superficie de UI.

---

### FASE 5 — Facturación: el "Graba" (~1-2 semanas)

**Objetivo:** cerrar el circuito. El motor de valorización YA está migrado y validado al
99,4% (`ValorizarGrupoAsync` + `CalcularTotalesLiquidacionAsync`); falta solo la escritura.

**Entregables:**

1. Pendientes del motor: servicios 2º/3º, rutas (`id_viaje_i`: valoriza el último tramo,
   el UPDATE pega a todos), ajuste global manual con motivo.
2. **`GrabarLiquidacionAsync`**: en UNA transacción (FoxPro no la tiene — mejora):
   INSERT `liquidacion` + `liquidacion_detalle`, `UPDATE viaje SET estado_via='FACTURADO',
   liquidacio=@id` (todos los tramos si es ruta), `UPDATE cliente_grupo SET f_grupo_fc=HOY`.
3. **Revertir corregido**: además de borrar liquidación+detalle y revivir viajes, limpiar
   `viaje.liquidacio` y reabrir el grupo (asimetría documentada del FoxPro que NO se
   replica).
4. Diálogo de cotización (t_cambio=1 con moneda≠PESOS) como confirmación real, no solo
   alert.
5. **Test de cuadre**: re-generar en local las últimas 3 liquidaciones reales de FoxPro y
   comparar totales y detalle 1:1.

**Dependencias:** Fase 2. Independiente de Fases 3 y 4 en código (puede adelantarse si se
necesita una victoria rápida), pero para probarlo end-to-end hacen falta viajes
FINALIZADOS creados por el circuito nuevo.

---

### FASE 6 — Ensayo general y preparación del corte (~2 semanas)

**Objetivo:** demostrar paridad con la operación real y ensayar el día D completo,
incluido el rollback.

**Entregables:**

1. **Feature flag de escritura** (`EscrituraViaje` en `appsettings`): permite deployar
   Buslink completo a producción ANTES del corte con la escritura apagada, y hace que el
   rollback sea "flag off".
2. **Operación sombra (3-5 días hábiles):** con backup fresco de producción restaurado en
   local, replicar en Buslink-local las operaciones reales que el despachante hace en
   FoxPro ese mismo día; al cierre, diff automático del día (`estado_via`, interno, chofer,
   importes) entre local y la réplica. Cero diferencias no explicadas = paridad demostrada
   con datos reales, riesgo cero.
3. **"Test de gemelos" de INSERT:** misma reserva cargada en FoxPro y en Buslink-local →
   comparar la fila `viaje` columna por columna (salvo metadata `_sync_*`).
4. **Ensayo del rollback en local:** apagar sync, escribir desde Blazor, re-encender sync,
   observar qué pasa con las filas (responde con evidencia la incógnita del ítem 3 de
   Fase 0).
5. Runbook del día D impreso (sección 3), scripts de verificación listos, capacitación a
   los usuarios reales (ALEJANDRA, SERGIO, DAMIAN, LUCIO — cada uno con su matriz de
   permisos), suite Playwright verde.

**Dependencias:** Fases 3, 4, 5 completas.

---

### FASE 7 — Día D (1 día + ventana nocturna)

El runbook completo está en la **sección 3**.

---

### FASE 8 — Post día D: estabilización y siguiente anillo del strangler

**Semana 1-2:** monitoreo intensivo (sección 3.4), check-in diario de 10 min con despacho
y facturación, hotfixes con prioridad absoluta, **backup diario de SQL** (ahora es el
master de la operación).

**Después (siguientes anillos, en orden sugerido):** liquidación a fleteros (PROVEEDOR,
casi sin uso — barato), informes pendientes (control pre-liquidación), módulos
Taller/Combustible/Vehículos en escritura, tarifarios (`lista_precio`) y catálogos que
quedaron en FoxPro, hasta poder apagar la sync entera y dejar FoxPro como archivo
histórico.

---

## 2. Fase Tráfico: orden interno de las operaciones de escritura

| # | Operación | Por qué en este lugar |
| --- | --- | --- |
| 1 | **Chequeo** | El "hola mundo" de la escritura en tráfico: `UPDATE viaje SET chequeo = chequeo + 1` + log CHEQUEO, sin transición de estado ni `vehiculo`. Valida la tubería completa (escritura → invalidación caché → auto-refresh → flash → estado display CHEQUEO) con riesgo mínimo. |
| 2 | **Asignar U/P** | El corazón del despacho: máximo valor, máxima frecuencia. Estrena la parte delicada: transacción viaje + `vehiculo` vivo + log + `vehiculo_km` (odómetro del mes) y las validaciones (franco del chofer con sub-flujo "Cbia. Franco"/"Trabaja Franco", estado de la unidad, anti-doble-asignación con `UPDLOCK`). Segundo, no primero, para no debuggear tubería y consistencia a la vez. |
| 3 | **Liberar (volver a SIN ASIGNAR)** | La inversa exacta de asignar (en FoxPro vive en el Zoom → "Sin Asignar"): cierra el par y habilita el ciclo de prueba reversible asignar↔liberar sobre ZZTEST. |
| 4 | **Otra Unidad / Reasignar** | Variación de asignar+liberar con motivo (`viaje_motivo_cambio`) y log **RE-ASIGNO** (interno y cronograma ori→new en columnas dedicadas); resetea `chequeo=0`; unidad nueva → ASIGNADO, vieja → LIBERADO. Casi todo reuso. |
| 5 | **Finalizar (= "Libe" de la toolbar)** | ⚠️ No es una transición simple: hs_fin + duración + pax real + voucher + odómetros/km + unidad LIBERADO **con zona nueva** (`vehiculo.id_zona`) + **adicionales con precio** en `viaje_adicional` (agua = `parametro.adic_agua` × `viaje.agua`, horas extra con motivo obligatorio). Solo se libera una unidad EN CURSO. Necesaria para que Facturación tenga qué liquidar. |
| 6 | **Cancelar con motivo** | Recién acá lo destructivo: motivo de `viaje_motivo_cancela` + cascada DELETE de `cliente_grupo` si todo el grupo quedó cancelado + hook GPS. Se llega con el motor de transiciones ya maduro. |
| 7 | **Reactivar** | La inversa de cancelar — juntas forman otro par de prueba reversible. |
| 8 | **Franco** (`chofer_franco`) | Tabla aparte, sin tocar viaje; obligatoria para el día 1 (con FoxPro bloqueado, los francos deben poder cargarse en Buslink) pero independiente — no bloquea a las anteriores. Alta masiva + DELETE físico + modifica (FT). |
| 9 | **Zoom del Viaje en edición (~35 campos)** | Al FINAL a propósito: la mayor superficie de UI, depende del diff MODIFICO, del permiso `F` (importes) y de que todas las primitivas estén estables. Menor frecuencia relativa, mayor costo de construcción — última. |
| 10 | **Duplicar + valor servicio** | Colgados del Zoom; cierran el alcance confirmado. |

**Principio rector:** primero la tubería (1), después el valor con la consistencia difícil
(2-4), después las transiciones de cierre (5-7), y la superficie ancha al final (9-10).
Cada par reversible (asignar/liberar, cancelar/reactivar) se entrega junto porque se
testean mutuamente con ZZTEST sin ensuciar datos.

**Regla transversal (de `gps_xlm`):** ASIGNO, RE-ASIGNO, FINALIZO y CANCELO notifican al
GPS. El hook se implementa una vez en el motor (Fase 2) y queda aislado y apagable.

---

## 3. Checklist del Día D

### 3.1 Precondiciones (D-14 → D-1)

- [ ] DoD de la sección 5 firmado por el dueño.
- [ ] Fecha elegida: el día de menor volumen histórico de viajes (verificar con la
      réplica), con la noche anterior como ventana de corte; freeze de código desde D-3
      (solo hotfixes).
- [ ] Capacitación hecha con los usuarios reales; cheat-sheet de "dónde está cada botón
      ahora" entregado.
- [ ] Buslink versión candidata **ya deployada** en 172.25.69.217 con
      `EscrituraViaje=false`, smoke de lectura verde en producción.
- [ ] Backups verificados (con restore de prueba): carpeta DBF completa + `replicaVPF`
      productivo.
- [ ] Réplica al día (lag 0) y operador del interruptor de sync disponible y confirmado
      para la ventana.
- [ ] Feriados del año cargados; catálogos de Fase 1 ya cortados y sanos.
- [ ] Scripts impresos/listos: verificación de conteos por tabla, apagado de sync,
      bloqueo FoxPro, smoke ZZTEST, rollback.
- [ ] Decisión GPS ejecutada y probada con el proveedor.
- [ ] Contactos de guardia: dev, dueño, operador de sync, referente de despacho.

### 3.2 Pasos del corte (noche D-1 → mañana D)

1. **Freeze operativo FoxPro** (ventana acordada, ej. 22:00→06:00): nadie carga ni asigna.
2. **Última sync completa** de las tablas del circuito; verificar lag 0 por tabla:
   `COUNT`, `MAX(_updated_at)`, `MAX(id)` de `viaje_log`, valores de `parametro`
   (lote_plant, id_viaje_int), estados de `vehiculo` — comparados DBF vs SQL.
3. **Backup final** de `replicaVPF` + copia de los DBF (este es el punto de restore del
   rollback).
4. **Apagar la sync de las 12 tablas**: `viaje`, `viaje_log`, `viaje_adicional`,
   `cliente_grupo`, `vehiculo`, `guia`, `parametro`, `liquidacion`,
   `liquidacion_detalle`, `chofer_franco`, `reserva_plantilla`, `vehiculo_km`.
   Marcar en checklist tabla por tabla, con doble verificación.
   ⚠️ **`parametro` ya está desconectada desde el 12/08/2026** (se adelantó para habilitar
   el ABM de Parámetros), así que en su caso el paso no es apagar la sync sino:
   🔴 **RESINCRONIZAR SUS CONTADORES** — obligatorio, antes de habilitar cualquier escritura
   del circuito. Desde el 12/08/2026 FoxPro los incrementa en su DBF y esos incrementos **no
   llegan a SQL**: las dos copias divergen. Copiar del DBF a SQL los valores finales de
   **`id_viaje_i`, `lote_plant`, `lote_sobre`, `stock_movi`** y verificar que `MAX(id_viaje)`
   de `viaje` sea coherente con `id_viaje_i`.
   **Si se saltea, el primer lote/viaje que arme Buslink sale repetido.**
5. **Bloquear FoxPro**: quitar menús/permisos de Reservas (las 3 puertas), Tráfico
   escritura y Facturación graba según el mecanismo probado en Fase 0.
   **Verificar logueándose con CADA usuario real** que no queda ninguna puerta de
   escritura.
6. **Activar `EscrituraViaje=true`** en Buslink productivo + reinicio del servicio.
7. **Smoke técnico con protocolo ZZTEST sobre producción** (única vez autorizada,
   documentada y con limpieza): alta reserva ZZTEST → aparece en planilla → asignar
   unidad → reasignar → finalizar → cancelar → reactivar → cancelar → verificar
   `viaje_log` completo y consistencia `vehiculo` → DELETE físico de todo lo ZZTEST.
   Dos señales en cada paso; el log de `dotnet` mirándose en vivo.
8. **Smoke operativo**: la primera reserva real y la primera asignación real del día las
   hace el operador con el dev al lado; verificación de dos señales.
9. **Registrar la hora exacta del corte** (todo `viaje_log` posterior nace de Buslink —
   es la marca de auditoría y la lista de re-entrada si hubiera rollback).
10. Anunciar "vivo". FoxPro queda solo de consulta.

### 3.3 Plan de rollback

- **Gatillo:** bug bloqueante en una operación core (asignar, alta, cancelar) sin fix
  posible en menos de ~2 horas.
- **Punto de no retorno explícito:** fin del primer día de operación. Después de eso, el
  costo de re-entrada manual supera al de cualquier hotfix → solo se avanza.
- **Pasos:** (1) `EscrituraViaje=false`; (2) listar desde `viaje_log` (posterior a la
  marca de corte) todas las operaciones hechas en Buslink → re-ingresarlas a mano en
  FoxPro; (3) reactivar menús/permisos FoxPro; (4) re-encender la sync tabla por tabla
  (comportamiento ya ensayado en Fase 6 — se sabe qué hace con las filas SQL nuevas);
  (5) verificación de conteos; (6) post-mortem, fix, nueva fecha.
- La ventana corta + el `viaje_log` exhaustivo son lo que hace al rollback barato: la
  lista de re-entrada es exacta, no una reconstrucción.

### 3.4 Monitoreo de la primera semana

**Queries diarias automatizables (script de salud):**

- Viajes creados por puerta (origen T/P, lotes) vs promedio histórico del mismo día de
  semana.
- Integridad de bitácora: toda transición de estado del día tiene su fila en `viaje_log`
  (cero huérfanas).
- Consistencia `vehiculo` ↔ `viaje`: cada `vehiculo.estado='ASIGNADO'` apunta a un viaje
  ASIGNADO vigente, y viceversa.
- Contadores `parametro` monotónicos, sin saltos ni lotes duplicados.
- Desnormalizados sanos: `str_f_rese = CONVERT(char(8), f_reserva, 112)` en todos los
  viajes nuevos; `hs_s_inici` con formato "HH:MM".
- `cliente_grupo`: sin grupos huérfanos ni cerrados por error; adicionales solo en tabla
  (slots `adi_*` vacíos en viajes nuevos).
- **Detección de doble escritura:** los DBF del circuito deben quedar congelados —
  verificar timestamps/registros de los DBF sin cambios post-corte; `_updated_at` en SQL
  sin orígenes inesperados.
- **Verificar que ninguna sync residual pisó nada:** fila centinela escrita por Blazor
  que debe sobrevivir cada ciclo de sync de las tablas que siguen replicando.

**Del lado app/SQL:** log de `dotnet` sin excepciones no controladas (fuente #1 de
verdad); duración de transacciones y bloqueos (`sys.dm_exec_requests`); latencia de las
operaciones de escritura (<1s); la planilla sigue fluida con el auto-refresh.

**Del lado humano:** check-in diario de 10 minutos con despacho y con facturación; la
primera liquidación real de la semana se graba supervisada y se cuadra contra el motor
(validado al 99,4%) y contra la factura manual.

---

## 4. Riesgos Top 8 y mitigaciones

| # | Riesgo | Mitigación concreta |
| --- | --- | --- |
| 1 | **La sync pisa datos escritos por Blazor** (interruptor mal apagado, job residual, tabla olvidada) | Inventario del interruptor por tabla documentado y probado (Fase 0); checklist tabla-por-tabla el día D con doble verificación; fila centinela post-corte que debe sobrevivir los ciclos de sync restantes; ensayo en local del re-encendido (Fase 6); monitoreo de `_updated_at` sospechosos la semana 1. |
| 2 | **Doble escritura en la transición** (alguien opera en FoxPro por costumbre o el bloqueo falló) | Bloqueo verificado usuario por usuario el día D (paso 5); auditoría diaria de DBF congelados; capacitación previa + cheat-sheet; cartel "SOLO CONSULTA" en FoxPro si el menú lo permite; el freeze nocturno elimina la ventana gris del corte. |
| 3 | **Contadores de `parametro` concurrentes** (lote duplicado, `id_viaje` repetido — FoxPro era efectivamente mono-usuario, la web no) | Patrón único en `ViajeAbmService`: `UPDATE parametro SET x = x+1 OUTPUT inserted.x` dentro de la transacción (la fila de `parametro` actúa de mutex); prohibido el `SELECT MAX()+1` fuera de transacción; test de concurrencia explícito en el DoD (dos lotes simultáneos). |
| 4 | 🔴 **`gps_xlm()` NO replicado** — riesgo **subido de categoría el 12/08/2026**: se creía apagado y está **VIVO** (`sql_gps = 1` en los dos servers productivos → `192.168.0.8`/`MetroCarSQL`/`Servicios`). Afecta al **93 % de los viajes** (136 clientes con `envia_gps`, incluida AEROLINEAS) y se dispara en ASIGNO, RE-ASIGNO, FINALIZO, CANCELO y armado. **Falla en silencio**: nadie recibe un error, simplemente dejan de entrar filas. | Implementar la vía SQL (INSERT/UPDATE de ~24 campos con estado mapeado `S`/`N`/`B`) en el hook `IGpsNotifier` del motor de Fase 2 — **ya no alcanza un no-op**; confirmar con el proveedor/cliente quién consume `MetroCarSQL.Servicios` y que los INSERT de hoy estén entrando (botón **Conexión** de `/parametros` → solapa GPS desde el servidor de Buslink); prueba end-to-end antes del día D; el hook aislado para poder apagarlo sin tocar las transacciones; **verificar el conteo de `Servicios` antes y después del corte** como señal de que el feed sigue vivo. |
| 5 | **Permiso `F` sin implementar** (operadores sin permiso viendo precios; Valor Especial sin control) | Entrega temprana en Fase 0 (patrón ya definido en `seguridad-nortur`: `Permisos.Tiene('F')`); se aplica retroactivo al Zoom actual (deuda conocida); test de matriz con DAMIAN (`TCVLA`, sin F) y LUCIO (`TVM`) en el DoD. |
| 6 | **Plantillas/Armar o Importa Excel generan basura masiva** (cientos de viajes mal generados en producción) | Preview dry-run obligatorio antes de insertar (mejora sobre FoxPro); transacción por lote completa; confirmación extra si el lote supera N viajes; el "deshacer lote" (`reserva_plantilla_elimina_viaje`) migrado ANTES del día D como botón de emergencia; los lotes identificables por `viaje.lote` hacen cualquier limpieza quirúrgica. |
| 7 | **UPDATEs sin índice por `id_viaje`** (cada operación escanea 521K filas: latencia + locks largos que bloquean la planilla de todos) | Re-plantear el índice al cliente con el argumento nuevo de escritura (Fase 0); mientras tanto, firma obligatoria `(idViaje, fReserva)` en todos los métodos del service — el WHERE siempre seekea por `ix_viaje_f_reserva`; revisar también `viaje_adicional`; medir lecturas lógicas de cada UPDATE en el DoD. |
| 8 | **Inconsistencia `viaje` ↔ `vehiculo` vivo** (doble click, reintento de SignalR, dos despachantes asignando la misma unidad a la vez) | Transacción única viaje+vehiculo+log; validación pesimista dentro de la tx (releer con `UPDLOCK`: si ya está ASIGNADO a otro viaje → error claro, no pisar — el FoxPro ya hace la versión optimista de este chequeo); botones disable-on-click; query de consistencia en el monitoreo diario (3.4); el auto-refresh de 60s reduce decisiones sobre datos viejos. |

**Vigilados menores** (no top 8 pero con dueño): asignación de `_sync_id` en INSERT (se
resuelve en Fase 0.5), invisibilidad de catálogos nuevos para FoxPro durante la ventana
(regla operativa de Fase 1), backup del server nuevo como único master post-corte (backup
diario desde el día D), y truncamiento a 10 chars en cualquier SQL nuevo (verificación
`INFORMATION_SCHEMA` sistemática, lección ya aprendida).

---

## 5. Criterios de "listo para el Día D" (Definition of Done del circuito)

**Conocimiento y datos**

1. ✅ `TRAFICO2_TOOLBAR.md` existe; cada botón implementado referencia su sección.
2. Mapeo campo a campo de las 12 tablas verificado contra `INFORMATION_SCHEMA` (incluye
   resolución de `_sync_id`, `id_viaje` y contadores).
3. Decisión GPS ejecutada y probada.

**Funcional (todo sobre servidor local, protocolo ZZTEST de dos señales)**

4. Las 10 operaciones de Tráfico (sección 2) pasan su test de dos señales, incluidos los
   pares reversibles completos.
5. Las 3 puertas de Reservas generan viajes correctos (o 2 puertas + descope de Excel
   firmado por el dueño); el dry-run y el deshacer-lote funcionan.
6. **Test de gemelos:** una misma reserva cargada en FoxPro y en Buslink produce filas
   `viaje` idénticas columna a columna (salvo metadata `_sync_*`).
7. **Cuadre de facturación:** las últimas 3 liquidaciones reales re-generadas en local
   dan totales y detalle 1:1; el Graba y el Revertir corregido pasan sus tests.
8. **Operación sombra:** 3-5 días hábiles replicando la operación real en local con diff
   diario limpio (cero diferencias no explicadas).

**Integridad y reglas**

9. `viaje_log` se emite en el 100% de las operaciones; el diff de MODIFICO reproduce el
   formato FoxPro.
10. Desnormalizados siempre correctos (`str_f_rese`, `hs_s_inici`, `nombre_cli`,
    `estado_importe`); adicionales solo en tabla.
11. Permiso `F` aplicado en todas las pantallas con importes; matriz de permisos probada
    con los 7 usuarios reales; niveles 2/3/4 en cada botón.
12. Test de concurrencia pasado: doble asignación simultánea de la misma unidad y doble
    generación de lote simultánea se resuelven sin corrupción.

**No funcional**

13. Toda operación de escritura < 1s y con lecturas lógicas acotadas (seek, no scan);
    cero deadlocks en prueba de operación simultánea.
14. Suite Playwright completa verde (smoke + funcionales nuevos del circuito); log de
    `dotnet` limpio durante toda la validación.

**Operacional**

15. Feature flag de escritura operativo; Buslink deployado en 172.25.69.217 con lectura
    verificada.
16. Interruptor de sync y bloqueo de FoxPro probados en ensayo; rollback ensayado en
    local (incluido el re-encendido de sync).
17. Runbook del día D completo; capacitación hecha; backups con restore verificado;
    feriados del año cargados en ambos lados.
18. Sign-off explícito del dueño sobre: alcance día 1, descopes (Excel sí/no), decisión
    GPS, y fecha.

---

## 6. Archivos clave de la implementación

| Archivo | Rol en el plan |
| --- | --- |
| `MetroCarSysBlazor/Services/AbmService.cs` | El patrón de escritura probado (ABM Usuarios) — el `ViajeAbmService` de Fase 2 nace de acá |
| `MetroCarSysBlazor/Services/ReportService.cs` | Lectura, caché e invalidación (`InvalidarCacheTrafico`) que toda escritura debe disparar; contiene el motor de tarifas validado |
| `MetroCarSysBlazor/Components/Pages/PlanillaTrafico.razor` | La pantalla que recibe las 10 operaciones de despacho de la Fase 3 |
| `MetroCarSysBlazor/Components/Shared/ZoomViajeDialog.razor` | Pasa de solo-lectura a edición completa (última entrega de Tráfico); donde aplica el permiso `F` |
| `docs/PlanoFoxPro/trafico/TRAFICO2_TOOLBAR.md` | La especificación de escritura del despacho (Fase 0.1 ✅) |
| `.claude/skills/modulo-trafico/references/ESCRITURA_CIRCUITO.md` | Matriz consolidada operación → tablas → campos → log |
