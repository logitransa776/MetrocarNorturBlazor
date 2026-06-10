# Lógica FoxPro — Zoom del Viaje (`trafico_zoom.scx`)

> Extraído del binario DBF/SCT con lector Python custom.  
> Fuente: `C:\MetroCarSys\Forms\trafico_zoom.scx` — 135 objetos, 29 controles con código.  
> Referencia directa para construir el ABM de reservas en Blazor.

---

## Contexto

Esta pantalla es el formulario de edición de un viaje/reserva individual. Se abre desde la grilla principal de tráfico haciendo doble clic sobre una fila. Recibe como parámetro el `id_viaje` y el modo de apertura (`cTipoMovimiento`).

**Modos de apertura:**

| `cTipoMovimiento` | Significado |
|---|---|
| `"MODIFICA"` | Edición normal |
| `"DUPLICA"` | Duplicar el viaje como base para uno nuevo |
| `"CANCELADO"` | Ver viaje cancelado (read-only + Reactivar) |
| `"CANCELAR"` | Cancelar directamente |
| `"CONSULTA"` | Solo lectura |

---

## Máquina de estados

El estado del viaje (`estado_viaje` en tabla `viaje`) determina qué botones están habilitados. **En Blazor, esta validación debe hacerse en la capa de servicio, no solo en la UI.**

| Estado | Modificar campos | bModificar | bCancelarViaje | bSinAsignar | bChofer | bAdicional | bValorServicio | bReactiva |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| **SIN ASIGNAR** | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **CHEQUEO** | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **ASIGNADO** | ❌ | ❌ | ❌ | ✅ | ❌ | ✅ | ❌ | ❌ |
| **CURSO** | ❌ | ❌ | ❌ | ✅ | ❌ | ✅ | ❌ | ❌ |
| **FINALIZADO** | ❌ | ❌ | ❌ | ❌ | ✅ | ✅ | ✅ | ❌ |
| **FACTURADO** | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ✅ | ❌ |
| **CANCELADO** | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ |

---

## Botón MODIFICAR (`bModificar.Click`)

### Validaciones (en orden, bloquean si fallan)

1. `f_pedido` no puede estar vacía — "No se cargó la fecha de pedido"
2. `f_reserva` no puede estar vacía — "No se cargó la fecha de reserva"
3. `cliente` no puede estar vacío
4. `id_servicio` no puede estar vacío — "Debe cargar al menos 1 servicio"
5. `d_destino` (Desde) no puede estar vacío
6. `h_destino` (Hasta) no puede estar vacío
7. `f_pedido` no puede ser mayor a `f_reserva`
8. Si `pax > capacidad del vehículo` → warning confirmable (no bloquea, el usuario puede forzar)
9. `provincia` (Distrito Inicio) no puede estar vacío
10. Si `hs_fin` está cargada: `hs_inicio` no puede ser mayor a `hs_fin`

### UPDATE en tabla `viaje`

Un solo UPDATE con ~35 campos. Campos y su origen:

```sql
UPDATE viaje SET
  -- Fechas
  f_reserva         = @f_reserva,
  f_pedido          = @f_pedido,
  str_f_reserva     = FORMAT(@f_reserva, 'yyyyMMdd'),  -- string YYYYMMDD, siempre sincronizado

  -- Horarios
  hs_inicio         = @datetime_inicio,       -- datetime compuesto de f_reserva + hh + mm
  hs_s_inicio       = @string_HH_MM,          -- string "HH:MM" derivado de hs_inicio
  hs_presentacion   = @datetime_presentacion, -- hs_inicio - offset según opción seleccionada
  hs_fin_aprox      = @datetime_fin_aprox,    -- calculado sumando duración de servicios
  hs_fin            = @datetime_fin,          -- fin real (puede ser vacío)
  duracion          = @string_duracion,       -- string calculado (ej: "02:30")

  -- Destino
  d_destino         = @d_lugar_destino,
  h_destino         = @h_lugar_destino,
  d_destino_prov    = @provincia,

  -- Servicios
  id_servicio       = @id_servicio,
  id_servicio1      = @id_servicio1,
  id_servicio2      = @id_servicio2,

  -- Cliente (desnormalizado — actualizar siempre aunque ya esté en tabla cliente)
  id_cliente        = @id_cliente,
  nombre_cliente    = @razon_social,

  -- Vehículo y pasajeros
  id_vehiculo_tipo  = @id_vehiculo_tipo,
  pax               = @pax,
  agua              = @agua,
  km                = @km,

  -- Grupo
  grupo             = @grupo,
  f_grupo_fin       = @f_grupo_fin,

  -- Otros datos
  cronograma        = @cronograma,
  id_operador       = @id_operador,
  comentario        = @comentario,
  vuelo             = @vuelo,
  nombre_guia       = @nombre_guia,
  Voucher_nro       = @n_voucher,
  file              = @file,

  -- Financiero (dos conceptos distintos)
  importe_convenido = @importe,               -- lo acordado con el cliente
  moneda_convenida  = @moneda,
  importe_pago      = @importe_pago,          -- lo que realmente pagó
  moneda_pago       = @moneda_pago,
  sin_cargo         = @sin_cargo,
  sin_cargo_pago    = @sin_cargo_pago,
  descuento_convenido = @porcentaje_descuento,
  estado_importe    = CASE WHEN @importe > 0 THEN 'FIJADO' ELSE '' END,

  -- Auditoría (obligatorios en cada UPDATE)
  f_modify          = GETDATE(),
  u_modify          = @usuario_actual

WHERE id_viaje = @id_viaje
```

### Cascada en `cliente_grupo`

Si `grupo != "SIN GRUPO"`:

```
¿Existe cliente_grupo WHERE id_cliente = X AND nombre = grupo?
  NO  → INSERT INTO cliente_grupo (id_cliente, nombre, f_grupo_fin)
  SÍ  → ¿f_reserva > f_grupo_fin del grupo?
          SÍ → UPDATE cliente_grupo SET f_grupo_fin = f_reserva WHERE id = grupo_id
               UPDATE viaje SET f_grupo_fin = f_reserva WHERE id_grupo = grupo_id
               (actualiza TODOS los viajes del grupo, no solo este)
```

### Auditoría en `viaje_log`

Compara el registro antes y después campo por campo. Si algún campo cambió:

```sql
INSERT INTO viaje_log
  (id_viaje, usuario, motivo, hora, cronograma, id_chofer, interno_ori, interno_new, comentario)
VALUES
  (@id_viaje, @usuario, 'MODIFICO', GETDATE(), '', '', 0, 0, @lista_cambios)
```

Donde `@lista_cambios` es texto libre con formato `"campo: valorViejo -> valorNuevo\n"` por cada campo que cambió.

---

## Botón CANCELAR VIAJE (`bCancelarViaje.Click`)

**Solo disponible si estado = `"SIN ASIGNAR"` o `"CHEQUEO"`.**

### Flujo

1. Confirmación del usuario ("¿Está totalmente seguro?")
2. Abre form `trafico_motivo_cancela` → devuelve `id_motivo` (obligatorio, bloquea si no se carga)
3. Ejecuta el UPDATE

### UPDATE en `viaje`

```sql
UPDATE viaje SET
  estado_viaje  = 'CANCELADO',
  id_motivo     = @id_motivo,
  interno       = 0,
  id_vehiculo   = '               ',  -- SPACE(15): campo char fijo, no NULL
  id_chofer     = '               ',  -- SPACE(15)
  nombre_chofer = '                              ',  -- SPACE(30)
  franco        = 0
WHERE id_viaje = @id_viaje
-- O WHERE id_viaje_int = @id_viaje_int  (si es ruta, cancela todos los sub-viajes)
```

### Auditoría

```sql
INSERT INTO viaje_log (..., motivo, comentario)
VALUES (..., 'CANCELO', @texto_motivo_cancela)
```

### Cascada en `cliente_grupo`

```
¿Todos los viajes del mismo cliente + grupo están CANCELADOS?
  SÍ → DELETE FROM cliente_grupo WHERE id_cliente = X AND nombre = grupo
```

### Llamada a GPS

```
gps_xlm(id_viaje)  -- notificación GPS/XML externa. Investigar qué hace en producción.
```

### Viajes en ruta (`id_viaje_int > 0`)

Si el viaje pertenece a una ruta (`id_viaje_int > 0`), la cancelación actualiza **todos** los viajes con ese `id_viaje_int`. Se logea cada uno individualmente.

---

## Botón PASAR A SIN ASIGNAR (`bSinAsignar.Click`)

**Solo disponible si estado = `"ASIGNADO"` o `"CURSO"`.**

### Efecto: libera chofer y vehículo

```sql
-- Viaje simple (id_viaje_int = 0)
UPDATE viaje SET
  estado_viaje  = 'SIN ASIGNAR',
  chequeo       = 0,
  interno       = 0,
  id_vehiculo   = '               ',
  id_chofer     = '               ',
  nombre_chofer = '                              ',
  franco        = 0,
  fletero       = '               ',
  id_interno    = '          '
WHERE id_viaje = @id_viaje

UPDATE vehiculo SET
  estado   = 'LIBERADO',
  id_viaje = 0
WHERE id_viaje = @id_viaje
```

Para rutas (`id_viaje_int > 0`): mismo UPDATE en `viaje` con `WHERE id_viaje_int = X AND estado IN ('ASIGNADO', 'CURSO')`. En `vehiculo`: `id_viaje_int = 2` (no 0 — diferencia intencional para rutas).

### Auditoría

```sql
INSERT INTO viaje_log (..., motivo, comentario)
VALUES (..., 'MODIFICO', 'PASO A SIN ASIGNAR')
```

---

## Botón DUPLICAR (`bDuplicar.Click`)

### Validaciones previas

Mismas que Modificar (1-6 y 9) más:
- Si `f_reserva < hoy` → warning confirmable
- Si tiene adicionales → pregunta si copiarlos
- Si `tipo_grupo = "NUEVO"` y grupo tiene nombre → verificar que ese nombre no exista ya en `cliente_grupo`

### Flujo

1. Abre form `reserva_transportacion_cantidad_servicio` → devuelve `nServicioContratado` (cuántas copias crear)
2. Gestiona `cliente_grupo` (igual que Modificar)
3. Por cada copia:

```sql
INSERT INTO viaje
  (str_f_reserva, origen, f_pedido, f_reserva, hs_inicio, hs_s_inicio,
   hs_fin_aprox, estado_viaje, pax, nombre_guia, id_grupo, grupo, f_grupo_fin,
   vuelo, id_servicio, id_cliente, nombre_cliente, comentario, f_create, u_create,
   d_destino, h_destino, d_destino_prov, id_vehiculo_tipo,
   importe_convenido, moneda_convenida, id_servicio1, id_servicio2,
   cronograma, cronogramacbio, km, sin_cargo, estado_importe, hs_presentacion,
   id_operador, agua, cabecera, recorrido_celular)
VALUES
  (..., 'SIN ASIGNAR', ...)  -- siempre nace como SIN ASIGNAR
```

4. Si `lCopiaAdicional`:
```sql
INSERT INTO viaje_adicional (id_viaje, id_adicional, nombre, cantidad)
-- por cada adicional del viaje original
```

5. Auditoría:
```sql
INSERT INTO viaje_log (..., motivo, comentario)
VALUES (..., 'ALTA', 'DUPLICO RESERVA')
```

---

## Botón VALOR SERVICIO (`bValorServicio`)

Abre form `reserva_transportacion_valor`. Si el usuario confirma, hace un UPDATE parcial:

```sql
UPDATE viaje SET
  sin_cargo           = @sin_cargo,
  importe_convenido   = @importe_convenido,
  moneda_convenida    = @moneda,
  moneda_pago         = @moneda_pago,
  importe_pago        = @importe_pago,
  sin_cargo_pago      = @sin_cargo_pago,
  descuento_convenido = @porcentaje_descuento
WHERE id_viaje = @id_viaje
```

---

## Cálculo de hora de presentación

El campo `hs_presentacion` se calcula restando segundos a `hs_inicio`:

| Opción en UI | Cálculo |
|---|---|
| "en hora" | `NULL` / vacío |
| "5 minutos antes" | `hs_inicio - 300 seg` |
| "15 minutos antes" | `hs_inicio - 900 seg` |
| "30 minutos antes" | `hs_inicio - 1800 seg` |
| "45 minutos antes" | `hs_inicio - 2700 seg` |
| "1 hora antes" | `hs_inicio - 3600 seg` |
| "2 horas antes" | `hs_inicio - 7200 seg` |

---

## Cálculo de hora fin aproximada

Se suma la duración de cada servicio (hasta 3 servicios encadenados):

```
hs_fin_aprox = hs_inicio
  + servicio.horas_duracion * 3600 + servicio.minutos_duracion * 60
  + servicio1.horas_duracion * 3600 + servicio1.minutos_duracion * 60  (si existe)
  + servicio2.horas_duracion * 3600 + servicio2.minutos_duracion * 60  (si existe)
```

---

## Resumen de tablas involucradas

| Tabla | Operaciones | Botones |
|---|---|---|
| `viaje` | UPDATE | Modificar, CancelarViaje, SinAsignar, ValorServicio |
| `viaje` | INSERT | Duplicar |
| `viaje_log` | INSERT (auditoría) | Todos los que escriben |
| `viaje_adicional` | READ + gestión via sub-form | bAdicional |
| `viaje_adicional` | INSERT (copia) | Duplicar (opcional) |
| `cliente_grupo` | INSERT / UPDATE | Modificar, Duplicar |
| `cliente_grupo` | DELETE | CancelarViaje (si todos cancelados) |
| `vehiculo` | UPDATE estado | SinAsignar |
| `servicio` | SELECT (calcular duración) | Al cambiar servicio en UI |
| `viaje_motivo_cancela` | SELECT (buscar texto motivo) | CancelarViaje |

---

## Reglas no obvias — críticas para Blazor

| # | Regla | Por qué importa |
|---|---|---|
| 1 | `str_f_reserva` = `f_reserva` en formato `"YYYYMMDD"` | Campo string redundante que **siempre** se actualiza junto con `f_reserva` |
| 2 | `nombre_cliente` en `viaje` es desnormalizado | Aunque ya esté en `cliente.razon_social`, hay que escribirlo en `viaje` también |
| 3 | `hs_s_inicio` es string `"HH:MM"` derivado de `hs_inicio` | Campo calculado que se persiste — no es solo para mostrar |
| 4 | `estado_importe = "FIJADO"` si importe > 0, vacío si no | Lógica de negocio que debe replicarse en la capa de servicio |
| 5 | Limpiar chofer/vehículo usa `SPACE(n)`, no `NULL` | Los campos son `char` de ancho fijo; `NULL` rompería consultas existentes |
| 6 | `viaje_log` se inserta en **cada operación** | Es el historial de auditoría completo — no es opcional |
| 7 | `importe_convenido` ≠ `importe_pago` | Son dos conceptos distintos: precio acordado vs. pago recibido |
| 8 | `gps_xlm()` se llama post-cancelación y post-liberación | Función GPS externa — investigar si tiene efecto en producción antes de replicar |
| 9 | `id_viaje_int > 0` significa que el viaje es parte de una ruta | Las operaciones afectan múltiples filas de `viaje` — no asumir 1 viaje = 1 fila |
| 10 | `lMantenimiento` desbloquea estados normalmente bloqueados | Flag de "modo mantenimiento" que bypasea la máquina de estados — útil para roles admin |

---

## Formularios secundarios que llama este form

| Form | Cuándo | Qué hace |
|---|---|---|
| `trafico_motivo_cancela` | bCancelarViaje | Selección de motivo de cancelación → devuelve `id_motivo` |
| `trafico_zoom_adicional` | bAdicional | ABM de `viaje_adicional` |
| `trafico_zoom_chofer` | bChofer | Asignación de chofer y vehículo |
| `reserva_transportacion_valor` | bValorServicio | Edición de importes y monedas |
| `reserva_transportacion_cantidad_servicio` | bDuplicar | Cuántas copias crear |

---

## Grilla principal de Tráfico (`trafico.scx`)

> Contexto extraído de capturas de pantalla del sistema productivo (08/05/2026).  
> Esta sección documenta la pantalla contenedora que lista los viajes del día y desde la que se abre `trafico_zoom.scx`.

## Título y encabezado

```
Trafico del VIERNES 08/05/2026 - Cantidad de Servicios : 330
```

El título cambia dinámicamente con la fecha seleccionada y muestra la cantidad total de servicios del día. Ambos datos deben replicarse en el reporte Blazor.

---

## Columnas de la grilla

La grilla tiene **dos paneles**: el panel principal (izquierda/centro) y un panel lateral fijo (derecha) con la asignación de chofer.

### Panel principal

| Columna | Campo probable en `viaje` | Observaciones |
|---|---|---|
| **Reserva** | `f_reserva` | Fecha de la reserva (dd/mm/yyyy) |
| **H. Pre** | `hs_presentacion` | Hora de presentación (HH:mm) — offset antes de salida |
| **H. Ini** | `hs_inicio` | Hora de inicio del servicio |
| **H. Fin** | `hs_fin` / `hs_fin_aprox` | Hora fin real o aproximada |
| **Tur** | `turno` o derivado de `hs_inicio` | Turno (no confirmado) |
| **Ref** | `id_viaje` o `n_referencia` | Número de referencia interno |
| **Emp** | `id_empresa` / `id_operador` | Empresa u operador |
| **UI/Pr** | interno o externo | Unidad Interna / Propia (?) |
| **U/Cb** | `id_vehiculo` | Unidad / Código de vehículo |
| **Chq** | `chequeo` | Flag de chequeo (0/1) |
| **Ag** | ¿`id_agencia`? | Agencia (a confirmar en schema) |
| **Recorrido** | `d_destino` + `h_destino` | Texto "Origen a Destino" — concatenado en UI |
| **Fletero** | `fletero` | Nombre del fletero externo (si aplica) |
| **Chofer** | `nombre_chofer` | Nombre del chofer asignado |
| **Veh** | `id_vehiculo_tipo` | Tipo de vehículo (BUS, VAN, MNI, AUTO) |
| **Cliente** | `id_cliente` / abreviado | Código o abreviatura del cliente |
| **Pax** | `pax` | Cantidad de pasajeros |
| **Agua** | `agua` | Flag agua (0/1 o cantidad) |
| **Adj** | `id_adjunto` o similar | Adjunto vinculado (?) |
| **Comentario** | `comentario` | Texto libre de comentario |
| **Grupo** | `grupo` | Nombre del grupo (o "SIN GRUPO") |
| **Vuelo** | `vuelo` | Número de vuelo |

### Panel lateral derecho (asignación)

Este panel parece ser una vista separada de asignación de chofer, scrolleable independientemente:

| Columna | Descripción |
|---|---|
| **Fletero** | Empresa fletero (ej: "NORTUR") |
| **Interno** | Número interno del vehículo (ej: 1, 2, 3…) |
| **Chofer** | Apellido del chofer asignado |
| **2° Chofer** | Segundo chofer (si aplica) |
| **Franco** | Flag de franco / descanso |

---

## Colores de fila (código visual de estado)

Los colores identifican visualmente el estado de cada viaje sin abrir el zoom:

| Color | Estado probable | Notas |
|---|---|---|
| **Rosa / fucsia** | CANCELADO | Filas claramente canceladas |
| **Fondo blanco** | SIN ASIGNAR o ASIGNADO | Estado normal/pendiente |
| **Amarillo / oro** | Seleccionado / fila activa | Fila sobre la que está el cursor |
| **Verde** | FINALIZADO o GPS activo | A confirmar — puede ser "en curso con GPS" |
| **Azul claro** | (a confirmar) | Posiblemente CHEQUEO o CURSO |

> Regla para Blazor: implementar columna de color de fondo en la `MudTable` con `RowClass` o `RowStyle` basado en `estado_viaje`.

---

## Toolbar de botones (barra superior)

Botones de acción rápida sin abrir el zoom. Muchos operan sobre el viaje seleccionado en la grilla:

| Botón | Acción probable |
|---|---|
| **S/C** | Sin cargo — marcar viaje como sin cargo |
| **Ref** | Refresh / actualizar grilla |
| **Emp** | Empresa — filtrar o asignar empresa |
| **Tur** | Turno — filtrar por turno |
| **<<** / **>>** | Día anterior / día siguiente |
| **Cql** | (a investigar — posiblemente "Cancelar/Limpiar" o filtro) |
| **Chequeo** | Marcar viaje como chequeado |
| **Asig U/P** | Asignar Unidad Propia |
| **Otra Unidad** | Asignar unidad de terceros / fletero |
| **Reas** | Reasignar chofer/vehículo |
| **Libe** | Liberar — equivale a "Pasar a Sin Asignar" |
| **Erc** | (a investigar) |
| **Comb** | Combinar viajes (rutas) |
| **GPS** | Abrir panel / vista GPS |

### Filtros / checkboxes de la toolbar

| Checkbox | Efecto |
|---|---|
| **GPS** (toggle botón) | Activa modo GPS en la grilla |
| **Chequeo** | Muestra solo viajes marcados para chequeo |
| **NORTUR** | Filtra solo viajes de flota propia NORTUR (excluye fleteros) |
| **Buses** | Filtra solo vehículos tipo BUS |

---

## Menú contextual (clic derecho sobre fila)

Disponible al hacer clic derecho sobre cualquier viaje de la grilla. Acciones:

### Nivel 1

| Opción | Descripción |
|---|---|
| **Ubicar en GPS** | Submenú — ver ubicación del vehículo en mapa GPS |
| **Refresh** | Refresca los datos del viaje seleccionado |
| **Novedad sobre el viaje** | Registra una novedad/incidencia sobre el viaje |
| **Aplicar Filtros s/viaje** | Submenú — aplica filtros basados en datos del viaje seleccionado |
| **Aplicar Filtros** | Submenú — filtros generales de la grilla |
| **Imprimir** | Submenú — opciones de impresión |
| **Exportar a Excel** | Submenú — exporta la grilla o el viaje a Excel |
| **Mantenimiento Viajes** | Submenú — operaciones de mantenimiento admin |
| **Lista de pasajeros** | Abre/imprime la lista de pasajeros del viaje |
| **Historia del viaje** | Abre el log de auditoría del viaje (`viaje_log`) |
| **Copiar Cronogramas** | Copia el cronograma de un viaje a otro |
| **Ver Datos Extras** | Submenú — accesos rápidos a datos relacionados |

### Submenú "Ver Datos Extras"

| Opción | Qué muestra |
|---|---|
| **Ver Datos Operador** | Datos del operador/agencia asignada al viaje |
| **Ver Datos Vehículo** | Ficha del vehículo asignado (de tabla `vehiculo`) |
| **Ver Datos Chofer** | Ficha del chofer asignado (de tabla `chofer`) |
| **Ver Datos Cliente** | Ficha del cliente (de tabla `cliente`) |
| **Ver Adjunto** | Abre adjunto vinculado al viaje |
| **Ver Recorrido** | Muestra el recorrido detallado del viaje |
| **Ver Adicionales** | Lista los adicionales del viaje (`viaje_adicional`) |

---

## Relevancia para el ABM en Blazor

### Qué replicar en el Informe de Tráfico

1. **Título dinámico** con fecha y cantidad de servicios del día.
2. **Grilla con todas las columnas** listadas arriba — usar `MudTable` con columnas fijas.
3. **Colores de fila por estado** — `RowStyle` o `RowClass` condicional en MudBlazor.
4. **Panel lateral de asignación** — puede ser una segunda tabla sincronizada o columnas adicionales visibles solo con scroll horizontal.
5. **Filtros de toolbar** — NORTUR / Buses / Chequeo como `MudCheckBox` o `MudToggleIconButton`.
6. **Navegación día anterior/siguiente** — botones `<<` / `>>` que modifican la fecha y recargan.

### Qué replicar en el ABM (Zoom del Viaje)

1. **Acceso a "Historia del viaje"** → leer `viaje_log` y mostrar en modal o página separada.
2. **"Ver Datos Extras"** → navegación a fichas de vehículo, chofer, cliente (links a sus propios ABM).
3. **"Novedad sobre el viaje"** → formulario para registrar incidencia (tabla a investigar: `viaje_novedad`?).
4. **"Lista de pasajeros"** → leer `viaje_pasajero` y mostrar en modal.

### Tablas adicionales a investigar (no estaban en el MD original)

| Tabla probable | Para qué |
|---|---|
| `viaje_novedad` | Novedades/incidencias sobre un viaje |
| `viaje_pasajero` | Lista de pasajeros por viaje |
| `motivo_cancela` | Catálogo de motivos de cancelación |
| `operador` | Datos del operador/agencia |
| `agencia` | Agencias vinculadas a reservas |
