# Francos (`chofer_franco.scx` + `_abm.scx` + `_auditoria.scx`)

> **Menú FoxPro:** Tráfico → Francos → { Ingreso de Francos · Mantenimiento de Francos · Auditoría Francos }
> (`POPUP francos`: BAR 1 `chofer_franco_abm`, BAR 3 `chofer_franco`, BAR 5 `chofer_franco_auditoria`).
> **Extraído:** 05/07/2026. **Migrado:** solo lectura + andamiaje ABM (`/francos`, `/francos-ingreso`,
> `/francos-auditoria`, permiso `'T'`).

## Concepto

Registro de **francos, licencias y permisos** de los choferes (un día sin trabajar, con motivo).
Lo cruza la liquidación de choferes y la auditoría de asistencia.

## Tabla `chofer_franco` (71.573 filas, server viejo)

| Columna | Tipo (réplica) | Significado |
| --- | --- | --- |
| `id` | int (**NO identity**) | PK física; alta = `MAX(id)+1` |
| `id_chofer` | nvarchar(30) | FK a `chofer` (JOIN para el nombre) |
| `codigo` | nvarchar(10) | Código del motivo (FT, E, V, LC, LSG, PL, OT, MT, EX) |
| `motivo` | nvarchar(60) | Descripción del motivo (desnormalizada) |
| `fecha` | date | Día del franco |
| `trabajo` | bit | ¿Trabajó igual ese día? (casi siempre NULL/0) |
| `valido` | bit | flag interno |
| `_deleted` | bit | metadata réplica |

> 🐛 **Baja FÍSICA** (`DELETE FROM chofer_franco WHERE id = nId`), sin `f_delete`.
> 🐛 **71k filas** → la grilla Blazor DEBE tener **rango de fechas obligatorio + Virtualize**.
> 🐛 Hay **fechas corruptas** (`9201-03-03`) → acotar con `FechaMinValida`/`FechaMaxValida`.

**Motivos canónicos** (de `metrocar.prg` `aFrancoMotivo`): FT (FRANCO TOMADO), EX (DIA DE
EXPULSION), E (ENFERMEDAD), V (VACACIONES), MT (MEDIO TURNO), PL (PERMISO LABORAL), LC (LICENCIA),
LSG (LICENCIA SIN GOCE DE SUELDO), OT (OTROS). En la data real también aparece `F` y vacío.

## 1. Mantenimiento de Francos (`chofer_franco.scx`) — la grilla

Filtro por rango de fechas (default HOY) + combo de motivo. JOIN a `chofer`. Columnas
Chofer/Nombre/Motivo/Fecha/Trabajo. Botones Agregar (→ ingreso masivo), Eliminar (DELETE por
`id`), Modifica (→ `chofer_franco_modifica.scx`, no migrado), Impresión (report).

## 2. Ingreso de Francos (`chofer_franco_abm.scx`) — ALTA MASIVA

Flujo especial (no el ABM de una fila): multiselect de **choferes** × un **rango de fechas**
(se van agregando a una lista) × un **motivo**. Antes de insertar valida por cada chofer×día:
¿ya tiene franco ese día? ¿trabajó ese día (hay viaje)? Si hay conflicto, lista los errores y
aborta. Si no, `INSERT INTO chofer_franco (id_chofer, codigo, motivo, fecha)` por cada combinación.

> Migración: `AbmService.AltaFrancosAsync(idsChofer[], fechas[], codigo, motivo)` saltea los días
> que ya tienen franco (chequeo por `id_chofer + fecha`). El chequeo de "trabajó ese día" del
> FoxPro se puede sumar al activar el ABM; hoy el andamiaje inserta salteando duplicados de franco.

## 3. Auditoría de Francos (`chofer_franco_auditoria.scx`) — INFORME de control

**No escribe** — arma en memoria una matriz **chofer × día del mes** (mes/año elegidos):
- **"trb"** = el chofer trabajó ese día (hay viaje FINALIZADO/FACTURADO PROPIO donde es
  `id_chofer` o `id_chofer2`).
- **código en minúscula** = franco cargado ese día (excluye código `F` solo).
- **"DUP"** = franco Y trabajo el mismo día (problema).
- Cuenta **días trabajados** (`d_work`) y **problemas** (`n_prob` = DUP).
- Opción **excluir auditores** (`chofer.auditor = 1`). Export a Excel.

> Migración: `ReportService.GetFrancoAuditoriaAsync(mes, ano, excluyeAuditores)` — 3 queries
> (viajes titular + viajes 2º chofer + francos) + cruce en memoria. Réplica: `estado_via`,
> `tipo_chofe` (truncado), `id_chofer2`. Validado: junio 2026 = 98 choferes (idéntico a SQL).

## Migración Blazor (resumen)

| Pieza | Archivo |
| --- | --- |
| Lectura | `ReportService.GetFrancosAsync` / `GetFrancoMotivosAsync` / `GetFrancoAuditoriaAsync` |
| Escritura (andamiaje) | `AbmService.AltaFrancosAsync` (masivo) / `BajaFrancoAsync` (DELETE) |
| Grilla | `Francos.razor` (`/francos`, Virtualize + filtro fecha+motivo) |
| Ingreso | `FrancosIngreso.razor` + `FrancoAltaDialog.razor` (multiselect + rango + motivo) |
| Auditoría | `FrancosAuditoria.razor` (matriz chofer×día, KPIs, Excel) |
| Flag | `AbmFeatureFlags.FrancosAbmActivo` (hoy `false`) |

Pendiente: `chofer_franco_modifica.scx` (modifica de un franco puntual) — TODO menor.
