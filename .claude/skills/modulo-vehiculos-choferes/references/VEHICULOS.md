# Vehículos - Flota — `vehiculo.scx` + `vehiculo_abm.scx`

> ✅ **Migrado solo lectura (15/06/2026):** `Components/Pages/Vehiculos.razor` (`/vehiculos`)
> + `VehiculoDetalleDialog.razor` (6 pestañas). Métodos `GetVehiculosAsync` /
> `GetVehiculoDetalleAsync` en `ReportService`. Falta el ABM (escritura). Permiso módulo `'V'`.

- **Tabla:** `vehiculo` (406 activos, ~80 columnas). PK `id_vehicul` (en form `id_vehiculo`).
  Patente = **`dominio`** (no existe `patente`).
- **Relacionadas:** `vehiculo_dueno` (235, dueños + `porcentaje`), `vehiculo_permiso`
  (permisos por vehículo), `vehiculo_tipo` (categoría), `fletero` (a quién pertenece),
  `vehiculo_repuesto`/cubiertas (pestañas), `vehiculo_combustible*` (módulo Combustible).

## Lista (`vehiculo.scx`)

- Filtros: combo **Fletero**, check **Ver Activos** (`activo`), check **Ver Flota Propia**
  (`uso = 'PROPIO'`). Orden por Interno o Dominio (optiongroup).
- **Egresado/baja = `f_delete` cargada O `!activo`** → fila amarilla (dynamicbackcolor
  `IIF(!EMPTY(f_delete) OR !activo, amarillo, blanco)`). OJO: doble condición, distinto de chofer.
- Botones: Agregar/Eliminar/Modificar (permisos 2/3/4), Exportar Excel, Log, Impresión, Salir.

## ABM (`vehiculo_abm.scx`) — 6 pestañas

> ⚠️ **El orden VISUAL de las pestañas ≠ el número de `page` interno** del pageframe
> (verificado en el dump del form, 15/06/2026). Orden visual ↔ page interno:

| # visual | Pestaña | Page interno | Fuente de datos |
| --- | --- | --- | --- |
| 1 | **Datos Vehículo** | `page1` | tabla `vehiculo` (todos los campos) |
| 2 | **Permisos** | `page6` | `vehiculo_permiso` JOIN `permiso` (grid1) |
| 3 | **Dueños** | `page2` | `vehiculo_dueno` + `dueno` (grid1) |
| 4 | **Cubiertas** | `page3` | columnas `r1..r7` de `vehiculo` (¡no es una tabla!) |
| 5 | **Tarjetas** | `page4` | columnas `ypf_*`/`esso_*` de `vehiculo` |
| 6 | **Repuestos** | `page5` | `vehiculo_repuesto` JOIN `taller_articulo` |

1. **Datos Vehículo** — dominio, marca_y_modelo (`marca_y_mo`), modelo (año, int), interno,
   fletero, cronograma, pax, tipo (`id_vehicu2` = tipo), color, chasis, motor, uso PROPIO/
   CONTRATADO, seguros (poliza_nom/nro/vto), CNRT (estado_cnr), radicación, tacógrafo
   (`tacografo_`=marca, `tacografo2`=nro), habilitación (`habilitaci`/`habilitac2`),
   verificación VTV (`verificaci`/`verificac2`), matafuegos (`vencimient`), puerto aeropuerto
   (`puerto_aeo`), nextel, GPS (`gps_activo` nvarchar(1)), comodidades (bano/bar/video/wifi),
   autonomía/tanque, prom. consumo (`d_cons_pro`/`h_cons_pro`). **Conductor logoneado**
   (`nombre_cho`) y **estado operativo** (`estado`) los pisa Tráfico, no el ABM.
2. **Permisos** — `vehiculo_permiso` (JOIN `permiso` por `id_permiso`): Código, Nombre,
   Nº Permiso (`nro_permis`), F.Venc, F.Baja. 680 filas en la réplica.
3. **Dueños** — grilla `vehiculo_dueno` (`id_dueno`, `porcentaje`); nombre desde tabla `dueno`.
   Suma debe dar 100%. 235 filas.
4. **Cubiertas** — **NO es una tabla**: son las 7 columnas `r1..r7` de `vehiculo` (un nro de
   serie de cubierta por posición, validadas por `valida_cubierta`, 7 posiciones).
5. **Tarjetas** — combustible: YPF (`ypf_tar`/`ypf_venc`/`ypf_pin`) y ESSO (`esso_*`),
   campos de la propia tabla `vehiculo`.
6. **Repuestos** — `vehiculo_repuesto` JOIN `taller_articulo`. **Vacía en la réplica** (0 filas).

## Validaciones ABM

Dominio, modelo, marca, interno, fletero, cronograma, pax, tipo, **vto póliza de seguro**,
**vto técnica (VTV)**. Regla operativa: **no se puede desactivar/inhabilitar un vehículo con
conductor logoneado** (Tráfico) — hay que deslogonearlo primero.

## Reglas no obvias

- `estado` (ASIGNADO/LIBERADO/TALLER), `id_chofer`, `id_viaje`, `hs_inicio/fin`, `nombre_cho`
  los **pisa Tráfico** en tiempo real — NO son del ABM, reflejan el estado del despacho.
- `franco` (bit), `id_zona` también los maneja Tráfico.
- Columnas de combustible (`ult_carga_`, `d_cons_pro`, `h_cons_pro`, `litro_tanq`) son del
  módulo Combustible (ver `modulo-combustible`).
- Vencimientos para **Agenda de Vencimientos**: `verificac2` (VTV) y `vencimient` (matafuegos),
  solo `uso = 'PROPIO'`.
