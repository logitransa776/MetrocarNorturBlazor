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

## Patrón de INFORME ANALÍTICO (dashboard) — plantilla probada

Referencia viva: **`ReservasFechaServicio.razor`** (rearmado 02/07/2026). Copiar su estructura
para cualquier informe con filtros + KPIs + gráficos + tabla. Las piezas y por qué:

1. **Barra de filtros horizontal** (no panel lateral): `<MudPaper Class="rfs-filtros">` con
   `display:flex; flex-wrap:wrap; gap`. Cada control con `Margin.Dense` + `Variant.Outlined`.
   Datepickers en modo Dialog + su fix de ancho (ver § MudDatePicker).
   **LAYOUT ESTÁNDAR de la barra (convención fija validada por el usuario, 05/07/2026 — Odómetros):**
   los **filtros a la IZQUIERDA** y los **botones de acción (Filtro/Aplicar · Actualizar · Excel)
   TODOS JUNTOS a la DERECHA**. Se logra con un `<span style="flex:1 1 auto;">` como separador
   ANTES de la botonera, y los botones envueltos en un **mismo `<div>` flex** (gap ~6px) para que
   no se partan a otra línea al hacer wrap (si van sueltos, el `flex-wrap` manda Excel abajo).
   Controles relacionados (ej. Interno + Dominio, Desde + Hasta) van agrupados en su propio
   `<div>` flex con gap chico (~6px) para que se lean como una unidad. Selectores con ancho
   cómodo (~150px) — que se lea el valor completo con la ✕ de Clearable. Regla del proyecto:
   **"filtros a la izquierda, acciones a la derecha, la botonera nunca se parte."** Referencia
   viva del layout: `Odometros.razor` (clase `.odo-filtros` + `.odo-grupo` en app.css).
2. **Snapshot de filtros al Aplicar.** Guardar los filtros *aplicados* en campos aparte
   (`_fDesdeAp`, `_servAp`, …), NO leer los de edición. Así el drill-down y el Excel
   corresponden a lo que se está viendo, no a lo que el usuario editó a medias sin aplicar.
   Convención con el service: **lista vacía = sin filtro (todos)**.
3. **KPIs en fila flex** (`.rfs-kpis { display:flex; gap; flex-wrap }` + hijos `flex:1 1 0;
   min-width:180px`). Reparte parejo N tarjetas — más flexible que `MudGrid`/`MudItem` cuando
   el número de KPIs varía. `KpiCard` es horizontal y desborda si la columna es angosta.
4. **Métrica que cambia sin re-query** (ej. Reservas ↔ Pax): recalcular en memoria desde el
   dataset ya traído (`CambiarMetrica` → `Recalcular()`), no volver a SQL.
5. **Gráficos ApexCharts con `Animations = new() { Enabled = false }`.** Con animación, las
   capturas y la primera vista agarran el donut/área a medio dibujar. Remontar los charts al
   recargar datos con `<div @key="_chartsKey">` (regenerar el Guid en `Recalcular`).
6. **Tabla pivote**: `<Virtualize SpacerElement="tr">` en el `<tbody>` (regla de performance);
   columna clave `position:sticky; left:0`; header y `<tfoot>` de totales `position:sticky`.
7. **Drill-down con detalle lazy**: la query de detalle (una fila por registro) se trae **una
   sola vez por combinación de filtros** (cacheada), recién al primer click o Excel — no en la
   carga inicial. Celdas clickeables abren un dialog (`ReservasFsDetalleDialog`) que reusa el
   Zoom del Viaje al clickear una fila.
8. **Excel multi-hoja** (`ExcelExportService`): agregado (pivote/ranking) **+ hoja de detalle
   una-por-una** cuando el informe la tiene.
9. **Color por entidad, MISMO en todos los gráficos** (regla dataviz, 03/07/2026). Cuando un
   informe tiene dos o más gráficos sobre la misma dimensión (ej. barras "por servicio" + donut
   "distribución por servicio"), cada categoría debe tener **un color único, igual en todos los
   gráficos**: LA PLATA=azul en barras Y en donut, AEROPARQUE=naranja en ambos, etc. El color
   sigue a la **entidad, no a la posición** dentro de cada gráfico. Patrón implementado en
   `ReservasFechaServicio.razor` (ver § Colores unificados abajo).
10. **Cross-filter / foco estilo Power BI — ESTÁNDAR para todo informe analítico nuevo**
    (pedido explícito del usuario, 03/07/2026; validado por él en producción local en los dos
    informes de Reservas). Clic en una categoría (barra, segmento apilado, porción de donut,
    leyenda o columna de la tabla) **enfoca todo el tablero** en esa categoría: KPIs, evolución
    y tabla se **recalculan en memoria** (sin re-query), y los gráficos de esa dimensión
    **resaltan** la categoría atenuando el resto. Chip "Filtrado por: X ✕" para quitar; clic de
    nuevo en la misma categoría togglea. Si el informe tiene 2+ dimensiones categóricas, los
    focos son combinables (AND), uno por dimensión. Ver § Cross-filter abajo (incluye la
    variante dos dimensiones). No preguntar si se incluye: se incluye.

### Colores unificados por categoría — patrón (ReservasFechaServicio.razor)

El error natural es dejar que cada `ApexChart` asigne colores por índice: entonces el mismo
servicio queda de un color en las barras y de otro en el donut. Solución: **un mapa único
`categoría → color` que alimenta a todos los gráficos.**

```csharp
// Paleta categórica (10 hues validados con dataviz, ΔE CVD ≥ 12) + gris para "Otros".
private static readonly List<string> _palette = new()
{ "#2058D0","#F99410","#16A34A","#DC2626","#0EA5E9",
  "#7C3AED","#B45309","#DB2777","#0D9488","#65A30D" };
private const string _colorOtros = "#94A3B8";

private Dictionary<string,string> _colorPorServicio = new();
private string ColorDe(string s) => _colorPorServicio.TryGetValue(s, out var c) ? c : _colorOtros;

// En Recalcular(), DESPUÉS de tener el ranking ordenado por valor:
_colorPorServicio = ranking
    .Select((x,i) => (x.Servicio,i))
    .ToDictionary(t => t.Servicio, t => t.i < _palette.Count ? _palette[t.i] : _colorOtros);

// Cada gráfico recibe SUS colores en el mismo orden que sus items, leídos del mapa:
_optsBar.Colors = _barItems.Select(b => ColorDe(b.Servicio)).ToList();
_optsPie.Colors = _pieItems.Select(p => ColorDe(p.Servicio)).ToList();  // "Otros" → gris
```

Claves:
- **Ranking por valor decide el hue** (top 1 = azul, top 2 = naranja…): estable dentro de un
  mismo dataset. Los que caen fuera de los 10 hues (que el donut pliega en "Otros") van a gris.
- **Barras top 10 y donut top 10 + "Otros"** (03/07/2026): la paleta tiene 10 hues justamente
  para que los 10 servicios del top tengan color propio en AMBOS gráficos (antes eran 8 hues +
  donut top 8, y los #9/#10 salían grises — el usuario los quería distintos). "Otros" agrupa del
  #11 en adelante. Regla dataviz: un 11º color NUNCA es un hue generado → se pliega en "Otros".
- **Gráfico de barras con UNA serie** necesita `PlotOptions.Bar.Distributed = true` para que
  cada barra tome su propio color (sin `Distributed`, todas usarían `Colors[0]`). Con
  `Distributed` la leyenda repite el eje Y → `Legend = new() { Show = false }`.
- Los charts se remontan con `@key="_chartsKey"` al recargar, así toman las `Colors` nuevas.
- **Validar la paleta** con el script de la skill `dataviz`
  (`node scripts/validate_palette.js "<hex,…>" --mode light`) antes de fijarla. La actual pasa
  separación CVD; los hues de bajo contraste están OK porque el gráfico tiene labels/leyenda
  (secondary encoding).

### Cross-filter / foco estilo Power BI — patrón (ReservasFechaServicio.razor)

Clic en una categoría enfoca todo el tablero en ella, **sin volver a SQL** (recálculo en
memoria del dataset ya traído, igual que la métrica Reservas↔Pax). NO hace falta ninguna
librería nueva: ApexCharts (ya instalado) emite el evento de click y Blazor guarda el estado.

**Modelo mental (el de Power BI):** en el visual donde clickeás, la categoría se **resalta**
(el resto se atenúa) — así podés cambiar de foco; en los DEMÁS visuales, se **filtra**.
Concretamente: los gráficos de esa dimensión (barras/donut) siguen mostrando todas las
categorías pero atenúan las no enfocadas; los KPIs, la serie temporal y la tabla se
recalculan al subconjunto enfocado.

**Piezas:**
```csharp
private string? _servicioFocus;   // null = sin foco (todos)
private bool EnFoco(string s) => _servicioFocus is null || _servicioFocus == s;

// Toggle: reclic en la misma categoría la quita. "Otros" (categoría plegada) NO es enfocable.
private void ToggleFoco(string s) {
    _servicioFocus = (_servicioFocus == s || s == "Otros") ? null : s;
    Recalcular();
}
private void LimpiarFoco() { _servicioFocus = null; Recalcular(); }

// Color atenuado si hay foco y no es el enfocado (gris claro #D9E1EC) — efecto highlight.
private string ColorFoco(string s) =>
    (_servicioFocus is null || s == _servicioFocus) ? ColorDe(s) : "#D9E1EC";
```

En `Recalcular()`, separar en dos bloques:
- **Ranking / colores / items de los gráficos de categoría** → SIEMPRE sobre el dataset
  completo (no cambian con el foco; solo cambian sus *colores* vía `ColorFoco`).
- **KPIs / serie temporal / tabla** → sobre `datos = _detalle.Where(d => EnFoco(...))`.

**Evento de ApexCharts** — la firma correcta en Blazor-ApexCharts 6.1.0 (verificada):
```razor
<ApexChart TItem="BarItem" Options="_optsBar" OnDataPointSelection="OnBarSelection"> ...
```
```csharp
// NO existe e.Item. El punto clickeado está en e.DataPoint; e.DataPoint.Items es
// IEnumerable<TItem> con los items agrupados en ese punto (uno por categoría en barras/donut).
private void OnBarSelection(SelectedData<BarItem> e) {
    var s = e.DataPoint?.Items?.FirstOrDefault()?.Servicio;
    if (s is not null) ToggleFoco(s);
}
```
`SelectedData<T>` expone: `Chart`, `Series`, `DataPoint`, `IsSelected`, `DataPointIndex`,
`SeriesIndex`. `DataPoint<T>` expone: `X`, `Y`, `Items`, `FillColor`, `Goals`, `Extra`.

**Sin parpadeo al cambiar de foco (03/07/2026) — CLAVE.** El `<div @key="_chartsKey">` que
envuelve los charts **desmonta y remonta** los ApexChart cuando el key cambia → eso PARPADEA.
Regenerar el key SOLO cuando cambian los datos de verdad (Aplicar filtros, cambio de métrica),
**NUNCA** en un cambio de foco. Para el foco, actualizar los charts **en su lugar** con `@ref`
+ `UpdateOptionsAsync(true, true, false)` (barras/donut) y `UpdateSeriesAsync(true)` (área) —
es el update suave de ApexCharts, sin remontar. Sacar `_chartsKey = Guid.NewGuid()` de
`Recalcular()` y ponerlo en `Cargar()` y `CambiarMetrica()`; `ToggleFoco` es async y llama a un
`ActualizarGraficos()` tras `Recalcular()`. (Verificable en test: marcar el MudPaper del panel
con un atributo y comprobar que sobrevive al foco → no remontó.)

**Ubicación del chip de foco:** vive al lado del título del gráfico (`.rfs-bar-head`: título a
la izquierda, chip/hint a la derecha), NO como barra ancha sobre los KPIs. Está **duplicado en
los dos paneles** (barras y donut) — la cabecera se extrajo a un `RenderFragment CabeceraFoco(string titulo)`
para no repetir el markup (el título va por parámetro porque el de barras es dinámico con la
métrica). La ✕ de cualquiera de los dos chips limpia el foco de todo el tablero.

**Doble-resaltado — DESACTIVAR el realce interno de ApexCharts (03/07/2026) — CLAVE.** Al usar
`OnDataPointSelection`, ApexCharts marca por su cuenta la porción/barra clickeada con su estado
`active` (además de nuestro highlight por color) → se veían DOS porciones resaltadas en el donut
y una barra extra en gris oscuro. Fix: en las `Options` de barras Y donut, desactivar los
estados internos:
```csharp
States = new()
{
    Active = new() { AllowMultipleDataPointsSelection = false, Filter = new() { Type = StatesFilterType.none } },
    Hover  = new() { Filter = new() { Type = StatesFilterType.none } },
};
```
OJO: los valores del enum `StatesFilterType` en Blazor-ApexCharts 6.1.0 son **minúscula**
(`none`, `lighten`, `darken`) — `StatesFilterType.None` NO compila. Así el clic dispara nuestro
handler pero ApexCharts no pinta nada por su cuenta; manda solo `ColorFoco`.

**Cursor pointer sobre las marcas** (para señalar que son clickeables): por CSS en
`.apexcharts-bar-area, .apexcharts-pie-area, .apexcharts-slice, .apexcharts-legend-series { cursor: pointer; }`.

**Selector "Mostrar Top N" en las barras (03/07/2026).** Cuando hay muchas categorías (24
servicios) y el gráfico solo muestra el top 10, el resto queda invisible. Solución: un
`MudSelect` "Mostrar" (Top 10/15/20/Todos=999) que controla `_barItems = ranking.Take(_topN)`.
Claves: (1) el **donut se deja fijo en top 10 + Otros** — 24 tajadas son ilegibles; solo las
barras crecen (tienen etiqueta por barra). (2) **Alto dinámico**: `Height="@_altoBarras"` con
`_altoBarras = Math.Max(360, _barItems.Count * 30)` para que no se apelotonen. (3) Cambiar N
cambia la FORMA del gráfico → remontar con `_chartsKey` nuevo (como el cambio de métrica), no
`UpdateOptions`. (4) Las barras #11+ salen en gris (solo 10 hues) — OK en barras porque cada una
tiene su etiqueta; el gris solo dice "fuera del top 10". (5) La tabla pivote ya muestra TODOS los
servicios en columnas, así que el detalle completo siempre está disponible ahí.

**Trampas resueltas:**
- **Resetear el foco al Aplicar filtros** (`_servicioFocus = null` en `Cargar()`): un nuevo
  dataset puede no contener el servicio enfocado.
- **KPIs que no aplican con foco**: ocultar el "Viajes cabecera" cuando hay foco (las cabeceras
  no son ese servicio) — sumar `&& _servicioFocus is null` a su condición.
- **En la tabla**, el clic en el **header/total de columna** hace foco (`ToggleFoco`); el clic
  en un **número/celda** mantiene su función previa (drill-down al detalle). No mezclar.
- **Testing (Playwright)**: para las capturas de un estado enfocado, **NO usar `captura()`** —
  hace `page.goto()` que RECARGA y pierde el foco. Tomar `page.screenshot()` directo sin
  re-navegar. Para clickear una categoría, el **header de la tabla** (`th.rfs-th-serv` por
  texto) es más determinístico que el hit-test de una barra SVG de ApexCharts. Botón del chip:
  `getByRole('button', { name: /Quitar filtro/i })` (el texto de MudButton va en un `<span>`).

**Variante con DOS dimensiones combinables (03/07/2026) — `ReservasBandaHoraria.razor`.**
Segundo informe con cross-filter, con dos focos independientes que se combinan con AND
(`_bandaFocus` y `_vehiculoFocus`), como los cross-filters de Power BI. Reglas de esa variante:
- **Semántica por visual:** el foco de una dimensión RESALTA en sus propios visuales (atenúa
  por color, valores intactos) y FILTRA los visuales de la OTRA dimensión; KPIs, totales de
  fila y gran total llevan AMBOS focos. En `Recalcular()` se arman 3 subconjuntos: `datosVeh`
  (para los visuales de banda), `datosBanda` (para el donut de vehículo) y `datos` (ambos).
- **El foco cambia VALORES de los otros charts, no solo colores** → tras `Recalcular()` va
  `UpdateOptionsAsync(true, true, false)` **y** `UpdateSeriesAsync(true)` en cada chart
  (en la variante de una dimensión bastaba UpdateOptions en barras/donut).
- **Categorías SIEMPRE completas y en orden estable** en cada gráfico (con valor 0 si el foco
  de la otra dimensión las vacía): si la cantidad/orden de series o porciones cambiara, el
  update en el lugar reordena o exige remontar (parpadeo). Para el donut de vehículo, ranking
  y colores salen del dataset COMPLETO (estables), solo los valores llevan el filtro.
- **Clic en la LEYENDA también enfoca:** `OnLegendClicked` en el `<ApexChart>` + en Options
  `Legend.OnItemClick = new() { ToggleDataSeries = false }` (apaga el default de ApexCharts de
  ocultar la serie, que confunde con el foco). En multi-serie la categoría viene en
  `e.Series?.Name`; en donut, en `e.DataPoint?.Items?.FirstOrDefault()`.
- **Los drill-downs respetan el foco activo**: el número visible en una celda/fila ya está
  filtrado → el detalle debe filtrar igual (`PasaFocoVeh`/`PasaFocoBanda`) y el subtítulo del
  dialog debe decirlo (sufijo " · BUS").
- **Barras APILADAS multi-serie**: el clic en un segmento llega igual por
  `OnDataPointSelection`; como cada `ApexPointSeries` ya viene filtrada por categoría, el
  primer item de `e.DataPoint.Items` trae la categoría de la serie clickeada.

### Trampa de negocio: no todo `id_servici` es un servicio real

Los informes agrupados por `servicio` pueden estar dominados por filas que NO son servicios de
transporte. Caso confirmado: `CABECERA_KM` / `CABECERA_SERV` son **modos de facturación** (~90%
del volumen), no viajes; aplastan el desglose. Regla: en un informe por servicio, **preguntar al
usuario** si esas categorías van excluidas por default (con switch reversible + KPI del volumen
excluido). Ver memoria `[[cabeceras-no-son-servicios]]` y skill `modulo-facturacion-liquidacion`.

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
  - **En una barra de filtros horizontal, usar `PickerVariant="PickerVariant.Dialog"`.** El
    default (Inline) abre el calendario dentro del flujo y empuja/recorta la barra.
  - **Trampa del ancho (verificada 02/07/2026):** el panel del calendario hereda el `width`
    del input. Con inputs angostos (≤160px) el header ("sáb, 02 may") y los días quedan
    cortados — solo se ven 3 columnas. Fix por CSS: `.mud-picker-paper.mud-dialog { min-width:
    310px; width:auto; }` (el `.mud-picker-paper` es el `<div>` limitado, NO `.mud-picker-container`
    —esa clase no existe). Ancho cómodo del input para `dd/MM/yyyy`: **~160px** (135 recorta el año).
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

## Themes (switcher en el AppBar)

Dos themes claros: **NORTUR clásico** (default) y **Compacto gris** (`body.theme-gris`).

- Variables CSS `--nt-*` definidas en `app.css` (`:root` = clásico, `body.theme-gris` = gris).
  Las usan la grilla de tráfico, el header del panel Buses y el drawer header.
- `wwwroot/theme.js` aplica la clase en `<body>` y persiste en `localStorage` (`nortur-theme`).
- Botón paleta en el AppBar (`MainLayout.razor`): `CambiarTheme()` → `JS norturTheme.set(...)`;
  `OnAfterRenderAsync(firstRender)` sincroniza el estado con `norturTheme.get()`.
- El AppBar tiene el degradado azul inline → el theme gris lo pisa con
  `body.theme-gris .mud-appbar { background: ... !important; }`.
- Para que un componente nuevo respete el theme: usar `var(--nt-...)`, no colores fijos.

## Estándar de MODALES — variables `--nt-dlg-*` (06/07/2026)

**Regla del proyecto (pedida y validada por el usuario):** TODO dialog del sistema se ve con
el MISMO tamaño de fuente, paddings y compactación. El estándar sale de la calibración del
Zoom del Viaje (que el usuario aprobó) y está centralizado en **variables CSS en `:root`**
(`app.css`, junto a las `--z-*` de color). **Objetivo de diseño: que un modal entre en una
terminal de tráfico de 1280×720 sin desbordar** (hay monitores/tablets de 720p en la empresa;
el desarrollo se hace en pantallas grandes y engaña).

**Por qué funciona con pocos cambios:** casi todos los dialogs comparten la clase base
`zoom-viaje-dialog` (shell: header azul noche, fondo, `max-width`); los editores y fichas suman
`cli-dialog nortur-ficha`. `cli-dialog` no tiene reglas propias (es solo un hook). Entonces el
sistema real de estilos de modal es **`.zoom-*` + la capa `.nortur-ficha`** — tocar las
variables reajusta los ~33 dialogs a la vez.

Las variables (calibrar acá, se propaga a todo):
```css
:root {
  --nt-dlg-font-label:  0.68rem;   /* etiqueta de campo (MAYÚS) */
  --nt-dlg-font-value:  0.83rem;   /* valor del campo */
  --nt-dlg-font-boxlbl: 0.72rem;   /* título de bloque/sección */
  --nt-dlg-pad-row:     2px 12px;  /* padding de una fila de campos */
  --nt-dlg-pad-boxlbl:  2px 12px;  /* padding del título de bloque */
  --nt-dlg-gap-box:     5px;       /* separación vertical entre bloques */
  --nt-dlg-line:        1.25;      /* interlineado de valores */
  --nt-dlg-content-pad: 12px 16px 14px;    /* padding del cuerpo */
  --nt-dlg-max-h:       calc(100vh - 100px);  /* tope de alto del cuerpo */
}
/* Terminal chica: más viewport al modal + apretar espaciado. La FUENTE NO baja
   (legibilidad primero); si no entra, el cuerpo scrollea dentro del modal. */
@media (max-height: 800px) {
  :root {
    --nt-dlg-max-h:       calc(100vh - 72px);
    --nt-dlg-gap-box:     4px;
    --nt-dlg-content-pad: 8px 14px 10px;
  }
}
```

Las consumen: `.mud-dialog-content` (padding + `max-height`), `.zoom-field__label/__value`
(fuente + interlineado), `.zoom-box*` (gap + padding + título), y **la capa `.nortur-ficha`**
(que ANTES redefinía tamaños propios distintos —0.8/0.76rem, margin 10px— que contradecían la
compactación; ahora hereda los mismos tamaños del estándar, y solo mantiene lo que es su
identidad visual: dato liviano `font-weight:500`, franja gris del header, borde/sombra de
tarjeta).

**Decisiones clave (no cambiar sin motivo):**
- **Legibilidad primero, scroll interno si hace falta** (elección del usuario): NO se baja la
  fuente por debajo de lo legible para forzar "todo en una pantalla". Si un dialog gigante
  (ficha Vehículo con 6 tabs, Siniestro con 5 solapas) no entra, scrollea DENTRO del modal
  vía `--nt-dlg-max-h` — nunca desborda el viewport.
- **Un dialog nuevo NO fija tamaños de fuente propios**: usa las clases `zoom-*`/`nortur-ficha`
  y ya toma el estándar. Si necesita un tamaño distinto, primero preguntarse si el estándar
  está mal (y cambiar la variable) antes de hacer una excepción local.
- **Verificado a 1280×720** (capturas Playwright con `test.use({ viewport })`): Zoom del Viaje,
  ficha Vehículo (la más densa) y editor ABM Usuario entran completos y legibles; 26/26 smoke
  tests OK. Ver [[testing-nortur]] §D para capturar a una resolución fija.

## Zoom del Viaje — layout de la ficha (ZoomViajeDialog.razor)

Ficha modal de solo lectura de una reserva/viaje — **una de las pantallas más usadas**
(se abre desde Tráfico, los informes de Reservas, Liquidación, etc.). Estructura: banda de
estado arriba + grid de dos columnas (izquierda = bloques `zoom-box`; derecha = Adicionales +
Valores del Servicio). Datos vía `Reports.GetDetalleViajeAsync` (SEEK por `f_reserva` si se
pasa `FReserva`) + `GetAdicionalesViajeAsync`, en paralelo (`Task.WhenAll`).

### Regla de oro de la ficha: NUNCA recortar un valor con "…" (04/07/2026)

Los campos son datos operativos que el usuario **necesita leer completos** (nombre de cliente,
recorrido Desde/Hasta, guía, servicio con descripción). El error a evitar: `white-space: nowrap
+ overflow: hidden + text-overflow: ellipsis` en `.zoom-field__value` — recorta el dato y solo
se ve con hover. **Regla fija validada por el usuario:** el valor hace **wrap** a 2+ líneas y se
ve completo, con **tooltip nativo (`title=`) como respaldo**. Aplica también a las fichas
`.nortur-ficha` (Cliente/Chofer/Vehículo), que heredan `.zoom-field__value`.

```css
.zoom-field__value {
    font-size: 0.83rem;          /* achicado 2px vs el 0.96rem original: más datos en 1 línea */
    white-space: normal;         /* NO nowrap */
    overflow-wrap: anywhere;
    word-break: break-word;
    line-height: 1.25;
}
```
En el `.razor`, cada helper que pinta un valor agrega `title="valor completo"` (en `F`, `Fv`,
`FDoc` y la celda de nombre de la tabla de Adicionales). El `title` va solo si el valor no es
vacío ni `"—"`.

### Anchos de campo: dar espacio a los textos largos, angostar los cortos

Cada fila (`.zoom-row`) es flex y reparte el ancho por *peso* (`flex`). El helper `F(...)`
controla el peso con `ancho:` (1..3 → `.zoom-field--{n}`) y con `sm: true` (angosto flexible).
Regla: **más `ancho` a los campos de texto largo, `sm` a los de valor corto** (códigos, números,
campos casi siempre vacíos) — así los largos entran en 1 línea y no wrapean.

```razor
@* Servicios: descripciones largas → ancho 3; código/km → sm *@
@F("1° Servicio",   Serv(...), ancho: 3)
@F("2° Servicio",   Serv(...), ancho: 3)
@F("3° Servicio",   Serv(...), ancho: 2)
@F("Cód. Cabecera", Str(...),  sm: true)
@F("Kilómetros",    ...,       sm: true)

@* Recorrido: Desde/Hasta largos → ancho 3; Distrito flexible (ancho 2, "CAPITAL FEDERAL"
   no entra en sm); Rec. Celular casi siempre "—" → sm *@
@F("Desde",          Str(...), ancho: 3)
@F("Hasta",          Str(...), ancho: 3)
@F("Distrito Inicio",Str(...), ancho: 2)
@F("Rec. Celular",   Str(...), sm: true)
```
```css
.zoom-field    { flex: 1 1 0; min-width: 90px; }
.zoom-field--2 { flex: 2 1 0; }
.zoom-field--3 { flex: 3 1 0; }
/* sm: base chica para ceder ancho, pero PUEDE crecer (flex-grow 1) para no wrapear */
.zoom-field--sm { flex: 1 1 90px; min-width: 90px; max-width: 150px; }
```
**Trampa (04/07/2026):** un `sm` de ancho FIJO (`flex:0 0 auto; width:110px`) fuerza el wrap de
valores medianos como "CAPITAL FEDERAL" → por eso `sm` es flexible (`flex:1 1 90px`) y "Distrito
Inicio" NO es `sm`, es `ancho:2`. Objetivo del usuario: "que no queden 2 renglones nunca" — se
logra con anchos, no con nowrap (el wrap es el fallback para el caso raro de un dato larguísimo).

### Ancho del diálogo
`.zoom-viaje-dialog { max-width: 1180px; }` (subido de 920px, 04/07/2026) — cómodo en monitores
≥1366px; con el wrap, casi todo entra en 1 línea. Los índices de secuencia del `RenderTreeBuilder`
en los helpers `F`/`Fv`/`FDoc` deben quedar **monótonos** al agregar atributos (agregar el `title`
corre los índices siguientes) o el diff de Blazor rompe.

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
