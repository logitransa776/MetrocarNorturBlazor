# Runbook — activar los ABM de Vehículos y Choferes (Fleteros y Tipo de Vehículos)

> Estado al **05/07/2026:** las pantallas están migradas en **solo lectura** con el andamiaje de
> escritura completo pero APAGADO. El cliente **todavía no autorizó** el ABM. Este documento es el
> checklist para encenderlo **a partir del mes que viene (agosto 2026)**, cuando la tabla pase de
> dueño a Buslink. Ver la regla strangler en la skill `abm-metrocar`.

## Qué ya está hecho (no rehacer)

- **Lista + ficha** de cada catálogo: `Fleteros.razor` (`/fleteros`) y `TiposVehiculo.razor`
  (`/tipos-vehiculo`).
- **Editor multi-modo** (`ver`/`alta`/`modifica`/`baja`): `FleteroEditorDialog.razor` y
  `TipoVehiculoEditorDialog.razor`.
- **Escritura** en `AbmService`: `AltaFleteroAsync` / `ModificaFleteroAsync` / `BajaFleteroAsync`
  y `AltaTipoVehiculoAsync` / `ModificaTipoVehiculoAsync` / `BajaTipoVehiculoAsync` (transacción,
  `SqlParameter`, `MAX(id)+1`, baja lógica con `f_delete`, `_deleted=0` en el INSERT).
- **Interruptores centralizados**: `Services/AbmFeatureFlags.cs`
  (`FleterosAbmActivo`, `TiposVehiculoAbmActivo`), hoy ambos en `false`.

Mientras el flag esté en `false` hay **doble candado**: el botón Grabar del editor está
deshabilitado Y el método `Grabar()` aborta antes de tocar la base. La botonera Agregar/Modificar/
Eliminar de la lista también está atada al flag.

## Pasos para activar (por cada catálogo, de a uno)

Hacerlo **uno a la vez** (primero Tipo de Vehículos, que es el más chico y sin dependencias;
después Fleteros). Para cada uno:

1. **[Cliente / dueño del sistema] Bloquear el ABM en FoxPro.** Sacar los permisos 2/3/4 de ese
   form (`vehiculo_tipo_abm` / `fletero_abm`) del `cNivel` de los usuarios, o quitar la barra del
   menú. Desde ese momento, en FoxPro la pantalla queda de consulta.
2. **[Infra] Apagar la sync DBF→SQL de esa tabla** (`vehiculo_tipo` / `fletero`). Si no se apaga,
   la próxima sync **pisa** lo que escriba Blazor (datos huérfanos). Confirmar con quien maneja la
   réplica que esa tabla ya no se sincroniza.
3. **[Código] Poner el flag en `true`** en `Services/AbmFeatureFlags.cs`:
   ```csharp
   public static readonly bool TiposVehiculoAbmActivo = true;   // o FleterosAbmActivo
   ```
   Con esto se habilitan solos: el botón Grabar del editor Y los botones Agregar/Modificar/
   Eliminar de la lista (ambos leen el mismo flag). **No hay que tocar el .razor.**
4. **[Deploy] Publicar** al server de producción (ver memoria `[[publicacion-iis-produccion]]`).
5. **[QA] Validar con el protocolo `testing-nortur`** (dos señales: UI + `SELECT`), con datos
   **`ZZTEST`** reversibles sobre el server correcto, y limpieza al final:
   - Alta → aparece en la lista y en la base.
   - Modifica → cambia en ambos; la PK lógica (`id_vehicul` / `id_contrat`) NO se edita.
   - Baja → `f_delete` con fecha; la fila queda en amarillo (no se borra físico).
   - Anti-duplicado → intentar alta con PK repetida da error controlado.

### Fleteros — paso extra (catálogo compartido)

`fletero` aparece también en el menú **Facturación**. Antes de activar su ABM, **coordinar con
`modulo-facturacion-liquidacion`**: la tabla debe tener **un solo dueño**. Si Facturación va a
seguir usando fleteros en FoxPro, no activar acá todavía; si Buslink toma el catálogo, avisar a
Facturación que su ABM de fleteros queda de consulta.

## Diferencias de validación vs FoxPro (decidir al activar)

Al construir el andamiaje se **relajaron** dos validaciones del FoxPro (había datos reales que no
las cumplían). Revisar con el cliente si se re-endurecen:

- **Fletero:** el FoxPro exige al menos una lista de precios (`id_lista_precio` OR
  `id_lista_personal`). Blazor hoy NO lo exige. Si se quiere fiel: agregar el chequeo en
  `AbmService.ValidarFletero`.
- **Tipo de Vehículo:** el FoxPro exige rango de consumo (`consumo_min≠0`, `consumo_max≠0`,
  `min<max`). Blazor solo chequea `max>=min` cuando ambos vienen (AUTO y KANGOO tienen consumo
  NULL en la base real). Si se quiere fiel: endurecer en `AbmService.ValidarTipoVehiculo`.

## Trampas ya resueltas (no repetir)

- `fletero.id` y `vehiculo_tipo.id` **NO son identity** → el alta calcula `MAX(id)+1`.
- La PK lógica tipeada (`id_contrat` / `id_vehicul`) es inmutable en modifica.
- Baja = `f_delete` (negocio, amarillo), NO `_deleted` (metadata de réplica). INSERT setea
  `_deleted=0` explícito.
- `GetFleterosAsync` (combo de Tráfico) ≠ `GetFleterosListaAsync` (grilla del ABM) — no confundir.

Planos de cada form: `docs/PLANOFOXPRO/vehiculos-choferes/FLETEROS.md` y `TIPO_VEHICULOS.md`.
Plantilla de referencia de un ABM ya en producción: el de **Usuarios** (`UsuariosAbm.razor` +
`UsuarioEditorDialog.razor` + `AbmService` sección Usuarios), skill `abm-metrocar`.
