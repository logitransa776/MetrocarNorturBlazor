---
name: foxpro-extract
description: Extraer y entender la lógica del sistema FoxPro Metrocar (C:\MetroCarSys) — forms .scx/.sct, reportes .frx, menús .mpr, programas .prg. Usar SIEMPRE que haya que saber qué hace una pantalla, form, botón o reporte del Metrocar viejo; antes de migrar cualquier informe o ABM a Blazor; cuando el usuario pasa una captura/foto del sistema FoxPro; o cuando pregunta dónde está la lógica de algo del sistema viejo. Incluye el script lector de .scx listo para ejecutar (scripts/scx_dump.py) — no reescribirlo.
---

# FoxPro Extract — leer la lógica del Metrocar viejo

El sistema productivo está en **`C:\MetroCarSys`**. Antes de migrar cualquier pantalla,
extraer y documentar su lógica real — no adivinar desde la base de datos si el form existe.

> ⚠️ El `metrocar.exe` productivo está **más actualizado** que parte del fuente en disco.
> Si algo no coincide con lo que el usuario ve en pantalla, avisarle: puede ser código viejo.

## Ubicaciones

| Qué | Dónde | Cantidad |
| --- | --- | --- |
| Forms | `C:\MetroCarSys\Forms\*.scx` (+ su `.sct`) | 378 |
| Programas | `C:\MetroCarSys\Progs\*.prg` (texto plano) | — |
| Reportes | `C:\MetroCarSys\Reports\*.frx` (+ `.frt`) | ~40 |
| Menús | `C:\MetroCarSys\Menus\*.mpr` (texto plano) | — |
| DBF originales | `C:\MetroCarSys\Nortur` (cp1252) | 108 tablas |
| Funciones globales | `C:\MetroCarSys\Progs\funcion.prg` | — |

## Workflow estándar (en orden)

1. **Ubicar el form desde el menú**: grep `OpenForm` en `C:\MetroCarSys\Menus\MENU_PRINCIPAL.MPR`
   (estructura: `DEFINE PAD/POPUP/BAR` + `ON SELECTION BAR n OF popup OpenForm("nombre_form")`).
2. **Dumpear el form** con el script incluido (NO reescribir el lector — ya existe):

   ```bash
   python .claude/skills/foxpro-extract/scripts/scx_dump.py \
       "C:\MetroCarSys\Forms\<form>.scx" "C:\MetroCarSys\Forms\<form>.SCT" \
       "%TEMP%\<form>_dump.txt"
   ```

   Salida: un bloque por objeto con `OBJNAME | CLASS | PARENT`, `PROPERTIES` y `METHODS`
   (el código FoxPro vive en METHODS: `PROCEDURE Click`, `Init`, `arma_grid`, etc.).
   Ojo con mayúsculas del `.SCT` — verificar el nombre real del archivo.
3. **Leer METHODS** del form y de cada botón. Buscar también las funciones globales que
   llama (`grep` en `funcion.prg`).
4. **Documentar en `docs/logica-foxpro/<NOMBRE>.md`** antes de codear el equivalente Blazor:
   tablas tocadas, validaciones, estados, colores, reglas no obvias. Seguir el estilo de
   `TRAFICO_ZOOM.md` y `TRAFICO2_FILTROS.md`.

## Formato binario (por si el script falla o hay que extender a .frx)

- `.scx`/`.frx`/`.sct` son **tablas DBF de VFP** con el código en campos **memo**.
- Puntero al memo: **entero de 4 bytes little-endian** en el campo del registro.
- En el `.sct`: **block size = 1** (el puntero es offset directo en bytes).
- Bloque memo: 4 bytes tipo + 4 bytes longitud (**big-endian**) + contenido (**cp1252**).
- Campos útiles: `METHODS` (código), `PROPERTIES`, `OBJNAME`, `CLASS`, `PARENT`.
- Los `.frx` son DBF también (campos distintos: `EXPR`, `TAG`, `TAG2`); el mismo enfoque sirve.

## Convenciones del código Metrocar (aparecen en todos lados)

- **Patrón ABM de dos forms**: `<entidad>.scx` (lista con grilla + botones Agregar/Modificar/
  Eliminar) abre `<entidad>_abm.scx` con parámetro `"alta"` / `"baja"` / `"modifica"`.
  Hay **73 forms `*_abm.scx`** con este patrón. Detalle completo → skill `abm-metrocar`.
- **Permisos por dígito** en la variable `cNivel` del usuario: `"2" $ cNivel` = puede alta,
  `"3"` = modifica, `"4"` = baja. Sin permiso → `cartel("sin_permiso")`.
- **Baja lógica**: `f_delete = DATE()` (nunca DELETE físico). `f_create` en alta,
  `f_modify` en modificación. Filas con `f_delete` se pintan **amarillas** en las grillas.
- **Reposicionamiento**: variable pública `c<Entidad>GoTo` — tras grabar, la grilla hace
  `LOCATE FOR` ese código.
- **Colores de grilla**: funciones `grid_color_*` en `funcion.prg` (ej: `grid_color_viaje`,
  `grid_color_vehiculo` en funcion.prg:952) usadas vía `SetAll("dynamicbackcolor", ...)`.
- **Réplica SQL trunca nombres a 10 chars**: `cronogramacbio` → `cronogram2`,
  `id_vehiculo_tipo` → `id_vehicu2`, `nombre_guia` → `nombre_gui`, `id_contratado` → `id_contrat`.
  Siempre verificar el nombre real con una query a `sys.columns` antes de escribir SQL.

## Forms ya documentados (no re-extraer)

| Form | Doc |
| --- | --- |
| `trafico_zoom.scx` | `docs/logica-foxpro/TRAFICO_ZOOM.md` |
| `trafico2.scx` (toolbar, filtros, Cxl, panel Buses) | `docs/logica-foxpro/TRAFICO2_FILTROS.md` |
