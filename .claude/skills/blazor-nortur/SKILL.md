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

> ⚠ Esa paleta es la de la **marca**. El color que identifica a cada **módulo** es otro y
> vive en otro lado — ver la sección siguiente. No mezclar: `NorturColors.Naranja` es el
> acento del sistema, el naranja de Tráfico es el código de color de un módulo.

**Fondo de página (`PaletteLight.Background`):** `#D5D9E2` — gris "Piedra", elegido por el
usuario el 16/08/2026 entre 4 variantes de gris (mockup comparativo en un Artifact) para
reemplazar un celeste (`#C8D4E3`) que competía en saturación con los campos blancos. Es un
gris NEUTRO con un dejo frío hacia `NorturColors.Azul` a propósito (un gris puro se sentiría
fuera de lugar) — no reintroducir un tono celeste/pastel acá. Se fija en dos lugares que
tienen que ir sincronizados: `NorturTheme.cs` (`PaletteLight.Background`, pinta
`.mud-main-content` → todas las pantallas) y `html, body` en `app.css` (fallback fuera del
layout).

### Campos de formulario — estándar de TODA la app (16/08/2026)

Vive en el bloque **"CAMPOS DE FORMULARIO"** arriba de todo en `app.css`, con las 3 variables
en `:root` para tocar una sola perilla. **No es por pantalla: aplica a todo input outlined.**

| Rol | Variable | Valor | Regla |
| --- | --- | --- | --- |
| Campo editable | `--nt-campo` | `#EDEFF4` | **aclara** sobre la página, pero NUNCA blanco puro — sobre gris el blanco salta demasiado (probado y rechazado por el usuario) |
| Campo bloqueado | `--nt-campo-ro` | `#C6CBD8` | **oscurece**: se apaga en vez de destacar. Dice "no tocar" sin cartel |
| Borde | `--nt-campo-borde` | `#112F5B` | azul nocturno de la marca. Con poca diferencia campo/página, el borde es lo que hace que se lea como caja |
| Ancho del borde | `--nt-campo-borde-ancho` | `2px` | el default de MudBlazor es 1px. El **foco va a +1px** (3px) para que se distinga del reposo por ancho Y por color |

**Contraseñas: nunca se muestran.** Los campos de password de Parámetros (SMTP y GPS) van con
`InputType="InputType.Password"` fijo, **sin el ojo de "mostrar"**: se pueden reescribir pero no
leer. Son credenciales de la empresa y la pantalla se abre con gente alrededor (además la base
las guarda en texto plano). ⚠ `UsuarioEditorDialog.razor` **todavía tiene el ojo** — es decisión
abierta, porque ahí el admin quizá necesite leer la clave para dictársela al usuario.

Esa regla es la de **menor especificidad** de la app a propósito: es la base, y los campos con
estilo propio (`.busc-wrap` de Tráfico, `.rfs-topn-select`) la pisan sin pelear. **No ponerle
`!important`** — rompería esos.

### 🔴 Campos sobre tarjeta BLANCA: la regla se INVIERTE (19/08/2026)

La tabla de arriba está calibrada contra el **fondo gris "Piedra" `#D5D9E2` de la página**: ahí
`--nt-campo` (`#EDEFF4`) **aclara** y por eso el campo se lee. Pero muchos controles no viven
sobre la página: viven **adentro de una `MudPaper` blanca** (barras de herramientas de grillas,
cabeceras de panel, filtros dentro de una tarjeta). Sobre blanco, `#EDEFF4` queda a un paso del
blanco → el campo pierde el borde de lectura y la barra entera se ve "lavada". El usuario lo
reportó exactamente así: *"el fondo en blanco no se ve bien"*.

**Sobre blanco hay que invertir el gesto: teñir el campo y definir el borde.**

| Rol | Sobre la página (gris) | Sobre tarjeta blanca |
| --- | --- | --- |
| Fondo del campo | `#EDEFF4` (aclara) | **`#F2F6FC`** (tiñe apenas) |
| Borde en reposo | `#112F5B` 2px | **`#C3D3E8` 1.5px** (el azul noche a 2px grita sobre blanco) |
| Foco | +1px | **`#F99410` 2px + halo** `0 0 0 3px rgba(249,148,16,.16)` y fondo a blanco puro |

Referencia viva: `.vpc-toolbar` en `app.css` (barra de la grilla de `ViajesPorChofer.razor`).
Ojo con la **especificidad al encadenar estados**: la regla de foco tiene que quedar con MÁS
peso que la de reposo y que la de "filtro activo", o el campo enfocado se queda con el borde
que no va. Y **no se valida con una captura**: hay que leer
`getComputedStyle(...).borderTopColor` en reposo / foco / activo (ver skill `testing-nortur`).

**🔴 Las 3 trampas de MudBlazor 9.5 acá (verificadas midiendo el DOM y el CSSOM, no de memoria):**

1. **`.mud-focused` y `.mud-error` NO EXISTEN.** MudBlazor marca el foco con `:focus-within` y
   el error con `.mud-input-error`. Excluir clases inexistentes no excluye nada: sólo infla la
   especificidad, y entonces tu regla **le gana a la del foco** y el campo enfocado se queda con
   el borde de reposo. Es un bug de accesibilidad **invisible en una captura** (azul nocturno y
   azul de foco se parecen a simple vista) — sólo aparece midiendo el color computado. El
   selector correcto es `:not(:focus-within):not(.mud-disabled)`. El error se defiende solo
   (lleva `!important`).
2. **Un `MudSelect` y un `MudDatePicker` ponen `readonly` en su input POR DISEÑO** (se cargan
   eligiendo, no tipeando). Cualquier regla de "campo bloqueado" basada en `:has(input[readonly])`
   **tiñe todos los desplegables y fechas de la app** si no se los excluye explícitamente.
3. **El hover lo maneja MudBlazor** con `--mud-palette-action-default` (`rgba(0,0,0,.537)`) y no
   se le puede ganar sin `!important` — que rompería el borde naranja de `.rfs-topn-select`. Se
   dejó como está: el hover es transitorio y termina en el foco, que sí se distingue.

Para verificar un cambio de estilo de campos **no alcanza una captura**: hay que leer
`getComputedStyle(...).borderTopColor` en los 3 estados (reposo / hover / foco). Ver el
protocolo en la skill `testing-nortur`.

---

## CÓDIGO DE COLOR POR MÓDULO (11/08/2026) — regla vigente

Cada módulo del sistema tiene **un color y es siempre el mismo**, en los tres lugares donde
el usuario lo ve: las tarjetas del hub `/informes`, el menú lateral y el filete de la barra
de título. El objetivo es que el cliente final reconozca el módulo por el color **antes** de
leer — con 7 módulos y ~105 ítems de menú, el texto solo no alcanza.

| Módulo | Hex | Letra `acceso` | Nombre en el drawer |
|---|---|---|---|
| Reservas | `#2563EB` | R | Reservas |
| Tráfico | `#F99410` | T | Tráfico |
| Flota | `#16A34A` | V | Vehículos y Choferes |
| Facturación | `#0891B2` | F | Facturación |
| Combustible | `#C2410C` | M | Combustible |
| Sistema | `#7C3AED` | S | Sistema |
| *(sin módulo)* | `#64748B` | — | Informes, el hub, la ayuda |

**Fuente única: `InformesCatalogo.Modulos`** (`Services/InformesCatalogo.cs`). El hex se escribe
**una sola vez ahí**. Cambiarlo repinta el hub, el menú y la barra a la vez. Nunca hardcodear
un color de módulo en un `.razor` ni en `app.css`.

### Cómo funciona (el mecanismo, en una línea)

El color entra como la **variable CSS `--nt-mod`** en el `<div class="nav-section">` (o en el
`Style` del AppBar) y **todo lo demás lo hereda**: ícono, filete, hover y el ítem activo.
No hay clases por módulo. Un módulo nuevo no necesita CSS nuevo — le alcanza con su hex.

```razor
@* Sección del drawer — el color entra UNA vez, acá *@
<div class="nav-section" style="--nt-mod:@InformesCatalogo.Color(ModuloInforme.Trafico)">
```

```css
/* app.css § CÓDIGO DE COLOR POR MÓDULO — todo deriva de la variable */
.nav-section__icon              { color: var(--nt-mod, #003AA0); }
.nav-section__title:not(.nav-section__title--sub)::before { background: var(--nt-mod, transparent); }
.nav-item--active               { border-left-color: var(--nt-mod, #F99410); }
```

**El fallback de cada `var()` es el color anterior al cambio**: si una sección se olvida de
declarar `--nt-mod`, el menú se ve como antes, no roto.

### Reglas al tocar esto

1. **Sección nueva en el drawer → declarar `--nt-mod`.** Sin eso, la sección queda gris/azul
   y rompe el código de color que el usuario ya aprendió.
2. **El ítem activo NO es naranja fijo.** Toma el color del módulo (antes era `#F99410` en
   todas las secciones: dentro de Facturación aparecía un naranja que no significaba nada).
3. **`color-mix()` siempre con fallback.** Se declara primero el color plano y después el
   `color-mix` — Chrome < 111 ignora la segunda línea y no queda un fondo transparente.
4. **El filete de la barra va como `::after`, NUNCA `border-bottom`.** Un borde le suma 3px
   a la altura del AppBar y MudBlazor calcula con esa altura el `padding-top` del contenido:
   todas las pantallas quedarían corridas. El pseudo-elemento se pinta adentro de la caja.
5. **Lo que no es módulo va neutro** (`InformesCatalogo.ColorNeutro`): la sección Informes,
   el hub y las pantallas sueltas. No inventarles un color de módulo: le enseñaría al
   usuario algo falso.
6. **Pantalla operativa nueva → agregarla a `_pantallasPorRuta`** en `MainLayout.razor` con
   su título **y su módulo**. Los informes NO se repiten ahí: los resuelve
   `InformesCatalogo.ModuloDeRuta()`, que ya sabe título y módulo de cada uno.

### Dos hex con historia (no revertir sin leer esto)

- **Reservas era `#003AA0`** = el azul institucional exacto del header, el logo y el footer.
  En el menú no se leía como "módulo Reservas" sino como "el color del sistema".
- **Combustible era `#B45309`**, demasiado cerca del `#F99410` de Tráfico de reojo y en
  monitores viejos. Corrido a terracota para que los dos naranjas no se confundan.

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

@* En nav-drawer__nav — OJO: --nt-mod es obligatorio (§ CÓDIGO DE COLOR POR MÓDULO) *@
<div class="nav-section" style="--nt-mod:@InformesCatalogo.Color(ModuloInforme.MiModulo)">
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
   ⚠ Limitación conocida (18/07/2026, SIN resolver): `<Virtualize>` en Blazor Server deja la
   grilla EN BLANCO un instante con scroll rápido (cada scroll fuera del overscan = round-trip
   SignalR). Se probó el camino alternativo (render completo + `content-visibility` + fila como
   componente con `ShouldRender` gateado): elimina el blanco, pero hacía sentir lentos el Zoom
   del Viaje y el menú contextual → **se revirtió; `<Virtualize>` sigue vigente**. NO reintentar
   sin leer `docs/performance/PENDIENTE_GRILLA_TRAFICO_BLANQUEO.md` (causa, mediciones A/B e
   hipótesis nuevas — la más prometedora: aislar el subárbol de la grilla para que abrir el
   Zoom/menú no dispare el render de las filas).
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
11. **Comparación mes-a-mes / tendencia (OPCIONAL, para pivotes con eje temporal en columnas).**
    Cuando la tabla pivote es entidad × mes (ReservasPorCliente), agregar el control de tendencia
    (validado por el usuario 15/07/2026). Piezas — ver § Comparación mes-a-mes abajo:
    - Selector **"Comparar"** (vs mes anterior `M1` / vs N meses atrás `N` + combo Salto / vs año
      pasado `YoY`). Todo **en memoria** sobre `_pivMap` — NO re-query. El interanual solo tiene
      números si el rango cargado abarca también el año anterior (si no, la celda muestra `·`/`—`;
      es el usuario quien amplía el "Desde", decisión acordada).
    - Switch **"Variación"**: togglea toda la grilla entre cantidad (default) y Δ por celda vs su
      mes base (verde sube / rojo baja). El footer y la caption también cambian.
    - Columna **"Tendencia"** SIEMPRE visible (sticky-right junto a TOTAL): Δ del último mes vs su
      base (▲/▼ + valor + `│ %`) + sparkline SVG inline (sin librerías). Ordena "los que suben"
      arriba. `nth-last-child(2)` sticky para pinnear TOTAL + Tendencia juntas a la derecha.

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

### Comparación mes-a-mes / tendencia — patrón (ReservasPorCliente.razor, 15/07/2026)

Para pivotes **entidad × mes** (columnas = meses), agregar un control que "cante" si cada
entidad sube o baja respecto de un mes de referencia. Pedido y validado por el usuario. **Todo
en memoria** sobre el `_pivMap` ya calculado — **cero re-query, cero cambios en el service**.

**Tres piezas** (todas en el mismo `.razor`):

1. **Selector "Comparar"** — contra qué mes se compara cada mes:
   - `M1` = mes inmediatamente anterior en la grilla (índice −1 en `_pivMeses`).
   - `N` = N meses calendario atrás (combo "Salto" 2/3/4/6); se busca ese mes en `_pivMeses`.
   - `YoY` = mismo mes del año anterior. **Solo tiene números si el rango cargado abarca también
     el año previo** — si no, la celda muestra `·`/`—`. Decisión de negocio (acordada): es el
     usuario quien amplía el "Desde" para el interanual, no se auto-trae el año anterior.
2. **Switch "Variación"** — togglea toda la grilla entre **cantidad** (default) y **Δ por celda**
   vs su mes base (`+19` verde / `−53` rojo, guión largo real `−` para negativos). El `<tfoot>`
   de totales y la caption también cambian. La primera columna (sin mes base) queda en `·`.
3. **Columna "Tendencia"** SIEMPRE visible (en los dos modos): Δ del **último mes** vs su base
   (▲/▼ + valor + `│ %`) + **sparkline SVG inline** de la serie mensual. Ordenar por ella
   pone "los que más suben/caen" arriba.

**Helpers clave** (el corazón es `MesBaseDe(mes)` → devuelve el mes contra el que comparar, o
`null`):
```csharp
private static string SumarMeses(string mes, int delta) {   // "2026-03" + (-2) → "2026-01"
    var y = int.Parse(mes[..4]); var m = int.Parse(mes[5..7]);
    var idx = y * 12 + (m - 1) + delta;
    return $"{idx / 12:0000}-{idx % 12 + 1:00}";
}
private string? MesBaseDe(string mes) => _comparar switch {
    "M1"  => _pivMeses.IndexOf(mes) is int i && i > 0 ? _pivMeses[i - 1] : null,
    "N"   => _pivMeses.Contains(SumarMeses(mes, -_saltoN)) ? SumarMeses(mes, -_saltoN) : null,
    _     => _pivMeses.Contains(SumarMeses(mes, -12)) ? SumarMeses(mes, -12) : null,   // YoY
};
private int? PivVar(string cli, string mes) =>            // Δ de una celda, null si no hay base
    MesBaseDe(mes) is string b ? PivVal(cli, mes) - PivVal(cli, b) : null;
```
El sparkline es un `MarkupString` con un `<svg><polyline>` normalizado (min–max de la serie),
color por el último tramo (verde/rojo/gris). Sin librerías.

**CSS — pinnear TOTAL + Tendencia juntas a la derecha.** Antes solo `td:last-child` era
sticky-right (era TOTAL). Al agregar Tendencia como nueva última columna, TOTAL pasa a
`nth-last-child(2)`: darle a Tendencia un ancho fijo (`width:132px`) y a la anteúltima
`position:sticky; right:132px`. Duplicar en las reglas de fondo/z-index de thead/tbody/tfoot/hover
el `:last-child` con `, ... :nth-last-child(2)`.

**Trampa de datos (no es bug):** si la réplica está congelada a mitad de mes, el último mes sale
casi vacío → `−100%` masivo y sparklines todos en picada. Es artefacto de la réplica (igual que
Control de cargas); en producción con datos frescos se distribuye normal. No hackear alrededor.

**Legibilidad:** el `%` de la tendencia va separado del Δ con `border-left:1px solid currentColor`
+ padding (si no, `−35−58%` se lee pegado).

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

### Datalabels en gráficos de barras — número + % afuera (13/07/2026)

Patrón validado en `ReservasFechaServicio.razor` para mostrar sobre cada barra el valor y su
% del total (formato **"171 (19%)"**), afuera al final, sin que se corte. **Patrón disponible**
(no obligatorio) — usarlo cuando un informe quiera datalabels legibles en barras/donut.

**1. Los datalabels NO se activan con `DataLabels.Enabled=true` global.** Con `<ApexPointSeries>`,
el `Enabled` global se pierde en la serialización → hay que poner **`ShowDataLabels="true"` en la
serie**. Vale para barras Y donut.

**2. Número AFUERA al final de la barra horizontal** (no centrado adentro, que es el default):
- `PlotOptions.Bar.DataLabels.Position = BarDataLabelPosition.Top` (en horizontal, Top = extremo).
- En el `DataLabels` global: `TextAnchor.Start`, color oscuro `#334155`, y un `OffsetX`:
  - **Datos parejos** (ranking gradual, ej. Reservas por servicio): `OffsetX = 8` alcanza.
  - **Datos MUY dispares** (una barra domina, otra ~0 — ej. tipo de vehículo BUS 74% vs HIACE 3%):
    las barras cortas se renderizan de pocos px y ApexCharts descoloca el label hacia el eje
    (queda ANTES del fin de barra, pisándolo). Hay un **umbral interno**: con `OffsetX ≤ 20` el
    label de la barra más corta queda pegado/adentro; con **`≥ 21` salta afuera**. Usar
    **`OffsetX = 22`** (mínimo con aire) → todos afuera, lo más cerca posible. Verificado midiendo
    `label.x − barra.right` (gap) por barra: con 8 daba gap −32/−12/−1 en las cortas; con 22, +22 parejo.

**3. Que el label NO se corte contra el borde derecho.** Sin `Max` de eje, ApexCharts pega la
barra más larga al ~100% del ancho → el label no entra. Fijar el eje X en `Recalcular` (cambia
con métrica/filtro/topN):
- `Xaxis.Min = 0` (sin esto, al fijar solo Max el eje muestra ticks negativos feos).
- `Xaxis.Max = maxValor × factor`: **+25% panel ancho (7/12)**, **+40% panel angosto (5/12)** — el
  panel angosto necesita más margen porque el label ocupa proporcionalmente más ancho.

**4. El "(%)" en barras se inyecta con el total** (el datalabel de barra recibe solo el valor, no
el total): `Formatter` con placeholder `__TOTAL__` reemplazado en cada `Recalcular` (método
`AplicarTotalEnDataLabels`). Formato acordado **"cantidad (%)"** (ej: `171 (19%)`). En **donut** el
% sí viene solo: `Formatter = "function(val,opts){ var v = opts.w.config.series[opts.seriesIndex];
return v + ' (' + Math.round(val) + '%)'; }"`.

**5. NO se puede colorear solo el % dentro del label** (verificado con POC, 13/07/2026): ApexCharts
**escapa el HTML del formatter** → devolver `171 <tspan style="fill:#F99410">…</tspan>` sale como
texto literal `171 &lt;tspan…&gt;`, no como markup. `Style.Colors` pinta el label COMPLETO, no una
parte. Post-procesar el SVG por DOM es frágil (se rompe en cada re-render/foco). Por eso la
jerarquía cantidad↔% se logra con el **paréntesis** (`171 (19%)`), no con color parcial.

**6. El donut/pie NO puede poner labels afuera del anillo** (ApexCharts no tiene leader lines
estilo Power BI — verificado con POC 13/07/2026, `offset` alto no las genera). Dos salidas:
- **Barras horizontales** con "cantidad (%)" afuera (como "Distribución por banda horaria").
- **Torta llena + números en la LEYENDA** (ver punto 8) — mejor cuando el usuario quiere la torta.

**7. 🔴 CROSS-FILTER + datalabels = remontar con `@key`, NO update en el lugar** (bug real,
13/07/2026). Si un gráfico con datalabels tiene cross-filter (clic para enfocar), el
`UpdateOptionsAsync`+`UpdateSeriesAsync` (update en el lugar) **reposiciona MAL los datalabels**:
quedan con la `y` del render anterior, **fuera de la caja del gráfico** (medí y≈160, arriba de la
página) → los números "desaparecen" tras el primer clic. Fix: darle a ESE chart un `@key` propio
(un `Guid` que se regenera en cada toggle de foco) para **remontarlo** en vez de actualizarlo en el
lugar; sacarlo de `ActualizarGraficos`. El remonte redibuja los datalabels en su posición correcta.
Verificado con secuencia foco A→B→quitar: sin `@key` los labels se despositionan; con `@key` aguantan.

**8. Torta ("distribución") con números en la LEYENDA — patrón ganador para pie/donut** (13/07/2026,
validado por el usuario en "Distribución por tipo de vehículo"). En vez de pelear con etiquetas sobre
las porciones, dejar la torta LIMPIA (`DataLabels.Enabled=false`) y poner cantidad + % al lado de
cada ícono con `Legend.Formatter` (ej: "BUS: 162 (74%)"). El formatter recibe `(name, opts)`:
`opts.w.globals.series[opts.seriesIndex]` = valor; el total se inyecta en `Recalcular` (placeholder
`__TOTALVEH__`). Es nativo, robusto, sin leader lines ni labels encimados. Igual que las barras,
si tiene cross-filter necesita `@key` (punto 7) para que la leyenda recalcule bien al enfocar.

```csharp
// Torta con números en la leyenda (patrón 8):
Legend = new() { Show = true, Position = LegendPosition.Bottom,
    Formatter = "function(name, opts){ var v = opts.w.globals.series[opts.seriesIndex]; var p = __TOTAL__>0?Math.round(v*100/__TOTAL__):0; return name+': '+v.toLocaleString('es-AR')+' ('+p+'%)'; }" },
DataLabels = new() { Enabled = false },   // torta limpia; los números van en la leyenda
// SeriesType.Pie (o Donut) en el markup, con @key propio si hay cross-filter.
```

```razor
<ApexPointSeries TItem="BarItem" Items="_barItems" Name="@_metrica"
                 SeriesType="SeriesType.Bar" XValue="@(x=>x.Servicio)" YValue="@(x=>x.Valor)"
                 ShowDataLabels="true" />   @* ← activa datalabels; el global NO alcanza *@
```
```csharp
PlotOptions = new() { Bar = new() {
    Horizontal = true, Distributed = true,
    DataLabels = new() { Position = BarDataLabelPosition.Top } } },
DataLabels = new() {
    Enabled = true, TextAnchor = TextAnchor.Start, OffsetX = 8,  // 22 si los datos son muy dispares (punto 2)
    Style = new() { Colors = new List<string> { "#334155" } },
    Formatter = "function(val){ var p = __TOTAL__>0?Math.round(val*100/__TOTAL__):0; return val.toLocaleString('es-AR')+' ('+p+'%)'; }" },
// en Recalcular: reemplazar __TOTAL__ por el total y fijar Xaxis.Min=0 / Xaxis.Max=max×factor.
// Si el gráfico tiene cross-filter (clic para enfocar), remontarlo con @key propio (punto 7),
// NO actualizarlo con UpdateOptions/UpdateSeries (despositiona los datalabels).
```

**Verificación (Playwright):** medir `label.right ≤ grid.right` para confirmar que no se corta
(probar en la métrica de números más largos, ej. Pax); leer `.apexcharts-datalabels text` para
confirmar el contenido. Diagnóstico en runtime: `window.Apex._chartInstances[i].chart.w.config`.

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

## Estándar de CARTEL BLOQUEANTE — "Contorno" (30/07/2026)

**Regla del proyecto (elegida por el usuario entre 5 variantes en rojo):** cuando una
validación **corta** una acción del usuario (no se ejecuta la consulta / no se graba), el aviso
va con el cartel **Contorno**: fondo blanco, filo e ícono en rojo de marca, texto en la tinta
azul noche de la app. Es deliberadamente la variante más liviana — un límite operativo no es una
falla del sistema, no hace falta gritar. **No usar `Severity.Warning`** (el naranja del default
se confunde con el acento de marca y no lee como bloqueo).

```csharp
@inject ISnackbar Snackbar   // MudSnackbarProvider ya está en MainLayout/EmptyLayout

Snackbar.Add(
    "El rango Desde–Hasta no puede superar los 60 días (elegiste 210). Achicá el período e intentá de nuevo.",
    Severity.Error,
    config =>
    {
        config.SnackbarVariant  = Variant.Outlined;
        config.SnackbarTypeClass = "nt-toast-contorno";   // definida en app.css
    });
return;   // 🔴 cortar ANTES de la query / del INSERT
```

**Trampas verificadas (por reflexión sobre MudBlazor 9.5, no por documentación):**
- La propiedad es **`SnackbarTypeClass`** — `SnackbarOptions` **no** tiene una propiedad `Class`
  genérica (falla con CS1061). Las opciones útiles del tipo: `SnackbarVariant`, `Icon`,
  `IconColor`, `ShowCloseIcon`, `VisibleStateDuration`, `RequireInteraction`, `HideIcon`.
- El `Variant.Outlined` nativo pinta **todo el texto de rojo** (`mud-alert-outlined-error` fija
  `color`) y deja el ✕ gris. Por eso `.nt-toast-contorno` en `app.css` pisa `background`, `color`
  y `border`, y pinta de rojo el ícono **y** el `.mud-snackbar-close-button` (si no, el ✕ queda
  desalineado del resto en color).

**Copy del mensaje:** decir el límite, el valor que eligió el usuario, y qué hacer
("Achicá el período e intentá de nuevo"). Nunca solo "valor inválido".

### Tope de rango Desde–Hasta en informes (convención)

Todo informe que pega contra `viaje` lleva un tope de ancho de período, declarado como constante
al lado de `Cargar()`. **El tope se avisa DOS veces:**

1. **En vivo, en la barra de filtros** — un chip rojo `máx. N días` (clase `.rfs-rango-alerta`
   en `app.css`) que aparece apenas el rango elegido supera el tope, **antes** de apretar
   "Aplicar filtros". Sin esto el usuario cree que la pantalla se rompió.
2. **Al aplicar** — el cartel Contorno de arriba, y `return` antes de la query.

```csharp
private const int RangoMaximoDias = 95;   // 3 meses
private int RangoDiasElegidos =>
    _desde is null || _hasta is null ? 0 : (int)(_hasta.Value.Date - _desde.Value.Date).TotalDays;
```

Topes vigentes: `ViajesPorChofer` 95 · `ReservasBandaHoraria` 60 · `ReservasFechaServicio` 400 ·
`ReservasPorCliente` 400. Referencia viva del patrón completo (chip + cartel):
`ViajesPorChofer.razor`.

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

## Patrón MAESTRO / DETALLE — grilla + ficha de lectura al costado (19/08/2026)

Cuando la fila tiene un **texto largo** que no entra en una columna (el mensaje de una novedad, un
comentario, una descripción), el patrón es grilla a la izquierda + ficha de lectura a la derecha,
en vez de abrir un modal por cada fila. Referencia viva: **`LibroNovedades.razor`**.

Las 5 reglas que costó afinar (todas medidas en el navegador, no a ojo):

1. **El bloque lleva alto propio, no los hijos.** `.split { height: calc(100vh - 430px);
   min-height: 320px; align-items: stretch; }`. Si el alto se lo ponés a la tabla
   (`MudTable Height="..."`), la tarjeta de la grilla termina después que la tabla y queda una
   **franja blanca** abajo, y el panel —que mide por contenido— queda mucho más corto que la
   grilla, con medio metro de gris al costado. Con el alto en el contenedor los dos miden igual.
2. **La tabla se estira con flex.** La tarjeta es `display:flex; flex-direction:column`, y
   `.mud-table` + `.mud-table-container` van con `flex:1; min-height:0`. Sin el `min-height:0`
   el flex no deja encoger y vuelve la franja.
3. **Al achicar la pantalla se achica el PANEL, no la grilla** (420 → 360 → 300 px), y el
   apilado se retrasa hasta **980 px**: el peor caso real del cliente es 1088×614 y ahí el
   maestro/detalle tiene que seguir en dos columnas — si se apila, el operador pierde de vista
   la lista justo cuando está leyendo.
4. **`table-layout: fixed` en la grilla.** Las columnas angostas conservan su ancho y cede la
   columna de texto (con `text-overflow: ellipsis` + `title`). Sin esto, un asunto largo empuja
   y la última columna se corta. Medí el ancho con la fuente real: `"19/08/2026 04:44"` en IBM
   Plex 11,8/500 **necesita 150 px** con el padding de la celda, no 130.
5. **El clic tiene que notarse de los dos lados.** En la grilla: fondo azul
   (`cli-grid__row--sel`) **+ barra naranja** `box-shadow: inset 3px 0 0 0 #F99410` en la primera
   celda — en una grilla densa el cambio de fondo solo no se ve si mirás la otra punta. En el
   panel: `@key="_sel?.Id"` para que se remonte y vuelva a correr una animación de entrada de
   ~180 ms (con `@media (prefers-reduced-motion: reduce)` que la apaga).

Extras que valieron la pena: filete del color del módulo arriba del panel, metadatos en grilla
de 2 columnas con rótulo chico en mayúsculas (no chips: ocupan más y dicen menos), y el número
de reserva como **link al Zoom dentro del panel** — el botón de la barra de abajo queda lejos de
donde el ojo está leyendo.

⚠️ No pintes filas por un estado que es mayoría transitoria. La grilla del libro teñía de
amarillo las novedades "sin enviar": durante todo el turno **todas** lo están, así que la grilla
quedaba entera amarilla hasta la corrida de correos. La columna ya lo dice.

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
| `.nav-section__title` | Botón de grupo colapsable (+ filete de módulo en `::before`) |
| `.nav-section__icon` | Ícono del grupo — color = `var(--nt-mod)` |
| `.nav-item` | Enlace de navegación |
| `.nav-item--active` | Ítem activo — borde y fondo derivados de `var(--nt-mod)` |
| `.nav-item--disabled` | Ítem sin ruta aún (gris, no clickeable) |
| `.nav-divider` | Línea separadora entre secciones |
