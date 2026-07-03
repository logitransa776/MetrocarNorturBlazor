---
name: modulo-vehiculos-choferes
description: Conocimiento del módulo Vehículos y Choferes de Metrocar — la flota y el personal de conducción. Usar SIEMPRE que se trabaje con choferes/conductores, vehículos/flota, fleteros (transportistas contratados), tipos de vehículo, odómetros/kilómetros, siniestros, apercibimientos/sanciones, capacitaciones/cursos, agenda de vencimientos (VTV, matafuegos, registro, CNRT, AEP), o cualquier pantalla/dato de las tablas chofer, vehiculo, vehiculo_chofer, fletero, vehiculo_tipo, vehiculo_km, siniestro, chofer_sancion, chofer_curso, vehiculo_dueno — tanto para informes/vistas como para los futuros ABMs en Blazor. Mapa de tablas, columnas truncadas, forms FoxPro, qué está migrado y qué falta, y las trampas de réplica (vehiculo_chofer vacía, chofer_log no replicada).
---

# Módulo Vehículos y Choferes — mapa de conocimiento

El módulo que administra **la flota** (vehículos propios y contratados) y **el personal de
conducción** (choferes propios, fleteros/contratados y sus datos: registros, cursos,
sanciones, siniestros). Es el módulo del menú FoxPro **"Vehículos y Choferes"**.

> Convención del proyecto: skill vertical de módulo (como `modulo-trafico`, `modulo-reservas`).
> Cubre el *qué* (dominio, tablas, pantallas). El *cómo migrar* (método ABM, regla de escritura)
> vive en `abm-metrocar`. El *cómo construir UI* en `blazor-nortur`. Permisos en `seguridad-nortur`.
> Forms FoxPro extraídos con `foxpro-extract` (15/06/2026).

---

## El menú FoxPro (estructura real)

Menú `Vehiculos y Choferes` (POPUP `vehiculosy` en `MENU_PRINCIPAL.MPR`). Letra de permiso
del módulo: **`'V'`** (campo `usuario.acceso`).

| Bar | Pantalla | Form lista | Form ABM | Tabla(s) | Estado Blazor |
| --- | --- | --- | --- | --- | --- |
| 1 | **Choferes** | `chofer.scx` | `chofer_abm.scx` | `chofer`, `vehiculo_chofer` | ✅ solo lectura |
| 2 | **Apercibimientos** | `chofer_sancion.scx` | `chofer_sancion_abm.scx` | `chofer_sancion` | ⬜ pendiente |
| 2→ | Apercibimientos: Motivos | `chofer_sancion_motivo.scx` | `chofer_sancion_motivo_abm.scx` | `chofer_sancion_motivo` | ⬜ pendiente |
| 3 | **Capacitaciones: Consulta** | `chofer_curso_consulta.scx` | — | `chofer_curso`, `chofer_curso_parametro` | ⬜ pendiente |
| 3→ | Capacitaciones: Armado | `chofer_curso_arma.scx` | — | `chofer_curso` | ⬜ pendiente |
| 5 | **Odómetros** | `vehiculo_km.scx` | — | `vehiculo_km` | ⬜ pendiente |
| 7 | **Siniestros** | `siniestro.scx` | `siniestro_abm.scx` | `siniestro` | ⬜ pendiente |
| 9 | **Vehículos - Flota** | `vehiculo.scx` | `vehiculo_abm.scx` | `vehiculo`, `vehiculo_dueno`, `vehiculo_permiso` | ✅ solo lectura |
| 10 | **Agenda de Vencimientos** | `agenda_vencimiento.scx` | — (informe) | `chofer` + `vehiculo` + `parametro` | ⬜ pendiente |
| 12 | **Fleteros** | `fletero.scx` | `fletero_abm.scx` | `fletero` | ⬜ pendiente |
| 14 | **Tipo de Vehículos** | `vehiculo_tipo.scx` | `vehiculo_tipo_abm.scx` | `vehiculo_tipo` | ⬜ pendiente |

> `Fleteros` aparece también en el menú **Facturación** (mismo form `fletero`). El catálogo
> de fleteros es compartido entre este módulo y Facturación/Liquidación.

Submenús del módulo que el menú FoxPro reparte en **otros** popups pero pertenecen al dominio
chofer (documentados en `modulo-facturacion-liquidacion` y `modulo-trafico`):
- **Francos** (`chofer_franco*`) → menú Tráfico. Tabla `chofer_franco`.
- **Viáticos** (`chofer_viatico*`) → menú Tráfico. Tablas `chofer_viatico*`.
- **Tarifario de Choferes** (`lista_precio_chofer*`) → menú Facturación.
- **Liquidación a Choferes / Adelantos** (`liquidacion_chofer*`, `chofer_adelanto*`) → Facturación.

---

## Mapa de tablas (replicaVPF) — verificado 15/06/2026

Conteos = filas con `_deleted = 0` en el server local (datos completos). Recordar la regla
del proyecto: **siempre filtrar `_deleted = 0`** (réplica) y mostrar `f_delete` como "egresado"
(borrado lógico del negocio, NO ocultar — pintar amarillo).

| Tabla | Filas activas | Rol | Réplica |
| --- | --- | --- | --- |
| `chofer` | 707 (249 activos / 458 egresados) | Maestro de conductores. ~88 columnas (5 pestañas). | ✅ |
| `vehiculo` | 406 | Maestro de la flota. ~80 columnas (seguros, vtos, GPS, combustible). | ✅ |
| `vehiculo_chofer` | **0** | N:N chofer↔vehículo asignado. **VACÍA en la réplica** (ver trampas). | ✅ estructura, sin datos |
| `vehiculo_dueno` | 235 | Dueños del vehículo + `porcentaje`. | ✅ |
| `vehiculo_tipo` | 6 | Catálogo de categorías (pax, consumo). | ✅ |
| `fletero` | 28 | Transportistas contratados (= `id_contratado`/`id_contrat`). | ✅ |
| `vehiculo_km` | 10.533 | Odómetros: lecturas de km por dominio/mes. Transaccional. | ✅ |
| `siniestro` | 313 | Accidentes: ~70 columnas (croquis, testigos, terceros). | ✅ |
| `chofer_sancion` | **0** | Apercibimientos. Replicada **sin datos**. | ✅ vacía |
| `chofer_sancion_motivo` | **0** | Motivos de sanción (catálogo). Vacía. | ✅ vacía |
| `chofer_curso` | 417 | Cursos hechos por chofer (capacitaciones). | ✅ |
| `chofer_curso_parametro` | 13 | Catálogo de cursos (nombre, duración). | ✅ |
| `chofer_log` | — | Auditoría de cambios del ABM. **NO replicada** (viva en FoxPro). | ❌ |
| `agenda_vencimiento` | — | NO existe como tabla; el form es un informe sobre `chofer`+`vehiculo`. | ❌ (es informe) |

> `agenda` (210 filas) **NO** es del módulo: es la agenda de contactos (Utilitarios), aunque
> su estructura se parece a `chofer`. No confundir.

### Columnas truncadas a 10 chars (réplica DBF→SQL) — CRÍTICO

La réplica trunca todo nombre de columna a 10 caracteres. SIEMPRE verificar con
`INFORMATION_SCHEMA.COLUMNS` antes de escribir SQL. Mapas ya resueltos:

**`chofer`** (form usa nombres largos → SQL real):
`domicilio_nro`→`domicilio_`, `domicilio_piso`→`domicilio2`, `domicilio_dpto`→`domicilio3`,
`entre_calle_1/2`→`entre_call`/`entre_cal2`, `registro_nro`→`registro_n`,
`registro_vto`→`registro_v`, `registro_nro_cnrt`→`registro_2`, `registro_vto_cnrt`→`registro_3`,
`registro_vto_aeo`→`registro_4`, `nextel_celu`→`nextel_cel`, `id_lista_precio`→`id_lista_p`,
`jornal_aplica`→`jornal_apl`, `grupo_sanguineo`→`grupo_sang`, `estado_civil`→`estado_civ`,
`lugar_nacimiento`→`lugar_naci`, `real_domicilio`→`real_domic`/`real_domi2..9`/`real_dom10`
(orden: calle, nro, piso, dpto, cpostal, localidad, partido, provincia, entre1, entre2).

**`vehiculo`** (vencimientos para Agenda):
`verificacion_vto` (VTV)→`verificac2`, `vencimiento_mat` (matafuegos)→`vencimient`,
`habilitacion_vto`→`habilitac2`, `id_vehiculo`→`id_vehicul`, patente = `dominio` (no `patente`).
`uso` = `PROPIO` | `CONTRATADO`. `estado` operativo = ASIGNADO/LIBERADO/TALLER (lo pisa Tráfico).
`activo` (bit) además de `f_delete`: en la lista, egresado = `!activo OR f_delete`.

**`fletero`**: PK `id_contratado`→`id_contrat`. Tiene `razon_soci`, `nombre`, `cuit`,
`id_lista_p`/`id_lista_2` (tarifarios de pago), `modo_liq`, `fc_prefere`.

Detalle campo a campo de cada pantalla → archivos en `references/` de esta skill.

---

## Detalle por pantalla (referencias)

Cada pantalla tiene su doc detallada (tablas, columnas, validaciones, reglas no obvias):

- `references/CHOFERES.md` — maestro de conductores (5 pestañas). **Migrado solo lectura.**
- `references/VEHICULOS.md` — maestro de flota (filtros, vencimientos, dueños, permisos). **Migrado solo lectura.**
- `references/FLETEROS.md` — transportistas contratados (catálogo compartido con Facturación).
- `references/TIPO_VEHICULOS.md` — catálogo de categorías (el más simple, buen 1er ABM).
- `references/ODOMETROS.md` — `vehiculo_km`, lecturas de kilómetros. ⚠️ **Descubierto
  02/07/2026: la ASIGNACIÓN de Tráfico también escribe `vehiculo_km`** (primer odómetro del
  mes → INSERT + cierra `km_fin` del mes anterior) — ver `TRAFICO2_TOOLBAR.md` §2.2. La tabla
  cambia de dueño con el circuito `viaje` el día D (plan Buslink), NO como catálogo suelto.
- `references/SINIESTROS.md` — accidentes (form gigante de ~70 campos).
- `references/APERCIBIMIENTOS.md` — sanciones + motivos (tablas vacías hoy).
- `references/CAPACITACIONES.md` — cursos por chofer + catálogo de cursos.
- `references/AGENDA_VENCIMIENTOS.md` — informe de vtos (chofer + vehículo), no ABM.

> La doc FoxPro "oficial" del proyecto vive en `docs/PlanoFoxPro/` (ej: `CHOFER_ABM.md`).
> Las referencias de esta skill resumen para decisión rápida; el detalle completo de un form
> migrado va a `docs/PlanoFoxPro/<FORM>_ABM.md` cuando se construye.

---

## Qué está migrado a Blazor

| Pantalla | Página | Ficha/Dialog | Doc |
| --- | --- | --- | --- |
| Choferes (solo lectura) | `Components/Pages/Choferes.razor` (`/choferes`) | `ChoferDetalleDialog.razor` (5 tabs) | `docs/PlanoFoxPro/vehiculos-choferes/CHOFER_ABM.md` |
| Vehículos - Flota (solo lectura) | `Components/Pages/Vehiculos.razor` (`/vehiculos`) | `VehiculoDetalleDialog.razor` (6 tabs) | `references/VEHICULOS.md` |

Patrón aplicado (copiar para las siguientes): lista MudTable con filtros + egresados en
amarillo (`cli-grid__row--egresado`), ficha en `MudDialog` con tabs, estilos `cli-*`/`zoom-*`,
botonera de ABM deshabilitada (solo lectura), permiso de módulo `'V'`. Métodos en
`ReportService` (`GetChoferesAsync` / `GetChoferDetalleAsync`).

El **Home/Tablero** ya muestra KPIs de este módulo: Vehículos, VTV/Matafuegos por vencer,
Choferes, Registros/CNRT/AEP por vencer (`TableroDto` en `ReportService`). Es la versión
resumida de la **Agenda de Vencimientos**.

---

## Orden sugerido de migración

Por fricción creciente (criterio de `abm-metrocar`: catálogos chicos primero):

1. **Tipo de Vehículos** (`vehiculo_tipo`, 6 filas) — catálogo mínimo, ideal primer ABM real.
2. **Fleteros** (`fletero`, 28) — catálogo chico; ojo: compartido con Facturación.
3. **Vehículos** (`vehiculo`, 406) — maestro grande, muchas pestañas; primero solo lectura
   (como Choferes), después ABM.
4. **Agenda de Vencimientos** — informe puro (sin escritura), alto valor operativo; reusa la
   lógica del `TableroDto`.
5. **Odómetros / Capacitaciones / Siniestros / Apercibimientos** — según necesidad del cliente.

---

## Trampas no obvias (no repetir)

1. **`vehiculo_chofer` está VACÍA en la réplica** (0 filas). La pestaña Vehículos de la ficha
   de Chofer muestra "Sin vehículos asignados" — es correcto, no es bug. Funcionará cuando se
   replique. La relación chofer↔vehículo del día a día se ve en Tráfico, no acá.
2. **`chofer_log` NO está replicada** (auditoría viva en FoxPro, como `vehiculo_sobre` en
   Combustible). El botón "Log" del FoxPro no se puede replicar en solo lectura.
3. **`vehiculo` no tiene `id_vehiculo` ni `patente`** en SQL: son `id_vehicul` y `dominio`.
4. **`chofer.nombre` es desnormalizado** (apellido + nombre1 + nombre2). Se usa para búsqueda
   incremental y grilla; al editar se recalcula.
5. **`fletero` es de doble dueño** (este módulo + Facturación). Antes de migrar su ABM,
   coordinar con `modulo-facturacion-liquidacion` (regla strangler: una sola fuente de verdad).
6. **`chofer_sancion` / `chofer_sancion_motivo` están vacías**: Apercibimientos no se usa hoy
   en producción. Migrar solo si el cliente lo pide.
7. **Agenda de Vencimientos no tiene tabla propia**: es un SELECT sobre `chofer` (registro/
   cnrt/aeo) y `vehiculo` (`verificac2` VTV, `vencimient` matafuegos, solo `uso='PROPIO'`),
   con días de anticipación desde `parametro`. Rojo si vence dentro del umbral.
8. **Pestaña Cubiertas del vehículo = columnas `r1..r7`** de la tabla `vehiculo` (un nro de
   serie por posición), **NO una tabla aparte**. La pestaña Repuestos sí es tabla
   (`vehiculo_repuesto`) pero está **vacía** en la réplica (0 filas).
9. **El orden VISUAL de las 6 pestañas del `vehiculo_abm` ≠ el número de `page` interno**:
   visual = Datos(page1) · Permisos(page6) · Dueños(page2) · Cubiertas(page3) ·
   Tarjetas(page4) · Repuestos(page5). Detalle en `references/VEHICULOS.md`.
10. **`vehiculo` egresado = doble condición**: `!activo OR f_delete` (distinto de chofer, que
    sólo mira `f_delete`). El filtro "Ver Activos" del FoxPro arranca **tildado**.
11. **Tacógrafo en `vehiculo`**: `tacografo_` = marca, `tacografo2` = nro (truncado, fácil
    de confundir). Dueños: nombre desde tabla `dueno`; Permisos: nombre desde tabla `permiso`.

## Estado

- Choferes y **Vehículos - Flota** migrados (solo lectura). Resto del módulo: relevado a
  fondo, pendiente de migrar.
- Esta skill mejora con cada pantalla migrada: actualizar la tabla "Qué está migrado",
  y al construir un ABM guardar las lecciones en `abm-metrocar` (método) y acá (dominio).
