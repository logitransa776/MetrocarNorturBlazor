# Informe Técnico — Plataforma de Informes y Reportes
## NORTUR (Metrocar) · Sistema de Gestión

---

**Fecha de elaboración:** Junio 2026
**Versión:** 2.0 (migrado a Blazor)
**Estado:** Producción (desarrollo incremental)

---

## 1. Objetivo del sistema

La plataforma de Informes y Reportes de NORTUR es una aplicación web interna que **reemplaza gradualmente los informes del sistema legacy FoxPro (Metrocar)**. El sistema viejo generaba exportaciones a Excel de forma estática; el nuevo dashboard permite consultas interactivas en tiempo real, con filtros, gráficos y exportación a Excel desde el navegador.

El principio de migración es **reporte por reporte**: se toma un informe del sistema FoxPro, se analiza su lógica de negocio original, y se reconstruye como un panel interactivo moderno — no como una copia, sino como una mejora.

---

## 2. Stack tecnológico

| Capa | Tecnología | Versión |
|---|---|---|
| Lenguaje | C# | .NET 9.0 |
| Framework web | Blazor Server (modo interactivo) | .NET 9.0 |
| Componentes UI | MudBlazor | 9.5.0 |
| Acceso a base de datos | Entity Framework Core (SQL crudo) | 9.x |
| Driver de conexión SQL Server | Microsoft.EntityFrameworkCore.SqlServer | 9.x |
| Exportación a Excel | ClosedXML | 0.105.0 |
| Caché en memoria | IMemoryCache (.NET built-in) | — |

**Blazor Server** funciona como una SPA con estado en el servidor, comunicada con el navegador vía SignalR (WebSocket). Cambiar de filtro o navegar entre reportes no recarga la página — actualiza sólo los componentes afectados.

---

## 3. Fuente de datos

### Base de datos: `replicaVPF`

| Parámetro | Valor |
|---|---|
| Motor | Microsoft SQL Server (Express) |
| Servidor | `DESKTOP-CV6LF0O\SQLEXPRESS` |
| Base de datos | `replicaVPF` |
| Usuario | `sa` |
| Total de tablas | 108 tablas |
| Tipo de base | Réplica de la base productiva FoxPro |

`replicaVPF` es una **réplica sincronizada de la base productiva** del sistema Metrocar (FoxPro). Permite consultar datos en tiempo real sin impactar el sistema de producción.

### Configuración de conexión: `appsettings.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=DESKTOP-CV6LF0O\\SQLEXPRESS;Database=replicaVPF;User Id=sa;Password=Nortur2021;TrustServerCertificate=True;"
  }
}
```

### Reglas de calidad de datos

La tabla principal (`viaje`) contiene **fechas corruptas** (registros con año 309, año 2252, etc.) provenientes del sistema FoxPro original. Todas las consultas acotan las fechas al rango válido definido en `ReportService`:

- **Fecha mínima:** 1 de enero de 2021 (`FechaMinValida`)
- **Fecha máxima:** 31 de diciembre de 2027 (`FechaMaxValida`)

---

## 4. Estructura de archivos del proyecto

```text
MetroCarSysBlazor/
│
├── Program.cs                          → Bootstrap: DI, middleware, EF, MudBlazor
├── appsettings.json                    → Connection string a replicaVPF
├── MetroCarSysBlazor.csproj            → Dependencias NuGet
│
├── Components/
│   ├── App.razor                       → Root component
│   ├── Routes.razor                    → Router
│   ├── _Imports.razor                  → Using globales para todos los .razor
│   ├── Layout/
│   │   ├── MainLayout.razor            → Layout principal (nav lateral + header)
│   │   └── EmptyLayout.razor           → Layout limpio (solo para login)
│   ├── Pages/
│   │   ├── Home.razor                  → Página de inicio
│   │   ├── Login.razor                 → Formulario de autenticación
│   │   └── ReservasFechaServicio.razor → Informe 1: Reservas por fecha y servicio
│   └── Shared/
│       └── KpiCard.razor               → Componente tarjeta KPI (reutilizable)
│
├── Data/
│   └── NorturDbContext.cs              → DbContext (puerta de acceso; sin DbSet — SQL crudo)
│
├── Services/
│   ├── ReportService.cs                → Capa de datos: todas las queries SQL + caché
│   ├── AuthService.cs                  → Validación de credenciales
│   ├── NorturAuthStateProvider.cs      → Estado de sesión (AuthenticationStateProvider)
│   └── ExcelExportService.cs           → Generación de .xlsx con ClosedXML
│
├── Theme/
│   └── NorturTheme.cs                  → Paleta MudBlazor corporativa NORTUR
│
└── wwwroot/                            → Assets estáticos (CSS, JS, Bootstrap)

docs/
└── INFORME_TECNICO.md                  → Este documento
```

---

## 5. Descripción de cada componente

### `Program.cs` — Bootstrap de la aplicación

Registra todos los servicios en el contenedor DI:

- `AddRazorComponents().AddInteractiveServerComponents()` — Blazor Server interactivo.
- `AddMudServices()` — componentes MudBlazor.
- `AddDbContextFactory<NorturDbContext>` — factory de EF Core (preferida sobre `AddDbContext` en Blazor Server para evitar conflictos de ciclo de vida por circuito SignalR).
- `AddMemoryCache()` — caché en memoria para `ReportService`.
- Servicios de aplicación: `ReportService`, `AuthService`, `ExcelExportService` (todos `Scoped`).
- `NorturAuthStateProvider` como `AuthenticationStateProvider` + `AddAuthorizationCore` + `AddCascadingAuthenticationState`.

### `NorturDbContext.cs` — Acceso a datos

DbContext mínimo: no declara `DbSet<>` para ninguna entidad. Toda interacción con la base es SQL crudo via `db.Database.SqlQuery<T>` o `db.Database.GetDbConnection()`. Esto evita mapear los ~80 campos de las tablas FoxPro.

### `ReportService.cs` — Capa de datos

**Todas las consultas SQL viven en este servicio.** Los componentes Razor nunca escriben SQL directamente — piden datos a métodos de `ReportService`. Esto centraliza el acceso y facilita el mantenimiento.

Los resultados se cachean con `IMemoryCache` (TTL 5 minutos). La clave de caché incluye todos los parámetros relevantes (fechas, filtros activos), por lo que mismas consultas no van dos veces a la base.

**Métodos disponibles:**

| Método | Descripción |
|---|---|
| `GetServiciosAsync()` | Catálogo de servicios para los filtros |
| `GetReservasPorFechaServicioAsync()` | Reservas agrupadas por fecha y tipo de servicio |
| `ValidarCredencialesAsync()` | Valida usuario/contraseña contra tabla `usuario` |

**Construcción de WHERE dinámico:** se construye el SQL como string, escapando inputs con `.Replace("'", "''")`. No se usan parámetros formales de EF porque las tablas no tienen entidades mapeadas.

### `NorturAuthStateProvider.cs` — Autenticación

Implementa `AuthenticationStateProvider` de ASP.NET Core. Mantiene el estado de sesión en el circuito SignalR (en memoria por instancia de circuito). El estado persiste mientras la pestaña esté abierta; si el usuario cierra la pestaña o se reconecta, debe volver a loguearse.

**Flujo de login:**

1. `Login.razor` captura usuario y contraseña.
2. Llama a `ValidarCredencialesAsync()` de `ReportService`.
3. Si es válido, `NorturAuthStateProvider` actualiza el estado y notifica a los componentes suscritos.
4. El router redirige a la página de inicio.

**Tabla de usuarios:** `[replicaVPF].[dbo].[usuario]`

| Campo | Tipo | Descripción |
|---|---|---|
| `usuario` | nvarchar | Nombre de usuario (ej: ALEJANDRA, SUPERVISOR) |
| `password` | nvarchar | Contraseña en texto plano (sistema FoxPro original) |
| `nivel` | nvarchar | Nivel de acceso |
| `_deleted` | bit | 0 = activo, 1 = eliminado lógicamente |

### `ExcelExportService.cs` — Export a Excel

Genera archivos `.xlsx` en memoria usando ClosedXML. Retorna `byte[]` que el componente Razor descarga via JS Interop (`descargarArchivo` en `wwwroot/js`).

### `NorturTheme.cs` — Paleta corporativa

Configura el `MudTheme` con los colores de la marca NORTUR (extraídos de nortur-srl.com.ar):

| Token | Color | Uso |
|---|---|---|
| `Primary` | `#003AA0` | Botones, bordes, KPIs principales |
| `Secondary` | `#F99410` | Acento naranja, logo |
| `Success` | `#1E9E6A` | Métricas positivas (pax) |
| `Error` | `#D64545` | Métricas negativas (cancelados) |

### `KpiCard.razor` — Componente reutilizable

Tarjeta de KPI con valor grande, etiqueta y subtexto. Parámetros: `Label`, `Value`, `Sub`, `Color`. Usada en todos los reportes.

---

## 6. Modelo de datos principal

### Tabla `viaje` — 512.876 filas (una fila = una reserva)

| Campo | Tipo | Descripción |
|---|---|---|
| `f_reserva` | date | Fecha de la reserva (campo principal para filtros) |
| `f_pedido` | date | Fecha en que se cargó el pedido |
| `id_servici` | nvarchar(15) | Código de servicio (FK → tabla `servicio`) |
| `id_cliente` | nvarchar(15) | Código de cliente |
| `nombre_cli` | nvarchar(50) | Nombre del cliente (desnormalizado) |
| `nombre_cho` | nvarchar | Nombre del chofer asignado |
| `id_chofer` | nvarchar(15) | Código del chofer |
| `id_vehicul` | nvarchar(15) | Código del vehículo asignado |
| `pax` | int | Cantidad de pasajeros |
| `estado_via` | nvarchar(15) | Estado del viaje |
| `origen` | char(1) | `'T'` = transportación, `'P'` = plantilla |
| `hs_inicio` | datetime2 | Hora de inicio del servicio |
| `hs_present` | datetime2 | Hora de presentación |
| `hs_fin` | datetime2 | Hora de finalización |
| `total` / `importe` / `precio` | decimal | Importes (muchos registros vienen NULL) |
| `_deleted` | bit | 1 = borrado lógico — **siempre filtrar `_deleted = 0`** |
| `_created_at` / `_updated_at` | datetime2 | Auditoría de la réplica |

**Estados de viaje (`estado_via`):**

| Estado | Cantidad aproximada |
|---|---|
| FACTURADO | 454.701 |
| FINALIZADO | 21.878 |
| CANCELADO | 21.287 |
| SIN ASIGNAR | 14.943 |
| ASIGNADO | 67 |

### Tabla `servicio` — 61 filas

Catálogo de tipos de servicio. PK: `id_servici`. Servicios más frecuentes: `CABECERA POR KILOMETRO`, `CABECERA POR SERVICIO`, `EZEIZA`, `TRASLADO`, `AEROPARQUE`, `CITY TOUR 4 HS`, `CENA SHOW`, `GUARDIA 8 HS`, entre otros.

### Tabla `viaje_horario` — 6 filas

Define las 6 franjas horarias para el informe de banda horaria:

| Franja | Desde | Hasta |
|---|---|---|
| Madrugada 1 | 00:00 | 00:01 |
| Madrugada 2 | 00:02 | 06:29 |
| Mañana temprana | 06:30 | 08:29 |
| Mañana/mediodía | 08:30 | 14:00 |
| Tarde | 14:01 | 18:00 |
| Noche | 18:01 | 23:59 |

### Tabla `usuario`

Tabla de usuarios del sistema FoxPro, usada para autenticación en el dashboard.

---

## 7. Informes disponibles

### Informe 1: Reservas por fecha y servicio

**Componente:** `Components/Pages/ReservasFechaServicio.razor`
**Origen FoxPro:** El código original no estaba en disco — se reconstruyó desde la base de datos.

**Qué muestra:**

- KPIs: total reservas, total pax, canceladas (+%), servicios distintos
- Gráfico de barras: top 10 servicios por reservas o pax
- Gráfico donut: distribución por servicio (top 12)
- Tabla pivote: fecha × servicio con totales por fila

**Filtros disponibles:**

- Período (Desde / Hasta) con `MudDatePicker`
- Servicios específicos (multiselección con `MudSelect`)
- Incluir / excluir canceladas (toggle)
- Métrica a mostrar: Reservas o Pax

**Exportación:** Excel vía ClosedXML (descarga por JS Interop)

**Query base:**

```sql
SELECT v.f_reserva, v.id_servici, COALESCE(s.nombre, v.id_servici),
       COUNT(*) AS Reservas,
       SUM(CASE WHEN v.estado_via='CANCELADO' THEN 1 ELSE 0 END) AS Canceladas,
       SUM(COALESCE(v.pax, 0)) AS Pax
FROM viaje v LEFT JOIN servicio s ON v.id_servici = s.id_servici
WHERE v._deleted = 0 AND v.f_reserva BETWEEN ? AND ?
GROUP BY v.f_reserva, v.id_servici, s.nombre
ORDER BY v.f_reserva, Servicio
```

---

## 8. Cómo levantar la aplicación

```bash
cd "c:\Users\HP\OneDrive\CLAUDE CODE\Metrocar Nortur Blazor\MetroCarSysBlazor"
dotnet run
```

La URL exacta se define en `Properties/launchSettings.json`. Por defecto corre en `https://localhost:7xxx` / `http://localhost:5xxx`.

**Requisitos previos:**

- .NET 9.0 SDK instalado
- SQL Server `DESKTOP-CV6LF0O\SQLEXPRESS` corriendo con la base `replicaVPF` accesible

---

## 9. Cómo agregar un informe nuevo

1. **Agregar la query** en `Services/ReportService.cs`: un método `async Task<List<MiDto>>` con caché por clave derivada de los parámetros.
2. **Crear el componente** `Components/Pages/MiReporte.razor` con `@page "/mi-ruta"` y `@rendermode InteractiveServer`. Usar `ReservasFechaServicio.razor` como plantilla.
3. **Agregar el link** en `Components/Layout/MainLayout.razor` en el nav lateral.

---

## 10. Origen de la lógica de negocio (FoxPro)

El sistema FoxPro completo está en `C:\MetroCarSys`:

- Formularios (`.scx` / `.sct`): 378 forms
- Reportes impresos (`.frx`): ~40 reportes
- Programas (`.prg`)
- Menús (`.mpr`)

Los `.scx`, `.frx`, `.sct` son tablas DBF de Visual FoxPro con código fuente en campos memo.

**Regla práctica al armar un informe nuevo:**

1. Buscar el form/programa correspondiente en `C:\MetroCarSys`
2. Si está disponible: leer la lógica original y replicarla
3. Si no está en disco: reconstruir desde la base y pedir al cliente una captura del Excel productivo como referencia

---

## 11. Próximos informes planificados

| Informe | Form FoxPro | Categoría |
|---|---|---|
| Reservas por banda horaria | `trafico_resumen_horario.scx` | Reservas |
| Viajes detalle | `trafico_informe`, `trafico_imprime` | Tráfico |
| Cuenta Corriente clientes | `ctacte_saldo_cliente` | Financiero |
| Liquidación choferes | `liquidacion_resumen` | Pagos |
| Combustible | — | Flota |
| Taller / Service | — | Flota |

---

## 12. Convenciones y buenas prácticas del proyecto

1. **Siempre filtrar `_deleted = 0`** en todas las consultas (borrado lógico del FoxPro).
2. **Acotar fechas** al rango válido (`FechaMinValida` / `FechaMaxValida`) para excluir datos corruptos.
3. **No escribir SQL en los componentes Razor** — todas las queries van en `ReportService`.
4. **No hardcodear colores hex** en los componentes — usar siempre `NorturTheme` o `NorturColors`.
5. **Usar `IDbContextFactory`** (no `IDbContext` inyectado directamente) — evita problemas de ciclo de vida en Blazor Server.
6. **Importes (`total`/`importe`) con muchos NULL** — no usar para métricas de facturación sin validar con el cliente. Las métricas confiables son cantidad de reservas y pax.

---

*Documento actualizado en Junio 2026. Para consultas técnicas o actualizaciones, referirse al archivo `CLAUDE.md` en la raíz del proyecto.*
