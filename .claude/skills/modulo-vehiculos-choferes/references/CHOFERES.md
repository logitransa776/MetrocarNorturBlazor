# Choferes — `chofer.scx` + `chofer_abm.scx` ("ABM de Conductores")

**Migrado a Blazor en SOLO LECTURA** (15/06/2026). Doc completa y oficial del form:
`docs/PlanoFoxPro/vehiculos-choferes/CHOFER_ABM.md`. Acá solo el resumen para decisión rápida.

- **Tabla:** `chofer` (707 filas: 249 activos / 458 egresados). PK `id_chofer` (texto, tipeada).
- **Relacionadas:** `vehiculo_chofer` (N:N, vacía en réplica), `lista_precio_modelo_chofer`
  (combo lista liquidación), `fletero` (combo), `chofer_log` (auditoría, NO replicada).
- **Lista:** filtros combo Fletero + búsqueda Nombre (incremental sobre `nombre`
  desnormalizado) + check Ver Egresados. Egresados (`f_delete`) en amarillo.
- **Ficha (5 pestañas):** Datos Personales · Domicilios (DNI + real) · Teléfonos
  (principal + 5 adicionales) · Condiciones Laborales (jornada, días, lista liquidación,
  legajo, auditor, YPF/Esso pin) · Vehículos (`vehiculo_chofer`).
- **Validaciones ABM:** código, apellido, 1º nombre, nº registro, vto registro, f. nacimiento,
  f. ingreso, lista de liquidación.
- **Vencimientos críticos:** `registro_v` (registro), `registro_3` (CNRT), `registro_4` (AEP).
  En Blazor resaltados rojo (vencido) / ámbar (≤30 días).

**Blazor:** `Components/Pages/Choferes.razor` (`/choferes`) + `ChoferDetalleDialog.razor`.
Métodos `GetChoferesAsync` / `GetChoferDetalleAsync` en `ReportService`.

Mapa de columnas truncadas → ver SKILL.md (sección "Columnas truncadas") y `CHOFER_ABM.md`.
