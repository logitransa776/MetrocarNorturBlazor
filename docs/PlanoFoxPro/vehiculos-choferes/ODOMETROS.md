# Control de Odómetros — `vehiculo_km.scx`

> **Estado:** ✅ migrado a Blazor **solo lectura** (04/07/2026) — `Components/Pages/Odometros.razor`
> (`/odometros`), menú **Vehículos y Choferes → Odómetros**, permiso `'V'`.
> Registro de lecturas de kilometraje por vehículo/mes. La carga (Agregar/Modificar/Eliminar)
> sigue en FoxPro — botonera ABM deshabilitada (estrategia strangler de `abm-metrocar`).

FoxPro: menú `Vehiculos y Choferes → Odometros` (`ON SELECTION BAR 5 OF vehiculosy OpenForm("vehiculo_km")`).

---

## Qué hace

Es un **informe/registro de lecturas** (no un ABM de catálogo). Muestra las lecturas de
odómetro cargadas para la flota. El operador filtra por un vehículo (dominio) o por todos, en
un rango de fechas, y ve la grilla de lecturas con los km recorridos calculados.

## Tabla `vehiculo_km`

| Campo (SQL) | Tipo | Significado |
| --- | --- | --- |
| `id` | int | PK |
| `dominio` | nvarchar(20) | Patente del vehículo |
| `interno` | bigint | Nro interno |
| `tipo_mov` | nvarchar(10) | Tipo de movimiento |
| `ano_y_mes` | nvarchar(6) | Período `AAAAMM` (ej. `202606`) |
| `fecha` | datetime2 | Fecha del movimiento |
| `f_carga` | date | **Fecha de carga de la lectura** (campo que filtra la grilla) |
| `km_inicio` | bigint | Km de inicio del período |
| `km_fin` | bigint | Km de cierre del período (**suele venir NULL en el mes en curso**) |
| `km_recorri` | bigint | Km recorridos (persistido; en la grilla se recalcula al vuelo) |
| `odometro` | bigint | Lectura de odómetro |
| `u_create` / `u_modify` | nvarchar(20) | Usuario que creó / modificó |

- **~10.533 filas** activas (`_deleted = 0`). Rango real de `f_carga`: 2009 → hoy.
- La tabla es **transaccional** y se relaciona con Combustible (l/100km usa estos km) y con el
  informe **Km Unidades vs Servicios** (`KM_UNIDADES_VS_SERVICIOS.md`).

## Grilla (`arma_grid`) — 9 columnas

| Col | ControlSource | Header | Alineación |
| --- | --- | --- | --- |
| 1 | `dominio` | Dominio | izq |
| 2 | `f_carga` | Fecha | izq |
| 3 | `ano_y_mes` | Año y Mes | izq |
| 4 | `km_inicio` | Km. Inicio | der |
| 5 | `km_fin` | Km. Fin | der |
| 6 | **`km_fin - km_inicio`** | Km. Recorridos | der |
| 7 | `interno` | Interno | der |
| 8 | `u_create` | U. Creó | — |
| 9 | `u_modify` | U. Modificó | — |

## Filtro (`bFiltro.Click`)

```foxpro
IF optiongroup1.option1 = 1   && "por Vehiculos"
    SELECT * FROM vehiculo_km WHERE cDominio = dominio AND BETWEEN(f_carga, dFecha, hFecha) ...
ELSE                           && "todos los Vehiculos"
    SELECT * FROM vehiculo_km WHERE BETWEEN(f_carga, dFecha, hFecha) ...
ORDER BY dominio, f_carga DESC
```

- Optiongroup **por Vehículos** (default, exige cargar dominio) / **todos los Vehículos**.
- Buscador de dominio con **F5** (`vehiculo_busca`) + autocompletar que sale de
  `SELECT dominio FROM vehiculo WHERE activo AND uso = 'PROPIO' ORDER BY dominio`.
- Rango de fechas sobre `f_carga`.
- Botón **Excel** = `COPY TO ... xls` del cursor filtrado.
- Botones **Agregar / Eliminar / Modificar** abren `vehiculo_km_abm.scx` (ALTA/BAJA/MODIFICA).

> ⚠️ **La ASIGNACIÓN de Tráfico también escribe `vehiculo_km`** (primer odómetro del mes →
> INSERT + cierra `km_fin` del mes anterior) — ver `trafico/TRAFICO2_TOOLBAR.md` §2.2. Por eso
> la tabla cambia de dueño con el circuito `viaje` el **día D** (plan Buslink), no como catálogo
> suelto. Hasta entonces: solo lectura desde Blazor.

---

## Migración a Blazor (`Odometros.razor`)

Réplica fiel de la grilla + KPIs de valor agregado (decidido con el usuario 04/07/2026):

- **Filtros idénticos:** radio *Por vehículo* (autocomplete de dominio, flota propia activa,
  vía `GetDominiosFlotaPropiaAsync`) / *Todos los vehículos* + Desde/Hasta (`f_carga`) + botón
  Filtro. Default = del 1º de hace 2 meses a hoy (el odómetro se cierra con retraso; el mes en
  curso tiene `km_fin` NULL). Botón **Actualizar** (invalida caché) y **Excel**.
- **KPIs:** Lecturas · Unidades (dominios distintos) · Km recorridos (suma) · Sin cierre
  (lecturas con `km_fin` NULL).
- **Grilla** (con `<Virtualize>`): las 9 columnas del FoxPro. `Año y Mes` se muestra formateado
  `MM/AAAA` (de `AAAAMM`). **Km Recorridos** solo se calcula cuando `km_inicio` Y `km_fin`
  existen y `km_fin >= km_inicio` (protección contra odómetro incoherente); si no, `—`.
- Botonera ABM deshabilitada (solo lectura).

### Método `ReportService`
- `GetOdometrosAsync(dominio?, desde, hasta)` — grilla filtrada (dominio vacío = todos).
- `GetDominiosFlotaPropiaAsync()` — dominios para el buscador (`vehiculo` PROPIO activo).
- Export: `ExcelExportService.Odometros(filas, desde, hasta)`.

### Validado (04/07/2026)
Default may–jul 2026: **203 lecturas / 106 unidades / 105 sin cierre / 1.990.053 km recorridos**
(idéntico a SQL). Smoke test en la suite.
