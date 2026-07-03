# Lógica FoxPro — Motivos de Cancelaciones (`viaje_motivo_cancela.scx` + `_abm.scx`)

> Menú: **ABM del sistema → Motivos de Cancelaciones de Reservas** (permiso letra `A`,
> `MENU_PRINCIPAL.MPR` BAR 19). Selector relacionado: `trafico_motivo_cancela.scx`
> (el dialog que pide el motivo al cancelar — lo llaman el Zoom del Viaje y la baja de grupos).
> Extraído del binario con `foxpro-extract` (02/07/2026).
> **Es el ABM Nº 1 de la Fase 1 del plan Buslink** (primer catálogo con cutover temprano;
> calca `UsuariosAbm.razor` + `UsuarioEditorDialog.razor`).

---

## Concepto

Catálogo de motivos de cancelación de viajes. `viaje.id_motivo` lo referencia en cada
viaje CANCELADO (la vista Cxl de Tráfico hace JOIN por `id` para mostrar la columna Motivo).

**Tabla `viaje_motivo_cancela`** (verificada contra `sys.columns`, 02/07/2026):
`id` (autoinc), `motivo` (30 chars), `f_create`, `f_delete`, `f_modify`.
**Datos: 6 motivos, todos activos** (ninguno con `f_delete`).

## Lista (`viaje_motivo_cancela.scx`)

- Grilla 3 columnas: Codigo (`id`), Nombre (`motivo`), "Inahib." [sic] (`f_delete`),
  orden por motivo. Filas con `f_delete` en **amarillo**.
- Botones Agregar / Eliminar / Modifica / Salir. Doble clic = Modifica.
- ⚠️ **NINGÚN botón chequea permisos** (`cNivel` 2/3/4) — rareza: no sigue el patrón
  estándar de los 73 ABMs. El único gate es la letra `A` del menú.
  **En Blazor aplicar los dígitos 2/3/4 igual** (mejora, no réplica).
- Reposicionamiento post-ABM: variable pública `nViajeMotivoCancela` → LOCATE por id.

## ABM (`viaje_motivo_cancela_abm.scx`)

Modos `alta` / `baja` / `modifica` (no hay consulta). Dos rarezas cosméticas heredadas:
el caption dice **"ABM de Adicionales"** y el objeto form se llama `trafico_motivo_cancela`
(copy-paste de otro form) — no copiar.

- **Alta**: motivo obligatorio (único mensaje de validación), MAYÚSCULAS (`Format "!"`),
  máx 30. Anti-duplicado exacto por `motivo` →
  `INSERT INTO viaje_motivo_cancela (motivo, f_create) VALUES (cNombre, DATE())`.
- **Baja**: **lógica** — `UPDATE ... SET f_delete = DATE() WHERE id = nId`.
  ⚠️ **Sin diálogo de confirmación** (en Blazor agregarlo).
- **Modifica**: `UPDATE ... SET motivo = ..., f_delete = <valor del textbox>,
  f_modify = DATE() WHERE id = nId`. El campo `f_delete` es editable → **rehabilitar**
  un motivo = limpiarle la fecha. ⚠️ **Sin anti-duplicado en modifica** (permite renombrar
  a un motivo ya existente — en Blazor validar).

## Selector (`trafico_motivo_cancela.scx`) — "Motivo de la cancelación"

Dialog modal que usan los flujos de cancelación (Zoom → Cancelar Viaje; baja de
`cliente_grupo`): combo de motivos **activos** (`WHERE EMPTY(f_delete) ORDER BY motivo`),
Aceptar devuelve el `id`, Cancelar devuelve `0` (el llamador aborta la cancelación si es 0).

## Reglas no obvias

1. **Nunca borrar físico**: `viaje.id_motivo` referencia el catálogo desde ~21K viajes
   cancelados históricos. La baja lógica saca el motivo del selector pero la vista de
   cancelados lo sigue resolviendo por `id`.
2. El selector filtra por `f_delete` vacío — un motivo inhabilitado deja de ofrecerse
   pero no rompe nada.
3. Al migrar (Fase 1, cutover temprano): bloquear este ABM en FoxPro + apagar la sync de
   `viaje_motivo_cancela`. FoxPro seguirá LEYENDO el DBF para el selector hasta el día D →
   los motivos nuevos creados en Buslink no aparecerán en el selector FoxPro
   (misma regla operativa de la ventana que `cliente`/`destino`).
4. Columnas SQL sin truncar (nombres cortos). Metadata de réplica: `_deleted`,
   `_created_at`, `_updated_at` (filtrar `_deleted = 0`).
