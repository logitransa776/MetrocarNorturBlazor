# PENDIENTE — Blanqueo de la grilla de Tráfico al scrollear (investigación archivada)

> ## 🔵 MITIGACIÓN EN PRUEBA (20/07/2026) — suavizado de la rueda del mouse
>
> Idea de Claudio: el blanco es una **carrera** (scroll vs. round-trip de SignalR), así que
> **bajar la velocidad de la rueda** le da tiempo al render a llegar. Ataca la variable real.
>
> **Aplicado y compilando limpio. PENDIENTE de probar en pantalla y en el servidor.**
>
> - **Sin archivo JS nuevo** (pedido explícito): la función `traficoSuavizarScroll` vive en
>   `wwwroot/descarga.js`, junto a `bloquearScrollVentana` / `traficoScrollFila`. Se engancha
>   desde `PlanillaTrafico.razor` en `OnAfterRenderAsync` y se libera en `Dispose`.
> - **Tres constantes** arriba del bloque: `TRAFICO_SCROLL_FACTOR = 0.70` (30% más lento — la
>   perilla a calibrar), `TRAFICO_SCROLL_EASING = 0.22` (scroll continuo por rAF en vez de
>   saltos de ~100px por notch) y `TRAFICO_SCROLL_EPSILON = 0.5`.
> - **Por qué este camino y no los de abajo:** es **100% cliente**. No toca Blazor ni SignalR,
>   así que **no puede** degradar el click, el Zoom ni el menú contextual — exactamente donde
>   fracasaron los intentos anteriores. Costo cero para el servidor.
> - **El easing probablemente rinda más que la ralentización sola:** el overscan de 20 filas
>   (400px) absorbe bien el movimiento gradual, pero no el salto seco de un notch.
> - **Es mitigación, no cura:** con arrastre de la barra o `Ctrl+Fin` el blanco vuelve.
>
> **Detalles que no son obvios:** listener con `{ passive: false }` (sin eso el
> `preventDefault()` se ignora y queda doble movimiento); **trackpad excluido** (ya scrollea
> continuo, ralentizarlo se siente roto — se detecta por la firma del delta); normaliza
> `deltaMode` 1/2 a píxeles; no captura en los bordes (deja el scroll chaining); los notches
> se **acumulan** si se gira rápido. 🐛 `traficoScrollFila` ahora **cancela la animación en
> vuelo**: si scrolleabas con la rueda y tocabas una flecha, la animación seguía a su destino
> viejo y pisaba el salto del teclado.
>
> **Para calibrar:** si se siente pesado → 0.80; si sigue blanqueando → 0.60. **Probarlo en el
> servidor** (WIN2022DEVBL): ahí la latencia de red es mayor que en local, el blanco es peor y
> 0.70 puede quedar corto.

> ## ⚠️ ACLARACIÓN DE ESTADO (20/07/2026)
>
> El camino "mover el render de filas al cliente" que se menciona al final del bloque de abajo
> **se llegó a implementar y medir** (blanco 0/6, click 113 ms), pero **Claudio lo revirtió a
> mano a un backup anterior** porque el JS andaba mal. **`wwwroot/js/trafico-grid.js` NO existe.**
> Lo vigente hoy vuelve a ser **`<Virtualize>`**, con el blanqueo abierto.

> ## ⛔ SEGUNDO INTENTO, MEDIDO Y REVERTIDO (18/07/2026) — LEER ANTES DE REINTENTAR
>
> El camino de §3 (render completo + `content-visibility`) se volvió a probar, esta vez
> **con el gate de fila de §3b aplicado** y **con mediciones antes/después** sobre un día real
> de 331 servicios (08/05/2026, viewport 1900×1100, 3 corridas por escenario).
>
> **Resultado: elimina el blanco por completo, y degrada la interacción entre 2,5x y 6x.**
>
> | Métrica | `<Virtualize>` (vigente) | Render completo + gate |
> | --- | --- | --- |
> | Filas en el DOM | 82 | 331 |
> | **Saltos en blanco** | 6/6 | **0/6** ✅ |
> | Ráfaga de 20 flechas ↓ | ~335 ms | **~2004 ms** 🔴 |
> | Click → selección visible | ~146 ms | **~689 ms** 🔴 |
> | Menú contextual | ~109 ms | **~271 ms** 🔴 |
> | Doble click frío → Zoom con datos | ~197 ms | ~256 ms |
>
> **Por qué el gate de fila NO alcanzó** (confirma empíricamente la hipótesis de §5): el costo
> dominante no es el diff de cada fila —eso el `ShouldRender` sí lo evita— sino que Blazor
> **visita** los 331 componentes en cada render del padre. Visitar no es gratis.
>
> **Por qué la hipótesis §6.2 (aislar el subárbol de la grilla) tampoco lo salva:** clickear una
> fila cambia `_filaActiva`, que es estado que la grilla necesita → su `ShouldRender` tendría que
> devolver `true` justo en la interacción más frecuente. No arregla el click→selección de 689 ms.
>
> **Lo que SÍ quedó aplicado y conviene mantener:**
> - **Filas fantasma** (`app.css`, `.trafico-grid--filas-fijas tbody` con `repeating-linear-gradient`
>   al paso de 20px): el hueco se lee como "cargando" en vez de "se rompió". Costo cero — es
>   pintado del navegador, no toca Blazor ni SignalR.
> - **`TraficoFilaRow`** (`Components/Shared/`): la fila como componente con `ShouldRender` gateado.
>   Con `<Virtualize>` el ahorro es modesto (~82 filas), pero es correcto y no cuesta nada.
>
> **Sobre subir `OverscanCount`:** la latencia escala con las filas en el DOM (~2,3 ms de click por
> fila, extrapolando 82→331). Overscan 60 daría ~150 filas → click de ~300 ms (3x peor) y solo
> mitiga parcialmente. No recomendado.
>
> **Lo único que quedaría por probar** (rewrite, no ajuste): mover el render de filas al cliente
> — mandar el día como JSON una vez y pintar con JS, dejando a Blazor solo las interacciones.
> Elimina el problema por definición. Es reescribir la pantalla.
>
> **Arnés de medición:** `tests/tmp-perf-trafico.spec.ts` (efímero, borrado). Medía blanco por
> "filas que intersectan el viewport tras un salto de scroll" (medición directa, no análisis de
> píxeles) + latencias de teclado/Zoom/menú/click. Recrearlo desde este doc si hace falta.


> **Fecha de la investigación:** 18/07/2026
> **Estado: NO APLICADO — el código volvió a la versión con `<Virtualize>` por decisión del
> usuario.** Este documento guarda todo lo aprendido para retomarlo más adelante.
> **Por qué se revirtió:** el fix eliminaba el blanqueo al scrollear, pero el usuario reportó
> que **el Zoom del Viaje y el menú contextual (botón derecho) se ponían lentos**. El costo
> se corrió de lugar en vez de desaparecer. Se prefirió volver al comportamiento conocido
> (blanquea al scrollear, pero Zoom y menú contextual rápidos) hasta encontrar una solución
> que no tenga ese trade-off.

---

## 1. El síntoma original

Al scrollear rápido la grilla de la Planilla de Tráfico (rueda fuerte o arrastrando la barra),
el cuerpo de la grilla queda **completamente en blanco** durante un instante. Evidencia:
video `Grillaparpadeo.mp4` (18/07/2026). Analizado cuadro por cuadro: frames con **blancura
1.00** (100% de píxeles blancos puros en la zona de la grilla) en t=1.1s, 3.5s y 6.7s.

Ocurre incluso en **localhost**, así que no es latencia de red.

---

## 2. La causa raíz (diagnóstico confirmado)

Es un **límite arquitectural de `<Virtualize>` en Blazor Server**, no un problema de
configuración:

1. `<Virtualize>` mantiene renderizadas solo `viewport + OverscanCount` filas (~65). El resto
   del alto lo ocupan `<tr>` spacer vacíos.
2. Cuando el scroll entra en zona no renderizada, hace falta un **round-trip completo**:
   IntersectionObserver (JS) → SignalR → render en el servidor → patch → paint.
3. Un golpe de rueda o un arrastre de barra mueve **más píxeles que el colchón renderizado**
   (20 filas × 22px = 440px de colchón, contra ~7.200px de altura total de un día de 328
   servicios). El scroll siempre le gana al round-trip → **blanco**.

**Lo que ya se había intentado y NO alcanza** (todo estaba aplicado y el blanco persistía):

- `OverscanCount` subido de 6 → 20 (agranda el colchón, no elimina el round-trip).
- `ItemSize="22"` calzado exacto al alto real de fila.
- `SpacerElement="tr"` (correcto y necesario dentro de `<tbody>`, pero ortogonal al blanco).
- `content-visibility` excluido de la grilla principal (correcto: choca con los spacers).

**Lo que no aplica:** el parámetro `Placeholder` de `<Virtualize>` solo existe cuando se usa
`ItemsProvider` (carga por páginas desde el servidor). Además pagaría el mismo round-trip:
mostraría un esqueleto gris en vez de blanco, pero seguiría habiendo un hueco visible.

---

## 3. El fix que se probó (funcionó para el blanco, se revirtió por el Zoom)

### 3a. Render completo + virtualización del lado del navegador

Se reemplazó `<Virtualize>` por un `@foreach` que renderiza **todas** las filas del día, y la
virtualización pasó 100% al navegador vía CSS:

```css
.trafico-grid tbody tr {
    content-visibility: auto;
    contain-intrinsic-size: auto 22px;   /* debe coincidir con el alto real de fila */
}
```

El scroll deja de tocar el servidor → **cero round-trips → blanco imposible**. Es el mismo
esquema que ya usan la grilla de cancelados y el panel Buses.

**El dato que lo habilita** (verificado contra `replicaVPF`, tabla `viaje`, 2025-2026):

| Métrica | Valor |
| --- | --- |
| Día típico | 300-460 servicios |
| Máximo real | 461 (02/04/2025) |
| Único outlier | 1.234 (07/11/2025) |
| Día del video | 328 (02/07/2026) |

Con ese techo, renderizar todo es barato para el navegador (el `content-visibility` hace que
pinte solo lo visible) y el DOM completo elimina el hueco.

### 3b. Fila como componente con `ShouldRender` gateado

Consecuencia del render completo: cada evento (click de fila, tecla, y **los 2 clicks que
componen un doble click**) hacía que Blazor rediffeara ~330 filas × 25 celdas en el servidor.
Para acotarlo se creó `Components/Shared/TraficoFilaRow.razor`: cada `<tr>` pasa a ser un
componente cuyo `ShouldRender` devuelve `false` salvo que haya cambiado algo propio de esa
fila (referencia de la fila, nº de orden, selección, flash):

```csharp
private PlanillaTraficoRow? _pintadaFila;
private int _pintadaNro;
private bool _pintadaActiva;
private bool _pintadaFlash;

protected override void OnInitialized() => RegistrarPintada();

protected override bool ShouldRender()
{
    if (ReferenceEquals(Fila, _pintadaFila) && Nro == _pintadaNro
        && EsActiva == _pintadaActiva && EsFlash == _pintadaFlash)
        return false;
    RegistrarPintada();
    return true;
}
```

Los callbacks al padre se pasan como `EventCallback<T>` (no `Action`), y los helpers de
presentación (`EstadoCss`, `EstadoLabel`, `PartirRecorrido`) se movieron al componente como
`public static` porque los reusan las grillas de cancelados y Buses.

⚠ **Trampa del patrón:** si una celda pasa a depender de estado externo a los parámetros
gateados, hay que sumarlo como parámetro y compararlo en `ShouldRender` — si no, la celda
queda desactualizada (el gate se come el render).

---

## 4. Mediciones (Playwright, día de 328 filas, viewport 2400×1240)

### Blanqueo al scrollear (blancura = fracción de píxeles blancos puros, captura sin espera
tras cada salto de scroll)

| Escenario | Con `<Virtualize>` | Con render completo |
| --- | --- | --- |
| Saltos tope↔fondo y ráfagas de rueda | **1.00** (blanco total) | **0.000-0.052** (sin blanco) |

### Latencias de interacción

| Medición | Sin gate de fila | Con gate (`TraficoFilaRow`) |
| --- | --- | --- |
| Ráfaga de 20 flechas ↓ | 585 ms | **192-276 ms** |
| Doble click → shell del diálogo | ~150-176 ms | ~130-161 ms |
| Doble click → Zoom con datos | ~193-227 ms | ~176-236 ms |
| Doble click tras ráfaga → datos | 370 ms | 409-581 ms |
| Click de fila → selección visible | ~180-277 ms | ~169-227 ms |

**Regresión:** la suite completa quedó en **40/40 en verde** con el fix aplicado.

---

## 5. Por qué se revirtió igual (el trade-off que quedó abierto)

A pesar de los números, el usuario reportó en uso real que **el Zoom del Viaje y el menú
contextual (botón derecho) se sienten lentos** con el render completo. Lectura honesta de las
mediciones: el doble click **tras una ráfaga de eventos** fue el único caso que empeoró
(370 ms → 409-581 ms), y ese es justo el patrón de uso real (el operador clickea, navega y
abre el Zoom en secuencia rápida). El gate de fila no alcanza porque el costo restante no está
en el diff de las filas sino, probablemente, en:

- el árbol de ~330 componentes que Blazor igual debe **visitar** en cada render del padre
  (aunque cada uno devuelva `false` en `ShouldRender`, la visita no es gratis);
- el **menú contextual**: hoy hay UN solo `MudMenu` compartido que se posiciona por programa,
  pero abrirlo dispara un render del padre → visita de los ~330 componentes;
- el diálogo del Zoom se monta como hijo del mismo árbol de render.

---

## 6. Hipótesis para retomar más adelante (sin probar)

Ordenadas por relación beneficio/riesgo:

1. **Híbrido con umbral** (ya estaba acordado como plan B): `@if (visibles.Count <= N)`
   render completo, `else` `<Virtualize>`. No resuelve el trade-off, solo lo acota.
2. **Aislar el subárbol de la grilla**: mover toda la `<table>` a un componente propio con su
   propio `ShouldRender`, de modo que abrir el Zoom o el menú contextual **no dispare** un
   render del árbol de filas. Esta es probablemente la de mayor impacto para el síntoma
   reportado (Zoom/menú lentos), porque ataca la visita del árbol, no el diff de cada fila.
3. **Sacar el menú contextual y el diálogo del árbol de la página** (renderizarlos en un
   contenedor hermano), para que su apertura no toque el subárbol de la grilla.
4. **`@key` estable + `OverscanCount` alto con Virtualize**, aceptando un blanco breve pero
   corto: no probado con overscan muy grande (ej. 100+), que aumentaría el colchón a ~2.200px.
   Cuesta memoria/diff, pero mantendría el Zoom rápido.
5. **Prerender del hueco**: mantener `<Virtualize>` pero pintar las filas fuera de viewport
   con un placeholder CSS del color de fondo (gris claro en vez de blanco), para que el hueco
   sea menos violento visualmente. No elimina el problema, lo disimula.

---

## 7. Archivos involucrados (para reaplicar el experimento)

- `MetroCarSysBlazor/Components/Pages/PlanillaTrafico.razor` — el `<tbody>` de la grilla
  principal (`<Virtualize>` ↔ `@foreach`), la clase `trafico-grid--virtual` en la `<table>`,
  y `DebounceInterval="250"` en el buscador.
- `MetroCarSysBlazor/wwwroot/app.css` — reglas `.trafico-grid--virtual tbody tr.tg-row`
  (alto fijo) y el selector de `content-visibility`.
- `MetroCarSysBlazor/Components/Shared/TraficoFilaRow.razor` — el componente de fila gateado
  (**eliminado al revertir**; recrearlo desde el snippet de §3b si se retoma).
- `tests/smoke.spec.ts` y `tests/trafico-filtro-reserva.spec.ts` — los selectores dependen de
  si la `<table>` lleva la clase `--virtual`: con `<Virtualize>` se usa
  `table.trafico-grid--virtual tbody tr.tg-row`; sin ella, `.trafico-wrap--nav tbody tr.tg-row`
  (este último funciona en **ambos** casos, es el más robusto).
