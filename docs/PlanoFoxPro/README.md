# Biblioteca de lógica FoxPro — índice maestro

> **Qué es esto:** un documento legible ("plano") por cada pantalla/módulo del Metrocar
> FoxPro original, extraído de los binarios `.scx/.sct` con la skill `foxpro-extract`.
> Es la fuente de verdad de QUÉ tiene que replicar cada pantalla Buslink (Blazor) — con
> sus tablas, validaciones, reglas de negocio y **trampas** (bugs heredados a NO copiar,
> columnas truncadas, campos que engañan).
>
> Organización: **una carpeta por módulo** (reorganizado 02/07/2026). Las skills de
> `.claude/skills/` son el CÓMO trabajar; estos docs son el QUÉ hace el sistema viejo.

## Mapa de carpetas

```text
PlanoFoxPro/
  trafico/             la operación diaria (despacho) — incluye la escritura del circuito
  reservas/            las 3 puertas de alta de viajes + informe banda horaria
  catalogos/           los ABMs de la Fase 1 del plan Buslink (cutover temprano + grupo B)
  facturacion/         liquidación a clientes/fleteros/choferes, tarifarios, cta. cte.
  combustible/         cargas de la flota, conciliación por lote, consumos
  vehiculos-choferes/  choferes (los otros 9 docs viven en la skill modulo-vehiculos-choferes)
  sistema/             usuarios, permisos, login
```

## Tráfico (`trafico/`)

| Doc | Form(s) FoxPro | Qué cubre | Migrado |
| --- | --- | --- | --- |
| `TRAFICO_ZOOM.md` | `trafico_zoom.scx` | Zoom del Viaje: máquina de estados, UPDATE de ~35 campos, cancelar, duplicar, sin asignar | Solo lectura ✅ · edición = Fase 3 |
| `TRAFICO2_FILTROS.md` | `trafico2.scx` | Filtros/combos de la planilla, vista Cxl, panel Buses, menú contextual, Aplicar Filtros | ✅ `PlanillaTrafico.razor` |
| `TRAFICO2_TOOLBAR.md` | `trafico2.scx` + `trafico_asigna/_reasigna/_liberar.scx` + `chofer_franco*.scx` | **La ESCRITURA del despacho** (spec de la Fase 3): chequeo, asignar, reasignar, finalizar, francos | Pendiente (Fase 3) |
| `TRAFICO_HISTORIAL.md` | `trafico_historial.scx` | Bitácora `viaje_log` + cabecera de auditoría | ✅ solo lectura |
| `GPS_XLM.md` | `procesos.prg` (función global) | La integración GPS (Fase 0.2): 2 vías (XML + SQL externo). **Hoy es no-op: flags apagados** | Decisión del dueño pendiente |
| `CABECERA_RECORRIDO.md` | `cabecera_recorrido.scx` + `_abm.scx` | Catálogo de cabeceras (código + 3 desc + recorrido largo). 187 filas. **Baja física** | ✅ solo lectura + andamiaje ABM |
| `CHOFER_FRANCO.md` | `chofer_franco.scx` + `_abm.scx` (alta masiva) + `_auditoria.scx` (informe) | Francos/licencias de choferes (71k filas). Auditoría = matriz chofer×día. **Baja física** | ✅ solo lectura + andamiaje ABM |
| `CHOFER_VIATICO.md` | `chofer_viatico.scx` + catálogos `_motivo`/`_liquida` | Viáticos de conductores (tablas VACÍAS, sin uso). **Baja física** | ✅ solo lectura + andamiaje ABM |
| `TRAFICO_VOUCHER_GUARDIA_CONTACTOS.md` | `trafico_voucher.scx` · `trafico_guardia*.scx` · `estacion*.scx` · `trafico_pasajero_planilla.scx` | Los 4 ítems restantes del menú: Voucher Recepción (auditoría + recepción sobre `viaje`), Guardia (`viaje_guardia`, baja física), Contactos y Proveedores (`estacion`/`estacion_rubro`, **compartido con Combustible**, baja física), Lista de pasajeros (buscador → reusa dialog) | ✅ solo lectura + andamiaje ABM (07/07/2026) |

## Reservas (`reservas/`)

| Doc | Form(s) FoxPro | Qué cubre | Migrado |
| --- | --- | --- | --- |
| `RESERVA_TRANSPORTACION.md` | `reserva_transportacion_con_adicional.scx` + 4 subdialogs | Alta manual: 14 validaciones, multiplicación días×servicios, modo ruta, grupos, Valor Especial (permiso F) | ✅ Solo lectura + andamiaje (07/07/2026) — `ReservasEspeciales.razor`; escritura en Fase 4/día D |
| `RESERVA_PLANTILLAS.md` | `reserva_plantilla_crear/_mantenimiento/_abm/_nombre/_armar.scx` | Ciclo de plantillas: crear, mantener, armar (generación masiva), cabecera 16 posiciones, lotes | ✅ Mantenimiento + Armado solo lectura + andamiaje (07/07/2026) — `PlantillasMantenimiento`/`ReservasPorPlantillas.razor`; escritura en Fase 4/día D |
| `IMPORTA_EXCEL_VIAJE.md` | `importa_excel_viaje.scx` | Importación masiva 28 columnas, 3 etapas de validación, transaccional | Pendiente (Fase 4, puerta 3 — candidata a descope) |
| `RESERVAS_INFORME_BANDA_HORARIA.md` | `trafico_resumen_horario*.scx` | Informe fecha × banda × vehículo | ✅ `ReservasBandaHoraria.razor` |
| `RESERVAS_INFORME_POR_CLIENTE.md` | `viaje_analisis.scx` (menú Utilitarios) | Informe cliente × mes × tipo (propio/contratado), solo `origen='T'`; modo cancelados (motivo 2) | ✅ `ReservasPorCliente.razor` |

## Catálogos (`catalogos/`) — los ABMs de la Fase 1

| Doc | Form(s) | Baja | Grupo del plan | Migrado |
| --- | --- | --- | --- | --- |
| `VIAJE_MOTIVO_CANCELA_ABM.md` | `viaje_motivo_cancela*.scx` + selector `trafico_motivo_cancela.scx` | lógica (`f_delete`) | **A — 1º ABM de Fase 1** | Pendiente |
| `FERIADO_ABM.md` | `feriado.scx` (+ `feriado_ver.scx`) | **física** | A — 2º (⚠️ hoy 0 feriados 2026 cargados) | Pendiente |
| `DESTINO_ABM.md` | `destino*.scx` | **física** (+ bug contacto a corregir) | A — 3º | Pendiente |
| `CLIENTE_OPERADOR_ABM.md` | `cliente_operador*.scx` | **física** (+ validar huérfanos) | A — 4º | Pendiente |
| `CLIENTE_ABM.md` | `cliente.scx` + `cliente_abm.scx` | lógica con fecha | A — 5º (el maestro grande) | Lista+ficha solo lectura ✅ |
| `VIAJE_MOTIVO_CAMBIO_ABM.md` | `viaje_motivo_cambio*.scx` | **física** (🐛 modifica roto en el fuente) | A (lo usa Reasignar, Fase 3) | Pendiente |
| `GUIA_ABM.md` | `guia.scx` + `guia_abm.scx` | **física** (pese a tener `f_delete`) | **B — cutover día D** (la escribe el circuito) | Pendiente |
| `CLIENTE_GRUPO_ABM.md` | `cliente_grupo*.scx` | cancelación masiva en cascada | **B — cutover día D** | Pendiente |

## Facturación (`facturacion/`)

| Doc | Cubre | Migrado |
| --- | --- | --- |
| `FACTURACION_LIQUIDACION.md` | El módulo entero: valorización (`arma_servicio`), cascadas de precios, Graba, Revertir, tarifarios, liquidación a fleteros/choferes, cta. cte. (sin uso) | Motor de tarifas ✅ (solo lectura, validado 99,4%) · Graba = Fase 5 |

## Combustible (`combustible/`)

| Doc | Cubre | Migrado |
| --- | --- | --- |
| `COMBUSTIBLE.md` | Las 2 eras (tabla viva `vehiculo_sobre` ✅ ya replicada en el server nuevo), conciliación por lote, consumos l/100km, saldos de estaciones (sin uso desde 2017), catálogo de proveedores | Pendiente (post día D) |

## Vehículos y Choferes (`vehiculos-choferes/`)

| Doc | Cubre | Migrado |
| --- | --- | --- |
| `CHOFER_ABM.md` | ABM de Conductores: ficha 5 pestañas, mapa de columnas truncadas, `vehiculo_chofer` | Lista+ficha solo lectura ✅ |
| `VIAJES_POR_CHOFER.md` | Informe (menú Utilitarios) chofer × día: viajes, turismo/cabecera, km, francos; solo PROPIO | ✅ `ViajesPorChofer.razor` |
| `KM_UNIDADES_VS_SERVICIOS.md` | Informe (menú Utilitarios) por unidad: km servicio vs odómetro, km vacío, % vacío; campos cruzados en réplica + bug % corregido | ✅ `KmUnidadesServicios.razor` |
| `ODOMETROS.md` | Control de Odómetros (`vehiculo_km.scx`): lecturas de km por dominio/mes, filtro por vehículo/todos + rango; km recorridos = km_fin−km_inicio | ✅ `Odometros.razor` (solo lectura) |
| `SINIESTROS.md` | Partes de accidente (`siniestro.scx`+`siniestro_abm.scx`): ~70 campos en 5 solapas; trampa id_vehicul (NORTUR) vs dominio (tercero) + columnas truncadas | ✅ `Siniestros.razor` + dialog (solo lectura) |

> Los otros 9 docs por pantalla del módulo (VEHICULOS, FLETEROS, ODOMETROS, SINIESTROS,
> APERCIBIMIENTOS, CAPACITACIONES, AGENDA_VENCIMIENTOS, TIPO_VEHICULOS, CHOFERES) viven en
> `.claude/skills/modulo-vehiculos-choferes/references/` (decisión 02/07/2026: se quedan ahí).

## Sistema (`sistema/`)

| Doc | Cubre | Migrado |
| --- | --- | --- |
| `USUARIO_ACCESOS.md` | Permisos: `acceso` (16 letras), `nivel` (dígitos ABM), `operador`, flujo de login | ✅ ABM Usuarios (primer ABM de escritura, 01/07/2026) |

---

## Convenciones de estos docs

1. **Encabezado**: ruta de menú FoxPro + forms que documenta + fecha de extracción + volumen de datos.
2. **Secciones típicas**: Concepto → Lista → ABM (validaciones + SQL de alta/baja/modifica) → Reglas no obvias.
3. **Nombres de campo**: se citan los del DBF FoxPro; la réplica SQL **trunca a 10 chars**.
   Cada doc lista sus truncados; ante la duda, SIEMPRE verificar contra `sys.columns`.
4. **Bugs heredados** se documentan con 🐛/⚠️ y la instrucción explícita de NO copiarlos.
5. Los dumps de `scx_dump.py` son temporales (van al scratchpad, no al repo) — lo que se
   conserva es el doc destilado. Regenerarlos tarda < 1 seg.

## Pendientes de extraer (cuando el plan los pida)

- `chofer_franco_modifica.scx` / `chofer_franco_auditoria.scx` (módulo francos completo, Fase 3.8).
- `trafico_liberar_hora_adicional.scx` (subdialog de horas extra del cierre, Fase 3.5).
- `viaje_motivo_tarde*.scx` (Motivos de Llegadas Tardes — mismo patrón que motivo_cambio).
- `vehiculo_tipo` ABM, `zona`, `servicio`, `iata` (catálogos de ABM del sistema, post Fase 1).
