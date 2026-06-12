# Lógica FoxPro — Destinos (`destino.scx` + `destino_abm.scx`)

> Menú: **Reservas → Destinos**. Buscador: `destino_busca.scx` (F5 en Desde/Hasta de la
> carga de reservas). Relacionado: `destino_repara.scx` (Utilitarios).
> Extraído del binario con `foxpro-extract` (12/06/2026). 398 destinos.

---

## Concepto

Catálogo de lugares origen/destino de los viajes. Alimenta el **autocomplete** de
Desde/Hasta en reservas y plantillas. Tabla `destino`: `id` (autoinc), `id_destino`,
`destino` (nombre, clave lógica), `direccion`, `localidad`, `telefono`, `correo`,
`contacto`, `cabecera`, **`mas100km`** (flag). **Sin `f_delete`** — baja física.
Tabla satélite `destino_localidad` (solo `localidad`) para el combo.

El campo `destino.mas100km` se copia a `viaje.mas100km` cuando el "Hasta" de una reserva
matchea un destino (recargo por distancia). La `localidad` del "Desde" autollena el
Districto Inicio (`d_destino_`): CAPITAL FEDERAL / BUENOS AIRES.

## Lista (`destino.scx`)

- Grilla 3 columnas: destino, dirección, localidad (orden por destino).
- Búsqueda incremental por nombre + botón Buscar (`destino_busca`).
- Permisos: alta `"2"`, baja `"4"`; **Modificar y Consulta no chequean permiso** (rareza
  heredada; en Blazor aplicar `"3"` a modificar).
- Doble clic = modificar.

## ABM (`destino_abm.scx`)

Modos `alta` / `baja` / `modifica` / `consulta`. Todos los campos se graban en MAYÚSCULAS
(`UPPER`).

- **Alta**: solo `destino` es obligatorio; anti-duplicado por nombre → INSERT
  (destino, direccion, localidad, telefono, contacto, correo, cabecera, mas100km).
- **Baja**: sin validación de referencias → **`DELETE FROM destino WHERE id = …` (física)**.
- **Modifica**: UPDATE por `id`. ⚠️ Bug heredado: `contacto = contacto` (se asigna a sí
  mismo — los cambios de contacto en modificación NUNCA se guardan). En Blazor corregirlo.
- **Nueva Localidad**: INPUTBOX → anti-duplicado → `INSERT INTO destino_localidad`.
- El campo `cabecera` está deshabilitado en el form (se carga por otra vía).
- El fuente referencia un botón `google` (mapa) que no existe en el `.scx` del disco —
  señal de que el exe productivo es más nuevo que este fuente.

## Reglas no obvias

1. El nombre (`destino`) es la clave de matcheo del autocomplete: los viajes guardan el
   TEXTO, no el id — renombrar un destino no arrastra viajes históricos.
2. `mas100km` y `localidad` tienen efectos colaterales en la carga de reservas (recargo y
   distrito).
3. Baja física sin chequeo de uso. En Blazor: avisar si hay viajes que lo referencian
   (por texto en `d_destino`/`h_destino`).
4. "Agregar Destino" de la carga de reservas abre este mismo ABM en modo alta.
