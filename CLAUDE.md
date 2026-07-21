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

| Capa | Tecnología |
| --- | --- |
| Lenguaje | **C# (.NET 10.0 LTS)** |
| Framework web | **Blazor Server** (modo interactivo) |
| Componentes UI | **MudBlazor 9.5** |
| Gráficos | **Blazor-ApexCharts 6.1.0** (instalado, listo para usar) |
| Acceso a datos | **Entity Framework Core 10** (SQL crudo con `FromSqlRaw`) |
| Export | **ClosedXML** (descarga a Excel) |
| Base de datos | **SQL Server** — base `replicaVPF` |

> **Decisión de stack:** Se migró de Python/Streamlit a C# Blazor Server para un stack más robusto, con tipado fuerte y mejor integración con el ecosistema .NET existente (`MetroCarSysAPI`). Migrado a **.NET 10 LTS** (junio 2026) — soporte hasta noviembre 2028.

### Levantar el dashboard

```bash
cd "c:\Users\HP\OneDrive\CLAUDE CODE\Metrocar Nortur Blazor\MetroCarSysBlazor"
dotnet run
# Abre en https://localhost:7xxx o http://localhost:5xxx (ver launchSettings.json)
```

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

## Estructura del proyecto

```text
MetroCarSysBlazor/
  Program.cs                          → Bootstrap: DI, middleware, EF, MudBlazor, ApexCharts
  appsettings.json                    → Connection string a replicaVPF
  MetroCarSysBlazor.csproj            → Dependencias NuGet
  Components/
    App.razor                         → Root component (incluye JS de ApexCharts)
    Routes.razor                      → Router
    _Imports.razor                    → Using globales (NO incluir ApexCharts aquí — ver nota)
    Layout/
      MainLayout.razor                → Layout principal: drawer CSS overlay + AppBar + auth guard
      EmptyLayout.razor               → Layout limpio (para login)
    Pages/
      Home.razor                      → Página de inicio
      Login.razor                     → Formulario de login
      ReservasFechaServicio.razor     → Informe 1
    Shared/
      KpiCard.razor                   → Componente tarjeta KPI (reutilizable)
  Data/
    NorturDbContext.cs                → DbContext (solo puerta de acceso; sin DbSet — SQL crudo)
  Services/
    ReportService.cs                  → Capa de datos: todas las queries SQL + caché
    AuthService.cs                    → Autenticación
    NorturAuthStateProvider.cs        → Estado de sesión (Blazor AuthenticationStateProvider)
    ExcelExportService.cs             → Generación de .xlsx con ClosedXML
    DbWarmupService.cs                → IHostedService: precalienta el pool de conexiones SQL al arrancar (performance)
  Theme/
    NorturTheme.cs                    → Paleta MudBlazor corporativa NORTUR
  wwwroot/                            → Assets estáticos (CSS, JS, Bootstrap)
    app.css                           → Estilos globales + sistema del drawer CSS
docs/                                 → biblioteca de documentación por tema (índice: docs/README.md)
  general/INFORME_TECNICO.md          → Documentación técnica completa
  pdfs/INFORME_TECNICO_NORTUR.pdf     → PDF del informe técnico
  performance/PERFORMANCE_GRILLAS_Y_CONEXION.md → lag de grillas grandes (pooling + Virtualize)
  buslink/                            → PLAN_MIGRACION + ANALISIS_SISTEMA + INFORME_AVANCE (.md + .docx)
  PlanoFoxPro/                        → "planos" del FoxPro por módulo (índice: PlanoFoxPro/README.md)
.claude/
  settings.json                       → Registra las skills locales del proyecto
  skills/
    blazor-nortur/SKILL.md            → Patrones UI/stack (drawer, KpiCard, ReportService, Excel)
    foxpro-extract/SKILL.md           → Leer forms FoxPro (.scx) — incluye scripts/scx_dump.py
    abm-metrocar/SKILL.md             → Metodología de migración de ABMs + regla de escritura
    modulo-trafico/SKILL.md           → Mapa del módulo Tráfico (tablas, estados, migrado/pendiente)
```

### Arquitectura general

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

### Instalación (ya hecha)

- Paquete NuGet: `Blazor-ApexCharts 6.1.0` (nombre correcto: con guión)
- Registrado en `Program.cs`: `builder.Services.AddApexCharts()`
- JS en `App.razor`: `<script src="_content/Blazor-ApexCharts/js/blazor-apexcharts.js"></script>`

### Regla crítica — colisión de namespaces

`ApexCharts` comparte nombres con `MudBlazor` (`Color`, `Size`, `ChartType`). **No agregar `@using ApexCharts` en `_Imports.razor`** — rompe todos los archivos que usan MudBlazor.

**Solución:** agregar `@using ApexCharts` solo en los archivos `.razor` que usen gráficos ApexCharts, al inicio del archivo.

### Uso básico en una página

```razor
@using ApexCharts   @* solo en páginas que usen ApexCharts *@

@* Gráfico de barras *@
<ApexChart TItem="MiDto" Title="Mi Título" Options="_optsBarras">
    <ApexPointSeries TItem="MiDto"
                     Items="_datos"
                     Name="Reservas"
                     SeriesType="SeriesType.Bar"
                     XValue="@(x => x.Etiqueta)"
                     YValue="@(x => x.Valor)" />
</ApexChart>

@code {
    private ApexChartOptions<MiDto> _optsBarras = new()
    {
        Chart = new() { Toolbar = new() { Show = false } },
        Colors = new List<string> { "#003AA0", "#F99410" }
    };
}
```

---

## Menú de navegación lateral (drawer)

### Decisión de arquitectura — drawer fuera del MudLayout

`MudDrawer` de MudBlazor dentro de `MudLayout` siempre se posiciona debajo del AppBar (el flex flow de MudLayout lo fuerza). Para lograr `position:fixed` desde `top:0`, el drawer es un `<div>` CSS puro **completamente fuera de `<MudLayout>`**.

### Estructura del layout

```
[nav-backdrop]         ← overlay oscuro, click cierra el drawer
[div.nav-drawer]       ← position:fixed, top:0, FUERA del MudLayout
  [nav-drawer__header] ← degradado azul NORTUR, altura 48px
  [nav-drawer__nav]    ← grupos colapsables por sección
[MudLayout]
  [MudAppBar]          ← hamburguesa, logo, usuario, cerrar sesión
  [MudMainContent] → @Body
```

### Secciones del menú actuales

| Sección | Estado | Ruta |
| --- | --- | --- |
| **Reservas** | expandida por defecto | — |
| → Reservas por fecha y servicio | activo | `/reservas-fecha-servicio` |
| → Reservas por banda horaria | activo | `/reservas-banda-horaria` |
| **Tráfico** | colapsado | — |
| → (placeholder) | deshabilitado | — |

### CSS del drawer (`wwwroot/app.css`)

| Clase | Descripción |
| --- | --- |
| `.nav-drawer` | Panel lateral, `position:fixed`, `top:0`, `width:270px` |
| `.nav-drawer--open` | Activa `transform:translateX(0)` |
| `.nav-backdrop` | Overlay oscuro detrás del drawer |
| `.nav-section__title` | Botón de grupo colapsable |
| `.nav-item` | Enlace de navegación |
| `.nav-item--active` | Ítem activo (borde naranja izquierdo) |
| `.nav-item--disabled` | Ítem sin ruta aún (gris, no clickeable) |

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

### ✅ Arquitectura Blazor + MudBlazor — HECHA

- Shell con navegación lateral CSS overlay (`MainLayout.razor`), tema NORTUR (`NorturTheme.cs`), autenticación con estado de sesión persistente por circuito SignalR.
- `KpiCard.razor` como componente reutilizable de tarjeta KPI.
- `ReportService` con caché en memoria (TTL 5 min).
- `ExcelExportService` con ClosedXML.
- **Skill local `blazor-nortur`** instalada en `.claude/skills/blazor-nortur/SKILL.md`.

### ✅ ApexCharts — INSTALADO Y CONFIGURADO

- `Blazor-ApexCharts 6.1.0` en el `.csproj`.
- Servicio registrado en `Program.cs`, JS cargado en `App.razor`.
- **Listo para usar en reportes nuevos** — agregar `@using ApexCharts` al inicio de cada `.razor` que lo use.

### ✅ Informe 1: "Reservas por fecha y servicio" — REARMADO (02/07/2026)

Componente `Components/Pages/ReservasFechaServicio.razor`. Rediseño completo validado
contra la base al dígito (smoke tests 15/15).

- **Barra de filtros horizontal compacta** (CSS `rfs-*` en `app.css`): período (datepickers
  en `PickerVariant.Dialog` + CSS `.mud-picker-paper.mud-dialog{min-width:310px}` porque si no
  el panel hereda el ancho del input=135px y recorta el header/días), servicios (multiselect
  con "Todos"), **estados** (multiselect de los 5 `estado_via`), switches **Internos** y
  **Cabeceras**, métrica (Reservas / Pax — cambia sin re-query, recálculo en memoria).
- **Cliente interno NORTUR excluido por defecto** (`parametro.id_cliente`), como todos los
  informes FoxPro — era ~6,8% de infle. Switch "Internos" para sumarlo.
- **Cabeceras excluidas por defecto:** `CABECERA_KM`/`CABECERA_SERV` NO son servicios reales,
  son **modos de facturación** (por km / por servicio; el destino real está en d_destino/
  h_destino). Eran ~90% del volumen y aplastaban el desglose por servicio. Se excluyen por
  defecto (switch "Cabeceras" para sumarlas), se sacan del dropdown de Servicios, y su volumen
  se muestra en un **KPI "Viajes cabecera"** aparte (transparencia). Constante
  `ReportService.ServiciosCabecera`; conteo `GetVolumenCabecerasAsync`.
- **KPIs** (fila flex `.rfs-kpis`, 4 o 5 tarjetas parejas): total reservas, total pax,
  canceladas (+% — muestra "—" si CANCELADO quedó fuera del filtro), servicios distintos, y
  "Viajes cabecera" (solo cuando están excluidas). Verificado por SQL: excluyendo cabeceras el
  período 02/05–02/07/2026 da 1.111 res / 31.010 pax / 24 serv; incluyéndolas 10.587 / 26.
- **Gráficos ApexCharts** (animaciones off — con animación, las capturas/vistas agarran el
  donut a medio dibujar): evolución diaria (área), barras top 10, donut top 8 + "Otros"
  (paleta categórica validada con dataviz; azul de serie `#2058D0`, el corporativo es
  demasiado oscuro para marcas de gráfico).
- **Tabla pivote** fecha × servicio: columna fecha fija + header/footer sticky, día de
  semana (finde en ámbar), ceros como `·`, fila TOTAL por columna, `<Virtualize>` en tbody.
- **Drill-down**: click en una **celda** o en el **total de fila** abre `ReservasFsDetalleDialog`
  (las reservas una por una, pill de estado; click en fila → Zoom del Viaje). El detalle se
  trae lazy 1 vez por filtro (`GetReservasFechaServicioDetalleAsync`).
- **Colores unificados por servicio (03/07/2026):** cada servicio tiene un color único, el
  MISMO en el gráfico de barras y en el donut (color por entidad, no por posición). Ver skill
  `blazor-nortur` § Colores unificados.
- **Cross-filter estilo Power BI (03/07/2026):** clic en un servicio (barra, porción del donut
  o **header/total de columna** de la tabla) enfoca todo el tablero en ese servicio — KPIs,
  evolución y tabla se recalculan en memoria (sin re-query) y los gráficos resaltan el servicio
  atenuando el resto. Chip "Filtrado por: X ✕" para quitar; reclic togglea. Ver skill
  `blazor-nortur` § Cross-filter. Sin librerías nuevas (ApexCharts `OnDataPointSelection`).
- **Switch "Cabeceras" eliminado (03/07/2026):** las cabeceras se excluyen SIEMPRE del desglose
  (comportamiento fijo, ya no configurable); el KPI "Viajes cabecera" sigue mostrando su volumen.
- **Excel** (ClosedXML): hojas Detalle + Pivote + Ranking + **Reservas** (una por una).

> **Lógica FoxPro (trampa):** el informe original del EXE productivo se llama "Reservas por
> Fecha en estado **SIN ASIGNAR O ASIGNADO**" — es demanda pendiente, NO histórico. Su form
> no existe en el fuente en disco (el ítem tampoco está en `MENU_PRINCIPAL.MPR`; ese popup
> solo tiene banda horaria). Para reproducir su número en Blazor: Estados = SIN ASIGNAR +
> ASIGNADO.

### ✅ Informe 2: "Reservas por banda horaria" — HECHO (+cross-filter 2D, 03/07/2026)

`Components/Pages/ReservasBandaHoraria.razor` (`/reservas-banda-horaria`), permiso `'R'`.
Réplica mejorada de `trafico_resumen_horario.scx`: viajes por franja horaria de inicio (las
6 bandas de `viaje_horario`, clasificadas por `CAST(hs_inicio AS TIME)` en el service).
Filtros período / tipo de vehículo / estados (default sin CANCELADO, fiel al FoxPro), métrica
Viajes↔Pax sin re-query, KPIs, barras apiladas fecha×banda, donuts por banda y por tipo de
vehículo, tabla pivote fecha×banda con drill-down (reusa `ReservasFsDetalleDialog` + Zoom) y
Excel. **Cross-filter estilo Power BI con DOS dimensiones combinables** (banda AND vehículo):
clic en un segmento/porción/leyenda/columna enfoca todo el tablero; la dimensión clickeada
resalta (atenúa el resto) y la otra + KPIs + tabla se filtran, en memoria y sin parpadeo
(update en el lugar). Chips "Banda: X ✕" / "Vehículo: Y ✕" en los tres paneles; los drill-downs
respetan el foco. Patrón y trampas: skill `blazor-nortur` § Cross-filter (variante dos
dimensiones).

### ✅ Informe 3: "Reservas por cliente" — HECHO (03/07/2026)

`Components/Pages/ReservasPorCliente.razor` (`/reservas-por-cliente`), permiso `'R'`, menú
Reservas → Informes. Réplica mejorada de `viaje_analisis.scx` (**menú Utilitarios → Reservas
por Cliente** del FoxPro, cuya única salida era una tabla dinámica de Excel por OLE). Plano:
`docs/PlanoFoxPro/reservas/RESERVAS_INFORME_POR_CLIENTE.md`. Cuenta viajes de transportación
(`origen='T'` — sin cabeceras por diseño, son origen 'P') por **cliente × mes × tipo de
unidad** (PROPIO `interno<1000` / CONTRATADO `interno>=1000` / SIN REALIZAR `interno=0`).
Decisiones vs FoxPro (acordadas 03/07/2026): cliente interno NORTUR **excluido por defecto**
(~30% del volumen acá; switch Internos), modo cancelados como **filtro flexible** (toggle
Activas/Canceladas + multiselect de los 6 motivos, default "CANCELADO POR CLIENTE"=motivo 2,
SIEMPRE respetando el período — el FoxPro barría todo el histórico), métrica Viajes↔Pax.
Barras apiladas mes×tipo, top-N clientes (selector Mostrar), donut por tipo, pivote
cliente×mes con drill-down, Excel 4 hojas (incl. Viajes con motivo de cancelación) y
**cross-filter 2D cliente AND tipo**. Trampas de réplica: `id_motivo` e `interno` vienen
NULL donde el DBF tenía 0 → `ISNULL(...,0)`. Validado al dígito contra SQL (4.632/98/65 +
celda GATE1×03/2026=335); smoke test en la suite.

**+ Control de tendencia mes-a-mes (15/07/2026, pedido y validado por el usuario):** en el
pivote cliente×mes se agregó (1) selector **"Comparar"** (vs mes anterior / vs N meses atrás
+ combo Salto / vs año pasado), (2) switch **"Variación"** que pone toda la grilla en modo Δ
(verde sube / rojo baja) y (3) columna **"Tendencia"** fija (Δ del último mes + %+ sparkline
SVG). Todo **en memoria** sobre `_pivMap`, sin re-query ni cambios en el service. El interanual
solo tiene números si el rango abarca el año previo (el usuario amplía el "Desde"). Patrón
reutilizable para cualquier pivote entidad×mes: skill `blazor-nortur` § Comparación mes-a-mes.

### ✅ Informes 4 y 5: "Viajes por chofer" y "Km Unidades vs Servicios" — HECHO (04/07/2026)

Los dos informes gemelos del menú **Utilitarios** del FoxPro (`viaje_analisis_chofer.scx` y
`viaje_analisis_km.scx`), migrados con el patrón dashboard completo. Menú **Vehículos y Choferes
→ Informes de Flota** (submenú nivel 3 nuevo), permiso `'V'`. Planos:
`docs/PlanoFoxPro/vehiculos-choferes/VIAJES_POR_CHOFER.md` y `KM_UNIDADES_VS_SERVICIOS.md`.

- **Viajes por chofer** (`ViajesPorChofer.razor`, `/viajes-por-chofer`): chofer × día con viajes,
  turismo (`origen='T'`) / cabecera (`origen='P'`), km, pax y **días de franco** (calculados en
  memoria como el FoxPro — día sin viajes entre el 1º y último día trabajado, se pintan "F" en
  ámbar). KPIs, evolución diaria (área turismo/cabecera), barras top-N, donut turismo/cabecera,
  pivote chofer×día con drill-down al Zoom, Excel (Resumen+Pivote con francos+Viajes),
  **cross-filter 1D por chofer**. Métrica Viajes↔Km↔Pax. Validado: jun 2026 = 97 chof / 1.951
  viajes / 125 tur / 1.826 cab / 55.379 km (idéntico a SQL).
- **Km Unidades vs Servicios** (`KmUnidadesServicios.razor`, `/km-unidades-servicios`): por unidad,
  km servicio (`SUM viaje.km`) vs km recorrido real (odómetro `vehiculo_km`), km vacío (recorrido
  − servicio), % vacío coloreado por eficiencia, días trabajados. KPIs, barras top-N, donut por
  tipo de vehículo, tabla por unidad con drill-down, Excel, **cross-filter 2D unidad AND tipo**.
  Validado: may 2026 = 203.522 km serv / 1.985.855 recorrido / 1.809.356 vacío (91%).
- **Decisiones vs FoxPro** (acordadas 04/07/2026): rango de fechas libre (el FoxPro filtraba un
  solo mes) y **switch "Incluir contratados"** (default solo PROPIO, fiel al FoxPro) + switch
  Internos. Default de Km = **dos meses atrás** (el odómetro se cierra con retraso; el mes en
  curso tiene `km_fin=0` → km vacío vacío).
- **🐛 Trampa CRÍTICA (Km):** en la réplica los campos de vehículo del viaje están **cruzados** —
  `viaje.id_vehicul` = TIPO (BUS/VAN/MINI), `viaje.id_vehicu2` = DOMINIO/patente. El odómetro
  une por `id_vehicu2`. **🐛 Bug heredado corregido:** el % vacío del FoxPro no protege contra
  odómetro incoherente (recorrido < servicio → % negativo gigante, -355.800% real); en Blazor
  esas unidades salen "—" (`recorrido >= km_servicio` en el chequeo `TieneOdometro`).

### ✅ Vistas de solo lectura migradas (lista + ficha) — HECHO

Réplicas fieles de pantallas FoxPro en **solo lectura** (lista + ficha modal, botonera de
ABM deshabilitada — la escritura sigue en FoxPro; estrategia strangler de `abm-metrocar`).
Mismo patrón y estilos CSS (`cli-*`, `zoom-*`) — calcar uno para hacer el siguiente.

| Entidad | Lista (página) | Ficha (dialog) | Doc FoxPro | Permiso | Menú |
| --- | --- | --- | --- | --- | --- |
| **Clientes** | `ClientesAbm.razor` (`/clientes-abm`) | `ClienteDetalleDialog.razor` | `CLIENTE_ABM.md` | `'F'` | Facturación → Clientes → ABM - Clientes |
| **Choferes** | `Choferes.razor` (`/choferes`) | `ChoferDetalleDialog.razor` (5 tabs) | `CHOFER_ABM.md` | `'V'` | Vehículos y Choferes → Choferes |
| **Vehículos** | `Vehiculos.razor` (`/vehiculos`) | `VehiculoDetalleDialog.razor` (6 tabs) | `skills/.../references/VEHICULOS.md` | `'V'` | Vehículos y Choferes → Vehículos - Flota |
| **Odómetros** | `Odometros.razor` (`/odometros`) | — (grilla + KPIs) | `PlanoFoxPro/vehiculos-choferes/ODOMETROS.md` | `'V'` | Vehículos y Choferes → Odómetros |
| **Siniestros** | `Siniestros.razor` (`/siniestros`) | `SiniestroDetalleDialog.razor` (5 solapas) | `PlanoFoxPro/vehiculos-choferes/SINIESTROS.md` | `'V'` | Vehículos y Choferes → Siniestros |
| **Agenda de Vencimientos** | `AgendaVencimientos.razor` (`/agenda-vencimientos`) | — (2 grillas + KPIs) | `PLANOFOXPRO/vehiculos-choferes/AGENDA_VENCIMIENTOS.md` | `'V'` | Vehículos y Choferes → Agenda de Vencimientos |
| **Fleteros** | `Fleteros.razor` (`/fleteros`) | `FleteroEditorDialog.razor` (4 modos, andamiaje) | `PLANOFOXPRO/vehiculos-choferes/FLETEROS.md` | `'V'` | Vehículos y Choferes → Fleteros |
| **Tipo de Vehículos** | `TiposVehiculo.razor` (`/tipos-vehiculo`) | `TipoVehiculoEditorDialog.razor` (4 modos, andamiaje) | `PLANOFOXPRO/vehiculos-choferes/TIPO_VEHICULOS.md` | `'V'` | Vehículos y Choferes → Tipo de Vehículos |

- **Choferes** (15/06/2026): grilla con filtro Fletero + búsqueda Nombre + Ver Egresados,
  columnas iguales al FoxPro, egresados en amarillo. Ficha con las 5 pestañas del FoxPro
  (Datos Personales, Domicilios, Teléfonos, Condiciones Laborales, Vehículos). Vencimientos
  de Registro/CNRT/AEP resaltados (rojo vencido / ámbar por vencer 30 días — valor agregado).
  Métodos `GetChoferesAsync` / `GetChoferDetalleAsync` en `ReportService`. Trampas resueltas:
  columnas truncadas a 10 chars (`registro_v/2/3/4`, `id_lista_p`, `real_domi*`, `entre_call`),
  `chofer_log` NO replicada, `vehiculo.id_vehicul`/`dominio` (no `id_vehiculo`/`patente`).
- **Vehículos - Flota** (15/06/2026): grilla con 15 columnas iguales al FoxPro + filtros
  Fletero / Ver Activos (arranca tildado) / Ver Flota Propia (`uso='PROPIO'`) + búsqueda por
  Dominio/Interno; egresados en amarillo (egresado = `!activo OR f_delete`). Ficha con las 6
  pestañas del FoxPro: Datos Vehículo, Permisos (`vehiculo_permiso`+`permiso`), Dueños
  (`vehiculo_dueno`+`dueno`, suma 100%), Cubiertas (columnas `r1..r7`, **no es tabla**),
  Tarjetas (YPF/ESSO en la propia `vehiculo`), Repuestos (`vehiculo_repuesto`, vacía).
  Vtos de Póliza/VTV/Matafuegos/Habilitación resaltados (rojo/ámbar). Métodos
  `GetVehiculosAsync` / `GetVehiculoDetalleAsync`. Trampas: orden visual de tabs ≠ nº de page,
  `tacografo_`=marca / `tacografo2`=nro, `gps_activo` nvarchar(1).
- **Odómetros** (04/07/2026): réplica de `vehiculo_km.scx` (Control de Odómetros). Grilla fiel
  (Dominio/Fecha/Año-Mes/Km Inicio/Fin/**Recorridos**/Interno/usuarios) + filtro *por vehículo*
  (autocomplete flota propia) / *todos* + rango de `f_carga`, + **KPIs** (Lecturas/Unidades/Km
  recorridos/Sin cierre) + Excel. Km Recorridos = `km_fin−km_inicio` (NULL si falta alguno o si
  daría negativo). Default = 2 meses atrás (el odómetro cierra con retraso; mes en curso
  `km_fin` NULL). Sin ficha (es un registro de lecturas). Métodos `GetOdometrosAsync` /
  `GetDominiosFlotaPropiaAsync`; export `ExcelExportService.Odometros`. ⚠️ La ASIGNACIÓN de
  Tráfico también escribe `vehiculo_km` → la tabla cambia de dueño el día D, no como catálogo
  suelto. Validado may–jul 2026: 203 lecturas / 106 unidades / 1.990.053 km.
- **Siniestros** (04/07/2026): réplica de `siniestro.scx`+`siniestro_abm.scx` (partes de
  accidente, ~70 campos). Lista (INNER JOIN a `chofer`) con "Buscar por" (=orden), filtro texto,
  filtro Tipo Acc. y Excel; ficha `SiniestroDetalleDialog` con **5 solapas** (El Hecho/asegurado ·
  Tercero · Propietario · Daños y descripción · Testigos). Métodos `GetSiniestrosAsync` /
  `GetSiniestroDetalleAsync`; export `ExcelExportService.Siniestros`. **🐛 Trampas:**
  `id_vehicul`=dominio del vehículo NORTUR (asegurado) vs `dominio`=dominio del **tercero**
  (`marca_y_mo` también del tercero); **sin `f_delete`** (solo `_deleted`, no hay egresados);
  ~30 columnas truncadas a 10 chars (`asegurado_dano`→`asegurado_`, `descripcion_acc`→
  `descripcio`, `test_N_nomb`→`test_N_nom`, etc.). Validado: 313 filas + fichas #6 y #12.
- **Agenda de Vencimientos + Fleteros + Tipo de Vehículos** (05/07/2026): los últimos 3 ítems del
  menú Vehículos y Choferes. **Agenda** (`AgendaVencimientos.razor`) = INFORME fiel de
  `agenda_vencimiento.scx`: 2 grillas (choferes registro/CNRT/AEP + vehículos propios VTV/matafuego)
  con celdas rojo/ámbar, KPIs y Excel; selector con modo **"según parámetros del sistema"** (fiel:
  chofer `aviso_cho`=30 / VTV `aviso_veh`=7 / mataf. `aviso_mat`=10, cada tipo su umbral) o umbral
  uniforme. Validado 249 choferes / 145 vehículos. **Fleteros** (`Fleteros.razor`) y **Tipo de
  Vehículos** (`TiposVehiculo.razor`) = catálogos en solo lectura **con andamiaje de ABM listo**:
  dialog editor multi-modo (`ver`/`alta`/`modifica`/`baja`, calca `UsuarioEditorDialog`) + escritura
  YA escrita en `AbmService` (`Alta/Modifica/BajaFleteroAsync`, `…TipoVehiculoAsync`) pero
  **deshabilitada** por `_abmActivo=false`. El día del corte a Buslink: `_abmActivo=true` + quitar
  `Disabled` de la botonera + bloquear FoxPro + apagar sync. **🐛 Trampas:** `fletero.id`/
  `vehiculo_tipo.id` NO son identity (alta `MAX(id)+1`); `parametro.aviso_*` son **bigint** →
  `CAST(... AS int)`; `GetFleterosAsync` ya existía (combo de Tráfico, devuelve `List<string>`) → el
  método de la grilla es `GetFleterosListaAsync`. Fleteros es catálogo **compartido con Facturación**
  (coordinar dueño único al activar el ABM). Planos: `docs/PLANOFOXPRO/vehiculos-choferes/`.

### ✅ Módulo Facturación — vistas de solo lectura (18/06/2026)

Migrado el submenú **Facturación → Resumen de Liquidaciones**, **Liquidación a Clientes** y
**Liquidaciones estimadas** (skill `modulo-facturacion-liquidacion`). Permiso `'F'`.
Tablas con dueño FoxPro → solo lectura.

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Resumen de Liquidaciones** | `ResumenLiquidaciones.razor` (`/resumen-liquidaciones`) | Réplica fiel de `liquidacion_cliente.scx`: maestro-detalle. Filtros Nº/Tipo (CLIENTE/PROVEEDOR)/rango fecha/cliente. Grilla cabeceras (`liquidacion` ⨝ `cliente`\|`fletero`) con columnas calculadas (Subtotal=ROUND((subtotal+extra)·t_cambio), Exento=adicional, TotalGral=+iva+adicional, Factura=tcp-lcp-ncp) + grilla detalle (`liquidacion_detalle`) al seleccionar. Revertir deshabilitado; **Factura abre comprobante en solo lectura** (`LiquidacionComprobanteDialog.razor`); Excel = cabeceras+detalle. |
| **Liquidación a Clientes** | `LiquidacionClientes.razor` (`/liquidacion-clientes`) | Réplica read-only de `facturacion_cliente_nueva.scx` (el form núcleo). Toolbar "Estado de las reservas" (combo + 2 fechas + botón `....`) y **árbol cliente→grupo** con las 2 cajas azules. **Rehecho 20/06/2026:** el árbol sale de **viajes pendientes de liquidar** (no de liquidaciones grabadas) — `GetViajesPendientesLiquidarAsync`. **POR ESTADO** (default, el más usado): `estado_via='FINALIZADO' AND f_grupo_fi < HOY`, ignora fechas → cajas Desde/Hasta **deshabilitadas**; **POR FECHA**: `f_grupo_fi BETWEEN`. Excluye el cliente de prueba `parametro.id_cliente` (=**NORTUR**). **4 solapas:** **Servicios** (viajes del grupo **valorizados en vivo** por el motor de tarifas — columna Importe por viaje + subtotal, badge S/TARIFA si falta precio; **click en una fila abre el Zoom del Viaje** reusando `ZoomViajeDialog`); **Adicionales** (`GetAdicionalesGrupoAsync` — **valorizados** contra `adicional_lista_precio` por adicional×tipo vehículo×vigencia, idéntico al FoxPro; estado ABONA/EXCLUIDO por `cliente_adicional_excluido`; badge S/TARIFA si falta precio); **Cliente** (ficha + grilla "Rubro de adicionales excluidos"); **Liquidacion** (**totales calculados en vivo** — cajas idénticas al FoxPro: Subtotal/Extras/Desc/Incr/Total/Cambio/IVA/Exento/Total Liquidación). Botonera de escritura (Graba) deshabilitada — solo lectura. |
| **Liquidaciones estimadas** | `FacturacionEstimada.razor` (`/facturacion-estimada`) | Proyección de venta por mes/cliente agregando `liquidacion_detalle` ya grabado (no re-valoriza viaje por viaje; el motor de tarifas ya existe —`ValorizarGrupoAsync`— pero esta vista usa lo liquidado por ser más rápido para visualizar tendencias). KPIs + gráfico mensual ApexCharts + tabla mes / tabla cliente + Excel. |

Métodos `ReportService`: `GetViajesPendientesLiquidarAsync` (árbol de Liquidación a
Clientes POR ESTADO/FECHA), `GetAdicionalesGrupoAsync` (solapa Adicionales valorizada),
`ValorizarGrupoAsync` (**motor de servicios `arma_servicio`** — precio por viaje) y
`CalcularTotalesLiquidacionAsync` (**totales solapa Liquidación `arma_liquidacion`**),
`GetLiquidacionesAsync`, `GetLiquidacionDetalleAsync`,
`GetLiquidacionCabeceraAsync` (cabecera cruda), `GetFacturacionEstimadaPorMesAsync`,
`GetFacturacionEstimadaPorClienteAsync`. Export:
`ExcelExportService.ResumenLiquidaciones` / `FacturacionEstimada`. CSS propio de
Liquidación a Clientes: clases `fcn-*` en `app.css`. **Valorización de adicionales
(20/06/2026):** la solapa Adicionales SÍ muestra precio/total reales — el tarifario
`adicional_lista_precio` está completo y vigente; se busca por adicional × `viaje.id_vehicul`
(=TIPO de vehículo: BUS/MINI) × fecha del viaje (`OUTER APPLY ... TOP 1 ORDER BY fdesdevg DESC`).
Verificado idéntico al FoxPro (GATE1/SAM-02: total 242.400). **Motor de SERVICIOS migrado
en vivo (22/06/2026):** `ValorizarGrupoAsync` + `CalcularTotalesLiquidacionAsync` replican
la cascada `arma_servicio`/`arma_liquidacion` (convenido→sin cargo→cabecera→servicio modo
S/H/K + horas extra + desc/incr) — **cálculo en vivo de solo lectura, NO graba** (strangler).
Validado al peso: 99,4% de 8.656 viajes históricos + grupo #2890197 (142807.34 / 38057.59 /
180864.93 exactos). Trampas finas en `docs/PlanoFoxPro/facturacion/FACTURACION_LIQUIDACION.md` §3.2 y
skill `modulo-facturacion-liquidacion` (bug minutos modo H, fracción 25, precio propio de
adicional que pisa la tarifa, tarifa retroactiva). Falta solo el **Graba** (escritura
transaccional + puente inverso). **Trampas resueltas:**
`cliente` NO tiene columna `nombre` (solo `razon_soci`); retenciones `retencion_`=IVA,
`retencion2`=IIBB, `retencion3`=SUSS (verificado contra el form); `total` viene NULL → se
recalcula siempre; mes con `CONVERT(char(7), fecha, 120)` (SQL 2012-friendly);
**`bGraba` guarda `liquidacion.subtotal`=total NETO de servicios y `liquidacion.extra`=ajuste
global manual** (no el desglose); **`viaje.id_viaje` y `viaje.pax` son `int` (no `bigint`)** →
`GetInt64` tira `InvalidCastException`, usar `CAST(... AS bigint)` en el SELECT.

### ✅ Módulo Tráfico — Cabeceras · Francos · Viáticos (05/07/2026)

Migrados los 3 ítems del menú **Tráfico** que faltaban (Cabeceras - Recorridos, Francos y
Viáticos, con sus submenús) en **solo lectura + andamiaje ABM** (patrón Fleteros/TipoVehiculo).
Permiso `'T'`. Planos: `docs/PlanoFoxPro/trafico/CABECERA_RECORRIDO.md`, `CHOFER_FRANCO.md`,
`CHOFER_VIATICO.md`. Los 7 ítems del menú ya son links reales (antes eran placeholders `--soon`).

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Cabeceras - Recorridos** | `CabecerasRecorridos.razor` (`/cabeceras-recorridos`) | Catálogo `cabecera` (código + 3 desc + recorrido largo), 187 filas. Ficha `CabeceraEditorDialog` (recorrido = editbox largo con wrap). |
| **Mantenimiento de Francos** | `Francos.razor` (`/francos`) | Grilla `chofer_franco` ⨝ `chofer` (71k filas → Virtualize + rango fecha obligatorio + combo motivo). |
| **Ingreso de Francos** | `FrancosIngreso.razor` (`/francos-ingreso`) + `FrancoAltaDialog` | Alta MASIVA: multiselect choferes × rango fechas × motivo (saltea duplicados). |
| **Auditoría Francos** | `FrancosAuditoria.razor` (`/francos-auditoria`) | **INFORME**: matriz chofer×día del mes (trb/franco/DUP), KPIs, Excel. Cruza `viaje` (titular + `id_chofer2`) vs francos. |
| **Viáticos** | `Viaticos.razor` (`/viaticos`) + `ViaticoEditorDialog` | Grilla `chofer_viatico` (4 JOINs), KPI total importe. Tabla VACÍA (sin uso). |
| **Motivo / Forma Liquidación** | `ViaticosMotivo.razor` / `ViaticosFormaLiquidacion.razor` | Catálogos id+nombre (VACÍOS) + `CatalogoSimpleEditorDialog` (un dialog parametrizado para ambos). |

Métodos `ReportService`: `GetCabecerasAsync`, `GetFrancosAsync`, `GetFrancoMotivosAsync`,
`GetFrancoAuditoriaAsync`, `GetViaticosAsync`, `GetViaticoMotivosAsync`, `GetViaticoLiquidaAsync`,
`GetChoferesComboAsync`. Escritura andamiaje en `AbmService` (Alta/Modifica/Baja de Cabecera,
alta masiva `AltaFrancosAsync` + `BajaFrancoAsync`, Viático, catálogos). Flags nuevos en
`AbmFeatureFlags`: `CabecerasAbmActivo`, `FrancosAbmActivo`, `ViaticosAbmActivo`,
`ViaticoCatalogosAbmActivo` (**todos `false`**). Export en `ExcelExportService`: `Cabeceras`,
`Francos`, `FrancosAuditoria`, `Viaticos`.

**🐛 Trampas clave (distintas al resto del proyecto):**
- **Baja FÍSICA** (`DELETE`), no lógica — las 5 tablas (`cabecera`, `chofer_franco`,
  `chofer_viatico`, `chofer_viatico_motivo`, `chofer_viatico_liquida`) **no tienen `f_delete`/
  `f_create`**. El andamiaje ABM refleja DELETE físico (a diferencia de `vehiculo_tipo`).
- **⚠️ Las 5 tablas están en el server VIEJO** (`DESKTOP-CV6LF0O`, el que apunta hoy la app)
  **pero NO en el nuevo** (`172.25.69.217`) → **replicarlas/crearlas allá antes del día D**.
- `chofer_franco`: 71k filas + fechas corruptas (`9201-03-03`) → Virtualize + `ClampFecha`.
- Auditoría: réplica usa `estado_via`, `tipo_chofe` (truncado), `id_chofer2`. Validado jun 2026
  = 98 choferes (idéntico a SQL). Cabeceras = 187 (idéntico). Viáticos/catálogos = 0 (vacíos).
- Pendiente menor: `chofer_franco_modifica.scx` (modifica de un franco puntual) — TODO.

### ✅ Módulo Tráfico — Voucher · Guardia · Contactos · Lista de pasajeros (07/07/2026)

Migrados los **4 ítems restantes** del menú **Tráfico** en **solo lectura + andamiaje ABM**
(patrón Fleteros/TipoVehiculo). Permiso `'T'`. Con esto el menú Tráfico **ya no tiene placeholders**.
Plano consolidado: `docs/PlanoFoxPro/trafico/TRAFICO_VOUCHER_GUARDIA_CONTACTOS.md`.

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Voucher Recepción** | `VoucherRecepcion.razor` (`/voucher-recepcion`) | Auditoría del voucher (comprobante que firma el pasajero). NO tiene tabla propia: lee/escribe `viaje` (`voucher_nr`/`voucher_re`). 3 modos: rango de voucher / rango de fecha / **sin recepcionar** (demanda pendiente de firma). KPIs, Excel. Botones de marcar recepción = **andamiaje** (tocan `viaje` → día D). |
| **Guardia** | `Guardias.razor` (`/guardias`) + `GuardiaEditorDialog` | ABM de `viaje_guardia` (guardias de choferes/unidades). Filtro rango fecha, KPIs. Modifica bloqueado si ya está pagada (`fpago`). Datos históricos 2006-2008 (default de rango amplio). |
| **Contactos** | `Contactos.razor` (`/contactos`) + `ContactoEditorDialog` | Catálogo `estacion` = **PROVEEDORES de toda la empresa** (178: estaciones, gomerías, grúas, fleteros…), **COMPARTIDO con Combustible**. Filtros rubro + campo + texto. Ficha con flags legacy de combustible. |
| **Rubros de contactos** | `RubrosContacto.razor` (`/rubros-contacto`) | `estacion_rubro` (8): id + nombre + flag `audita`. Reusa `CatalogoSimpleEditorDialog` (extendido con `audita`). |
| **Lista de pasajeros** | `ListaPasajeros.razor` (`/lista-pasajeros`) | Buscador de viaje (fecha + interno/servicio/cliente/destino) → abre el `ListaPasajerosDialog` ya existente. Sin escritura. |

Métodos `ReportService`: `GetVoucherAuditoriaAsync`, `GetGuardiasAsync`/`GetGuardiaRowAsync`,
`GetContactosListaAsync`/`GetContactoRowAsync`, `GetRubrosContactoAsync`, `GetViajesParaBuscadorAsync`.
Escritura andamiaje en `AbmService`: `Alta/Modifica/BajaGuardiaAsync`, `…ContactoAsync`,
`…RubroContactoAsync`, `MarcarRecepcionAsync`/`MarcarRecepcionLoteAsync`. Flags nuevos en
`AbmFeatureFlags`: `GuardiaAbmActivo`, `ContactosAbmActivo`, `RubrosContactoAbmActivo`,
`VoucherRecepcionActivo` (**todos `false`**). Export: `Voucher`, `Guardias`, `Contactos`, `RubrosContacto`.

**🐛 Trampas clave:**

- **Baja FÍSICA** (`DELETE`) en `viaje_guardia`/`estacion`/`estacion_rubro` (sin `f_delete`). Alta
  con `MAX(id)+1` (id no-identity). Las 3 tablas **SÍ están replicadas** en el server activo.
- **`viaje.interno` es `bigint`** → leerlo con `GetInt32` tira `InvalidCastException`. Fix:
  `CAST(ISNULL(v.interno,0) AS int)` (Voucher + buscador de Lista de pasajeros). Truncados verificados:
  `voucher_nr`/`voucher_re`/`hs_s_inici` (viaje), `id_vehicul`/`nombre_cho` (guardia),
  `control_sa`/`cairo_codi` (estacion; `rubro` es bigint FK a `estacion_rubro`).
- **`estacion` es catálogo compartido con Combustible** → coordinar dueño único al activar el ABM
  (como Fleteros con Facturación).
- **Voucher se activa el DÍA D** con el circuito (toca `viaje`), no como catálogo suelto; la
  marca de recepción usa WHERE `id_viaje` + `f_reserva` (no hay índice por `id_viaje`).
- Validado 07/07/2026: 5 guardias / 178 contactos / 8 rubros / 120 vouchers; 25/25 smoke tests.

### ✅ Módulo Combustible — Consumos · Conciliación · Saldos · Depósitos (07/07/2026)

Primera entrega del menú **Combustible** (permiso `'M'` — no confundir con `'C'`=avisos). El drawer
ya tenía la sección con placeholders; ahora son **5 links reales** (se sacó `nav-section--soon`).
Patrón solo lectura + **andamiaje ABM** (Fleteros/Contactos). Plano de mapeo:
`docs/PlanoFoxPro/combustible/COMBUSTIBLE_ABM_MENU.md` (relevamiento completo: `COMBUSTIBLE.md`).

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Promedio de Consumos** | `PromedioConsumos.razor` (`/promedio-consumos`) | INFORME analítico de `vehiculo_combustible_consumo`. l/100km por unidad medido con el **método correcto** (entre cargas LLENO: Σlitros/Σkm del tramo, promedio global real, sanitizando Δodómetro ≤0 o >1000km) — corrige los 2 bugs del FoxPro. KPIs, barras top-N por unidad (cross-filter 1D), evolución mensual, tabla con **drill-down** a las cargas (`CargasUnidadDialog`), Excel. Costo/km solo si hay importe. |
| **ABM y Conciliación cargas** | `CombustibleConciliacion.razor` (`/combustible-conciliacion`) + `CargaCombustibleEditorDialog` | GRILLA de `vehiculo_sobre` (filtro TODOS/DOMINIO/LOTE/ESTACIÓN, LOTE ignora fechas), KPIs (incl. sin conciliar), filas conciliadas en amarillo. Barra de conciliación (**Nuevo lote / Marcar / Desmarcar / Marcar todo**) + ABM de la carga = **andamiaje** (`ConciliacionCombustibleAbmActivo=false`). |
| **Saldos de Estaciones** | `SaldosEstaciones.razor` (`/saldos-estaciones`) | INFORME histórico debe(depósitos)/haber(consumos)/saldo por estación con control de saldo. Aviso "sin uso desde 2017", default 2013-2017. |
| **Carga de Depósitos** | `DepositosEstacion.razor` (`/depositos-estacion`) + `DepositoEstacionEditorDialog` | Grilla `vehiculo_estacion_saldo` (ingreso/egreso) + Agregar (andamiaje). |
| **Mantenimiento de Depósitos** | `DepositosMantenimiento.razor` (`/depositos-mantenimiento`) | Misma grilla + Eliminar (baja **física**, andamiaje). Barra de filtros compartida: `DepositosFiltros.razor`. |
| **Control de cargas** | `ControlCargas.razor` (`/control-cargas`) | INFORME de días sin cargar (réplica `trafico_vehiculo_combustible`): última carga + días + odómetro por unidad propia activa; check "solo atrasadas" con umbral; filas rojas ≥15 días. `GetControlCargasAsync`. Link `vehiculo.id_vehicul=vehiculo_sobre.dominio`. |
| **Consumo Mensual** | `ConsumoMensual.razor` (`/consumo-mensual`) | INFORME nuevo (no existe en FoxPro): litros por mes × unidad × estación × tipo. Métrica = **litros** (el importe viene 0 con prepaga). KPIs, barras litros/mes, donut por tipo (cross-filter), pivote mes×unidad, Excel. `GetConsumoMensualAsync`. |

Métodos `ReportService`: `GetPromedioConsumosAsync`, `GetCargasCombustibleAsync`,
`GetCargaCombustibleRowAsync`, `GetLotesCombustibleAsync`, `GetEstacionesCombustibleAsync`,
`GetSaldosEstacionesAsync`, `GetDepositosEstacionAsync`, `GetDepositoEstacionRowAsync`. Escritura
andamiaje en `AbmService`: `NuevoLoteAsync`, `Marcar/Desmarcar/MarcarLoteMasivoAsync`,
`Alta/Modifica/BajaCargaCombustibleAsync`, `Alta/BajaDepositoEstacionAsync`. Flags nuevos:
`ConciliacionCombustibleAbmActivo`, `DepositosCombustibleAbmActivo` (ambos `false`). Export:
`PromedioConsumos`/`CargasCombustible`/`SaldosEstaciones`/`DepositosEstacion`.

**🐛 Trampas clave (validadas contra la base):**
- **`ClampFecha` NO va en combustible** — acota a 2021, vaciaba los informes históricos 2013-2017
  (Saldos/Depósitos daban $0). Fix: helper propio `ClampComb` (rango 2009-2027) en los 4 métodos.
- **`vehiculo_sobre`**: `estacion_n` (nombre truncado 30), `idrubro`, y `interno`/`odometro`/`n_sobre`/
  `estacion`/`n_factura`/`n_remito` son **bigint**; `f_carga` es la fecha operativa; `n_sobre=0`=sin
  conciliar. Filtrar SIEMPRE `f_carga BETWEEN 2009 AND 2027` (años corruptos, MAX sin filtro=4202).
- **`vehiculo_estacion_saldo`** (787 filas, todas 2013-2017): sin `f_delete` → baja FÍSICA; egreso =
  importe negativo; empresa "NORTUR" (el fuente hardcodea "PATAGONIA" — bug latente, no reproducir).
- **Haber=0 en Saldos es dato real** (importe 0 en cargas prepagas 2013-2017), no bug — fiel al FoxPro.
- **Costo/km casi siempre "—"** (tarjeta prepaga carga importe 0 desde 2018); métrica sólida = l/100km.
- **`parametro.lote_sobre`/`rubro_comb` son bigint** → `CAST(... AS int)` al leer.
- **`estacion` compartida con Tráfico (Contactos)**: los catálogos Estaciones/Rubro/Artículos del menú
  FoxPro NO se re-migran acá (reusan `Contactos`/`RubrosContacto`). Coordinar dueño único al activar.
- Validado: Saldos Larrazábal=1.620.555,90 / Senillosa=6.943.745,64; Depósitos 787 movs / $50M;
  Consumos 80 unidades / 206.446 litros.

**2ª entrega (07/07/2026): Control de cargas + Consumo Mensual** (informes, sin escritura).
- **Control de cargas** (`/control-cargas`) — réplica de `trafico_vehiculo_combustible`: días sin
  cargar por unidad propia activa (última carga + `DATEDIFF` a hoy + odómetro), check "solo atrasadas"
  con umbral, filas rojas ≥15 días. Link `vehiculo.id_vehicul = vehiculo_sobre.dominio`.
- **Consumo Mensual** (`/consumo-mensual`) — informe **nuevo** (no existe en FoxPro): litros por mes ×
  unidad × estación × tipo. Métrica = **litros** (el importe viene 0 con prepaga → no hay costo real,
  verificado 2020-2026). KPIs, barras litros/mes, donut por tipo (cross-filter 1D), pivote mes×unidad.
- **🐛 Ojo Control de cargas:** la réplica está congelada al 08/06/2026 → hoy TODAS las unidades salen
  "atrasada" (los datos de julio no se replicaron). Es real, no bug; en producción con datos frescos
  se distribuye normal. `GetControlCargasAsync`/`GetConsumoMensualAsync`; exports `ControlCargas`/
  `ConsumoMensual`. Validado: 86 unidades, 529.096 litros 2026; 32/32 smoke tests.

**3ª entrega (07/07/2026): los 3 catálogos del menú → menú Combustible COMPLETO (10/10 ítems).**
Hallazgo (verificado en `MENU_PRINCIPAL.MPR` 427-429): los 3 ítems abren los MISMOS forms FoxPro que
otros menús — Estaciones→`estacion` y Rubro de Consumos→`estacion_rubro` son idénticos a los de
Tráfico (Contactos/Rubros de contactos). Decisión (validada con el usuario): **reutilizar** — los links
"Estaciones" y "Rubro de Consumos" apuntan a `/contactos` y `/rubros-contacto` (una sola pantalla
mantenida, DRY). Solo se creó la 3ª: **Artículos por Rubro de Consumo** (`ArticulosRubro.razor`,
`/articulos-rubro` + `ArticuloRubroEditorDialog`) sobre `estacion_rubro_articulo` — solo lectura +
andamiaje (`ArticulosRubroAbmActivo=false`). Combo de rubro (`estacion_rubro`) + nombre; para rubro 1
son los tipos de combustible (DIESEL 500 / EURO-DIESEL). 🐛 baja física, id no-identity (`MAX(id)+1`),
`nombre` truncado a 30. `GetArticulosRubroAsync`/`GetArticuloRubroRowAsync`; `Alta/Modifica/BajaArticuloRubroAsync`;
export `ArticulosRubro`. Validado: 2 artículos; 33/33 smoke tests. **El menú Combustible ya no tiene placeholders.**

### ✅ Módulo Reservas — Operadores · Grupos · Destinos (06/07/2026)

Migrados los 3 ítems del menú **Reservas** que faltaban (Operadores, Grupos, Destinos) en
**solo lectura + andamiaje ABM** (patrón Fleteros/TipoVehiculo). Permiso `'R'` (módulo Reservas).
Los 3 ítems del drawer pasan de placeholders `--disabled` a **links reales**. Planos verificados
al dígito contra el fuente FoxPro (dumps 06/07/2026): `docs/PlanoFoxPro/catalogos/CLIENTE_OPERADOR_ABM.md`,
`CLIENTE_GRUPO_ABM.md`, `DESTINO_ABM.md`.

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Operadores** | `Operadores.razor` (`/operadores`) + `OperadorEditorDialog` | Catálogo `cliente_operador` (128 filas): contacto de la agencia dentro de un cliente. `id_operado` = PK lógica **global**. LEFT JOIN a cliente (razón social). |
| **Grupos** | `Grupos.razor` (`/grupos`) + `GrupoEditorDialog` | `cliente_grupo` (11.272 → Virtualize) + combo No finalizados (default)/Finalizados/Todos. **Sin alta** (los grupos nacen en Reservas). Editor muestra el desglose de viajes por estado (cascada). |
| **Destinos** | `Destinos.razor` (`/destinos`) + `DestinoEditorDialog` | `destino` (398): lugares origen/destino (autocomplete Desde/Hasta). Checkbox `mas100km`, combo localidad (`destino_localidad`, 34). |

Métodos `ReportService`: `GetOperadoresListaAsync`/`GetOperadorRowAsync`,
`GetGruposListaAsync(filtro)`/`GetGrupoRowAsync`/`GetViajesGrupoPorEstadoAsync`,
`GetDestinosListaAsync`/`GetDestinoRowAsync`/`GetDestinoLocalidadesAsync`. Escritura andamiaje en
`AbmService` (Alta/Modifica/Baja de Operador y Destino + `AltaLocalidadAsync`; Grupos = **solo
Modifica/Baja en cascada**, sin alta). Flags nuevos en `AbmFeatureFlags`: `OperadoresAbmActivo`,
`DestinosAbmActivo`, `GruposAbmActivo` (**todos `false`**). Export: `ExcelExportService.Operadores`/
`Grupos`/`Destinos`.

**🐛 Trampas clave:**
- **Baja FÍSICA** (`DELETE`) en las 3 (sin `f_delete`; solo `_deleted` de la réplica). Alta con
  `MAX(id)+1` (id no-identity).
- **Grupos NO tiene alta** (el form FoxPro la tiene 100% comentada — nacen en Reservas). Su
  **modifica y baja operan EN CASCADA sobre `viaje`** → tocan el circuito viaje: `GruposAbmActivo`
  se activa el **día D** con Tráfico, no como catálogo suelto. Baja = cancelación masiva con motivo
  (`viaje_motivo_cancela`) + DELETE del grupo solo si no hay FINALIZADO/FACTURADO. Clasificación de
  estados **distinta entre baja y modifica**: en modifica **FINALIZADO es modificable**; en baja es
  bloqueante junto con FACTURADO (verificado contra el fuente).
- Operadores/Destinos son **Grupo A** del plan Buslink (cutover temprano); Grupos es **Grupo B**
  (día D).
- Columnas verificadas: `cliente_grupo` truncadas `f_grupo_fi`/`f_grupo_in`/`f_grupo_fc`; `id_operado`
  truncado. `viaje` para la cascada: `estado_via`, `grupo`, `id_cliente`, `id_motivo`, `interno`,
  `id_vehicul`, `id_chofer`, `nombre_cho`, `franco`.
- 🐛 Bug heredado corregido en Destinos: el modifica del FoxPro hace `contacto = contacto` (no
  guarda el contacto) — en Blazor **sí** se graba.
- Validado contra SQL: 128 / 189 (grupos no finalizados) / 398 / 34 localidades.

### ✅ Módulo Reservas — Reservas Especiales · Plantillas · Armado (07/07/2026)

Migrados los **2 ítems principales del menú Reservas** (los últimos placeholders operativos del
módulo) en **solo lectura + andamiaje ABM**. ⚠ A diferencia de los catálogos, estas 3 son
**puertas de alta al circuito `viaje`** (no CRUD de catálogo): insertan filas en `viaje`. Son
**Fase 4 del plan Buslink** y cambian de dueño el **día D** junto con Tráfico y el Graba de
Facturación — NO se activan como catálogo suelto. Permiso `'R'`. Planos (ya completos):
`docs/PlanoFoxPro/reservas/RESERVA_TRANSPORTACION.md` y `RESERVA_PLANTILLAS.md`.

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Reservas Especiales** | `ReservasEspeciales.razor` (`/reservas-especiales`) | Grilla de las reservas ya cargadas manualmente (`viaje.origen='T'`) — rango de fechas obligatorio + búsqueda + estados + Virtualize (~12-19k/año). Ficha reusa `ReservasFsDetalleDialog` (drill-down al Zoom). Botón **"Nueva reserva"** → abre `ReservaEspecialEditorDialog` (form de alta FIEL completo). |
| **Mantenimiento de Plantillas** | `PlantillasMantenimiento.razor` (`/plantillas-mantenimiento`) | Combo de las 9 plantillas + grilla de sus filas (`reserva_plantilla`, Virtualize). Botonera Agregar/Modificar/Eliminar/Eliminar Todo/Renombrar/Duplicar (andamiaje) → `PlantillaFilaEditorDialog` (4 modos) + `PlantillaNombreDialog` (renombrar/duplicar). |
| **Reservas por Plantillas (Armado)** | `ReservasPorPlantillas.razor` (`/reservas-por-plantillas`) | Combo plantilla + Buscar → grilla read-only de filas + cliente + rango de fechas + checks de días (L-D + Feriados) + botones rápidos. **Preview / dry-run EN MEMORIA** (cuántos viajes generaría, sin escribir). Botón Generar (andamiaje). |

Dialogs: `ReservaEspecialEditorDialog.razor` (form de alta fiel: fechas, cliente/operador,
servicios 1/2/3, vehículo, pax/km, grupo, guía, destinos con autocomplete, **Valor Especial**
solo con permiso `'F'`, Cnt Servicios, **grilla de Adicionales** en memoria, modo ruta "varios
días", **preview de filas** días×servicios), `PlantillaFilaEditorDialog.razor` (ver/alta/
modifica/baja, cabecera de 15 pos), `PlantillaNombreDialog.razor` (renombrar/duplicar con aviso
de fusión).

Métodos `ReportService`: `GetReservasEspecialesAsync` (reusa `ReservaFsDetalleRow`),
`GetPlantillasResumenAsync`/`GetPlantillasComboAsync`/`GetPlantillaFilasAsync`/
`GetPlantillaFilaRowAsync`, `GetFeriadosRangoAsync`. Escritura andamiaje en `AbmService`
(fiel a `graba_viaje`): `AltaReservaEspecialAsync` (resolución de grupo + loop días×servicios
o ruta + `viaje_log` + `viaje_adicional` + upsert `guia`, **con transacción**), ABM de
plantilla (`Alta/Modifica/Baja/BajaCompleta/Renombrar/DuplicarPlantilla…`), `ArmarPlantillaAsync`
(lote + lógica E/S de cabecera). Flags nuevos en `AbmFeatureFlags`: `ReservasEspecialesAbmActivo`,
`PlantillasAbmActivo`, `ArmadoPlantillasActivo` (**todos `false`**). Export:
`ExcelExportService.ReservasEspeciales` / `Plantillas`.

**🐛 Trampas clave:**
- **Bigint en `viaje`** (`id_grupo`/`id_plantil`/`id_viaje_i`/`interno`/`km`/`voucher_nr`) y en
  **`reserva_plantilla`** (`hs`/`km`/`km_real`/`pax`/`adi_can_1..5`) → `CAST(... AS int)` al leer.
- **`reserva_plantilla` baja FÍSICA** (DELETE, sin `f_delete`), id no-identity (`MAX(id)+1`).
  Cabecera es nvarchar(**15**) (la pos 16 lógica = rango de vehículo).
- **0 feriados 2026 cargados** → el armado avisa (fiel al FoxPro). El preview del armado es
  100% en memoria (dry-run, no toca la base).
- **Concatenación de raw strings SQL** (bug resuelto): concatenar `PlantillaFilaSelect` (raw
  string que NO termina en newline) con un `"""WHERE…"""` pega `s.id_serviciWHERE` → error de
  sintaxis. Fix: concatenar con `+ " WHERE …"` (string normal con espacio inicial).
- **Estado inicial único `SIN ASIGNAR`**, `cronograma=cronogram2='S/C'`, `str_f_rese` sincronizado
  con `f_reserva` — reglas de oro del INSERT (relevantes al activar el día D).
- Valor Especial requiere permiso `'F'` (precios convenidos) — la sección se oculta sin él.

Validado 07/07/2026 (dos señales UI+SQL): Reservas 'T' 30 días = 378 res / 9.637 pax; 9
plantillas / 574 filas; armado AEROLINEAS 07-14/07 L-V = 786 viajes (6 fechas × 131). Smoke tests
en la suite (verifican carga + botones de escritura deshabilitados). **El menú Reservas ya no
tiene placeholders operativos** (quedan "Clientes" y "Crear Plantillas", no pedidos en esta entrega).

### Drawer: arranca todo colapsado (15/06/2026)

Todas las secciones del menú lateral inician **colapsadas** para cualquier usuario
(flags `_*Expanded = false` en `MainLayout.razor`). Hay un aviso "Tocá una sección para
abrirla" (`.nav-hint`) al inicio del `<nav>`. El usuario abre la sección que necesite.

### ✅ ABM de Usuarios y Permisos — HECHO (01/07/2026) · PRIMER ABM DE ESCRITURA

El **primer ABM con escritura real** (alta/baja/modificación) del proyecto — hasta acá todo era
solo lectura. Estrena la estrategia "SQL dueño tabla por tabla": la tabla `usuario` migró de dueño
a SQL (ABM salido de FoxPro, sync DBF→SQL apagada) y Blazor la escribe en el **server local**.
Permiso `'S'` (solo supervisor). Menú: sección **Sistema** del drawer.

| Pieza | Archivo |
| --- | --- |
| Capa de escritura (nueva, plantilla) | `Services/AbmService.cs` — INSERT/UPDATE con `SqlParameter` + transacción, `AbmResult` |
| Catálogo de permisos | `Services/PermisosCatalogo.cs` — 16 letras en orden `S R T C D V L F A E U B H X N M` + reglas |
| Lectura | `ReportService.GetUsuariosAsync` / `GetUsuarioDetalleAsync` |
| Lista | `Components/Pages/UsuariosAbm.razor` (`/usuarios-abm`) |
| Dialog | `Components/Shared/UsuarioEditorDialog.razor` — un solo dialog, 4 modos ver/alta/modifica/baja |

Trampas resueltas: `usuario.id` **no es identity** (alta con `MAX(id)+1`); `password`/`acceso`
son `nvarchar(15)` (password plano; validar acceso ≤15 aunque haya 16 letras); baja lógica =
`f_delete` (no `_deleted`); `nivel` fijo `"12345"`; reglas en vivo C→T y X→SUPERVISOR; defensa
anti-autobloqueo. Validado con `ZZTEST01` (dos señales) + capturas. Detalle: skill
`abm-metrocar` (§ Primer ABM de escritura). **Pendiente producción real:** bloquear ABM en FoxPro
+ confirmar sync apagada antes de escribir en el server nuevo.

### Pendiente / próximos — el plan Buslink (02/07/2026)

El roadmap vigente es **`docs/buslink/PLAN_MIGRACION_BUSLINK.md`** (fases, día D, riesgos, DoD).
Resumen del orden:

- **Fase 0** (en curso): decisión `gps_xlm`, interruptor de sync, bloqueo FoxPro, mapeo
  de las 12 tablas del circuito, **regla del permiso `F`** (primera entrega de código).
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

```text
docs/
  PlanoFoxPro/           ← biblioteca de "planos" del FoxPro (21 docs), por módulo:
    README.md              ← ÍNDICE MAESTRO (leer primero)
    trafico/               ZOOM, FILTROS, TOOLBAR (spec Fase 3), HISTORIAL, GPS_XLM
    reservas/              TRANSPORTACION, PLANTILLAS, IMPORTA_EXCEL, BANDA_HORARIA
    catalogos/             los ABMs de Fase 1: CLIENTE, GRUPO, OPERADOR, DESTINO,
                           VIAJE_MOTIVO_CANCELA, FERIADO, GUIA, VIAJE_MOTIVO_CAMBIO
    facturacion/           FACTURACION_LIQUIDACION (módulo completo)
    combustible/           COMBUSTIBLE (módulo completo)
    vehiculos-choferes/    CHOFER_ABM (los otros 9 docs: skill modulo-vehiculos-choferes/references/)
    sistema/               USUARIO_ACCESOS
  pdfs/                    ← PDFs del proyecto
  INFORME_TECNICO.md
```

Hallazgos clave de la extracción de catálogos (02/07/2026): `gps_xlm()` **hoy es no-op**
(`parametro.xml_envia = 0` y `sql_gps = 0` — evidencia para la decisión GPS de Fase 0.2,
ver `trafico/GPS_XLM.md`); **0 feriados de 2026 cargados** (alerta de Fase 1);
`viaje_motivo_cambio_abm` tiene el Modificar ROTO en el fuente (pega a la tabla equivocada
— no copiar).

---

## El código FoxPro: a veces está en disco, a veces no

El sistema FoxPro completo está en `C:\MetroCarSys` (fuentes `.prg`, forms `.scx`/`.sct`, reportes `.frx`, menús `.mpr`). El `metrocar.exe` productivo está **más actualizado** que parte del fuente en disco — verificar caso por caso.

**Regla práctica:** antes de armar un informe, buscar su form/prg en disco. Si está, leer la lógica; si no, reconstruir desde la base.

### Lector DBF/FPT para inspeccionar forms FoxPro

Los `.scx`/`.frx`/`.sct`/`.fpt` son tablas DBF de VFP con el código en campos **memo**:
- El puntero al memo se guarda como **entero de 4 bytes little-endian** en el campo de registro.
- En el `.sct`, **block size = 1**. Bloque memo: 4 bytes tipo + 4 bytes longitud (big-endian) + contenido (cp1252).
- Campos relevantes por objeto: `METHODS` (código), `PROPERTIES`, `OBJNAME`.

### Ubicaciones de referencia FoxPro
- App completa: `C:\MetroCarSys`
- Reportes: `C:\MetroCarSys\Reports\*.frx` (~40)
- Programas: `C:\MetroCarSys\Progs\*.prg`
- Forms: `C:\MetroCarSys\Forms\*.scx` (378)
- Menús: `C:\MetroCarSys\Menus\*.mpr`
- DBF originales: `C:\MetroCarSys\Nortur` (cp1252)

---

## Próximos informes candidatos

- **Tráfico / Viajes** (`trafico_informe`, `trafico_imprime`, `trafico_grupo`) — operación
- **Cuenta Corriente** (`ctacte_saldo_cliente`) — financiero
- **Liquidación choferes** (`liquidacion_resumen`) — pagos a choferes/fleteros
- **Reservas por fecha y banda horaria** — `trafico_resumen_horario.scx` (lógica disponible)
- Combustible, Taller/Service, Viáticos, etc.

---

## Sistema de skills del proyecto (jun 2026)

Arquitectura de skills chicas y componibles (procesos × módulos), siguiendo los principios
de Anthropic: cada corrección aprendida se guarda en la skill correspondiente, no se repite.

| Skill | Tipo | Cubre |
| --- | --- | --- |
| `blazor-nortur` | horizontal | cómo construir UI/reportes en este stack |
| `foxpro-extract` | horizontal | cómo leer la lógica del FoxPro viejo (script incluido) |
| `abm-metrocar` | horizontal | cómo migrar escritura (ABMs) — **leer antes de cualquier INSERT/UPDATE** |
| `seguridad-nortur` | horizontal | permisos: `acceso` (letras), `nivel` (dígitos ABM), claims en Blazor |
| `modulo-trafico` | vertical | conocimiento del módulo Tráfico |
| `modulo-reservas` | vertical | conocimiento del módulo Reservas (12/06/2026) — alta manual, plantillas, grupos, catálogos, importa Excel; base para los ABMs futuros |
| `modulo-facturacion-liquidacion` | vertical | conocimiento del módulo Facturación/Liquidación (12/06/2026) — liquidación a clientes/fleteros/choferes, tarifarios, ctacte (sin uso). Doc detallado: `docs/PlanoFoxPro/facturacion/FACTURACION_LIQUIDACION.md` |
| `modulo-combustible` | vertical | conocimiento del módulo Combustible (12/06/2026) — cargas de la flota (tabla viva `vehiculo_sobre`, **NO replicada a SQL**), conciliación por lote, consumos l/100km, saldos de estaciones (sin uso desde 2017). Doc detallado: `docs/PlanoFoxPro/combustible/COMBUSTIBLE.md` |
| `modulo-vehiculos-choferes` | vertical | conocimiento del módulo Vehículos y Choferes (15/06/2026) — flota (`vehiculo`) y personal de conducción (`chofer`, `fletero`), tipos de vehículo, odómetros, siniestros, apercibimientos, capacitaciones, agenda de vencimientos. Una referencia por pantalla en `references/`. Trampas: `vehiculo_chofer` vacía, `chofer_log` no replicada, columnas truncadas. Choferes y Vehículos-Flota ya migrados (solo lectura) |
| `blazor-performance` | horizontal | optimización de rendimiento en Blazor (16/06/2026) — lentitud, re-renders, `MudDataGrid`/`MudTable`/`Virtualize<T>`, memory leaks, paginación, `StateHasChanged` excesivo. Referencias: `mudblazor-performance.md`, `apexcharts-blazor.md` |
| `testing-nortur` | horizontal | cómo testear/validar la app (29/06/2026) — smoke tests Playwright, capturas a demanda (`captura()` en `tests/helpers.ts`), validación de escritura de ABMs con **dos señales** (UI + `SELECT`), **protocolo de datos de prueba `ZZTEST`** sobre el servidor local (no ensuciar `replicaVPF`), dónde viven los errores en Blazor Server (lógica→server log, no browser). Complementa `abm-metrocar` (construir↔validar). Decisión registrada: `browser-tools-mcp` y la skill `browser-automation` descartadas |

**Skills de módulo futuras** (crear recién al arrancar cada módulo, no antes):
`modulo-taller` (taller/service). La cuenta corriente quedó cubierta dentro de
`modulo-facturacion-liquidacion` (módulo programado pero sin uso en producción).

### Decisión de escritura para ABMs (10/06/2026)

**SQL dueño, tabla por tabla (strangler):** Blazor solo escribe en tablas cuyo dueño ya es
SQL. Una tabla migra cuando su ABM Blazor está listo + se bloquea el ABM en FoxPro + se apaga
la sync DBF→SQL de esa tabla. Los datos escritos en SQL **se quedan en SQL** — no hay puente
inverso. Mientras tanto, las tablas de FoxPro son **solo lectura** desde Blazor.
Detalle completo y checklist: skill `abm-metrocar`.

---

## Contexto de negocio

Claudio Marañon construye una **agencia de IA para e-commerce**. Este proyecto (Metrocar Nortur) es un cliente de **migración/modernización de sistema legacy FoxPro**. El enfoque: reporte por reporte, incremental y replicable.
