# Parámetros del sistema — Empresa, Generales y GPS

> **Menú FoxPro:** `ABM del sistema → Parámetros Generales →` submenú de 4 ítems
> (`Parametros Generales` · `Parametros Empresa` · `Parametros Pantalla Trafico` ·
> `Parametros SQL Server para GPS`).
> **Forms cubiertos por este doc:** `parametro.scx` (Generales, 105 objetos),
> `parametro_empresa.scx` (Empresa, 42 objetos) y `parametro_sql_server.scx` (GPS, 26 objetos).
> **Fuera de alcance (documentar cuando se migre):** `parametro_trafico.scx` (44 objetos).
> **Extraído:** 12/08/2026 · **Volumen:** tabla `parametro` = **1 fila, 72 columnas**.

---

## 1. Concepto: no son ABMs, son editores de una fila única

Ninguna de las tres pantallas tiene lista, ni alta, ni baja. Las tres siguen el mismo esquema
minimalista:

1. **`Init`** → `SELECT * FROM parametro INTO CURSOR` y vuelca los valores en los controles.
2. **`Aceptar`** → un único `UPDATE parametro SET ...` **sin `WHERE`** (la tabla tiene una
   sola fila), y `Thisform.Release`.
3. **`Cancelar`** → `Thisform.Release` a secas, sin preguntar si había cambios pendientes.

No hay transacción, ni chequeo de permisos dentro del form, ni auditoría (`f_modify` no
existe en esta tabla), ni concurrencia: el último que aprieta Aceptar gana.

### 🔴 Lo que manda sobre todo: en esa fila viven los contadores del circuito

En la **misma fila** conviven dos naturalezas distintas:

| Naturaleza | Columnas | Frecuencia de cambio |
| --- | --- | --- |
| Configuración estática | los 15 de Empresa + los ~35 de Generales | una vez cada varios años |
| **Contadores vivos del circuito** | `id_viaje_i`, `lote_plant` (44566), `lote_sobre` (1768), `stock_movi` | permanentemente, todo el día |

Como la réplica DBF→SQL pisa **la fila entera**, no existe "dueño parcial por columna": o la
tabla es de FoxPro, o es de SQL. Por eso `parametro` figuraba entre las **12 tablas que cambian
de dueño el día D** (`docs/buslink/PLAN_MIGRACION_BUSLINK.md`) — hasta que se adelantó el corte.

→ **Cómo se resolvió (12/08/2026):** el cliente **desconectó `parametro` del watcher** (sync
DBF→SQL apagada para esta tabla), igual que se hizo con `usuario`. Con eso el dueño pasa a ser
SQL y la escritura quedó **ACTIVA** (`AbmFeatureFlags.ParametrosAbmActivo = true`) — es el 2º
ABM de escritura real del proyecto.

> 🔴 **La deuda que deja ese corte anticipado.** FoxPro **sigue incrementando los contadores en
> su DBF** y esos incrementos ya no llegan a SQL: las dos copias divergen desde el 12/08/2026.
> Hoy no molesta (Buslink todavía no genera lotes ni viajes), pero **el día D hay que
> resincronizar `id_viaje_i`, `lote_plant`, `lote_sobre` y `stock_movi`** antes de habilitar
> las escrituras del circuito, o el primer lote/viaje que arme Buslink sale repetido.
> Ya está anotado en el paso 4 del checklist del corte del plan.
>
> Con `usuario` esto no pasó: no tiene contadores compartidos. Es la diferencia entre las dos
> tablas, y la lección general: al desconectar una tabla del watcher, preguntarse **qué columnas
> de esa fila sigue escribiendo FoxPro**.

**Regla que sigue vigente igual:** escribir **solo las columnas de la pantalla**, una por una,
con `SqlParameter`. **Nunca** `SELECT *` + reescritura de fila completa — pisaría los contadores.

Verificado 12/08/2026: la tabla y su fila existen en los dos servers de producción
(`172.25.69.217` y `172.25.80.234`).

---

## 2. Parámetros Empresa (`parametro_empresa.scx`)

### 2.1 Los 15 campos — mapeo FoxPro → SQL

⚠️ La réplica **trunca los nombres a 10 caracteres**. Verificado contra `sys.columns`.

| Etiqueta en pantalla | Campo FoxPro | **Columna SQL** | Tipo | Valor en producción |
| --- | --- | --- | --- | --- |
| Nombre | `empresa_nom` | `empresa_no` | nvarchar(60) | `NORTUR SRL` |
| Direccion | `empresa_dir` | `empresa_di` | nvarchar(60) | `B. DE IRIGOYEN 146   3º A` |
| Nº de Cuit | `empresa_cuit` | `empresa_cu` | nvarchar(26) | `30-70722951-5` |
| Tasa de IVA | `pIva` | `piva` | decimal(5) | **`0.00`** |
| Telefono | `empresa_tel` | `empresa_te` | nvarchar(50) | `5218-5103` |
| Inscripcion Reg. Nac | `empresa_hab` | `empresa_ha` | **int** | `9763` |
| Vencimiento Circuito | `empresa_vto` | `empresa_vt` | date | **`29/08/2009`** |
| Circuito Cerrado | `empresa_cir` | `empresa_ci` | nvarchar(20) | `B` |
| Logo | `logo` | `logo` | nvarchar(200) | `O:\METROCARSYS\GRAPHICS\LOGO\NORTUR-LOGO-AL-30.JPG` |
| Correo · Nombre | `smtp_nombre` | `smtp_nombr` | nvarchar(200) | `Dto. Trafico Nortur SRL <traficonortur@nrumbos.com.ar>` |
| Correo · Servidor | `smtp_server` | `smtp_serve` | nvarchar(100) | `mr.fibercorp.com.ar` |
| Correo · Usuario | `smtp_usuario` | `smtp_usuar` | nvarchar(200) | `traficonortur@nrumbos.com.ar` |
| Correo · Password | `smtp_password` | `smtp_passw` | nvarchar(30) | **texto plano** |
| Correo · Puerto | `smtp_puerto` | `smtp_puert` | bigint | `25` |
| Firma del correo | `smtp_firma` | `smtp_firma` | nvarchar(508) | 2 renglones |

### 2.2 Validaciones (hay una sola)

**CUIT** (`empresa_cuit.Valid`) — el único control validado del form:

```foxpro
IF !EMPTY(this.Value)
    if len(allt(this.value)) # 13          && exige la máscara 30-12345678-1
        messageBox("¡ Problemas en la carga de CUIT. Ej.: 30-12345678-1 ", 64, "Lea atentamente")
        this.value = "" ; retu .f.
    endif
    if !_ValidaCUIT(this.value)            && dígito verificador módulo 11
        messageBox("¡ No se cargo correctamente el Nro. de CUIT !  Intente nuevamente", 64, ...)
        this.value = "" ; retu .f.
    ENDIF
ENDIF
```

`_ValidaCUIT` (`Progs/funcion.prg:339`) — pesos **5,4,3,2,7,6,5,4,3,2** aplicados a los
dígitos 1,2,4..11 de la cadena con máscara (salta los guiones de las posiciones 3 y 12);
verificador = `11 − (suma mod 11)`, y `0` si el resto es 0. Vacío se considera válido.

Otras reglas de los controles:

- `empresa_nom` y `empresa_dir` tienen `Format = "!"` → **fuerzan mayúsculas**. El resto no.
- `empresa_cuit` tiene `MaxLength = 13`. Ningún otro control limita el largo.
- El textbox del logo está `Enabled = .F.` — se completa solo por el botón `.....` (`GETPICT()`).

### 2.3 `UPDATE` del botón Aceptar

Escribe las 15 columnas de la tabla anterior, sin revalidar el CUIT y sin `WHERE`.

### 2.4 🐛 Botón "Probar envio correo" — tres defectos

```foxpro
Update parametro Set smtp_nombre = ..., smtp_server = ..., smtp_usuario = ...,
                     smtp_password = ..., smtp_puerto = ...          && ← ①
...
aguarde("Enviando correo a : jlsilvamtb@gmail.com" , .t. )           && ← ②
If envio_correo_gmail( "jlsilvamtb@gmail.com" , "Correo de prueba de configuración" , cBody )
```

1. **Graba los 5 campos SMTP en la base ANTES de probar.** Si después el usuario aprieta
   Cancelar, el SMTP ya quedó cambiado igual.
2. **El destinatario está hardcodeado**: `jlsilvamtb@gmail.com` — la casilla del
   desarrollador original del FoxPro, no de NORTUR.
3. `envio_correo_gmail` (`funcion.prg:1149`) usa **CDO** (COM de Windows) forzando
   `smtpusessl = .T.` contra el puerto **25**. SSL implícito sobre 25 es contradictorio:
   es muy probable que el envío **hoy esté roto** y nadie se haya enterado.

**Decisión Buslink (12/08/2026):** se migra **corregido** — prueba sin grabar, contra la
config que hay en pantalla, y el destinatario se pide (por defecto, el del usuario logueado).

### 2.5 Quién consume estos campos

| Campo | Consumidores en FoxPro | Estado en Buslink |
| --- | --- | --- |
| `empresa_*` | `trafico_pasajero_planilla.scx` + reportes `viaje_pasajero.frx` / `viaje_pasajero_cnrt_1.frx` (CNRT) | Lista de Pasajeros migrada, pero **no muestra** datos de empresa |
| `piva` | `facturacion_cliente_nueva.scx`, `liquidacion_fletero_nueva.scx` | leído y cacheado 30 min (`ReportService.cs:4548`) |
| `logo` | `login.scx` (splash), `chofer_sancion`, facturación, liquidación fletero | Buslink usa su propio logo (`wwwroot/images`) |
| `smtp_*` | `envia_correo_electronico.scx`, `libro_novedad_abm.scx`, `libro_novedad_envia_correo.scx` (F2 de Tráfico) | **el correo del F2 NO se migró** |

⚠️ **Regla no obvia:** los `empresa_*` se copian como **snapshot** dentro de
`viaje_pasajero` al generar cada lista de pasajeros (esa tabla tiene sus propias columnas
`empresa_no/di/ci/ha/ve/cu`). Cambiar los parámetros **no reescribe listas ya emitidas** —
comportamiento correcto que hay que respetar, no un bug.

---

## 3. Parámetros Generales (`parametro.scx`)

Misma mecánica, pero acá los campos **no son datos de membrete: son las perillas que
gobiernan pantallas que Buslink ya tiene migradas.** Es la consola de configuración del
sistema, con radio de impacto grande y superficie visual chica.

### 3.1 Combos que se llenan en el `Init`

| Control | Origen |
| --- | --- |
| `empresa_fc` (Empresa de Facturación General) | `SELECT * FROM empresa ORDER BY empresa` |
| `adicional_servicio` / `adicional_chofer` | `SELECT id_servicio, nombre FROM servicio WHERE EMPTY(f_delete) ORDER BY nombre` |
| `lista_precio` | `SELECT id_lista_precio, nombre FROM lista_precio_modelo WHERE EMPTY(f_delete)` |
| `adic_agua` / `adic_maleta` | `SELECT id_adicional, nombre FROM adicional ORDER BY id_adicional` |
| `h_reserva` / `m_reserva` | armados a mano: 0..23 y 0..59 |

### 3.2 Campos — mapeo y valores de producción

| Etiqueta | FoxPro | **SQL** | Tipo | Valor hoy | Grabado por Aceptar |
| --- | --- | --- | --- | --- | --- |
| Empresa de Facturacion General | `empresa_cairo` | `empresa_ca` | nvarchar(30) | `NORTUR` | ✅ |
| Servicio Hora Excedente | `cliente_adicional` | `cliente_ad` | nvarchar(30) | `HORA ADICIONAL` | ✅ |
| Servicios Horas Adicionales (Chofer) | `chofer_adicional` | `chofer_adi` | nvarchar(30) | `HORA ADICIONAL` | ✅ |
| Fracción de Hora Facturación | `fraccion_hora` | `fraccion_h` | bigint | `25` | ✅ |
| Fracción de Hora (Chofer) | `fraccion_hora_chofer` | `fraccion_2` | bigint | `30` | ✅ |
| Lista de Precio Comun (migracion) | `lista_precio` | `lista_prec` | nvarchar(30) | `AGENCIAUS` | ✅ |
| Ultimo Lote de Plantillas | `lote_plantilla` | `lote_plant` | bigint | `44566` | 🐛 **NO** |
| Porcentaje trabajo día franco | `porc_franco` | `porc_franc` | decimal(5) | `0.00` | ✅ |
| Importe franco trabajado | `imp_franco` | `imp_franco` | decimal(9) | `160.00` | ✅ |
| Avisos · Choferes (días) | `aviso_cho` | `aviso_cho` | bigint | `30` | ✅ |
| Avisos · Tecnica (días) | `aviso_veh` | `aviso_veh` | bigint | `7` | ✅ |
| Avisos · Matafuego (días) | `aviso_mat` | `aviso_mat` | bigint | `10` | 🐛 **NO** |
| Activacion de avisos a operadores | `aviso_chequeo` | `aviso_cheq` | nvarchar(2) | `S` | ✅ (`"S"`/`"N"`) |
| Chequear los servicios N antes | `aviso_tiempo` | `aviso_tiem` | datetime2 | `00:10` | ✅ |
| Sueldo bruto + Presentismo | `bruto` | `bruto` | decimal(9) | `3717.37` | ✅ |
| Importe Horas Extras en Bus | `hs_extra_bus` | `hs_extra_b` | decimal(9) | `29.04` | ✅ |
| Importe Horas Extras en Minibus | `hs_extra_mb` | `hs_extra_m` | decimal(9) | `0.00` | ✅ |
| Franco al mes | `franco_mes` | `franco_mes` | bigint | `6` | ✅ |
| Auditoria de comb. % de Vacio | `porc_vacio` | `porc_vacio` | decimal(9) | `0.00` | ✅ |
| Fecha desde control saldo combustible | `dcombsaldo` | `dcombsaldo` | date | `2013-08-01` | ✅ |
| Ultimo lote de auditoria de combustible | `lote_sobre` | `lote_sobre` | bigint | `1768` | ✅ ⚠️ contador vivo |
| Rubro de Combustible | `rubro_combustible` | `rubro_comb` | bigint | `1` | ✅ |
| Id. de Clientes para Mov. Internos | `id_cliente_prueba` | `id_cliente` | nvarchar(30) | `NORTUR` | ✅ |
| Servicios Adicionales (Aguas) | `adic_agua` | `adic_agua` | nvarchar(30) | `AGUA 1/2 LITRO` | ✅ |
| Servicios Adicionales (Maletas) | `adic_maleta` | `adic_malet` | nvarchar(30) | `MALETA` | ✅ |
| Leyenda liquidaciones · Renglon 1 | `ley_liq_1` | `ley_liq_1` | nvarchar(200) | *(vacío)* | ✅ |
| Leyenda liquidaciones · Renglon 2 | `ley_liq_2` | `ley_liq_2` | nvarchar(200) | *(vacío)* | ✅ |
| Envia los XML para subir a la Intranet | `xml_envia` | `xml_envia` | bit | `0` | ✅ ⚠️ **flag GPS** |
| Directorio XML | `dir_xml` | `dir_xml` | nvarchar(140) | `O:\METROCARSYS\XML\` | ✅ |
| Directorio de Auditoria | `dir_auditoria` | `dir_audito` | nvarchar(140) | `O:\METROCARSYS\AUDITORIA\` | ✅ |
| Directorio de Auditoria (externo) | `dir_ex_auditoria` | `dir_ex_aud` | nvarchar(140) | *(vacío)* | ✅ |
| Directorio Intercambio Sist. Contable | `dir_facturacion` | `dir_factur` | nvarchar(140) | `W:\DOCUMENTACION\TRASPASO\` | ✅ |
| Sonido alarma aviso trafico | `dir_sonido_trafico` | `dir_sonido` | nvarchar(140) | `O:\...\SD_SHUTDOWN_12.WAV` | ✅ |
| Directorio de Back-Up | `backup_dir` | `backup_dir` | nvarchar(140) | `C:\BKMETROCAR\` | ✅ |
| Tiempo entre Back-Up | `backup_time` | `backup_tim` | bigint | `1800` (seg) | ✅ |
| *(sin control visible)* | `dir_mdb` | `dir_mdb` | nvarchar(200) | *(vacío)* | 🐛 **sí, sin cargar** |
| *(sin control visible)* | `intranet` | `intranet` | bit | `0` | 🐛 **sí, sin cargar** |

`backup_time` se guarda en **segundos**; el form muestra minutos en un control auxiliar
(`c_tiempo = backup_time / 60`) que no se graba.
`aviso_tiempo` se arma como `DATETIME(1999,12,1, hora, minuto)` — la fecha es basura, solo
importa la hora.

### 3.3 Validaciones

Solo una: **`id_cliente_prueba.Valid`** verifica que el código exista en `cliente`
(`SELECT * FROM cliente WHERE id_cliente = this.value`; si `_tally = 0` → "Cliente
Inexistente" y borra el valor). Todo el bloque de validación de horarios del Aceptar está
**comentado** en el fuente.

### 3.4 🐛 Tres bugs en el fuente — **NO copiar**

1. **`aviso_mat` (Matafuego) se edita pero NO se graba.** El `Init` lo carga en el spinner,
   el usuario lo cambia... y el `UPDATE` **no incluye la columna** (ni siquiera se asigna la
   variable `nAviso_mat`). Nunca se guardó. Y ese valor alimenta el chip de Vencimientos de
   Buslink (`ReportService.cs:2971-2973`).
2. **`dir_mdb` e `intranet` se graban sin haberse cargado.** En el `Init` las dos líneas
   están comentadas (`*thisform.dir_mdb.Value = ...`), pero el Aceptar sí lee los controles
   y los escribe → **cada Aceptar blanquea `dir_mdb` y pone `intranet = 0`**. En la base
   están justamente vacío y 0, consistente con el bug.
3. **`lote_plant` se muestra pero no se graba.** Acá el bug juega a favor: protege por
   accidente un contador vivo del circuito de reservas.

**Decisión Buslink (12/08/2026): se corrigen los tres.** Buslink graba `aviso_mat`, **no
toca** `dir_mdb`/`intranet`, y `lote_plant` se graba solo si el usuario lo cambió a propósito
(el cambio dispara una confirmación con el antes→después).

### 3.5 ⚠️ Campos de riesgo

Todos son **editables** desde el 12/08/2026 (decisión explícita del usuario), pero los que
fallan callados o son difíciles de deshacer disparan una **confirmación al grabar**:

| Campo | Por qué es sensible | Guarda en Buslink |
| --- | --- | --- |
| `sql_gps` (+ `xml_envia`) | Interruptor del GPS. 🔴 **Está ACTIVO en producción**: apagarlo corta el seguimiento de 136 clientes **sin generar ningún error** (ver `trafico/GPS_XLM.md`) | Confirmación al apagarlo, indicando a cuántos clientes afecta. No se graba activo con la conexión incompleta |
| `lote_sobre` (1768) | Contador vivo de la conciliación de combustible | Confirmación con el antes→después |
| `lote_plant` (44566) | Contador vivo del armado de plantillas | Confirmación con el antes→después |
| `empresa_ca`, `lista_prec` | Config de facturación heredada; mueve el motor de valorización. ⚠️ `lista_prec = 'AGENCIAUS'` **ya no existe** en `lista_precio_modelo` | Combos que **preservan el valor huérfano** en vez de blanquearlo al grabar |
| Las 6 rutas de red | `O:\`, `W:\`, `C:\BKMETROCAR\` — unidades del Metrocar que el server de Buslink no ve y que Buslink no usa | Aviso en pantalla |

### 3.6 Detalle menor

Junto a la ruta del sonido hay un botón sin caption (`Command10`, icono `wznext.bmp`) que
reproduce la alarma: `Set Bell To (cFileSonido)` + `?Chr(7)`. En Blazor el sonido tendría
que sonar **en el navegador**, no en el server — no se migra por ahora.

### 3.7 Quién consume estos campos en Buslink (radio de impacto)

| Campo SQL | Pantalla/servicio Buslink ya entregado |
| --- | --- |
| `aviso_cho` / `aviso_veh` / `aviso_mat` | **Chip de Vencimientos** de la barra superior (`ReportService.cs:2971`) |
| `id_cliente` (= `NORTUR`) | Exclusión de movimientos internos en **5 informes** (`ReportService.cs` 838, 2081, 2628, 2787, 4150) |
| `cliente_ad` + `fraccion_h` | **Motor de valorización** de Liquidación (`ReportService.cs:4343`) |
| `aviso_cheq` + `aviso_tiem` | Motor de avisos **F4** de Tráfico (`ReportService.cs:5206`) |
| `rubro_comb` | Módulo **Combustible**, 4 queries (7657, 7866, 7917, 8054) |
| `lote_sobre` | Contador de la conciliación de combustible (`AbmService.cs:2706`) |
| `piva` | Facturación (`ReportService.cs:4548`) |

---

## 4. Parámetros SQL Server para GPS (`parametro_sql_server.scx`)

> 🔴 **Antes de leer esta sección:** esta pantalla configura una **integración VIVA**, no un
> circuito muerto. Ver `docs/PlanoFoxPro/trafico/GPS_XLM.md` (corregido el 12/08/2026).

### 4.1 Qué configura

Es el destino de la **vía 2** de `gps_xlm()`: por cada ASIGNO / RE-ASIGNO / FINALIZO /
CANCELO / armado de plantilla, FoxPro conecta por ODBC a un **SQL Server externo** y hace
`INSERT` o `UPDATE_ALL` del viaje en la tabla configurada.

**Estado productivo verificado el 12/08/2026** (los dos servers, uno actualizado ese mismo
día): `sql_gps = 1` → **ACTIVO**, apuntando a `192.168.0.8` / `MetroCarSQL` / `Servicios`.
Alcanza a **136 clientes** con `cliente.envia_gps = 1` (incluida AEROLINEAS) =
**3.466 de 3.713 viajes del último mes (93 %)**.

⚠️ La réplica local (`DESKTOP-CV6LF0O`) todavía dice `sql_gps = 0` y
`SISTEMA01\SQLEXPRESS_AXOFT` — es un snapshot viejo. **No sacar conclusiones de ahí.**

### 4.2 Campos

| Etiqueta | FoxPro | **SQL** | Tipo | Valor productivo |
| --- | --- | --- | --- | --- |
| Envia datos al SQL SERVER… | `sql_gps` | `sql_gps` | bit | **1 — ACTIVO** |
| Servidor | `sql_server` | `sql_server` | nvarchar(100) | `192.168.0.8` |
| Base | `sql_base` | `sql_base` | nvarchar(50) | `MetroCarSQL` |
| Usuario | `sql_usuario` | `sql_usuari` | nvarchar(50) | `sa` |
| Password | `sql_password` | `sql_passwo` | nvarchar(50) | *(texto plano)* |
| Tabla Servicio | `sql_tabla` | `sql_tabla` | nvarchar(50) | `Servicios` |

**Solo UI, no se persisten:** "Maquina" e "Instancia" (se componen en `sql_server` con `\`) y
el optiongroup **Servidor/Terminal** (solo decide si el botón Conexión hace un chequeo previo).

### 4.3 🐛 Bug del `Init` — hoy es una bomba en producción

```foxpro
thisform.sql_maquina.Value = SUBSTR( sql_server , 1 , AT("\" , sql_server) - 1 )
```

El valor productivo `192.168.0.8` **no tiene backslash** → `AT()` devuelve 0 y
`SUBSTR(x, 1, -1)` devuelve **cadena vacía**: al abrir la pantalla, "Maquina" queda **en
blanco**. Y el `LostFocus` de Maquina/Instancia recompone el servidor a partir de ese vacío:

```foxpro
thisform.sql_server.Value = ALLTRIM(sql_maquina.Value) + "\" + ALLTRIM(sql_instancia.Value)
```

→ **abrir esa pantalla y tabular por los campos borra la dirección del servidor de GPS**, y
Grabar lo persiste. Con la config vieja (`SISTEMA01\SQLEXPRESS_AXOFT`) no pasaba porque tenía
backslash: el bug se activó cuando el servidor pasó a ser una IP pelada.

### 4.4 🐛 El botón "Truncate" — roto **y** destructivo (3 bugs en 6 líneas)

```foxpro
lcLimpia = "TRUNCATE TABLE " + cSql_tabla     && ① cSql_tabla NO existe en este método
SQLExec(lnHandle, lcLimpia )                  && ② lnHandle NO existe (la conexión es nu_conexion)
...
lcConsulta = "DELETE FROM servicios_nortur"   && ③ tabla HARDCODEADA, ignora sql_tabla
```

Intenta truncar con dos variables indefinidas y después borra a mano una tabla distinta de la
configurada. **No copiar.**

### 4.5 Otras trampas

- El textbox de Password tiene `MaxLength = 13`, pero la columna es `nvarchar(50)`: una clave
  más larga no se puede ni tipear.
- `SQL_instalado()` (chequeo del modo "Servidor", `funcion.prg:2220`) recorre por WMI los
  servicios de **la máquina local** buscando alguno con "SQL" en el nombre. No dice nada del
  servidor remoto que se quiere probar — no se replica.
- `Grabar` arranca con `Enabled = .T.`, así que se puede grabar sin haber probado la conexión
  (el botón Conexión lo habilita/deshabilita, pero ya nace habilitado).
- `SQL_conectar` usa `Driver={SQL Server}` (el driver ODBC viejo de Windows) y setea
  `IdleTimeout = 0`.

### 4.6 Qué hizo Buslink

Se migró primero **en solo lectura** y ese mismo día pasó a **editable**, junto con el resto de
la pantalla, cuando el cliente desconectó la tabla del watcher. Guardas: el servidor se edita
**como un solo campo** (esquiva el bug de §4.3), no se graba `sql_gps = 1` con servidor/base/tabla
vacíos, y **apagar el envío pide confirmación explícita** mostrando a cuántos clientes afecta.
Los tres botones se migran corregidos:

| Botón FoxPro | En Buslink |
| --- | --- |
| **Conexion** | `GpsSqlService.ProbarConexionAsync` — conecta y **además verifica que la tabla destino exista** (conectar con la tabla ausente es justo el modo en que este feed falla en silencio). Sin el chequeo WMI inútil |
| **Select** (`Browse` de toda la tabla) | `UltimasFilasAsync` — total de filas + las últimas 20, ordenadas por la columna identity si la hay. Contesta la pregunta real: *¿está entrando el feed y cuál fue el último viaje?* |
| **Truncate** | `VaciarTablaAsync` — usa la conexión y la tabla **configuradas** (sin el `DELETE` hardcodeado), valida el nombre como identificador, `TRUNCATE` con fallback a `DELETE`, y pide confirmación explícita. ⛔ **Apagado** por `AbmFeatureFlags.GpsTruncateActivo`: es destructivo sobre un servidor de terceros |

La solapa muestra además el **alcance medido en vivo** (`GetGpsAlcanceAsync`: clientes con GPS
y % de viajes afectados) para que quede a la vista lo que se rompe si no se migra el envío.

---

## 5. Decisiones de migración (12/08/2026, validadas con el usuario)

| Tema | Decisión |
| --- | --- |
| **Ubicación y permiso** | Módulo **Sistema**, permiso **`S`** (junto a Usuarios y Auditoría de accesos), **no** `A` como el FoxPro. Motivo: la pantalla expone CUIT, tasa de IVA y la password del correo; `S` es el círculo chico (SUPERVISOR, ANDRES, SERGIO) |
| **Alcance** | Empresa + Generales. `parametro_trafico` y `parametro_sql_server` quedan para después |
| **Layout** | **Una página `/parametros` con 2 solapas.** Escriben la MISMA fila de la MISMA tabla: un solo Grabar coherente evita que dos pantallas se pisen |
| **Bugs del FoxPro** | Se **corrigen los 3** (§3.4) |
| **Rutas de red** | Editables, con aviso de que son unidades del Metrocar que este servidor no ve |
| **Probar envío de correo** | Se migra **corregido**: prueba sin grabar, destinatario a elección |
| **Logo** | **Prefijo configurable** en `appsettings.json` (`Logo:PrefijoFoxPro` / `Logo:BasePath`), igual que los Adjuntos. Se guarda la misma ruta que el FoxPro para no romperle el login ni los reportes al sistema viejo |
| **Escritura** | **ACTIVA** (`ParametrosAbmActivo = true`): el cliente desconectó `parametro` del watcher. Deuda de contadores a resincronizar el día D — §1 |
| **Campos de riesgo** | Editables por decisión del usuario (12/08/2026), con confirmación al grabar que muestra el antes→después |
| **Permiso de edición** | Módulo `'S'` + dígito `3` del `nivel` (`Permisos.TieneABM(3)`) |

---

## 5. Trampas para el que retome esto

1. **Nombres truncados a 10 chars** — `empresa_nom → empresa_no`, `smtp_password → smtp_passw`,
   `id_cliente_prueba → id_cliente`, `fraccion_hora_chofer → fraccion_2`,
   `adic_maleta → adic_malet`, `backup_time → backup_tim`. Verificar SIEMPRE contra `sys.columns`.
2. **`empresa_ha` es `int`, no nvarchar** — y varios "números" de la tabla son `bigint`
   (`aviso_*`, `fraccion_*`, `smtp_puert`, `franco_mes`, `lote_*`, `rubro_comb`): leerlos con
   `GetInt32` tira `InvalidCastException` → castear en el SQL (misma trampa que `viaje.interno`).
3. **`piva` está en `0.00`** y **"Vencimiento Circuito" quedó en 2009**: la pantalla se
   abandonó hace años. No asumir que los valores están vigentes.
4. **La password SMTP se guarda en texto plano** en la base. Buslink la muestra enmascarada
   con botón de revelar, pero el dato sigue en claro en la columna (cambiarlo excede esta
   pantalla — habría que tocar también el FoxPro que la lee).
5. **`UPDATE` sin `WHERE`**: en Buslink usar `UPDATE parametro SET ...` igual (1 fila), pero
   **jamás** un `SELECT *` + reescritura de fila completa — pisaría los contadores vivos.
   Escribir **solo las columnas de la pantalla**, una por una, con `SqlParameter`.
