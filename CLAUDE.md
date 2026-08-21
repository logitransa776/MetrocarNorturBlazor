# BUSLINK (Metrocar Nortur) — migración FoxPro → Blazor

> **Naming (02/07/2026):** el sistema pasa a llamarse **Buslink**. Es el mismo sistema
> Blazor de este repo (el código sigue siendo `MetroCarSysBlazor`, misma base
> `replicaVPF`) — el renombre aplica a docs, skills y comunicación con el cliente.

## Objetivo del proyecto

**Etapa actual (jul 2026): migración masiva de los ABMs** — que la operación de tráfico
de NORTUR (cargar internos, asignar choferes, estados, cancelaciones, alta de reservas,
grabar liquidaciones) se maneje desde Buslink, con **un día D** en que el circuito
`viaje` cambia de dueño y FoxPro queda de consulta.

- **Plan de migración completo (fases 0-8, día D, riesgos, DoD):** `docs/buslink/PLAN_MIGRACION_BUSLINK.md`
- **Análisis del estado del sistema (para seguimiento, con versión Word):** `docs/buslink/ANALISIS_SISTEMA_BUSLINK.md` (+ `.docx`)

Etapa anterior (completada en lo esencial): migrar los informes/reportes leyendo desde
**SQL Server `replicaVPF`**, reporte por reporte, mejorando sobre el FoxPro.

**Empresa:** NORTUR (Metrocar) — transporte / transfers / turismo (combis, traslados aeropuerto, city tours, cenas show, etc.).

---

## Stack elegido

> **Decisión de stack:** Se migró de Python/Streamlit a C# Blazor Server para un stack más robusto, con tipado fuerte y mejor integración con el ecosistema .NET existente (`MetroCarSysAPI`). Migrado a **.NET 10 LTS** (junio 2026) — soporte hasta noviembre 2028.

---

## Levantar la app

```bash
cd "c:/Users/HP/OneDrive/CLAUDE CODE/MetrocarNorturBlazor/MetroCarSysBlazor"
dotnet run
```

- **HTTP:** `http://localhost:5287` · **HTTPS:** `https://localhost:7277` (perfil `https`)
- Puertos definidos en `MetroCarSysBlazor/Properties/launchSettings.json`.
- La terminal de `dotnet run` es la **fuente #1 de verdad para bugs de lógica** (Blazor Server
  no muestra las excepciones de servidor en el browser). Detalle: skill `testing-nortur`.
- Publicación a producción (IIS, server WIN2022DEVBL): ver la memoria
  `publicacion-iis-produccion` — el Application Pool hay que frenarlo antes de publicar.

---

## Conexión a la base (replicaVPF)

**Servidor activo (nuevo)** — apuntado en `appsettings.json`:

```text
Server     = 172.25.69.217   (SQL Server 2012 — fuera de soporte)
Database   = replicaVPF
User Id    = sa
Password   = Nortur1024
Encrypt = True ; TrustServerCertificate = True
```

> ✅ **Datos replicados (verificado 10/06/2026):** el servidor nuevo ya tiene la réplica operativa (viajes de 2026, 204 vehículos activos). El motor es **SQL Server 2012**: evitar funciones que no soporta al escribir SQL nuevo (`STRING_AGG`, `TRIM`, `CONCAT_WS`, `APPROX_COUNT_DISTINCT`, etc.).

**Servidor viejo (local, con datos completos)** — fallback mientras dure la migración:

```text
Server     = DESKTOP-CV6LF0O\SQLEXPRESS
Database   = replicaVPF   (108 tablas, 512.876 filas en viaje)
User Id    = sa
Password   = Nortur2021
TrustServerCertificate = True
```

- Centralizada en `appsettings.json` (clave `ConnectionStrings:DefaultConnection`).
- **108 tablas** en total. Es una réplica de la base productiva de FoxPro.

> ⚠️ **Connection string: SIEMPRE `Pooling=True` (regla de performance, 16/06/2026).**
> Tener `Pooling=False` hacía que cada query pagara handshake TLS + login + resolución de
> instancia `\SQLEXPRESS` desde cero (eran segundos de lag por conexión). El string correcto
> incluye `Pooling=True;Min Pool Size=2;Max Pool Size=50`. `Encrypt=True` se mantiene (con
> pool, el TLS se amortiza). Al arrancar, `DbWarmupService` precalienta el pool (verás
> "Pool de conexiones SQL calentado: N conexiones en NNN ms" en el log). Detalle completo:
> `docs/performance/PERFORMANCE_GRILLAS_Y_CONEXION.md`.

---

## Arquitectura general

- **Blazor Server interactivo**: el rendering ocurre en el servidor vía SignalR. No hay WASM.
- **EF Core sin modelos de entidad**: `NorturDbContext` solo provee la conexión. Las queries usan SQL crudo directo para evitar mapear los ~80 campos de FoxPro.
- **Caché en memoria**: `IMemoryCache` con TTL de 5 minutos por query. Se usa en `ReportService`.
- **MudBlazor**: componentes de UI (MudGrid, MudPaper, MudTable, etc.).
- **ApexCharts**: para todos los gráficos nuevos (ver sección abajo).
- **Autenticación**: custom `AuthenticationStateProvider` (`NorturAuthStateProvider`) que valida contra la tabla `usuario` de `replicaVPF`.

### Cómo agregar un reporte nuevo (3 pasos)

1. Agregar la query en `Services/ReportService.cs` con un método `async Task<List<MiDto>>`.
2. Crear `Components/Pages/MiReporte.razor` con `@page "/mi-ruta"` (usar `ReservasFechaServicio.razor` como plantilla).
3. Agregar el link en `Components/Layout/MainLayout.razor` en el nav lateral (sección correspondiente).

---

## ApexCharts — integración y uso

### Regla crítica — colisión de namespaces

`ApexCharts` comparte nombres con `MudBlazor` (`Color`, `Size`, `ChartType`). **No agregar `@using ApexCharts` en `_Imports.razor`** — rompe todos los archivos que usan MudBlazor.

**Solución:** agregar `@using ApexCharts` solo en los archivos `.razor` que usen gráficos ApexCharts, al inicio del archivo.

---

## Menú de navegación lateral (drawer)

### Decisión de arquitectura — drawer fuera del MudLayout

`MudDrawer` de MudBlazor dentro de `MudLayout` siempre se posiciona debajo del AppBar (el flex flow de MudLayout lo fuerza). Para lograr `position:fixed` desde `top:0`, el drawer es un `<div>` CSS puro **completamente fuera de `<MudLayout>`**.

---

## Modelo de datos relevado

### Tabla `viaje` = una RESERVA (512.876 filas)

| Campo | Tipo | Significado |
| --- | --- |--- |
| `f_reserva` | date | **Fecha de la reserva** (campo para agrupar/filtrar) |
| `f_pedido` | date | Fecha en que se cargó/pidió la reserva |
| `id_servici` | nvarchar(15) | Código de servicio → FK a tabla `servicio` |
| `id_cliente` | nvarchar(15) | Código de cliente |
| `nombre_cli` | nvarchar(50) | Nombre del cliente (desnormalizado) |
| `nombre_cho` | nvarchar | Nombre del chofer asignado |
| `id_chofer` | nvarchar(15) | Chofer asignado |
| `id_vehicul` | nvarchar(15) | Vehículo asignado |
| `pax` | int | Cantidad de pasajeros |
| `estado_via` | nvarchar(15) | Estado del viaje (ver abajo) |
| `origen` | char(1) | `'T'` = transportación, `'P'` = plantilla |
| `total` / `importe` / `precio` | decimal | Importes (**OJO: muchos vienen `NULL`**) |
| `hs_inicio` / `hs_present` / `hs_fin` | datetime2 | Horarios |
| `_deleted` | bit | 1 = borrado lógico (siempre filtrar `_deleted = 0`) |
| `_created_at` / `_updated_at` | datetime2 | Auditoría de la réplica |

**Estados (`estado_via`):**
| Estado | Cantidad aprox |
| --- | --- |
| FACTURADO | 454.701 |
| FINALIZADO | 21.878 |
| CANCELADO | 21.287 |
| SIN ASIGNAR | 14.943 |
| ASIGNADO | 67 |

### Tabla `servicio` (61 filas)

Catálogo de servicios. Clave: `id_servici` (PK lógica), `nombre`.

Top servicios por volumen: `CABECERA_KM`, `CABECERA_SERV`, `EZEIZA`, `TRASLADO`, `AEROPARQUE`, `CITY`, `GUARDIA8`, `CENA SHOW`, `LA PLATA 1/2`, etc.

### Tabla `viaje_horario` (6 filas) — bandas horarias

Franjas horarias para el informe de banda horaria: `dhorario` (desde, HH:mm), `hhorario` (hasta, HH:mm).
Las 6 bandas: `00:00-00:01`, `00:02-06:29`, `06:30-08:29`, `08:30-14:00`, `14:01-18:00`, `18:01-23:59`.

### Otras tablas relevantes

`viaje_adicional`, `viaje_pasajero`, `cliente`, `chofer`, `vehiculo`, `ctacte`, `liquidacion`, `taller_service`, `vehiculo_combustible`, `reserva_plantilla`, `destino`, `zona`, `vehiculo_tipo`, `usuario`.

---

## Reglas de negocio / convenciones

1. **Siempre filtrar `_deleted = 0`** en `viaje` y demás tablas (borrado lógico).
2. **Acotar fechas al rango válido** (`ReportService.FechaMinValida` = 2021-01-01, `FechaMaxValida` = 2027-12-31) para evitar fechas corruptas (año 309, 2252, etc.).
3. Datos usables de `f_reserva`: **2021–2026** (años con volumen real).
4. **`total`/`importe` con muchos NULL** → métricas confiables hoy: **cantidad de reservas** y **pax**.
5. **No usar parametrización de EF para WHERE dinámico**: construir el SQL como string (ver `ReportService`), pero siempre escapar con `.Replace("'", "''")`.
6. **Performance de grillas grandes (regla, 16/06/2026):** una grilla que puede superar
   ~100-150 filas DEBE usar `<Virtualize>` (el servidor genera solo las filas visibles, no
   todas) + memoizar el filtrado en un campo (no recalcular en cada render). El connection
   string SIEMPRE con `Pooling=True`. El síntoma "lento solo con muchos registros" es render
   de Blazor, no SQL. Patrón completo y trampas: `docs/performance/PERFORMANCE_GRILLAS_Y_CONEXION.md`.
   Referencia viva ya optimizada: `PlanillaTrafico.razor`.
   ⚠ **Pendiente abierto (18/07/2026):** con `<Virtualize>` la grilla de Tráfico **queda en
   blanco un instante al scrollear rápido**. Se investigó a fondo y se probó el fix (render
   completo + `content-visibility`): elimina el blanco, pero hacía sentir lentos el Zoom del
   Viaje y el menú contextual, así que **se revirtió y sigue vigente `<Virtualize>`**. Causa,
   mediciones e hipótesis para retomarlo: `docs/performance/PENDIENTE_GRILLA_TRAFICO_BLANQUEO.md`
   — leerlo ANTES de volver a tocar la virtualización de esa grilla.
7. **Todo informe analítico nuevo lleva el patrón dashboard completo (regla, 03/07/2026,
   pedida y validada por el usuario):** barra de filtros compacta, KPIs flex, gráficos
   ApexCharts con colores unificados por entidad, tabla pivote con drill-down, Excel
   multi-hoja y **cross-filter estilo Power BI incluido por defecto** (clic en una categoría
   enfoca todo el tablero, en memoria, sin re-query; con 2+ dimensiones categóricas los focos
   se combinan con AND). Plantillas vivas: `ReservasFechaServicio.razor` (1 dimensión) y
   `ReservasBandaHoraria.razor` (2 dimensiones). Patrón y trampas: skill `blazor-nortur`
   § Patrón de INFORME ANALÍTICO y § Cross-filter.

---

## Estado actual

El detalle módulo por módulo (pantallas, rutas, métodos de `ReportService`, flags de andamiaje ABM y
trampas resueltas de cada tabla) vive en la skill **`estado-buslink`** — se carga sola cuando hace falta.

| Módulo / entrega | Estado |
| --- | --- |
| Arquitectura Blazor + MudBlazor + ApexCharts | ✅ hecha |
| Informes analíticos 1-5 (Reservas fecha/servicio, banda horaria, por cliente; Viajes por chofer; Km unidades) | ✅ hechos (patrón dashboard + cross-filter) |
| Vehículos y Choferes (flota, choferes, odómetros, siniestros, agenda, fleteros, tipos) | ✅ solo lectura (+ andamiaje ABM) |
| Facturación (resumen, liquidación a clientes, estimadas) | ✅ solo lectura — motor de tarifas migrado en vivo; falta el **Graba** |
| Panel de Clientes (`/panel-clientes`) — informe NUEVO | ✅ completo: Cartera · Retención y riesgo · Salud del padrón |
| Tráfico (planilla, zoom, cabeceras, francos, viáticos, voucher, guardia, contactos, pasajeros, **menú del panel Buses**, **libro de novedades + envío de correos**) | ✅ solo lectura + andamiaje — **sin placeholders** |
| Combustible (consumos, conciliación, saldos, depósitos, control, catálogos) | ✅ solo lectura + andamiaje — **10/10 ítems** |
| Reservas (operadores, grupos, destinos, especiales, plantillas, armado) | ✅ solo lectura + andamiaje — puertas de alta al circuito `viaje` (día D) |
| ABM de Usuarios y Permisos | ✅ **primer ABM con escritura real** (plantilla `AbmService`) |
| Parámetros del sistema (Empresa · Generales · GPS) | ✅ **2º ABM con escritura real** (`/parametros`, 12/08/2026) |
| Drawer | arranca todo colapsado para cualquier usuario |

> Regla vigente: la escritura nueva se construye **deshabilitada** por flags en
> `AbmFeatureFlags` (`*AbmActivo = false`) y se activa cuando la tabla cambia de dueño.
> **Ya cambiaron de dueño (watcher apagado): `usuario` (01/07) y `parametro` (12/08).**
> El resto espera al día D. Registro y checklist: skill `abm-metrocar`.

### Pendiente / próximos — el plan Buslink (02/07/2026)

El roadmap vigente es **`docs/buslink/PLAN_MIGRACION_BUSLINK.md`** (fases, día D, riesgos, DoD).
Resumen del orden:

- **Fase 0** (en curso): 🔴 **`gps_xlm` está VIVO** (corregido 12/08/2026: `sql_gps = 1` en
  los dos servers productivos, `192.168.0.8` → `MetroCarSQL.Servicios`; 136 clientes con
  `envia_gps`, el **93 % de los viajes**). Ya NO es "confirmar muerto": es una **integración
  de entrega obligatoria antes del corte** — sin ella el seguimiento de esos clientes se
  corta en silencio. Ver `docs/PlanoFoxPro/trafico/GPS_XLM.md`. Además: interruptor de sync,
  bloqueo FoxPro, mapeo de las 12 tablas del circuito, **regla del permiso `F`**.
  `TRAFICO2_TOOLBAR.md` ✅ hecho.
- **Fase 1:** catálogos con cutover temprano (`viaje_motivo_cancela` → `feriado` →
  `destino` → `cliente_operador` → `cliente`) + grupo B que corta el día D.
- **Fase 2:** motor `ViajeAbmService` (primitivas compartidas del circuito).
- **Fase 3:** **Tráfico en escritura** (chequeo → asignar → … → Zoom edición) — la prioridad.
- **Fase 4:** Reservas (alta manual, plantillas, importa Excel).
- **Fase 5:** Facturación Graba + Revertir corregido.
- **Fases 6-8:** ensayo general → **día D** → estabilización y siguientes anillos.

Secundarios (post día D): liquidación choferes, control pre-liquidación, cuenta
corriente (sin uso en producción).

---

## Lógica FoxPro documentada (para ABM)

Metodología: antes de construir cualquier ABM en Blazor, extraer y documentar la lógica del form FoxPro correspondiente.

**La biblioteca está organizada por carpetas de módulo (reorganizada 02/07/2026).
Índice maestro con estado de migración de cada doc: `docs/PlanoFoxPro/README.md`.**

Hallazgos clave de la extracción de catálogos (02/07/2026): **0 feriados de 2026 cargados**
(alerta de Fase 1);
`viaje_motivo_cambio_abm` tiene el Modificar ROTO en el fuente (pega a la tabla equivocada
— no copiar).

---

## El código FoxPro: a veces está en disco, a veces no

El sistema FoxPro completo está en `C:\MetroCarSys` (fuentes `.prg`, forms `.scx`/`.sct`, reportes `.frx`, menús `.mpr`). El `metrocar.exe` productivo está **más actualizado** que parte del fuente en disco — verificar caso por caso.

**Regla práctica:** antes de armar un informe, buscar su form/prg en disco. Si está, leer la lógica; si no, reconstruir desde la base.

Cómo leer los `.scx`/`.sct` (formato memo DBF) y el script `scx_dump.py`: skill `foxpro-extract`.

### Ubicaciones de referencia FoxPro
- App completa: `C:\MetroCarSys`
- Reportes: `C:\MetroCarSys\Reports\*.frx` (~40)
- Programas: `C:\MetroCarSys\Progs\*.prg`
- Forms: `C:\MetroCarSys\Forms\*.scx` (378)
- Menús: `C:\MetroCarSys\Menus\*.mpr`
- DBF originales: `C:\MetroCarSys\Nortur` (cp1252)

---

## Decisión de escritura para ABMs (10/06/2026)

**SQL dueño, tabla por tabla (strangler):** Blazor solo escribe en tablas cuyo dueño ya es
SQL. Una tabla migra cuando su ABM Blazor está listo + se bloquea el ABM en FoxPro + se apaga
la sync DBF→SQL de esa tabla. Los datos escritos en SQL **se quedan en SQL** — no hay puente
inverso. Mientras tanto, las tablas de FoxPro son **solo lectura** desde Blazor.
Detalle completo y checklist: skill `abm-metrocar`.
