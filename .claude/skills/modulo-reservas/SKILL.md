---
name: modulo-reservas
description: Conocimiento del módulo Reservas de Metrocar — alta manual de reservas (Reservas Especiales), plantillas (crear/mantener/armar), grupos de clientes, clientes, operadores, destinos, importación desde Excel e informes de banda horaria. Usar SIEMPRE que se trabaje con la carga/edición de reservas, tablas viaje (origen T/P), reserva_plantilla, cliente, cliente_grupo, cliente_operador, destino, guia, adicional, cabeceras de 16 posiciones, lotes, o cuando se construyan los futuros ABMs del módulo Reservas en Blazor. Mapa de tablas, flujos de escritura, validaciones, forms FoxPro y docs detallados por pantalla.
---

# Módulo Reservas — mapa de conocimiento

Reservas es donde **nacen los viajes**: todo lo que Tráfico despacha entró por una de las
3 puertas de este módulo. La regla de oro del negocio:

```
PUERTA 1: Reservas Especiales (manual)       → viaje.origen = 'T'   (~81K, 14%)
PUERTA 2: Reservas por Plantillas (masiva)   → viaje.origen = 'P'   (~440K, 86%)
PUERTA 3: Importa Excel (masiva puntual)     → viaje.origen = 'T' + lote
```

Las tres insertan con `estado_viaje = 'SIN ASIGNAR'` y `cronograma = cronogram2 = 'S/C'`
(o el de la plantilla); después Tráfico asigna unidad/chofer (skill `modulo-trafico`).

## Documentación detallada por pantalla (leer ANTES de codear cada ABM)

| Doc en `docs/logica-foxpro/` | Cubre | Form FoxPro |
| --- | --- | --- |
| `RESERVA_TRANSPORTACION.md` | alta manual: validaciones, multiplicación días×servicios, varios días (ruta), grupos, guías, valor especial (permiso "F"), adicionales, INSERT completo | `reserva_transportacion_con_adicional.scx` + 4 subdialogs |
| `RESERVA_PLANTILLAS.md` | ciclo completo de plantillas: crear, mantener (ABM/renombrar/duplicar), **armar** (generación por días+feriados+lote), cabecera de 16 posiciones, E/S | `reserva_plantilla_crear/_mantenimiento/_mantenimiento_abm/_mantenimiento_nombre/_armar.scx` |
| `CLIENTE_ABM.md` | catálogo cliente: CUIT, precios (ob_precio/lista/tarifa), flags operativos (pide_pax, voucher, bus24, GPS), rubros excluidos, baja lógica con fecha | `cliente.scx` + `cliente_abm.scx` |
| `CLIENTE_GRUPO_ABM.md` | grupos: candado f_grupo_fc, baja = cancelación masiva con motivo, renombre/cambio de cliente con arrastre a viaje | `cliente_grupo.scx` + `cliente_grupo_abm.scx` |
| `CLIENTE_OPERADOR_ABM.md` | operadores por cliente (baja física, id global) | `cliente_operador*.scx` |
| `DESTINO_ABM.md` | destinos: autocomplete, mas100km, distrito, bug del contacto | `destino*.scx` |
| `IMPORTA_EXCEL_VIAJE.md` | importación: 28 columnas, 3 etapas de validación, transacción, adicionales INLINE | `importa_excel_viaje.scx` |
| `RESERVAS_INFORME_BANDA_HORARIA.md` | Informe 2 (pendiente en Blazor): conteo por fecha×banda×vehículo, SQL listo | `trafico_resumen_horario*.scx` |

## Tablas del módulo (nombres SQL reales — truncados a 10 chars)

| Tabla | Rol | Columnas trampa (truncadas) |
| --- | --- | --- |
| `viaje` | una reserva/servicio | `str_f_rese` (YYYYMMDD char), `hs_present`, `hs_s_inici`, `hs_fin_apr`, `estado_via`, `id_servici`/`id_servic2`/`id_servic3`, `id_vehicu2` (=tipo), `id_vehicul` (=unidad), `nombre_cli`, `nombre_gui`, `id_operado`, `f_grupo_fi`, `d_destino_` (provincia), `moneda_con`, `importe_co`, `descuento_`, `sin_cargo_` (pago), `moneda_pag`, `importe_pa`, `voucher_nr`, `id_plantil`, `id_viaje_i`, `hs_ini_rut`/`hs_fin_rut`, `recorrido_`, `cronogram2`, slots `adi_*_1..5` inline |
| `reserva_plantilla` | filas de plantilla; `id_reserva` = NOMBRE (clave de agrupación) | `id_servici`, `id_vehicul` (=tipo acá), `nombre_gui`, `d_destino_`, `empresa_de`, `recorrido_`, `dia_siguie` |
| `cliente` | catálogo (413 activos) | `razon_soci`, `domicilio_`/`domicilio2`/`domicilio3` (nro/piso/dpto), `id_lista_p`, `envia_gps_`/`envia_gps2`, `fc_prefere`, `ob_precio` |
| `cliente_grupo` | grupos (11K); candado `f_grupo_fc` | `f_grupo_fi`, `f_grupo_in` |
| `cliente_operador` | operadores (128) — baja física | `id_operado` |
| `cliente_adicional_excluido` | rubros que no se facturan al cliente | — |
| `destino` (398) / `destino_localidad` | lugares + autocomplete — baja física | — |
| `guia` (1.141) | guías; alta automática desde la reserva | `registro_n`/`registro_v` |
| `adicional` (27) / `adicional_rubro` | catálogo de adicionales | `id_adicion` |
| `viaje_adicional` | adicionales por viaje (manual y plantilla; **Excel va inline**) | `id_adicion` |
| `viaje_log` | auditoría (ALTA en manual y plantilla; **Excel no loguea**) | `interno_or`/`interno_ne` |
| `viaje_horario` | 6 bandas horarias del informe | `dhorario`/`hhorario` char(5) |
| `feriado` (15) | excluye/incluye generación de plantillas | — |
| `parametro` | contadores globales: `lote_plant` (plantillas+Excel), `id_viaje_int` (ruta) | `lote_plant` |
| `iata` (106) / `cabecera` / `cronograma` / `empresa` / `moneda_tipo` / `responsable_tipo` / `lista_precio_modelo` / `lista_precio` / `cliente_tarifa` / `vehiculo_tipo` | catálogos satélite | `lista_precio`: `id_lista_p`, `f_vigencia`/`f_vigenci2` |

## Conceptos clave del módulo

- **Grupo**: agrupa viajes de un cliente para facturarlos juntos. Clave desnormalizada
  `(viaje.id_cliente, viaje.grupo)` + moderna `viaje.id_grupo`. `f_grupo_fc` con valor =
  grupo CERRADO (no acepta más servicios, no se renombra). Extender la fecha fin desde la
  carga arrastra TODOS los viajes del grupo.
- **Cabecera (16 posiciones)**: código posicional del recorrido en plantillas —
  cliente(2) + HHMM(4) + E/S(1) + IATA ida(3) + IATA vuelta(3) + recorrido(1) +
  refuerzo(1) + rango vehículo(1). La posición 7 (E/S) decide los destinos reales al
  generar (check "nombre de planta").
- **Lote** (`viaje.lote`): identifica cada corrida masiva (plantillas e importa Excel
  comparten el contador `parametro.lote_plant`). Permite deshacer corridas
  (`reserva_plantilla_elimina_viaje.scx`).
- **Multiplicación del alta manual**: días (hasta `f_fin`) × cantidad de servicios.
  "Varios días" = modo ruta: 1 fila por día compartiendo `id_viaje_i`.
- **Guías**: `guia_dueno` `'N'` (nuestra, con id) / `'C'` (del cliente, texto) / `'S'` (sin).
  En alta manual `nombre_gui` guarda "NOMBRE : TELEFONO" y el alta de guía a la tabla es
  automática.
- **Permisos**: ABMs estándar por dígitos en `cNivel` (2=alta, 3=modifica, 4=baja) +
  letra **"F"** en `acceso` para Valor Especial (precios convenidos). Detalle: skill
  `seguridad-nortur`. Ojo: varios botones FoxPro NO chequean permiso (documentado por
  pantalla) — en Blazor aplicar el esquema completo igual.

## Borrado: tres políticas distintas (no asumir)

| Política | Tablas |
| --- | --- |
| Lógica con fecha editable (`f_delete`) | `cliente` (re-habilitable), `servicio`, `vehiculo_tipo`, `guia`, `moneda_tipo`, `lista_precio_modelo` |
| **Física** (DELETE) | `destino`, `cliente_operador`, `reserva_plantilla`, `cliente_adicional_excluido`, `viaje_horario` |
| Cancelación con motivo (nunca DELETE) | `viaje` (estado CANCELADO + `id_motivo`), grupos (cascada documentada en `CLIENTE_GRUPO_ABM.md`) |

## Qué ya está migrado en Blazor (NO rehacer)

- **Informe 1** Reservas por fecha y servicio: `ReservasFechaServicio.razor`.
- **Informe 2** banda horaria: página `ReservasBandaHoraria.razor` (en curso — la lógica
  FoxPro exacta está en `RESERVAS_INFORME_BANDA_HORARIA.md` con el SQL listo).
- Lectura de viajes (Planilla de Tráfico, Zoom) — skill `modulo-trafico`.

## Qué falta (los ABMs futuros — en orden sugerido de riesgo creciente)

1. **Destinos** (catálogo simple, sin cascadas — ideal primer ABM del módulo)
2. **Operadores** (simple; agregar validación de huérfanos que FoxPro no tiene)
3. **Clientes** (mediano: muchos campos + flags; sin cascadas peligrosas)
4. **Grupos** (delicado: cancela viajes en cascada)
5. **Plantillas** crear/mantener (mediano) y **Armar plantilla** (generador masivo)
6. **Reserva de Transportación** (el más grande: el form completo + subdialogs)
7. **Importa Excel** (reemplazable por upload + validador en Blazor)

**Antes de construir CUALQUIERA**: leer skill `abm-metrocar` (regla SQL-dueño tabla por
tabla — mientras la réplica DBF→SQL siga activa, estas tablas son SOLO LECTURA desde
Blazor) + el doc `docs/logica-foxpro/` de la pantalla.

## Reglas de oro al escribir (cuando llegue el cutover)

1. `str_f_rese` SIEMPRE sincronizado con `f_reserva` (YYYYMMDD char — los informes viejos
   filtran por él).
2. Estado inicial único: `SIN ASIGNAR`. `viaje_log` con motivo "ALTA" en cada inserción.
3. Las altas masivas en FoxPro NO son transaccionales (salvo Excel) — en Blazor SIEMPRE
   transacción.
4. Adicionales: 2 representaciones (tabla `viaje_adicional` Y slots inline `adi_*` en
   `viaje`) — al leer, contemplar ambas; al escribir, definir UNA (recomendado: tabla).
5. Horas como char "HH:MM" comparadas como string (bandas, plantillas) — respetar para
   reproducir resultados.
6. Fechas vacías FoxPro pueden venir NULL o 1899/1900 en SQL — usar el rango válido de
   `ReportService` y `EMPTY()` ≈ `IS NULL OR < '1990-01-01'`.
7. Bugs heredados documentados (no copiarlos): contacto que no se actualiza en destino,
   rubros excluidos operando sobre el cliente equivocado en alta, mensaje engañoso del
   CUIT, INSERT de plantilla que omite campos que el UPDATE sí graba.

## Forms FoxPro del módulo (en `C:\MetroCarSys\Forms\`)

`reserva_transportacion_con_adicional.scx` (+`_abm`, `_adicional`, `_valor`,
`_cantidad_servicio`), `reserva_plantilla_armar/_crear/_mantenimiento/_mantenimiento_abm/
_mantenimiento_nombre/_repara/_elimina_viaje.scx`, `cliente.scx` + `cliente_abm.scx` +
`cliente_abm_email.scx` + `cliente_busca.scx`, `cliente_operador*.scx`,
`cliente_grupo*.scx`, `destino*.scx`, `importa_excel_viaje.scx`,
`trafico_resumen_horario*.scx`, `iata_busca.scx`, `feriado*.scx`, `calendario.scx`.
Dumps ya generados en `%TEMP%\reservas_dumps\` (12/06/2026) — regenerar con
`foxpro-extract` si hace falta releer.

> ⚠️ El fuente en disco está parcialmente desactualizado vs el exe productivo: botón
> "Lista Pax" y botón Google de destinos no existen en los .scx. Verificar con el usuario
> al migrar esas piezas.
