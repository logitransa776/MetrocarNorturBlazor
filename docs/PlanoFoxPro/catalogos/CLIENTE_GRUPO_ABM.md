# Lógica FoxPro — Grupos de Clientes (`cliente_grupo.scx` + `cliente_grupo_abm.scx`)

> Menú: **Reservas → Grupos**. Relacionados: `cliente_grupo_view.scx` (viajes del grupo),
> `cliente_grupo_factura*.scx` (facturación por grupo), `cliente_grupo_masivo.scx`.
> Extraído del binario con `foxpro-extract` (12/06/2026). 11.272 grupos.

---

## Concepto

Un **grupo** agrupa varios viajes de un cliente para tratarlos como unidad (contingente /
evento / tour de varios días) y **facturarlos juntos**. Tabla `cliente_grupo`:
`id` (autoinc), `id_cliente`, `nombre` (la dupla id_cliente+nombre es la clave lógica),
`f_grupo_in` (inicio), `f_grupo_fi` (fin), **`f_grupo_fc` (fecha de facturación — si tiene
valor, el grupo está CERRADO)**, `f_delete`, `lote`, `liquidacio`, `f_llegada`, `f_partida`.

Los viajes referencian el grupo de dos maneras (ambas conviven):
- `viaje.id_grupo` (FK al autoinc) — la moderna;
- `viaje.grupo` (nombre desnormalizado) + `viaje.f_grupo_fi` — la histórica, usada en los
  WHERE del FoxPro (`id_cliente + grupo = …`).

Los grupos **se crean normalmente desde la carga de reservas** ("Grupo Nuevo" en
`reserva_transportacion_con_adicional` — ver `../reservas/RESERVA_TRANSPORTACION.md`); esta pantalla
es para administrarlos.

---

## Lista (`cliente_grupo.scx`)

- Filtro por combo: **Grupos No finalizados** (`f_grupo_fin >= hoy`, default) /
  **Finalizados** (`< hoy`) / **Todos**.
- Orden por optiongroup: por **cliente** (razón social, nombre) o por **grupo** (nombre).
  La búsqueda incremental usa el campo del orden activo.
- Grilla: código cliente, razón social, nombre del grupo, F. Fin Grupo, **F. Facturo**, id.
- JOIN: `INNER JOIN cliente ON cliente_grupo.id_cliente = cliente.id_cliente` (un grupo cuyo
  cliente no exista desaparece de la lista).
- ⚠️ **El `arma_grid` activo NO filtra `f_delete`** (verificado 06/07/2026): trae TODOS los
  grupos (borrados incluidos). Existe un `arma_grid_bk` (backup, NO usado) que sí filtra
  `EMPTY(f_delete)`. Como la baja de grupo es **DELETE físico** (ver abajo), en la práctica no
  hay grupos con `f_delete` seteado. En Blazor: replicar el activo (`_deleted = 0` de la réplica
  igual, por convención del proyecto).
- **Agregar** exige permiso `"2" $ cNivel`; Eliminar/Modificar/Consulta **no chequean
  permiso** (rareza heredada — en Blazor aplicar 3/4 igual).
- **Cambio de cliente** (botón + textbox "cliente nuevo"): mueve un grupo entero a otro
  cliente. Valida que el nombre de grupo sea único globalmente (si otro cliente tiene un
  grupo homónimo se cancela). Muestra los viajes afectados (`cliente_grupo_view`) y luego:
  `UPDATE cliente_grupo SET id_cliente = nuevo WHERE id = …` +
  `UPDATE viaje SET id_cliente = nuevo, nombre_cliente = … WHERE id_cliente + grupo = viejo+nombre`.
- Botón "view" → `cliente_grupo_view` con el id (consulta de los viajes del grupo).

## ABM (`cliente_grupo_abm.scx`)

Modos `alta` / `baja` / `modifica` / `consulta`. **El alta está totalmente comentada**
(los grupos nacen desde Reservas) — el form igual la ofrece pero no graba nada.

### Baja (la operación pesada — cancela viajes en cascada)

1. Confirma. Cuenta los viajes del grupo agrupados por estado
   (`WHERE id_cliente + grupo = …`).
2. Decisión según estados encontrados:
   - **Hay ASIGNADO** → bloquea todo: "no se puede eliminar hasta que se haya cambiado al
     estado SIN ASIGNAR". (También bloquea si hay CURSO, que en datos es ASIGNADO.)
   - **Hay SIN ASIGNAR y nada ASIGNADO** → pide confirmación + **motivo de cancelación**
     (form `trafico_motivo_cancela` → `id_motivo`; sin motivo se cancela el proceso) →
     `UPDATE viaje SET estado_viaje='CANCELADO', interno=0, id_motivo=…,
     id_vehiculo='', id_chofer='', nombre_chofer='', franco=.F.`
     — sobre todo el grupo si no hay FINALIZADO/FACTURADO, o solo sobre los SIN ASIGNAR
     si los hay.
   - **Solo FINALIZADO/FACTURADO o nada** → avisa y cancela el proceso.
3. **Solo si NO había FINALIZADO/FACTURADO** (`nHayF = 0`):
   `DELETE FROM cliente_grupo WHERE id = …` (físico — el UPDATE f_delete está comentado).
   Si había, el grupo queda vivo con sus viajes históricos.

### Modifica (renombrar grupo / cambiar fecha fin)

- Campos editables: **nombre nuevo** (`nombre_new`) y `f_grupo_fin` — pero si
  `f_grupo_fc` tiene valor (grupo facturado) quedan deshabilitados. (⚠️ En el fuente en disco
  `f_grupo_fin` viene deshabilitado siempre en el `Init` de modifica —línea 115—, luego solo
  `nombre_new` y `f_grupo_ini` son editables de hecho; en Blazor habilitar según `f_grupo_fc`.)
- Valida que el nombre nuevo no exista para ese cliente (solo si cambió el nombre).
- **Clasificación de estados en MODIFICA (distinta a la BAJA — verificado 06/07/2026):**
  - `CANCELADO` → se ignora (no cuenta).
  - `SIN ASIGNAR` **o** `ASIGNADO` **o** `FINALIZADO` → suman a `nHayC` = **modificables**.
    (⚠️ ojo: acá **FINALIZADO SÍ es modificable**, al revés que en la baja, donde FINALIZADO
    es bloqueante junto con FACTURADO.)
  - `FACTURADO` → suma a `nHayF` = **bloqueante**.
  - Si `nHayC ≠ 0` (hay al menos uno modificable) → confirma y hace el UPDATE.
  - Si `nHayC = 0` (todo FACTURADO/cancelado) → bloquea: "los estados de las reservas ya no
    pueden ser modificadas".
- Confirmación → `UPDATE viaje SET grupo = nuevo, f_grupo_fin = … WHERE id_cliente+grupo=…`
  + `UPDATE cliente_grupo SET nombre, f_grupo_fin, f_grupo_ini WHERE id = …`.

### Consulta (toggle GPS)

Rareza: el Aceptar en consulta **togglea `viaje.gps_noveda`** de todos los viajes del grupo
(si está vacío lo llena con el datetime actual, si no lo vacía) — marca de novedad para el
sistema GPS.

---

## Reglas no obvias

1. **`f_grupo_fc` = grupo facturado = candado**: no se agregan servicios (lo valida la
   carga de reservas), no se renombra, no se cambia fecha.
2. La baja de un grupo es en realidad una **cancelación masiva de viajes con motivo** +
   borrado del grupo solo si no hay historia facturada. Nunca borra viajes.
3. Renombrar o cambiar cliente arrastra los `viaje` por el par desnormalizado
   `(id_cliente, grupo)` — mantener las dos escrituras (tabla + viajes) atómicas en Blazor.
4. Extender `f_grupo_fin` desde acá NO toca `cliente_grupo.f_grupo_fc` ni valida contra
   reservas existentes (la extensión automática la hace la carga de reservas).
5. Columnas SQL truncadas: `f_grupo_fi`, `f_grupo_in`, `liquidacio`.
