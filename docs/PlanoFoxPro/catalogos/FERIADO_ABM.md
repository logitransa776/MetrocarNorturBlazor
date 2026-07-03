# Lógica FoxPro — Feriados (`feriado.scx` + `feriado_ver.scx`)

> Menú: **ABM del sistema → Feriados** (permiso letra `A`, `MENU_PRINCIPAL.MPR` BAR 17).
> Extraído del binario con `foxpro-extract` (02/07/2026).
> **ABM Nº 2 de la Fase 1 del plan Buslink.** El más chico de todo el sistema.

---

## Concepto

Calendario de feriados. Lo consume el **armado de plantillas** (`reserva_plantilla_armar`):
un feriado EXCLUYE la generación de viajes ese día, salvo que el check "Feriados" esté
marcado (ver `../reservas/RESERVA_PLANTILLAS.md`). También la función global
`_es_feriado(fecha)` (`procesos.prg`).

**Tabla `feriado`** (verificada contra `sys.columns`): `id` (autoinc), `fecha`. Nada más.
**Datos: 15 filas — ⚠️ CERO feriados cargados para 2026** (verificado 02/07/2026).

> 🚨 **Alerta operativa (precaución del plan, Fase 1):** antes de apagar la sync de esta
> tabla hay que cargar en FoxPro todos los feriados restantes del año — hoy no hay NINGUNO
> de 2026, lo que además significa que el "armar plantillas" actual genera viajes en
> feriados como si fueran días comunes.

## El form (`feriado.scx`) — ABM inline, sin form `_abm`

Único catálogo con el alta/baja **dentro de la misma pantalla** (no abre dialog):

- Grilla de 1 columna (`fecha`, orden descendente).
- **Agregar** / **Eliminar** activan el panel inferior: textbox de fecha (F5 = calendario)
  + botones Aceptar/Cancelar. Eliminar precarga la fecha de la fila seleccionada.
- **Alta**: fecha obligatoria + anti-duplicado → `INSERT INTO feriado (fecha) VALUES (dFecha)`.
- **Baja**: **`DELETE FROM feriado WHERE fecha = dFecha` — FÍSICA y por FECHA** (no por id).
- Sin permisos 2/3/4, sin confirmación de baja, sin `f_delete`, sin auditoría.
- Variable pública `dBuscarFecha`: otros forms pueden abrirlo pre-posicionado
  ("Ver Feriados" del armado de plantillas).

## `feriado_ver.scx` — "Lista de feriados a procesar"

No es ABM: es el dialog de **confirmación** que muestra el armado de plantillas con los
feriados del rango elegido (listbox sobre el cursor `_tp_feriado`) y devuelve
Aceptar/Cancelar. Se migra junto con el Armar (Fase 4), no con este catálogo.

## Reglas no obvias

1. Baja **física por fecha** — si la réplica trae el delete, la fila desaparece. No hay
   histórico de feriados borrados.
2. No hay validación de año/rango: se puede cargar cualquier fecha (en Blazor: validar
   rango razonable y avisar duplicados de año).
3. La tabla es leída por el circuito FoxPro (armar plantillas) hasta el día D →
   **cutover temprano pero con la precaución de pre-cargar el año** (ver alerta arriba).
4. En Blazor: es el candidato ideal a segunda entrega tras `viaje_motivo_cancela` —
   una grilla + un date picker. Mejoras: confirmación de baja, carga masiva de feriados
   nacionales del año, aviso si el armado de plantillas cae en un rango sin feriados cargados.
