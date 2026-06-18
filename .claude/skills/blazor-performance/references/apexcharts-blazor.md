# ApexCharts + Blazor — Referencia de Performance

## Biblioteca recomendada
`Blazor-ApexCharts` (apexcharts/Blazor-ApexCharts en NuGet)

---

## Problema más común: recrear el gráfico en cada update

```razor
@* ❌ INCORRECTO: esto destruye y recrea el canvas en cada re-render *@
<ApexChart TItem="DataPoint" Series="@_series" />

@code {
    // Si _series es un campo que se reasigna, el gráfico se destruye y recrea
    private List<Series<DataPoint>> _series = new();
    
    private void ActualizarDatos()
    {
        _series = BuildNuevosSeries(); // ← ESTO destruye el gráfico
    }
}
```

## Solución: referencia + UpdateSeriesAsync

```razor
@* ✅ CORRECTO: mantener referencia y usar métodos de actualización *@
<ApexChart TItem="DataPoint" @ref="_chart" Options="@_opciones">
    <ApexPointSeries TItem="DataPoint"
                     Items="@_datos"
                     Name="Vehículos en circulación"
                     SeriesType="SeriesType.Line"
                     XValue="@(d => d.Fecha)"
                     YValue="@(d => d.Cantidad)" />
</ApexChart>

@code {
    private ApexChart<DataPoint>? _chart;
    private List<DataPoint> _datos = [];
    
    private async Task ActualizarDesdeApi()
    {
        var nuevos = await ApiService.GetDatosGraficoAsync();
        _datos.Clear();
        _datos.AddRange(nuevos);
        
        if (_chart is not null)
            await _chart.UpdateSeriesAsync(animate: false);
        // No se llama StateHasChanged() — UpdateSeriesAsync lo maneja
    }
}
```

---

## Configuración de opciones para performance

```csharp
private ApexChartOptions<DataPoint> BuildOpciones() => new()
{
    Chart = new Chart
    {
        Animations = new Animations
        {
            Enabled = false,        // Deshabilitar en datos en tiempo real
            DynamicAnimation = new DynamicAnimation { Enabled = false }
        },
        Toolbar = new Toolbar { Show = false }, // Si no se necesita toolbar
        Zoom = new Zoom { Enabled = false },    // Deshabilitar si no se usa
        Background = "transparent",
        RedrawOnParentResize = false,           // Evita redraws al resize del padre
        RedrawOnWindowResize = false
    },
    Tooltip = new Tooltip
    {
        Enabled = true,
        // Shared tooltip más eficiente que individual en series múltiples
        Shared = true,
        Intersect = false
    },
    DataLabels = new DataLabels { Enabled = false }, // Muy costoso con muchos puntos
    Stroke = new Stroke { Curve = Curve.Smooth, Width = [2] },
    // Limitar puntos visibles en series largas
    // ApexCharts automáticamente samplea si hay >500 puntos
};
```

---

## Actualización periódica (polling)

```csharp
// Patrón para actualización cada N segundos sin memory leaks
private PeriodicTimer? _timer;
private CancellationTokenSource _cts = new();

protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (!firstRender) return;
    
    _timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
    
    // Loop en background — NO bloquea el render
    _ = Task.Run(async () =>
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                var datos = await ApiService.GetDatosGraficoAsync(_cts.Token);
                
                await InvokeAsync(async () =>
                {
                    _datos.Clear();
                    _datos.AddRange(datos);
                    if (_chart is not null)
                        await _chart.UpdateSeriesAsync(animate: false);
                });
            }
        }
        catch (OperationCanceledException) { /* Dispose, ok */ }
    });
}

public void Dispose()
{
    _cts.Cancel();
    _cts.Dispose();
    _timer?.Dispose();
}
```

---

## Múltiples gráficos en la misma página

Cuando hay 3+ gráficos en una página, el costo de renderizado se multiplica:

```razor
@* Lazy load de gráficos usando IntersectionObserver pattern *@
<div @ref="_chartContainer">
    @if (_esVisible)
    {
        <GraficoVehiculos />
    }
    else
    {
        <div style="height: 300px;" class="d-flex align-center justify-center">
            <MudProgressCircular />
        </div>
    }
</div>

@code {
    private ElementReference _chartContainer;
    private bool _esVisible = false;
    private DotNetObjectReference<MiPagina>? _selfRef;
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _selfRef = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("observarVisibilidad", _chartContainer, _selfRef);
    }
    
    [JSInvokable]
    public void OnVisible()
    {
        _esVisible = true;
        StateHasChanged();
    }
    
    public ValueTask DisposeAsync()
    {
        _selfRef?.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

```javascript
// wwwroot/js/visibility.js
window.observarVisibilidad = (element, dotNetRef) => {
    const observer = new IntersectionObserver(entries => {
        if (entries[0].isIntersecting) {
            dotNetRef.invokeMethodAsync('OnVisible');
            observer.disconnect();
        }
    }, { threshold: 0.1 });
    observer.observe(element);
};
```
