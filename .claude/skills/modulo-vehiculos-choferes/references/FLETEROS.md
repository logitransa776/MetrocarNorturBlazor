# Fleteros — `fletero.scx` + `fletero_abm.scx`

> Pendiente. Catálogo chico, buen ABM temprano. **OJO: compartido con Facturación**
> (mismo form en el menú Facturación). Antes de migrar su ABM, coordinar con
> `modulo-facturacion-liquidacion` para no tener dos dueños de la misma tabla (regla strangler).

- **Tabla:** `fletero` (28 activos). PK `id_contrat` (en form `id_contratado`).
- **Qué es:** transportista contratado / razón social que aporta vehículos y choferes. Es la
  contraparte de `uso = 'CONTRATADO'` en `vehiculo` y del campo `fletero` en `chofer`/`vehiculo`.

## Lista (`fletero.scx`)

- `SELECT * FROM fletero ORDER BY orden, nombre`. Egresado (`f_delete`) en amarillo.
- Botones Agregar/Eliminar/Modificar (permisos 2/3/4) + Salir.

## ABM (`fletero_abm.scx`)

Campos (→ columna SQL real):
- `id_contratado` → `id_contrat` (PK, inmutable en modifica)
- `razon_social` → `razon_soci`, `nombre`, `orden` (orden de aparición en combos)
- `id_lista_precio` → `id_lista_p`, `id_lista_personal` → `id_lista_2` (tarifarios de PAGO)
- `modo_liq` (modo de liquidación, 1 char), `fc_prefere` (preferencia facturación)
- Datos: `cuit`, `tipo_resp`, `domicilio`, `localidad`, `postal`, `provincia`, `telefono`,
  `celular`, `email`, `contacto`, `diagrama` (bit)
- Auditoría: `f_create` / `f_modify` / `f_delete` (baja lógica).

## Reglas no obvias

- Los `id_lista_p` / `id_lista_2` son tarifarios de **pago al fletero** (no de venta) — viven
  en el módulo Facturación/Liquidación.
- El combo "Fletero" de Choferes y de Vehículos sale de `SELECT id_contratado FROM fletero`.
