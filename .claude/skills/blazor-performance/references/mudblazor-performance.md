# MudBlazor Performance — MudDataGrid y MudTable

## Cuándo usar cada uno

| Componente | Usar cuando |
|---|---|
| `MudDataGrid<T>` | Tablas con ordenamiento, filtros, paginación server-side, edición inline |
| `MudTable<T>` | Tablas simples con datos en memoria (<500 rows), sin server-side |
| `<Virtualize>` + HTML | Control total sobre performance, sin overhead de MudBlazor |

---

## MudDataGrid con ServerData (patrón recomendado para MetroCar)

```razor
<MudDataGrid T="VehiculoDto"
             @ref="_grid"
             ServerData="@CargarVehiculos"
             Virtualize="false"        @* ServerData maneja la paginación *@
             FixedHeader="true"
             Height="calc(100vh - 200px)"
             Dense="true"
             Hover="true"
             RowsPerPage="@_pageSize"
             MultiSelection="false">
    
    <ToolBarContent>
        <MudText Typo="Typo.h6">Vehículos</MudText>
        <MudSpacer />
        <MudTextField @bind-Value="_busqueda"
                      Placeholder="Buscar..."
                      Adornment="Adornment.Start"
                      AdornmentIcon="@Icons.Material.Filled.Search"
                      Immediate="true"
                      DebounceInterval="300"    @* ← Debounce incorporado *@
                      OnDebounceIntervalElapsed="OnBusquedaChanged" />
    </ToolBarContent>
    
    <Columns>
        @* Usar PropertyColumn en lugar de TemplateColumn cuando sea posible *@
        <PropertyColumn Property="x => x.Patente" Title="Patente" Sortable="true" />
        <PropertyColumn Property="x => x.Marca" Title="Marca" Sortable="true" />
        <PropertyColumn Property="x => x.Modelo" Title="Modelo" Sortable="true" />
        <PropertyColumn Property="x => x.Estado" Title="Estado" Sortable="false" />
        
        @* TemplateColumn solo cuando necesitás HTML personalizado *@
        <TemplateColumn Title="Acciones" Sortable="false" Filterable="false">
            <CellTemplate>
                <MudIconButton Icon="@Icons.Material.Filled.Edit"
                               Size="Size.Small"
                               OnClick="@(() => EditarVehiculo(context.Item))" />
            </CellTemplate>
        </TemplateColumn>
    </Columns>
    
    <PagerContent>
        <MudDataGridPager T="VehiculoDto" PageSizeOptions="@(new int[]{25, 50, 100})" />
    </PagerContent>
    
</MudDataGrid>

@code {
    private MudDataGrid<VehiculoDto>? _grid;
    private string _busqueda = string.Empty;
    private int _pageSize = 50;
    
    private async Task<GridData<VehiculoDto>> CargarVehiculos(GridState<VehiculoDto> state)
    {
        // Mapear el estado de MudDataGrid a parámetros de la API
        var sortField = state.SortDefinitions.FirstOrDefault()?.SortBy ?? "Patente";
        var descending = state.SortDefinitions.FirstOrDefault()?.Descending ?? false;
        
        var response = await VehiculoService.GetPagedAsync(new VehiculoQuery
        {
            Busqueda = _busqueda,
            Skip = state.Page * state.PageSize,
            Take = state.PageSize,
            SortBy = sortField,
            Descending = descending
        });
        
        return new GridData<VehiculoDto>
        {
            Items = response.Items,
            TotalItems = response.Total
        };
    }
    
    private async Task OnBusquedaChanged()
    {
        // Volver a la primera página al buscar
        if (_grid is not null)
        {
            await _grid.NavigateTo(Page.First);
            await _grid.ReloadServerData();
        }
    }
}
```

---

## Evitar re-renders en celdas con TemplateColumn

```razor
@* ❌ MAL: lambda captura el item en cada render *@
<TemplateColumn Title="Estado">
    <CellTemplate>
        <MudChip Color="@GetColor(context.Item.Estado)">
            @context.Item.Estado
        </MudChip>
    </CellTemplate>
</TemplateColumn>

@* ✅ BIEN: componente separado para la celda *@
<TemplateColumn Title="Estado">
    <CellTemplate>
        <ChipEstado Estado="@context.Item.Estado" />
    </CellTemplate>
</TemplateColumn>

@* ChipEstado.razor — componente simple con ShouldRender *@
@code {
    [Parameter] public string Estado { get; set; } = string.Empty;
    private string _lastEstado = string.Empty;
    
    protected override bool ShouldRender()
    {
        if (_lastEstado == Estado) return false;
        _lastEstado = Estado;
        return true;
    }
}
```

---

## MudTable en memoria — solo para datasets pequeños

```razor
@* Para <200 registros cargados completamente en memoria *@
<MudTable Items="@_vehiculosEnMemoria"
          Dense="true"
          Hover="true"
          Filter="@FiltrarVehiculo"
          @bind-SelectedItem="_vehiculoSeleccionado">
    <HeaderContent>
        <MudTh><MudTableSortLabel SortBy="new Func<Vehiculo, object>(v => v.Patente)">Patente</MudTableSortLabel></MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd DataLabel="Patente">@context.Patente</MudTd>
    </RowTemplate>
</MudTable>

@code {
    private Func<Vehiculo, bool> FiltrarVehiculo => v =>
        string.IsNullOrWhiteSpace(_busqueda) ||
        v.Patente.Contains(_busqueda, StringComparison.OrdinalIgnoreCase);
}
```

---

## Tip: Loading skeleton mientras carga ServerData

```razor
<MudDataGrid T="VehiculoDto" ServerData="@CargarVehiculos" ...>
    <LoadingContent>
        @* Skeleton que imita la estructura de las filas *@
        @for (int i = 0; i < 10; i++)
        {
            <tr>
                <td><MudSkeleton Width="120px" /></td>
                <td><MudSkeleton Width="150px" /></td>
                <td><MudSkeleton Width="100px" /></td>
            </tr>
        }
    </LoadingContent>
</MudDataGrid>
```
