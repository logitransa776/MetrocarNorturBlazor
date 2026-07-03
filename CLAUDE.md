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
| → Reservas por banda horaria | deshabilitado (próximo) | — |
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

### ✅ Vistas de solo lectura migradas (lista + ficha) — HECHO

Réplicas fieles de pantallas FoxPro en **solo lectura** (lista + ficha modal, botonera de
ABM deshabilitada — la escritura sigue en FoxPro; estrategia strangler de `abm-metrocar`).
Mismo patrón y estilos CSS (`cli-*`, `zoom-*`) — calcar uno para hacer el siguiente.

| Entidad | Lista (página) | Ficha (dialog) | Doc FoxPro | Permiso | Menú |
| --- | --- | --- | --- | --- | --- |
| **Clientes** | `ClientesAbm.razor` (`/clientes-abm`) | `ClienteDetalleDialog.razor` | `CLIENTE_ABM.md` | `'F'` | Facturación → Clientes → ABM - Clientes |
| **Choferes** | `Choferes.razor` (`/choferes`) | `ChoferDetalleDialog.razor` (5 tabs) | `CHOFER_ABM.md` | `'V'` | Vehículos y Choferes → Choferes |
| **Vehículos** | `Vehiculos.razor` (`/vehiculos`) | `VehiculoDetalleDialog.razor` (6 tabs) | `skills/.../references/VEHICULOS.md` | `'V'` | Vehículos y Choferes → Vehículos - Flota |

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
