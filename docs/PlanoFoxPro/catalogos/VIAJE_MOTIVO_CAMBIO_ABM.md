# Lógica FoxPro — Motivos de Cambio de Cronogramas (`viaje_motivo_cambio.scx` + `_abm.scx`)

> Menú: **ABM del sistema → Motivos de Cambio de Cronogramas** (permiso letra `A`,
> `MENU_PRINCIPAL.MPR` BAR 20). Hermano: BAR 21 "Motivos de Llegadas Tardes"
> (`viaje_motivo_tarde*`, mismo patrón — extraer si se necesita).
> Extraído del binario con `foxpro-extract` (02/07/2026). **11 motivos.**
> Lo consume la operación **Reasignar / Otra Unidad** de Tráfico (motivo obligatorio del
> log RE-ASIGNO — ver `../trafico/TRAFICO2_TOOLBAR.md` §2-3 y plan Buslink §2.4).

---

## Concepto

Catálogo de motivos por los que se cambia la unidad asignada a un viaje. El form
`trafico_asigna` (modo "CON" = Otra Unidad) y `trafico_reasigna` piden un motivo de esta
tabla y lo vuelcan al comentario/log del viaje.

**Tabla `viaje_motivo_cambio`** (verificada contra `sys.columns`, 02/07/2026):
**`id` (autoinc) y `nombre` — nada más.** Sin `f_create`/`f_delete`/`f_modify`.

## Lista (`viaje_motivo_cambio.scx`)

- Grilla 2 columnas: Codigo (`id`), Nombre (`nombre`), orden por nombre.
- Botones Agregar / Eliminar / Modifica / Salir. Doble clic = Modifica.
- ⚠️ **Sin chequeo de permisos 2/3/4** (igual que motivo_cancela — en Blazor aplicarlos).
- Sin colores (la tabla no tiene `f_delete`; el código del amarillo está comentado).
- Rareza cosmética: el objeto form se llama `trafico_motivo_cancela` (copy-paste).

## ABM (`viaje_motivo_cambio_abm.scx`) — ⚠️ ROTO en el fuente de disco

Modos `alta` / `baja` / `modifica`. Caption copy-paste "ABM de Adicionales".

- **Alta** (lo único claramente sano): motivo obligatorio, MAYÚSCULAS, máx 50 en UI.
  Anti-duplicado por `nombre` → `INSERT INTO viaje_motivo_cambio (nombre) VALUES (cNombre)`.
- **Baja**: `DELETE FROM viaje_motivo_cambio WHERE id = nId` — **FÍSICA** (la tabla no
  tiene baja lógica), sin confirmación.
- **Modifica**: 🐛 **BUG GRAVE heredado — actualiza LA TABLA EQUIVOCADA**:
  ```foxpro
  UPDATE viaje_motivo_cancela SET motivo = cNombre , f_delete = dF_delete , f_modify = DATE() WHERE id = nId
  ```
  (copy-paste del ABM de motivos de cancelación). Además `dF_delete = thisform.id.value`
  — le mete el **id** en el campo fecha. Efecto real: modificar un motivo de cambio
  **corrompe la fila homónima en id de `viaje_motivo_cancela`** y no toca la tabla propia.
- 🐛 Bug adicional: el Init de baja/modifica lee `cursorViajeMotivoCambio.motivo`, columna
  que **no existe** (la tabla tiene `nombre`) → error de runtime en el fuente de disco.

> **Conclusión:** en el fuente de disco solo el ALTA funciona con certeza; baja/modifica
> están rotos o difieren del exe productivo (regla del proyecto: el exe manda — verificar
> con el usuario si en producción se puede modificar un motivo de cambio). Para Blazor no
> importa: se implementa sano (`UPDATE viaje_motivo_cambio SET nombre = ... WHERE id = ...`)
> y **NO se copian los bugs**.

## Reglas no obvias

1. La tabla es mínima (`id`, `nombre`) — el ABM Blazor es un calco de
   `viaje_motivo_cancela` sin el campo de inhabilitación.
2. Baja física: verificar referencias antes de borrar (el motivo viaja como TEXTO al
   comentario del log, no como FK — borrar no rompe históricos, pero conviene avisar).
3. Cutover: el catálogo lo LEE la toolbar de Tráfico FoxPro hasta el día D (misma ventana
   operativa que los demás catálogos del grupo A — los motivos nuevos de Buslink no se
   verán en FoxPro).
4. `viaje_motivo_tarde` (Llegadas Tardes) es el mismo patrón — si el alcance de Tráfico
   día 1 lo requiere, extraerlo lleva minutos.
