---
name: abm-metrocar
description: Metodología para migrar ABMs (alta/baja/modificación) del FoxPro Metrocar a Blazor. Usar SIEMPRE que se construya un ABM, CRUD, formulario de alta/edición/borrado, o cuando se hable de ESCRIBIR datos en la base desde Blazor (INSERT/UPDATE), de migrar una tabla, o de "dar de alta", "modificar" o "eliminar" registros. Define la regla de escritura del proyecto (SQL dueño tabla por tabla), el patrón universal de los 73 forms *_abm.scx del FoxPro y su mapeo a Blazor. Leer ANTES de escribir cualquier código que modifique datos.
---

# ABM Metrocar — metodología de migración de escritura

Hasta ahora el proyecto es **solo lectura** (informes). Esta skill define cómo se migran
los ABM — y la regla más importante no es de código, es de **propiedad de los datos**.

## La regla de escritura — SQL dueño, tabla por tabla (decisión 2026-06-10)

La réplica actual es **unidireccional**: FoxPro escribe DBF → un proceso sincroniza a SQL
(`replicaVPF`). Por eso:

1. **Blazor NO escribe en tablas cuyo dueño sigue siendo FoxPro.** La sync DBF→SQL pisaría
   los cambios (datos huérfanos: una zona creada en Blazor sería pisada por la próxima sync).
2. **Una tabla migra de dueño cuando su ABM Blazor está listo**: desde ese momento se edita
   SOLO en Blazor. En FoxPro se bloquea el ABM correspondiente (sacar permisos 2/3/4 del
   `cNivel` de los usuarios para ese form, o quitar la barra del menú).
3. **Los datos escritos en SQL se quedan en SQL.** No hay puente inverso SQL→DBF. FoxPro
   deja de ser la fuente de verdad para la tabla migrada — los operadores usan Blazor.
4. La sync DBF→SQL se **apaga para la tabla migrada** (si no, pisaría lo escrito en SQL).
5. Empezar por **tablas catálogo chicas y de baja fricción** (zona, nacionalidad, profesion,
   feriado, motivos de cancelación/cambio/tarde) antes de tocar maestros grandes
   (cliente, chofer, vehiculo) o transaccionales (viaje — ese es workflow, no CRUD).

## El patrón FoxPro (los 73 `*_abm.scx` son TODOS iguales)

Dos forms por entidad — extraer siempre el real con la skill `foxpro-extract` y documentarlo
en `docs/PlanoFoxPro/<FORM>_ABM.md` antes de codear, pero la estructura es:

**Form lista** (`zona.scx`): grilla + botones con permiso por dígito de `cNivel`:
`"2" $ cNivel` → Agregar, `"3"` → Modificar, `"4"` → Eliminar; sin permiso →
`cartel("sin_permiso")`. Eliminar sobre fila ya eliminada → aviso y aborta.

**Form edición** (`zona_abm.scx`) recibe `"alta"` / `"baja"` / `"modifica"`:

| Operación | Lógica FoxPro | Regla a replicar |
| --- | --- | --- |
| alta | valida campos vacíos → valida **PK duplicada** → `INSERT ... f_create = DATE()` | mismas validaciones + mensaje |
| modifica | PK **no editable**; `UPDATE ... f_modify = DATE()` (puede editar `f_delete` para rehabilitar) | ídem |
| baja | **`UPDATE ... f_delete = DATE()`** — NUNCA delete físico | soft delete siempre |

Detalles que no son negociables (los datos históricos y FoxPro dependen de esto):

- **PKs de texto tipeadas por el usuario** (`id_zona`, `id_servici`, …), NO identity/GUID.
- **`f_create` / `f_modify` / `f_delete`** son los campos de auditoría del NEGOCIO.
  No confundir con `_created_at` / `_updated_at` / `_deleted` que son **metadata de la
  réplica**: en INSERT desde Blazor setear **`_deleted = 0`** explícito (los informes
  filtran por esa columna) y dejar que `_created_at`/`_updated_at` tengan default o GETDATE().
- Filas con `f_delete` cargado = **inhabilitadas**: se muestran en amarillo, no se ocultan.
- Tras grabar, la lista se reposiciona en el registro tocado (réplica de `c<Entidad>GoTo`).

## Mapeo a Blazor

- **Página lista**: patrón de tabla del proyecto (ver skill `blazor-nortur`) + botones
  Agregar/Modificar/Eliminar habilitados según permisos del usuario logueado.
- **Form de edición**: `MudDialog` (como `ZoomViajeDialog` pero editable) con el parámetro
  de modo alta/baja/modifica — un solo dialog por entidad, igual que el FoxPro.
- **Servicio de escritura separado**: `AbmService` (no mezclar con `ReportService`).
  Para escritura usar **SIEMPRE `SqlParameter`** — la convención de string + `Replace("'","''")`
  del proyecto es solo para los WHERE de lectura; en INSERT/UPDATE no se negocia.
- **Invalidar caché** del `ReportService` tras cada escritura (las keys del recurso tocado).
- **Permisos**: replicar los niveles 2/3/4 leyendo `usuario`/`permiso` de la réplica.
- **SQL Server 2012**: sin `STRING_AGG`, `TRIM`, `CONCAT_WS`, etc.
- **Transacción** por operación; la PK duplicada se valida dentro de la misma conexión.

## Checklist por cada ABM nuevo (en orden)

1. Extraer la lógica FoxPro (`foxpro-extract`) → `docs/PlanoFoxPro/<FORM>_ABM.md`.
2. Métodos en `AbmService` (alta con chequeo duplicado / modifica / baja lógica).
3. Página lista + MudDialog de edición.
4. Permisos por usuario (niveles 2/3/4).
5. Bloquear el ABM en FoxPro (permisos o menú) y apagar la sync DBF→SQL de esa tabla.
6. Prueba end-to-end en Blazor: alta → aparece en lista → modifica → baja lógica visible.
   **Seguir el protocolo de la skill `testing-nortur`**: validación con DOS señales (UI +
   `SELECT`), datos de prueba `ZZTEST` reversibles sobre el servidor **local**, y limpieza al
   final. No ensuciar `replicaVPF` con datos de prueba.

## Estado

### ✅ Primer ABM de escritura: USUARIOS Y PERMISOS (01/07/2026)

El **primer ABM con escritura real** del proyecto (alta/baja/modificación en la tabla
`usuario` de `replicaVPF` local). Estrenó la capa de escritura. **Plantilla para los ABMs siguientes.**

| Pieza | Archivo |
|---|---|
| Capa de escritura (nueva) | `Services/AbmService.cs` — INSERT/UPDATE con `SqlParameter` + transacción |
| Catálogo de permisos | `Services/PermisosCatalogo.cs` — 16 letras en orden fijo + `Construir()`/`Tiene()` |
| Lectura | `ReportService.GetUsuariosAsync` / `GetUsuarioDetalleAsync` + DTOs `UsuarioListaRow`/`UsuarioDetalleDto` |
| Página lista | `Components/Pages/UsuariosAbm.razor` (`/usuarios-abm`) — botonera Agregar/Modificar/Eliminar/Ver **habilitada** |
| Dialog editor | `Components/Shared/UsuarioEditorDialog.razor` — **un solo dialog, 4 modos** `ver`/`alta`/`modifica`/`baja` (calca `usuario_abm.scx`) |
| Menú | `MainLayout.razor` → sección **Sistema** (guard `Permisos.Tiene('S')`) |
| CSS | `usr-*` en `app.css` (grilla de permisos del dialog) |

**Patrón `AbmService` (calcar):** ctor recibe `IDbContextFactory<NorturDbContext>` + `ReportService`.
Cada op: abre `SqlConnection`, `BeginTransactionAsync`, valida (duplicado dentro de la tx),
ejecuta con `SqlParameter`, `CommitAsync`, y al final `_reports.InvalidarCacheAbm()`. Devuelve un
`record AbmResult(bool Ok, string? Error, int? Id)`. El dialog cierra con `MudDialog.Close(DialogResult.Ok(true))`
si grabó; la lista recarga si `res.Data is bool grabo && grabo`.

**Dialog multi-modo (calcar):** params `[Parameter] Modo` (string) + `[Parameter] Id` (int).
Propiedades `EsAlta/EsModifica/EsBaja/SoloLectura/SoloEscritura` derivadas del modo. En `alta` la
PK es editable; en el resto solo lectura. El botón de grabar cambia según modo.

**Lecciones (trampas resueltas):**
- **`usuario.id` NO es identity** → el alta calcula `SELECT ISNULL(MAX(id),0)+1`. Verificar
  `COLUMNPROPERTY(OBJECT_ID('tabla'),'id','IsIdentity')` antes de asumir identity en cualquier tabla.
- **`password` es `nvarchar(15)`** → texto plano, un hash no entra. Se mantiene plano (consistente
  con el login FoxPro y el Blazor actual). En modifica, password en blanco = "no cambiar".
- **`acceso` es `nvarchar(15)`** pero hay 16 letras posibles → validar longitud ≤ 15 al grabar.
- **Doble capa de borrado**: la baja lógica toca `f_delete` (negocio → amarillo), NO `_deleted`
  (metadata réplica). El INSERT setea `_deleted=0` explícito.
- **`nivel` fijo `"12345"`** en el INSERT (no se expone en la UI).
- **Reglas de permisos en vivo** (del `usuario_abm.scx`): 'C' (Avisos) se deshabilita si no está
  'T'; al destildar 'T' cae 'C'. 'X' (Tablero) solo lo tilda el SUPERVISOR. Modeladas como flags
  en `PermisosCatalogo.Permiso` (`DependeDeTrafico`, `SoloSupervisor`).
- **Defensa anti-autobloqueo** (valor sobre FoxPro): no quitarse 'S' a uno mismo, no darse de baja
  a uno mismo; no dar de baja a SUPERVISOR (esto último ya en FoxPro).
- **Validación sin poder invocar el AbmService desde afuera**: se ejecutó el MISMO SQL que genera
  el service sobre `ZZTEST01` (alta/modifica/baja/anti-dup) con dos señales y DELETE físico al
  final. El orden del string `acceso` se validó aparte (entrada desordenada → orden fijo `SRTCDVLFAEUBHXNM`).
- **Label largo en `zoom-field--1` se solapa** con el campo contiguo → dar `--2` o acortar el label.

**Falta para producción real:** hoy escribe en el server local (sync de `usuario` apagada).
`CambiarPasswordAsync` existe en `AbmService` pero sin UI dedicada (se cambia dentro de "modifica").
Antes de ir al server nuevo: bloquear el ABM de usuarios en FoxPro + confirmar sync apagada allá.

### Roadmap de ABMs — lo fija el plan Buslink (02/07/2026)

El orden de los ABMs ya NO se decide ad-hoc: está definido en **`docs/buslink/PLAN_MIGRACION_BUSLINK.md`**
(el plan de migración del circuito `viaje` hacia Buslink, nombre nuevo del sistema).

- **Grupo A — catálogos con cutover temprano** (bloqueo FoxPro + sync off al terminar cada uno):
  `viaje_motivo_cancela` → `feriado` → `destino` → `cliente_operador` → `cliente`.
  Cada uno calca `UsuariosAbm.razor` + `UsuarioEditorDialog.razor` + métodos en `AbmService`.
- **Grupo B — se CONSTRUYEN antes pero cambian de dueño el día D** (el circuito FoxPro las
  escribe hasta el corte — apagar su sync antes perdería escrituras): `guia`, `cliente_grupo`,
  `chofer_franco`, `reserva_plantilla`.
- **El circuito `viaje`** (Reservas alta + Tráfico operación + Facturación Graba) NO es CRUD:
  es workflow → motor `ViajeAbmService` + día D único. Ver el plan y la skill `modulo-trafico`
  (`references/ESCRITURA_CIRCUITO.md`).

Regla operativa del grupo A: desde su cutover, las altas nuevas viven SOLO en SQL y FoxPro no
las ve → cortar `cliente`/`destino` lo más cerca posible del día D.

Anotar acá las lecciones de cada ABM nuevo — esta skill mejora con cada uno.

### Vistas de solo lectura ya migradas (plantilla para los ABMs futuros)

Patrón de **solo lectura** (lista + ficha, botonera deshabilitada, escritura sigue en FoxPro):

| Entidad | Lista | Ficha (dialog) | Doc FoxPro | Permiso módulo |
|---|---|---|---|---|
| Clientes | `ClientesAbm.razor` (`/clientes-abm`) | `ClienteDetalleDialog.razor` | `CLIENTE_ABM.md` | `'F'` |
| Choferes | `Choferes.razor` (`/choferes`) | `ChoferDetalleDialog.razor` (5 tabs) | `CHOFER_ABM.md` | `'V'` |

Lecciones de Choferes (15/06/2026):
- Reusar los estilos **`cli-*`** (grilla, toolbar, footer, tabs, flags) y **`zoom-*`**
  (campos `F()`, boxes) — NO crear CSS nuevo salvo lo específico (ej: `chof-vto--vencido/proximo`).
- La grilla FoxPro filtra por `empty(f_delete)` por defecto (check "Ver Egresados" lo invierte);
  egresados en amarillo (`cli-grid__row--egresado`). Misma lógica que Clientes.
- **Nombres truncados a 10 chars en la réplica**: SIEMPRE verificar con `INFORMATION_SCHEMA.COLUMNS`
  antes de escribir SQL. En `chofer`: `registro_v/2/3/4`, `nextel_cel`, `id_lista_p`,
  `real_domi*`, `entre_call/cal2`. Cruzar el form FoxPro (nombres largos) con la tabla real.
- **Tablas de auditoría viva NO replicadas** (`chofer_log`, como `vehiculo_sobre` en Combustible):
  no usarlas en solo lectura. Verificar existencia/datos antes de hacer JOIN.
- `vehiculo.id_vehiculo` no existe en SQL → es `id_vehicul`; patente = `dominio`. Verificar.
- Para vencimientos críticos se agregó valor sobre el FoxPro: rojo vencido / ámbar 30 días.
