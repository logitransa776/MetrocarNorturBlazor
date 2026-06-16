# Tipo de Vehículos — `vehiculo_tipo.scx` + `vehiculo_tipo_abm.scx`

> Pendiente. **El catálogo más simple del módulo → candidato ideal para el PRIMER ABM real**
> de escritura del proyecto (junto con `zona` que sugiere `abm-metrocar`).

- **Tabla:** `vehiculo_tipo` (6 filas — las 6 categorías de la flota). PK `id_vehicul` (texto).
  OJO: la PK se llama `id_vehicul` igual que en `vehiculo`, pero es la PK del tipo.

## Campos (→ columna SQL)

| Control | Columna | Notas |
|---|---|---|
| Código | `id_vehicul` | PK, inmutable en modifica |
| Nombre | `nombre` | obligatorio |
| Pax | `pax` (int) | capacidad de pasajeros |
| Consumo mín / máx | `consumo_mi` / `consumo_ma` (decimal) | rango l/100km esperado |
| (tipo 2) | `id_vehicu2` (1 char) | subclasificación |
| Vende | `vende` (bit) | si se ofrece en cotización |
| Dir. dibujo | `dir_dibujo` | ruta de imagen del tipo |
| Auditoría | `f_create`/`f_modify`/`f_delete` | baja lógica |

## Lista / ABM

- Lista `vehiculo_tipo.scx`: grilla simple, egresado (`f_delete`) amarillo, botones 2/3/4.
- ABM `vehiculo_tipo_abm.scx`: alta (anti-duplicado PK), modifica (PK no editable), baja
  (`f_delete = DATE()`). Patrón ABM estándar (ver `abm-metrocar`).

## Por qué es buen primer ABM

6 filas, 1 tabla, sin relaciones complejas, sin lógica operativa de Tráfico. Migrarlo deja la
plantilla de **escritura** (AbmService + MudDialog editable + permisos 2/3/4 + bloqueo en
FoxPro + apagado de sync) para replicar en el resto del módulo.
