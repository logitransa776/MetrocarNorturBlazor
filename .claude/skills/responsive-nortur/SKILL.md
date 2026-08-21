---
name: responsive-nortur
description: >
  Contrato de responsividad de pantallas de Buslink (Metrocar NORTUR): qué resolución hay que
  soportar, cuánto ancho y alto hay realmente disponible, y cómo se construye una franja de KPIs,
  una barra de filtros o una grilla que NO recorte texto en las PCs del cliente. Usar SIEMPRE que
  se agregue o modifique una tarjeta KPI, una fila de indicadores, una barra de filtros o cualquier
  layout de un informe; cuando el usuario diga que algo "se ve cortado", "no se lee", "se recorta",
  "queda con puntos suspensivos", "se desarma en pantalla chica" o hable de resolución/tamaño de
  pantalla; y antes de dar por terminado cualquier informe nuevo. Incluye el auditor automático
  (tests/responsive.spec.ts) que mide truncados de verdad en el navegador.
---

# Responsividad NORTUR — el contrato de pantalla

> Establecido el 11/08/2026 a partir de un bug real: en `/panel-flota` las 6 tarjetas KPI
> se recortaban a 1360×768 ("3.3…", "5…") y el sub de una tarjeta quedaba con **12px
> visibles de los 133 que necesitaba**. No era un detalle estético: el número del indicador
> era ilegible.

---

## 1. El contrato (esto es lo que hay que cumplir)

| Concepto | Valor | De dónde sale |
| --- | --- | --- |
| Resolución mínima soportada | **1360×768 físicos** | Es la PC más chica de NORTUR (dato del cliente) |
| Escala de Windows en las PCs | **conviven 100% y 125%** | Dato del cliente (11/08/2026) |
| **Peor caso real (el que hay que cumplir)** | **1088×614 px CSS** | 1360/1,25 × 768/1,25 |
| Ancho útil del contenido a 1088 | ~1040px | 1088 − padding del layout |
| Ancho útil del contenido a 1360 | **1312px** | medido en la app real |

> ⚠ **La trampa que casi nadie ve:** una pantalla de 1360 físicos con escala de Windows al
> 125% le entrega al navegador **1088 px CSS**, no 1360. Diseñar contra "1360" y darlo por
> bueno deja fuera a la mitad del parque de PCs. **Siempre verificar contra 1088×614.**

---

## 2. La regla de oro (la causa de fondo del bug)

**El ancho que un componente necesita es la SUMA de sus textos si están en una fila, y el
MÁXIMO si están apilados.**

```
Horizontal:  [ VIAJES SIN ASIGNAR ][ 3.370 ][ 5,8 % de 58.552 pedidos ]  → 135+55+133+gaps ≈ 370px
Apilado:      VIAJES SIN ASIGNAR
              3.370                                                       → max(135,55,133)+pad ≈ 165px
              5,8 % de 58.552 pedidos
```

6 tarjetas horizontales pedían **2.220px** sobre 1.312px disponibles. Apiladas piden ~1.000px.
Ese factor 2,2× es todo el bug. **Ante falta de ancho, apilar antes que achicar la fuente.**

Y apilar **también gana alto**: la fila horizontal se partía en dos (106px); la apilada es
una sola de ~70px.

---

## 3. Jerarquía de cesión (lo que se recorta y lo que no)

Cuando algo no entra, hay que decidir **qué cede**. En Buslink el orden es fijo:

1. **El VALOR nunca se recorta.** Ni ellipsis, ni corte. Es el dato; sin él la tarjeta no
   existe. Válvula de escape: partir en 2 renglones **solo por espacios** — así un número
   formateado ("158.840") nunca se parte al medio, pero un valor de texto ("GATE1 TRAVEL")
   baja de renglón en vez de salirse.
2. **La ETIQUETA nunca se recorta.** Identifica la tarjeta: "VIAJES SIN ASIG…" no dice nada.
   Si no entra, wrap a 2 renglones. **Nunca ellipsis.**
3. **El SUB es lo único descartable.** Hasta 2 renglones; lo que exceda se corta **pero el
   texto completo queda en el `title` (tooltip)**. El dato nunca se pierde, solo se esconde.

Corolario: **si ves un `text-overflow: ellipsis` sobre un valor o una etiqueta, es un bug.**

---

## 4. Patrón A — Franja de KPIs (RESUELTO, no reinventar)

Es el patrón por defecto de todo informe analítico. **Ya está implementado y auditado en
las 23 pantallas que lo usan** — no hay que tocar nada para que un informe nuevo lo herede.

```razor
<div class="rfs-kpis mb-3">
    <KpiCard Label="Unidades" Value="@Fmt(_kUnidades)" Sub="@_kUnidadesSub" Color="@NorturColors.Azul" />
    ...
</div>
```

Lo que hace el CSS por vos (`app.css`, sección "FRANJA DE KPIs"):

- `display:grid` con `repeat(auto-fit, minmax(var(--nt-kpi-min), 1fr))` → **todas las
  tarjetas del mismo ancho, siempre**, y salto de fila solo cuando de verdad no entra.
- `--nt-kpi-min`: 190px normal, **165px por debajo de 1200px** de viewport (ahí entran las
  6 en una sola fila a 1088).
- `@media (max-height:720px)` aprieta padding y fuentes: a 614px de alto, la franja no
  puede comerse el espacio de los gráficos.
- La tarjeta (`.mud-paper.nt-kpi`) es **apilada** y aplica la jerarquía de cesión del §3.

**Reglas para el que agrega KPIs:**

- ✅ Hasta **6 KPIs** por franja. Con 7+ el grid los baja de fila prolijamente, pero
  revisá si no conviene una segunda franja o un KPI menos.
- ✅ Etiquetas **cortas**: "Sin uso" mejor que "Unidades sin utilización en el período".
  La etiqueta identifica; el detalle va en el `Sub`.
- ✅ El `Sub` puede ser largo: tiene 2 renglones + tooltip.
- ❌ **No pongas `Style=` con anchos, ni `min-width`, ni fuentes propias en un `KpiCard`.**
  Rompe el contrato para esa pantalla y nadie se entera hasta que un usuario lo ve.
- ❌ No metas hijos que no sean `KpiCard` dentro de `.rfs-kpis` (el grid los trata como
  una tarjeta más y quedan deformes).

### Trampa de CSS que ya costó una vuelta

`MudBlazor.min.css` se carga **después** de `app.css` ([App.razor](../../MetroCarSysBlazor/Components/App.razor)).
A igual especificidad **gana MudBlazor**: una regla `.nt-kpi { background: … }` la pisa
`.mud-paper`. Por eso las reglas de la tarjeta van como **`.mud-paper.nt-kpi`**. Si escribís
CSS para cualquier componente Mud y "no toma", es esto — no hace falta `!important` ni
estilos inline, alcanza con sumar la clase Mud al selector.

---

## 5. Patrón B — Franja compacta `kpi-strip` (pantallas operativas)

La usa la Planilla de Tráfico: una barra continua de segmentos, la mitad de alto que las
tarjetas, con botones integrados (Buses, Ocupación, Avisos).

**Cuándo cada una:**

| | Patrón A `rfs-kpis` | Patrón B `kpi-strip` |
| --- | --- | --- |
| Para | Informes analíticos | Pantallas operativas de uso continuo (Tráfico) |
| KPIs | hasta 6 | **hasta 4** |
| Etiquetas | medianas | **cortas** ("Servicios", "Total pax") |
| Alto | ~70px | ~35px |
| Botones adentro | no | sí |

⚠ `kpi-strip` **no es inmune**: aguanta porque tiene 4 segmentos de etiqueta corta
(~200px cada uno sobre 1312 disponibles). Con 6 KPIs se rompe igual que se rompía el
patrón A. Si necesitás más de 4 indicadores en una pantalla operativa, usá el patrón A.

---

## 6. Anti-patrones (lo que causó el bug — no repetir)

| Anti-patrón | Por qué falla |
| --- | --- |
| `min-width: 240px` en un ítem que necesita 370px | `min-width` es *el piso antes de bajar de fila*, no "lo que necesito". Si es menor al ancho real, el navegador mete de más y **recorta** en vez de bajar de fila. |
| `flex: 1 1 0` + `flex-wrap` para una grilla de tarjetas | El último ítem se queda con **todo el sobrante** de su fila: en `/panel-flota` la 6ª tarjeta medía **1312px** sola. Usar **grid**, que reparte parejo. |
| `@media` solo a ≤900px / ≤640px | Son breakpoints de **celular**: nunca disparan en 1088–1920, que es donde trabaja el 100% de los usuarios. **El rango que importa es 1088–1920.** |
| `text-overflow: ellipsis` sobre el valor o la etiqueta | Convierte un dato en un adorno ("3.3…"). Ver §3. |
| Ajustar a ojo mirando el navegador en 1920 | El desarrollo pasa en pantallas grandes; el bug vive en 1088. **Medir, no mirar** (§7). |

---

## 7. El auditor — cómo se verifica (obligatorio antes de cerrar un informe)

`tests/responsive.spec.ts` recorre las 23 pantallas con KPIs en **las dos resoluciones** y
falla si encuentra texto recortado o desborde horizontal. Detecta lo que el ojo no ve,
porque el truncado depende de la fuente y del **dato real** de la base, no del CSS.

```powershell
$env:NORTUR_USER="SUPERVISOR"; $env:NORTUR_PASS="..."
npx playwright test tests/responsive.spec.ts --reporter=list
```

Criterio: un elemento está recortado cuando `scrollWidth > clientWidth` (o `scrollHeight >
clientHeight`). Es la única medición confiable.

**Agregar un informe nuevo:** sumar su ruta al array `RUTAS`. Nada más.

**Estado 11/08/2026: 23/23 rutas en verde, en las dos resoluciones.**

> 🔎 **Lo que destapó el auditor (y por qué el test vale la pena).** En la primera corrida,
> 4 rutas (`/guardias`, `/voucher-recepcion`, `/resumen-liquidaciones`,
> `/facturacion-estimada`) daban "ⓘ sin KPIs". La lectura fácil era "no tienen datos".
> Era falso: **tenían KPIs a la vista, pero NO usaban `.rfs-kpis`** — estaban armados con
> `<div class="d-flex flex-wrap">` y wrappers `style="flex:1 1 0; min-width:180px"`.
> O sea: heredaron la tarjeta apilada (usan `<KpiCard>`) pero **no** el contenedor grid, y
> el auditor no las estaba midiendo. Ya están migradas.
>
> **Moraleja para la próxima:** un "ⓘ sin KPIs" **no es un caso benigno, es un agujero de
> cobertura** — hay que ir a mirar la pantalla antes de aceptarlo. Y si el test no ve una
> franja de KPIs que en pantalla existe, el problema es la pantalla, no el test.

Verificación de que no queda ninguna afuera (correr al agregar pantallas nuevas):

```bash
# 1) todo archivo con <KpiCard> debe tener rfs-kpis
for f in $(grep -rl '<KpiCard' --include=*.razor .); do grep -q 'rfs-kpis' "$f" || echo "SIN rfs-kpis -> $f"; done
# 2) ningún <KpiCard> puede colgar de un d-flex
grep -rn -B1 '<KpiCard' --include=*.razor . | grep 'd-flex'
```

**La grilla de Tráfico se excluye a propósito:** trunca por diseño (anchos fijos por
`colgroup`, ver memoria `grilla-anchos-colgroup-fixed`). Auditarla daría ruido permanente
y terminaría con un test que todos ignoran.

---

## 8. Checklist para un informe nuevo

- [ ] La franja de KPIs usa `<div class="rfs-kpis">` + `<KpiCard>`, **sin estilos propios**.
- [ ] Máximo 6 KPIs; etiquetas cortas; el detalle en el `Sub`.
- [ ] Ruta agregada a `RUTAS` en `tests/responsive.spec.ts`.
- [ ] `npx playwright test tests/responsive.spec.ts` en verde en **las dos** resoluciones.
- [ ] Mirado con los ojos a 1088×614 (captura), no solo medido.

---

## 9. Pendiente — lo que este contrato TODAVÍA no cubre

Cerrado el 11/08/2026: **solo las tarjetas KPI**. Falta llevar el mismo tratamiento a:

1. **Barra de filtros** — a 1088 los date pickers, selects y los botones Filtro/Excel se
   aprietan. Convención vigente (memoria `barra-filtros-layout-estandar`): filtros a la
   izquierda, botones juntos a la derecha **sin partirse**. Falta verificar que se cumpla
   a 1088 y sumarlo al auditor.
2. **Gráficos ApexCharts** — a 614px de alto los gráficos y el pivote quedan bajo la línea
   de flotación. Falta definir un presupuesto de **alto** (hoy el contrato solo fija ancho).
3. **Tablas pivote** — desborde horizontal con muchas columnas de meses.
4. **Diálogos** — ya tienen su propio contrato (`--nt-dlg-*`, memoria
   `modales-estandar-nt-dlg`, objetivo 1280×720). Verificar que se sostenga a 1088×614.
