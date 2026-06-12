# Lógica FoxPro — Operadores (`cliente_operador.scx` + `cliente_operador_abm.scx`)

> Menú: **Reservas → Operadores**. Buscador: `cliente_operador_busca.scx` (F5 desde la
> carga de reservas, filtrado por cliente).
> Extraído del binario con `foxpro-extract` (12/06/2026). 128 operadores.

---

## Concepto

Un **operador** es la persona de contacto dentro de un cliente (la operadora de la agencia
que pide la reserva). Tabla `cliente_operador`: `id` (autoinc), `id_operado` (código char,
clave lógica global), `id_cliente` (FK), `nombre`, `email`, `telefono`, `nextel`, `celular`,
`interno`, `comentario`. **Sin `f_delete`** — baja física.

En la reserva se graba `viaje.id_operado`. El combo/buscador de la carga de reservas se
filtra por el cliente elegido.

## Lista (`cliente_operador.scx`)

- Grilla 9 columnas: id, id_operador, nombre, razón social (JOIN con `cliente`), teléfono,
  interno, celular, nextel, email. Orden por nombre.
- ⚠️ El JOIN es `FROM cliente_operador a, cliente b WHERE a.id_cliente = b.id_cliente`
  (inner): un operador cuyo cliente no exista **desaparece de la lista**.
- Búsqueda incremental por nombre. Buscar → `cliente_operador_busca`.
- Permisos estándar: alta `"2"`, modifica `"3"`, baja `"4"` en `cNivel`.

## ABM (`cliente_operador_abm.scx`)

Modos `alta` / `baja` / `modifica` / `consulta`. Puede recibir el código de cliente como
2º parámetro (cuando se abre desde otra pantalla deja el cliente fijo).

- **Validaciones** (en el Click, no hay audita_carga real): código operador, cliente y
  nombre obligatorios. Email con `@` (Valid del control). Cliente debe existir (lookup).
- **Alta**: anti-duplicado global de `id_operado` → INSERT.
- **Baja**: confirma → **`DELETE FROM cliente_operador WHERE id_operador = …` (física)**.
- **Modifica**: UPDATE por `id_operado` (no actualiza f_modify — no existe el campo).
- F5 en cliente → `cliente_busca`.

## Reglas no obvias

1. `id_operado` es único **global**, no por cliente.
2. Baja física, sin papelera — si el operador tiene viajes históricos, `viaje.id_operado`
   queda huérfano (el FoxPro no lo valida). En Blazor: validar referencias antes de borrar
   o pasar a baja lógica.
3. Columna SQL truncada: `id_operado`.
