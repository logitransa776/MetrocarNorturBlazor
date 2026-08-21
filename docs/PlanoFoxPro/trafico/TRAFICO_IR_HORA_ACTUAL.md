# `ir_hora_actual` — posicionamiento automático de la Planilla en la hora actual

**Form:** `trafico2.scx` · **Extraído:** 29/07/2026 · **Migrado:** ✅ `PlanillaTrafico.razor`

Al abrir la Planilla de Tráfico, el Metrocar no deja la grilla arriba de todo (el primer
servicio de un día real arranca ~03:40). Se planta solo en el servicio de la hora. Este doc
es la extracción de esa lógica y la decisión de cómo se portó a Buslink.

---

## El procedimiento, completo

```foxpro
PROCEDURE ir_hora_actual
Select cursorViajeReserva
Go Top
Do While !Eof()
    If cursorViajeReserva.hs_inicio >= Datetime()
        Exit
    Endif
    Skip
Enddo
Thisform.grid1.Refresh
Thisform.grid1.SetFocus
ENDPROC
```

Recorre el cursor desde el tope y para en **el primer servicio cuya `hs_inicio` todavía no
pasó**. No hay parámetros ni ventana configurable: compara contra `Datetime()` exacto.

Después llama a `grid1.Refresh`. VFP desplaza la grilla lo mínimo necesario para que el
registro actual quede visible y, como el puntero venía bajando desde el tope, **la fila
aterriza pegada al renglón de abajo**. Todo lo que se ve arriba ya arrancó.

> ⚠️ **El FoxPro NO resta dos horas.** Si el operador dice que "ve las dos horas
> anteriores", eso es consecuencia del alto de la grilla, no de la lógica: `Grid1.Height`
> 493 ÷ `RowHeight` 17 = 28 filas de diseño (~46 maximizada, `WindowState = 2`), y a un
> promedio de 14 servicios/hora esas filas son ~2 h de historia.

---

## Cuándo se dispara

No siempre. `aplica_filtro(lpFiltroActivo)` prende o apaga una bandera local `lHoraActual`
según la rama, y recién al final:

```foxpro
If lOkey
    Thisform.arma_grid_viaje(lpFiltroActivo)
    If lHoraActual
        Thisform.ir_hora_actual()
    Endif
Endif
```

| Acción en el Metrocar | Camino | ¿Salta? |
| --- | --- | --- |
| Abrir la Planilla | `Init` → `cFiltroActivo = "REFRESH"` → `aplica_filtro(...)` | ✅ |
| **F3** · botón `Ref` | `bRefresh.Click` → fecha = `Date()` → `aplica_filtro("REFRESH")` | ✅ |
| Filtros `CRONOGRAMA`, `_CRONOGRAMACBIO`, `CRONOGRAMA_SELECCION`, `HS_INICIO_SELECCION`, `CLIENTE_SELECCION*` | `lHoraActual = .T.` | ✅ |
| Normalizar cronogramas | `aplica_filtro("REFRESH")` | ✅ |
| `<<` día anterior · `>>` día siguiente | `arma_grid_viaje(cFiltroActivo)` **directo**, saltea `aplica_filtro` | ❌ |
| Botón `Cxl` (cancelados) | `aplica_filtro("CANCELADO")`, `lHoraActual = .F.` | ❌ |
| Filtro por chofer | `aplica_filtro("CHOFERES")`, `lHoraActual = .F.` | ❌ |
| `VARIO_RUTA` / `VARIO_DIA` / `RESERVA_SELECCION` | `lHoraActual = .F.` | ❌ |

> Los botones de día están **cruzados en el fuente**: el objeto llamado `bSiguiente` resta un
> día y el llamado `bAnterior` suma uno. Los `Caption`/`ToolTipText` sí son correctos. No
> copiar los nombres.

---

## Cómo se portó a Buslink (29/07/2026)

**Decisión del usuario: scroll CENTRADO**, no la posición del FoxPro.

Se simuló contra un día real (miércoles 15/07/2026, 323 servicios) comparando cuatro
variantes. La conclusión que definió la elección:

| Modo | Historia arriba | Servicios por venir abajo |
| --- | --- | --- |
| Metrocar fiel («ahora» al pie) | 35 min a 4 h según la hora | **0 a toda hora** (por construcción) |
| Tope = ahora − 2 h | 2 h fijas | 0, y en el pico pierde «ahora» de pantalla |
| Híbrido (`max` de los dos) | ≤ 2 h | 0 en todo el pico |
| **Centrado** ✅ | se ajusta a la densidad | **15 siempre** |

El centrado es el único que muestra a la vez lo que está corriendo y lo que viene, y su
ventana de tiempo se adapta sola: en el pico de las 07:45 son ~30 min para cada lado; a las
20:00, con el día flojo, 1 h 45 hacia atrás y 2 h 15 hacia adelante. **Su punto flojo:** a
las 15:20 (47 servicios en las 2 h previas) las 16 filas de arriba se consumen en 5 minutos.

Se descartó una variante «centrado con piso de 1 h de historia»: fuerza tanto hacia arriba en
las horas densas que se queda sin servicios futuros y pierde «ahora».

### Implementación

- `PlanillaTrafico.razor` → `PosicionarEnHoraActual()`: `_visibles.FindIndex(hs_inicio >= Now)`,
  con `idx = Count - 1` si no hay ninguno (equivalente al `Eof()` del FoxPro). Se dispara desde
  `OnAfterRenderAsync` — la grilla tiene que estar en el DOM para medir el alto real.
- `descarga.js` → `traficoIrHoraActual(...)`: centra en **dos pasos** (ver la trampa de abajo).
  Paso 1, `scrollTop = idxAhora * itemSize` — sin usar el alto, solo para que `<Virtualize>`
  renderice ese tramo. Paso 2, cuando la fila aparece en el DOM, se la centra con su
  `getBoundingClientRect()` real.
- `TraficoFilaRow.razor` emite `data-nro="@Nro"` en el `<tr>` — es cómo el JS encuentra la fila.
- **Disparadores:** entrada a la pantalla (`OnInitializedAsync`) y botón **HOY** (`IrHoy`),
  que es el equivalente de `bRefresh.Click`. Las flechas de día no reposicionan, igual que el
  FoxPro. **El auto-refresh de 60 s tampoco** — le arrancaría el scroll al operador.
- El reloj es `DateTime.Now` del **servidor** (Blazor Server), el mismo que ya usa
  `PlanillaTraficoRow.EstadoDisplay` para derivar EN CURSO: scroll y colores siempre coinciden.

### 🔴 Trampa: NO centrar por aritmética de píxeles (29/07/2026)

La primera versión calculaba todo de una: `scrollTop = (idxAhora − filas/2) * itemSize`, con
`filas = Math.floor(wrap.clientHeight / itemSize)`. **Quedó 18 filas más arriba de lo debido.**

Medido contra el día 29/07/2026 (338 servicios, `Now` = 13:23): el índice de la hora era
correcto —la primera fila con `hs_inicio >= 13:23` es la #170 (13:25), verificado por SQL—
pero la grilla arrancó en la #137, con la #170 a **33 filas** del tope. Para que `floor(filas/2)`
diera 33, `clientHeight` tuvo que valer ~1320 px, contra los ~620 px reales del área visible.

**Causa:** en el render inicial la cadena flex de `body.planilla-fixed` todavía no se asentó
(la clase la pone el propio `OnAfterRenderAsync` vía `bloquearScrollVentana`), así que el
contenedor mide mucho más de lo que va a medir un instante después. Esperar un
`requestAnimationFrame` **no alcanza**.

**Regla:** cualquier posicionamiento que necesite "media pantalla" tiene que medir la
geometría REAL de los elementos ya renderizados, no derivarla de `clientHeight` en el primer
render. `traficoScrollFila` (navegación con flechas) no sufre esto porque corre mucho después,
con el layout ya estable, y porque solo compara bordes en vez de calcular un centro.

### No migrado

La línea visual «AHORA» que separa lo que arrancó de lo que viene **no existe en el Metrocar**
y quedó sin implementar — es un agregado a decidir aparte.
