---
name: estado-buslink
description: >-
  Qué está migrado de FoxPro a Buslink (Metrocar Nortur), módulo por módulo: pantallas y rutas Blazor,
  métodos de ReportService, exports de Excel, flags de andamiaje ABM (AbmFeatureFlags) y las trampas ya
  resueltas de cada tabla. Usar al retomar un módulo, antes de migrar una pantalla nueva, o para saber si
  algo ya está hecho y con qué decisiones (Reservas, Tráfico, Facturación, Combustible, Vehículos y
  Choferes, informes analíticos, ABM de Usuarios).
---

# Estado de la migración Buslink — módulo por módulo

> Extraído de `CLAUDE.md` el 29/07/2026 para sacarlo del contexto siempre-cargado.
> El roadmap vigente sigue en `CLAUDE.md` § Pendiente / próximos y en `docs/buslink/PLAN_MIGRACION_BUSLINK.md`.

### ✅ Arquitectura Blazor + MudBlazor — HECHA

- Shell con navegación lateral CSS overlay (`MainLayout.razor`), tema NORTUR (`NorturTheme.cs`), autenticación con estado de sesión persistente por circuito SignalR.
- `KpiCard.razor` como componente reutilizable de tarjeta KPI.
- `ReportService` con caché en memoria (TTL 5 min).
- `ExcelExportService` con ClosedXML.
- **Skill local `blazor-nortur`** instalada en `.claude/skills/blazor-nortur/SKILL.md`.

### ✅ ApexCharts — INSTALADO Y CONFIGURADO

- `Blazor-ApexCharts 6.1.0` en el `.csproj`.
- Servicio registrado en `Program.cs`, JS cargado en `App.razor`.
- **Listo para usar en reportes nuevos** — agregar `@using ApexCharts` al inicio de cada `.razor` que lo use.

### ✅ Informe 1: "Reservas por fecha y servicio" — REARMADO (02/07/2026)

Componente `Components/Pages/ReservasFechaServicio.razor`. Rediseño completo validado
contra la base al dígito (smoke tests 15/15).

- **Barra de filtros horizontal compacta** (CSS `rfs-*` en `app.css`): período (datepickers
  en `PickerVariant.Dialog` + CSS `.mud-picker-paper.mud-dialog{min-width:310px}` porque si no
  el panel hereda el ancho del input=135px y recorta el header/días), servicios (multiselect
  con "Todos"), **estados** (multiselect de los 5 `estado_via`), switches **Internos** y
  **Cabeceras**, métrica (Reservas / Pax — cambia sin re-query, recálculo en memoria).
- **Cliente interno NORTUR excluido por defecto** (`parametro.id_cliente`), como todos los
  informes FoxPro — era ~6,8% de infle. Switch "Internos" para sumarlo.
- **Cabeceras excluidas por defecto:** `CABECERA_KM`/`CABECERA_SERV` NO son servicios reales,
  son **modos de facturación** (por km / por servicio; el destino real está en d_destino/
  h_destino). Eran ~90% del volumen y aplastaban el desglose por servicio. Se excluyen por
  defecto (switch "Cabeceras" para sumarlas), se sacan del dropdown de Servicios, y su volumen
  se muestra en un **KPI "Viajes cabecera"** aparte (transparencia). Constante
  `ReportService.ServiciosCabecera`; conteo `GetVolumenCabecerasAsync`.
- **KPIs** (fila flex `.rfs-kpis`, 4 o 5 tarjetas parejas): total reservas, total pax,
  canceladas (+% — muestra "—" si CANCELADO quedó fuera del filtro), servicios distintos, y
  "Viajes cabecera" (solo cuando están excluidas). Verificado por SQL: excluyendo cabeceras el
  período 02/05–02/07/2026 da 1.111 res / 31.010 pax / 24 serv; incluyéndolas 10.587 / 26.
- **Gráficos ApexCharts** (animaciones off — con animación, las capturas/vistas agarran el
  donut a medio dibujar): evolución diaria (área), barras top 10, donut top 8 + "Otros"
  (paleta categórica validada con dataviz; azul de serie `#2058D0`, el corporativo es
  demasiado oscuro para marcas de gráfico).
- **Tabla pivote** fecha × servicio: columna fecha fija + header/footer sticky, día de
  semana (finde en ámbar), ceros como `·`, fila TOTAL por columna, `<Virtualize>` en tbody.
- **Drill-down**: click en una **celda** o en el **total de fila** abre `ReservasFsDetalleDialog`
  (las reservas una por una, pill de estado; click en fila → Zoom del Viaje). El detalle se
  trae lazy 1 vez por filtro (`GetReservasFechaServicioDetalleAsync`).
- **Colores unificados por servicio (03/07/2026):** cada servicio tiene un color único, el
  MISMO en el gráfico de barras y en el donut (color por entidad, no por posición). Ver skill
  `blazor-nortur` § Colores unificados.
- **Cross-filter estilo Power BI (03/07/2026):** clic en un servicio (barra, porción del donut
  o **header/total de columna** de la tabla) enfoca todo el tablero en ese servicio — KPIs,
  evolución y tabla se recalculan en memoria (sin re-query) y los gráficos resaltan el servicio
  atenuando el resto. Chip "Filtrado por: X ✕" para quitar; reclic togglea. Ver skill
  `blazor-nortur` § Cross-filter. Sin librerías nuevas (ApexCharts `OnDataPointSelection`).
- **Switch "Cabeceras" eliminado (03/07/2026):** las cabeceras se excluyen SIEMPRE del desglose
  (comportamiento fijo, ya no configurable); el KPI "Viajes cabecera" sigue mostrando su volumen.
- **Excel** (ClosedXML): hojas Detalle + Pivote + Ranking + **Reservas** (una por una).

> **Lógica FoxPro (trampa):** el informe original del EXE productivo se llama "Reservas por
> Fecha en estado **SIN ASIGNAR O ASIGNADO**" — es demanda pendiente, NO histórico. Su form
> no existe en el fuente en disco (el ítem tampoco está en `MENU_PRINCIPAL.MPR`; ese popup
> solo tiene banda horaria). Para reproducir su número en Blazor: Estados = SIN ASIGNAR +
> ASIGNADO.

### ✅ Informe 2: "Reservas por banda horaria" — HECHO (+cross-filter 3D y vista por hora, 30/07/2026)

`Components/Pages/ReservasBandaHoraria.razor` (`/reservas-banda-horaria`), permiso `'R'`.
Réplica mejorada de `trafico_resumen_horario.scx`: viajes por franja horaria de inicio (las
6 bandas de `viaje_horario`, clasificadas por `CAST(hs_inicio AS TIME)` en el service).
Filtros período / **tipo de servicio** / tipo de vehículo / estados (default sin CANCELADO, fiel
al FoxPro), métrica Viajes↔Pax sin re-query, KPIs, barras apiladas fecha×banda, distribución por
banda/hora, por tipo de servicio y por tipo de vehículo, tabla pivote con drill-down (reusa
`ReservasFsDetalleDialog` + Zoom) y Excel de 5 hojas.

**🔴 Cambio de alcance (30/07/2026, pedido del frente final).** El informe traía fijos
`origen='T'` + exclusión del cliente interno, o sea **SOLO Turismo: el 7% de la operación**
(280 viajes contra 3.946 de Empresa en jul/2026). Ahora hay filtro **"Tipo de servicio"** con 3
categorías tildadas por defecto — **Empresa** (`origen='P'`, transporte de personal), **Turismo**
(`origen='T'`) e **Interno** (`id_cliente = parametro.id_cliente`, hoy 'NORTUR'; Interno gana
sobre origen). Destildando Empresa e Interno se recupera exacto el informe histórico.

**Vista por HORA además de por banda.** Selector "Agrupar por: Bandas (6) · Horas (24)": el
agregado trae banda **y** `DATEPART(hour, hs_inicio)` juntos, así que alternar es recálculo en
memoria sin re-query (las bandas NO están alineadas a la hora — 06:30-08:29 parte la hora 6 y la
8 — así que ninguna determina a la otra). Al Aplicar con **Desde = Hasta** el selector se
posiciona solo en Horas (el caso "capacidad operativa de un día"), salvo que el usuario ya lo
haya elegido a mano (`_agrupacionManual`). Las 24 horas usan una **rampa cíclica de color**
(ordinal, sigue el reloj), no la paleta categórica.

**⚠ Trampa SQL:** el CASE de categoría se repite en el `GROUP BY` y SQL Server **rechaza
subconsultas ahí** ("No se pueden usar agregados ni subconsultas en las expresiones de la lista
de agrupación"). Por eso el id del cliente interno se resuelve antes con
`ReportService.GetIdClienteInternoAsync()` (cacheado) y entra como literal.

**Cross-filter estilo Power BI con TRES dimensiones combinables** (banda/hora AND tipo de
servicio AND vehículo): clic en un segmento/porción/leyenda/columna enfoca todo el tablero; la
dimensión clickeada resalta (atenúa el resto) y las otras + KPIs + tabla se filtran, en memoria.
Chips "Banda: X ✕" / "Servicio: Y ✕" / "Vehículo: Z ✕" en los tres paneles; los drill-downs
respetan los focos. Cambiar de agrupación descarta el foco de columna ("08:30-14:00" no existe
como hora). Patrón y trampas: skill `blazor-nortur` § Cross-filter.

### ✅ Informe 3: "Reservas por cliente" — HECHO (03/07/2026)

`Components/Pages/ReservasPorCliente.razor` (`/reservas-por-cliente`), permiso `'R'`, menú
Reservas → Informes. Réplica mejorada de `viaje_analisis.scx` (**menú Utilitarios → Reservas
por Cliente** del FoxPro, cuya única salida era una tabla dinámica de Excel por OLE). Plano:
`docs/PlanoFoxPro/reservas/RESERVAS_INFORME_POR_CLIENTE.md`. Cuenta viajes de transportación
(`origen='T'` — sin cabeceras por diseño, son origen 'P') por **cliente × mes × tipo de
unidad** (PROPIO `interno<1000` / CONTRATADO `interno>=1000` / SIN REALIZAR `interno=0`).
Decisiones vs FoxPro (acordadas 03/07/2026): cliente interno NORTUR **excluido por defecto**
(~30% del volumen acá; switch Internos), modo cancelados como **filtro flexible** (toggle
Activas/Canceladas + multiselect de los 6 motivos, default "CANCELADO POR CLIENTE"=motivo 2,
SIEMPRE respetando el período — el FoxPro barría todo el histórico), métrica Viajes↔Pax.
Barras apiladas mes×tipo, top-N clientes (selector Mostrar), donut por tipo, pivote
cliente×mes con drill-down, Excel 4 hojas (incl. Viajes con motivo de cancelación) y
**cross-filter 2D cliente AND tipo**. Trampas de réplica: `id_motivo` e `interno` vienen
NULL donde el DBF tenía 0 → `ISNULL(...,0)`. Validado al dígito contra SQL (4.632/98/65 +
celda GATE1×03/2026=335); smoke test en la suite.

**+ Control de tendencia mes-a-mes (15/07/2026):** selector "Comparar" + switch "Variación" +
columna "Tendencia", todo en memoria sobre `_pivMap`. **Reemplazado el 30/07/2026** — ver abajo.

**🔄 REDISEÑO 30/07/2026 — las 4 correcciones de la clienta (Paula).** Plano actualizado con la
tabla punto por punto: `docs/PlanoFoxPro/reservas/RESERVAS_INFORME_POR_CLIENTE.md` § Rediseño.

- **🔴 La clasificación de unidad estaba MAL.** La regla FoxPro `interno >= 1000 → CONTRATADO`
  no mide lo que dice: los internos 3003-9999 son unidades PROPIAS de NORTUR y los fleteros
  reales (PANELLA, NUEVOS RUMBOS, TEB, MTL…) tienen interno < 1000 (2023: 741 contratados
  reales vs 1 que mostraba la regla). Ahora se clasifica leyendo `vehiculo` por
  `id_vehicu2` = dominio, con **selector de criterio** (`uso='CONTRATADO'` default vs
  `fletero <> 'NORTUR'`) porque la carga de la flota no es consistente entre los dos campos —
  la definición final la confirma la clienta. `ReportService.TipoUnidadCaseSql`.
- **Comparación de períodos rehecha:** selector **"Comparar con"** (Sin comparación / Mes
  anterior / Mismo mes del año anterior) que trae el **período base con una 2ª consulta** (antes
  se calculaba con los meses ya cargados y el interanual quedaba vacío). Impacta KPIs (badge
  Δ%), gráfico mensual (barras agrupadas período vs base) y tabla (columna "vs …" con base + Δ +
  % + sparkline). El rango base es el mismo rango corrido **día a día**.
- **El switch "Variación" se eliminó** (la clienta reportó que "no hacía nada": su efecto estaba
  3 pantallas más abajo). Es un selector **"Mostrar: Cantidad · Variación · Cantidad y
  variación" en la cabecera de la tabla**.
- **Modo Canceladas reconfigurado:** el **motivo reemplaza al tipo de unidad como 2ª dimensión**
  (el 100% de las canceladas está SIN REALIZAR, 5.948/5.948 en 2025-26). Apilado mes × motivo,
  donut por motivo, cross-filter por motivo, pivote con **Columnas: Por mes / Por motivo** (el
  cruce cliente × motivo del Metrocar) y KPIs de **tasa de cancelación** (2ª consulta a las
  activas para el denominador), motivo principal y cliente que más cancela. Default de motivos:
  **todos** (antes solo el 2, hardcode del FoxPro que escondía 371 "POR ERROR EN CARGA").
- **KPI "Contratados" clickeable** → `ComposicionTipoDialog` (nuevo): desglose por unidad
  (interno · dominio · dueño · uso) y por cliente, con drill-down a los viajes.
- **Filtro "Tipo de servicio"** (Empresa/Turismo/Interno) como en Banda Horaria — el informe
  tenía fijo `origen='T'`. **Solo Turismo tildado por defecto** → los números no se movieron.
- **Firmas nuevas:** `GetReservasPorClienteAsync/DetalleAsync(desde, hasta, categoriasSel,
  canceladas, motivosSel)`; DTOs con `TipoUso`/`TipoFletero`/`IdMotivo` + `TipoSegun(criterio)`;
  `ExcelExportService.ReservasPorCliente(filas, metrica, criterio, motivos, viajes, filasBase, baseLbl)`
  con hojas nuevas **Motivos de cancelación** y **Cliente x motivo**.
- **`KpiCard` extendido (global):** `Delta`/`DeltaClase` (badge de variación) y `OnClick`
  (tarjeta clickeable). Cambió la prioridad de recorte: ahora **la etiqueta no cede nunca** y el
  valor hace elipsis (con valores de texto largo la etiqueta se comía hasta "M…").
- **Validado 30/07/2026 (dos señales):** 01/02–30/07/2026 Turismo activas = 4.845 viajes /
  111.926 pax / 101 clientes (idéntico a SQL y al informe anterior); base interanual 4.308 →
  +537 (+12%); canceladas 1.304, tasa 21,2%, cliente × motivo 1.264+38+2. Suite **49/49**.

### ✅ Informes 4 y 5: "Viajes por chofer" y "Km Unidades vs Servicios" — HECHO (04/07/2026)

Los dos informes gemelos del menú **Utilitarios** del FoxPro (`viaje_analisis_chofer.scx` y
`viaje_analisis_km.scx`), migrados con el patrón dashboard completo. Menú **Vehículos y Choferes
→ Informes de Flota** (submenú nivel 3 nuevo), permiso `'V'`. Planos:
`docs/PlanoFoxPro/vehiculos-choferes/VIAJES_POR_CHOFER.md` y `KM_UNIDADES_VS_SERVICIOS.md`.

- **Viajes por chofer** (`ViajesPorChofer.razor`, `/viajes-por-chofer`): chofer × día con viajes,
  turismo (`origen='T'`) / cabecera (`origen='P'`), km, pax y **días de franco** (calculados en
  memoria como el FoxPro — día sin viajes entre el 1º y último día trabajado, se pintan "F" en
  ámbar). KPIs, evolución diaria (área turismo/cabecera), barras top-N, donut turismo/cabecera,
  pivote chofer×día con drill-down al Zoom, Excel (Resumen+Pivote con francos+Viajes),
  **cross-filter 1D por chofer**. Métrica Viajes↔Km↔Pax. Validado: jun 2026 = 97 chof / 1.951
  viajes / 125 tur / 1.826 cab / 55.379 km (idéntico a SQL).
- **Km Unidades vs Servicios** (`KmUnidadesServicios.razor`, `/km-unidades-servicios`): por unidad,
  km servicio (`SUM viaje.km`) vs km recorrido real (odómetro `vehiculo_km`), km vacío (recorrido
  − servicio), % vacío coloreado por eficiencia, días trabajados. KPIs, barras top-N, donut por
  tipo de vehículo, tabla por unidad con drill-down, Excel, **cross-filter 2D unidad AND tipo**.
  Validado: may 2026 = 203.522 km serv / 1.985.855 recorrido / 1.809.356 vacío (91%).
- **Decisiones vs FoxPro** (acordadas 04/07/2026): rango de fechas libre (el FoxPro filtraba un
  solo mes) y **switch "Incluir contratados"** (default solo PROPIO, fiel al FoxPro) + switch
  Internos. Default de Km = **dos meses atrás** (el odómetro se cierra con retraso; el mes en
  curso tiene `km_fin=0` → km vacío vacío).
- **🐛 Trampa CRÍTICA (Km):** en la réplica los campos de vehículo del viaje están **cruzados** —
  `viaje.id_vehicul` = TIPO (BUS/VAN/MINI), `viaje.id_vehicu2` = DOMINIO/patente. El odómetro
  une por `id_vehicu2`. **🐛 Bug heredado corregido:** el % vacío del FoxPro no protege contra
  odómetro incoherente (recorrido < servicio → % negativo gigante, -355.800% real); en Blazor
  esas unidades salen "—" (`recorrido >= km_servicio` en el chequeo `TieneOdometro`).

### ✅ Vistas de solo lectura migradas (lista + ficha) — HECHO

Réplicas fieles de pantallas FoxPro en **solo lectura** (lista + ficha modal, botonera de
ABM deshabilitada — la escritura sigue en FoxPro; estrategia strangler de `abm-metrocar`).
Mismo patrón y estilos CSS (`cli-*`, `zoom-*`) — calcar uno para hacer el siguiente.

| Entidad | Lista (página) | Ficha (dialog) | Doc FoxPro | Permiso | Menú |
| --- | --- | --- | --- | --- | --- |
| **Clientes** | `ClientesAbm.razor` (`/clientes-abm`) | `ClienteDetalleDialog.razor` | `CLIENTE_ABM.md` | `'F'` | Facturación → Clientes → ABM - Clientes |
| **Choferes** | `Choferes.razor` (`/choferes`) | `ChoferDetalleDialog.razor` (5 tabs) | `CHOFER_ABM.md` | `'V'` | Vehículos y Choferes → Choferes |
| **Vehículos** | `Vehiculos.razor` (`/vehiculos`) | `VehiculoDetalleDialog.razor` (6 tabs) | `skills/.../references/VEHICULOS.md` | `'V'` | Vehículos y Choferes → Vehículos - Flota |
| **Odómetros** | `Odometros.razor` (`/odometros`) | — (grilla + KPIs) | `PlanoFoxPro/vehiculos-choferes/ODOMETROS.md` | `'V'` | Vehículos y Choferes → Odómetros |
| **Siniestros** | `Siniestros.razor` (`/siniestros`) | `SiniestroDetalleDialog.razor` (5 solapas) | `PlanoFoxPro/vehiculos-choferes/SINIESTROS.md` | `'V'` | Vehículos y Choferes → Siniestros |
| **Agenda de Vencimientos** | `AgendaVencimientos.razor` (`/agenda-vencimientos`) | — (2 grillas + KPIs) | `PLANOFOXPRO/vehiculos-choferes/AGENDA_VENCIMIENTOS.md` | `'V'` | Vehículos y Choferes → Agenda de Vencimientos |
| **Fleteros** | `Fleteros.razor` (`/fleteros`) | `FleteroEditorDialog.razor` (4 modos, andamiaje) | `PLANOFOXPRO/vehiculos-choferes/FLETEROS.md` | `'V'` | Vehículos y Choferes → Fleteros |
| **Tipo de Vehículos** | `TiposVehiculo.razor` (`/tipos-vehiculo`) | `TipoVehiculoEditorDialog.razor` (4 modos, andamiaje) | `PLANOFOXPRO/vehiculos-choferes/TIPO_VEHICULOS.md` | `'V'` | Vehículos y Choferes → Tipo de Vehículos |

- **Choferes** (15/06/2026): grilla con filtro Fletero + búsqueda Nombre + Ver Egresados,
  columnas iguales al FoxPro, egresados en amarillo. Ficha con las 5 pestañas del FoxPro
  (Datos Personales, Domicilios, Teléfonos, Condiciones Laborales, Vehículos). Vencimientos
  de Registro/CNRT/AEP resaltados (rojo vencido / ámbar por vencer 30 días — valor agregado).
  Métodos `GetChoferesAsync` / `GetChoferDetalleAsync` en `ReportService`. Trampas resueltas:
  columnas truncadas a 10 chars (`registro_v/2/3/4`, `id_lista_p`, `real_domi*`, `entre_call`),
  `chofer_log` NO replicada, `vehiculo.id_vehicul`/`dominio` (no `id_vehiculo`/`patente`).
- **Vehículos - Flota** (15/06/2026): grilla con 15 columnas iguales al FoxPro + filtros
  Fletero / Ver Activos (arranca tildado) / Ver Flota Propia (`uso='PROPIO'`) + búsqueda por
  Dominio/Interno; egresados en amarillo (egresado = `!activo OR f_delete`). Ficha con las 6
  pestañas del FoxPro: Datos Vehículo, Permisos (`vehiculo_permiso`+`permiso`), Dueños
  (`vehiculo_dueno`+`dueno`, suma 100%), Cubiertas (columnas `r1..r7`, **no es tabla**),
  Tarjetas (YPF/ESSO en la propia `vehiculo`), Repuestos (`vehiculo_repuesto`, vacía).
  Vtos de Póliza/VTV/Matafuegos/Habilitación resaltados (rojo/ámbar). Métodos
  `GetVehiculosAsync` / `GetVehiculoDetalleAsync`. Trampas: orden visual de tabs ≠ nº de page,
  `tacografo_`=marca / `tacografo2`=nro, `gps_activo` nvarchar(1).
- **Odómetros** (04/07/2026): réplica de `vehiculo_km.scx` (Control de Odómetros). Grilla fiel
  (Dominio/Fecha/Año-Mes/Km Inicio/Fin/**Recorridos**/Interno/usuarios) + filtro *por vehículo*
  (autocomplete flota propia) / *todos* + rango de `f_carga`, + **KPIs** (Lecturas/Unidades/Km
  recorridos/Sin cierre) + Excel. Km Recorridos = `km_fin−km_inicio` (NULL si falta alguno o si
  daría negativo). Default = 2 meses atrás (el odómetro cierra con retraso; mes en curso
  `km_fin` NULL). Sin ficha (es un registro de lecturas). Métodos `GetOdometrosAsync` /
  `GetDominiosFlotaPropiaAsync`; export `ExcelExportService.Odometros`. ⚠️ La ASIGNACIÓN de
  Tráfico también escribe `vehiculo_km` → la tabla cambia de dueño el día D, no como catálogo
  suelto. Validado may–jul 2026: 203 lecturas / 106 unidades / 1.990.053 km.
- **Siniestros** (04/07/2026): réplica de `siniestro.scx`+`siniestro_abm.scx` (partes de
  accidente, ~70 campos). Lista (INNER JOIN a `chofer`) con "Buscar por" (=orden), filtro texto,
  filtro Tipo Acc. y Excel; ficha `SiniestroDetalleDialog` con **5 solapas** (El Hecho/asegurado ·
  Tercero · Propietario · Daños y descripción · Testigos). Métodos `GetSiniestrosAsync` /
  `GetSiniestroDetalleAsync`; export `ExcelExportService.Siniestros`. **🐛 Trampas:**
  `id_vehicul`=dominio del vehículo NORTUR (asegurado) vs `dominio`=dominio del **tercero**
  (`marca_y_mo` también del tercero); **sin `f_delete`** (solo `_deleted`, no hay egresados);
  ~30 columnas truncadas a 10 chars (`asegurado_dano`→`asegurado_`, `descripcion_acc`→
  `descripcio`, `test_N_nomb`→`test_N_nom`, etc.). Validado: 313 filas + fichas #6 y #12.
- **Agenda de Vencimientos + Fleteros + Tipo de Vehículos** (05/07/2026): los últimos 3 ítems del
  menú Vehículos y Choferes. **Agenda** (`AgendaVencimientos.razor`) = INFORME fiel de
  `agenda_vencimiento.scx`: 2 grillas (choferes registro/CNRT/AEP + vehículos propios VTV/matafuego)
  con celdas rojo/ámbar, KPIs y Excel; selector con modo **"según parámetros del sistema"** (fiel:
  chofer `aviso_cho`=30 / VTV `aviso_veh`=7 / mataf. `aviso_mat`=10, cada tipo su umbral) o umbral
  uniforme. Validado 249 choferes / 145 vehículos. **Fleteros** (`Fleteros.razor`) y **Tipo de
  Vehículos** (`TiposVehiculo.razor`) = catálogos en solo lectura **con andamiaje de ABM listo**:
  dialog editor multi-modo (`ver`/`alta`/`modifica`/`baja`, calca `UsuarioEditorDialog`) + escritura
  YA escrita en `AbmService` (`Alta/Modifica/BajaFleteroAsync`, `…TipoVehiculoAsync`) pero
  **deshabilitada** por `_abmActivo=false`. El día del corte a Buslink: `_abmActivo=true` + quitar
  `Disabled` de la botonera + bloquear FoxPro + apagar sync. **🐛 Trampas:** `fletero.id`/
  `vehiculo_tipo.id` NO son identity (alta `MAX(id)+1`); `parametro.aviso_*` son **bigint** →
  `CAST(... AS int)`; `GetFleterosAsync` ya existía (combo de Tráfico, devuelve `List<string>`) → el
  método de la grilla es `GetFleterosListaAsync`. Fleteros es catálogo **compartido con Facturación**
  (coordinar dueño único al activar el ABM). Planos: `docs/PLANOFOXPRO/vehiculos-choferes/`.

### ✅ Módulo Facturación — vistas de solo lectura (18/06/2026)

Migrado el submenú **Facturación → Resumen de Liquidaciones**, **Liquidación a Clientes** y
**Liquidaciones estimadas** (skill `modulo-facturacion-liquidacion`). Permiso `'F'`.
Tablas con dueño FoxPro → solo lectura.

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Resumen de Liquidaciones** | `ResumenLiquidaciones.razor` (`/resumen-liquidaciones`) | Réplica fiel de `liquidacion_cliente.scx`: maestro-detalle. Filtros Nº/Tipo (CLIENTE/PROVEEDOR)/rango fecha/cliente. Grilla cabeceras (`liquidacion` ⨝ `cliente`\|`fletero`) con columnas calculadas (Subtotal=ROUND((subtotal+extra)·t_cambio), Exento=adicional, TotalGral=+iva+adicional, Factura=tcp-lcp-ncp) + grilla detalle (`liquidacion_detalle`) al seleccionar. Revertir deshabilitado; **Factura abre comprobante en solo lectura** (`LiquidacionComprobanteDialog.razor`); Excel = cabeceras+detalle. |
| **Liquidación a Clientes** | `LiquidacionClientes.razor` (`/liquidacion-clientes`) | Réplica read-only de `facturacion_cliente_nueva.scx` (el form núcleo). Toolbar "Estado de las reservas" (combo + 2 fechas + botón `....`) y **árbol cliente→grupo** con las 2 cajas azules. **Rehecho 20/06/2026:** el árbol sale de **viajes pendientes de liquidar** (no de liquidaciones grabadas) — `GetViajesPendientesLiquidarAsync`. **POR ESTADO** (default, el más usado): `estado_via='FINALIZADO' AND f_grupo_fi < HOY`, ignora fechas → cajas Desde/Hasta **deshabilitadas**; **POR FECHA**: `f_grupo_fi BETWEEN`. Excluye el cliente de prueba `parametro.id_cliente` (=**NORTUR**). **4 solapas:** **Servicios** (viajes del grupo **valorizados en vivo** por el motor de tarifas — columna Importe por viaje + subtotal, badge S/TARIFA si falta precio; **click en una fila abre el Zoom del Viaje** reusando `ZoomViajeDialog`); **Adicionales** (`GetAdicionalesGrupoAsync` — **valorizados** contra `adicional_lista_precio` por adicional×tipo vehículo×vigencia, idéntico al FoxPro; estado ABONA/EXCLUIDO por `cliente_adicional_excluido`; badge S/TARIFA si falta precio); **Cliente** (ficha + grilla "Rubro de adicionales excluidos"); **Liquidacion** (**totales calculados en vivo** — cajas idénticas al FoxPro: Subtotal/Extras/Desc/Incr/Total/Cambio/IVA/Exento/Total Liquidación). Botonera de escritura (Graba) deshabilitada — solo lectura. |
| **Liquidaciones estimadas** | `FacturacionEstimada.razor` (`/facturacion-estimada`) | Proyección de venta por mes/cliente agregando `liquidacion_detalle` ya grabado (no re-valoriza viaje por viaje; el motor de tarifas ya existe —`ValorizarGrupoAsync`— pero esta vista usa lo liquidado por ser más rápido para visualizar tendencias). KPIs + gráfico mensual ApexCharts + tabla mes / tabla cliente + Excel. |

Métodos `ReportService`: `GetViajesPendientesLiquidarAsync` (árbol de Liquidación a
Clientes POR ESTADO/FECHA), `GetAdicionalesGrupoAsync` (solapa Adicionales valorizada),
`ValorizarGrupoAsync` (**motor de servicios `arma_servicio`** — precio por viaje) y
`CalcularTotalesLiquidacionAsync` (**totales solapa Liquidación `arma_liquidacion`**),
`GetLiquidacionesAsync`, `GetLiquidacionDetalleAsync`,
`GetLiquidacionCabeceraAsync` (cabecera cruda), `GetFacturacionEstimadaPorMesAsync`,
`GetFacturacionEstimadaPorClienteAsync`. Export:
`ExcelExportService.ResumenLiquidaciones` / `FacturacionEstimada`. CSS propio de
Liquidación a Clientes: clases `fcn-*` en `app.css`. **Valorización de adicionales
(20/06/2026):** la solapa Adicionales SÍ muestra precio/total reales — el tarifario
`adicional_lista_precio` está completo y vigente; se busca por adicional × `viaje.id_vehicul`
(=TIPO de vehículo: BUS/MINI) × fecha del viaje (`OUTER APPLY ... TOP 1 ORDER BY fdesdevg DESC`).
Verificado idéntico al FoxPro (GATE1/SAM-02: total 242.400). **Motor de SERVICIOS migrado
en vivo (22/06/2026):** `ValorizarGrupoAsync` + `CalcularTotalesLiquidacionAsync` replican
la cascada `arma_servicio`/`arma_liquidacion` (convenido→sin cargo→cabecera→servicio modo
S/H/K + horas extra + desc/incr) — **cálculo en vivo de solo lectura, NO graba** (strangler).
Validado al peso: 99,4% de 8.656 viajes históricos + grupo #2890197 (142807.34 / 38057.59 /
180864.93 exactos). Trampas finas en `docs/PlanoFoxPro/facturacion/FACTURACION_LIQUIDACION.md` §3.2 y
skill `modulo-facturacion-liquidacion` (bug minutos modo H, fracción 25, precio propio de
adicional que pisa la tarifa, tarifa retroactiva). Falta solo el **Graba** (escritura
transaccional + puente inverso). **Trampas resueltas:**
`cliente` NO tiene columna `nombre` (solo `razon_soci`); retenciones `retencion_`=IVA,
`retencion2`=IIBB, `retencion3`=SUSS (verificado contra el form); `total` viene NULL → se
recalcula siempre; mes con `CONVERT(char(7), fecha, 120)` (SQL 2012-friendly);
**`bGraba` guarda `liquidacion.subtotal`=total NETO de servicios y `liquidacion.extra`=ajuste
global manual** (no el desglose); **`viaje.id_viaje` y `viaje.pax` son `int` (no `bigint`)** →
`GetInt64` tira `InvalidCastException`, usar `CAST(... AS bigint)` en el SELECT.

### ✅ Módulo Tráfico — Cabeceras · Francos · Viáticos (05/07/2026)

Migrados los 3 ítems del menú **Tráfico** que faltaban (Cabeceras - Recorridos, Francos y
Viáticos, con sus submenús) en **solo lectura + andamiaje ABM** (patrón Fleteros/TipoVehiculo).
Permiso `'T'`. Planos: `docs/PlanoFoxPro/trafico/CABECERA_RECORRIDO.md`, `CHOFER_FRANCO.md`,
`CHOFER_VIATICO.md`. Los 7 ítems del menú ya son links reales (antes eran placeholders `--soon`).

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Cabeceras - Recorridos** | `CabecerasRecorridos.razor` (`/cabeceras-recorridos`) | Catálogo `cabecera` (código + 3 desc + recorrido largo), 187 filas. Ficha `CabeceraEditorDialog` (recorrido = editbox largo con wrap). |
| **Mantenimiento de Francos** | `Francos.razor` (`/francos`) | Grilla `chofer_franco` ⨝ `chofer` (71k filas → Virtualize + rango fecha obligatorio + combo motivo). |
| **Ingreso de Francos** | `FrancosIngreso.razor` (`/francos-ingreso`) + `FrancoAltaDialog` | Alta MASIVA: multiselect choferes × rango fechas × motivo (saltea duplicados). |
| **Auditoría Francos** | `FrancosAuditoria.razor` (`/francos-auditoria`) | **INFORME**: matriz chofer×día del mes (trb/franco/DUP), KPIs, Excel. Cruza `viaje` (titular + `id_chofer2`) vs francos. |
| **Viáticos** | `Viaticos.razor` (`/viaticos`) + `ViaticoEditorDialog` | Grilla `chofer_viatico` (4 JOINs), KPI total importe. Tabla VACÍA (sin uso). |
| **Motivo / Forma Liquidación** | `ViaticosMotivo.razor` / `ViaticosFormaLiquidacion.razor` | Catálogos id+nombre (VACÍOS) + `CatalogoSimpleEditorDialog` (un dialog parametrizado para ambos). |

Métodos `ReportService`: `GetCabecerasAsync`, `GetFrancosAsync`, `GetFrancoMotivosAsync`,
`GetFrancoAuditoriaAsync`, `GetViaticosAsync`, `GetViaticoMotivosAsync`, `GetViaticoLiquidaAsync`,
`GetChoferesComboAsync`. Escritura andamiaje en `AbmService` (Alta/Modifica/Baja de Cabecera,
alta masiva `AltaFrancosAsync` + `BajaFrancoAsync`, Viático, catálogos). Flags nuevos en
`AbmFeatureFlags`: `CabecerasAbmActivo`, `FrancosAbmActivo`, `ViaticosAbmActivo`,
`ViaticoCatalogosAbmActivo` (**todos `false`**). Export en `ExcelExportService`: `Cabeceras`,
`Francos`, `FrancosAuditoria`, `Viaticos`.

**🐛 Trampas clave (distintas al resto del proyecto):**
- **Baja FÍSICA** (`DELETE`), no lógica — las 5 tablas (`cabecera`, `chofer_franco`,
  `chofer_viatico`, `chofer_viatico_motivo`, `chofer_viatico_liquida`) **no tienen `f_delete`/
  `f_create`**. El andamiaje ABM refleja DELETE físico (a diferencia de `vehiculo_tipo`).
- **⚠️ Las 5 tablas están en el server VIEJO** (`DESKTOP-CV6LF0O`, el que apunta hoy la app)
  **pero NO en el nuevo** (`172.25.69.217`) → **replicarlas/crearlas allá antes del día D**.
- `chofer_franco`: 71k filas + fechas corruptas (`9201-03-03`) → Virtualize + `ClampFecha`.
- Auditoría: réplica usa `estado_via`, `tipo_chofe` (truncado), `id_chofer2`. Validado jun 2026
  = 98 choferes (idéntico a SQL). Cabeceras = 187 (idéntico). Viáticos/catálogos = 0 (vacíos).
- Pendiente menor: `chofer_franco_modifica.scx` (modifica de un franco puntual) — TODO.

### ✅ Módulo Tráfico — Menú contextual del panel BUSES (04/08/2026)

Migrado el **clic derecho sobre la grilla de Buses** (`Grid2.RightClick → menu_viaje_vehiculo.mpr`):
**16 acciones + submenú "Ver Datos Extras" de 4**, todo en **solo lectura + andamiaje**.
Es un menú DISTINTO al de la grilla de viajes: opera sobre `vehiculo`, no sobre `viaje`.
Plano: `docs/PlanoFoxPro/trafico/TRAFICO_BUSES_MENU.md`. Detalle ítem por ítem: skill `modulo-trafico`.

- **Infra**: `PanelBusRow` ampliado con `Id`, `NombreChofer`, `IdViajeInt`, `Uso`, `Cronograma`
  (el FoxPro los tiene gratis porque su cursor es `SELECT vehiculo.*`). Segundo `MudMenu`
  (`_menuBus`/`_busCtx`) con el mismo mecanismo `PositionAtCursor` del menú de viajes.
- **Diálogos nuevos**: `TarjetasUnidadDialog`, `AdicionalStockUnidadDialog`,
  `OrdenesTrabajoUnidadDialog`, `LogoneoDialog` (4 modos), `TomaFrancoDialog`, `LiberarUnidadDialog`.
- **Reusos**: `NovedadViajeDialog` estrena un **modo unidad** (`Interno > 0`); combustible reusa
  `CargasUnidadDialog`; Vehículo/Chofer reusan sus fichas; Viático navega a `/viaticos?chofer=XXX`
  (query param nuevo en esa página).
- **Métodos nuevos en `ReportService`**: `GetNovedadesUnidadAsync`, `GetAdicionalStockUnidadAsync`,
  `GetOrdenesTrabajoUnidadAsync`, `GetTarjetasUnidadAsync`, `GetVencimientosUnidadAsync`,
  `GetChoferesParaLogonearAsync`, `GetZonasAsync`.
- **Escritura en `AbmService`** (toda apagada): `LogonearAsync`, `DeslogonearAsync`,
  `TomarFrancoAsync`, `LiberarUnidadAsync` + helper privado `LogChoferAsync`.
  Flags nuevos: `LogoneoAbmActivo`, `TomaFrancoActivo`, `LiberarUnidadActivo` (los 3 en `false`).
  `AbmResult` estrena `Aviso` (éxito con salvedad).
- ⛔ **Bloqueante conocido: `viaje_log_chofer` no está replicada** (75.001 filas en el DBF). Es la
  bitácora del logoneo → **hay que replicarla antes de encender `LogoneoAbmActivo`**.
- Validado en pantalla (04/08/2026): los 16 ítems con las guardas del FoxPro correctas, y los 6
  diálogos abriendo con datos reales (200 novedades del interno 1, 63 adicionales, 29 OT, tarjetas
  YPF/ESSO con PIN). "Ir al Viaje" ubica el viaje 1541321 en la grilla.

### ✅ Módulo Tráfico — submenú Libro de Novedades: los 3 ítems (19/08/2026)

Migrado el submenú `Tráfico → Libro de Novedades` completo, que había quedado como placeholder.
Permiso `'T'`. Plano: `docs/PlanoFoxPro/trafico/LIBRO_NOVEDADES.md`.

| Ítem FoxPro | Pantalla Buslink | Estado |
| --- | --- | --- |
| `libro_novedad.scx` | `/libro-novedades` (`LibroNovedades.razor`) — **entra al hub `/informes`**, módulo Tráfico | ✅ lectura · modificar/eliminar con andamiaje (`NovedadesAbmActivo`) |
| `libro_novedad_envia_correo.scx` | `/envio-correos` (`EnvioCorreos.razor` + `CorreoNovedadesService`) | 🔒 **previsualiza pero NO manda** (`EnvioCorreosActivo`) |
| `libro_novedad_parametro.scx` | `/correos-destinatarios` (`CorreosDestinatarios.razor` + `DestinatarioCorreoEditorDialog`) | ✅ lectura + andamiaje (`DestinatariosCorreoAbmActivo`) |

**Métodos nuevos**: `ReportService.LibroNovedades.cs` (`GetLibroNovedadesAsync`,
`GetUsuariosLibroNovedadAsync`, `GetLibroNovedadAsync`, `GetNovedadesPendientesEnvioAsync`,
`GetUltimasNovedadesEnviadasAsync`, `GetSiniestrosPendientesEnvioAsync`,
`GetDestinatariosCorreoAsync`, `GetSmtpConfigAsync`) · `AbmService.LibroNovedades.cs`
(`ModificarNovedadAsync`, `BajaNovedadAsync`, `MarcarNovedadesEnviadasAsync`,
`MarcarSiniestrosEnviadosAsync`, `Alta/Modificar/BajaDestinatarioCorreoAsync`) ·
`ExcelExportService.LibroNovedades`.

**Decisiones y trampas (no re-descubrir):**
- El **envío no manda**: acción hacia afuera; si Buslink y el Metrocar mandaran los dos, los 12
  destinatarios internos reciben todo duplicado. Encender `EnvioCorreosActivo` exige bloquear el
  ítem en FoxPro **el mismo día**.
- Solo se migraron los bloques **Novedades** y **Siniestros**. **Combustible** y **Taller**
  (con adjunto Excel/PDF, gobernados por `parametro.f_ult_envi`) siguen en el Metrocar.
- 🐛 **`f_envio` NO prueba que el correo llegó**: el FoxPro lo estampa aunque el SMTP falle.
  Buslink lo estampa solo si al menos un destinatario recibió.
- El SMTP sale de `parametro` (`smtp_serve`/`smtp_puert`/`smtp_usuar`/`smtp_passw`/`smtp_nombr`,
  todos truncados) y reusa el criterio de `CorreoPruebaService` (STARTTLS con fallback en claro).
- `libro_novedad_parametro` **no tiene `id`**: PK lógica `contacto` (mayúsculas). Truncado
  `combustible` → **`combustibl`**. Baja FÍSICA en las dos tablas.
- Hallazgo: **9 novedades con `_deleted = 1`** en la réplica (jul–ago 2026) → el botón Eliminar de
  la lista SÍ se usa en producción. (El doc de junio decía que no había borradas.)
- Ninguna de las dos tablas es del circuito `viaje` → **podrían cortar antes del día D**, como
  `usuario` y `parametro`.

### ✅ Módulo Tráfico — Voucher · Guardia · Contactos · Lista de pasajeros (07/07/2026)

Migrados los **4 ítems restantes** del menú **Tráfico** en **solo lectura + andamiaje ABM**
(patrón Fleteros/TipoVehiculo). Permiso `'T'`. Con esto el menú Tráfico **ya no tiene placeholders**.
Plano consolidado: `docs/PlanoFoxPro/trafico/TRAFICO_VOUCHER_GUARDIA_CONTACTOS.md`.

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Voucher Recepción** | `VoucherRecepcion.razor` (`/voucher-recepcion`) | Auditoría del voucher (comprobante que firma el pasajero). NO tiene tabla propia: lee/escribe `viaje` (`voucher_nr`/`voucher_re`). 3 modos: rango de voucher / rango de fecha / **sin recepcionar** (demanda pendiente de firma). KPIs, Excel. Botones de marcar recepción = **andamiaje** (tocan `viaje` → día D). |
| **Guardia** | `Guardias.razor` (`/guardias`) + `GuardiaEditorDialog` | ABM de `viaje_guardia` (guardias de choferes/unidades). Filtro rango fecha, KPIs. Modifica bloqueado si ya está pagada (`fpago`). Datos históricos 2006-2008 (default de rango amplio). |
| **Contactos** | `Contactos.razor` (`/contactos`) + `ContactoEditorDialog` | Catálogo `estacion` = **PROVEEDORES de toda la empresa** (178: estaciones, gomerías, grúas, fleteros…), **COMPARTIDO con Combustible**. Filtros rubro + campo + texto. Ficha con flags legacy de combustible. |
| **Rubros de contactos** | `RubrosContacto.razor` (`/rubros-contacto`) | `estacion_rubro` (8): id + nombre + flag `audita`. Reusa `CatalogoSimpleEditorDialog` (extendido con `audita`). |
| **Lista de pasajeros** | `ListaPasajeros.razor` (`/lista-pasajeros`) | Buscador de viaje (fecha + interno/servicio/cliente/destino) → abre el `ListaPasajerosDialog` ya existente. Sin escritura. |

Métodos `ReportService`: `GetVoucherAuditoriaAsync`, `GetGuardiasAsync`/`GetGuardiaRowAsync`,
`GetContactosListaAsync`/`GetContactoRowAsync`, `GetRubrosContactoAsync`, `GetViajesParaBuscadorAsync`.
Escritura andamiaje en `AbmService`: `Alta/Modifica/BajaGuardiaAsync`, `…ContactoAsync`,
`…RubroContactoAsync`, `MarcarRecepcionAsync`/`MarcarRecepcionLoteAsync`. Flags nuevos en
`AbmFeatureFlags`: `GuardiaAbmActivo`, `ContactosAbmActivo`, `RubrosContactoAbmActivo`,
`VoucherRecepcionActivo` (**todos `false`**). Export: `Voucher`, `Guardias`, `Contactos`, `RubrosContacto`.

**🐛 Trampas clave:**

- **Baja FÍSICA** (`DELETE`) en `viaje_guardia`/`estacion`/`estacion_rubro` (sin `f_delete`). Alta
  con `MAX(id)+1` (id no-identity). Las 3 tablas **SÍ están replicadas** en el server activo.
- **`viaje.interno` es `bigint`** → leerlo con `GetInt32` tira `InvalidCastException`. Fix:
  `CAST(ISNULL(v.interno,0) AS int)` (Voucher + buscador de Lista de pasajeros). Truncados verificados:
  `voucher_nr`/`voucher_re`/`hs_s_inici` (viaje), `id_vehicul`/`nombre_cho` (guardia),
  `control_sa`/`cairo_codi` (estacion; `rubro` es bigint FK a `estacion_rubro`).
- **`estacion` es catálogo compartido con Combustible** → coordinar dueño único al activar el ABM
  (como Fleteros con Facturación).
- **Voucher se activa el DÍA D** con el circuito (toca `viaje`), no como catálogo suelto; la
  marca de recepción usa WHERE `id_viaje` + `f_reserva` (no hay índice por `id_viaje`).
- Validado 07/07/2026: 5 guardias / 178 contactos / 8 rubros / 120 vouchers; 25/25 smoke tests.

### ✅ Módulo Combustible — Consumos · Conciliación · Saldos · Depósitos (07/07/2026)

Primera entrega del menú **Combustible** (permiso `'M'` — no confundir con `'C'`=avisos). El drawer
ya tenía la sección con placeholders; ahora son **5 links reales** (se sacó `nav-section--soon`).
Patrón solo lectura + **andamiaje ABM** (Fleteros/Contactos). Plano de mapeo:
`docs/PlanoFoxPro/combustible/COMBUSTIBLE_ABM_MENU.md` (relevamiento completo: `COMBUSTIBLE.md`).

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Promedio de Consumos** | `PromedioConsumos.razor` (`/promedio-consumos`) | INFORME analítico de `vehiculo_combustible_consumo`. l/100km por unidad medido con el **método correcto** (entre cargas LLENO: Σlitros/Σkm del tramo, promedio global real, sanitizando Δodómetro ≤0 o >1000km) — corrige los 2 bugs del FoxPro. KPIs, barras top-N por unidad (cross-filter 1D), evolución mensual, tabla con **drill-down** a las cargas (`CargasUnidadDialog`), Excel. Costo/km solo si hay importe. |
| **ABM y Conciliación cargas** | `CombustibleConciliacion.razor` (`/combustible-conciliacion`) + `CargaCombustibleEditorDialog` | GRILLA de `vehiculo_sobre` (filtro TODOS/DOMINIO/LOTE/ESTACIÓN, LOTE ignora fechas), KPIs (incl. sin conciliar), filas conciliadas en amarillo. Barra de conciliación (**Nuevo lote / Marcar / Desmarcar / Marcar todo**) + ABM de la carga = **andamiaje** (`ConciliacionCombustibleAbmActivo=false`). |
| **Saldos de Estaciones** | `SaldosEstaciones.razor` (`/saldos-estaciones`) | INFORME histórico debe(depósitos)/haber(consumos)/saldo por estación con control de saldo. Aviso "sin uso desde 2017", default 2013-2017. |
| **Carga de Depósitos** | `DepositosEstacion.razor` (`/depositos-estacion`) + `DepositoEstacionEditorDialog` | Grilla `vehiculo_estacion_saldo` (ingreso/egreso) + Agregar (andamiaje). |
| **Mantenimiento de Depósitos** | `DepositosMantenimiento.razor` (`/depositos-mantenimiento`) | Misma grilla + Eliminar (baja **física**, andamiaje). Barra de filtros compartida: `DepositosFiltros.razor`. |
| **Control de cargas** | `ControlCargas.razor` (`/control-cargas`) | INFORME de días sin cargar (réplica `trafico_vehiculo_combustible`): última carga + días + odómetro por unidad propia activa; check "solo atrasadas" con umbral; filas rojas ≥15 días. `GetControlCargasAsync`. Link `vehiculo.id_vehicul=vehiculo_sobre.dominio`. |
| **Consumo Mensual** | `ConsumoMensual.razor` (`/consumo-mensual`) | INFORME nuevo (no existe en FoxPro): litros por mes × unidad × estación × tipo. Métrica = **litros** (el importe viene 0 con prepaga). KPIs, barras litros/mes, donut por tipo (cross-filter), pivote mes×unidad, Excel. `GetConsumoMensualAsync`. |

Métodos `ReportService`: `GetPromedioConsumosAsync`, `GetCargasCombustibleAsync`,
`GetCargaCombustibleRowAsync`, `GetLotesCombustibleAsync`, `GetEstacionesCombustibleAsync`,
`GetSaldosEstacionesAsync`, `GetDepositosEstacionAsync`, `GetDepositoEstacionRowAsync`. Escritura
andamiaje en `AbmService`: `NuevoLoteAsync`, `Marcar/Desmarcar/MarcarLoteMasivoAsync`,
`Alta/Modifica/BajaCargaCombustibleAsync`, `Alta/BajaDepositoEstacionAsync`. Flags nuevos:
`ConciliacionCombustibleAbmActivo`, `DepositosCombustibleAbmActivo` (ambos `false`). Export:
`PromedioConsumos`/`CargasCombustible`/`SaldosEstaciones`/`DepositosEstacion`.

**🐛 Trampas clave (validadas contra la base):**
- **`ClampFecha` NO va en combustible** — acota a 2021, vaciaba los informes históricos 2013-2017
  (Saldos/Depósitos daban $0). Fix: helper propio `ClampComb` (rango 2009-2027) en los 4 métodos.
- **`vehiculo_sobre`**: `estacion_n` (nombre truncado 30), `idrubro`, y `interno`/`odometro`/`n_sobre`/
  `estacion`/`n_factura`/`n_remito` son **bigint**; `f_carga` es la fecha operativa; `n_sobre=0`=sin
  conciliar. Filtrar SIEMPRE `f_carga BETWEEN 2009 AND 2027` (años corruptos, MAX sin filtro=4202).
- **`vehiculo_estacion_saldo`** (787 filas, todas 2013-2017): sin `f_delete` → baja FÍSICA; egreso =
  importe negativo; empresa "NORTUR" (el fuente hardcodea "PATAGONIA" — bug latente, no reproducir).
- **Haber=0 en Saldos es dato real** (importe 0 en cargas prepagas 2013-2017), no bug — fiel al FoxPro.
- **Costo/km casi siempre "—"** (tarjeta prepaga carga importe 0 desde 2018); métrica sólida = l/100km.
- **`parametro.lote_sobre`/`rubro_comb` son bigint** → `CAST(... AS int)` al leer.
- **`estacion` compartida con Tráfico (Contactos)**: los catálogos Estaciones/Rubro/Artículos del menú
  FoxPro NO se re-migran acá (reusan `Contactos`/`RubrosContacto`). Coordinar dueño único al activar.
- Validado: Saldos Larrazábal=1.620.555,90 / Senillosa=6.943.745,64; Depósitos 787 movs / $50M;
  Consumos 80 unidades / 206.446 litros.

**2ª entrega (07/07/2026): Control de cargas + Consumo Mensual** (informes, sin escritura).
- **Control de cargas** (`/control-cargas`) — réplica de `trafico_vehiculo_combustible`: días sin
  cargar por unidad propia activa (última carga + `DATEDIFF` a hoy + odómetro), check "solo atrasadas"
  con umbral, filas rojas ≥15 días. Link `vehiculo.id_vehicul = vehiculo_sobre.dominio`.
- **Consumo Mensual** (`/consumo-mensual`) — informe **nuevo** (no existe en FoxPro): litros por mes ×
  unidad × estación × tipo. Métrica = **litros** (el importe viene 0 con prepaga → no hay costo real,
  verificado 2020-2026). KPIs, barras litros/mes, donut por tipo (cross-filter 1D), pivote mes×unidad.
- **🐛 Ojo Control de cargas:** la réplica está congelada al 08/06/2026 → hoy TODAS las unidades salen
  "atrasada" (los datos de julio no se replicaron). Es real, no bug; en producción con datos frescos
  se distribuye normal. `GetControlCargasAsync`/`GetConsumoMensualAsync`; exports `ControlCargas`/
  `ConsumoMensual`. Validado: 86 unidades, 529.096 litros 2026; 32/32 smoke tests.

**3ª entrega (07/07/2026): los 3 catálogos del menú → menú Combustible COMPLETO (10/10 ítems).**
Hallazgo (verificado en `MENU_PRINCIPAL.MPR` 427-429): los 3 ítems abren los MISMOS forms FoxPro que
otros menús — Estaciones→`estacion` y Rubro de Consumos→`estacion_rubro` son idénticos a los de
Tráfico (Contactos/Rubros de contactos). Decisión (validada con el usuario): **reutilizar** — los links
"Estaciones" y "Rubro de Consumos" apuntan a `/contactos` y `/rubros-contacto` (una sola pantalla
mantenida, DRY). Solo se creó la 3ª: **Artículos por Rubro de Consumo** (`ArticulosRubro.razor`,
`/articulos-rubro` + `ArticuloRubroEditorDialog`) sobre `estacion_rubro_articulo` — solo lectura +
andamiaje (`ArticulosRubroAbmActivo=false`). Combo de rubro (`estacion_rubro`) + nombre; para rubro 1
son los tipos de combustible (DIESEL 500 / EURO-DIESEL). 🐛 baja física, id no-identity (`MAX(id)+1`),
`nombre` truncado a 30. `GetArticulosRubroAsync`/`GetArticuloRubroRowAsync`; `Alta/Modifica/BajaArticuloRubroAsync`;
export `ArticulosRubro`. Validado: 2 artículos; 33/33 smoke tests. **El menú Combustible ya no tiene placeholders.**

### ✅ Módulo Reservas — Operadores · Grupos · Destinos (06/07/2026)

Migrados los 3 ítems del menú **Reservas** que faltaban (Operadores, Grupos, Destinos) en
**solo lectura + andamiaje ABM** (patrón Fleteros/TipoVehiculo). Permiso `'R'` (módulo Reservas).
Los 3 ítems del drawer pasan de placeholders `--disabled` a **links reales**. Planos verificados
al dígito contra el fuente FoxPro (dumps 06/07/2026): `docs/PlanoFoxPro/catalogos/CLIENTE_OPERADOR_ABM.md`,
`CLIENTE_GRUPO_ABM.md`, `DESTINO_ABM.md`.

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Operadores** | `Operadores.razor` (`/operadores`) + `OperadorEditorDialog` | Catálogo `cliente_operador` (128 filas): contacto de la agencia dentro de un cliente. `id_operado` = PK lógica **global**. LEFT JOIN a cliente (razón social). |
| **Grupos** | `Grupos.razor` (`/grupos`) + `GrupoEditorDialog` | `cliente_grupo` (11.272 → Virtualize) + combo No finalizados (default)/Finalizados/Todos. **Sin alta** (los grupos nacen en Reservas). Editor muestra el desglose de viajes por estado (cascada). |
| **Destinos** | `Destinos.razor` (`/destinos`) + `DestinoEditorDialog` | `destino` (398): lugares origen/destino (autocomplete Desde/Hasta). Checkbox `mas100km`, combo localidad (`destino_localidad`, 34). |

Métodos `ReportService`: `GetOperadoresListaAsync`/`GetOperadorRowAsync`,
`GetGruposListaAsync(filtro)`/`GetGrupoRowAsync`/`GetViajesGrupoPorEstadoAsync`,
`GetDestinosListaAsync`/`GetDestinoRowAsync`/`GetDestinoLocalidadesAsync`. Escritura andamiaje en
`AbmService` (Alta/Modifica/Baja de Operador y Destino + `AltaLocalidadAsync`; Grupos = **solo
Modifica/Baja en cascada**, sin alta). Flags nuevos en `AbmFeatureFlags`: `OperadoresAbmActivo`,
`DestinosAbmActivo`, `GruposAbmActivo` (**todos `false`**). Export: `ExcelExportService.Operadores`/
`Grupos`/`Destinos`.

**🐛 Trampas clave:**
- **Baja FÍSICA** (`DELETE`) en las 3 (sin `f_delete`; solo `_deleted` de la réplica). Alta con
  `MAX(id)+1` (id no-identity).
- **Grupos NO tiene alta** (el form FoxPro la tiene 100% comentada — nacen en Reservas). Su
  **modifica y baja operan EN CASCADA sobre `viaje`** → tocan el circuito viaje: `GruposAbmActivo`
  se activa el **día D** con Tráfico, no como catálogo suelto. Baja = cancelación masiva con motivo
  (`viaje_motivo_cancela`) + DELETE del grupo solo si no hay FINALIZADO/FACTURADO. Clasificación de
  estados **distinta entre baja y modifica**: en modifica **FINALIZADO es modificable**; en baja es
  bloqueante junto con FACTURADO (verificado contra el fuente).
- Operadores/Destinos son **Grupo A** del plan Buslink (cutover temprano); Grupos es **Grupo B**
  (día D).
- Columnas verificadas: `cliente_grupo` truncadas `f_grupo_fi`/`f_grupo_in`/`f_grupo_fc`; `id_operado`
  truncado. `viaje` para la cascada: `estado_via`, `grupo`, `id_cliente`, `id_motivo`, `interno`,
  `id_vehicul`, `id_chofer`, `nombre_cho`, `franco`.
- 🐛 Bug heredado corregido en Destinos: el modifica del FoxPro hace `contacto = contacto` (no
  guarda el contacto) — en Blazor **sí** se graba.
- Validado contra SQL: 128 / 189 (grupos no finalizados) / 398 / 34 localidades.

### ✅ Módulo Reservas — Reservas Especiales · Plantillas · Armado (07/07/2026)

Migrados los **2 ítems principales del menú Reservas** (los últimos placeholders operativos del
módulo) en **solo lectura + andamiaje ABM**. ⚠ A diferencia de los catálogos, estas 3 son
**puertas de alta al circuito `viaje`** (no CRUD de catálogo): insertan filas en `viaje`. Son
**Fase 4 del plan Buslink** y cambian de dueño el **día D** junto con Tráfico y el Graba de
Facturación — NO se activan como catálogo suelto. Permiso `'R'`. Planos (ya completos):
`docs/PlanoFoxPro/reservas/RESERVA_TRANSPORTACION.md` y `RESERVA_PLANTILLAS.md`.

| Vista | Página (ruta) | Qué hace |
| --- | --- | --- |
| **Reservas Especiales** | `ReservasEspeciales.razor` (`/reservas-especiales`) | Grilla de las reservas ya cargadas manualmente (`viaje.origen='T'`) — rango de fechas obligatorio + búsqueda + estados + Virtualize (~12-19k/año). Ficha reusa `ReservasFsDetalleDialog` (drill-down al Zoom). Botón **"Nueva reserva"** → abre `ReservaEspecialEditorDialog` (form de alta FIEL completo). |
| **Mantenimiento de Plantillas** | `PlantillasMantenimiento.razor` (`/plantillas-mantenimiento`) | Combo de las 9 plantillas + grilla de sus filas (`reserva_plantilla`, Virtualize). Botonera Agregar/Modificar/Eliminar/Eliminar Todo/Renombrar/Duplicar (andamiaje) → `PlantillaFilaEditorDialog` (4 modos) + `PlantillaNombreDialog` (renombrar/duplicar). |
| **Reservas por Plantillas (Armado)** | `ReservasPorPlantillas.razor` (`/reservas-por-plantillas`) | Combo plantilla + Buscar → grilla read-only de filas + cliente + rango de fechas + checks de días (L-D + Feriados) + botones rápidos. **Preview / dry-run EN MEMORIA** (cuántos viajes generaría, sin escribir). Botón Generar (andamiaje). |

Dialogs: `ReservaEspecialEditorDialog.razor` (form de alta fiel: fechas, cliente/operador,
servicios 1/2/3, vehículo, pax/km, grupo, guía, destinos con autocomplete, **Valor Especial**
solo con permiso `'F'`, Cnt Servicios, **grilla de Adicionales** en memoria, modo ruta "varios
días", **preview de filas** días×servicios), `PlantillaFilaEditorDialog.razor` (ver/alta/
modifica/baja, cabecera de 15 pos), `PlantillaNombreDialog.razor` (renombrar/duplicar con aviso
de fusión).

Métodos `ReportService`: `GetReservasEspecialesAsync` (reusa `ReservaFsDetalleRow`),
`GetPlantillasResumenAsync`/`GetPlantillasComboAsync`/`GetPlantillaFilasAsync`/
`GetPlantillaFilaRowAsync`, `GetFeriadosRangoAsync`. Escritura andamiaje en `AbmService`
(fiel a `graba_viaje`): `AltaReservaEspecialAsync` (resolución de grupo + loop días×servicios
o ruta + `viaje_log` + `viaje_adicional` + upsert `guia`, **con transacción**), ABM de
plantilla (`Alta/Modifica/Baja/BajaCompleta/Renombrar/DuplicarPlantilla…`), `ArmarPlantillaAsync`
(lote + lógica E/S de cabecera). Flags nuevos en `AbmFeatureFlags`: `ReservasEspecialesAbmActivo`,
`PlantillasAbmActivo`, `ArmadoPlantillasActivo` (**todos `false`**). Export:
`ExcelExportService.ReservasEspeciales` / `Plantillas`.

**🐛 Trampas clave:**
- **Bigint en `viaje`** (`id_grupo`/`id_plantil`/`id_viaje_i`/`interno`/`km`/`voucher_nr`) y en
  **`reserva_plantilla`** (`hs`/`km`/`km_real`/`pax`/`adi_can_1..5`) → `CAST(... AS int)` al leer.
- **`reserva_plantilla` baja FÍSICA** (DELETE, sin `f_delete`), id no-identity (`MAX(id)+1`).
  Cabecera es nvarchar(**15**) (la pos 16 lógica = rango de vehículo).
- **0 feriados 2026 cargados** → el armado avisa (fiel al FoxPro). El preview del armado es
  100% en memoria (dry-run, no toca la base).
- **Concatenación de raw strings SQL** (bug resuelto): concatenar `PlantillaFilaSelect` (raw
  string que NO termina en newline) con un `"""WHERE…"""` pega `s.id_serviciWHERE` → error de
  sintaxis. Fix: concatenar con `+ " WHERE …"` (string normal con espacio inicial).
- **Estado inicial único `SIN ASIGNAR`**, `cronograma=cronogram2='S/C'`, `str_f_rese` sincronizado
  con `f_reserva` — reglas de oro del INSERT (relevantes al activar el día D).
- Valor Especial requiere permiso `'F'` (precios convenidos) — la sección se oculta sin él.

Validado 07/07/2026 (dos señales UI+SQL): Reservas 'T' 30 días = 378 res / 9.637 pax; 9
plantillas / 574 filas; armado AEROLINEAS 07-14/07 L-V = 786 viajes (6 fechas × 131). Smoke tests
en la suite (verifican carga + botones de escritura deshabilitados). **El menú Reservas ya no
tiene placeholders operativos** (quedan "Clientes" y "Crear Plantillas", no pedidos en esta entrega).

### Drawer: arranca todo colapsado (15/06/2026)

Todas las secciones del menú lateral inician **colapsadas** para cualquier usuario
(flags `_*Expanded = false` en `MainLayout.razor`). Hay un aviso "Tocá una sección para
abrirla" (`.nav-hint`) al inicio del `<nav>`. El usuario abre la sección que necesite.

### ✅ ABM de Usuarios y Permisos — HECHO (01/07/2026) · PRIMER ABM DE ESCRITURA

El **primer ABM con escritura real** (alta/baja/modificación) del proyecto — hasta acá todo era
solo lectura. Estrena la estrategia "SQL dueño tabla por tabla": la tabla `usuario` migró de dueño
a SQL (ABM salido de FoxPro, sync DBF→SQL apagada) y Blazor la escribe en el **server local**.
Permiso `'S'` (solo supervisor). Menú: sección **Sistema** del drawer.

| Pieza | Archivo |
| --- | --- |
| Capa de escritura (nueva, plantilla) | `Services/AbmService.cs` — INSERT/UPDATE con `SqlParameter` + transacción, `AbmResult` |
| Catálogo de permisos | `Services/PermisosCatalogo.cs` — 16 letras en orden `S R T C D V L F A E U B H X N M` + reglas |
| Lectura | `ReportService.GetUsuariosAsync` / `GetUsuarioDetalleAsync` |
| Lista | `Components/Pages/UsuariosAbm.razor` (`/usuarios-abm`) |
| Dialog | `Components/Shared/UsuarioEditorDialog.razor` — un solo dialog, 4 modos ver/alta/modifica/baja |

Trampas resueltas: `usuario.id` **no es identity** (alta con `MAX(id)+1`); `password`/`acceso`
son `nvarchar(15)` (password plano; validar acceso ≤15 aunque haya 16 letras); baja lógica =
`f_delete` (no `_deleted`); `nivel` fijo `"12345"`; reglas en vivo C→T y X→SUPERVISOR; defensa
anti-autobloqueo. Validado con `ZZTEST01` (dos señales) + capturas. Detalle: skill
`abm-metrocar` (§ Primer ABM de escritura). **Pendiente producción real:** bloquear ABM en FoxPro
+ confirmar sync apagada antes de escribir en el server nuevo.

### ✅ Parámetros del sistema (Empresa + Generales + GPS) — HECHO (12/08/2026) · **2º ABM CON ESCRITURA REAL**

Migra `parametro_empresa.scx` (42 objetos) y `parametro.scx` (105 objetos) — los 2 primeros de los
4 ítems del submenú **ABM del sistema → Parámetros Generales** del FoxPro. Son **editores de la
fila única de `parametro`** (1 fila, 72 columnas), no ABMs: no hay lista, ni alta, ni baja.

⚠️ **Ubicación cambiada a propósito:** en el FoxPro cuelgan de `ABM del sistema` (permiso `'A'`);
en Buslink van en la sección **Sistema** con permiso **`'S'`** (junto a Usuarios y Auditoría de
accesos), porque la pantalla expone CUIT, tasa de IVA y la password del correo. Los 2 ítems
placeholder del drawer bajo "ABM del sistema" quedan como links a `/parametros`, pero solo si el
usuario además tiene `'S'`.

| Pieza | Archivo |
| --- | --- |
| Lectura | `ReportService.GetParametrosEmpresaAsync` / `GetParametrosGeneralesAsync` / `GetParametrosCombosAsync` / `ExisteClienteAsync` |
| Escritura (andamiaje) | `AbmService.GrabarParametrosEmpresaAsync` / `GrabarParametrosGeneralesAsync` / `ValidarCuit` |
| Pantalla | `Components/Pages/Parametros.razor` (`/parametros`) — **una página, 2 solapas** |
| Logo | `Services/LogoEmpresaService.cs` + endpoint `/logo-empresa` en `Program.cs` + `Logo:*` en appsettings |
| Prueba de correo | `Services/CorreoPruebaService.cs` |
| Invalidación de caché | `ReportService.InvalidarCacheParametros()` (piva · aviso_config · agenda-venc) |
| Flag | `AbmFeatureFlags.ParametrosAbmActivo = **true**` (el cliente desconectó `parametro` del watcher) |

**Una sola página con 2 solapas a propósito:** las dos pantallas del FoxPro escriben la MISMA fila
de la MISMA tabla; separadas se pisarían.

✅ **Escritura ACTIVA desde el 12/08/2026** — 2º ABM real, después de Usuarios. Las 3 solapas son
editables (el usuario eligió explícitamente incluir GPS y contadores). Guardas: confirmación al
grabar solo si hay cambios sensibles (apagar el GPS, tocar un contador — con el antes→después) y
`Permisos.TieneABM(3)`, que hasta acá existía sin que lo usara nadie.

🔴 **Deuda del corte anticipado:** en la misma fila viven los **contadores vivos** del circuito
(`id_viaje_i`, `lote_plant`, `lote_sobre`, `stock_movi`). Con el watcher apagado, FoxPro los
sigue incrementando en su DBF y esos incrementos ya no llegan a SQL → las dos copias **divergen
desde el 12/08**. **Hay que resincronizarlos el día D** (paso 4 del checklist del corte) o el
primer lote/viaje que arme Buslink sale repetido. Registro de tablas migradas y checklist de
activación: skill `abm-metrocar`.

**3 bugs del fuente FoxPro corregidos** (§3.4 del plano): `aviso_mat` se editaba y **no se
grababa** (y alimenta el chip de Vencimientos); `dir_mdb`/`intranet` se **blanqueaban en cada
Aceptar** (se escriben sin haberse cargado); `lote_plant` no se grababa (bug a favor). Buslink
graba `aviso_mat`, no toca `dir_mdb`/`intranet` y deja los contadores en solo lectura.

**Solo lectura permanente:** contadores vivos, `xml_envia`/`dir_xml` (interruptor del GPS,
pendiente de la decisión de Fase 0) y las 6 rutas de unidades de red del FoxPro.

**«Probar envío correo» migrado corregido:** el original grababa el SMTP antes de probar, mandaba
a `jlsilvamtb@gmail.com` (casilla del desarrollador viejo, hardcodeada) y forzaba SSL sobre el
puerto 25. Ahora no escribe nada, el destinatario se elige, e intenta STARTTLS con fallback en
claro informando cuál anduvo.

Validado: SQL de los 2 UPDATE probado dentro de una transacción revertida (`aviso_mat` 10→99,
contadores intactos); `ValidarCuit` cruzado contra la transcripción del FoxPro en 40.006 casos
(única diferencia: rechaza separadores que no sean guiones — endurecimiento deliberado); 2 smoke
tests nuevos. Plano: `docs/PlanoFoxPro/sistema/PARAMETROS.md` · skill `modulo-sistema`.

### ✅ Solapa GPS — 3er tramo (12/08/2026) · y el hallazgo que corrige el plan

Migra `parametro_sql_server.scx` (26 objetos) como **3ª solapa de `/parametros`**, toda en
solo lectura + los 3 botones de diagnóstico.

> 🔴 **Hallazgo que corrige la Fase 0.2 del plan:** el proyecto daba por **muerta** la
> integración GPS (`gps_xlm()` "es un NO-OP"). **Era un error** — esa conclusión salió de la
> réplica **local**, que es un snapshot viejo. En los **dos servers productivos**
> `parametro.sql_gps = 1`, apuntando a `192.168.0.8` / `MetroCarSQL` / `Servicios`.
> `gps_xlm()` corre con `xml_envia` **OR** `sql_gps`, y filtra por `cliente.envia_gps`:
> **136 clientes** (incluida AEROLINEAS) = **3.466 de 3.713 viajes del último mes (93 %)**.
> Se dispara en ASIGNO, RE-ASIGNO, FINALIZO, CANCELO y armado de plantillas.
> **Si Buslink toma el circuito sin replicar el envío, el feed muere en silencio.**
> Docs ya corregidos: `GPS_XLM.md`, `CLAUDE.md`, el plan (Fase 0.2 + riesgo 4).
> **Sin confirmar:** el host responde ping pero su puerto SQL no es accesible desde la PC de
> desarrollo → verificado que la bandera está en 1, **no** que los INSERT estén entrando.

| Pieza | Archivo |
| --- | --- |
| Lectura | `ReportService.GetParametrosGpsAsync` + `GetGpsAlcanceAsync` (clientes con GPS y % de viajes) |
| Diagnóstico | `Services/GpsSqlService.cs` — `ProbarConexionAsync` · `UltimasFilasAsync` · `VaciarTablaAsync` |
| Flag | `AbmFeatureFlags.GpsTruncateActivo = false` (**no espera al día D**: no toca `replicaVPF`) |

**Los 3 botones, corregidos:** *Conexión* también verifica que la tabla destino exista (el modo
exacto en que este feed falla callado); *Select* pasa de un `Browse` de toda la tabla a total +
últimas 20 filas; *Truncate* estaba **roto** (usaba `lnHandle` y `cSql_tabla`, **ninguna
definida en su método**, y después hacía `DELETE FROM servicios_nortur` hardcodeado) → ahora usa
la conexión y tabla configuradas, valida el identificador, pide confirmación y va con flag apagado.

**🐛 Bug del FoxPro que NO se replicó, y que hoy es una bomba en producción:** el `Init` hace
`SUBSTR(sql_server, 1, AT("\", sql_server) - 1)`. Como el valor productivo `192.168.0.8` **no
tiene backslash**, "Maquina" carga **vacía**, y el `LostFocus` recompone `sql_server` desde ese
vacío → **abrir esa pantalla del Metrocar y tabular borra la dirección del GPS**. Por eso en
Buslink el servidor se muestra entero y en solo lectura.

Validado: camino de error (server inexistente → mensaje limpio, sin excepción) y camino de éxito
(apuntando temporalmente la config del server **local** a una tabla real: conectó, contó 62
filas, detectó la identity y renderizó la vista previa; config restaurada idéntica). 1 smoke test.

**Falta de este submenú:** `parametro_trafico.scx` (44 objetos).

### ✅ Panel de Clientes — HECHO tramo 1 (10/08/2026) · INFORME NUEVO, no migra nada del FoxPro

Cruza las tres caras del cliente, que hasta ahora vivían en pantallas que no se hablaban:
**padrón** (`cliente`) + **actividad** (`viaje`) + **plata** (`liquidacion_detalle`).
Ruta `/panel-clientes`, permiso `'F'` (Facturación — la letra que además protege precios).
Alta en el hub: **una línea en `InformesCatalogo.cs`**, módulo `Facturacion`.

| Pieza | Archivo |
| --- | --- |
| Datos | `Services/ReportService.PanelClientes.cs` — `GetClientesActividadAsync` (cliente × mes), `GetClientesPadronAsync`, `GetFechaCorteDatosAsync` |
| Pantalla | `Components/Pages/PanelClientes.razor` |
| Excel | `ExcelExportService.PanelClientes` — hojas Ranking · pivote · **Padrón** (lista accionable) |
| Ficha | reusa `ClienteDetalleDialog` (clic en el nombre del cliente) |

**Qué muestra:** 6 KPIs (clientes, facturado, viajes, pax, facturado por viaje, concentración
top-5), barras top-N + torta por dimensión con cross-filter, **ranking con % del total y %
acumulado (Pareto)** y pivote cliente × mes con columna Evolución + sparkline. Dos consultas y
todo lo demás en memoria: cambiar dimensión, métrica o enfocar no vuelve a la base.

**Cuatro dimensiones, y ninguna sale de un campo "tipo"** (`cliente.empresa_fc` vale NORTUR en
los 414 registros): **línea de negocio** (derivada de `servicio.modo_uso`, mismo criterio que
Banda Horaria), **moneda facturada**, **tipo fiscal** (`tipo_resp`) y **segmento de actividad**
(por última reserva contra la fecha de corte de los datos).

**Cálculo de la plata — leer antes de tocar** (detalle en el encabezado del `.cs` y en la
memoria `panel-clientes-informe`): devengado al mes del VIAJE; importe por línea =
`importe + incremento − descuento` con la moneda DE LA LÍNEA y el `t_cambio` de su cabecera;
**NO se usa `liquidacion.total`** (tiene cargas corruptas: la liquidación 2364 declara
$22.200 millones contra $6,9 M de su detalle). Reconstruye el 99,97% de 2026.

Trampas resueltas: `responsable_tipo` tiene **EXT duplicado** (usar subconsulta `TOP 1` con
`ISNULL` externo, nunca `LEFT JOIN`); **números dentro de formatters JS con `InvariantCulture`**
o el chart queda vacío y cae el circuito; `OffsetX = 46` en los datalabels (el 22 del patrón no
alcanza con datos tan extremos); la columna Evolución excluye el mes en curso; aviso de rezago
de liquidación cuando el último mes completo tiene < 95% de sus viajes facturados.

**Vista 2 — Salud del padrón (hecha):** `Components/Shared/PadronClientesPanel.razor`, con
selector de vistas arriba (`Cartera` | `Salud del padrón`; en la 2ª se ocultan los filtros de
período, que no aplican al catálogo). Cada problema se muestra con **dos números: cuántos lo
tienen y cuántos de esos OPERAN HOY** — 405 sin contacto pero 49 activos, 170 sin teléfono →
14 activos. Detecta **provincias mal escritas por distancia de Levenshtein ≤2** contra un
valor más frecuente (sin lista fija de provincias), los **9 clientes sin forma de valorizar**
(regla del ABM: `ob_precio='CLIENTE'` exige `cliente_tarifa`, si no exige `id_lista_p`) y los
**grupos que comparten CUIT** — que NO son fichas duplicadas: AEROLINEAS tiene 4 códigos por
centro de facturación (AA/AAESP/AEOEVENT/EZEEVENT), y el ranking de cartera los cuenta por
separado. El Excel exporta las filas filtradas que se ven.

**Vista 3 — Retención y riesgo (hecha):** `Components/Shared/RetencionClientesPanel.razor`.
Compara el período contra un **período base** de una 2ª consulta (selector *Período anterior* /
*Año pasado*). Pieza central: **matriz ABC × recencia** — la clase sale del Pareto de la
métrica (A = primer 80 %, B = hasta 95 %, C = la cola), no de montos fijos que la inflación
envejece. 🔴 **Las fugas conservan la clase del período BASE**: clasificadas por el actual (que
es 0) caerían todas en C y el informe se comería el caso grave. Estados: se fue / cayó /
estable / creció / nuevo (±40 %), con filtros rápidos y drill a la ficha. El **aviso de rezago
de liquidación** también va acá — sin él la vista informa una caída de −$1,8 MM que en buena
parte es facturación no emitida todavía.

**Ficha del cliente:** `ClienteDetalleDialog` estrena la solapa **Actividad**
(`GetClienteActividadAsync`: serie mensual con plata devengada, servicios y recorridos más
usados, operadores, últimas liquidaciones). Se carga después de los datos del ABM para que el
dialog abra rápido, y **si el cliente no operó en el período reconsulta 24 meses y lo avisa** —
es el caso de todas las fugas, donde lo que interesa es qué hacían antes de irse.

🔴 **Trampa Razor:** un parámetro de componente de tipo **string sin `@`** toma el LITERAL
(`Metrica="_metrica"` pasa el texto, no el campo). Los de tipo lista/objeto sí se evalúan, así
que el bug se disfraza: los datos se ven bien y solo fallan los textos y los formatos.
