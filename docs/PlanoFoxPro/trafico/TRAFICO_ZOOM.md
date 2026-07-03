# Lógica FoxPro — Zoom del Viaje (`trafico_zoom.scx`)

> Extraído del binario DBF/SCT con lector Python custom.  
> Fuente: `C:\MetroCarSys\Forms\trafico_zoom.scx` — 135 objetos, 29 controles con código.  
> Referencia directa para construir el ABM de reservas en Blazor.
>
> ⚠️ **Nombres de campo:** este doc cita los nombres del DBF FoxPro (`estado_viaje`,
> `hs_presentacion`, `nombre_cliente`, `id_vehiculo_tipo`…). La réplica SQL los **trunca a
> 10 chars** (`estado_via`, `hs_present`, `nombre_cli`, `id_vehicu2`…). Mapa de truncados en
> `TRAFICO2_FILTROS.md` y `ESCRITURA_CIRCUITO.md` (skill `modulo-trafico`) — verificar
> SIEMPRE contra `sys.columns` antes de escribir SQL nuevo.

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

## La pantalla contenedora (grilla de Tráfico) — ver docs dedicados

> Acá había un análisis preliminar hecho por capturas de pantalla (mayo 2026) del form
> contenedor. Quedó **superado por las extracciones reales del binario** y se eliminó para
> evitar contradicciones (p. ej. afirmaba que el botón "Libe" equivalía a "Pasar a Sin
> Asignar", cuando la extracción demostró que **Libe = FINALIZAR el viaje**; que "S/C" era
> "sin cargo", cuando es el filtro *sin cronograma*; y que "Comb" combinaba rutas, cuando
> abre el módulo Combustible).

La referencia vigente sobre `trafico2.scx` (el form productivo — `trafico.scx` es una
versión vieja) es:

| Tema | Doc |
|---|---|
| Filtros: combos U/Pr y U/Cb, S/C, Cxl (cancelados), Emp/Tur, panel Buses, colores, menú contextual, "Aplicar Filtros" | `TRAFICO2_FILTROS.md` |
| Botones de ESCRITURA de la toolbar: Chequeo, Asig U/P, Otra Unidad, Reas, **Libe (=FINALIZAR)**, Frc (francos) | `TRAFICO2_TOOLBAR.md` |
| Bitácora ("Historia del viaje", `viaje_log`) | `TRAFICO_HISTORIAL.md` |
| Matriz consolidada operación → tablas → campos → log | `.claude/skills/modulo-trafico/references/ESCRITURA_CIRCUITO.md` |

Datos que aquel análisis dejaba "a investigar", ya resueltos:

- **"Novedad sobre el viaje"** → tabla `libro_novedad` (48.160 filas) — migrado solo lectura.
- **"Lista de pasajeros"** → `viaje_pasajero` + `viaje_pasajero_detalle` (casi vacías) — migrado solo lectura.
- **Motivos de cancelación** → tabla `viaje_motivo_cancela` (no "motivo_cancela").
- **"Ver Datos Extras"** (7 ítems), operador → `cliente_operador` (no `cliente`) — migrado solo lectura.
