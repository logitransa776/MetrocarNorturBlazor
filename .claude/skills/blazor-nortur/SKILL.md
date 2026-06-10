---
name: blazor-nortur
description: >
  Guía de patrones, convenciones y componentes del proyecto Metrocar NORTUR en Blazor Server (.NET 10 LTS + MudBlazor 9.5).
  Usar SIEMPRE que se trabaje en este proyecto: agregar reportes, modificar el layout, crear componentes UI,
  escribir queries en ReportService, exportar Excel, o tocar cualquier archivo .razor, .cs o .css del proyecto.
  También usar cuando el usuario pregunte cómo hacer algo en la UI, cómo agregar un menú, cómo conectar datos,
  o cómo replicar el estilo NORTUR. Incluye los patrones ya probados y resueltos: drawer, KpiCard, tema, caché,
  SQL crudo, export Excel. Evita reinventar lo que ya funciona.
---

# Blazor NORTUR — Guía de patrones del proyecto

## Stack

| Capa | Tecnología |
| --- | --- |
| Framework | Blazor Server interactivo (.NET 10 LTS) |
| UI | MudBlazor 9.5.0 |
| Gráficos | Blazor-ApexCharts 6.1.0 (instalado) |
| Datos | EF Core 10 + SQL crudo (`FromSqlRaw` / `SqlQuery`) |
| Export | ClosedXML |
| DB | SQL Server — base `replicaVPF` |

---

## Paleta NORTUR (Theme/NorturTheme.cs)

```csharp
NorturColors.Azul      = "#003AA0"   // azul principal
NorturColors.AzulNoche = "#112F5B"   // header/drawer header
NorturColors.Naranja   = "#F99410"   // acento, logo diamante
NorturColors.AzulClaro = "#E8EFF9"   // highlights
NorturColors.Verde     = "#16A34A"   // éxito
NorturColors.Rojo      = "#DC2626"   // error/canceladas
```

El tema ya está registrado en `MainLayout.razor` vía `<MudThemeProvider Theme="NorturTheme.Theme" />`.
Para usar colores en inline style: `Style="@($"color:{NorturColors.Azul};")"`.

---

## Layout (MainLayout.razor)

El layout usa un **drawer CSS puro** (fuera del MudLayout) para garantizar `position:fixed` desde `top:0`.
**No usar `MudDrawer` de MudBlazor** — se posiciona debajo del AppBar por el flujo del MudLayout.

### Estructura del layout

```
[nav-backdrop]         ← overlay oscuro, click cierra el drawer
[div.nav-drawer]       ← panel lateral, position:fixed, top:0, FUERA del MudLayout
  [nav-drawer__header] ← degradado azul NORTUR, altura 48px (igual al AppBar)
  [nav-drawer__nav]    ← grupos colapsables con botones HTML nativos
[MudLayout]
  [MudAppBar]          ← header con hamburguesa, logo, usuario, cerrar sesión
  [MudMainContent]
    [MudContainer MaxWidth="ExtraExtraLarge"]
      @Body
```

### Agregar una sección al drawer

Cada sección del menú sigue este patrón en `MainLayout.razor`:

```razor
@* Estado en @code: *@
private bool _miSeccionExpanded = false;

@* En nav-drawer__nav: *@
<div class="nav-section">
    <button class="nav-section__title @(_miSeccionExpanded ? "nav-section__title--open" : "")"
            @onclick="() => _miSeccionExpanded = !_miSeccionExpanded">
        <MudIcon Icon="@Icons.Material.Filled.IconName" Size="Size.Small" Class="nav-section__icon" />
        <span>Mi Sección</span>
        <MudIcon Icon="@(_miSeccionExpanded ? Icons.Material.Filled.ExpandLess : Icons.Material.Filled.ExpandMore)"
                 Size="Size.Small" Class="nav-section__chevron" />
    </button>
    @if (_miSeccionExpanded)
    {
        <div class="nav-section__items">
            <a href="/mi-ruta"
               class="nav-item @(Nav.Uri.Contains("mi-ruta") ? "nav-item--active" : "")"
               @onclick="ToggleDrawer">
                <MudIcon Icon="@Icons.Material.Filled.IconName" Size="Size.Small" Class="nav-item__icon" />
                <span>Nombre del reporte</span>
            </a>
            @* Ítem deshabilitado (sin ruta aún): *@
            <span class="nav-item nav-item--disabled">
                <MudIcon Icon="@Icons.Material.Filled.IconName" Size="Size.Small" Class="nav-item__icon" />
                <span>Próximamente...</span>
            </span>
        </div>
    }
</div>
<div class="nav-divider"></div>
```

---

## Agregar un reporte nuevo — 3 pasos

### 1. Query en ReportService.cs

```csharp
public async Task<List<MiReporteRow>> GetMiReporteAsync(DateOnly desde, DateOnly hasta)
{
    var key = $"mi-reporte|{desde:yyyyMMdd}|{hasta:yyyyMMdd}";
    return await _cache.GetOrCreateAsync(key, async entry =>
    {
        entry.AbsoluteExpirationRelativeToNow = CacheTtl;  // 5 min
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var sql = $"""
            SELECT ...
            FROM viaje v
            WHERE v._deleted = 0           -- SIEMPRE filtrar _deleted
              AND v.f_reserva BETWEEN '{desde:yyyy-MM-dd}' AND '{hasta:yyyy-MM-dd}'
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = new List<MiReporteRow>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new MiReporteRow(...));
        return result;
    }) ?? new();
}

// DTO como record al final del archivo:
public record MiReporteRow(DateOnly Fecha, string Campo, int Valor);
```

**Reglas de negocio siempre presentes:**
- `_deleted = 0` en toda query sobre `viaje` y otras tablas
- Fechas acotar a `FechaMinValida` (2021-01-01) / `FechaMaxValida` (2027-12-31)
- `total`/`importe` tienen muchos NULL — usar `COALESCE(campo, 0)`
- Escapar strings: `.Replace("'", "''")`

### 2. Página .razor en Components/Pages/

```razor
@page "/mi-ruta"
@inject ReportService ReportSvc
@inject ExcelExportService ExcelSvc

<PageTitle>Mi Reporte — NORTUR</PageTitle>

@* Panel de filtros *@
<MudPaper Class="pa-4 mb-4" Elevation="1">
    <MudGrid>
        <MudItem xs="12" sm="6" md="3">
            <MudDatePicker Label="Desde" @bind-Date="_desde" ... />
        </MudItem>
        @* más filtros *@
        <MudItem xs="12" sm="6" md="2">
            <MudButton Variant="Variant.Filled" Color="Color.Primary"
                       FullWidth="true" OnClick="CargarDatos"
                       Disabled="_cargando">
                @(_cargando ? "Cargando..." : "Aplicar filtros")
            </MudButton>
        </MudItem>
    </MudGrid>
</MudPaper>

@* KPIs *@
<MudGrid Class="mb-4">
    <MudItem xs="12" sm="6" md="3">
        <KpiCard Label="TOTAL RESERVAS" Value="@_totalReservas.ToString("N0")"
                 Sub="@($"{_desde:dd/MM/yyyy} – {_hasta:dd/MM/yyyy}")" />
    </MudItem>
</MudGrid>

@* Tabla *@
<MudPaper Elevation="1" Class="pa-0">
    <MudTable Items="_datos" Dense="true" Hover="true" Striped="true"
              Loading="_cargando" FixedHeader="true" Height="500px">
        <HeaderContent>
            <MudTh>Fecha</MudTh>
            <MudTh Style="text-align:right">Valor</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.Fecha.ToString("dd/MM/yyyy")</MudTd>
            <MudTd Style="text-align:right">@context.Valor.ToString("N0")</MudTd>
        </RowTemplate>
    </MudTable>
</MudPaper>

@code {
    private List<MiReporteRow> _datos = new();
    private bool _cargando = false;
    private DateTime? _desde = DateTime.Today.AddDays(-30);
    private DateTime? _hasta = DateTime.Today;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) await CargarDatos();
    }

    private async Task CargarDatos()
    {
        _cargando = true;
        StateHasChanged();
        var desde = DateOnly.FromDateTime(_desde ?? DateTime.Today.AddDays(-30));
        var hasta = DateOnly.FromDateTime(_hasta ?? DateTime.Today);
        _datos = await ReportSvc.GetMiReporteAsync(desde, hasta);
        _cargando = false;
    }
}
```

### 3. Link en MainLayout.razor

Agregar `<a href="/mi-ruta" ...>` dentro del `nav-section` correspondiente (ver patrón arriba).

---

## Componente KpiCard

```razor
<KpiCard Label="ETIQUETA MAYÚSCULAS"
         Value="@numero.ToString("N0")"
         Sub="texto secundario opcional"
         Color="@NorturColors.Azul" />
```

Parámetros: `Label` (string, required), `Value` (string, required), `Sub` (string, opcional), `Color` (string, default azul NORTUR).

---

## Export Excel (ExcelExportService)

```csharp
// En la página .razor:
@inject ExcelExportService ExcelSvc
@inject IJSRuntime JS

private async Task DescargarExcel()
{
    var bytes = ExcelSvc.GenerarExcel(_datos, "NombreHoja");
    await JS.InvokeVoidAsync("downloadFile", "reporte.xlsx",
        Convert.ToBase64String(bytes));
}
```

```razor
<MudButton Variant="Variant.Filled" Color="Color.Success"
           StartIcon="@Icons.Material.Filled.Download"
           OnClick="DescargarExcel">
    Descargar Excel
</MudButton>
```

---

## ApexCharts — integración y reglas de uso

### Instalación (ya hecha — no repetir)

- NuGet: `Blazor-ApexCharts 6.1.0` (nombre con guión, no `Blazor.ApexCharts`)
- `Program.cs`: `builder.Services.AddApexCharts()`
- `App.razor`: `<script src="_content/Blazor-ApexCharts/js/blazor-apexcharts.js"></script>`

### Regla crítica — colisión de namespaces con MudBlazor

`ApexCharts` tiene tipos que colisionan con `MudBlazor`: `Color`, `Size`, `ChartType`.
**NO agregar `@using ApexCharts` en `_Imports.razor`** — rompe todos los archivos que usan MudBlazor.

**Solución:** agregar `@using ApexCharts` al inicio de cada `.razor` que use gráficos ApexCharts.

### Uso básico

```razor
@using ApexCharts   @* solo en páginas con gráficos *@

<ApexChart TItem="MiDto" Title="Mi Título" Options="_opts">
    <ApexPointSeries TItem="MiDto"
                     Items="_datos"
                     Name="Reservas"
                     SeriesType="SeriesType.Bar"
                     XValue="@(x => x.Etiqueta)"
                     YValue="@(x => x.Valor)" />
</ApexChart>

@code {
    private ApexChartOptions<MiDto> _opts = new()
    {
        Chart = new() { Toolbar = new() { Show = false } },
        Colors = new List<string> { "#003AA0", "#F99410" }
    };
}
```

---

## Patrones MudBlazor 9.5 — notas importantes

- **MudChart warnings MUD0002**: `XAxisLabels`, `InputData`, `InputLabels` son atributos no reconocidos en v9. Funciona igual pero muestra warnings. Los reportes nuevos deben usar ApexCharts en lugar de MudChart.
- **MudTable**: usar `Dense="true" Hover="true" Striped="true"` como estándar.
- **MudDatePicker**: el binding es `DateTime?`, convertir a `DateOnly` al llamar el service.
- **MudSelect multiselect**: `MultiSelection="true" @bind-SelectedValues="_seleccion"` donde `_seleccion` es `IEnumerable<string>`.
- **`@rendermode InteractiveServer`** no es necesario en páginas porque ya está configurado globalmente en `Routes.razor`.
- **StateHasChanged()**: llamar antes del await en operaciones largas para mostrar el spinner.

---

## Estructura de archivos del proyecto

```
MetroCarSysBlazor/
  Components/
    Layout/
      MainLayout.razor    ← drawer CSS + AppBar + auth guard
      EmptyLayout.razor   ← solo para /login
    Pages/
      ReservasFechaServicio.razor   ← Informe 1 (plantilla base)
      [NuevoReporte].razor          ← cada reporte nuevo aquí
    Shared/
      KpiCard.razor                 ← componente reutilizable
  Data/
    NorturDbContext.cs              ← solo puerta de conexión, sin DbSets
  Services/
    ReportService.cs                ← todas las queries + caché
    ExcelExportService.cs           ← export .xlsx con ClosedXML
    AuthService.cs                  ← valida contra tabla `usuario`
    NorturAuthStateProvider.cs      ← estado de sesión SignalR
  Theme/
    NorturTheme.cs                  ← NorturColors + MudTheme
  wwwroot/
    app.css                         ← estilos del drawer y globales
```

---

## CSS del drawer (wwwroot/app.css)

Las clases del drawer son CSS puro (no MudBlazor). Las principales:

| Clase | Descripción |
|-------|-------------|
| `.nav-drawer` | Panel lateral, `position:fixed`, `top:0`, `width:270px` |
| `.nav-drawer--open` | Activa `transform:translateX(0)` |
| `.nav-backdrop` | Overlay oscuro detrás del drawer |
| `.nav-section__title` | Botón de grupo colapsable |
| `.nav-item` | Enlace de navegación |
| `.nav-item--active` | Ítem activo (borde naranja izquierdo) |
| `.nav-item--disabled` | Ítem sin ruta aún (gris, no clickeable) |
| `.nav-divider` | Línea separadora entre secciones |
