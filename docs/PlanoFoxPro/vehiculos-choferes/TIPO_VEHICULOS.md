# Tipo de Vehículos — `vehiculo_tipo.scx` + `vehiculo_tipo_abm.scx`

> **Migrado a Blazor (solo lectura + andamiaje ABM) — 05/07/2026.**
> Lista `Components/Pages/TiposVehiculo.razor` (`/tipos-vehiculo`) + ficha/editor
> `Components/Shared/TipoVehiculoEditorDialog.razor`. Permiso `'V'`, menú **Vehículos y Choferes
> → Tipo de Vehículos**. La ESCRITURA está construida en `AbmService` (`AltaTipoVehiculoAsync` /
> `ModificaTipoVehiculoAsync` / `BajaTipoVehiculoAsync`) pero **deshabilitada** (`_abmActivo=false`).
> **Es el catálogo más chico del módulo (6 filas) → primer ABM real candidato.**

## Qué es

Catálogo de categorías de la flota: BUS, VAN, MINI (MINI BUS), HIACE (HIACE TOYOTA), AUTO, KANGOO.
Define capacidad de pasajeros y rango de consumo esperado. `viaje.id_vehicul` guarda el TIPO
(BUS/VAN/…), no el vehículo — ver `[[viaje-campos-vehiculo-cruzados]]`.

## Lista (`vehiculo_tipo.scx`)

Grilla simple `ORDER BY id_vehicul`, egresado (`f_delete`) amarillo, botones 2/3/4.

## ABM (`vehiculo_tipo_abm.scx`) — lógica real extraída

Validaciones (modo alta/modifica):
```foxpro
If Empty(nombre) → "No se ha cargado el nombre"
If Empty(codigo) → "No se ha cargado el codigo"
If Empty(pax)    → "No se ha cargado la cantidad de pasajeros"
IF consumo_min = 0 OR consumo_max = 0    → "No se el rango de consumo promedio"
IF consumo_min >= consumo_max            → "Problemas con el rango de consumo promedio"
```
- **Alta:** valida PK (`codigo`=`id_vehicul`) no duplicada → `INSERT` con `f_create=DATE()`.
- **Modifica:** la PK (`pax` y el código) queda deshabilitada; `UPDATE … f_modify=DATE()`.
- **Baja:** `UPDATE … f_delete=DATE()`. Nunca borra físico.

## Campos (→ columna SQL real)

| Control       | Columna SQL   | Notas |
|---------------|---------------|-------|
| Código        | `id_vehicul`  | PK lógica (nvarchar 15), inmutable en modifica. OJO: mismo nombre que `vehiculo.id_vehicul` pero es la PK del TIPO |
| Nombre        | `nombre`      | obligatorio (nvarchar 30) |
| Pax           | `pax` (int)   | capacidad de pasajeros |
| Subtipo       | `id_vehicu2`  | 1 char (B/K/H/T — subclasificación de combustible) |
| Consumo mín/máx | `consumo_mi` / `consumo_ma` (decimal) | rango l/100km |
| Vende         | `vende` (bit) | se ofrece en cotización |
| Dir. dibujo   | `dir_dibujo`  | ruta de imagen del tipo |
| PK física     | `id` (int, **NO identity**) | alta = `MAX(id)+1` |
| Auditoría     | `f_create`/`f_modify`/`f_delete` | baja lógica |

## Decisiones vs FoxPro (Blazor)

- **Rango de consumo NO obligatorio en la validación Blazor** (el FoxPro exige `min≠0`, `max≠0`,
  `min<max`). Se relajó porque en la base real hay tipos SIN consumo cargado (AUTO y KANGOO tienen
  `consumo_mi`/`consumo_ma` NULL) — endurecerlo rechazaría datos válidos existentes. Blazor solo
  chequea `max >= min` cuando ambos vienen. Anotado en `AbmService.ValidarTipoVehiculo`.
- **Andamiaje:** botonera de escritura visible pero deshabilitada; "Ver ficha" activo. El día del
  corte a Buslink: `_abmActivo=true` + bloquear ABM en FoxPro + apagar sync de `vehiculo_tipo`.
- **Excel** de la grilla (código, nombre, pax, subtipo, consumos, vende, baja).

## Validación (05/07/2026)

6 tipos en la réplica, todos activos. Grilla, ficha "ver" (AUTO) y Excel verificados por captura.
Datos idénticos a `SELECT * FROM vehiculo_tipo` (BUS pax 46, HIACE 5, MINI 24, VAN 19, AUTO 3,
KANGOO 3; consumos NULL en AUTO y KANGOO se muestran "—").
