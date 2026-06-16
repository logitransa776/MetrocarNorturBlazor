# Apercibimientos / Sanciones — `chofer_sancion.scx` + `_abm` + `_motivo`

> Pendiente. **Tablas replicadas pero VACÍAS (0 filas)** → función no usada en producción hoy.
> Migrar solo si el cliente lo pide.

- **Tablas:** `chofer_sancion` (0), `chofer_sancion_motivo` (0, catálogo de motivos).

## `chofer_sancion`

Columnas: `fecha`, `f_sancion`, `id_chofer`, `id_vehicul`, `id_viaje`, `falta` (memo),
`descargo` (memo), `id_sancion`, `sancion`, `interno`.

## Lista (`chofer_sancion.scx`) — "Apercibimientos / Sanciones"

- Filtro por rango de fechas (`f_sancion` BETWEEN) + por chofer. Orden por fecha o chofer.
- Muestra `falta` y `descargo` en cuadros de texto al seleccionar fila.
- Combo chofer: `SELECT nombre, id_chofer FROM chofer WHERE EMPTY(f_delete)`.

## Motivos (`chofer_sancion_motivo.scx`)

Catálogo simple: `nombre`. Patrón ABM estándar.

## Mapeo a Blazor

Baja prioridad (sin datos). Si se migra: lista filtrable + ficha con falta/descargo + ABM del
catálogo de motivos. Relacionable con un viaje (`id_viaje`) y un vehículo.
