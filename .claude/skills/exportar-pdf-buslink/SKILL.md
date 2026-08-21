---
name: exportar-pdf-buslink
description: >
  Formato ESTÁNDAR de exportación a PDF de Buslink (NORTUR) y cómo implementarlo.
  Usar SIEMPRE que haya que agregar un botón "PDF"/"Imprimir" a un informe, panel, grilla,
  comprobante o dialog; cuando el usuario pida "exportar a PDF", "imprimir esto", "que se pueda
  guardar en PDF", "un reporte para mandar", o "que no se pueda modificar"; y antes de tocar
  `imprimirElemento` (descarga.js) o cualquier CSS dentro de `@media print`. También al depurar
  un PDF que sale con hojas en blanco, sin colores, con "about:blank", cortado o mal paginado.
  Define el encabezado de marca obligatorio, las reglas de CSS de impresión de Chrome, el modo
  PDF de los gráficos y cómo VERIFICAR el PDF generado (no "a ojo").
---

# Exportar a PDF en Buslink

Cómo se hace un PDF en este sistema y cómo tiene que verse. **Antes de inventar nada, copiar el
patrón que ya está en producción** en `ViajesPorChofer.razor` (panel "… por chofer") — es la
referencia viva de esta skill.

---

## 1. La decisión de arquitectura (no re-discutirla sin motivo)

**No hay librería de PDF en el proyecto y no hace falta.** El `.csproj` solo trae `ClosedXML`
(Excel). Los PDF se generan **del lado del cliente**: `window.imprimirElemento()` abre el bloque
en una ventana aislada y dispara la impresión; el usuario elige "Guardar como PDF" del navegador.

- Costo de servidor: **cero**. Dependencias nuevas: **cero**.
- Ya está probado en producción en dos lugares: el comprobante de Liquidación a Clientes
  (`LiquidacionResumenDialog.razor`) y el panel de Viajes por chofer.

**Cuándo NO alcanza** (y recién ahí evaluar `QuestPDF` server-side): cuando el PDF tiene que
generarse **sin que nadie tenga la pantalla abierta** — mandarlo por correo automático (reusando
`CorreoNovedadesService`), archivarlo, un cron mensual. Ver la memoria `imprimir-pdf-analisis`.

---

## 2. Los 4 pasos para agregar un PDF a una pantalla

### Paso 1 — Envolver lo que va al PDF

```razor
<div id="mi-panel-print" class="pdf-hoja">
    ... encabezado de marca (paso 2) ...
    ... el contenido (gráfico, tabla, comprobante) ...
</div>
```

`.pdf-hoja` da el padding de la hoja. **Todo lo que quede adentro del div sale en el PDF.**

### Paso 2 — Encabezado de marca (OBLIGATORIO, es el formato de la casa)

Copiar el bloque `.pdf-doc` de `ViajesPorChofer.razor`. Lleva, en este orden:

| Parte | Qué va | Por qué |
| --- | --- | --- |
| **Banda de marca** | Logo NORTUR (`images/logo.png`) + ícono Buslink (`images/buslink_icon_e.png`) + "BUSLINK" + fecha de emisión a la derecha | Identidad. Los dos logos, siempre |
| **Título** | Qué informe es | — |
| **Filtros aplicados** | Período · métrica · TODOS los filtros + el foco del cross-filter si está activo | 🔴 **Sin esto dos PDF distintos son indistinguibles y nadie puede reproducir el número** |
| **Franja de KPIs** | Los mismos indicadores de la pantalla | Contexto de una mirada |
| **Nota** (si aplica) | "Gráfico: top N de M · la tabla incluye los M" | Honestidad sobre lo que se recortó |

Al pie del documento, una línea gris: `Buslink · emitido <fecha> · <filtros> · vista en pantalla,
no reemplaza un comprobante oficial`.

### 🔴 Regla de marca: **"Metrocar" NO va en material de marca**

Es el nombre del sistema VIEJO que se está migrando. En encabezados, pies, títulos y subtítulos
va **Buslink** (el sistema) y **Nortur** (la empresa). Sigue siendo correcto nombrar al Metrocar
cuando se habla del FoxPro en sí ("esto todavía se carga en el Metrocar") — eso es información,
no marca.

### Paso 2b — El botón, en la esquina superior derecha del panel

**`MudButton` Outlined con ícono + la palabra "PDF"** — nunca un `MudIconButton` suelto: como
ícono de 28px pasa desapercibido. La convención de la app es que **la acción se nombra**.

```razor
<MudButton Variant="Variant.Outlined" Size="MudSize.Small"
           StartIcon="@Icons.Material.Filled.PictureAsPdf"
           OnClick="ExportarPdf" Disabled="@(_sinDatos || _exportando)"
           Class="rfs-pdf-btn" title="Exportar este gráfico a PDF">
    @(_exportando ? "Exportando…" : "PDF")
</MudButton>
```

Estilo ya definido en `.rfs-pdf-btn`: rojo **Outlined** (borde `#E4A0A0`, fondo `#FFF7F7`, texto
`#B91C1C`) + sombra suave. **No usar relleno macizo**: el "Descargar Excel" ya es un relleno
verde fuerte y dos macizos en la misma pantalla se pelean por la atención.

⚠ Mostrar **"Exportando…"** y deshabilitar mientras dura el remonte del gráfico: el modo PDF
tarda ~1,4s y sin señal el usuario cree que no pasó nada.

### Paso 3 — Llamar a la función

```csharp
await JS.InvokeVoidAsync("imprimirElemento", "mi-panel-print", titulo, "landscape", 0);
//                        id del div          título ventana   orientación   margen mm
```

- **orientación**: `"portrait"` (default) para comprobantes tipo factura · `"landscape"` para
  gráficos y tablas anchas.
- **margen**: `0` para informes (ver §3) · `12` (o no pasar nada) para comprobantes.

### Paso 4 — CSS de impresión

Lo que es de pantalla se oculta, lo que es de papel aparece:

```css
/* Fuera de @media print */
.vpc-print-tabla, .pdf-doc { display: none; }   /* solo papel */

@media print {
    .no-print { display: none !important; }      /* solo pantalla */
    .vpc-print-tabla { display: table; }
    .pdf-doc { display: block; }
}
```

Los controles de pantalla (selectores, botones, chips de foco) van con `class="no-print"`.

**Funciona porque `imprimirElemento` copia TODOS los `<link>/<style>` del documento** a la
ventana nueva, así que las media queries de impresión se evalúan igual que en cualquier página.

---

## 3. Las reglas de Chrome que hay que saber sí o sí

### 🔴 Chrome NO imprime fondos

`background-color` y `background-image` **no salen impresos** salvo que el usuario tilde
"Gráficos de fondo" en el diálogo. Sí se imprimen siempre: **`<img>`, bordes y color de texto**.

- ❌ Banner azul con texto blanco → puede salir **blanco sobre blanco** (ilegible).
- ✅ Banda blanca con filete de color (`border-bottom`), KPIs con `border-left` de color, valores
  en color de texto.
- Si un fondo es imprescindible: `-webkit-print-color-adjust: exact; print-color-adjust: exact;`
  (en ese orden, si no el linter avisa).

### 🔴 El "about:blank" y la fecha no son nuestros

Son el encabezado/pie que **Chrome dibuja en el ÁREA DE MARGEN** de la hoja (fecha · título ·
URL · nº de hoja). Se eliminan con **`@page { margin: 0 }`** — sin margen no le queda dónde
ponerlos. Por eso los informes pasan `margenMm = 0` y el respiro lo da `.pdf-hoja { padding }`.

- **Contrapartida**: también se pierde el "1/9" de Chrome. CSS no tiene numerado de páginas en
  Chrome (no existe `@page { @bottom-center }`). Si el nº de hoja importa más que el
  `about:blank`, pasar `margenMm = 12` y aceptar el encabezado del navegador.
- **Límite del padding**: `padding` vertical solo separa en la PRIMERA y la ÚLTIMA hoja. Para las
  del medio, el aire de arriba lo da el `<thead>` repetido de la tabla con `padding-top: 8mm`
  (truco reusable).

### 🔴 Un `<svg>` NO se puede partir entre dos hojas

Un `<svg>` es un elemento **atómico**. Si es más alto que lo que queda de la hoja, se va ENTERO a
la siguiente y **deja la hoja anterior casi en blanco**. (Si además es más alto que una hoja
completa, desborda hacia abajo y *parece* que se fragmentó — no es así.)

**Regla: el gráfico del PDF tiene que entrar en UNA hoja.** Patrón "modo PDF" ya implementado:

```csharp
private const int TopMaxPdf = 25;      // barras que entran en una A4 apaisada
private const int AltoBarrasPdf = 500; // px que quedan bajo el encabezado
private const int AnchoBarrasPdf = 1040; // A4 apaisada (1123px @96dpi) menos padding

private async Task ExportarPdf()
{
    _modoPdf = true;  AplicarTopeBarras();  _barsKey = Guid.NewGuid();
    StateHasChanged();
    await Task.Delay(900);      // ApexCharts redibuja: ida y vuelta por SignalR + remonte
    await JS.InvokeVoidAsync("imprimirElemento", "mi-panel-print", titulo, "landscape", 0);
    await Task.Delay(500);
    _modoPdf = false; AplicarTopeBarras(); _barsKey = Guid.NewGuid();   // SIEMPRE en finally
}
```

**El gráfico se recorta; la TABLA no.** La tabla sale de la selección completa del usuario, así
no se pierde ni una fila, y una nota aclara el recorte del gráfico.

### 🔴 ApexCharts hornea el ancho en píxeles

El `<svg>` se lleva al PDF el ancho que tenía **en pantalla** (el del panel, ej. un MudItem
`md="7"` ≈ 830px) y deja un cuarto de hoja vacío. Fix: `Width="@(_modoPdf ? 1040 : null)"` en el
`<ApexChart>` (en pantalla `null` = responsivo).

### 🔴 El fondo gris de la app viaja a la ventana de impresión

`app.css` pinta `html, body` con el gris "Piedra" y esa regla se copia. `imprimirElemento` ya lo
neutraliza con `html,body{background:#fff !important}` — no quitarlo.

---

## 4. Reglas de la tabla del PDF

Un PDF **no es interactivo**: no hay tooltip ni hover. Si en pantalla el dato exacto se lee
pasando el mouse, en papel **hace falta la tabla**.

```css
@media print {
    .mi-tabla thead { display: table-header-group; }   /* encabezado en CADA hoja */
    .mi-tabla thead th { padding-top: 8mm; }           /* aire en las hojas del medio */
    .mi-tabla tr { break-inside: avoid; page-break-inside: avoid; }
    .mi-tabla caption { break-after: avoid; }          /* que no quede huérfano al pie */
}
```

- **Fila de TOTAL obligatoria**, como última fila del `<tbody>`. ⚠ **NO usar `<tfoot>`**: en
  impresión el `tfoot` se repite en TODAS las hojas.
- Números a la derecha, `color` explícito en las celdas (la ventana copia todos los estilos del
  documento y alguna regla ajena puede colar un color).

---

## 5. 🔴 Cómo VERIFICAR un PDF (no "a ojo")

Una captura de pantalla **no alcanza** y engaña. Protocolo:

```ts
// En el spec de Playwright: neutralizar print/close para poder inspeccionar la ventana
await context.addInitScript(() => { window.print = () => {}; window.close = () => {}; });
const [popup] = await Promise.all([context.waitForEvent('page'), page.locator('.rfs-pdf-btn').click()]);
await popup.emulateMedia({ media: 'print' });
await popup.pdf({ path: 'tests/__shots__/salida.pdf', preferCSSPageSize: true, printBackground: true });
```

```python
# PyMuPDF ESTÁ disponible en este entorno
import fitz
d = fitz.open('tests/__shots__/salida.pdf')
print('paginas:', d.page_count)
for i, pg in enumerate(d):
    t = pg.get_text().strip()
    print(f'p{i+1}: {len(t)} chars, {len(pg.get_images())} img | {t[:80] or "*** VACIA ***"}')
d[0].get_pixmap(dpi=110).save('p1.png')   # rasterizar para mirarla
```

Qué chequear siempre:
1. **Ninguna hoja vacía** (`len(texto) == 0` la delata).
2. **Cantidad de hojas** razonable (el bug de la hoja en blanco daba 9; el fix, 5).
3. **Los totales cierran** con los KPIs de pantalla.
4. **Los logos están** (`pg.get_images()` ≥ 2 en la hoja 1).
5. A 1440 y a **1088×614** (ver skill `responsive-nortur`).

⚠ `popup.pdf()` **NO reproduce el encabezado/pie de Chrome** (esa API no los dibuja por
defecto) → el fix de `margin: 0` **no se puede verificar por ahí**, hay que mirarlo en el
navegador real.

⚠ Para verificar el COLOR de un texto usar `getComputedStyle`, **nunca el ojo sobre un PNG
ampliado**: el antialiasing de subpíxel LCD en fuentes chicas (10px) produce fringing azul/naranja
que parece un bug de CSS y no lo es.

---

## 6. Excel "que no se pueda modificar"

`ClosedXML` soporta `worksheet.Protect()`. **Decir la verdad al usuario**: es candado de cajón,
se saca en segundos con herramientas gratuitas online. Si el objetivo real es que no se pueda
tocar, **el PDF ya lo cumple solo**.

Patrón recomendado (el que ya usa Liquidación): ofrecer **las dos salidas**, cada una con su rol
— el **Excel para trabajar** (pivotear, filtrar) y el **PDF para mostrar/archivar/enviar**.

---

## Referencias vivas

- `MetroCarSysBlazor/Components/Pages/ViajesPorChofer.razor` — panel con botón PDF completo
  (encabezado de marca, KPIs, modo PDF del gráfico, tabla con total).
- `MetroCarSysBlazor/Components/Shared/LiquidacionResumenDialog.razor` — comprobante vertical.
- `MetroCarSysBlazor/wwwroot/descarga.js` → `window.imprimirElemento`.
- `MetroCarSysBlazor/wwwroot/app.css` → bloque "EXPORTAR A PDF" y su `@media print`.
- Memoria `imprimir-pdf-analisis` — análisis de rutas, trade-offs e historial de bugs.
