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
4. **`nextel` NO es editable** (verificado 06/07/2026): el campo existe en la tabla pero en el
   form el textbox nextel está comentado (`*Thisform.nextel.enabled`) y NO figura en el
   INSERT/UPDATE. En Blazor: no ofrecerlo en el editor (o mostrarlo solo lectura).
5. **Campos del editor y largos (MaxLength del form):** `id_operado` código=15 · `nombre`=40 ·
   `id_cliente`=15 · `telefono`=20 · `celular`=20 · `interno`=70 · `email`=70 (valida que
   contenga `@`) · `comentario`=70. El email se graba en minúsculas (`LOWER`); el resto en
   MAYÚSCULAS (`Format="!"`). Obligatorios: código, cliente (debe existir) y nombre.
