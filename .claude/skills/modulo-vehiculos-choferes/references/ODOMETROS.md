# Odómetros — `vehiculo_km.scx` ("Control de Odómetros")

> Pendiente. Es un **informe/registro de lecturas** (no un ABM clásico de catálogo).

- **Tabla:** `vehiculo_km` (10.533 filas — transaccional con datos reales).
- **Columnas:** `dominio`, `interno`, `tipo_mov`, `ano_y_mes` (AAAAMM, 6 char), `fecha`,
  `f_carga`, `km_inicio`, `km_fin`, `km_recorri`, `odometro`, `u_create`, `u_modify`.
- **km recorridos** = `km_fin - km_inicio` (la grilla lo calcula al vuelo).

## Lista / filtros (`vehiculo_km.scx`)

- Filtro por **Dominio** (con buscador F5) + rango de fechas (`f_carga` BETWEEN dFecha/hFecha).
- Optiongroup "por Vehículos" / (otro). Orden `dominio, f_carga DESC`.
- El buscador de dominio sale de `SELECT dominio FROM vehiculo WHERE activo AND uso='PROPIO'`.

## Mapeo a Blazor

Informe simple: tabla filtrable por dominio + fechas, columna km recorridos calculada.
Relacionado con consumos de Combustible (l/100km usa estos km) → ver `modulo-combustible`.
