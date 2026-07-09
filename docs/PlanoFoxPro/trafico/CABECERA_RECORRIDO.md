# Cabeceras - Recorridos (`cabecera_recorrido.scx` + `_abm.scx`)

> **Menú FoxPro:** Tráfico → Cabeceras - Recorridos (`ON SELECTION BAR 5 OF trafico openForm("cabecera_recorrido")`).
> **Extraído:** 05/07/2026. **Migrado:** solo lectura + andamiaje ABM (`CabecerasRecorridos.razor`, `/cabeceras-recorridos`, permiso `'T'`).

## Concepto

Catálogo de **cabeceras** (recorridos maestros de las líneas): un código + 3 descripciones +
un texto largo con el itinerario detallado y las paradas intermedias. Lo usa Tráfico para los
servicios tipo cabecera (`origen='P'`, ver `CABECERA_KM`/`CABECERA_SERV`).

## Tabla `cabecera` (187 filas, server viejo)

| Columna | Tipo (réplica) | Significado |
| --- | --- | --- |
| `id` | int (**NO identity**) | PK física; alta = `MAX(id)+1` |
| `codigo` | nvarchar(20) | PK lógica (AE02, EE41, …); tipeada por el usuario |
| `nombre` | nvarchar(140) | 1ª descripción (ej. "BARRIO AERONAUTICO") |
| `nombre1` | nvarchar(140) | 2ª descripción (ej. horario "LUNES A LUNES ENTRADA 05.15 HS") |
| `nombre2` | nvarchar(140) | 3ª descripción (ej. "ESTE SERVICIO SE CUMPLE CON 1 BUS…") |
| `recorrido` | nvarchar(MAX) | Itinerario completo + paradas intermedias (editbox largo) |
| `_deleted` | bit | metadata de la réplica |

> 🐛 **Trampa:** la tabla **NO tiene `f_delete`/`f_create`/`f_modify`**. En el FoxPro la baja
> es **FÍSICA** (`DELETE From cabecera Where codigo = cCodigo`), no lógica. El ABM Blazor
> respeta esto (`BajaCabeceraAsync` = `DELETE`). En la réplica el `nombre_abm` mapea así:
> ABM `nombre1`→`nombre`, `nombre2`→`nombre1`, `nombre3`→`nombre2` (el form desplaza los nombres).

## Lista (`cabecera_recorrido.scx` → arma_grid)

Grilla ordenada por `nombre`, 4 columnas (Código + 3 descripciones). Filtro de texto
(`Text1.InteractiveChange` → LOCATE por nombre). Al pie, un editbox de **recorrido** de solo
lectura con botones Editar/Grabar (edición inline del campo recorrido — en Blazor va en la ficha).
Botones Agregar/Eliminar/Modificar/Consulta → abren `cabecera_recorrido_abm.scx` con el modo.

## ABM (`cabecera_recorrido_abm.scx`)

- **alta** (`audita_carga`): valida `codigo` no vacío + 1ª descripción + recorrido; valida
  `codigo` duplicado (SELECT); `INSERT INTO cabecera (codigo,nombre,nombre1,nombre2,recorrido)`.
- **modifica**: `codigo` no editable; `UPDATE ... SET nombre, nombre1, nombre2, recorrido`.
- **baja**: `MessageBox` de confirmación → **`DELETE From cabecera`** (física).
- **consulta**: campos deshabilitados (solo lectura).

## Migración Blazor

- `ReportService.GetCabecerasAsync()` → `CabeceraRow`.
- `AbmService.Alta/Modifica/BajaCabeceraAsync` (andamiaje, **baja física**), `CabeceraInput`.
- `CabecerasRecorridos.razor` (lista, calca `Fleteros.razor`) + `CabeceraEditorDialog.razor`
  (4 modos, recorrido = editbox largo con wrap; regla de oro de fichas: no recortar).
- Flag `AbmFeatureFlags.CabecerasAbmActivo` (hoy `false`).
- Validado: 187 filas en la grilla (idéntico a `SELECT COUNT(*)`).
