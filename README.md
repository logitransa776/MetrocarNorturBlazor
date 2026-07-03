# Metrocar Nortur — Dashboard de Informes

Dashboard web moderno para **NORTUR (Metrocar)** — empresa de transporte/transfers/turismo de Buenos Aires (combis, traslados aeropuerto, city tours, cenas show, etc.).

Migra los informes del sistema legacy **FoxPro** a una plataforma interactiva, leyendo datos directamente desde la base SQL Server `replicaVPF`. El enfoque es incremental: un reporte a la vez, reconstruido como dashboard moderno (no una copia del Excel viejo).

---

## Stack

| Capa | Tecnología |
|---|---|
| Lenguaje | C# — .NET 10.0 LTS |
| Framework web | Blazor Server (modo interactivo, SignalR) |
| Componentes UI | MudBlazor 9.5 |
| Gráficos | Blazor-ApexCharts 6.1.0 |
| Acceso a datos | EF Core 10 — SQL crudo con `FromSqlRaw` |
| Export | ClosedXML — descarga a `.xlsx` |
| Base de datos | SQL Server — base `replicaVPF` (108 tablas, ~512.000 filas en `viaje`) |

---

## Levantar el proyecto

```bash
cd MetroCarSysBlazor
dotnet run
```

Abre en:
- **HTTP:** `http://localhost:5287`
- **HTTPS:** `https://localhost:7277`

Credenciales: validadas contra la tabla `usuario` de `replicaVPF`.

---

## Reportes implementados

### Informe 1 — Reservas por fecha y servicio (`/reservas-fecha-servicio`)

Análisis de reservas agrupadas por servicio en un período.

- **Filtros:** rango de fechas, servicios (multiselect), incluir/excluir canceladas, métrica (Reservas / Pax)
- **KPIs:** total reservas, total pax, canceladas (%), servicios distintos
- **Gráficos:** barras top 10 servicios + donut distribución top 12 (ApexCharts)
- **Tabla pivote:** fecha × servicio con totales
- **Export:** descarga a Excel con ClosedXML

### Informe 2 — Reservas por banda horaria (`/reservas-banda-horaria`)

Distribución de reservas pendientes (SIN ASIGNAR / ASIGNADO) por franja horaria.

- Bandas de `viaje_horario`: `00:02-06:29`, `06:30-08:29`, `08:30-14:00`, `14:01-18:00`, `18:01-23:59`
- Filtro por rango de fechas y tipo de servicio
- Gráficos ApexCharts

### Planilla de Tráfico (`/planilla-trafico`)

Vista operativa de viajes por día — análogo a la pantalla Tráfico del sistema FoxPro.

- Navegación día a día (`<<` fecha `>>`)
- Tablero de alertas en tiempo real: vehículos, choferes, pasajeros del día
- Zoom detalle de cada viaje (dialog `ZoomViajeDialog`)
- Export a Excel

---

## Estructura del proyecto

```
MetroCarSysBlazor/
  Program.cs                        → Bootstrap: DI, EF, MudBlazor, ApexCharts
  appsettings.json                  → Connection string a replicaVPF
  MetroCarSysBlazor.csproj          → NuGet packages
  Components/
    App.razor                       → Root component (incluye JS de ApexCharts)
    Routes.razor                    → Router
    _Imports.razor                  → Usings globales (sin ApexCharts — ver nota abajo)
    Layout/
      MainLayout.razor              → Drawer CSS overlay + AppBar + auth guard
      EmptyLayout.razor             → Layout limpio para login
    Pages/
      Home.razor                    → Página de inicio
      Login.razor                   → Formulario de login
      ReservasFechaServicio.razor   → Informe 1
      ReservasBandaHoraria.razor    → Informe 2
      PlanillaTrafico.razor         → Planilla operativa de tráfico
    Shared/
      KpiCard.razor                 → Tarjeta KPI reutilizable
      TableroAlertas.razor          → Barra de alertas del día (planilla tráfico)
      ChipAlerta.razor              → Chip individual de alerta
      ZoomViajeDialog.razor         → Dialog zoom/detalle de un viaje
  Data/
    NorturDbContext.cs              → DbContext (solo conexión — sin DbSet; SQL crudo)
  Services/
    ReportService.cs                → Queries SQL + caché IMemoryCache (TTL 5 min)
    AuthService.cs                  → Autenticación
    NorturAuthStateProvider.cs      → Estado de sesión (AuthenticationStateProvider)
    ExcelExportService.cs           → Generación .xlsx con ClosedXML
  Theme/
    NorturTheme.cs                  → Paleta MudBlazor corporativa NORTUR
  wwwroot/
    app.css                         → Estilos globales + sistema del drawer CSS

docs/                               → biblioteca de documentación (por tema, ver docs/README.md)
  general/INFORME_TECNICO.md        → Documentación técnica completa
  buslink/                          → plan de migración + análisis + informe de avance
  performance/ trafico/ facturacion/ combustible/ seguridad/ testing/
  PlanoFoxPro/                    → "planos" del FoxPro por módulo (ver su README.md)
```

---

## Conexión a la base de datos

**Servidor activo** (configurado en `appsettings.json`):

```
Server   = 172.25.69.217   (SQL Server 2012)
Database = replicaVPF
```

> El motor es SQL Server 2012: no usar `STRING_AGG`, `TRIM`, `CONCAT_WS`, `APPROX_COUNT_DISTINCT` ni otras funciones no soportadas al escribir SQL nuevo.

**Reglas de negocio en todas las queries:**
1. Siempre filtrar `_deleted = 0` (borrado lógico)
2. Acotar `f_reserva` a 2021-01-01 / 2027-12-31 (hay fechas corruptas: año 309, 2252, etc.)
3. Los campos `total`/`importe` tienen muchos NULL — las métricas confiables son **cantidad de reservas** y **pax**

---

## Nota sobre ApexCharts y MudBlazor

`ApexCharts` comparte nombres con `MudBlazor` (`Color`, `Size`, `ChartType`). **No agregar `@using ApexCharts` en `_Imports.razor`** — rompe todos los componentes MudBlazor.

Solución: agregar `@using ApexCharts` solo al inicio del archivo `.razor` que lo use, junto con los aliases:

```razor
@using ApexCharts
@using MudColor = MudBlazor.Color
@using MudSize  = MudBlazor.Size
```

---

## Agregar un reporte nuevo

1. Añadir el método de query en [Services/ReportService.cs](MetroCarSysBlazor/Services/ReportService.cs) — `async Task<List<MiDto>>`
2. Crear [Components/Pages/MiReporte.razor](MetroCarSysBlazor/Components/Pages/) con `@page "/mi-ruta"` (tomar `ReservasFechaServicio.razor` como plantilla)
3. Agregar el link en [Components/Layout/MainLayout.razor](MetroCarSysBlazor/Components/Layout/MainLayout.razor) en la sección correspondiente del drawer

---

## Próximos informes

| Informe | Origen FoxPro | Módulo |
|---|---|---|
| Reservas por chofer / vehículo | `trafico_informe.frx` | Tráfico |
| Cuenta Corriente clientes | `ctacte_saldo_cliente.scx` | Finanzas |
| Liquidación choferes | `liquidacion_resumen.scx` | Pagos |
| Combustible | varios | Flota |

---

## Sistema legacy FoxPro

Las fuentes originales están en `C:\MetroCarSys` (forms `.scx`, reportes `.frx`, programas `.prg`).
Antes de construir un informe nuevo, revisar si el form FoxPro correspondiente está disponible en disco y documentar su lógica en `docs/PlanoFoxPro/`.

---

## Contexto del proyecto

Este dashboard es parte de la migración gradual del sistema **Metrocar (FoxPro)** de NORTUR hacia una plataforma .NET moderna. El enfoque es incremental: cada informe se reconstruye como un dashboard interactivo mejorado, no como una réplica del sistema viejo.
