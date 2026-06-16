# Agenda de Vencimientos — `agenda_vencimiento.scx`

> Pendiente. **Es un INFORME, no un ABM** (no tiene tabla propia). Alto valor operativo,
> sin escritura → buen candidato temprano. Ya existe la versión resumida en el Home (`TableroDto`).

Cruza dos fuentes con días de anticipación configurables (de `parametro`):

## Choferes vencidos / por vencer

```sql
SELECT *,
  IIF(registro_vto      <= DATE()+nDiasDifCho, 1, 0) AS registro,
  IIF(registro_vto_cnrt <= DATE()+nDiasDifCho, 1, 0) AS cnrt,
  IIF(registro_vto_aeo  <= DATE()+nDiasDifCho, 1, 0) AS aeo
FROM chofer
WHERE EMPTY(f_delete)
  AND (registro_vto      <= DATE()+nDiasDifCho
    OR registro_vto_cnrt <= DATE()+nDiasDifCho
    OR registro_vto_aeo  <= DATE()+nDiasDifCho)
ORDER BY id_chofer
```
SQL real: `registro_vto`→`registro_v`, `registro_vto_cnrt`→`registro_3`, `registro_vto_aeo`→`registro_4`.
Celda en **rojo** (fondo) cuando vence dentro del umbral.

## Vehículos vencidos / por vencer (solo flota propia)

```sql
SELECT *,
  IIF(verificacion_vto <= DATE()+nDiasDifVeh, 1, 0) AS xverif,
  IIF(vencimiento_mat  <= DATE()+nDiasDifMat, 1, 0) AS xmat
FROM vehiculo
WHERE EMPTY(f_delete)
  AND (verificacion_vto <= DATE()+nDiasDifVeh OR vencimiento_mat <= DATE()+nDiasDifMat)
  AND uso = 'PROPIO'
ORDER BY interno
```
SQL real: `verificacion_vto` (VTV)→`verificac2`, `vencimiento_mat` (matafuegos)→`vencimient`.

## Parámetros (de `parametro`)

`nDiasDifCho` (días previos choferes), `nDiasDifVeh` (técnica/VTV), `nDiasDifMat` (matafuegos).
Botones "Modifica Vehículo" / "Modifica Chofer" abren los ABM respectivos.

## Mapeo a Blazor

Dos grillas (Choferes / Vehículos) con celdas de vencimiento en rojo/ámbar. Reusa la lógica
de `TableroDto` del Home (mismos campos). Sin escritura. Título: "Vencimientos al <fecha>".
