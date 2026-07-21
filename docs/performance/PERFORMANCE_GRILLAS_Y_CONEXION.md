# Performance — Grillas grandes y conexión a SQL (Blazor Server)

> **Fecha:** 16/06/2026 · **Nota 18/07/2026:** ver §3 — el blanqueo al scrollear de la
> Planilla de Tráfico es un **pendiente abierto** (el fix se probó y se revirtió).
> **Disparador:** El Zoom del Viaje tardaba 6-7 s en abrir sobre la Planilla de Tráfico.
> **Resultado:** Sub-segundo, comparable a la fluidez de la grilla vieja (Streamlit).
> **Archivos tocados:** `appsettings.json`, `Program.cs`, `Services/DbWarmupService.cs`
> (nuevo), `Components/Pages/PlanillaTrafico.razor`, `wwwroot/app.css`.

Este documento explica **por qué** la grilla iba lenta y **qué se hizo**, para no volver
a perder tiempo re-investigando lo mismo en futuros informes/grillas.

---

## TL;DR — las dos causas (eran independientes)

El lag tenía **dos causas sumadas**, no una. Las dos hay que tenerlas en cuenta al armar
cualquier grilla nueva con muchas filas:

1. **Conexión a SQL en frío en cada query.** El connection string tenía `Pooling=False`
   con `Encrypt=True` + instancia con nombre (`\SQLEXPRESS`). Cada `OpenAsync()` pagaba
   resolución de instancia (SQL Browser UDP 1434) + handshake TLS + login TDS, y lo tiraba.
   El Zoom abre 2 conexiones; la Planilla abre más. → **segundos perdidos por conexión.**
2. **Render de Blazor Server escalando con la cantidad de filas.** Cada interacción
   (incluido el doble clic que abre el Zoom) re-renderizaba las ~300 `<tr>` del día en el
   servidor, las diffeaba y las mandaba por SignalR. Con pocas filas era rápido; con 300,
   4-5 s. → **el costo escalaba con la cantidad de filas, no con la query.**

> **Síntoma diagnóstico clave:** "es lento solo cuando la grilla tiene muchos registros".
> Si fuera la conexión o la query, tardaría igual con 10 o con 300 filas. Que **escale con
> la cantidad de filas** apunta al render de Blazor, no a SQL.

---

## Por qué la grilla vieja (Streamlit/Python) era instantánea

La app anterior (`MetrocarNorturBlazorGrillaVieja/`, Streamlit) era rápida por dos razones
que conviene entender porque son justo lo contrario de lo que hacía mal el Blazor:

- **Una sola conexión viva, reutilizada siempre** (`@st.cache_resource` sobre
  `get_connection()` en `data.py`). Pagaba el handshake **una vez** en todo el proceso.
- **Renderizaba HTML plano de una sola pasada**, sin árbol de diffing por celda ni
  delegados de evento por fila. No hay "re-render incremental" que escale con las filas.

No se puede copiar la arquitectura de Streamlit a Blazor Server (son modelos distintos),
pero **sí se puede imitar el efecto**: pool caliente (≈ conexión persistente) + virtualizar
el render (≈ no generar las filas que no se ven).

---

## Solución 1 — Connection string + warmup del pool

### 1a. `appsettings.json` — activar el pool

```diff
- ...;Pooling=False;MultipleActiveResultSets=False;Encrypt=True;...
+ ...;Pooling=True;Min Pool Size=2;Max Pool Size=50;MultipleActiveResultSets=False;Encrypt=True;...
```

- `Pooling=True` — **el cambio de mayor impacto.** SqlClient reutiliza conexiones del pool
  en vez de tirarlas. La primera query paga el handshake; las siguientes lo reutilizan.
- `Min Pool Size=2` — piso de conexiones vivas y autenticadas (≈ el `cache_resource` viejo).
- `Encrypt=True` se **mantiene** — con pool el costo del TLS se paga una vez y se amortiza.
  No hace falta resignar cifrado para ganar velocidad.

> **Puerto fijo (NO se aplicó, a propósito):** la instancia `MSSQL16.SQLEXPRESS` usa
> **puertos dinámicos** (registro: `TcpDynamicPorts=0`, `TcpPort` vacío). Hardcodear un
> puerto (`Server=...,1433`) se rompería en el próximo reinicio del servicio SQL. Con el
> pool caliente, la resolución vía SQL Browser se paga una sola vez en el arranque, así que
> el puerto fijo deja de importar. Para fijarlo de verdad habría que tocar SQL Server
> Configuration Manager (infraestructura, no código) — no lo vale para una instancia local.

### 1b. `Services/DbWarmupService.cs` — calentar el pool al arrancar

`IHostedService` que, al arrancar la app, abre `Min Pool Size` conexiones en paralelo y
ejecuta `SELECT 1` para que el handshake + login se paguen **en el boot del servidor** y no
en el primer doble clic del operador. Fire-and-forget (no bloquea el arranque), tolerante a
fallos (si la base no está, loguea y sigue).

En el arranque verás en el log:
```
info: MetroCarSysBlazor.Services.DbWarmupService[0]
      Pool de conexiones SQL calentado: 2 conexiones en NNN ms.
```

> **Trampa resuelta (importante para futuros servicios de arranque):** la primera versión
> tomaba la conexión vía `IDbContextFactory` + `db.Database.GetDbConnection()` y la disponía
> con `await using`. Eso fallaba con `InvalidOperationException: No se ha inicializado la
> propiedad ConnectionString` / `server '' database ''`. Causa: en el arranque, antes de que
> el pipeline esté listo, las `DbContextOptions` del factory pueden llegar a medio
> inicializar, **y** disponer a mano la conexión que es propiedad del DbContext la deja con
> ConnectionString vacío (la misma trampa que el comentario de `Program.cs` advierte para el
> pooled factory). **Solución:** el warmup abre una `SqlConnection` PROPIA leyendo el
> connection string directo de `IConfiguration`. El pool de SqlClient es global por
> connection string, así que calentar así beneficia igual a todas las conexiones que abre EF
> después. Regla: **en código de arranque (`IHostedService`/`StartAsync`) no dependas del
> DbContextFactory para abrir conexiones; usá `SqlConnection` + `IConfiguration`.**

### Por qué NO se usa `AddPooledDbContextFactory`

`Program.cs` usa `AddDbContextFactory` (no `AddPooled...`) a propósito: `ReportService`
hace `await using var conn = db.Database.GetDbConnection()`, que dispone la conexión del
DbContext. Con un contexto del pool eso deja la conexión muerta para el próximo uso →
`InvalidOperationException` intermitente. El pooling que importa para performance es el de
**SqlClient** (`Pooling=True` en el connection string), no el de instancias de DbContext.

---

## Solución 2 — Virtualizar el render de la grilla

El cuello de botella del lado servidor se ataca en **dos capas complementarias**:

### 2a. `<Virtualize>` de Blazor (lado SERVIDOR) — la que mueve la aguja

La grilla principal de `PlanillaTrafico.razor` pasó de `@foreach` a `<Virtualize>`:

```razor
<Virtualize Items="visibles" Context="f" ItemSize="26" OverscanCount="6">
    @{ var _nro = visibles.IndexOf(f) + 1; var esActiva = _filaActiva == f.IdViaje; }
    <tr @key="f.IdViaje" ... @ondblclick="() => AbrirZoom(f.IdViaje, f.Fecha)">
        ...
    </tr>
</Virtualize>
```

El servidor genera solo las ~25 filas visibles en el viewport (no las ~300 del día). En cada
re-render (abrir Zoom, filtrar) solo esas se diffean y viajan por SignalR. El costo pasa de
**O(total de filas)** a **O(filas visibles)** — que es exactamente lo que hace fluida a la
grilla vieja.

- El numerito de fila `#` se calcula con `visibles.IndexOf(f) + 1` (no con un contador del
  `@foreach`, porque Virtualize no garantiza orden de invocación).
- `ItemSize` ≈ alto de fila en px; `OverscanCount` = filas extra arriba/abajo para que el
  scroll no parpadee.
- Requiere un contenedor con **altura fija y scroll** (`.trafico-wrap` ya tiene
  `max-height: calc(100vh - 230px); overflow: auto;`).

### 2b. `content-visibility: auto` (lado NAVEGADOR) — las tablas no virtualizadas

`app.css` mantiene la virtualización por CSS (el navegador pinta solo las filas en viewport)
para las tablas que **no** usan `<Virtualize>` (cancelados, panel Buses). La grilla principal
lleva la clase `.trafico-grid--virtual` y queda **excluida** del `content-visibility`, porque
choca con los spacers de altura que inserta `<Virtualize>` (doble cálculo de alto → scroll
inestable):

```css
.trafico-grid:not(.trafico-grid--virtual) tbody tr {
    content-visibility: auto;
    contain-intrinsic-size: auto 22px;
}
```

### 2c. Memoizar los filtros

`FilasVisibles()` recorría y filtraba las ~300 filas **en cada render**. Ahora el resultado
se guarda en campos (`_visibles`, `_canceladosVisibles`) y solo se recalcula vía
`RecalcularVisibles()` cuando cambian los datos o un filtro (combos, Emp/Tur/Nortur, S/C,
Cxl, buscador, `Cargar`, `RefrescarConFlash`). El render solo lee los campos.

---

## §3 — Pendiente abierto: el blanco al scrollear (18/07/2026)

Con `<Virtualize>`, la grilla de la Planilla de Tráfico **queda en blanco un instante al
scrollear rápido** (rueda fuerte o arrastrando la barra). Es arquitectural de `<Virtualize>`
en Blazor Server: el scroll le gana al round-trip de SignalR. No se arregla subiendo
`OverscanCount` (ya está en 20).

Se investigó a fondo y se probó el fix (render completo del día + `content-visibility` +
fila como componente con `ShouldRender` gateado). **Elimina el blanco** —verificado por
píxel—, pero hacía sentir **lentos el Zoom del Viaje y el menú contextual**, así que
**se revirtió**: hoy sigue vigente `<Virtualize>` con el blanqueo.

👉 **Toda la investigación (causa, mediciones A/B, snippets e hipótesis para retomarlo) está
en `docs/performance/PENDIENTE_GRILLA_TRAFICO_BLANQUEO.md`. Leer ESE doc antes de volver a
tocar la virtualización de esa grilla — evita repetir un camino ya recorrido.**

---

## Checklist para CUALQUIER grilla nueva con muchas filas

1. **¿El connection string tiene `Pooling=True`?** (debería; ver `appsettings.json`). Nunca
   poner `Pooling=False`.
2. **Si la grilla puede superar ~100-150 filas → usar `<Virtualize>`** sobre el `<tbody>`,
   con la clase `--virtual` en la `<table>` y un wrapper de altura fija + `overflow:auto`.
   No virtualizar tablas chicas (overhead innecesario). Tener presente el trade-off del §3
   (blanqueo con scroll rápido).
3. **Memoizar el filtrado** en un campo; recalcular solo al cambiar datos/filtros, no en
   cada render.
4. **No abrir conexiones en `IHostedService`/arranque vía el DbContextFactory** — usar
   `SqlConnection` + `IConfiguration`.
5. **Patrón de modal que ya está bien (no tocar):** `DialogService.ShowAsync` abre el modal
   con su spinner ANTES de tener los datos (respuesta visual inmediata), y las queries del
   modal van en paralelo con `Task.WhenAll`. El detalle del viaje hace SEEK por `f_reserva`
   (que la fila conoce) en vez de scan de la tabla `viaje` (521K filas).
