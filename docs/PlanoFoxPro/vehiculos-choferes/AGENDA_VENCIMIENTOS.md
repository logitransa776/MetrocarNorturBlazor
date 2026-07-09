# Agenda de Vencimientos — `agenda_vencimiento.scx`

> **Migrado a Blazor (solo lectura / informe) — 05/07/2026.**
> Página `Components/Pages/AgendaVencimientos.razor` (`/agenda-vencimientos`), permiso `'V'`,
> menú **Vehículos y Choferes → Agenda de Vencimientos**. Es un INFORME (sin escritura): cruza
> `chofer` (registros) y `vehiculo` propio (VTV/matafuegos) por umbrales de anticipación.

## Qué hace el FoxPro (extraído de `agenda_vencimiento.scx`, verificado)

Dos cursores, uno por grilla, con umbrales que salen de `parametro`:

```foxpro
* Umbrales (public), leídos de parametro en el Init:
nDiasDifCho = cursorParametro.aviso_cho   && choferes  (30 en la base actual)
nDiasDifVeh = cursorParametro.aviso_veh   && VTV       (7)
nDiasDifMat = cursorParametro.aviso_mat   && matafuegos (10)

* Cursor 1 — choferes:
SELECT *, IIF(registro_vto      <= DATE()+nDiasDifCho, 1, 0) AS registro,
          IIF(registro_vto_cnrt <= DATE()+nDiasDifCho, 1, 0) AS cnrt,
          IIF(registro_vto_aeo  <= DATE()+nDiasDifCho, 1, 0) AS aeo
  FROM chofer
  WHERE EMPTY(f_delete)
    AND (registro_vto <= DATE()+nDiasDifCho
      OR registro_vto_cnrt <= DATE()+nDiasDifCho
      OR registro_vto_aeo  <= DATE()+nDiasDifCho)
  ORDER BY id_chofer INTO CURSOR cursorChofer

* Cursor 2 — vehículos (solo flota propia):
SELECT *, IIF(verificacion_vto <= DATE()+nDiasDifVeh, 1, 0) AS xverif,
          IIF(vencimiento_mat  <= DATE()+nDiasDifMat, 1, 0) AS xmat
  FROM vehiculo
  WHERE EMPTY(f_delete)
    AND (verificacion_vto <= DATE()+nDiasDifVeh OR vencimiento_mat <= DATE()+nDiasDifMat)
    AND uso = "PROPIO"
  ORDER BY interno INTO CURSOR cursorVehiculo
```

- **Celda en ROJO** (`DynamicBackColor RGB(255,0,0)`) cuando el flag (`registro`/`cnrt`/`aeo`/
  `xverif`/`xmat`) = 1, es decir cuando ese vencimiento cae dentro del umbral. El resto en blanco.
- El título del form es `"Vencimientos al " + DTOC(DATE())`.
- Botones "Modifica Vehículo" / "Modifica Chofer" abrían los ABM respectivos (no migrados aquí:
  la edición sigue en las fichas de Chofer y Vehículo).

## Mapeo de columnas (réplica trunca a 10 chars)

| Form FoxPro (largo)   | Columna SQL real | Grilla        |
|-----------------------|------------------|---------------|
| `registro_vto`        | `registro_v`     | Registro      |
| `registro_vto_cnrt`   | `registro_3`     | CNRT          |
| `registro_vto_aeo`    | `registro_4`     | AEP           |
| `verificacion_vto`    | `verificac2`     | VTV           |
| `vencimiento_mat`     | `vencimient`     | Matafuegos    |
| `nombre` / `id_chofer`| iguales          | Nombre/Chofer |
| `interno` / `dominio` | iguales          | Interno/Dominio|

Parámetros de `parametro`: `aviso_cho` / `aviso_veh` / `aviso_mat` — **son `bigint`** en la
réplica → `CAST(... AS int)` en el SELECT (si no, `GetInt32` tira `InvalidCastException`). Los
valores actuales: 30 / 7 / 10.

## Decisiones vs FoxPro (Blazor, 05/07/2026)

- **NULL cuenta como vencido.** El FoxPro compara `registro_vto <= DATE()+n`; en FoxPro una fecha
  vacía es "cero" y siempre `<= hoy`. En SQL se replica con `ISNULL(campo,'1900-01-01') <= @lim`,
  y en la grilla la fecha faltante se pinta "sin fecha" en rojo.
- **Selector de anticipación con dos modos:**
  1. **"Según parámetros del sistema"** (default, **fiel al FoxPro**): cada tipo con su umbral
     (chofer `aviso_cho`, VTV `aviso_veh`, matafuegos `aviso_mat`).
  2. **Umbral uniforme** (0/15/30/60/90) para los tres — conveniencia para "ver con más
     anticipación". Con 0 = solo lo vencido a hoy.
- **Rojo (vencido / sin fecha) + ámbar (por vencer)** — el FoxPro solo tenía rojo (dentro del
  umbral) vs blanco. Se agregó el ámbar para distinguir "ya venció" de "vence pronto" (valor
  sobre el original). Clases CSS reusadas: `chof-vto--vencido` / `chof-vto--proximo`.
- **KPIs** (no estaban en el FoxPro): choferes a revisar, registros vencidos, vehículos a revisar,
  VTV/matafuego vencido. **Excel** de 2 hojas (Choferes / Vehículos) con el sombreado por celda.

## Validación (05/07/2026)

Con los umbrales del sistema (chofer 30 / VTV 7 / mataf. 10) sobre `replicaVPF` local: **249
choferes** a revisar y **145 vehículos** propios — idéntico al `SELECT COUNT(*)` directo. Trampa
real de datos: muchos choferes tienen CNRT = `31/12/2099` (fecha "sin vencimiento") y aparecen en
la lista solo porque su registro o AEP sí está próximo; la celda 2099 sale en ámbar (no vencida),
correcto. Hay vehículos propios activos que **comparten `interno`** (interno 1 = 4 dominios) — es
dato real de la base, el FoxPro los muestra igual (ordena por interno y lista todos).
