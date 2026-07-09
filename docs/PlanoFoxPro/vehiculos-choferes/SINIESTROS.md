# Siniestros — `siniestro.scx` + `siniestro_abm.scx`

> **Estado:** ✅ migrado a Blazor **solo lectura** (04/07/2026) — `Components/Pages/Siniestros.razor`
> (`/siniestros`) + `Components/Shared/SiniestroDetalleDialog.razor`, menú
> **Vehículos y Choferes → Siniestros**, permiso `'V'`. La carga sigue en FoxPro —
> botonera ABM deshabilitada (estrategia strangler de `abm-metrocar`).

FoxPro: menú `Vehiculos y Choferes → Siniestros` (`ON SELECTION BAR 7 OF vehiculosy openForm("siniestro")`).
Parte de accidente completo (~70 campos): vehículo asegurado NORTUR, tercero, propietario,
croquis de daños, testigos y descripción libre.

---

## Lista (`siniestro.scx`)

`arma_grid`: **INNER JOIN** `siniestro a` con `chofer b` por `id_chofer` para el nombre del
conductor. Orden por el criterio del combo **"Buscar por"**. Grilla de 8 columnas:

| Col | ControlSource | Header |
| --- | --- | --- |
| 1 | `id` | Siniestro |
| 2 | `chofer` (`b.nombre`) | Conductor |
| 3 | `id_vehiculo` (→ SQL `id_vehicul`) | **Dominio** (vehículo NORTUR) |
| 4 | `interno` | Interno |
| 5 | `fecha` | Fecha |
| 6 | `lugar` | Lugar |
| 7 | `marca_y_modelo` (→ SQL `marca_y_mo`) | Marca (**del TERCERO**) |
| 8 | `tipo_acc` | Tipo Acc. |

**Combo "Buscar por"** (`Combo1.InteractiveChange`) — cambia `cBuscaSql` (el `ORDER BY`):
Siniestro (`a.id`) · Conductor (`b.nombre`) · Dominio (`a.id_vehiculo`) · Interno (`a.interno`)
· Fecha (`a.fecha`). Doble clic en la grilla → Modificar. Botones Agregar/Eliminar/Modificar/
Consulta abren `siniestro_abm.scx` con `"alta"`/`"baja"`/`"modifica"`/`"consulta"`.

- **313 filas** activas. `tipo_acc`: VEHICULAR (312) / LABORAL (1).

## Ficha (`siniestro_abm.scx`) — 5 solapas (`carga_variable`)

| Solapa (page) | Campos (nombre form → SQL truncado) |
| --- | --- |
| **1 — El Hecho / Vehículo asegurado** | `id_chofer`, `id_vehiculo`→`id_vehicul`, `interno`, `id_viaje`, `lugar`, `fecha`, `hora`, `localidad`, `provincia`, `comisaria`, `tipo_acc`, `velocidad`, condiciones `visible`/`bocina`/`luces`/`mano_unica`/`lluvia`, `asegurado_dano`→`asegurado_` |
| **2 — Conductor + Vehículo del tercero** | `conductor`, `edad`, `registro_nro`→`registro_n`, `registro_vto`→`registro_v`, `tdoc`, `ndoc`, `conductor_direccion`→`conductor_`, `conductor_localidad`→`conductor2`, `conductor_telefono`→`conductor3`, `conductor_celular`→`conductor4`, `dominio`, `marca_y_modelo`→`marca_y_mo`, `tipo`, `ano`, `seguro`, `seguro_nombre`→`seguro_nom`, `seguro_poliza`→`seguro_pol`, `conductor_dano`→`conductor5`, `circula` |
| **3 — Propietario del tercero** | `propietario`→`propietari`, `propietario_direccion`→`propietar2`, `..._localidad`→`propietar3`, `..._telefono`→`propietar4`, `..._celular`→`propietar5`, `propietario_dano`→`propietar6` |
| **4 — Daños + descripción** | `descripcion_acc`→`descripcio` (memo), `aseg_delante/lateral/trasera`→`aseg_delan`/`aseg_later`/`aseg_trase`, `otro_delante/lateral/trasera`→`otro_delan`/`otro_later`/`otro_trase` |
| **5 — Testigos 1-3** | `test_N_nomb`→`test_N_nom`, `test_N_tdoc`→`test_N_tdo`, `test_N_ndoc`→`test_N_ndo`, `test_N_tel`, `test_N_cel` |

Auditoría: `usuario_cr`/`usuario_de`/`usuario_mo`, `f_ingreso`, `f_envio`.

---

## 🐛 Trampas de la réplica (verificadas 04/07/2026)

1. **`id_vehicul` = dominio del vehículo NORTUR (asegurado); `dominio` = dominio del TERCERO.**
   Son dos columnas distintas. La grilla usa `id_vehicul` (col "Dominio"); la ficha muestra
   `dominio` en la solapa Tercero. `marca_y_mo` también es del **tercero**.
2. **~30 columnas con nombre truncado a 10 chars** (ver tabla arriba). Verificar siempre con
   `INFORMATION_SCHEMA.COLUMNS` antes de escribir SQL.
3. **La tabla NO tiene `f_delete`** — la baja lógica es solo `_deleted`. No hay concepto de
   "egresado"/fila amarilla acá.
4. **`id_chofer` siempre matchea con `chofer`** (0 huérfanos) → el INNER JOIN de la grilla no
   pierde filas (313 = 313).
5. `hora` es `datetime2` → se lee como `TimeOnly` (formato `HH:mm`). `velocidad`/`edad`/`ano`
   son `int`. Los daños/condiciones son `bit` (checkboxes de solo lectura en Blazor).

---

## Migración a Blazor

- **Lista** (`Siniestros.razor`): grilla `cli-grid` con las columnas del FoxPro; combo
  "Buscar por" = criterio de **orden** (en memoria); filtro de texto + filtro Tipo Acc.; chip
  Total; Actualizar + Excel; botonera ABM deshabilitada. Doble clic / botón Consulta → ficha.
- **Ficha** (`SiniestroDetalleDialog.razor`): `MudDialog` con las **5 solapas** del ABM,
  calcado del patrón `ChoferDetalleDialog`. Condiciones y daños como flags (`cli-flag`);
  vencimiento de registro del tercero con color (rojo vencido / ámbar por vencer). La
  comparación de zonas dañadas (asegurado vs tercero) va en dos columnas (`sin-danos`); la
  descripción libre en un panel (`sin-descripcion`, `white-space:pre-wrap`).

### Métodos `ReportService`
- `GetSiniestrosAsync()` — grilla (INNER JOIN a `chofer`).
- `GetSiniestroDetalleAsync(id)` — ficha completa (5 solapas + auditoría).
- Export: `ExcelExportService.Siniestros(filas)`.

### Validado (04/07/2026)
Grilla: **313 filas** (coincide con el total). Ficha #6 (ROSALES/GSN673, tercero LUIS
VELLA/DKJ208) y #12 (MARTINEZ/FOH348, tercero LUPPO NESTOR/MERCEDES BENZ SPRINTER, descripción
+ 2 testigos + daños asegurado-trasera/tercero-delante) verificadas al dígito contra SQL y por
captura. Smoke test en la suite.
