# Metrocar Nortur — Informes y Reportes (Blazor)

## Objetivo del proyecto

Migrar **gradualmente los informes y reportes** del sistema viejo **Metrocar (FoxPro)** a una plataforma nueva y moderna, leyendo los datos directamente desde la base **SQL Server `replicaVPF`**.

Se va **reporte por reporte**: se toma un informe que hoy existe en FoxPro, se entiende qué hace, y se reconstruye como un **dashboard interactivo moderno** (no se copia el Excel viejo al pie de la letra — se mejora).

**Empresa:** NORTUR (Metrocar) — transporte / transfers / turismo (combis, traslados aeropuerto, city tours, cenas show, etc.).

---

## Stack elegido

| Capa | Tecnología |
| --- | --- |
| Lenguaje | **C# (.NET 10.0 LTS)** |
| Framework web | **Blazor Server** (modo interactivo) |
| Componentes UI | **MudBlazor 9.5** |
| Gráficos | **Blazor-ApexCharts 6.1.0** (instalado, listo para usar) |
| Acceso a datos | **Entity Framework Core 10** (SQL crudo con `FromSqlRaw`) |
| Export | **ClosedXML** (descarga a Excel) |
| Base de datos | **SQL Server** — base `replicaVPF` |

> **Decisión de stack:** Se migró de Python/Streamlit a C# Blazor Server para un stack más robusto, con tipado fuerte y mejor integración con el ecosistema .NET existente (`MetroCarSysAPI`). Migrado a **.NET 10 LTS** (junio 2026) — soporte hasta noviembre 2028.

### Levantar el dashboard

```bash
cd "c:\Users\HP\OneDrive\CLAUDE CODE\Metrocar Nortur Blazor\MetroCarSysBlazor"
dotnet run
# Abre en https://localhost:7xxx o http://localhost:5xxx (ver launchSettings.json)
```

---

## Conexión a la base (replicaVPF)

**Servidor activo (nuevo)** — apuntado en `appsettings.json`:

```text
Server     = 172.25.69.217   (SQL Server 2012 — fuera de soporte)
Database   = replicaVPF
User Id    = sa
Password   = Nortur1024
Encrypt = True ; TrustServerCertificate = True
```

> ✅ **Datos replicados (verificado 10/06/2026):** el servidor nuevo ya tiene la réplica operativa (viajes de 2026, 204 vehículos activos). El motor es **SQL Server 2012**: evitar funciones que no soporta al escribir SQL nuevo (`STRING_AGG`, `TRIM`, `CONCAT_WS`, `APPROX_COUNT_DISTINCT`, etc.).

**Servidor viejo (local, con datos completos)** — fallback mientras dure la migración:

```text
Server     = DESKTOP-CV6LF0O\SQLEXPRESS
Database   = replicaVPF   (108 tablas, 512.876 filas en viaje)
User Id    = sa
Password   = Nortur2021
TrustServerCertificate = True
```

- Centralizada en `appsettings.json` (clave `ConnectionStrings:DefaultConnection`).
- **108 tablas** en total. Es una réplica de la base productiva de FoxPro.

> ⚠️ **Connection string: SIEMPRE `Pooling=True` (regla de performance, 16/06/2026).**
> Tener `Pooling=False` hacía que cada query pagara handshake TLS + login + resolución de
> instancia `\SQLEXPRESS` desde cero (eran segundos de lag por conexión). El string correcto
> incluye `Pooling=True;Min Pool Size=2;Max Pool Size=50`. `Encrypt=True` se mantiene (con
> pool, el TLS se amortiza). Al arrancar, `DbWarmupService` precalienta el pool (verás
> "Pool de conexiones SQL calentado: N conexiones en NNN ms" en el log). Detalle completo:
> `docs/PERFORMANCE_GRILLAS_Y_CONEXION.md`.

---

## Estructura del proyecto

```text
MetroCarSysBlazor/
  Program.cs                          → Bootstrap: DI, middleware, EF, MudBlazor, ApexCharts
  appsettings.json                    → Connection string a replicaVPF
  MetroCarSysBlazor.csproj            → Dependencias NuGet
  Components/
    App.razor                         → Root component (incluye JS de ApexCharts)
    Routes.razor                      → Router
    _Imports.razor                    → Using globales (NO incluir ApexCharts aquí — ver nota)
    Layout/
      MainLayout.razor                → Layout principal: drawer CSS overlay + AppBar + auth guard
      EmptyLayout.razor               → Layout limpio (para login)
    Pages/
      Home.razor                      → Página de inicio
      Login.razor                     → Formulario de login
      ReservasFechaServicio.razor     → Informe 1
    Shared/
      KpiCard.razor                   → Componente tarjeta KPI (reutilizable)
  Data/
    NorturDbContext.cs                → DbContext (solo puerta de acceso; sin DbSet — SQL crudo)
  Services/
    ReportService.cs                  → Capa de datos: todas las queries SQL + caché
    AuthService.cs                    → Autenticación
    NorturAuthStateProvider.cs        → Estado de sesión (Blazor AuthenticationStateProvider)
    ExcelExportService.cs             → Generación de .xlsx con ClosedXML
    DbWarmupService.cs                → IHostedService: precalienta el pool de conexiones SQL al arrancar (performance)
  Theme/
    NorturTheme.cs                    → Paleta MudBlazor corporativa NORTUR
  wwwroot/                            → Assets estáticos (CSS, JS, Bootstrap)
    app.css                           → Estilos globales + sistema del drawer CSS
docs/
  INFORME_TECNICO.md                  → Documentación técnica completa
  INFORME_TECNICO_NORTUR.pdf          → PDF del informe técnico
  PERFORMANCE_GRILLAS_Y_CONEXION.md   → Por qué/cómo se resolvió el lag de grillas grandes (pooling + Virtualize)
.claude/
  settings.json                       → Registra las skills locales del proyecto
  skills/
    blazor-nortur/SKILL.md            → Patrones UI/stack (drawer, KpiCard, ReportService, Excel)
    foxpro-extract/SKILL.md           → Leer forms FoxPro (.scx) — incluye scripts/scx_dump.py
    abm-metrocar/SKILL.md             → Metodología de migración de ABMs + regla de escritura
    modulo-trafico/SKILL.md           → Mapa del módulo Tráfico (tablas, estados, migrado/pendiente)
```

### Arquitectura general

- **Blazor Server interactivo**: el rendering ocurre en el servidor vía SignalR. No hay WASM.
- **EF Core sin modelos de entidad**: `NorturDbContext` solo provee la conexión. Las queries usan SQL crudo directo para evitar mapear los ~80 campos de FoxPro.
- **Caché en memoria**: `IMemoryCache` con TTL de 5 minutos por query. Se usa en `ReportService`.
- **MudBlazor**: componentes de UI (MudGrid, MudPaper, MudTable, etc.).
- **ApexCharts**: para todos los gráficos nuevos (ver sección abajo).
- **Autenticación**: custom `AuthenticationStateProvider` (`NorturAuthStateProvider`) que valida contra la tabla `usuario` de `replicaVPF`.

### Cómo agregar un reporte nuevo (3 pasos)

1. Agregar la query en `Services/ReportService.cs` con un método `async Task<List<MiDto>>`.
2. Crear `Components/Pages/MiReporte.razor` con `@page "/mi-ruta"` (usar `ReservasFechaServicio.razor` como plantilla).
3. Agregar el link en `Components/Layout/MainLayout.razor` en el nav lateral (sección correspondiente).

---

## ApexCharts — integración y uso

### Instalación (ya hecha)

- Paquete NuGet: `Blazor-ApexCharts 6.1.0` (nombre correcto: con guión)
- Registrado en `Program.cs`: `builder.Services.AddApexCharts()`
- JS en `App.razor`: `<script src="_content/Blazor-ApexCharts/js/blazor-apexcharts.js"></script>`

### Regla crítica — colisión de namespaces

`ApexCharts` comparte nombres con `MudBlazor` (`Color`, `Size`, `ChartType`). **No agregar `@using ApexCharts` en `_Imports.razor`** — rompe todos los archivos que usan MudBlazor.

**Solución:** agregar `@using ApexCharts` solo en los archivos `.razor` que usen gráficos ApexCharts, al inicio del archivo.

### Uso básico en una página

```razor
@using ApexCharts   @* solo en páginas que usen ApexCharts *@

@* Gráfico de barras *@
<ApexChart TItem="MiDto" Title="Mi Título" Options="_optsBarras">
    <ApexPointSeries TItem="MiDto"
                     Items="_datos"
                     Name="Reservas"
                     SeriesType="SeriesType.Bar"
                     XValue="@(x => x.Etiqueta)"
                     YValue="@(x => x.Valor)" />
</ApexChart>

@code {
    private ApexChartOptions<MiDto> _optsBarras = new()
    {
        Chart = new() { Toolbar = new() { Show = false } },
        Colors = new List<string> { "#003AA0", "#F99410" }
    };
}
```

---

## Menú de navegación lateral (drawer)

### Decisión de arquitectura — drawer fuera del MudLayout

`MudDrawer` de MudBlazor dentro de `MudLayout` siempre se posiciona debajo del AppBar (el flex flow de MudLayout lo fuerza). Para lograr `position:fixed` desde `top:0`, el drawer es un `<div>` CSS puro **completamente fuera de `<MudLayout>`**.

### Estructura del layout

```
[nav-backdrop]         ← overlay oscuro, click cierra el drawer
[div.nav-drawer]       ← position:fixed, top:0, FUERA del MudLayout
  [nav-drawer__header] ← degradado azul NORTUR, altura 48px
  [nav-drawer__nav]    ← grupos colapsables por sección
[MudLayout]
  [MudAppBar]          ← hamburguesa, logo, usuario, cerrar sesión
  [MudMainContent] → @Body
```

### Secciones del menú actuales

| Sección | Estado | Ruta |
| --- | --- | --- |
| **Reservas** | expandida por defecto | — |
| → Reservas por fecha y servicio | activo | `/reservas-fecha-servicio` |
| → Reservas por banda horaria | deshabilitado (próximo) | — |
| **Tráfico** | colapsado | — |
| → (placeholder) | deshabilitado | — |

### CSS del drawer (`wwwroot/app.css`)

| Clase | Descripción |
| --- | --- |
| `.nav-drawer` | Panel lateral, `position:fixed`, `top:0`, `width:270px` |
| `.nav-drawer--open` | Activa `transform:translateX(0)` |
| `.nav-backdrop` | Overlay oscuro detrás del drawer |
| `.nav-section__title` | Botón de grupo colapsable |
| `.nav-item` | Enlace de navegación |
| `.nav-item--active` | Ítem activo (borde naranja izquierdo) |
| `.nav-item--disabled` | Ítem sin ruta aún (gris, no clickeable) |

---

## Modelo de datos relevado

### Tabla `viaje` = una RESERVA (512.876 filas)

| Campo | Tipo | Significado |
| --- | --- |--- |
| `f_reserva` | date | **Fecha de la reserva** (campo para agrupar/filtrar) |
| `f_pedido` | date | Fecha en que se cargó/pidió la reserva |
| `id_servici` | nvarchar(15) | Código de servicio → FK a tabla `servicio` |
| `id_cliente` | nvarchar(15) | Código de cliente |
| `nombre_cli` | nvarchar(50) | Nombre del cliente (desnormalizado) |
| `nombre_cho` | nvarchar | Nombre del chofer asignado |
| `id_chofer` | nvarchar(15) | Chofer asignado |
| `id_vehicul` | nvarchar(15) | Vehículo asignado |
| `pax` | int | Cantidad de pasajeros |
| `estado_via` | nvarchar(15) | Estado del viaje (ver abajo) |
| `origen` | char(1) | `'T'` = transportación, `'P'` = plantilla |
| `total` / `importe` / `precio` | decimal | Importes (**OJO: muchos vienen `NULL`**) |
| `hs_inicio` / `hs_present` / `hs_fin` | datetime2 | Horarios |
| `_deleted` | bit | 1 = borrado lógico (siempre filtrar `_deleted = 0`) |
| `_created_at` / `_updated_at` | datetime2 | Auditoría de la réplica |

**Estados (`estado_via`):**
| Estado | Cantidad aprox |
| --- | --- |
| FACTURADO | 454.701 |
| FINALIZADO | 21.878 |
| CANCELADO | 21.287 |
| SIN ASIGNAR | 14.943 |
| ASIGNADO | 67 |

### Tabla `servicio` (61 filas)

Catálogo de servicios. Clave: `id_servici` (PK lógica), `nombre`.

Top servicios por volumen: `CABECERA_KM`, `CABECERA_SERV`, `EZEIZA`, `TRASLADO`, `AEROPARQUE`, `CITY`, `GUARDIA8`, `CENA SHOW`, `LA PLATA 1/2`, etc.

### Tabla `viaje_horario` (6 filas) — bandas horarias

Franjas horarias para el informe de banda horaria: `dhorario` (desde, HH:mm), `hhorario` (hasta, HH:mm).
Las 6 bandas: `00:00-00:01`, `00:02-06:29`, `06:30-08:29`, `08:30-14:00`, `14:01-18:00`, `18:01-23:59`.

### Otras tablas relevantes

`viaje_adicional`, `viaje_pasajero`, `cliente`, `chofer`, `vehiculo`, `ctacte`, `liquidacion`, `taller_service`, `vehiculo_combustible`, `reserva_plantilla`, `destino`, `zona`, `vehiculo_tipo`, `usuario`.

---

## Reglas de negocio / convenciones

1. **Siempre filtrar `_deleted = 0`** en `viaje` y demás tablas (borrado lógico).
2. **Acotar fechas al rango válido** (`ReportService.FechaMinValida` = 2021-01-01, `FechaMaxValida` = 2027-12-31) para evitar fechas corruptas (año 309, 2252, etc.).
3. Datos usables de `f_reserva`: **2021–2026** (años con volumen real).
4. **`total`/`importe` con muchos NULL** → métricas confiables hoy: **cantidad de reservas** y **pax**.
5. **No usar parametrización de EF para WHERE dinámico**: construir el SQL como string (ver `ReportService`), pero siempre escapar con `.Replace("'", "''")`.
6. **Performance de grillas grandes (regla, 16/06/2026):** una grilla que puede superar
   ~100-150 filas DEBE usar `<Virtualize>` (el servidor genera solo las filas visibles, no
   todas) + memoizar el filtrado en un campo (no recalcular en cada render). El connection
   string SIEMPRE con `Pooling=True`. El síntoma "lento solo con muchos registros" es render
   de Blazor, no SQL. Patrón completo y trampas: `docs/PERFORMANCE_GRILLAS_Y_CONEXION.md`.
   Referencia viva ya optimizada: `PlanillaTrafico.razor`.

---

## Estado actual

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

### ✅ Informe 1: "Reservas por fecha y servicio" — HECHO

Componente `Components/Pages/ReservasFechaServicio.razor`.

- **Filtros:** período, servicios (multiselect), incluir/excluir canceladas, métrica (Reservas / Pax).
- **KPIs:** total reservas, total pax, canceladas (+%), servicios distintos.
- **Gráficos:** barras (top 10 servicios) + donut (distribución top 12) con `MudChart` (pendiente migrar a ApexCharts).
- **Tabla pivote** fecha × servicio con totales.
- **Botón Descargar Excel** (ClosedXML).

### ✅ Vistas de solo lectura migradas (lista + ficha) — HECHO

Réplicas fieles de pantallas FoxPro en **solo lectura** (lista + ficha modal, botonera de
ABM deshabilitada — la escritura sigue en FoxPro; estrategia strangler de `abm-metrocar`).
Mismo patrón y estilos CSS (`cli-*`, `zoom-*`) — calcar uno para hacer el siguiente.

| Entidad | Lista (página) | Ficha (dialog) | Doc FoxPro | Permiso | Menú |
| --- | --- | --- | --- | --- | --- |
| **Clientes** | `ClientesAbm.razor` (`/clientes-abm`) | `ClienteDetalleDialog.razor` | `CLIENTE_ABM.md` | `'F'` | Facturación → Clientes → ABM - Clientes |
| **Choferes** | `Choferes.razor` (`/choferes`) | `ChoferDetalleDialog.razor` (5 tabs) | `CHOFER_ABM.md` | `'V'` | Vehículos y Choferes → Choferes |
| **Vehículos** | `Vehiculos.razor` (`/vehiculos`) | `VehiculoDetalleDialog.razor` (6 tabs) | `skills/.../references/VEHICULOS.md` | `'V'` | Vehículos y Choferes → Vehículos - Flota |

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

### Drawer: arranca todo colapsado (15/06/2026)

Todas las secciones del menú lateral inician **colapsadas** para cualquier usuario
(flags `_*Expanded = false` en `MainLayout.razor`). Hay un aviso "Tocá una sección para
abrirla" (`.nav-hint`) al inicio del `<nav>`. El usuario abre la sección que necesite.

### Pendiente / próximos

- **Informe 2:** Reservas por fecha y banda horaria (lógica de `trafico_resumen_horario.scx`) — usar ApexCharts.
- **Informe 3:** Tráfico / Operación (cliente / chofer / vehículo).
- **Cuenta Corriente**, **Liquidación choferes**, etc.
- Migrar gráficos del Informe 1 de MudChart a ApexCharts.

---

## Lógica FoxPro documentada (para ABM)

Metodología: antes de construir cualquier ABM en Blazor, extraer y documentar la lógica del form FoxPro correspondiente.

```text
docs/
  logica-foxpro/    ← un .md por cada form ABM documentado
  pdfs/             ← PDFs del proyecto
  INFORME_TECNICO.md
```

| Archivo | Form FoxPro | Descripción |
| --- | --- | --- |
| `docs/logica-foxpro/TRAFICO_ZOOM.md` | `trafico_zoom.scx` | Zoom del Viaje: máquina de estados, validaciones, tablas tocadas, reglas no obvias |
| `docs/logica-foxpro/TRAFICO2_FILTROS.md` | `trafico2.scx` | Toolbar de Tráfico: combos de unidades (U/Pr `cronogram2`, U/Cb `cronograma`), botón S/C, botón Cxl (cancelados con motivo), mapeo de columnas en la réplica |
| `docs/logica-foxpro/RESERVA_TRANSPORTACION.md` | `reserva_transportacion_con_adicional.scx` + 4 subdialogs | Alta manual de reservas: validaciones, multiplicación días×servicios, modo ruta, grupos, valor especial (permiso "F"), adicionales, INSERT completo |
| `docs/logica-foxpro/RESERVA_PLANTILLAS.md` | `reserva_plantilla_crear/_mantenimiento/_mantenimiento_abm/_nombre/_armar.scx` | Ciclo completo de plantillas: crear, mantener, armar (generación masiva por días+feriados), cabecera 16 posiciones, lotes |
| `docs/logica-foxpro/CLIENTE_ABM.md` | `cliente.scx` + `cliente_abm.scx` | Catálogo cliente: CUIT, esquema de precios, flags operativos, rubros excluidos, baja lógica con fecha |
| `docs/logica-foxpro/CHOFER_ABM.md` | `chofer.scx` + `chofer_abm.scx` | ABM de Conductores: grilla (filtro Fletero/Nombre/Egresados), ficha de 5 pestañas, mapa de columnas truncadas, vehiculo_chofer, `chofer_log` no replicada |
| `docs/logica-foxpro/CLIENTE_GRUPO_ABM.md` | `cliente_grupo.scx` + `cliente_grupo_abm.scx` | Grupos: candado `f_grupo_fc`, baja = cancelación masiva de viajes con motivo, renombre con arrastre |
| `docs/logica-foxpro/CLIENTE_OPERADOR_ABM.md` | `cliente_operador*.scx` | Operadores por cliente (id global, baja física) |
| `docs/logica-foxpro/DESTINO_ABM.md` | `destino*.scx` | Destinos: autocomplete de Desde/Hasta, `mas100km`, distrito |
| `docs/logica-foxpro/IMPORTA_EXCEL_VIAJE.md` | `importa_excel_viaje.scx` | Importación masiva: 28 columnas, 3 etapas de validación, transaccional, adicionales inline |
| `docs/logica-foxpro/RESERVAS_INFORME_BANDA_HORARIA.md` | `trafico_resumen_horario*.scx` | Informe 2: conteo fecha×banda×vehículo, SQL listo para Blazor |
| `docs/logica-foxpro/FACTURACION_LIQUIDACION.md` | `facturacion_cliente_nueva.scx`, `liquidacion_fletero_nueva.scx`, `liquidacion_cliente.scx`, `liquidacion_chofer_por_hora.scx`, `ctacte_*.scx`, `lista_precio_*.scx` | Módulo completo Facturación/Liquidación: valorización, cascadas de precios, grabado, revertir, tarifarios, cta. cte. (sin uso), tablas SQL reales |
| `docs/logica-foxpro/COMBUSTIBLE.md` | `vehiculo_combustible_mant_sobre_lote.scx`, `vehiculo_combustible_carga_sobre(_trafico).scx`, `vehiculo_combustible_consumo.scx`, `trafico_vehiculo_combustible.scx`, `vehiculo_estacion_saldo*.scx`, `estacion*.scx` | Módulo completo Combustible: las 2 eras (tabla viva `vehiculo_sobre` NO replicada), conciliación por sobre/lote, promedio de consumos, saldos/depósitos, catálogo de proveedores, trampas y candidatos Blazor |

---

## El código FoxPro: a veces está en disco, a veces no

El sistema FoxPro completo está en `C:\MetroCarSys` (fuentes `.prg`, forms `.scx`/`.sct`, reportes `.frx`, menús `.mpr`). El `metrocar.exe` productivo está **más actualizado** que parte del fuente en disco — verificar caso por caso.

**Regla práctica:** antes de armar un informe, buscar su form/prg en disco. Si está, leer la lógica; si no, reconstruir desde la base.

### Lector DBF/FPT para inspeccionar forms FoxPro

Los `.scx`/`.frx`/`.sct`/`.fpt` son tablas DBF de VFP con el código en campos **memo**:
- El puntero al memo se guarda como **entero de 4 bytes little-endian** en el campo de registro.
- En el `.sct`, **block size = 1**. Bloque memo: 4 bytes tipo + 4 bytes longitud (big-endian) + contenido (cp1252).
- Campos relevantes por objeto: `METHODS` (código), `PROPERTIES`, `OBJNAME`.

### Ubicaciones de referencia FoxPro
- App completa: `C:\MetroCarSys`
- Reportes: `C:\MetroCarSys\Reports\*.frx` (~40)
- Programas: `C:\MetroCarSys\Progs\*.prg`
- Forms: `C:\MetroCarSys\Forms\*.scx` (378)
- Menús: `C:\MetroCarSys\Menus\*.mpr`
- DBF originales: `C:\MetroCarSys\Nortur` (cp1252)

---

## Próximos informes candidatos

- **Tráfico / Viajes** (`trafico_informe`, `trafico_imprime`, `trafico_grupo`) — operación
- **Cuenta Corriente** (`ctacte_saldo_cliente`) — financiero
- **Liquidación choferes** (`liquidacion_resumen`) — pagos a choferes/fleteros
- **Reservas por fecha y banda horaria** — `trafico_resumen_horario.scx` (lógica disponible)
- Combustible, Taller/Service, Viáticos, etc.

---

## Sistema de skills del proyecto (jun 2026)

Arquitectura de skills chicas y componibles (procesos × módulos), siguiendo los principios
de Anthropic: cada corrección aprendida se guarda en la skill correspondiente, no se repite.

| Skill | Tipo | Cubre |
| --- | --- | --- |
| `blazor-nortur` | horizontal | cómo construir UI/reportes en este stack |
| `foxpro-extract` | horizontal | cómo leer la lógica del FoxPro viejo (script incluido) |
| `abm-metrocar` | horizontal | cómo migrar escritura (ABMs) — **leer antes de cualquier INSERT/UPDATE** |
| `seguridad-nortur` | horizontal | permisos: `acceso` (letras), `nivel` (dígitos ABM), claims en Blazor |
| `modulo-trafico` | vertical | conocimiento del módulo Tráfico |
| `modulo-reservas` | vertical | conocimiento del módulo Reservas (12/06/2026) — alta manual, plantillas, grupos, catálogos, importa Excel; base para los ABMs futuros |
| `modulo-facturacion-liquidacion` | vertical | conocimiento del módulo Facturación/Liquidación (12/06/2026) — liquidación a clientes/fleteros/choferes, tarifarios, ctacte (sin uso). Doc detallado: `docs/logica-foxpro/FACTURACION_LIQUIDACION.md` |
| `modulo-combustible` | vertical | conocimiento del módulo Combustible (12/06/2026) — cargas de la flota (tabla viva `vehiculo_sobre`, **NO replicada a SQL**), conciliación por lote, consumos l/100km, saldos de estaciones (sin uso desde 2017). Doc detallado: `docs/logica-foxpro/COMBUSTIBLE.md` |
| `modulo-vehiculos-choferes` | vertical | conocimiento del módulo Vehículos y Choferes (15/06/2026) — flota (`vehiculo`) y personal de conducción (`chofer`, `fletero`), tipos de vehículo, odómetros, siniestros, apercibimientos, capacitaciones, agenda de vencimientos. Una referencia por pantalla en `references/`. Trampas: `vehiculo_chofer` vacía, `chofer_log` no replicada, columnas truncadas. Choferes y Vehículos-Flota ya migrados (solo lectura) |
| `blazor-performance` | horizontal | optimización de rendimiento en Blazor (16/06/2026) — lentitud, re-renders, `MudDataGrid`/`MudTable`/`Virtualize<T>`, memory leaks, paginación, `StateHasChanged` excesivo. Referencias: `mudblazor-performance.md`, `apexcharts-blazor.md` |

**Skills de módulo futuras** (crear recién al arrancar cada módulo, no antes):
`modulo-taller` (taller/service). La cuenta corriente quedó cubierta dentro de
`modulo-facturacion-liquidacion` (módulo programado pero sin uso en producción).

### Decisión de escritura para ABMs (10/06/2026)

**SQL dueño, tabla por tabla (strangler):** Blazor solo escribe en tablas cuyo dueño ya es
SQL. Una tabla migra cuando su ABM Blazor está listo + se bloquea el ABM en FoxPro + se apaga
la sync DBF→SQL de esa tabla. Los datos escritos en SQL **se quedan en SQL** — no hay puente
inverso. Mientras tanto, las tablas de FoxPro son **solo lectura** desde Blazor.
Detalle completo y checklist: skill `abm-metrocar`.

---

## Contexto de negocio

Claudio Marañon construye una **agencia de IA para e-commerce**. Este proyecto (Metrocar Nortur) es un cliente de **migración/modernización de sistema legacy FoxPro**. El enfoque: reporte por reporte, incremental y replicable.
