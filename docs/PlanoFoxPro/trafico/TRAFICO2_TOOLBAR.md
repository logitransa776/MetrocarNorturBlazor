# TRAFICO2_TOOLBAR — Botones de escritura de la toolbar de Tráfico (trafico2.scx)

> Extraído de `trafico2.scx` + `trafico_asigna.scx` + `trafico_reasigna.scx` +
> `trafico_liberar.scx` + `chofer_franco.scx` / `chofer_franco_abm.scx` (02/07/2026, scx_dump).
> Completa la tríada con `TRAFICO_ZOOM.md` (botones del Zoom) y `TRAFICO2_FILTROS.md` (filtros).
> **Este doc es la especificación para la Fase 3 (Tráfico en escritura) del plan Buslink**
> (`docs/buslink/PLAN_MIGRACION_BUSLINK.md`).

## Mapa de botones de la toolbar

| Botón | Objeto | Qué hace | ¿Escribe? |
| --- | --- | --- | --- |
| **Chequeo** | `chequeo` | Marca chequeo de la unidad (contador) | ✅ `viaje.chequeo+1` + log |
| **Asig U/P** | `bAsigna` | Asigna unidad+chofer a viaje SIN ASIGNAR | ✅ vía form `trafico_asigna` ("SIN") |
| **Otra Unidad** | `bAsignaComent` | Asigna OTRA unidad (con motivo obligatorio) | ✅ vía form `trafico_asigna` ("CON") |
| **Reas** | `bReasigna` | Reasigna un viaje ya asignado a otra unidad | ✅ vía form `trafico_reasigna` |
| **Libe** | `bLiberar` | **FINALIZA** el viaje y libera la unidad | ✅ vía form `trafico_liberar` |
| **Frc** | `franco` | Abre el ABM de francos | ✅ vía forms `chofer_franco*` |
| **Cxl** | `Command3` | **Solo FILTRO** — muestra cancelados del rango | ❌ (cancelar vive en el Zoom) |
| Emp / Tur / S/C / combos | varios | Solo filtros (`arma_grid_viaje`/`aplica_filtro`) | ❌ |
| Comb | `combustible` | Abre `trafico_vehiculo_combustible` | (módulo Combustible) |

> ⚠️ **"Libe" NO devuelve el viaje a SIN ASIGNAR** — eso lo hace el botón "Sin Asignar" del
> **Zoom** (ver `TRAFICO_ZOOM.md`). "Libe" = cerrar el servicio: viaje → FINALIZADO + unidad
> → LIBERADA.

## Descubrimientos clave (no estaban en el análisis previo)

1. **`gps_xlm(id_viaje)` se llama en ASIGNO, RE-ASIGNO y FINALIZO** (no solo al cancelar).
   Cada cambio operativo notifica al GPS.
2. **La asignación escribe `vehiculo_km`** (odómetro mensual): si es el primer odómetro del
   mes → `INSERT vehiculo_km (dominio, interno, fecha, ano_y_mes, km_inicio, f_carga)` +
   `UPDATE vehiculo_km SET km_fin = odómetro` del mes anterior.
3. **La asignación puede escribir `chofer_franco`** (sub-flujo "franco trabajado", ver §3.4).
4. **Anti-doble-asignación optimista**: antes del UPDATE relee el viaje — si `id_chofer` ya
   no está vacío → "El Viaje ya fue Asignado por otro usuario" y aborta.
5. **Reasignar resetea `chequeo = 0`**.
6. **Liberar/finalizar asigna ZONA nueva a la unidad** (`vehiculo.id_zona = zona_new`) — el
   despachante dice dónde quedó el vehículo.
7. Estados de `vehiculo.estado` vistos: `ASIGNADO`, `LIBERADO`, `CURSO` (display), `TALLER`,
   `GUARDIA`. Además `vehiculo.trabaja` ≠ vacío = chofer de franco/licencia.

---

## 1. Chequeo (botón `chequeo` — escribe directo, sin form)

**Validaciones:** viaje "S/C" sin cronograma programado y sin interno → error; estado
FINALIZADO/FACTURADO → error.

**Escritura:**
```sql
UPDATE viaje SET chequeo = chequeo + 1 WHERE id_viaje = @nIdViaje;

INSERT INTO viaje_log (id_viaje, usuario, motivo, hora, cronograma, id_chofer,
                       interno_ori, interno_new, comentario)
VALUES (@nIdViaje, @cUsuario, 'CHEQUEO', GETDATE(),
        COALESCE(NULLIF(@cronogramaCbio,''), @cronograma),  -- U/Pr si hay, si no U/Cb
        @id_chofer, @interno, @interno, 'CHEQUEO UNIDAD');
```

> El estado "CHEQUEO" **solo se pinta en pantalla** (`SIN ASIGNAR` + `chequeo > 0`); en la
> base el `estado_viaje` NO cambia. Confirmado: el `REPLACE estado_viaje WITH "CHEQUEO"` es
> sobre el cursor local, el UPDATE solo toca el contador.

---

## 2. Asig U/P y Otra Unidad (botones `bAsigna` / `bAsignaComent` → form `trafico_asigna`)

Ambos abren `trafico_asigna.scx`; la diferencia es el parámetro: `"SIN"` (sin motivo) para
Asig U/P, `"CON"` (motivo **obligatorio**) para Otra Unidad.

### 2.1 Validaciones en la toolbar (antes de abrir el form)

Sobre la unidad seleccionada en el panel Buses (`cursorVehiculoTrafico`):
1. `estado = 'CURSO'` → "La unidad se encuentra realizando un servicio"
2. `estado = 'TALLER'` → "La unidad se encuentra fuera de servicio"
3. `id_chofer` vacío → "¡Unidad no logoneada!" (la unidad debe tener chofer)
4. `id_viaje ≠ 0` → "¡Unidad cumpliendo un viaje!"
5. `estado = 'GUARDIA'` → "Hay que liberarla"
6. `trabaja` no vacío → "El conductor se encuentra de Franco o con Licencia"

Sobre el viaje (`cursorViajeReserva`):
7. **Ruta** (`id_viaje_int ≠ 0`): todos los tramos ANTERIORES deben estar FINALIZADOS
   ("hay otros servicios primero por asignar").
8. **2º conductor**: si el servicio exige 2 conductores (`verifica_2_conductor()`) y la unidad
   no tiene `id_chofer2` → warning con confirmación (y viceversa: unidad con 2 choferes en
   servicio de 1 → confirmación).
9. `controla_vencimiento()` → bloquea si el chofer/vehículo tiene vencimientos (registro,
   CNRT, AEP, VTV...) — muestra `form_error`.
10. `viaje.pax > vehiculo.pax` → warning "esa unidad NO cubre la necesidad de pasajeros"
    con confirmación (no bloquea).

En Otra Unidad se agregan las del viaje: CANCELADO / FINALIZADO / `id_viaje = 0` → error.

### 2.2 Escritura (form `trafico_asigna`, botón Asignar)

**Check anti-concurrencia** (replicar con `UPDLOCK` en Buslink):
```sql
-- Si viaje.id_chofer YA no está vacío → abortar: "ya fue Asignado por otro usuario"
```

**Viaje simple** (`id_viaje_int = 0`):
```sql
UPDATE viaje SET estado_viaje = 'ASIGNADO', odometro = @nOdometro,
       id_chofer = @cId_chofer, nombre_chofer = @cNombre_chofer, franco = @lFranco,
       tipo_chofer = @cTipo_chofer, id_vehiculo = @cId_vehiculo, interno = @nInterno,
       id_chofer2 = @cId_chofer2, id_interno = @cId_interno, fletero = @cId_fletero
WHERE id_viaje = @nIdViaje;

UPDATE vehiculo SET estado = 'ASIGNADO', hs_inicio = @dtHs_inicio, id_viaje = @nId_reserva
WHERE id = @nIdVehiculo;

INSERT INTO viaje_log (id_viaje, usuario, motivo, hora, id_chofer, interno_ori, interno_new, comentario)
VALUES (@nId_reserva, @cUsuario, 'ASIGNO', GETDATE(), @cId_chofer, @nInterno, @nInterno, @cMotivo);
-- gps_xlm(nIdViaje)
```

**Ruta** (`id_viaje_int ≠ 0`): mismos UPDATE pero `WHERE id_viaje_int = @id_viaje_int`
(pega a TODOS los tramos); `vehiculo` guarda además `id_viaje_int`; un `INSERT viaje_log`
+ `gps_xlm()` **por cada tramo**.

**Odómetro mensual** (si es el primer odómetro del mes para ese dominio):
```sql
INSERT INTO vehiculo_km (dominio, interno, fecha, ano_y_mes, km_inicio, f_carga)
VALUES (@cId_vehiculo, @nInterno, GETDATE(), @cAno_y_mes, @nOdometro, CAST(GETDATE() AS date));
-- y cerrar el mes anterior:
UPDATE vehiculo_km SET km_fin = @nOdometro
WHERE dominio = @cId_vehiculo AND ano_y_mes = @cMesAnterior;  -- formato 'YYYYMM'
```

Campos del form: interno/vehículo/chofer (de la unidad elegida), 2º chofer, odómetro,
tipo_chofer (`uso`), fletero, motivo (solo modo CON).

### 2.3 `franco` (campo bit en viaje)

`viaje.franco = @lFranco` marca si el chofer estaba de franco al asignarle el viaje (para la
liquidación de choferes lo paga distinto).

### 2.4 Sub-flujo "franco trabajado" (dentro de `trafico_asigna`)

Si el chofer elegido tiene un franco cargado para HOY (`chofer_franco`), el form habilita dos
salidas (obligatorio elegir una para poder asignar):

- **Cbia. Franco** (mueve el franco a otro día, futuro):
  ```sql
  UPDATE chofer_franco SET fecha = @dNewFranco, valido = 0, trabajo = 0 WHERE id = @nIdFranco
  ```
- **Trabaja Franco** (el chofer trabaja igual — franco trabajado):
  ```sql
  UPDATE chofer_franco SET codigo = 'FT', motivo = 'FRANCO TRABAJADO', valido = 1, trabajo = 1
  WHERE id = @nIdFranco
  ```

---

## 3. Reas (botón `bReasigna` → form `trafico_reasigna`)

### 3.1 Validaciones toolbar

Unidad nueva: TALLER / no logoneada / GUARDIA → error. Viaje: sin asignar (`id_chofer` vacío)
→ "Asígnelo primero"; CANCELADO / FINALIZADO / `id_viaje = 0` → error; mismo chofer → "Unidad
ya asignada a ese chofer".

### 3.2 Escritura (viaje simple)

```sql
UPDATE viaje SET chequeo = 0, estado_viaje = 'ASIGNADO',
       id_chofer = @cId_chofer_new, nombre_chofer = @cNombre_chofer,
       tipo_chofer = @cTipo_chofer, franco = @lFranco,
       id_vehiculo = @cId_vehiculo_new, interno = @nInterno_new,
       id_interno = @cId_interno_new, fletero = @cFletero_new
WHERE id_viaje = @nIdViaje;

-- la unidad NUEVA toma el viaje:
UPDATE vehiculo SET estado = 'ASIGNADO', hs_inicio = @dtHs_inicio, id_viaje = @nId_reserva
WHERE cronograma = @cId_interno_new;
-- la unidad VIEJA queda libre:
UPDATE vehiculo SET estado = 'LIBERADO', id_viaje = 0 WHERE cronograma = @cId_interno;

INSERT INTO viaje_log (id_viaje, usuario, motivo, hora, id_chofer,
                       interno_ori, interno_new, cronograma, cronograma_new)
VALUES (@nId_reserva, @cUsuario, 'RE-ASIGNO', GETDATE(), @cId_chofer_new,
        @nInterno, @nInterno_new, @cId_interno, @cId_interno_new);
-- gps_xlm(nIdViaje)
```

**Ruta**: mismos UPDATE por `id_viaje_int` (limpia también `vehiculo.id_viaje_int` de la
vieja) + log/gps por tramo. **Motivo de log = 'RE-ASIGNO'** (con interno y cronograma
viejo→nuevo en columnas dedicadas — en la réplica: `interno_or`, `interno_ne`, `cronograma`,
`cronogram2`).

> El UPDATE de `vehiculo` acá va por `WHERE cronograma = interno` (no por `id`) — en Buslink
> normalizar a la PK.

---

## 4. Libe (botón `bLiberar` → form `trafico_liberar`) — FINALIZAR el viaje

### 4.1 Validaciones toolbar

- La unidad no tiene viaje (`id_viaje = 0`) → error.
- Viaje CANCELADO / FINALIZADO → error.
- Unidad sin chofer → error; `estado = 'LIBERADO'` → error.
- **`estado = 'ASIGNADO'` → error "Ese vehículo no inició el viaje todavía"** — solo se
  libera una unidad EN CURSO (`hs_inicio` ya pasó).
- **Ruta**: solo se libera cerrando el ÚLTIMO tramo (`MAX(id_viaje)` de la ruta).

### 4.2 El form (cierre del servicio)

Campos: hora fin (+ duración calculada), pax real, voucher, odómetro fin / km recorrido,
zona donde queda la unidad (`zona_new`), chofer/chofer2 (editable en "cierre manual").
Sub-flujo **horas adicionales**: si la duración real excede la teórica → form
`trafico_liberar_hora_adicional` pide MOTIVO (obligatorio) y genera el adicional de horas
extra. También arma adicionales de **agua** (`parametro.adic_agua` × `viaje.agua`) y otros
del servicio.

### 4.3 Escritura (viaje simple)

```sql
UPDATE vehiculo SET estado = 'LIBERADO', id_viaje = 0, id_viaje_int = 0, id_zona = @cZona_New
WHERE id_viaje = @nIdViaje;

UPDATE viaje SET estado_viaje = 'FINALIZADO', hs_fin = @dtHora_fin, duracion = @cDuracion,
       pax = @nPax, voucher_nro = @nVoucher_nro, odometro = @nOdometro,
       odometro_fin = @nOdometroFin, km_recorrido = @nKmRecorrido
WHERE id_viaje = @nIdViaje;

INSERT INTO viaje_log (id_viaje, usuario, motivo, hora, id_chofer, interno_ori, interno_new, comentario)
VALUES (@nIdViaje, @cUsuario, 'FINALIZO', GETDATE(), @cId_chofer, @nInterno, @nInterno,
        @cMotivo_hora_adicional);
-- gps_xlm(nIdViaje)

-- adicionales generados en el cierre (agua, horas extra, stock):
INSERT INTO viaje_adicional (id_viaje, id_adicional, nombre, cantidad, precio)
VALUES (@nIdViaje, @id_adicional, @nombre, @cantidad, @precio);
```

**Modo "cierre manual"** (`lCierreManual`): el mismo UPDATE de viaje incluye además
`id_chofer, nombre_chofer, id_chofer2, tipo_chofer, id_vehiculo, interno` (el operador
corrige quién lo hizo realmente).

**Ruta**: `vehiculo` se libera por `id_viaje_int`; cada tramo intermedio se finaliza con
`hs_fin = 23:59` del día y `duracion = '23:59:00'`; el último tramo lleva la hora real.
Log + gps por tramo; los adicionales van al último tramo.

---

## 5. Frc (botón `franco` → forms `chofer_franco` / `chofer_franco_abm`)

Módulo de FRANCOS (días libres del chofer) — tabla `chofer_franco`
(id, id_chofer, codigo, motivo, fecha, valido, trabajo).

- **Lista** (`chofer_franco.scx`): grilla + Eliminar (permiso dígito `'4'`):
  `DELETE FROM chofer_franco WHERE id = @nId` — **baja FÍSICA** (sin f_delete).
- **Alta masiva** (`chofer_franco_abm.scx`): multi-select de choferes × array de días.
  Validaciones por chofer×día (bloquean TODO el lote si falla alguna):
  1. Ya tiene franco/permiso ese día (`chofer_franco` por `id_chofer + DTOS(fecha)`).
  2. **Trabajó ese día**: existe `viaje` con `str_f_reserva = día` y ese `id_chofer`.
  ```sql
  INSERT INTO chofer_franco (id_chofer, codigo, motivo, fecha)
  VALUES (@cId_chofer, @cCodigo, @cMotivo, @dControl)  -- solo si no existe ya
  ```
- **Franco trabajado / cambio de fecha**: se escribe desde la ASIGNACIÓN (ver §2.4).
- `chofer_franco_modifica.scx` y `chofer_franco_auditoria.scx` completan el módulo
  (modificación y auditoría) — extraer si se migran.

---

## 6. Resumen: tablas que escribe la toolbar (para `ViajeAbmService`)

| Operación | viaje | vehiculo | viaje_log (motivo) | Otras |
| --- | --- | --- | --- | --- |
| Chequeo | `chequeo+1` | — | CHEQUEO | — |
| Asignar (SIN/CON) | estado ASIGNADO + 10 campos | ASIGNADO + hs_inicio + id_viaje | ASIGNO | `vehiculo_km` (1er odómetro del mes), `chofer_franco` (franco trabajado), GPS |
| Reasignar | ídem + `chequeo=0` | nueva ASIGNADO / vieja LIBERADO | RE-ASIGNO (interno y cronograma ori→new) | GPS |
| Liberar (=Finalizar) | FINALIZADO + hs_fin/duracion/pax/voucher/odómetros/km | LIBERADO + id_viaje=0 + **id_zona nueva** | FINALIZO (comentario = motivo hs. extra) | `viaje_adicional` (agua/hs extra/stock, con precio), GPS |
| Francos | — | — | — | `chofer_franco` (INSERT masivo / DELETE físico / UPDATE FT) |

**Reglas transversales a replicar en Buslink:**
- Transacción única viaje+vehiculo+log (FoxPro no la tiene — mejora obligatoria).
- Anti-doble-asignación: releer con `UPDLOCK` dentro de la transacción.
- `gps_xlm()` en ASIGNO / RE-ASIGNO / FINALIZO / CANCELO — resolver la decisión GPS de Fase 0
  ANTES de codear estas operaciones.
- Rutas (`id_viaje_int > 0`): toda operación pega a todos los tramos y loguea por tramo.
- Los WHERE de escritura llevan SIEMPRE `f_reserva` además de `id_viaje` (no hay índice por
  `id_viaje` en la réplica).
