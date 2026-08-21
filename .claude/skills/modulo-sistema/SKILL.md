---
name: modulo-sistema
description: Conocimiento del módulo "ABM del sistema" de Metrocar/Buslink — los catálogos y parámetros de configuración del sistema. Usar SIEMPRE que se trabaje con la tabla `parametro` (la fila única de configuración) o sus pantallas (Parámetros Empresa, Parámetros Generales, Parámetros Pantalla Tráfico, Parámetros SQL Server para GPS), y con los catálogos de ese menú: cronogramas de servicio, servicios, IATA, cursos, guías, dueños, permisos, zonas, nacionalidades, profesiones, feriados y los 3 motivos (cancelaciones, cambio de cronogramas, llegadas tardes). También al tocar cualquier valor de configuración que cambie el comportamiento de otras pantallas (avisos de vencimientos, fracción de hora, cliente de movimientos internos, rubro de combustible, contadores de lote). Mapa de tablas, forms FoxPro, qué está migrado y las trampas de la tabla `parametro`.
---

# Módulo ABM del sistema — mapa de conocimiento

El menú **`ABM del sistema`** del Metrocar (permiso `A`) agrupa los catálogos maestros y
la configuración del sistema. Es el módulo con **menos volumen de datos y más radio de
impacto**: son pocas filas, pero gobiernan el comportamiento de todo lo demás.

> **Skills hermanas:** `seguridad-nortur` cubre usuarios/permisos/login (el menú
> `Sistema → Accesos`, que es OTRO menú). Esta skill cubre los catálogos y parámetros.
> Para construir escritura, siempre `abm-metrocar`. Para extraer forms, `foxpro-extract`.

## 🔴 La tabla `parametro` — leer esto antes de tocarla

**1 fila, 72 columnas.** Es la fila de configuración del sistema entero. En ella conviven
dos naturalezas que NO se pueden separar:

| Naturaleza | Columnas | Cambia |
| --- | --- | --- |
| Configuración estática | los ~50 campos de las 4 pantallas de Parámetros | una vez cada años |
| **Contadores VIVOS del circuito** | `id_viaje_i`, `lote_plant` (44566), `lote_sobre` (1768), `stock_movi` | todo el día |

Como la réplica DBF→SQL pisa **la fila entera**, no existe dueño parcial por columna →
**`parametro` está entre las 12 tablas que cambian de dueño el día D**
(`docs/buslink/PLAN_MIGRACION_BUSLINK.md`). Toda escritura sobre ella va con **andamiaje**.

### Reglas no negociables al escribir en `parametro`

1. **Nunca** `SELECT *` + reescritura de fila completa: pisaría los contadores vivos.
   Escribir **solo las columnas de la pantalla**, una por una, con `SqlParameter`.
2. `UPDATE parametro SET ...` **sin `WHERE`** está bien (es 1 fila) — así lo hace el FoxPro.
3. Los contadores se incrementan **siempre** con `UPDATE parametro SET x = x+1 OUTPUT
   inserted.x` dentro de la transacción (la fila actúa de mutex). **Prohibido `SELECT MAX()+1`.**
   Ya está implementado: `AbmService.SiguienteParametroAsync`.
4. Muchos "números" son **`bigint`** (`aviso_*`, `fraccion_*`, `smtp_puert`, `franco_mes`,
   `lote_*`, `rubro_comb`) → castear en el SQL o revientan con `InvalidCastException`.
   `empresa_ha` es `int`. Misma trampa que `viaje.interno`.

### Quién depende de `parametro` en Buslink (radio de impacto)

Cambiar un número acá le cambia el comportamiento a pantallas ya entregadas:

| Columna | Qué gobierna |
| --- | --- |
| `aviso_cho` / `aviso_veh` / `aviso_mat` | Chip de **Vencimientos** de la barra (`ReportService.cs:2971`) |
| `id_cliente` (= `NORTUR`) | Exclusión de **movimientos internos** en 5 informes (838, 2081, 2628, 2787, 4150) |
| `cliente_ad` + `fraccion_h` | **Motor de valorización** de Liquidación (4343) |
| `aviso_cheq` + `aviso_tiem` | Motor de avisos **F4** de Tráfico (5206) |
| `rubro_comb` | Módulo **Combustible**, 4 queries (7657, 7866, 7917, 8054) |
| `lote_sobre` | Contador de la conciliación de combustible (`AbmService.cs:2706`) |
| `piva` | Facturación (4548) |
| `sql_gps` + `sql_server`/`sql_base`/`sql_tabla` | 🔴 **Interruptor del GPS — ACTIVO en producción** (ver abajo) |
| `xml_envia` + `dir_xml` | La otra vía del GPS (XML file-drop), ésta sí apagada |

## 🔴 El GPS está VIVO — corregido el 12/08/2026

Hasta el 12/08/2026 este proyecto daba por muerta la integración GPS. **Era un error**: la
conclusión salió de leer la réplica **local**, que es un snapshot viejo. Contra los dos
servers **productivos**, `parametro.sql_gps = 1` apuntando a `192.168.0.8` / `MetroCarSQL` /
tabla `Servicios`.

`gps_xlm()` corre si **`xml_envia` OR `sql_gps`** (es un OR) y filtra por `cliente.envia_gps`:
**136 clientes** (incluida AEROLINEAS) = **93 % de los viajes del último mes**. Se dispara en
ASIGNO, RE-ASIGNO, FINALIZO, CANCELO y el armado de plantillas.

**Regla:** cualquier trabajo sobre el circuito `viaje` tiene que contemplar este envío. Si
Buslink toma el circuito sin replicarlo, el feed muere **sin que nadie reciba un error**.
Detalle y campos: `docs/PlanoFoxPro/trafico/GPS_XLM.md`.

> Verificar SIEMPRE los flags de `parametro` **contra producción**, no contra la réplica local
> (`DESKTOP-CV6LF0O\SQLEXPRESS`). Esa es exactamente la trampa que produjo el error.

## Las 4 pantallas de Parámetros

| Form FoxPro | Pantalla | Objetos | Estado Buslink |
| --- | --- | --- | --- |
| `parametro_empresa.scx` | Parámetros Empresa | 42 | ✅ solo lectura + andamiaje — solapa de `/parametros` |
| `parametro.scx` | Parámetros Generales | 105 | ✅ solo lectura + andamiaje — solapa de `/parametros` |
| `parametro_trafico.scx` | Parámetros Pantalla Tráfico | 44 | ⬜ sin migrar |
| `parametro_sql_server.scx` | Parámetros SQL Server para GPS | 26 | ✅ solo lectura + 3 botones de diagnóstico — solapa de `/parametros` |

**Plano completo campo por campo:** `docs/PlanoFoxPro/sistema/PARAMETROS.md`.

### 🐛 Bugs del fuente FoxPro en Parámetros Generales — NO copiar

1. **`aviso_mat` se edita pero NO se graba** (falta en el `UPDATE`). Nunca se guardó.
2. **`dir_mdb` e `intranet` se graban sin haberse cargado** (el `Init` los tiene comentados)
   → cada Aceptar los blanquea.
3. **`lote_plant` se muestra pero no se graba** — el único bug que juega a favor (protege
   un contador vivo).

Buslink **corrige los tres** (decisión 12/08/2026).

### Otras trampas de estas pantallas

- **Nombres truncados a 10 chars**: `empresa_nom → empresa_no`, `smtp_password → smtp_passw`,
  `id_cliente_prueba → id_cliente`, `fraccion_hora_chofer → fraccion_2`, `adic_maleta → adic_malet`,
  `backup_time → backup_tim`.
- **"Probar envío correo" del FoxPro tiene 3 defectos**: graba el SMTP antes de probar,
  manda a `jlsilvamtb@gmail.com` (casilla del desarrollador viejo, hardcodeada) y usa CDO
  con SSL forzado sobre el puerto 25 (probablemente roto). Buslink lo migra **corregido**.
- **La password SMTP está en texto plano** en la columna.
- **`piva = 0.00`** y **"Vencimiento Circuito" = 2009**: la pantalla está abandonada hace
  años. No asumir que los valores están vigentes.
- **6 rutas apuntan a unidades de red** (`O:\`, `W:\`) que el server de Blazor no ve. Mismo
  problema ya resuelto para Adjuntos y el Logo: prefijo configurable en `appsettings.json`.
- **Validación de CUIT**: largo 13 con máscara + dígito verificador módulo 11
  (`_ValidaCUIT`, `funcion.prg:339`). Implementada en `AbmService.ValidarCuit`.

## Catálogos del menú (sin migrar)

Todos siguen el patrón universal de dos forms (`<entidad>.scx` lista + `<entidad>_abm.scx`)
descrito en la skill `abm-metrocar`.

| Ítem del menú | Form | Tabla | Doc del plano |
| --- | --- | --- | --- |
| Cronogramas de Servicio | `cronograma.scx` | `cronograma` | ⬜ |
| Servicios | `servicio.scx` | `servicio` (61 filas) | ⬜ |
| IATA | `iata.scx` | `iata` | ⬜ |
| Cursos - Descripción | `chofer_curso_parametro.scx` | `chofer_curso_*` | ⬜ |
| Guías | `guia.scx` | `guia` | ✅ `catalogos/GUIA_ABM.md` |
| Dueños | `dueno.scx` | `vehiculo_dueno` | ⬜ |
| Permisos | `permiso.scx` | `permiso` | ⬜ |
| Zonas | `zona.scx` | `zona` | ⬜ |
| Nacionalidades | `nacionalidad.scx` | `nacionalidad` | ⬜ |
| Profesiones | `profesion.scx` | `profesion` | ⬜ |
| Feriados | `feriado.scx` | `feriado` | ✅ `catalogos/FERIADO_ABM.md` (⚠️ 0 feriados de 2026) |
| Motivos de Cancelaciones | `viaje_motivo_cancela.scx` | `viaje_motivo_cancela` | ✅ `catalogos/VIAJE_MOTIVO_CANCELA_ABM.md` |
| Motivos de Cambio de Cronogramas | `viaje_motivo_cambio.scx` | `viaje_motivo_cambio` | ✅ `catalogos/VIAJE_MOTIVO_CAMBIO_ABM.md` (🐛 modifica roto) |
| Motivos de Llegadas Tardes | `viaje_motivo_tarde.scx` | `viaje_motivo_tarde` | ⬜ |
| Facturación → 6 ítems | `empresa`, `moneda_tipo`, `moneda_cotizacion`, `banco`, … | varias | parcial en `facturacion/` |

> Varios de estos están **muertos** en producción (ver memoria
> `modulos-metrocar-muertos-vs-vivos`): medir contra la base antes de invertir en migrarlos.

## Ubicación en Buslink

⚠️ **Los Parámetros NO van donde el FoxPro los tiene.** Decisión del 12/08/2026:

- FoxPro: `ABM del sistema` → permiso **`A`** (lo tienen 5 usuarios, incluidas ALEJANDRA y DAMIAN).
- Buslink: sección **Sistema** → permiso **`S`** (SUPERVISOR, ANDRES, SERGIO).

Motivo: la pantalla expone CUIT, tasa de IVA y la password del correo. `S` es el círculo chico.
El resto de los catálogos de este módulo sí van bajo `A`, en la sección "ABM del sistema"
del drawer (hoy con placeholders deshabilitados).

**Ruta:** `/parametros` — una sola página con 2 solapas (Empresa · Generales), porque las
dos escriben la MISMA fila de la MISMA tabla y dos pantallas separadas se pisarían.

## Flags de escritura

`AbmFeatureFlags.ParametrosAbmActivo` — `false` hasta el día D. Checklist de activación:
el genérico de `AbmFeatureFlags` **más** verificar que la sync de `parametro` esté apagada
(si no, pisa lo que escriba Buslink y además desincroniza los contadores).

`AbmFeatureFlags.GpsTruncateActivo` — `false`, y **no espera al día D**: no toca ninguna tabla
de `replicaVPF`. Está apagado porque vacía la tabla del **SQL de terceros** del GPS, que hoy
alimenta el seguimiento de 136 clientes. Encender solo con autorización explícita.

## Diagnóstico del SQL del GPS (`GpsSqlService`)

Los 3 botones del form FoxPro, migrados corregidos y disponibles aunque la escritura esté
apagada (los 2 primeros son solo lectura):

- **Probar conexión** — conecta y **además verifica que exista la tabla destino**; conectar con
  la tabla ausente es justo el modo en que este feed falla en silencio.
- **Ver últimas filas** — total + últimas 20 filas ordenadas por la columna identity. Es la
  forma de contestar *¿el feed está entrando?* sin acceso al sistema del proveedor.
- **Vaciar tabla** — el `Truncate` del FoxPro, que estaba roto (usaba `lnHandle` y `cSql_tabla`,
  **ninguna definida en su método**, y después hacía `DELETE FROM servicios_nortur`
  hardcodeado). Corregido + confirmación + flag apagado.
