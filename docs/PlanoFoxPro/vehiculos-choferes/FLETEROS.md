# Fleteros — `fletero.scx` + `fletero_abm.scx`

> **Migrado a Blazor (solo lectura + andamiaje ABM) — 05/07/2026.**
> Lista `Components/Pages/Fleteros.razor` (`/fleteros`) + ficha/editor
> `Components/Shared/FleteroEditorDialog.razor`. Permiso `'V'`, menú **Vehículos y Choferes →
> Fleteros**. La ESCRITURA está construida en `AbmService` (`AltaFleteroAsync` /
> `ModificaFleteroAsync` / `BajaFleteroAsync`) pero **deshabilitada** (`_abmActivo=false`) — la
> tabla `fletero` sigue con dueño FoxPro.

## Qué es

Transportista contratado / razón social que aporta vehículos y choferes. Contraparte de
`uso='CONTRATADO'` en `vehiculo` y del campo `fletero` en `chofer`/`vehiculo`. **Catálogo
compartido con Facturación** (mismo form en ese menú) — sus tarifas `id_lista_p`/`id_lista_2`
son de **pago al fletero**, del módulo Facturación/Liquidación.

## Lista (`fletero.scx`)

`SELECT * FROM fletero ORDER BY orden, nombre`. Egresado (`f_delete`) en amarillo. Botones
Agregar/Eliminar/Modificar (permisos 2/3/4) + Salir.

## ABM (`fletero_abm.scx`) — lógica real extraída

Validaciones (modo alta/modifica):
```foxpro
If Empty(id_contratado) → "No se ha cargado el codigo del contratado"
If Empty(nombre)        → "No se ha cargado el nombre"
If Empty(id_lista_precio) AND Empty(id_lista_personal)
                        → "No se ha cargado ninguna lista para liquidar servicios"
```
- **Alta:** `INSERT INTO fletero (id_contratado, razon_social, nombre, orden, id_lista_precio,
  id_lista_personal, modo_liq, f_create, …)`. Si el INSERT falla → aviso "Verifique el codigo del
  fletero o del Cronograma" (choca PK duplicada / cronograma inválido).
- **Modifica:** `UPDATE … SET razon_social=…, id_lista_personal=…, modo_liq=…, f_delete=dF_delete`
  (puede reactivar editando `f_delete`). La PK `id_contratado` NO se edita.
- **Baja:** confirma "¿Está totalmente seguro de procesar la baja?" → `UPDATE fletero SET
  f_delete = Date() WHERE id_contratado = cId_contratado`. Nunca borra físico.
- Hay un validador de **CUIT** (`if !empty CUIT → chequea dígito verificador`).

## Mapeo de columnas (→ SQL real)

| Form FoxPro (largo)   | Columna SQL real | Notas |
|-----------------------|------------------|-------|
| `id_contratado`       | `id_contrat`     | PK lógica (nvarchar 15), inmutable en modifica |
| `razon_social`        | `razon_soci`     | obligatorio (nvarchar 50) |
| `nombre`, `orden`     | iguales          | `orden` = bigint (orden en combos) |
| `id_lista_precio`     | `id_lista_p`     | tarifa de pago (Facturación) |
| `id_lista_personal`   | `id_lista_2`     | tarifa de pago personal |
| `modo_liq`, `fc_prefere` | iguales       | 1 char |
| resto                 | iguales          | cuit, tipo_resp, domicilio, localidad, postal, provincia, telefono, celular, email, contacto, diagrama |
| PK física             | `id` (int, **NO identity**) | alta = `MAX(id)+1` |

## Decisiones vs FoxPro (Blazor)

- **Lista de precios NO obligatoria en la validación Blazor** (el FoxPro exige al menos una). Se
  relajó porque las tarifas son del módulo Facturación y el catálogo puede cargarse sin tarifa y
  completarse después; se puede re-endurecer al activar el ABM si el cliente lo pide. Anotado en
  `AbmService.ValidarFletero`.
- **Andamiaje:** botonera Agregar/Modificar/Eliminar visible pero deshabilitada; "Ver ficha"
  activo (dialog modo "ver"). El día del corte a Buslink: `_abmActivo=true` + bloquear ABM en
  FoxPro + apagar sync de `fletero` + **coordinar con Facturación** (una sola fuente de verdad).
- Ficha en 3 secciones: Identificación · Datos (fiscal/contacto) · Facturación al fletero.
- **Excel** de la grilla (código, razón, nombre, CUIT, localidad, teléfono, email, baja).

## Validación (05/07/2026)

28 fleteros en la réplica (22 activos / 6 de baja). Grilla, ficha "ver" (VIABUS/AGRIPINA SRL) y
Excel verificados por captura. Datos idénticos a `SELECT * FROM fletero`.
