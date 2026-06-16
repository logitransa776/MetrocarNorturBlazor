# Lógica FoxPro — ABM de Choferes (`chofer.scx` + `chofer_abm.scx`)

> Menú: **Vehículos y Choferes → Choferes** (`OpenForm("chofer")`).
> Patrón ABM de dos forms (lista + ABM "ABM de Conductores"). Relacionados:
> `chofer_busca.scx` (buscador), `chofer_log.scx` (auditoría — **NO replicada a SQL**),
> `chofer_curso_consulta.scx` (cursos), `vehiculo_chofer` (vehículos asignados).
> Extraído del binario con `foxpro-extract` (15/06/2026). 707 choferes (249 activos / 458 egresados).

---

## Lista (`chofer.scx`)

- Grilla de la tabla `chofer` ordenada por `nombre`. Cabecera: `Total de conductores : N`.
- **Filtros (toolbar superior):**
  - Combo **Fletero** → `SELECT id_contratado FROM fletero ORDER BY id_contratado`. Filtra
    `fletero = '<codigo>'`. En SQL el campo de fletero es `id_contrat` (truncado).
  - Textbox **Nombre** → búsqueda incremental `LOCATE FOR <texto> $ nombre` (SET EXACT OFF,
    sobre el campo desnormalizado `nombre` = apellido + nombre1 + nombre2).
  - Check **Ver Egresados**: OFF (default) filtra `empty(f_delete)`; ON muestra todos.
- Filas con `f_delete` cargada se pintan **amarillas**
  (`dynamicbackcolor IIF(!EMPTY(f_delete), RGB(255,255,0), blanco)`).
- **Columnas** (definidas en `arma_grid`, comentadas pero son las reales que se ven):
  código (`id_chofer`), nombre, Inhabilit. (`f_delete`), domicilio (+nro/piso/depto),
  localidad, C.Postal, teléfono, celular, nextel, T.Doc, Nº Doc, Vto Registro, Reg CNRT Vto,
  Reg AEP Vto, YPF Pin, Esso Pin. En la captura productiva además: Fletero, Apellido,
  Nombre1, Nombre2, Padre, Madre, y los domicilios.
- **Botones** con permiso por dígito de `cNivel`: `"2"` → Agregar, `"4"` → Eliminar,
  `"3"` → Modificar/Consultar (doble clic = Modificar). Sin permiso → `cartel("sin_permiso")`.
  Otros: **Ver cursos** (`chofer_curso_consulta`), **Exportar a Excel**, **Log**
  (`chofer_log`), **Salir**.
- Reposicionamiento post-ABM: variable pública `cChoferGoTo` → `LOCATE FOR id_chofer`.

## ABM (`chofer_abm.scx`) — "ABM de Conductores"

Modos: `"alta"`, `"baja"`, `"modifica"`, `"consulta"`. Pageframe de **5 pestañas**.
PK `id_chofer` inmutable en modifica (`codigo.Enabled = .F.`).

### Pestaña 1 — Datos Personales

| Control | Columna SQL real | Notas |
|---|---|---|
| Código | `id_chofer` (char 15) | PK, inmutable en modifica. **obligatorio** |
| Fletero | `id_contrat` (en `chofer` se llama `fletero`) | combo `fletero.id_contratado` |
| Apellido | `apellido` | **obligatorio** |
| 1º Nombre / 2º Nombre | `nombre1` / `nombre2` | nombre1 **obligatorio** |
| Tipo Doc | `tdoc` | combo (DU, LE, LC, …) |
| Nro de Doc | `ndoc` | |
| Venc Registro | `registro_v` (FoxPro `registro_vto`) | date. **obligatorio** |
| Venc. Reg CNRT | `registro_3` (FoxPro `registro_vto_cnrt`) | date |
| Lugar de nacimiento | `lugar_naci` (FoxPro `lugar_nacimiento`) | |
| F. Nacimiento | `f_nac` | date. **obligatorio** |
| Estado Civil | `estado_civ` (FoxPro `estado_civil`) | combo: SOLTERO/CASADO/CONCUBINATO/SEPARADO/VIUDO |
| Nombre del padre / madre | `padre` / `madre` | |
| E-Mail | `email` | se graba en minúsculas |
| Grupo Sanguíneo | `grupo_sang` (FoxPro `grupo_sanguineo`) | combo: A/AB/B/O |
| RH (No informo/Positivo/Negativo) | `rh_pos_neg` | optiongroup: I / P / N (vacío = I) |
| Nro de C.U.I.L. | `ncuil` | máscara 99-99999999-9 |
| Nro de Registro | `registro_n` (FoxPro `registro_nro`) | **obligatorio** |
| Nro de Registro CNRT | `registro_2` (FoxPro `registro_nro_cnrt`) | |
| F. Venc. Ingreso Aeo | `registro_4` (FoxPro `registro_vto_aeo`) | date |
| Fecha Inhabilitación | `f_delete` | editable solo en baja/modifica (= egresado) |
| Comentarios | `comentario` | |
| Nextel de la empresa | `nextel` | |
| Nextel Celular | `nextel_cel` (FoxPro `nextel_celu`) | |

### Pestaña 2 — Condiciones Laborales

| Control | Columna SQL real | Notas |
|---|---|---|
| F. Ingreso | `f_ingreso` | date. **obligatorio** (default DATE() en alta) |
| Días (Lun..Dom) | `lunes`..`domingo` | 7 flags bit (días de trabajo) |
| Jornal hora inicio/fin | `h_i_jornal` / `h_f_jornal` | datetime2; fin = inicio + 3600*jornal |
| Jornal (horas) | `jornal` | bigint |
| Aplica jornal | `jornal_apl` (FoxPro `jornal_aplica`) | flag |
| Lista de liquidación | `id_lista_p` (FoxPro `id_lista_precio`) | combo `lista_precio_modelo_chofer`. **obligatorio** |
| Legajo | `legajo` | bigint |
| Auditor | `auditor` | flag |

### Pestaña 3 — Vehículos

- Grilla de **vehículos asignados** al chofer: tabla `vehiculo_chofer`
  (`id_vehicul`, `id_chofer`, `interno`). Combo de alta = `vehiculo` (cód/interno).
  En modifica el FoxPro hace `DELETE vehiculo_chofer` + re-INSERT del cursor temporal.
- Además, en esta pestaña: **YPF Pin** (`ypf_pin`) y **Esso Pin** (`esso_pin`).

### Pestaña 4 — Domicilios (dos bloques)

**Domicilio que figura en el Documento Nacional:**

| Control | Columna SQL real (FoxPro) |
|---|---|
| Domicilio / Nro / Piso / Depto | `domicilio` / `domicilio_` / `domicilio2` / `domicilio3` |
| Entre / Y | `entre_call` / `entre_cal2` (FoxPro `entre_calle_1/2`) |
| C.Postal / Localidad / Partido / Provincia | `cpostal` / `localidad` / `partido` / `provincia` |

**Domicilio donde vive actualmente** (campos `real_domi*`, orden verificado con datos):

| Control | Columna SQL real |
|---|---|
| Domicilio | `real_domic` |
| Nro | `real_domi2` |
| Piso | `real_domi3` |
| Depto | `real_domi4` |
| C.Postal | `real_domi5` |
| Localidad | `real_domi6` |
| Partido | `real_domi7` |
| Provincia | `real_domi8` |
| Entre | `real_domi9` |
| Y | `real_dom10` |

> Botón "Copiar Domicilio" replica el del DNI al actual (no relevante en solo lectura).

### Pestaña 5 — Teléfonos

| Control | Columna SQL real |
|---|---|
| Teléfono / Celular | `telefono` / `celular` |
| Tel/Línea/Cel 1..5 | `tel_1..5` / `linea_1..5` / `cel_1..5` |

### Validaciones (`audita_carga`, acumula → `form_error`)

1. Código obligatorio. 2. Apellido obligatorio. 3. 1º Nombre obligatorio.
4. Nº de Registro obligatorio. 5. Vto de Registro obligatorio. 6. Fecha de nacimiento.
7. F. Ingreso. 8. Lista de liquidación de honorarios (`id_lista_p`).

### Operaciones

- **Alta**: anti-duplicado de `id_chofer` → INSERT. `f_create = DATE()`. `nombre` = apellido +
  " " + nombre1 + " " + nombre2 (desnormalizado). Log "Alta de vehiculo".
- **Baja**: `UPDATE chofer SET f_delete = <fecha del form>` (lógica, NUNCA DELETE).
- **Modifica**: UPDATE completo + `f_modify = DATE()` + re-sync de `vehiculo_chofer`.
  Diff campo a campo → `INSERT chofer_log` (auditoría).

---

## Reglas no obvias

1. `chofer` usa **borrado lógico con fecha editable** (`f_delete`) — "egresado" =
   `f_delete` con valor; se puede rehabilitar limpiándola. Misma semántica que `cliente`.
2. `nombre` es **desnormalizado** (apellido + nombre1 + nombre2) — se usa para la búsqueda
   incremental y la grilla. Al modificar se recalcula.
3. La réplica SQL **trunca a 10 chars** los nombres largos del DBF. Mapa crítico:
   `domicilio_nro`→`domicilio_`, `domicilio_piso`→`domicilio2`, `domicilio_dpto`→`domicilio3`,
   `entre_calle_1/2`→`entre_call`/`entre_cal2`, `registro_nro`→`registro_n`,
   `registro_vto`→`registro_v`, `registro_nro_cnrt`→`registro_2`, `registro_vto_cnrt`→`registro_3`,
   `registro_vto_aeo`→`registro_4`, `nextel_celu`→`nextel_cel`, `id_lista_precio`→`id_lista_p`,
   `jornal_aplica`→`jornal_apl`, `grupo_sanguineo`→`grupo_sang`, `estado_civil`→`estado_civ`,
   `lugar_nacimiento`→`lugar_naci`, `real_domicilio*`→`real_domic`/`real_domi2..9`/`real_dom10`,
   `vehiculo_chofer.id_vehiculo`→`id_vehicul`.
4. **`chofer_log` NO está replicada a SQL** (tabla de auditoría viva en FoxPro, como
   `vehiculo_sobre` en Combustible). En Blazor solo lectura no se usa.
5. Las 3 fechas de vencimiento (`registro_v` Registro, `registro_3` CNRT, `registro_4` AEP)
   son críticas para operación: en Blazor se resaltan vencidas (rojo) y por vencer (ámbar 30d).
6. `vehiculo_chofer` puede tener N vehículos por chofer (grilla, no 1:1).

## Decisión de migración (Blazor)

- **Solo lectura** por ahora (igual que `cliente`): lista + ficha 5 pestañas, sin ABM.
  La escritura sigue siendo de FoxPro (tabla `chofer` dueño FoxPro). Botonera de la ficha y
  de la lista deshabilitada (placeholder), lista para activar cuando se migre el ABM.
- Patrón calcado de **Clientes** (`ClientesAbm.razor` + `ClienteDetalleDialog.razor`):
  mismos estilos `cli-*`, misma grilla MudTable, mismo dialog con tabs.
