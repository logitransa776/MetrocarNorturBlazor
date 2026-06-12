# Lógica FoxPro — Plantillas de Reservas (ciclo completo)

> Menú: **Reservas → Plantillas → Crear / Mantenimiento** y **Reservas → Reservas por Plantillas**.
> Forms: `reserva_plantilla_crear.scx`, `reserva_plantilla_mantenimiento.scx`,
> `reserva_plantilla_mantenimiento_abm.scx`, `reserva_plantilla_mantenimiento_nombre.scx`,
> `reserva_plantilla_armar.scx`. Relacionado (Utilitarios): `reserva_plantilla_repara.scx`.
> Extraído del binario con `foxpro-extract` (12/06/2026).

---

## Concepto

Una **plantilla** es un conjunto de servicios-tipo (filas en `reserva_plantilla`) agrupados
por nombre (`id_reserva`, char — el nombre ES la clave de agrupación, no hay tabla cabecera).
El **armado** (`reserva_plantilla_armar`) toma una plantilla + cliente + rango de fechas +
días de la semana y genera **una fila en `viaje` por cada servicio × cada fecha que matchea**,
con `origen = 'P'`.

Es el flujo dominante del negocio: **~440K viajes con origen 'P' vs ~81K con origen 'T'**
(86% del volumen). Clientes corporativos tipo MAPFRE con recorridos fijos
(ej. plantilla "MAPFRE LUN-MAR-JUE": 8 servicios CABECERA_SERV).

Datos actuales: 574 filas en `reserva_plantilla`, **9 plantillas** distintas.

```
crear ──► reserva_plantilla (N filas por plantilla)
                │   mantenimiento: ABM fila por fila, renombrar, duplicar, eliminar todo
                ▼
armar (cliente + fechas + días) ──► viaje (origen 'P', estado SIN ASIGNAR) × fecha × fila
                                    + viaje_adicional + viaje_log + lote en parametro
```

---

## Tabla `reserva_plantilla` (columnas SQL reales)

`id_reserva` (nombre plantilla, clave de agrupación), `cronograma`, `hs_inicio`/`hs_fin`
(char(5) "HH:MM"), `hs_entrada`, `desde`/`hasta` (+`desde_rama`/`hasta_rama`),
`id_servici`, `id_vehicul` (= tipo de vehículo), `comentario`, `hs`, `km`, `km_real`, `pax`,
`id` (autoinc), `id_guia`, `nombre_gui`, `guia_dueno` (N/C/S), `gps_cod`,
`adi_cod_1..5`, `adi_nom_1..5`, `adi_can_1..5` (adicionales en 5 slots planos),
`d_destino_` (provincia/distrito), `cabecera` (código de 16 posiciones, ver abajo),
`empresa_de` (empresa destino), `iata_desde`, `iata_hasta`, `recorrido_` (recorrido celular
"DESDE / HASTA", máx 35), `tipo_mov`, `cod_cab`, `dia_siguie`.

### La CABECERA — código posicional de 16 caracteres

Identificador del recorrido para el sistema GPS/celular. `cc_arma`/`cc_desarma`:

| Posición | Largo | Contenido | Ejemplo (`MP0720ECONOPT00`) |
|---|---|---|---|
| 1–2 | 2 | Código cliente abreviado (2 letras mayúsculas validadas A–Z) | `MP` (MAPFRE) |
| 3–6 | 4 | Hora de inicio HHMM (se autoarma al elegir hora) | `0720` |
| 7 | 1 | `E` = Entrada / `S` = Salida (a la planta) | `E` |
| 8–10 | 3 | Código IATA origen (tabla `iata`, 106 filas, F5 → `iata_busca`) | `CON` (Constitución) |
| 11–13 | 3 | Código IATA destino | `OPT` (Óptima) |
| 14 | 1 | Nº de recorrido (0–5) | `0` |
| 15 | 1 | Refuerzo (0 = no, 1 = sí) | `0` |
| 16 | 1 | `id_vehiculo_rango` del tipo de vehículo (se pega al elegir vehículo) | — |

La cabecera debe ser **única dentro de la plantilla** (valida contra el cursor al agregar).
Puede valer `"SIN CABECERA"` (check en el ABM de mantenimiento que deshabilita los campos cc_*).

---

## Crear Plantillas (`reserva_plantilla_crear.scx`)

Carga en un **cursor en memoria** (`r_plantilla`, CREATE CURSOR con todos los campos) fila
por fila; recién al apretar **Generar Plantilla** hace el INSERT masivo a `reserva_plantilla`
y cierra el form. Si se cancela antes, no queda nada.

- **Nombre de plantilla** (`plantilla`, máx 30): Valid → no debe existir ya en
  `reserva_plantilla` ("El nombre de la plantilla ya existe").
- `cc_cliente` se deshabilita tras la primera fila (todas las filas comparten el prefijo).
- Hora fin: al elegir servicio se autocalcula con `horas_dura`/`minutos_du` del servicio
  (`arma_hora_fin`); si el servicio no tiene duración queda igual a la hora de inicio.
- Vehículo → copia `pax` máximo y `id_vehiculo_rango` (posición 16 de la cabecera).
- Guía: optiongroup **Nuestra** (valida `id_guia` contra tabla `guia`, `guia_dueno='N'`) /
  **Cliente** (texto libre, `id_guia='GUIA CLIENTE'`, dueño `'C'`) / **Sin Guía** (`'S'`).
- Adicionales: abre el dialog legacy de 5 slots con `lValidaPrecio=.F.` (sin importes).
- **Validaciones de `audita_carga`**: plantilla, hora/min inicio, vehículo, servicio,
  pax>0, (km>0 **o** hs>0), cronograma, desde, hasta, recorrido celular desde+hasta
  (suma ≤ 35 chars con el " / ", sin carácter `/` interno), provincia, empresa destino,
  cc_cliente (2 letras), cc_hora, cc_es, IATA inicio y fin, guía según optiongroup.
- **Agregar**: si desde = hasta pide confirmación; cabecera duplicada en el cursor bloquea.
- **Eliminar**: borra la fila marcada del cursor (solo memoria).
- **Generar Plantilla**: confirma → loop INSERT INTO `reserva_plantilla` por cada fila del
  cursor (todas con `id_reserva` = nombre elegido) → cierra.

---

## Mantenimiento (`reserva_plantilla_mantenimiento.scx`)

Pantalla lista del patrón ABM. Combo de plantillas
(`SELECT id_reserva … GROUP BY id_reserva`) + **Buscar** → grilla de 15 columnas con las
filas de esa plantilla (orden `hs_inicio`).

| Botón | Lógica |
|---|---|
| **Agregar** | `reserva_plantilla_mantenimiento_abm` con `"alta"` — INSERT de una fila a la plantilla actual (hereda nombre, cc_cliente y empresa destino de la fila cursor) |
| **Modificar** / doble clic | ídem con `"modifica"` — UPDATE por `id` (ver abajo) |
| **Eliminar** | confirma → **`DELETE FROM reserva_plantilla WHERE id = …` (borrado FÍSICO, sin f_delete)** |
| **Eliminar Todo** | confirma → `DELETE … WHERE id_reserva = <plantilla>` (toda la plantilla, físico) |
| **Renombrar** | dialog `_nombre` → `UPDATE reserva_plantilla SET id_reserva = nuevo WHERE LEFT(id_reserva,30) = LEFT(viejo,30)`. Si el nombre destino ya existe avisa que se **fusionarán** ("se duplicaran servicios") y pide confirmación |
| **Duplicar** | dialog `_nombre` → copia todas las filas (`SCATTER … EXCEPT id` → `APPEND` con `id_reserva = nuevo`). Mismo aviso si el destino existe |
| **Impresión** | `REPORT FORM reserva_plantilla.frx` (preview) |
| **Limpia** | resetea y re-habilita el combo |

El dialog `_nombre` (`reserva_plantilla_mantenimiento_nombre.scx`) ofrece **texto libre**
(plantilla nueva) o **combo de plantillas existentes** (para fusionar/agrupar).

### ABM de fila (`reserva_plantilla_mantenimiento_abm.scx`)

Mismos campos y validaciones que crear (incluye recorrido celular y cc_*). En `"modifica"`
hace `cc_desarma()` para descomponer la cabecera en los 8 campos; si la cabecera es
"SIN CABECERA" marca el check y deshabilita los cc_*. UPDATE por `id` de TODOS los campos
(incluidos los 5 slots de adicionales y `gps_cod`, cuyo combo sale de la tabla `cabecera`).
Tras grabar, deja `nBuscaId` para que la grilla reposicione.

⚠️ El INSERT del alta **no graba** `d_destino_prov`, `km_real`, `gps_cod`, `iata_desde/hasta`
(la modificación sí los actualiza) — rareza heredada del fuente; igualarlo en Blazor.

---

## Armado / generación (`reserva_plantilla_armar.scx` — "Reservas por Plantillas")

Pantalla del screenshot "Armado de Plantilla". Flujo:

1. **Combo plantilla + Buscar** → carga `cursorReservaPlantilla` (orden `hs_inicio`) y
   muestra grilla (9 col). **Si alguna fila no tiene cabecera, bloquea**: "Repare la
   Plantilla antes de usarla" (→ form `reserva_plantilla_repara` en Utilitarios).
   Si pasa, habilita el resto de los controles y deshabilita el combo.
2. **Cliente** (F5 busca) — valida existencia y toma razón social.
3. **Desde Fecha / Hasta Fecha** (F5 calendario) — rango a generar.
4. **Días**: checks Lunes…Domingo + **Feriados**. Botones rápidos:
   "Toda la semana" = L a V; "Todos los días" = los 7 + feriados.
   "Ver Feriados" abre el ABM de la tabla `feriado`.
5. **Generar Plantilla**:
   - Valida: plantilla, cliente, fechas (desde ≤ hasta), al menos un día marcado.
   - Busca feriados del rango (`SELECT fecha FROM feriado BETWEEN …`). Si no hay,
     pregunta si quiere verificarlos (puede abortar); si hay, informa cuántos y permite
     revisarlos (`feriado_ver`).
   - Confirmación con la lista de días elegidos.
   - **Toma lote**: `parametro.lote_plant + 1` → UPDATE → todas las filas de esta corrida
     comparten `viaje.lote` (permite identificar/deshacer la corrida — `reserva_plantilla_elimina_viaje.scx`).
   - Loop por cada fecha del rango: si el día de la semana está marcado **y la fecha no es
     feriado** → `graba_viaje(fecha)`. Si "Feriados" está marcado y la fecha ES feriado →
     también genera. (Un feriado que cae lunes con solo "Lunes" marcado NO se genera —
     feriado excluye salvo check explícito.)
   - Al final: mensaje con Nº de lote + deshabilita todo + vacía la grilla (una corrida
     por apertura; Limpia re-arma).

### `graba_viaje(fecha)` del armado — por cada fila de la plantilla

- `hs_inicio` = datetime(fecha, HH, MM de `hs_inicio` char). Si `hs_inicio > hs_fin`
  (cruza medianoche) la fecha de fin es el día siguiente → `hs_fin_apr`.
- **Check "Utilizar nombre de la planta como Origen/Destino de la cabecera"** (default ON):
  - posición 7 de la cabecera = `'E'` (Entrada) → `d_destino = desde` de la fila,
    `h_destino = empresa_destino`;
  - `'S'` (Salida) → `d_destino = empresa_destino`, `h_destino = hasta`.
  - Check OFF → usa `desde`/`hasta` tal cual.
- `INSERT INTO viaje`: `origen = 'P'`, `estado_viaje = 'SIN ASIGNAR'`,
  `str_f_reserva = DTOS(fecha)`, `cronograma = cronogramacbio =` cronograma de la fila,
  `grupo = 'SIN GRUPO'`, `f_grupo_fin = fecha`, `vuelo = 'SIN VUELO'`,
  id_servicio, id_cliente + nombre_cliente (del form), comentario, pax, km, hs,
  id_vehiculo_tipo (⚠️ también se graba en `id_vehicul` = unidad — herencia rara),
  id_guia/nombre_guia/guia_dueno de la fila, `lote`, `gps_cod`,
  **`id_plantilla` = id de la fila origen** (`id_plantil` en SQL), `d_destino_prov`,
  `cabecera`, `recorrido_celular`, `f_create = DATE()`, `u_create = cUsuario`.
- `nID_viaje = GETAUTOINCVALUE(0)`.
- Adicionales: por cada slot `adi_cod_n` no vacío → `INSERT INTO viaje_adicional
  (id_viaje, id_adicional, nombre, cantidad)` (sin precio).
- `graba_log_viaje(id, usuario, 'ALTA', …, 'CARGA DE PLANTILLA')` (función global).
- `gps_xlm(nID_viaje)` (función global — genera el XML para el sistema GPS).

**Sin transacción** (el BEGIN/ROLLBACK está comentado). Barra de progreso "Termometro".

---

## Reglas no obvias

1. El nombre de la plantilla es la PK lógica de agrupación (`id_reserva` char) — renombrar
   con un nombre existente **fusiona** plantillas, duplicar hacia uno existente **suma** filas.
2. `reserva_plantilla` se borra **físicamente** (a diferencia de los catálogos con `f_delete`).
3. `viaje.lote` agrupa cada corrida de generación; `parametro.lote_plant` es el contador
   global (compartido con Importa Excel).
4. `viaje.id_plantil` apunta al `reserva_plantilla.id` que originó cada viaje — trazabilidad.
5. La lógica E/S de la cabecera decide los destinos reales del viaje según el check de
   "nombre de planta".
6. Feriado excluye generación salvo que el check Feriados esté marcado (y entonces genera
   también el feriado).
7. La pantalla genera **una corrida por apertura** — tras generar queda todo deshabilitado.
8. `viaje_horario` (6 bandas) NO interviene acá — es solo para el informe de banda horaria.
