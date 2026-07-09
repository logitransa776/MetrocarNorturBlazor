# Viáticos (`chofer_viatico.scx` + catálogos `_motivo` / `_liquida`)

> **Menú FoxPro:** Tráfico → Viáticos → { Viáticos · Motivo · Forma Liquidacion }
> (`POPUP viaticos`: BAR 1 `chofer_viatico`, BAR 3 `chofer_viatico_motivo`, BAR 4 `chofer_viatico_liquida`).
> **Extraído:** 05/07/2026. **Migrado:** solo lectura + andamiaje ABM (`/viaticos`, `/viaticos-motivo`,
> `/viaticos-forma-liquidacion`, permiso `'T'`).

## Concepto

Pagos de **viáticos** a los conductores (un importe por fecha, con motivo y forma de liquidación).
**Módulo sin uso en producción:** las 3 tablas están **vacías** (0 filas) en la réplica. Se migra
por completitud del menú + andamiaje ABM listo.

## Tablas

### `chofer_viatico` (0 filas)

| Columna | Tipo | Significado |
| --- | --- | --- |
| `id` | int (**NO identity**) | PK física; alta = `MAX(id)+1` |
| `fecha` | date | Fecha del viático |
| `id_chofer` | nvarchar(30) | FK a `chofer` |
| `id_motivo` | **bigint** | FK a `chofer_viatico_motivo.id` |
| `id_liquida` | **bigint** | FK a `chofer_viatico_liquida.id` |
| `forma_pago` | nvarchar(30) | CONTADO / CUENTA CORRIENTE / OTRAS |
| `importe` | decimal | Monto |
| `f_pago` | date | Fecha de pago |
| `_deleted` | bit | metadata réplica |

### `chofer_viatico_motivo` / `chofer_viatico_liquida` (0 filas cada una)

`id` (int, no identity) + `nombre` (nvarchar 60). Catálogos idénticos entre sí.

> 🐛 **Baja FÍSICA** en las 3 tablas (`DELETE`), sin `f_delete`.
> 🐛 `id_motivo`/`id_liquida` son **bigint** en `chofer_viatico` pero `id` es **int** en los
> catálogos (el JOIN funciona igual). En el ABM se leen como int.

## Lista de Viáticos (`chofer_viatico.scx`)

Filtro por rango de fechas + chofer opcional. 4 JOINs: `chofer_viatico_liquida`, `chofer`,
`chofer_viatico_motivo`. Columnas Fecha/Conductor/Motivo/Liquida/Forma Pago/Importe/F.Pago +
total de importe. Botones Agregar/Eliminar/Modificar/Consultar → `chofer_viatico_abm.scx`.
Report ABIERTO/RESUMEN. (El form comparte código con el de combustible — de ahí campos muertos
como `sobre`, `odometro`, `litros` que NO aplican a viáticos.)

## ABM de Viático (`chofer_viatico_abm.scx`)

- **alta**: valida chofer, motivo, forma de liquidación, forma de pago, importe > 0; `INSERT`.
  (También modo `"trafico"` = alta pre-cargando el chofer — no se usa acá.)
- **modifica**: `UPDATE` de todos los campos.
- **baja**: confirmación → **`DELETE`** (física).
- **consulta**: solo lectura.

## Catálogos Motivo / Forma Liquidación (`_motivo_abm` / `_liquida_abm`)

ABM simple id+nombre: alta valida nombre no vacío + duplicado (por nombre, UPPER), `INSERT (nombre)`;
modifica `UPDATE nombre`; baja **`DELETE`** física. Idénticos → un solo dialog parametrizado en Blazor.

## Migración Blazor (resumen)

| Pieza | Archivo |
| --- | --- |
| Lectura | `ReportService.GetViaticosAsync` / `GetViaticoMotivosAsync` / `GetViaticoLiquidaAsync` / `GetChoferesComboAsync` |
| Escritura viático (andamiaje) | `AbmService.Alta/Modifica/BajaViaticoAsync`, `ViaticoInput` |
| Escritura catálogos (andamiaje) | `AbmService.*ViaticoMotivoAsync` / `*ViaticoLiquidaAsync` (helpers genéricos) |
| Grilla | `Viaticos.razor` + `ViaticoEditorDialog.razor` (combos chofer/motivo/liquida/forma pago) |
| Catálogos | `ViaticosMotivo.razor` / `ViaticosFormaLiquidacion.razor` + `CatalogoSimpleEditorDialog.razor` (parametrizado) |
| Flags | `AbmFeatureFlags.ViaticosAbmActivo` + `ViaticoCatalogosAbmActivo` (hoy `false`) |

Validado: 0 filas en las 3 → las vistas muestran "sin registros" correctamente; andamiaje completo.
