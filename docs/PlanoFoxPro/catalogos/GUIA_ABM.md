# Lógica FoxPro — Guías (`guia.scx` + `guia_abm.scx`)

> Menú: **ABM del sistema → Guias** (permiso letra `A`, `MENU_PRINCIPAL.MPR` BAR 8).
> Buscador: `guia_busca.scx`. Utilitarios: `guia_repara.scx`.
> Extraído del binario con `foxpro-extract` (02/07/2026). **1.141 guías, todas activas.**
> **Tabla del GRUPO B del plan Buslink**: el ABM se construye antes, pero el **cutover es
> el día D** — la carga de reservas FoxPro hace upsert de guías hasta el último día.

---

## Concepto

Catálogo de guías de turismo (acompañan tours/cenas show). Dos vías de escritura conviven:

1. **Este ABM** (alta/baja/modifica manual) — poco usado.
2. **El upsert automático de la carga de reservas** (la vía dominante — por eso hay 1.141):
   si el nombre no existe → `INSERT (nombre, telefono)`; si existe con otro teléfono →
   `UPDATE telefono` (ver `../reservas/RESERVA_TRANSPORTACION.md` §3). Las plantillas
   además graban `id_guia` + `guia_dueno` N/C/S en `reserva_plantilla`.

**Tabla `guia`** (verificada contra `sys.columns`, 02/07/2026): `id` (autoinc), `id_guia`,
`nombre`, `apellido`, `nombre1`, `nombre2`, `telefono`, `celular`, `domicilio`,
**`domicilio_`** (nro), **`domicilio2`** (piso), **`domicilio3`** (dpto), `cpostal`,
`localidad`, `provincia`, `tdoc`, `ndoc`, `ncuit`, `ncuil`, `email`, `comentario`, `radio`,
`f_ingreso`, **`registro_n`** / **`registro_v`** (nro/vto registro), `lunes`..`domingo`
(7 flags), `f_create`, `f_delete`, `f_modify`.

> La tabla tiene **estructura tipo `chofer`** (días de trabajo, registro, CUIL...) pero el
> ABM solo usa un subconjunto chico — el resto quedó sin UI. Mismos truncados a 10 chars
> que `chofer` (`domicilio_`, `domicilio2/3`, `registro_n/v`).

## Lista (`guia.scx`)

- Grilla de **15 columnas**: Codigo (`id`), Nombre, Telefono, Inhabit. (`f_delete`),
  Domicilio, Nro, Piso, Depto, Localidad, C. Postal, Provincia, Celular, T. Doc, Nº Doc,
  Vto. Registro. Orden por nombre. Filas con `f_delete` en **amarillo**.
- **Permisos estándar** ✓: Agregar `"2" $ cNivel`, Modificar `"3"`, Eliminar `"4"`
  (sin permiso → `cartel("sin_permiso")`). Doble clic = Modificar.
- **Buscar** → `guia_busca` (busca por `id_guia` — devuelve en `cBuscarGuia`).
- Reposicionamiento: `cGuiaGoTo` / `nBuscarGuiaId` (nombres de variable inconsistentes
  en el fuente: la baja setea `nGuiaGoTo`, que no existe — inocuo).

## ABM (`guia_abm.scx`) — "ABM de Guias"

Modos `alta` / `baja` / `modifica` / `consulta` (consulta acepta un `id` por parámetro —
la usan otras pantallas).

**Campos del form**: Apellido (máx 40, MAYÚSCULAS), Id (readonly), Teléfono, Domicilio +
Nro/Piso/Depto, C.Postal, Localidad, Provincia (combo array global `aProvincia`), Tipo Doc
(combo `aTipoDoc`), Nro Doc, Fecha Inhabilitación (`f_delete`, solo lectura salvo modifica),
E-Mail (Valid: debe contener `@`; se graba en minúsculas), Comentario.

### Validaciones (`audita_carga`)

1. Apellido obligatorio. 2. Teléfono obligatorio. (Nada más.)

### Operaciones

- **Alta**: anti-duplicado por `nombre` →
  `INSERT INTO guia (nombre, apellido, domicilio, domicilio_nro, domicilio_piso,
  domicilio_dpto, cPostal, localidad, provincia, telefono, celular, tDoc, nDoc, email,
  comentario)`. ⚠️ Bugs heredados: **`nombre` y `apellido` se graban con el MISMO valor**
  (el textbox "Apellido"); la variable `f_create = Date()` se calcula pero **NO está en el
  INSERT** (las altas manuales quedan sin fecha de creación); **no graba `id_guia`**
  (la clave que después usan las plantillas).
- **Baja**: confirma → **`DELETE FROM guia WHERE id = nId` — FÍSICA**, pese a que
  `f_delete` existe y la grilla pinta amarillo (contradicción heredada: hay filas viejas
  inhabilitadas lógicamente, pero el botón actual borra físico). En Blazor: **baja lógica**
  — el upsert de reservas recrearía el guía igual si se borra físico.
- **Modifica**: UPDATE de los mismos campos (otra vez `nombre = apellido = mismo valor`),
  **sin `f_modify`** y **sin anti-duplicado**.
- Trampa de UI: el textbox `celular` está **deshabilitado y fuera del área visible** del
  form (Top 300 > alto 283) — el celular NUNCA se editó desde este ABM aunque la grilla
  lo muestre.

## Reglas no obvias

1. **La clave de matcheo del circuito es el NOMBRE**, no `id` ni `id_guia`: la reserva
   graba el texto `"NOMBRE : TELEFONO"` en `viaje.nombre_gui` y el upsert busca por nombre.
   Renombrar un guía acá NO arrastra viajes ni plantillas.
2. `id_guia` la escriben las **plantillas** (`'GUIA CLIENTE'` para guías del cliente),
   no este ABM. El buscador `guia_busca` busca por `id_guia` — puede no encontrar guías
   dados de alta manualmente (que la tienen vacía).
3. **Grupo B / día D**: apagar la sync de `guia` antes del día D rompería el upsert de la
   carga de reservas FoxPro. Construir el ABM Blazor + dejarlo detrás del feature flag.
4. En Blazor, unificar la escritura: el upsert del motor (`ViajeAbmService`, Fase 2 punto 5)
   y este ABM deben compartir la misma primitiva.
