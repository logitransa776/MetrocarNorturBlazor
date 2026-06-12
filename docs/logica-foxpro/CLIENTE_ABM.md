# Lógica FoxPro — ABM de Clientes (`cliente.scx` + `cliente_abm.scx`)

> Menú: **Reservas → Clientes** (`openForm("cliente",,.t.)`).
> Patrón ABM de dos forms (lista + ABM). Relacionado: `cliente_abm_email.scx` (correos),
> `cliente_busca.scx` (buscador F5), `cliente_descuento*`, `cliente_tarifa*` (módulo precios).
> Extraído del binario con `foxpro-extract` (12/06/2026). 413 clientes activos.

---

## Lista (`cliente.scx`)

- Grilla de 13 columnas: código, razón social, teléfono, celular, **Inhabilitado**
  (`f_delete`), domicilio (+nro/piso/depto), localidad, % descuento, contacto1, contacto2.
- Check **"Ver Clientes Egresados"**: OFF (default) filtra `EMPTY(f_delete)`; ON muestra
  todos. Filas con `f_delete` cargada se pintan **amarillas**
  (`dynamicbackcolor IIF(!EMPTY(f_delete), amarillo, blanco)`).
- Textbox de búsqueda incremental por razón social (LOCATE con SET EXACT OFF).
- **Click en encabezado de columna = reordenar** por ese campo (BINDEVENT MouseUp →
  INDEX ON al vuelo; botón derecho = descendente). Patrón `clickmheader` reutilizable.
- Botón **Correos Electrónicos** → `cliente_abm_email.scx` (mantenimiento de
  `email1..email10` / contactos 1..10 — form aparte).
- Permisos por dígito en `cNivel`: Agregar `"2"`, Modificar `"3"` (también doble clic),
  Eliminar `"4"`. Sin permiso → `cartel("sin_permiso")`.
- Reposicionamiento post-ABM: variable pública `cClienteGoTo` → LOCATE.

## ABM (`cliente_abm.scx`)

Modos: `"alta"`, `"baja"`, `"modifica"`, `"consulta"`, `"cuit"` (consulta acotada que achica
el form — la usan otras pantallas para mostrar datos fiscales).

### Campos (→ columna SQL real en `cliente`)

| Control | Columna | Notas |
|---|---|---|
| Código | `id_cliente` (char 15) | inmutable en modifica |
| Razón Social | `razon_soci` (máx UI 40) | obligatorio |
| Domicilio / Nro / Piso / Dpto | `domicilio`, `domicilio_`, `domicilio2`, `domicilio3` | truncados en SQL |
| C. Postal / Localidad / Provincia | `cpostal`, `localidad`, `provincia` | provincia = combo array global `aProvincia` |
| Teléfono / Celular | `telefono`, `celular` | |
| Nº CUIT | `ncuit` | máscara 99-99999999-9, Valid → función global `_ValidaCUIT` (dígito verificador) |
| T. Resp | `tipo_resp` | combo de tabla `responsable_tipo` |
| E-Mail | `email` | Valid: debe contener `@` |
| Comentario | `comentario` | |
| 1º/2º Contacto + Cargo | `contacto1`, `cargo1`, `contacto2`, `cargo2` | |
| Descuento / Incremento | `descuento`, `incremento` | **excluyentes** (no pueden convivir ≠ 0); F5 = calculadora |
| Cod. sistema contable | `cairo` | obligatorio |
| Lista de Precio | `id_lista_p` | combo `lista_precio_modelo` no borrados |
| Obtención de precios | `ob_precio` | `"CLIENTE"` (tarifario propio en `cliente_tarifa`) o `"LISTA PRECIO"` |
| Emp de Facturación | `empresa_fc` | combo tabla `empresa`; obligatorio |
| Preferencia Facturación | `fc_prefere` | optiongroup **Cabecera** → `'C'` / **Servicio** → `'S'` |
| ARSA | `arsa` | flag |
| Pide pax al finalizar | `pide_pax` | flag (Tráfico pregunta pax al cerrar viaje) |
| Bus por 24 pax en transfer | `bus24` | flag |
| Pide Nº voucher al finalizar | `voucher` | flag |
| Envía datos al GPS | `envia_gps` + `envia_gps_` (tipo A/E/S) + `envia_gps2` (hora S/L) | check habilita los 2 combos; si ON ambos obligatorios |
| Fecha Inhabilitación | `f_delete` | editable solo en baja/modifica |
| Rubros de Adicionales Excluidos | tabla `cliente_adicional_excluido` | ver abajo |

### Validaciones de `audita_carga` (acumula errores → `form_error`)

1. Código obligatorio. 2. Razón social obligatoria. 3. `ob_precio` obligatorio.
4. `cairo` obligatorio. 5. `empresa_fc` obligatoria.
6. Incremento y descuento no pueden convivir (ambos ≠ 0).
7. `ob_precio = "LISTA PRECIO"` exige `id_lista_p`; `ob_precio = "CLIENTE"` exige
   `id_lista_p` **vacía**.
8. Si `envia_gps`: tipo y hora obligatorios.
(CUIT y email validan en el control, no acá.)

### Operaciones

- **Alta**: anti-duplicado de `id_cliente` → INSERT (no graba descuento — rareza heredada;
  la modificación sí lo graba). `f_create = DATE()`.
- **Baja**: confirma → **`UPDATE cliente SET f_delete = <fecha del form>`** (lógica, NUNCA
  DELETE). La fecha la elige el usuario (default vacía → cargarla).
- **Modifica**: UPDATE completo + `f_modify = DATE()`. Permite limpiar `f_delete`
  (re-habilitar cliente).
- **Rubros excluidos** (grilla lateral): Agregar → anti-duplicado → `INSERT INTO
  cliente_adicional_excluido (id_cliente, rubro)`; Eliminar → confirma → DELETE físico.
  Combo de tabla `adicional_rubro`. **Escriben directo, fuera del Aceptar** (en alta el
  cliente aún no existe — los botones operan sobre `cursorCliente.id_cliente`, o sea el
  cliente de la fila previa: bug heredado; en Blazor deshabilitarlos hasta grabar).

---

## Reglas no obvias

1. `cliente` usa **borrado lógico con fecha editable** — "egresado" = `f_delete` con valor;
   se puede volver a habilitar limpiándola.
2. `ob_precio` define de dónde sale el precio al facturar: lista modelo (`lista_precio`)
   o tarifario propio (`cliente_tarifa` con vigencias). Son excluyentes.
3. Los flags `pide_pax` / `voucher` cambian el comportamiento de Tráfico al FINALIZAR un
   viaje (pide datos extra). `bus24` autoriza enviar bus en transfers de 24 pax.
4. Los datos GPS replican la config de plantillas (entrada/salida × hora salida/llegada).
5. `cliente_adicional_excluido` filtra qué rubros de adicionales NO se le facturan.
6. La réplica SQL trunca: `razon_soci`, `domicilio_` (nro), `domicilio2` (piso),
   `domicilio3` (dpto), `id_lista_p`, `envia_gps_` (tipo), `envia_gps2` (hora),
   `fc_prefere`, `cierre_pla`, `plantilla_`.
