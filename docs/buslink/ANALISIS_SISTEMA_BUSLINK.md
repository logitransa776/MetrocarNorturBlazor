# BUSLINK — Análisis completo del sistema

**Empresa:** NORTUR (Metrocar) — transporte, transfers y turismo
**Documento:** estado del sistema, arquitectura, inventario y plan de modernización
**Fecha:** 02 de julio de 2026
**Autor:** equipo de migración (Claudio Marañon + asistencia IA)

---

## 1. Resumen ejecutivo

**Buslink** es el nombre del nuevo sistema de gestión de NORTUR: una aplicación web
moderna (Blazor Server sobre .NET 10) que está reemplazando gradualmente al sistema
histórico **Metrocar**, construido en Visual FoxPro hace más de 20 años.

### Dónde está parado el proyecto hoy

- **La lectura está madura.** Buslink ya muestra en vivo la operación completa: la
  Planilla de Tráfico del día (la pantalla central del negocio), los maestros de
  Clientes, Choferes y Vehículos con sus fichas completas, el módulo de Facturación y
  Liquidación con su motor de tarifas (validado contra 8.656 casos históricos con 99,4%
  de coincidencia), informes de reservas, y el tablero de alertas de vencimientos.
- **La escritura ya arrancó.** El primer ABM con escritura real (alta, baja y
  modificación) es el de **Usuarios y Permisos**, en producción de desarrollo desde el
  01/07/2026. Estrenó la infraestructura de escritura (`AbmService`) que será la
  plantilla de todo lo que viene.
- **El conocimiento del sistema viejo está capturado.** 18 documentos técnicos destilan
  la lógica real de los formularios FoxPro (validaciones, SQL, reglas no obvias),
  extraída del código fuente original — no de suposiciones.

### A dónde va

La etapa que arranca ahora es la decisiva: **que la operación diaria de tráfico de
NORTUR (asignar internos y choferes, estados de los viajes, cancelaciones) se maneje
desde Buslink**, junto con el alta de reservas y el grabado de liquidaciones. El plan
completo está en `docs/buslink/PLAN_MIGRACION_BUSLINK.md` y se resume en el capítulo 7 de este
documento: 6 fases de construcción y ensayo, un único **día D** de corte, y FoxPro
quedando como sistema de consulta histórica.

### Los tres números que definen el momento

| Métrica | Valor |
| --- | --- |
| Pantallas funcionales migradas (lectura + 1 escritura) | 11 |
| Ítems del menú que replican el FoxPro y esperan migración | ~136 |
| Tablas que cambian de dueño el día D | 12 |

---

## 2. Arquitectura y stack

### 2.1 Stack tecnológico

| Capa | Tecnología | Nota |
| --- | --- | --- |
| Lenguaje | C# / .NET 10 LTS | Soporte hasta noviembre 2028 |
| Framework web | Blazor Server (interactivo) | Render en servidor vía SignalR; sin WASM |
| Componentes UI | MudBlazor 9.5 | + tema corporativo NORTUR propio |
| Gráficos | Blazor-ApexCharts 6.1 | Los gráficos nuevos van todos acá |
| Acceso a datos | EF Core 10 con SQL crudo | Sin modelos de entidad: la base FoxPro tiene ~80 campos por tabla |
| Export | ClosedXML | Descarga a Excel en todas las grillas grandes |
| Base de datos | SQL Server 2012 — base `replicaVPF` | Réplica de los DBF de FoxPro (108 tablas) |
| Testing | Playwright (smoke + funcionales) | Protocolo propio de validación (cap. 6) |

**Restricción permanente:** el motor es SQL Server 2012 — el SQL nuevo no puede usar
funciones modernas (`STRING_AGG`, `TRIM`, `CONCAT_WS`, etc.).

### 2.2 Cómo fluyen los datos hoy (y por qué importa)

```
FoxPro (DBF) ──escribe──► proceso de sync ──replica──► SQL Server (replicaVPF)
                                                            │
                                                            ▼
                                                    Buslink (lee todo,
                                                    escribe solo `usuario`)
```

La réplica es **unidireccional**: FoxPro escribe sus DBF y un proceso los sincroniza a
SQL. Esto define la regla de oro del proyecto (la "regla madre"): **Buslink no escribe
en una tabla mientras su dueño siga siendo FoxPro** — la sync pisaría los cambios. Una
tabla migra de dueño cuando su pantalla Blazor está lista, el ABM FoxPro se bloquea y la
sync de esa tabla se apaga. Ya ocurrió con `usuario`; el circuito de viajes entero lo
hará el día D.

### 2.3 Seguridad

Replica exactamente el modelo del FoxPro (sin inventar nada nuevo, para convivir):

- **Login** contra la tabla `usuario` con el mismo flujo de mensajes del FoxPro.
- **Permisos por módulo:** el campo `acceso` es un string de hasta 16 letras
  (S R T C D V L F A E U B H X N M), cada una habilita un módulo del menú. En Buslink
  cada letra se convierte en un claim y el menú se filtra por `Permisos.Tiene('X')`.
- **Permisos de ABM:** el campo `nivel` con dígitos 2 (alta), 3 (modifica), 4 (baja).
- **Reglas especiales ya implementadas:** C requiere T (avisos de chequeo);
  X solo lo otorga SUPERVISOR (tablero); la letra F además de habilitar Facturación
  controla la **visibilidad de precios** en toda la app (pendiente de aplicar al Zoom —
  es la primera entrega de código del plan).
- El **ABM de Usuarios** en Buslink materializó el mapa completo de letras y reglas en
  `PermisosCatalogo.cs` (fuente única), con defensa anti-autobloqueo.

### 2.4 Performance (lecciones ya incorporadas como reglas)

| Regla | Origen |
| --- | --- |
| Connection string SIEMPRE con `Pooling=True` + warmup del pool al arrancar (`DbWarmupService`) | El lag de segundos por query era el handshake TLS+login sin pool |
| Grillas de +100 filas → `Virtualize` o `content-visibility: auto` | La Planilla de Tráfico con 365 filas repintaba 9.000 celdas |
| Toda query por `id_viaje` DEBE acotar también por `f_reserva` | `viaje` NO tiene índice por `id_viaje` (PK clustered = `_sync_id`): el scan son ~84.000 lecturas vs ~1.050 del seek |
| Caché en memoria TTL 5 min (55 s en Tráfico) + invalidación explícita tras escritura | Evita repetir queries pesadas |
| Auto-refresh inteligente: token de versión liviano cada 60 s, recarga solo si cambió | La planilla se actualiza sola sin costo |

Documentación de detalle: `docs/performance/PERFORMANCE_GRILLAS_Y_CONEXION.md` y skill
`blazor-performance`.

---

## 3. Inventario de lo construido

### 3.1 Páginas (13)

| Página | Ruta | Permiso | Estado |
| --- | --- | --- | --- |
| Home | `/` | — | Activa (bienvenida + aviso si no tiene módulos) |
| Login | `/login` | — | Activa (flujo FoxPro completo) |
| **Planilla de Tráfico** | `/planilla-trafico` | `T` | **Activa — la pantalla central.** Grilla del día (25 columnas, colores de estado FoxPro), combos U/Pr / U/Cb, S/C, vista Cancelados, panel Buses (flota viva), 8 diálogos de filtro, menú contextual completo, auto-refresh 60 s con flash de cambios. Solo lectura |
| Reservas por fecha y servicio | `/reservas-fecha-servicio` | `R` | Activa — KPIs, gráficos, pivote, Excel |
| Reservas por banda horaria | `/reservas-banda-horaria` | `R` | Activa — Informe 2 (fecha × banda × vehículo) |
| Clientes | `/clientes-abm` | `F` | Activa — lista + ficha, solo lectura |
| Choferes | `/choferes` | `V` | Activa — lista + ficha 5 pestañas, vencimientos resaltados, solo lectura |
| Vehículos - Flota | `/vehiculos` | `V` | Activa — lista 15 columnas + ficha 6 pestañas, solo lectura |
| Resumen de Liquidaciones | `/resumen-liquidaciones` | `F` | Activa — maestro-detalle + comprobante, solo lectura |
| Liquidación a Clientes | `/liquidacion-clientes` | `F` | Activa — árbol de pendientes + 4 solapas **valorizadas en vivo por el motor de tarifas**; falta solo el Graba |
| Liquidaciones estimadas | `/facturacion-estimada` | `F` | Activa — proyección mensual con gráficos |
| **Usuarios y Permisos** | `/usuarios-abm` | `S` | **Activa — el primer ABM de ESCRITURA** (alta/baja/modificación reales) |
| Error | — | — | Infraestructura |

### 3.2 Componentes compartidos (26)

- **Fichas de entidad (solo lectura):** `ClienteDetalleDialog`, `ChoferDetalleDialog`
  (5 tabs), `VehiculoDetalleDialog` (6 tabs), `OperadorDetalleDialog`.
- **Universo del viaje:** `ZoomViajeDialog` (la ficha completa del viaje),
  `HistorialViajeDialog` (bitácora `viaje_log`), `NovedadViajeDialog`,
  `ListaPasajerosDialog` (planilla CNRT), `ViajeAdicionalesDialog`,
  `RecorridoCabeceraDialog`, `TextoZoomDialog`, `GpsMapaDialog` (en desarrollo).
- **Filtros de Tráfico (8):** por tipo de reserva, rango, fletero, estado, conductor,
  interno, vuelo y reserva.
- **Facturación:** `LiquidacionComprobanteDialog`, `LiquidacionResumenDialog`.
- **Escritura:** `UsuarioEditorDialog` (un solo diálogo, 4 modos: ver/alta/modifica/baja
  — el patrón para los ABMs futuros).
- **Infraestructura visual:** `KpiCard`, `TableroAlertas` (vencimientos VTV/Matafuego/
  Registro/CNRT/AEP), `ChipAlerta`.

### 3.3 Capa de servicios

| Servicio | Rol | Tamaño |
| --- | --- | --- |
| `ReportService` | TODA la lectura: 41 métodos async de datos + caché + invalidación | ~3.100 líneas |
| `AbmService` | Escritura (hoy: usuarios) — transacciones + `SqlParameter` + `AbmResult` | La plantilla de la escritura futura |
| `ExcelExportService` | Genera los .xlsx de todas las pantallas | — |
| `AuthService` + `NorturAuthStateProvider` | Login y sesión por circuito SignalR | — |
| `PermissionService` + `PermisosCatalogo` | Los permisos del FoxPro como claims + catálogo de las 16 letras | — |
| `AdjuntoService` | Sirve los archivos adjuntos de los viajes (ruta UNC configurable) | — |
| `DbWarmupService` | Precalienta el pool de conexiones al arrancar | — |

Dentro de `ReportService` vive además el **motor de tarifas migrado**
(`ValorizarGrupoAsync` + `CalcularTotalesLiquidacionAsync`): la réplica en vivo de las
cascadas de precios del FoxPro (convenido → sin cargo → cabecera → servicio modo S/H/K +
horas extra + descuentos/incrementos), validada al peso contra 8.656 viajes históricos
(99,4% de coincidencia exacta) y contra liquidaciones reales completas.

### 3.4 Base de conocimiento (el activo invisible)

- **18 documentos** en `docs/PlanoFoxPro/` — la lógica real de cada form FoxPro
  relevante, extraída del fuente binario (.scx) con un lector propio.
- **12 skills** en `.claude/skills/` — el conocimiento operativo por módulo (Tráfico,
  Reservas, Facturación, Combustible, Vehículos/Choferes) y por proceso (cómo construir
  UI, cómo migrar ABMs, cómo testear, cómo optimizar, seguridad).
- **Suite de tests Playwright** — smoke de todas las pantallas + tests funcionales +
  helper de capturas.

---

## 4. El mapa del menú: migrado vs pendiente

El menú lateral de Buslink replica la estructura completa del menú FoxPro. Los ítems
sin migrar están visibles pero deshabilitados — funcionan como **backlog a la vista**.

| Sección del menú | Migrado (activo) | Pendiente (deshabilitado) |
| --- | --- | --- |
| Reservas | 2 informes | Alta manual, plantillas, importa Excel, grupos, guías… |
| Tráfico | Planilla completa (lectura) | **Toda la escritura del despacho** (Fase 3 del plan) |
| Vehículos y Choferes | Choferes, Vehículos-Flota | Fleteros, siniestros, apercibimientos, service, odómetros… |
| Facturación | Resumen, Liq. a Clientes, Estimadas, Clientes | El Graba, liq. fleteros, liq. choferes, tarifarios, cta. cte. |
| Taller | — | Todo el módulo |
| Combustible | — | Todo el módulo (la tabla viva no está replicada) |
| ABM del Sistema | — | Catálogos (zonas, servicios, feriados, motivos…) — Fase 1 |
| Utilitarios | — | Scheduler, backup, conectados |
| Sistema | **Usuarios y Permisos (escritura)** | Tablero de control |

En números: **~10 rutas activas** contra **~136 ítems deshabilitados**. El plan de
migración no pretende migrarlos todos antes del día D — solo el **circuito del viaje**
(capítulo 5); el resto son los "anillos siguientes" del strangler (Fase 8).

---

## 5. El circuito `viaje` — lo que se migra en esta etapa

### 5.1 La máquina de estados

```
SIN ASIGNAR ──asignar──► ASIGNADO ──finalizar──► FINALIZADO ──grabar──► FACTURADO
     │                       │
     │ chequeo > 0           │ hs_inicio <= ahora
     ▼ (solo display)        ▼ (solo display)
  CHEQUEO                  CURSO
     └──────── cancelar ──► CANCELADO (con motivo) ──reactivar──► SIN ASIGNAR
```

CHEQUEO y CURSO **no se graban nunca**: son conversiones de display al armar la grilla.

### 5.2 Quién escribe qué (la matriz del circuito)

| Operación | Origen FoxPro | Tablas que toca |
| --- | --- | --- |
| Alta de reserva (manual / plantilla / Excel) | `reserva_transportacion*`, `reserva_plantilla_armar`, `importa_excel_viaje` | `viaje` (INSERT 35+ campos), `viaje_log` (ALTA), `cliente_grupo`, `guia`, `viaje_adicional`, `parametro` (contadores) |
| Chequeo | toolbar `trafico2` | `viaje.chequeo += 1` + log CHEQUEO |
| Asignar unidad/chofer | `trafico_asigna` | `viaje` (10 campos), `vehiculo` (estado vivo ASIGNADO), `viaje_log` (ASIGNO), `vehiculo_km` (odómetro del mes), `chofer_franco` (franco trabajado), GPS |
| Reasignar | `trafico_reasigna` | Ídem + motivo, log RE-ASIGNO, `chequeo=0`, unidad vieja LIBERADO, GPS |
| Finalizar ("Libe") | `trafico_liberar` | `viaje` (hs_fin, duración, pax, voucher, odómetros), `vehiculo` (LIBERADO + zona nueva), `viaje_adicional` (con precio), log FINALIZO, GPS |
| Cancelar (con motivo) / Reactivar | Zoom del Viaje | `viaje`, log CANCELO/REACTIVAR, cascada DELETE `cliente_grupo`, GPS |
| Zoom edición / Duplicar | `trafico_zoom` | `viaje` + log MODIFICO (diff campo por campo) |
| Francos | `chofer_franco*` | `chofer_franco` (INSERT masivo / DELETE físico / FT) |
| Graba liquidación | `facturacion_cliente_nueva` | `liquidacion` + `liquidacion_detalle` (INSERT), `viaje` → FACTURADO, `cliente_grupo` (cierre) |

**Las 12 tablas que cambian de dueño el día D:** `viaje`, `viaje_log`, `viaje_adicional`,
`cliente_grupo`, `vehiculo`, `guia`, `parametro`, `liquidacion`, `liquidacion_detalle`,
`chofer_franco`, `reserva_plantilla`, `vehiculo_km`.

### 5.3 Los hallazgos que cambiaron el plan (extracción 02/07/2026)

1. La integración **GPS (`gps_xlm`) se dispara en 4 operaciones** (asignar, reasignar,
   finalizar, cancelar), no solo al cancelar — la decisión sobre qué hacer con ella es
   prerrequisito (Fase 0).
2. El botón "Libe" de la toolbar **no libera: FINALIZA** el viaje (con pax real,
   voucher, odómetros y zona nueva de la unidad).
3. Asignar también escribe el **odómetro mensual** (`vehiculo_km`) y puede tocar los
   **francos** (sub-flujo "trabaja franco").
4. FoxPro **no usa transacciones en nada de esto** — Buslink las agrega (mejora
   obligatoria, no opcional).
5. FoxPro ya tenía un chequeo **anti-doble-asignación** (optimista); Buslink lo endurece
   con bloqueo pesimista dentro de la transacción.

---

## 6. Metodología de trabajo

### 6.1 El patrón strangler: "SQL dueño, tabla por tabla"

No se migra el sistema entero de un golpe. Cada tabla tiene UN dueño (FoxPro o SQL) y la
propiedad se transfiere tabla por tabla cuando su pantalla Buslink está lista. El
precedente ya funciona: la tabla `usuario` migró el 01/07/2026 y el ABM de usuarios
opera 100% en Buslink.

### 6.2 Extraer antes de construir

Ninguna pantalla se migra "de memoria": primero se extrae la lógica real del form FoxPro
(un lector propio de los .scx binarios), se documenta en `docs/PlanoFoxPro/` (SQL
exacto, validaciones, reglas no obvias, bugs heredados que NO se copian), y recién
entonces se construye el equivalente. Los 18 documentos existentes son ese trabajo.

### 6.3 Validación con dos señales

Ninguna escritura se da por buena porque "el botón no tiró error". Cada operación se
verifica con **dos señales independientes**: la UI muestra el cambio **Y** un `SELECT`
directo a la base lo confirma. Los datos de prueba usan el prefijo `ZZTEST`, van solo al
servidor local de desarrollo, y se borran físicamente al terminar — la base productiva
no se ensucia jamás.

### 6.4 Cada corrección se guarda, no se repite

Las lecciones (nombres de columnas truncados, trampas de MudBlazor, flakes de Playwright,
bugs del FoxPro) se registran en la skill correspondiente en el momento en que se
aprenden. El proyecto acumula conocimiento en vez de re-descubrirlo.

---

## 7. El plan de migración (resumen ejecutivo)

> Detalle completo, riesgos y checklist: `docs/buslink/PLAN_MIGRACION_BUSLINK.md`.

| Fase | Qué entrega | Duración estimada |
| --- | --- | --- |
| **0 — Gaps e infraestructura de corte** | Docs de GPS, interruptor de sync y bloqueo FoxPro; mapeo de las 12 tablas; regla del permiso F (primera entrega de código). La spec de la toolbar ✅ ya está | 1-2 semanas |
| **1 — Catálogos** | 5 ABMs con cutover temprano (motivos, feriados, destinos, operadores, clientes) + 4 ABMs que cortan el día D | 2-3 semanas |
| **2 — Motor `ViajeAbmService`** | Las primitivas compartidas: INSERT del viaje, bitácora, transiciones atómicas, contadores seguros, cascadas de grupo | 2 semanas |
| **3 — Tráfico en escritura** | El despacho operable: chequeo → asignar → liberar → reasignar → finalizar → cancelar → reactivar → francos → Zoom edición → duplicar | 3 semanas |
| **4 — Reservas (3 puertas)** | Alta manual, plantillas con dry-run y deshacer-lote, importa Excel (descope posible) | 3-4 semanas |
| **5 — Facturación Graba** | Grabado transaccional + Revertir corregido + test de cuadre | 1-2 semanas |
| **6 — Ensayo general** | Feature flag, operación sombra 3-5 días, test de gemelos, ensayo de rollback, capacitación | 2 semanas |
| **7 — Día D** | El corte (capítulo 8) | 1 día + noche previa |
| **8 — Post** | Monitoreo semana 1 + siguientes anillos (fleteros, Taller, Combustible, tarifarios) | continuo |

Suma gruesa de construcción: **~4 meses de calendario** para un dev asistido por IA,
con entregas verificables cada 1-3 días. Las fases 1-2 son paralelizables entre sí, y
Facturación (5) puede adelantarse si conviene una victoria rápida.

---

## 8. El Día D (resumen del runbook)

**El concepto:** todo el circuito se construye y ensaya contra el servidor local; el
software se deploya a producción ANTES del corte con la escritura apagada por feature
flag. El día D es una ventana nocturna: freeze de FoxPro → última sync verificada →
backup → apagar la sync de las 12 tablas → bloquear la escritura en FoxPro (verificando
usuario por usuario) → encender el flag → smoke técnico con datos ZZTEST (con limpieza)
→ primera operación real supervisada → vivo.

**El rollback es barato por diseño:** flag off + la bitácora `viaje_log` da la lista
exacta de operaciones a re-ingresar en FoxPro + re-encender la sync (comportamiento
ensayado antes, no descubierto en la crisis). Punto de no retorno: fin del primer día.

**Los 8 riesgos vigilados** (cada uno con mitigación concreta en el plan): la sync que
pisa datos, la doble escritura FoxPro/Buslink, los contadores concurrentes de
`parametro`, la integración GPS, el permiso F, los lotes masivos de plantillas, los
UPDATEs sin índice por `id_viaje`, y la consistencia viaje↔vehículo con usuarios
simultáneos.

**Definición de "listo":** 18 criterios verificables (funcionales, de integridad, de
performance y operacionales) que incluyen el test de gemelos (misma reserva en ambos
sistemas → filas idénticas), el cuadre de las últimas 3 liquidaciones reales, y 3-5 días
de operación sombra sin diferencias.

---

## 9. Apéndices

### 9.1 Mapeo FoxPro → Buslink (lo ya migrado)

| Form FoxPro | Equivalente Buslink | Modo |
| --- | --- | --- |
| `trafico2.scx` (planilla) | `PlanillaTrafico.razor` | Lectura |
| `trafico_zoom.scx` | `ZoomViajeDialog.razor` | Lectura (edición = Fase 3) |
| `trafico_historial.scx` | `HistorialViajeDialog.razor` | Lectura |
| `cliente.scx` + `cliente_abm.scx` | `ClientesAbm.razor` + ficha | Lectura |
| `chofer.scx` + `chofer_abm.scx` | `Choferes.razor` + ficha | Lectura |
| `vehiculo*.scx` | `Vehiculos.razor` + ficha | Lectura |
| `liquidacion_cliente.scx` | `ResumenLiquidaciones.razor` | Lectura |
| `facturacion_cliente_nueva.scx` | `LiquidacionClientes.razor` | Lectura + motor en vivo (Graba = Fase 5) |
| `trafico_resumen_horario.scx` | `ReservasBandaHoraria.razor` | Lectura |
| `usuario.scx` + `usuario_abm.scx` | `UsuariosAbm.razor` + editor | **ESCRITURA** ✅ |
| `login.scx` | `Login.razor` | Completo |

### 9.2 Trampas de la réplica (para quien escriba SQL nuevo)

- **Nombres truncados a 10 caracteres:** la réplica corta los nombres de columna del DBF
  (`id_operador`→`id_operado`, `cronogramacbio`→`cronogram2`, `registro_v2`, etc.).
  Verificar SIEMPRE contra `INFORMATION_SCHEMA.COLUMNS` antes de escribir SQL.
- **Doble juego de auditoría:** `f_create`/`f_modify`/`f_delete` son del NEGOCIO (baja
  lógica = `f_delete` con fecha); `_created_at`/`_updated_at`/`_deleted` son metadata de
  la réplica (filtrar siempre `_deleted = 0`; setear `_deleted = 0` en INSERTs propios).
- **`viaje` no tiene índice por `id_viaje`** (PK clustered = `_sync_id`): toda búsqueda
  o UPDATE debe acotar por `f_reserva`.
- **Importes con muchos NULL** en el histórico: las métricas confiables son cantidad de
  reservas y pax; los importes se recalculan con el motor de tarifas.
- **Tablas vivas NO replicadas:** `vehiculo_sobre` (combustible), `chofer_log` — no
  existen en SQL; verificar existencia antes de hacer JOIN.

### 9.3 Glosario

| Término | Significado |
| --- | --- |
| **Interno** | Número de unidad (vehículo) de la flota — "cargar los internos" = asignar unidades a los servicios |
| **U/Pr y U/Cb** | Unidad Programada (cronograma previsto) y Unidad Cambiada (la real del día) |
| **Fletero** | Empresa/dueño tercerizado que aporta vehículos y choferes |
| **Cabecera** | Recorrido troncal con puntos de paso (usado por servicios CABECERA_*) |
| **Grupo** | Conjunto de viajes de un cliente que se liquidan juntos (`cliente_grupo`) |
| **Lote** | Tanda de viajes generados de una vez por el armado de plantillas |
| **Zoom** | La ficha completa de un registro (jerga FoxPro heredada) |
| **Franco** | Día de descanso del chofer; "FT" = franco trabajado |
| **Día D** | El día del corte: las 12 tablas del circuito cambian de dueño y FoxPro queda de consulta |
| **Strangler** | Patrón de migración: el sistema nuevo "estrangula" al viejo módulo por módulo, sin big-bang |
| **Dos señales** | Protocolo de validación: la UI muestra el cambio Y un SELECT lo confirma |
| **ZZTEST** | Prefijo de todos los datos de prueba (reconocibles, reversibles, solo en el server local) |

---

*Documento generado el 02/07/2026. Fuente de verdad viva: `CLAUDE.md`,
`docs/buslink/PLAN_MIGRACION_BUSLINK.md` y las skills del proyecto.*
