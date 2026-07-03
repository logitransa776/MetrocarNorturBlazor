# Lógica FoxPro — `gps_xlm()`: la integración GPS (Fase 0.2 del plan Buslink)

> Fuente: **`C:\MetroCarSys\Progs\procesos.prg`** (Function `gps_xlm`, línea 41; +
> `existeViajeSql`, `armaStringSql`, `gps_xml_muestra`). Extraído 02/07/2026.
> **Quién la llama** (verificado grepeando los binarios `.sct`): `trafico_asigna` (ASIGNO),
> `trafico_reasigna` (RE-ASIGNO), `trafico_liberar` (FINALIZO), `trafico_zoom` (CANCELO),
> `reserva_plantilla_armar` (ALTA por plantilla) y el legacy `trafico_liberar_2`.
> Era el **gap crítico Nº 2 de la Fase 0** — este doc lo cierra; la decisión final
> (replicar / stub / muerto) la firma el dueño.

---

## ⚡ Hallazgo central: HOY ES UN NO-OP

La función arranca leyendo `parametro` y **sale sin hacer nada** si los dos flags están
apagados:

```foxpro
If tmpParametroXLM.xml_envia Or tmpParametroXLM.SQL_GPS   && si ambos = .F. → no-op
```

**Verificado contra la réplica (02/07/2026): `parametro.xml_envia = 0` y
`parametro.sql_gps = 0`** → cada llamada desde asignar/reasignar/finalizar/cancelar/armar
hoy **no envía nada a ningún lado**. Configuración residual encontrada:

| Campo `parametro` (nombre SQL) | Valor actual | Para qué era |
| --- | --- | --- |
| `xml_envia` | **0** | habilita la vía 1 (XML file-drop) |
| `sql_gps` | **0** | habilita la vía 2 (SQL Server externo) |
| `dir_xml` | `O:\METROCARSYS\XML\` | carpeta destino de los XML |
| `sql_server` | `SISTEMA01\SQLEXPRESS_AXOFT` | server SQL del sistema GPS |
| `sql_base` / `sql_tabla` | `MetroCarSQL` / `Servicios` | base y tabla destino |
| `sql_usuari` / `sql_passwo` | `sa` / … | credenciales |
| `url_gps` | `http://metrocar.nortur.ar` | usado por otras pantallas (mapa), no por esta función |

Además, **136 clientes activos tienen `cliente.envia_gps = 1`** — configuración que quedó
cargada de cuando la integración estaba viva.

> **Implicancia para la decisión de Fase 0:** la evidencia apunta a **"confirmar muerto"**
> (flags apagados en producción). Preguntas para cerrar con el dueño: ① ¿el server
> `SISTEMA01\SQLEXPRESS_AXOFT` existe todavía? ② ¿el proveedor GPS consume la tabla
> `Servicios` o la carpeta `O:\...\XML`? ③ ¿hay OTRO mecanismo GPS vigente (la pantalla
> del mapa usa `url_gps`)? Si se confirma muerto → stub no-op con log en `ViajeAbmService`.

---

## Qué hace cuando está encendida

### 1. Selección del viaje (filtro por cliente)

```foxpro
SELECT ... FROM viaje a
  INNER JOIN cliente b ON a.id_cliente = b.id_cliente
  LEFT  JOIN chofer  c ON a.id_chofer  = c.id_chofer
WHERE id_viaje = lpID_viaje AND b.envia_gps
```

⚠️ **Solo notifica viajes de clientes con `envia_gps` prendido** — si el cliente no lo
tiene, la función no hace nada aunque los flags globales estén encendidos.

### 2. Vía 1 — XML file-drop (`xml_envia`)

`CURSORTOXML` → archivo **`<dir_xml>\<id_viaje>.xml`** con ~28 campos del viaje (horarios,
cliente, chofer + celular/DNI del chofer, destinos, cabecera, pax, estado...). Trivial de
replicar en .NET si hiciera falta (serializar a XML y escribir el archivo).

### 3. Vía 2 — Escritura directa en el SQL Server del GPS (`sql_gps`)

Conecta por ODBC al server externo y hace **INSERT o UPDATE en la tabla `Servicios`**
(si el `id_viaje` ya existe → UPDATE_ALL):

```sql
INSERT INTO Servicios ( id_viaje , id_cliente , razon_social , conductor , desde , hasta ,
  f_ini , f_fin , id_vehiculo , id_vehiculo_tipo , pax , estado_viaje , cabecera ,
  id_interno , EstadoMc , FechaMc , interno ) VALUES ( ... )
```

- `f_ini` = `hs_inicio`, `f_fin` = `hs_fin`; `cabecera` = **`recorrido_celular`** del viaje
  (no la columna `cabecera` — el SELECT la renombra); `conductor` = nombre del chofer
  (de la tabla `chofer` si existe, sino el desnormalizado).
- **`EstadoMc`** (estado para el sistema del GPS): `SIN ASIGNAR → 'S'` ·
  `ASIGNADO / FINALIZADO → 'N'` · `CANCELADO → 'B'` · cualquier otro → `'B'`.
  `FechaMc = DATETIME()` del envío.
- Hay una tercera variante `UPDATE_ESTADO` (solo EstadoMc/FechaMc) definida en
  `armaStringSql` pero **esta función no la usa**.

### 4. `gps_xml_muestra` (filtro E/S por cliente)

Decide si el recorrido se muestra según `cliente.envia_gps_tipo` (réplica: `envia_gps_`):
`A` = ambos, `E` = solo entradas (posición 7 de la cabecera = 'E'), `S` = solo salidas.
🐛 **Bug heredado**: el caso `"S"` chequea `= "E"` (copy-paste del caso E) — las salidas
nunca matchean su propio filtro.

---

## Recomendación para Buslink (alineada con el plan)

1. **Hook aislado y apagable** en `ViajeAbmService` (Fase 2 punto 3): `IGpsNotifier` con
   implementación no-op por defecto. Las transacciones NUNCA dependen del hook.
2. Si el dueño confirma muerto (probable): dejar el stub con log informativo y cerrar el
   ítem 2 de Fase 0. Los campos `parametro.*` y `cliente.envia_gps` quedan como archivo.
3. Si estuviera vivo: ambas vías son baratas en .NET (XML serializer / `SqlConnection` al
   server externo con el mapeo `EstadoMc` de arriba). Probar con el proveedor antes del
   día D (checklist 3.1).
4. Los call-sites a replicar son exactamente los 4 del plan (ASIGNO, RE-ASIGNO, FINALIZO,
   CANCELO) + el alta por plantillas (armar) — verificado contra los binarios.
