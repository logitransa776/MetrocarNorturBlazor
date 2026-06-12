# Lógica FoxPro — Importa Reservas desde Excel (`importa_excel_viaje.scx`)

> Menú: **Reservas → Importa Reservas desde Excel**.
> Carga masiva de reservas desde un .xls con estructura fija de 28 columnas.
> Usa OLE Automation de Excel (requiere Excel instalado). Extraído 12/06/2026.

---

## Flujo general

```
[Archivo] elegir .xls
   ↓
Página 1 "Analisis de Problemas" → [Importa] = SOLO VALIDA (3 etapas)
   errores → grilla problem_i (columna, fila, campo, problema, dato) + [Imprime]
   ↓ sin errores
Página 2 "Datos a Importar" → grilla reserva_i (39 col) + contador
   → [Pasa viajes a base de reservas] = INSERT masivo a viaje (CON transacción)
```

Dos cursores temporales (tablas free en el TEMP):
- `problem_i (columna, fila, campo, problema, dato)` — errores de validación.
- `reserva_i` — staging con las 28 columnas + adicionales resueltos.

## Estructura obligatoria del Excel (28 columnas, fila 1 = encabezados exactos)

| # | Encabezado | Tipo | Oblig. | Validación de contenido |
|---|---|---|---|---|
| 1 | CLIENTE | char | ✔ | existe en `cliente` (padded a 15) **y tiene CUIT** |
| 2 | FECHA | fecha | ✔ | **≥ hoy** |
| 3 | HORA | fecha/hora | ✔ | — |
| 4 | SERVICIO1 | char | ✔ | existe en `servicio` no borrado |
| 5 | SERVICIO2 | char | — | ídem si viene |
| 6 | SERVICIO3 | char | — | ídem |
| 7 | VEHICULO | char | ✔ | existe en `vehiculo_tipo` |
| 8 | DESDE | char | ✔ | — |
| 9 | HASTA | char | ✔ | — |
| 10 | GRUPO | char | ✔ | — |
| 11 | F_FIN_GPO | fecha | ✔ | — |
| 12 | VUELO | char | — | — |
| 13 | GUIA | char | — | — |
| 14 | OBS | char | — | — |
| 15 | PAX | num | ✔ | **≤ capacidad del vehículo** (`vehiculo_tipo.pax`) |
| 16–25 | ADI_COD_n / ADI_CAN_n (×5) | char/num | — | adicional existe; cantidad default 1; precio y nombre se toman de `adicional` |
| 26 | MONEDA | char | — | existe en `moneda_tipo`; si viene exige importe > 0 |
| 27 | IMPORTE | num | — | — |
| 28 | PRESENTACION | num | ✔ | ∈ {0, 5, 15, 30, 45, 60, 120} (minutos antes) |

### Etapas de validación (cada una corta si encuentra errores)

1. **Faltantes**: columnas obligatorias sin blancos/NULL.
2. **Tipos**: carácter / fecha / numérico por columna (VARTYPE de la celda).
3. **Consistencia**: lookups contra catálogos (cliente+CUIT, servicios, vehículo,
   pax vs capacidad, adicionales, moneda+importe, presentación, fecha ≥ hoy).
4. Pasa a `reserva_i` (`arma_reserva`) y un 4º control: un mismo GRUPO **no puede tener
   F_FIN_GPO distintas** entre filas → cancela.

## Grabación (página 2)

- Confirmación → **toma lote** (`parametro.lote_plant + 1`, mismo contador que plantillas)
  → `viaje.lote`.
- **BEGIN TRANSACTION … END TRANSACTION con TRY/CATCH y ROLLBACK** (único alta de reservas
  del módulo que es transaccional).
- Por cada fila de `reserva_i`:
  - `hs_inicio` = datetime(fecha + HH:MM); `hs_present` = hs_inicio − minutos (0 = vacío);
    `hs_fin` = hs_inicio + Σ duraciones de los servicios (`arma_hora_fin`).
  - Guía: vacía → "SIN GUIA"/dueño `'S'`; con valor → id "GUIA CLIENTE", dueño `'C'`.
  - Vuelo vacío → "SIN VUELO". Grupo vacío → "SIN GRUPO" + f_grupo_fin = fecha.
  - **Grupo con valor**: busca/crea en `cliente_grupo` (igual que la carga manual) →
    `id_grupo`. ⚠️ No extiende `f_grupo_fin` de grupos existentes (a diferencia de la
    carga manual).
  - `INSERT INTO viaje`: `origen = 'T'`, `estado_viaje = 'SIN ASIGNAR'`,
    `cronograma = cronogramacbio = 'S/C'`, `str_f_reserva = DTOS(fecha)`, lote,
    f_pedido = hoy, hs_salida = hs_inicio, servicios 1/2/3, cliente + nombre, comentario
    (OBS), d/h_destino, id_vehiculo_tipo, guía (id/nombre/dueño), grupo + f_grupo_fin +
    id_grupo, vuelo, pax, `moneda_convenida`, `importe_convenido`, `hs_presentacion`,
    `f_create`, `u_create`,
    y **los 5 slots de adicionales INLINE en `viaje`** (`adi_cod_n`, `adi_can_n`,
    `adi_nom_n`, `adi_pre_n`, `adi_imp_n = can × pre`) — ⚠️ acá NO usa `viaje_adicional`,
    a diferencia de la carga manual y de plantillas.
  - **No graba `viaje_log`** (otra diferencia).
- Mensaje final: lote + rango de reservas generadas (desde Nº / hasta Nº) y cierra el form.

## Reglas no obvias

1. Es el único alta **transaccional** del módulo (manual y plantillas no lo son).
2. Los adicionales van **inline en `viaje`** (slots `adi_*`), no en `viaje_adicional`.
   Cualquier informe de adicionales debe mirar AMBOS lados.
3. `m_pres` numérico reemplaza el combo de presentación (mismos valores en minutos).
4. La fila de encabezados debe matchear EXACTO (mayúsculas) y deben ser exactamente
   28 columnas usadas — si no, rechaza el archivo entero.
5. No valida duplicados contra reservas ya existentes — importar dos veces el mismo
   archivo duplica todo (solo cambia el lote).
6. Comparte el contador de lotes con la generación por plantillas.
