# Siniestros — `siniestro.scx` + `siniestro_abm.scx`

> Pendiente. Form **gigante** (~70 columnas): parte de accidente completo con croquis,
> terceros y testigos.

- **Tabla:** `siniestro` (313 filas).
- **Claves:** `id_chofer`, `id_vehicul`, `interno`, `id_viaje` (a qué viaje pertenece),
  `tipo_acc`, `fecha`, `hora`, `lugar`, `localidad`, `provincia`, `comisaria`.

## Grupos de campos

- **Asegurado (vehículo NORTUR):** `velocidad`, condiciones (`visible`, `bocina`, `lluvia`,
  `luces`, `mano_unica`), conductor (`conductor`, `edad`, `registro_n/v`, `tdoc`/`ndoc`),
  daños (`aseg_delan/later/trase`), seguro (`seguro`, `seguro_nom`, `seguro_pol`).
- **Tercero:** `dominio`, `marca_y_mo`, `tipo`, `ano`, propietario (`propietari`..`propietar6`),
  daños del otro (`otro_delan/later/trase`), `conductor_`/`conductor2..5`, `circula`.
- **Testigos 1-3:** `test_N_nom`, `test_N_tdo`, `test_N_ndo`, `test_N_tel`, `test_N_cel`.
- **Descripción:** `descripcio` (memo, nvarchar(max)). **Auditoría:** `usuario_cr/de/mo`,
  `f_ingreso`, `f_envio`.

## Mapeo a Blazor

Por el volumen de campos, ficha con varias secciones (Datos del hecho / Vehículo asegurado /
Tercero / Testigos / Descripción). Solo lectura primero; ABM solo si el cliente lo necesita.
`visible` (bit) parece flag de visibilidad en grilla.
