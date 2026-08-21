# Propuesta de informes nuevos — Buslink

> **Fecha:** 10/08/2026 · **Estado:** propuesta, ninguno construido.
> **Alcance:** informes que NO existen en Buslink **ni en el FoxPro** — no son migraciones,
> son preguntas que hoy nadie puede contestar con ninguno de los dos sistemas.
> Todos los números de este documento están **medidos sobre `replicaVPF`** (año 2026,
> `_deleted = 0`), no estimados. Las queries de verificación están al pie.

---

## 1. Diagnóstico: qué contesta Buslink hoy y qué no

El catálogo (`Services/InformesCatalogo.cs`) tiene **17 informes disponibles** y 4 marcados
*Próximamente*. Repartidos así:

| Módulo | Disponibles |
| --- | --- |
| Reservas | 3 |
| Tráfico | 2 |
| Flota | 4 |
| Facturación | 4 |
| Combustible | 3 |
| Sistema | 1 |

Vistos juntos, los 17 contestan variantes de **dos** preguntas: *cuánto volumen* (reservas,
pax, km) y *cuánta plata*, agregados por entidad (cliente, servicio, chofer, unidad). Es una
cobertura buena de **el qué**, y nula de **el cómo**.

### Las cinco dimensiones que ningún informe toca

| Dimensión | Evidencia en la base (2026) | Informes que la usan |
| --- | --- | --- |
| **Tiempo real de operación** | 53.036 viajes con `hs_inicio` **y** `hs_fin`. Duración real promedio: **91 min** | 0 |
| **Tercerización** | `viaje.fletero` poblado al 90%: NORTUR 43.295 vs terceros 9.767 | 0 |
| **Quién opera el sistema** | `u_create` poblado al **100%**. LEONARDO: 47.578 altas = **77% del total** | 0 |
| **Calidad del despacho** | `chequeo = 1` en 115.110 de 172.301 (67%). EZEIZA al **48%**, CABECERA_KM al **73%** | 0 |
| **Plata a nivel viaje** | `liquidacion_detalle.id_viaje` permite bajar el ingreso a la unidad y al servicio | 0 (la plata solo se ve por cliente) |

---

## 2. Trampas de datos verificadas

Condicionan el diseño de cualquier informe nuevo. **Confirmadas con `COUNT`, no supuestas.**

| Campo | Realidad medida | Consecuencia |
| --- | --- | --- |
| `viaje.total`, `.importe`, `.descuento`, `.hext`, `.imp_liq`, `.adi_*` | **0 filas con valor ≠ 0** en 2025-2026 | La plata vive **solo** en `liquidacion_detalle`. No hay costo de chofer en la base |
| `viaje.km_recorri` | **3 filas** pobladas | Inservible. Usar `viaje.km` (82% poblado) |
| `viaje.hs_present` | 13.571 de 172.301 (8%) | **No se puede medir puntualidad de presentación**. Descartar la idea |
| `viaje.hs_fin_apr` | 100% poblado, pero es `inicio + 2h` fijo en ~30% de los casos (ver memoria `hs-fin-apr-default-2h`) | Es un **presupuesto**, no un hecho. Sirve como línea base contra la cual medir el desvío — nunca como duración real |
| `viaje.id_operado` | **3 filas** pobladas | El operador comercial no se puede analizar desde `viaje` |
| `viaje.f_modify` en cancelados | 1.967 de 2.304 cancelados de 2026 **sin `f_modify`** | La anticipación de la cancelación solo se puede medir en el 15% de los casos. **Idea descartada** |
| `CABECERA_KM` / `CABECERA_SERV` | ~74% del volumen | Son modos de facturación, no servicios. Excluir o segmentar siempre (memoria `cabeceras-no-son-servicios`) |

---

## 3. Las seis propuestas

Cada una sigue la regla vigente de CLAUDE.md § 7: patrón dashboard completo (filtros
compactos, KPIs, ApexCharts con color por entidad, pivote con drill-down, Excel multi-hoja y
**cross-filter**). El alta en el hub es **una línea** en `InformesCatalogo.cs`.

---

### 3.1 · Panel del Operador (auditoría de carga) — `Sistema`

> ✅ **CONSTRUIDO el 11/08/2026** — vive en `/panel-operador`, bajo Informes → **En desarrollo**
> (permiso `S`) hasta que el cliente lo apruebe. Lo que sigue es el alcance **real**, ya
> corregido contra lo que los datos permiten: dos de las cuatro ideas originales resultaron
> imposibles y se reemplazaron (ver el recuadro al pie).

**Pregunta:** ¿Quién carga el trabajo, cuándo, y quién modifica lo que cargó otro?

**Fuente:** `viaje.u_create`, `f_create`, `u_modify`, `f_modify` — los cuatro poblados.
Una sola tabla, sin joins. Es el más barato de los seis.

**Contenido:**
- Ranking de altas por operador y evolución diaria/mensual de la carga
- **Antelación**: días entre que se cargó la reserva y la fecha del viaje, y las cargas
  *retroactivas* (cargadas después de que el viaje ocurrió)
- Matriz *quién modificó lo de quién* — el indicador de fricción entre operadores
- Concentración: % de la carga en el operador top, con el mix de clientes al lado —
  es lo que distingue "una persona hace todo" de "a una persona le tocó el contrato grande"
- Calidad de lo cargado: % cancelado y % sin asignar por operador
- Control: operadores con cargas que **no figuran** en el padrón de Usuarios

> ⚠ **Dos ideas de la propuesta original NO se pudieron hacer** (medido 11/08/2026):
> **la curva horaria de carga** —`f_create` es `date`, sin hora, y `_created_at` es el
> timestamp de la réplica (todas las filas comparten el mismo instante de importación)— y
> **la latencia de asignación**, porque no hay historial de estados: no existe registro de
> *cuándo* una reserva pasó a ASIGNADO. Se reemplazaron por la antelación y la calidad de
> lo cargado, que son mejores y sí tienen respaldo en el dato.

**Por qué importa dos veces:**
1. **Hoy, como gestión.** LEONARDO cargó el **77%** de las reservas de 2026 (47.578 de ~62.000).
   OSVALDO el 20%. Los otros nueve usuarios se reparten el 3%. Es un riesgo de continuidad
   que el dueño no tiene medido.
2. **Después del día D, como control.** Cuando Buslink pase a escribir el circuito `viaje`,
   este informe es la caja negra que contesta *"¿quién tocó esto?"*. Conviene tenerlo
   **antes** del corte, para tener línea base de cómo se cargaba en FoxPro.

**Esfuerzo:** bajo. **Riesgo:** ninguno (solo lectura, una tabla).

---

### 3.2 · Panel de Tercerización — Nortur vs Fleteros — `Flota`

**Pregunta:** ¿Cuánto de la operación se está dando a terceros, y cuánto podría absorber
la flota propia?

**Fuente:** `viaje.fletero` (90% poblado). Valores 2026: `NORTUR` 43.295 · vacío 9.523 ·
`VANSQ` 3.066 · `MVTRAVEL` 2.368 · `NEUQUEN` 2.163 · `TEB` 1.221 · `TEDESCHI` 505 ·
`MASIMIGLIA` 381 · y una cola menor.

> ⚠ Este es el campo correcto para separar flota propia de contratada. La regla FoxPro
> "interno ≥ 1000 = contratado" está **rota** (memoria `contratado-no-es-interno-1000`).

**Contenido:**
- % tercerizado por mes, cliente, servicio y tipo de unidad; km y pax tercerizados
- Ranking de fleteros con tendencia mes a mes
- **El cruce que lo justifica:** días y franjas donde se contrató afuera **teniendo unidades
  propias libres** — reutilizando el modelo de `Services/OcupacionFlota.cs`, sin query nueva

**Por qué importa:** es el informe de más impacto económico directo de los seis, y engancha
con lo que ya destapó el Panel de Flota (33% de la demanda sin cubrir con flota propia).

**Limitación a declarar en pantalla:** se mide **volumen**, no costo. La liquidación a
fleteros está muerta desde el 21/12/2023 (auditoría 09/08/2026), así que no hay contra qué
valorizar lo tercerizado.

**Esfuerzo:** medio. **Riesgo:** bajo.

---

### 3.3 · Panel de Puntualidad y Duración Real — `Tráfico`

**Pregunta:** ¿Qué servicios se van sistemáticamente del tiempo presupuestado?

**Fuente:** `hs_inicio` → `hs_fin` (duración real, 53.036 casos en 2026, promedio 91 min)
contra `hs_fin_apr` (duración presupuestada).

**Contenido:**
- Desvío promedio y mediano (real − presupuestado) por servicio, cliente, chofer y franja
- Distribución de duraciones por servicio (dónde está la cola larga, no solo el promedio)
- Los servicios que se pasan **siempre** vs los que se pasan a veces
- Filtro que aísle los casos con `hs_fin_apr` = default de 2 h, que son los que más ruido meten

**Por qué importa:** es munición comercial concreta — *"el CITY que te facturo como 3 h me
ocupa 4"*. Y es la primera métrica de tiempo real que va a tener la empresa.

**Cuidado de diseño:** el informe **debe** explicar en pantalla que `hs_fin_apr` es un
presupuesto con default, o el usuario va a leer el desvío como error de los choferes.

**Esfuerzo:** medio. **Riesgo:** medio — el valor depende de comunicar bien la limitación.

---

### 3.4 · Tablero de Cumplimiento Operativo (chequeo y avisos) — `Tráfico`

**Pregunta:** ¿Qué servicios están saliendo sin chequear?

**Fuente:** `viaje.chequeo`, `chequeo_ag`, `hs_aviso` (el motor de avisos F4 ya está vivo en
Buslink — memoria `trafico-f4-avisos`).

**Contenido:**
- % chequeado por servicio, franja horaria, día de semana y operador
- Listado de los que se escaparon, exportable, para revisión del jefe de tráfico
- Evolución mensual del cumplimiento

**El hallazgo que lo dispara** (2026, hasta 10/07):

| Servicio | Viajes | Chequeados | % |
| --- | --- | --- | --- |
| CABECERA_KM | 36.058 | 26.464 | 73% |
| CABECERA_SERV | 9.280 | 5.654 | 61% |
| TRASLADO | 2.296 | 1.351 | 59% |
| **EZEIZA** | 1.896 | 914 | **48%** |
| GUARDIA8 | 1.579 | 934 | 59% |
| AEROPARQUE | 1.353 | 812 | 60% |
| CITY | 1.060 | 428 | 40% |
| CENA SHOW | 635 | 229 | 36% |

Que los servicios de aeropuerto —los más sensibles, con vuelo de por medio— chequeen **por
debajo** de las cabeceras es exactamente el tipo de hallazgo por el que después piden el
informe formal.

**Esfuerzo:** bajo-medio. **Riesgo:** bajo.

---

### 3.5 · Rentabilidad por unidad y por servicio — `Facturación`

**Pregunta:** ¿Qué unidad y qué servicio dejan plata?

**Fuente:** `liquidacion_detalle` (tiene `id_viaje` → se puede bajar el ingreso al viaje, y
de ahí a la unidad por `viaje.id_vehicu2`) cruzado con combustible por unidad
(`vehiculo_sobre`) y km (`viaje.km`).

**Contenido:**
- Ingreso, litros, km y contribución por unidad, por servicio y por mes
- $/km e ingreso por unidad disponible
- Ranking de unidades y servicios por contribución

> 🔴 **Advertencia obligatoria en pantalla:** `viaje.imp_liq` (costo de chofer) está en **0
> en toda la tabla**. El margen que se puede calcular es **contribución antes de mano de
> obra**, y hay que rotularlo así. Vender esto como "rentabilidad" a secas sería mentirle
> al dueño.

**Además:** la facturación hay que calcularla del **detalle**
(`importe + incremento − descuento`, moneda por línea), no de `liquidacion.total`, que tiene
cargas corruptas (memoria `panel-clientes-informe`).

**Esfuerzo:** alto. **Riesgo:** alto — es el que más se puede equivocar y el que peor se
perdona si da un número mal.

---

### 3.6 · Curva de demanda y dimensionamiento de flota — `Flota`

**Pregunta:** ¿Cuántas unidades necesito de verdad un martes a las 7 de la mañana?

**Fuente:** `hs_inicio` / `hs_fin_apr` de todo un rango, no de un día. Extiende el tablero de
ocupación (`Services/OcupacionFlota.cs`), que hoy es de una sola fecha.

**Contenido:**
- Simultaneidad de servicios en bandas de 15 min, promediada por día de semana y mes
- Pico de demanda contra flota disponible → dónde estructuralmente no se llega
- Heatmap día de semana × hora
- Cruce con 3.2: los picos no cubiertos son los que se están tercerizando

**Por qué importa:** es el informe que justifica comprar (o no comprar) una unidad, y el más
vistoso para mostrarle al dueño.

**Cuidado:** usa `hs_fin_apr` para el fin (es lo único disponible a futuro), con el sesgo del
default de 2 h → **infla la ocupación ~55%**. Hay que mostrar la banda optimista/pesimista,
no un número solo.

**Esfuerzo:** medio-alto. **Riesgo:** medio.

---

## 4. Orden recomendado

| # | Informe | Esfuerzo | Impacto | Por qué en ese lugar |
| --- | --- | --- | --- | --- |
| 1 | **Panel del Operador** ✅ **hecho** | Bajo | Alto | Barato, sin riesgo, y conviene tener la línea base **antes** del día D |
| 2 | **Panel de Tercerización** | Medio | Muy alto | El de más impacto económico directo |
| 3 | **Puntualidad y Duración Real** | Medio | Alto | Primera métrica de tiempo real de la empresa; argumento comercial |
| 4 | **Curva de demanda** | Medio-alto | Alto | El más vistoso; se apoya en 3.2 |
| 5 | **Cumplimiento Operativo** | Bajo-medio | Medio | Barato, pero es control interno: menos vendible hacia afuera |
| 6 | **Rentabilidad por unidad** | Alto | Alto | Último a propósito: el que peor se perdona si da mal |

---

## 5. Descartados, y por qué

| Idea | Motivo |
| --- | --- |
| **Anticipación de cancelaciones** | 1.967 de 2.304 cancelados de 2026 no tienen `f_modify`. Solo se podría medir el 15% |
| **Puntualidad de presentación** | `hs_present` poblado al 8% |
| **Lead time de reserva** | El 78% cae en "2-7 días" porque lo generan las plantillas: mide el proceso interno, no al cliente |
| **Apercibimientos por chofer** | `chofer_sancion` con 0 filas (auditoría 09/08/2026) |
| **Liquidación a fleteros** | Muerta desde el 21/12/2023 |

## 6. Bonus baratos (fuera del top 6)

- **Panel de Choferes** — gemelo del Panel de Flota: quién trabaja y quién no, francos,
  antigüedad, registro por vencer, siniestros. Todas las tablas ya están migradas, así que es
  casi todo ensamblado.
- **Informe de vuelos** — `viaje.vuelo` está poblado al 98% con códigos reales (AR1775,
  LA426…), aunque 57.997 de 2026 son `SIN VUELO`. Nicho, para la operación de aeropuerto.

---

## 7. Queries de verificación

Todo lo afirmado acá se reproduce con estas consultas (server local `DESKTOP-CV6LF0O\SQLEXPRESS`).
Recordar: literales de fecha en formato **`'yyyyMMdd'`** sin guiones (memoria
`fechas-sql-formato-yyyymmdd`), y el server nuevo es **SQL Server 2012** (sin `STRING_AGG`,
`TRIM`, `CONCAT_WS`).

```sql
-- Población de campos (cambiar el campo en el COUNT)
SELECT COUNT(*) total, COUNT(NULLIF(LTRIM(u_create),'')) poblado
FROM viaje WHERE _deleted = 0 AND f_reserva >= '20250101' AND f_reserva < '20270101';

-- Concentración de carga por operador
SELECT TOP 12 u_create, COUNT(*) n, MIN(f_create) desde, MAX(f_create) hasta
FROM viaje WHERE _deleted = 0 AND f_reserva >= '20260101'
GROUP BY u_create ORDER BY 2 DESC;

-- Tercerización
SELECT TOP 10 fletero, COUNT(*) n
FROM viaje WHERE _deleted = 0 AND f_reserva >= '20260101'
GROUP BY fletero ORDER BY 2 DESC;

-- Duración real
SELECT COUNT(*) n, AVG(DATEDIFF(minute, hs_inicio, hs_fin) * 1.0) prom
FROM viaje WHERE _deleted = 0 AND f_reserva >= '20260101'
  AND hs_fin IS NOT NULL AND hs_inicio IS NOT NULL;

-- Cumplimiento de chequeo por servicio
SELECT TOP 8 id_servici, COUNT(*) n, SUM(CASE WHEN chequeo = 1 THEN 1 ELSE 0 END) chequeados
FROM viaje WHERE _deleted = 0 AND f_reserva >= '20260101' AND f_reserva < '20260710'
GROUP BY id_servici ORDER BY 2 DESC;
```
