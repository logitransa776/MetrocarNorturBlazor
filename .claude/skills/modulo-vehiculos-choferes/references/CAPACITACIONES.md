# Capacitaciones / Cursos — `chofer_curso_consulta.scx` + `chofer_curso_arma.scx`

> Pendiente. Registra qué cursos hizo cada chofer.

- **Tablas:** `chofer_curso` (417 — cursos hechos por chofer), `chofer_curso_parametro`
  (13 — catálogo de cursos), `chofer_curso_arma` no tiene tabla (es proceso de armado).

## `chofer_curso` (hechos)

`id_curso`, `id_chofer`, `fecha`, `lugar`. JOIN a `chofer_curso_parametro` (nombre del curso)
y `chofer` (nombre del conductor).

## `chofer_curso_parametro` (catálogo)

`id_curso`, `nombre`, `duracion`, `comentario`. Catálogo de los cursos disponibles.

## Consulta (`chofer_curso_consulta.scx`) — "Consulta de Stock"

Query base (réplica directa para Blazor):
```sql
SELECT b.nombre AS modulo, c.nombre AS conductor, a.fecha, a.lugar, a.id
FROM chofer_curso a
JOIN chofer_curso_parametro b ON a.id_curso = b.id_curso
JOIN chofer c ON a.id_chofer = c.id
ORDER BY b.nombre, c.nombre
```
(filtrable por `a.id_chofer`). Botón "Eliminar Curso".

## Armado (`chofer_curso_arma.scx`)

Selección múltiple de choferes (list2) + curso → inserta en `chofer_curso` para cada chofer
seleccionado (asigna un curso a un grupo de una vez). Toma `nombre, tdoc, ndoc, f_nac` del chofer.

## Mapeo a Blazor

Consulta = informe con JOIN listo (arriba). Armado = futuro: selector múltiple de choferes +
combo de curso + fecha/lugar → inserción masiva (ABM de escritura, ver `abm-metrocar`).
