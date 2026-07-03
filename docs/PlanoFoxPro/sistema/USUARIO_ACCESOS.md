# Seguridad y permisos del Metrocar — tabla `usuario` (campos `acceso`, `nivel`, `operador`)

> Fuentes analizadas: `Forms/login.scx`, `Forms/usuario_abm.scx`, `Menus/MENU_PRINCIPAL.MPR`,
> `Menus/MENU_PRINCIPAL_C_CC.MPR`, `Forms/trafico2.scx`, `Forms/trafico3.scx`,
> `Forms/trafico_zoom.scx`, `Forms/reserva_transportacion*.scx`, `Forms/cliente.scx` (ejemplo ABM).
> Fecha de extracción: 11/06/2026.

## Modelo general (3 niveles de permiso)

El Metrocar tiene **tres mecanismos de permisos independientes**, todos cargados al login
(`login.scx`) como **variables públicas globales**:

```foxpro
cUsuario               = cursorUsuario.usuario
cNivel                 = cursorUsuario.nivel      && dígitos → botones ABM
cAcceso                = cursorUsuario.acceso     && letras  → menús/módulos
lOperadorMesaDeTrafico = cursorUsuario.operador   && bit     → rol operador de tráfico
```

| Mecanismo | Campo | Formato | Qué controla |
| --- | --- | --- | --- |
| **Módulos** | `acceso` | string de letras (flags) | Qué menús/pantallas ve el usuario |
| **Operaciones ABM** | `nivel` | string de dígitos | Botones Agregar (`2`) / Modificar (`3`) / Eliminar (`4`) |
| **Rol operador** | `operador` | bit | "Cumple tareas de operador de la mesa de tráfico" |

### Validación del login (`login.scx`)

1. `SELECT * FROM usuario WHERE usuario = cUsuario` — si no existe → "Usuario Inexistente".
2. Si `f_delete` no está vacío → **"Usuario Inhabilitado"** (baja lógica, no hay DELETE físico).
3. Compara password **en texto plano** (`Allt(form.Password) = Allt(cursorUsuario.Password)`).
4. Carga las 4 variables públicas y ejecuta `menu_principal.mpr`.
5. Extras post-login según `acceso`:
   - `"V" $ cAcceso` → corre los chequeos de **vencimientos** (chofer REG/CNRT/AEO, vehículo).
   - `"B" $ cAcceso` → abre directo el form **Backup** (usuario de servicio).
   - sino `"H" $ cAcceso` → abre el form **Scheduler**.
6. Control de sesión única vía tabla `login` (IP, hostname, inicio/fin).

## Campo `acceso` — una letra = un módulo

Es un **bitmask en forma de string**: cada letra presente habilita un módulo. El chequeo
universal es `"X" $ cAcceso` (operador `$` = "contiene"). El menú principal usa
`SKIP FOR !("letra" $ cAcceso)` — el ítem queda **deshabilitado (gris)**, no oculto.

El string lo arma `usuario_abm.scx` (Sistema → Accesos) concatenando en este **orden fijo**:
`S R T C D V L F A E U B H X N M`.

### Mapa completo de letras (16)

| Letra | Checkbox en ABM ("Operaciones permitidas") | Qué habilita | Dónde se chequea |
| --- | --- | --- | --- |
| `S` | Usuarios y Password | **Sistema → Accesos** (ABM de usuarios) y **Utilitarios → Conectados al sistema** | menú |
| `R` | Reservas | Menú **Reservas** completo | menú |
| `T` | Trafico | Menú **Tráfico** completo + modo búsqueda por teclado en grilla | menú, `trafico2/3` |
| `C` | Avisos de chequeos | Timer de avisos de chequeo en pantalla Tráfico (cada 60 seg, si `parametro.aviso_chequeo = "S"`). Solo habilitable si `T` está tildado | `trafico2/3` |
| `D` | Diagramador | Funciones de **diagramador** en Tráfico: normalizar cronograma, F5 = cambiar rango de fechas de trabajo, búsqueda U/Pr | `trafico2/3` |
| `V` | Vehiculos | Menú **Vehículos y Choferes** + chequeo de vencimientos al login | menú, `login` |
| `L` | Taller | Menú **Taller** | menú |
| `F` | Facturación | Menú **Facturación** + ver/editar **precios e importes**: botón Precio en reservas, campos importe/moneda/sin cargo/porcentaje en Zoom del Viaje | menú, `trafico_zoom`, `reserva_transportacion*` |
| `A` | ABM del Sistema | Menú **ABM del sistema** (catálogos: servicios, zonas, feriados, parámetros, etc.) | menú |
| `E` | Estadisticas | **⚠️ Flag muerto**: el checkbox existe pero ninguna pantalla ni menú del fuente lo chequea | — |
| `U` | Utilitarios | Menú **Utilitarios** | menú |
| `B` | Back - Up | Al login abre directo el form **Backup** (usuario de servicio tipo `BACKUP`, acceso = `"B"`) | `login` |
| `H` | Scheduler | **Utilitarios → Scheduler** + lo abre al login (si no tiene `B`) | menú, `login` |
| `X` | Tablero de comando | **Sistema → Tablero de Control**. Solo el usuario `SUPERVISOR` puede tildar este checkbox a otros | menú, `usuario_abm` |
| `N` | Cuentas Corrientes | **Facturación → Cuentas Corrientes** (solo existe en la variante `MENU_PRINCIPAL_C_CC.MPR`) | menú C_CC |
| `M` | Combustible y Consumos | Menú **Combustible** (ojo: la letra es `M`, no `C`) | menú |

### Trampas no obvias

- **`M` = Combustible** (no `C`). `C` = avisos de chequeos. La hotkey del menú es ALT+C
  pero el flag es `M`.
- **`L` = Taller** (de Ta**l**ler, hotkey ALT+L). No confundir con Liquidaciones
  (que viven dentro de Facturación = `F`).
- **`E` (Estadísticas) no hace nada** en el fuente actual. Puede que el `metrocar.exe`
  productivo (más nuevo que parte del fuente) sí lo use — verificar contra el sistema vivo.
- **`F` es doble**: además del menú Facturación, **gatea la visibilidad de precios**
  dentro de Reservas y del Zoom del Viaje. Un usuario sin `F` no ve ni toca importes.
- `C` depende de `T`: el ABM deshabilita el checkbox de avisos si Tráfico no está tildado.
- Mecanismo legacy muerto en `login.scx`: `nombre_nivel()` (niveles S/A/U/O/I) y
  `desactiva_menu()` (tabla `acceso_nivel`) — **nunca se llaman** y la tabla `acceso_nivel`
  ni siquiera existe en los DBF. Ignorar.

## Campo `nivel` — dígitos para botones ABM

Patrón en los 73 forms lista (`cliente.scx`, `chofer.scx`, etc.):

```foxpro
IF "2" $ cNivel   && botón Agregar    (78 chequeos en el fuente)
IF "3" $ cNivel   && botón Modificar  (56 chequeos)
IF "4" $ cNivel   && botón Eliminar   (64 chequeos)
* sin permiso → cartel("sin_permiso")
```

- Los dígitos `1` y `5` **no se chequean en ningún lado** (0 ocurrencias).
- El alta de usuario (`usuario_abm`) **hardcodea `nivel = "12345"`** → en la práctica
  **todos los usuarios tienen permiso total de ABM** y la granularidad real la da `acceso`.

## Campo `operador` — bit

Checkbox "El usuario cumple tareas de Operador de la mesa de trafico". Se carga en
`lOperadorMesaDeTrafico` al login. Marca quién opera el despacho diario.

## Decodificación de los usuarios reales (datos al 10/06/2026)

| Usuario | acceso | Módulos |
| --- | --- | --- |
| SUPERVISOR | `SRTDVLFAEUXM` | Todo menos avisos chequeo, backup, scheduler y ctas. ctes. Único con Tablero (`X`) |
| ANDRES | `SRTDVLFAEUBNM` | Todo menos avisos chequeo (`C`), scheduler (`H`) y tablero (`X`) |
| ALEJANDRA | `RTVFAEU` | Reservas, Tráfico, Vehículos, Facturación, ABM, Estadísticas, Utilitarios — **sin** gestión de usuarios (`S`), taller, diagramador, combustible |
| SERGIO | `SRTVLFAEU` | Como Alejandra + gestión de usuarios + taller |
| DAMIAN | `TCVLA` | Tráfico con avisos de chequeo, Vehículos, Taller, ABM — perfil operativo |
| BACKUP | `B` | Usuario de servicio: al loguear abre el form Backup directo |
| LUCIO | `TVM` | Tráfico, Vehículos, Combustible |

(El resto se decodifica igual, letra por letra, con la tabla de arriba.)

## Mapeo a Blazor (para replicar permisos)

1. **Leer `acceso`, `nivel`, `operador` en `AuthService`** al validar el login (ya se valida
   contra `usuario` con `f_delete IS NULL`) y guardarlos como **claims** del usuario:
   `acceso` → un claim por letra (ej. claim type `"modulo"`, valores `R`, `T`, `V`...).
2. **Drawer**: cada sección/ítem del nav declara su letra requerida y se muestra/oculta
   según el claim (equivalente al `SKIP FOR`). Sugerencia: en Blazor **ocultar** en vez de
   deshabilitar.
3. **Regla especial `F`**: además del menú, ocultar columnas/campos de importes en
   reportes y en el Zoom del Viaje si el usuario no tiene `F`.
4. `nivel` se puede ignorar por ahora (todos `"12345"`); recién importa al migrar ABMs
   (botones alta/modifica/baja = dígitos 2/3/4).
5. No inventar letras nuevas mientras conviva con FoxPro: el string lo escribe
   `usuario_abm.scx` y FoxPro lo relee en cada login.
