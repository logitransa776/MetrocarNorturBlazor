# Lógica FoxPro — `gps_xlm()`: la integración GPS (Fase 0.2 del plan Buslink)

> Fuente: **`C:\MetroCarSys\Progs\procesos.prg`** (Function `gps_xlm`, línea 41; +
> `existeViajeSql`, `armaStringSql`, `gps_xml_muestra`). Extraído 02/07/2026.
> **Quién la llama** (verificado grepeando los binarios `.sct`): `trafico_asigna` (ASIGNO),
> `trafico_reasigna` (RE-ASIGNO), `trafico_liberar` (FINALIZO), `trafico_zoom` (CANCELO),
> `reserva_plantilla_armar` (ALTA por plantilla) y el legacy `trafico_liberar_2`.
> Era el **gap crítico Nº 2 de la Fase 0** — este doc lo cierra; la decisión final
> (replicar / stub / muerto) la firma el dueño.

---

## 🔴 CORREGIDO EL 12/08/2026 — la integración está VIVA, NO es un no-op

> **Este doc afirmaba lo contrario hasta el 12/08/2026 y estaba equivocado.** La conclusión
> "hoy es un NO-OP" se sacó leyendo la **réplica local** (`DESKTOP-CV6LF0O\SQLEXPRESS`), que
> es un snapshot viejo (17/07/2026). Contra los servidores **productivos** el flag SQL está
> **encendido**. Cualquier decisión tomada sobre la versión anterior de este doc hay que
> revisarla.

La función arranca leyendo `parametro` y sale sin hacer nada **sólo si los DOS flags están
apagados** — es un OR, no un AND:

```foxpro
If tmpParametroXLM.xml_envia Or tmpParametroXLM.SQL_GPS   && basta UNO en .T. para que corra
```

### Estado real, verificado el 12/08/2026 en los tres servidores

| Servidor | `sql_gps` | `sql_server` | `_updated_at` |
| --- | --- | --- | --- |
| **172.25.69.217** (productivo nuevo) | **1 — ACTIVO** | `192.168.0.8` | 22/07/2026 |
| **172.25.80.234** (productivo) | **1 — ACTIVO** | `192.168.0.8` | **12/08/2026** (réplica viva) |
| `DESKTOP-CV6LF0O` (local, snapshot viejo) | 0 | `SISTEMA01\SQLEXPRESS_AXOFT` | 17/07/2026 |

| Campo `parametro` (nombre SQL) | Valor productivo | Para qué es |
| --- | --- | --- |
| `xml_envia` | **0** | vía 1 (XML file-drop) — **ésta sí está muerta** |
| `sql_gps` | **1 — ACTIVO** | vía 2 (SQL Server externo) — **ésta CORRE** |
| `dir_xml` | `O:\METROCARSYS\XML\` | carpeta destino de los XML (sin uso) |
| `sql_server` | **`192.168.0.8`** | server SQL del sistema GPS (cambió: antes `SISTEMA01\SQLEXPRESS_AXOFT`) |
| `sql_base` / `sql_tabla` | `MetroCarSQL` / `Servicios` | base y tabla destino |
| `sql_usuari` / `sql_passwo` | `sa` / … | credenciales (texto plano) |
| `url_gps` | `http://metrocar.nortur.ar` | usado por otras pantallas (mapa), no por esta función |

### Cuánto pesa (medido contra producción, 12/08/2026)

- **136 clientes** con `cliente.envia_gps = 1` — incluida **AEROLINEAS ARGENTINAS**.
- **3.466 de 3.713 viajes** del último mes (**93 %**) pertenecen a esos clientes.
- Se dispara en **ASIGNO, RE-ASIGNO, FINALIZO, CANCELO** y en el armado de plantillas.

> ### 🔴 Implicancia para el día D
>
> **`gps_xlm()` NO se puede stubbear.** Si Buslink toma el circuito `viaje` sin replicar la
> vía 2, el feed que alimenta el seguimiento de 136 clientes se corta **en silencio** (nadie
> recibe un error: simplemente dejan de entrar filas en `Servicios`). Pasa de "riesgo 4,
> confirmar muerto" a **integración viva de entrega obligatoria antes del corte**.
>
> **Lo que falta confirmar** (no se pudo desde la PC de desarrollo): `192.168.0.8` responde
> ping, pero **el puerto SQL no contesta** desde ahí, así que está verificado que la bandera
> está en 1 y que el host vive, **pero no que los INSERT estén realmente entrando**. Hay que
> confirmarlo con el cliente o desde el servidor de Buslink — para eso está el botón
> **Conexión** de la solapa GPS de `/parametros`, que prueba exactamente eso.
>
> Preguntas que siguen abiertas con el dueño: ① ¿quién consume `MetroCarSQL.Servicios`
> (proveedor GPS, app propia)? ② ¿el cambio de `SISTEMA01\SQLEXPRESS_AXOFT` a `192.168.0.8`
> fue una migración de ese sistema? ③ ¿la vía XML se puede dar de baja formalmente?

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
