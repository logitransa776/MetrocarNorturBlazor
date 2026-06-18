---
name: blazor-performance
description: >
  Optimización de rendimiento para aplicaciones Blazor con .NET 10, MudBlazor y ApexCharts.
  Usar este skill siempre que el usuario mencione: lentitud, re-renders innecesarios, tablas
  lentas, gráficos que tardan, memory leaks, lag en la UI, virtualización, paginación,
  carga de datos lenta, StateHasChanged excesivo, o cualquier problema de performance en
  Blazor. También activar cuando se trabaje con MudDataGrid, MudTable, Virtualize<T>,
  ApexCharts, o cuando se diseñen componentes Blazor que manejen grandes volúmenes de datos.
  Aplica tanto a Blazor WebAssembly como Blazor Server, con foco en .NET 10.
---

# Blazor Performance Optimization Skill
## Stack objetivo: Blazor (.NET 10) + MudBlazor + ApexCharts + ApiWeb10 REST

---

## 1. DIAGNÓSTICO RÁPIDO — Identificar el tipo de problema

Antes de sugerir soluciones, clasificar el problema:

| Síntoma | Causa probable | Sección a leer |
|---|---|---|
| UI "congela" al cargar datos | Bloqueo del hilo principal | §3 Async patterns |
| Tablas/listas lentas con muchos registros | Falta de virtualización | §4 Virtualización |
| Clicks/cambios generan lag visual | Re-renders en cascada | §2 Render pipeline |
| Gráficos ApexCharts lentos | Updates innecesarios | §6 ApexCharts |
| Memoria crece con el tiempo | Memory leaks / Dispose | §7 Memory leaks |
| Primera carga lenta (WASM) | Bundle size / AOT | §8 .NET 10 WASM |
| API calls redundantes | Sin caché / sin debounce | §5 HTTP patterns |

---

## 2. RENDER PIPELINE — Evitar re-renders innecesarios

### La regla de oro
`StateHasChanged()` dispara un re-render. Cada re-render recorre el árbol de componentes hijo. **Minimizar ambas cosas.**

### ShouldRender() — La primera línea de defensa
```csharp
// ❌ MAL: se re-renderiza siempre
protected override bool ShouldRender() => true; // default implícito

// ✅ BIEN: solo si cambió algo relevante
private string _lastRenderedValue = string.Empty;

protected override bool ShouldRender()
{
    if (_lastRenderedValue == CurrentValue) return false;
    _lastRenderedValue = CurrentValue;
    return true;
}
```

### EventCallback vs Action/Func
```csharp
// ❌ Action/Func: re-renderiza el componente PADRE completo
[Parameter] public Action<string> OnChange { get; set; }

// ✅ EventCallback: Blazor optimiza y solo re-renderiza donde es necesario
[Parameter] public EventCallback<string> OnChange { get; set; }
```

### Cascading Parameters — Úsalos con cuidado
```csharp
// ❌ MAL: si CascadingValue cambia, TODOS los hijos se re-renderizan
<CascadingValue Value="@userData">
    <MiArbolCompleto />
</CascadingValue>

// ✅ BIEN: IsFixed=true cuando el valor no cambia en el lifetime del componente
<CascadingValue Value="@userData" IsFixed="true">
    <MiArbolCompleto />
</CascadingValue>
```

### @key en listas
```razor
@* ❌ Sin @key: Blazor recrea todos los elementos al cambiar la lista *@
@foreach (var item in Items)
{
    <TarjetaItem Item="@item" />
}

@* ✅ Con @key: Blazor reutiliza el elemento DOM existente *@
@foreach (var item in Items)
{
    <TarjetaItem @key="item.IdItem" Item="@item" />
}
```

---

## 3. ASYNC PATTERNS — No bloquear el hilo de renderizado

### Carga de datos: patrón estándar
```csharp
// ✅ Patrón correcto para cargar datos en Blazor
private List<Vehiculo>? _vehiculos;
private bool _cargando = true;
private string? _error;

protected override async Task OnInitializedAsync()
{
    try
    {
        _vehiculos = await VehiculoService.GetAllAsync();
    }
    catch (Exception ex)
    {
        _error = ex.Message;
    }
    finally
    {
        _cargando = false;
        // NO necesitás StateHasChanged() aquí — OnInitializedAsync lo hace automático
    }
}
```

### Evitar el "double render" en OnAfterRenderAsync
```csharp
// ❌ MAL: se ejecuta en cada render
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    await CargarDatosDelGrafico(); // se llama en CADA render
}

// ✅ BIEN: solo en el primer render
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (!firstRender) return;
    await CargarDatosDelGrafico();
    StateHasChanged(); // aquí sí, porque fue disparado manualmente
}
```

### Debounce en búsquedas/filtros
```csharp
private System.Timers.Timer? _debounceTimer;
private string _filtro = string.Empty;

private void OnFiltroChanged(string value)
{
    _filtro = value;
    _debounceTimer?.Stop();
    _debounceTimer?.Dispose();
    _debounceTimer = new System.Timers.Timer(350);
    _debounceTimer.Elapsed += async (_, _) =>
    {
        _debounceTimer?.Dispose();
        await InvokeAsync(async () =>
        {
            await CargarDatos(_filtro);
            StateHasChanged();
        });
    };
    _debounceTimer.Start();
}
```

---

## 4. VIRTUALIZACIÓN — Tablas y listas con miles de registros

### Virtualize\<T\> nativo de Blazor
```razor
@* Solo renderiza las filas visibles en pantalla — esencial para listas grandes *@
<div style="height: 600px; overflow-y: auto;">
    <Virtualize Items="@_vehiculos" Context="v" OverscanCount="5">
        <FilaVehiculo Vehiculo="@v" />
    </Virtualize>
</div>
```

### ItemsProvider para datos del servidor (paginación virtual)
```razor
@* Carga páginas del servidor a medida que el usuario scrollea *@
<Virtualize Context="v" 
            ItemsProvider="@CargarVehiculosVirtuales"
            ItemSize="52">
    <FilaVehiculo Vehiculo="@v" />
</Virtualize>

@code {
    private async ValueTask<ItemsProviderResult<Vehiculo>> CargarVehiculosVirtuales(
        ItemsProviderRequest request)
    {
        var resultado = await VehiculoService.GetPagedAsync(
            skip: request.StartIndex,
            take: request.Count,
            cancellationToken: request.CancellationToken);
        
        return new ItemsProviderResult<Vehiculo>(resultado.Items, resultado.Total);
    }
}
```

### MudDataGrid — Configuración para rendimiento
```razor
<MudDataGrid T="Vehiculo"
             ServerData="@CargarVehiculosGrid"
             Virtualize="true"
             FixedHeader="true"
             Height="600px"
             Dense="true"
             RowsPerPage="50">
    <Columns>
        <PropertyColumn Property="x => x.Patente" Title="Patente" />
        <PropertyColumn Property="x => x.Marca" Title="Marca" />
    </Columns>
</MudDataGrid>

@code {
    private async Task<GridData<Vehiculo>> CargarVehiculosGrid(GridState<Vehiculo> state)
    {
        var resultado = await VehiculoService.GetPagedAsync(
            skip: state.Page * state.PageSize,
            take: state.PageSize,
            sortField: state.SortDefinitions.FirstOrDefault()?.SortBy,
            descending: state.SortDefinitions.FirstOrDefault()?.Descending ?? false);
        
        return new GridData<Vehiculo>
        {
            Items = resultado.Items,
            TotalItems = resultado.Total
        };
    }
}
```

---

## 5. HTTP PATTERNS — Evitar llamadas redundantes a ApiWeb10

### HttpClient con caché en memoria
```csharp
// Program.cs — registrar con scope
builder.Services.AddScoped<VehiculoCacheService>();

// VehiculoCacheService.cs
public class VehiculoCacheService
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    
    public VehiculoCacheService(HttpClient http, IMemoryCache cache)
    {
        _http = http;
        _cache = cache;
    }
    
    public async Task<List<Vehiculo>> GetAllAsync()
    {
        return await _cache.GetOrCreateAsync("vehiculos_all", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await _http.GetFromJsonAsync<List<Vehiculo>>("api/vehiculos") ?? [];
        }) ?? [];
    }
    
    public void InvalidarCache() => _cache.Remove("vehiculos_all");
}
```

### CancellationToken en todas las llamadas
```csharp
// Evitar llamadas "zombie" cuando el usuario navega a otra página
private CancellationTokenSource _cts = new();

protected override async Task OnInitializedAsync()
{
    try
    {
        _datos = await ApiService.GetAsync(_cts.Token);
    }
    catch (OperationCanceledException) { /* navegó, ok */ }
}

public void Dispose()
{
    _cts.Cancel();
    _cts.Dispose();
}
```

---

## 6. APEXCHARTS — Optimizar actualizaciones de gráficos

Para más detalles, ver: `references/apexcharts-blazor.md`

### Patrón básico de actualización eficiente
```csharp
// ❌ MAL: recrea el gráfico completo
private List<SeriesData> _series = new();
// cada vez que llegan datos nuevos, reasignar _series fuerza un destroy/recreate

// ✅ BIEN: usar UpdateSeriesAsync para actualizar datos sin recrear el componente
@code {
    private ApexChart<DataPoint>? _chart;
    
    private async Task ActualizarDatos(List<DataPoint> nuevosDatos)
    {
        if (_chart is not null)
            await _chart.UpdateSeriesAsync(animate: false);
    }
}
```

### Deshabilitar animaciones en datos en tiempo real
```razor
<ApexChart TItem="DataPoint" 
           @ref="_chart"
           Options="@_opciones">
    ...
</ApexChart>

@code {
    private ApexChartOptions<DataPoint> _opciones = new()
    {
        Chart = new Chart { Animations = new Animations { Enabled = false } },
        Tooltip = new Tooltip { Enabled = false } // tooltips caros en muchos puntos
    };
}
```

---

## 7. MEMORY LEAKS — Dispose correcto

### Checklist de Dispose en componentes Blazor
```csharp
@implements IDisposable
// O si usás async: @implements IAsyncDisposable

@code {
    // ✅ 1. Timer → siempre Dispose
    private System.Timers.Timer? _timer;
    
    // ✅ 2. CancellationTokenSource → siempre Cancel + Dispose
    private CancellationTokenSource _cts = new();
    
    // ✅ 3. Suscripciones a eventos → siempre desuscribir
    protected override void OnInitialized()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
        MiServicio.DatosActualizados += OnDatosActualizados;
    }
    
    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        NavigationManager.LocationChanged -= OnLocationChanged; // ← CRÍTICO
        MiServicio.DatosActualizados -= OnDatosActualizados;   // ← CRÍTICO
    }
}
```

---

## 8. .NET 10 WASM — Optimizaciones específicas

### AOT Compilation (para Blazor WebAssembly)
```xml
<!-- MetroCar.Client.csproj -->
<PropertyGroup>
    <RunAOTCompilation>true</RunAOTCompilation>   <!-- Build más lento, runtime más rápido -->
    <PublishTrimmed>true</PublishTrimmed>           <!-- Reduce bundle size -->
    <TrimmerRootDescriptor>trimmer-roots.xml</TrimmerRootDescriptor>
</PropertyGroup>
```

### Lazy loading de assemblies por ruta
```csharp
// Program.cs
builder.Services.AddLazyAssemblyLoader();

// Router.razor
<Router AppAssembly="@typeof(App).Assembly"
        AdditionalAssemblies="@_assembliesAddicionales"
        OnNavigateAsync="@OnNavigateAsync">
    ...
</Router>

@code {
    private List<Assembly> _assembliesAddicionales = [];
    
    private async Task OnNavigateAsync(NavigationContext ctx)
    {
        if (ctx.Path.StartsWith("reportes"))
        {
            var assemblies = await AssemblyLoader.LoadAssembliesAsync(
                ["MetroCar.Reportes.dll"]);
            _assembliesAddicionales = assemblies.ToList();
        }
    }
}
```

---

## 9. REGLAS GENERALES — Lista de verificación

Antes de entregar cualquier componente Blazor del proyecto MetroCar, verificar:

- [ ] ¿El componente implementa `IDisposable`/`IAsyncDisposable` si suscribe eventos o usa timers?
- [ ] ¿Las listas con más de 50 items usan `<Virtualize>` o paginación server-side?
- [ ] ¿Los event handlers usan `EventCallback` en lugar de `Action`/`Func`?
- [ ] ¿Las listas iteradas con `@foreach` tienen `@key="item.Id"`?
- [ ] ¿Las llamadas async incluyen `CancellationToken`?
- [ ] ¿Las búsquedas/filtros tienen debounce (mínimo 300ms)?
- [ ] ¿`OnAfterRenderAsync` tiene el guard `if (!firstRender) return`?
- [ ] ¿Los datos que no cambian usan `CascadingValue IsFixed="true"`?
- [ ] ¿Las llamadas repetidas a la misma API usan caché?
- [ ] ¿Los gráficos ApexCharts usan `UpdateSeriesAsync` en lugar de rebinding?

---

## Referencias adicionales

- `references/apexcharts-blazor.md` — Configuración avanzada de ApexCharts con Blazor
- `references/mudblazor-performance.md` — Patrones avanzados de MudDataGrid y MudTable
- `references/apihttp-patterns.md` — Patrones HTTP para consumir ApiWeb10

Estas referencias se leen solo cuando el problema específico lo requiere.
