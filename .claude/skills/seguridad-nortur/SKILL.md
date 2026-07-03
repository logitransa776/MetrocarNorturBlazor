---
name: seguridad-nortur
description: >
  Sistema de permisos y seguridad del Metrocar NORTUR — campo `acceso` (16 letras/módulos),
  `nivel` (dígitos ABM) y `operador` (rol despacho). Usar SIEMPRE que se trabaje en autenticación,
  claims, visibilidad de menús/secciones, permisos de botones, ocultar columnas de precios,
  o ABM de usuarios en Blazor. Incluye el mapa completo letra→módulo extraído del FoxPro,
  las trampas no obvias (M=Combustible, F=precios, E=muerto), el flujo de login y
  el patrón de implementación en AuthService + claims + drawer.
---

# Seguridad NORTUR — permisos del Metrocar en Blazor

> Fuente primaria: `login.scx`, `usuario_abm.scx`, `MENU_PRINCIPAL.MPR` (código FoxPro real).
> Doc completa: `docs/PlanoFoxPro/sistema/USUARIO_ACCESOS.md`.
> Extraído: 11/06/2026.

## Los 3 mecanismos de permisos

| Mecanismo | Campo BD | Formato | Qué controla |
| --- | --- | --- | --- |
| **Módulos/menús** | `usuario.acceso` | String de letras | Qué secciones del sistema ve el usuario |
| **Operaciones ABM** | `usuario.nivel` | String de dígitos | Botones Alta(`2`) / Modifica(`3`) / Baja(`4`) |
| **Rol operador** | `usuario.operador` | bit (0/1) | Operador de la mesa de tráfico |

### Regla práctica sobre `nivel`

El ABM de usuarios en FoxPro hardcodea `nivel = "12345"` para todos — en la práctica
**todos tienen ABM completo**. Ignorar `nivel` hasta que se migren los ABMs; cuando se
migren, chequear dígitos 2/3/4.

---

## Campo `acceso` — mapa completo de las 16 letras

El string es un bitmask textual: `"T" $ acceso` = tiene Tráfico.
El orden de concatenación en `usuario_abm.scx` es: `S R T C D V L F A E U B H X N M`.

| Letra | Módulo | Qué habilita exactamente |
| --- | --- | --- |
| `S` | Usuarios y Password | ABM de usuarios (Sistema → Accesos) + ver "Conectados al sistema" |
| `R` | Reservas | Menú Reservas completo |
| `T` | Tráfico | Menú Tráfico completo |
| `C` | Avisos de chequeos | Timer de avisos en pantalla Tráfico (60 seg). **Solo funciona si tiene `T`** |
| `D` | Diagramador | Funciones de diagramador en Tráfico: normalizar cronograma, F5 cambiar rango de fechas, búsqueda U/Pr |
| `V` | Vehículos | Menú Vehículos y Choferes + chequeo de vencimientos al login |
| `L` | Taller | Menú Taller |
| `F` | Facturación | Menú Facturación **+ visibilidad de precios e importes** en Reservas y Zoom del Viaje |
| `A` | ABM del Sistema | Menú ABM del sistema (catálogos: servicios, zonas, feriados, parámetros) |
| `E` | Estadísticas | ⚠️ **Flag muerto** — checkbox existe, ningún menú ni form lo chequea en el fuente |
| `U` | Utilitarios | Menú Utilitarios |
| `B` | Back-Up | Al login abre directo el form Backup (usuario de servicio) |
| `H` | Scheduler | Utilitarios → Scheduler + lo abre al login si no tiene `B` |
| `X` | Tablero de Control | Sistema → Tablero de Control. Solo SUPERVISOR puede otorgarlo |
| `N` | Cuentas Corrientes | Facturación → Cuentas Corrientes (menú variante `_C_CC`) |
| `M` | Combustible | Menú Combustible (**letra `M`, no `C`** — `C` es avisos de chequeo) |

### Trampas no obvias — leer antes de implementar

1. **`M` = Combustible, `C` = Avisos**. La hotkey del menú es ALT+C pero el flag es `M`.
2. **`F` es doble**: controla el menú Facturación Y la visibilidad de campos de importe/precio
   en Reservas y en el Zoom del Viaje. Sin `F` → ocultar botón Precio, campos importe/moneda/
   sin cargo/porcentaje.
3. **`E` no hace nada** en el fuente conocido. Reservar para uso futuro sin asignarle semántica nueva.
4. **`C` depende de `T`**: el ABM deshabilita el checkbox de avisos si Tráfico no está tildado.
5. **`X` solo SUPERVISOR puede otorgarlo**: el form `usuario_abm` lo deshabilita para no-SUPERVISOR.
6. **Mecanismo legacy muerto**: `nombre_nivel()` (niveles S/A/U/O/I) y `desactiva_menu()` con
   tabla `acceso_nivel` están en `login.scx` pero **nunca se llaman**. La tabla `acceso_nivel`
   no existe en los DBF. Ignorar completamente.

---

## Flujo de login del FoxPro (para replicar en Blazor)

```
1. SELECT * FROM usuario WHERE usuario = @usuario
2. Si no existe → error "Usuario Inexistente"
3. Si f_delete IS NOT NULL → error "Usuario Inhabilitado" (baja lógica)
4. Si password no coincide → error (password en texto plano en FoxPro)
5. Cargar: cUsuario, cNivel, cAcceso, lOperadorMesaDeTrafico
6. Post-login:
   - "V" $ cAcceso → corre chequeos de vencimientos (chofer REG/CNRT/AEO, vehículo)
   - "B" $ cAcceso → abre form Backup directamente
   - "H" $ cAcceso (sin B) → abre form Scheduler
```

---

## Usuarios actuales decodificados (replicaVPF al 10/06/2026)

| Usuario | acceso | Módulos habilitados |
| --- | --- | --- |
| SUPERVISOR | `SRTDVLFAEUXM` | Todo excepto avisos chequeo (C), backup (B), scheduler (H), ctas. ctes. (N). Único con Tablero (X) |
| ANDRES | `SRTDVLFAEUBNM` | Todo excepto avisos chequeo (C), scheduler (H), tablero (X) |
| ALEJANDRA | `RTVFAEU` | Reservas, Tráfico, Vehículos, Facturación, ABM, Estadísticas, Utilitarios — sin S, C, D, L, M |
| SERGIO | `SRTVLFAEU` | Como Alejandra + Usuarios (S) + Taller (L) |
| DAMIAN | `TCVLA` | Tráfico + avisos chequeo + Vehículos + Taller + ABM |
| BACKUP | `B` | Solo Backup — usuario de servicio |
| LUCIO | `TVM` | Tráfico, Vehículos, Combustible |

---

## Implementación en Blazor — patrón recomendado

### 1. AuthService — cargar claims al login

En `Services/AuthService.cs`, al validar el usuario, leer también `acceso` y `operador`
y convertir cada letra presente en un claim individual:

```csharp
// Claim type para módulos del menú
const string ClaimModulo = "nortur:modulo";
const string ClaimOperador = "nortur:operador";

// Por cada letra en acceso, agregar un claim
foreach (char letra in usuarioDb.Acceso ?? "")
    claims.Add(new Claim(ClaimModulo, letra.ToString()));

// Flag operador
if (usuarioDb.Operador)
    claims.Add(new Claim(ClaimOperador, "1"));
```

### 2. Helper de verificación — IPermissionService

```csharp
// Services/PermissionService.cs
public interface IPermissionService
{
    bool Tiene(char modulo);           // "T" $ cAcceso
    bool TieneABM(int operacion);      // 2=alta, 3=modifica, 4=baja
    bool EsOperador { get; }
}
```

Implementación: leer los claims del `AuthenticationState` actual.

### 3. Drawer — visibilidad por módulo

```razor
@* Equivalente al SKIP FOR !("T" $ cAcceso) — ocultar (no deshabilitar) *@
@if (Permisos.Tiene('T'))
{
    <NavLink href="/trafico">Tráfico</NavLink>
}
```

En Blazor se **oculta** el ítem (no se deja gris como en FoxPro).

### 4. Regla especial F — precios

En cualquier componente que muestre importes/precios:

```razor
@if (Permisos.Tiene('F'))
{
    <MudTableColumn T="ViajeDto" Field="@nameof(ViajeDto.Importe)" Title="Importe" />
}
```

### 5. NO inventar letras nuevas

Mientras Blazor conviva con FoxPro, el string `acceso` lo sigue escribiendo `usuario_abm.scx`.
Si se agrega una letra nueva desde Blazor, FoxPro no la conoce y la pierde al próximo `UPDATE`.
Las letras disponibles no usadas en el fuente: `E` (estadísticas, muerto), `P`, `G`, `I`, `J`, `K`, `O`, `Q`, `W`, `Y`, `Z` — **no asignar hasta migrar el ABM de usuarios a Blazor**.

---

## Estado en Blazor (11/06/2026)

- [x] Login con flujo FoxPro (`ReportService.LoginAsync`): inexistente → inhabilitado
      (`f_delete`) → contraseña, cada caso con su mensaje
- [x] Claims al login (`NorturAuthStateProvider.IniciarSesion`): un `nortur:modulo`
      por letra + `nortur:nivel` + `nortur:operador`
- [x] `IPermissionService` implementado (`Services/PermissionService.cs`):
      `Tiene(char)`, `TieneABM(int)`, `EsOperador`, `DestinoInicial()`
- [x] Drawer condicionado por módulo (`MainLayout.razor`): las 8 secciones se
      ocultan según letra — R, T, V, F, L, M, A, U
- [x] `TableroAlertas` (vencimientos) solo con letra `V` — como el post-login FoxPro
- [x] Guards de ruta: `/planilla-trafico` exige `T`, `/reservas-*` exigen `R`;
      sin módulo → redirect a `/` (Home muestra aviso si no tiene ninguno)
- [ ] **Regla `F` — ocultar columnas de importe** — 🔴 **AHORA ES PRERREQUISITO DE FASE 0
      del plan Buslink** (`docs/buslink/PLAN_MIGRACION_BUSLINK.md`): hoy el Zoom muestra precios a
      TODOS, y el Zoom en edición + el "Valor Especial" de Reservas dependen de este permiso.
      Primera entrega de código de la migración (chica e independiente).
      En cualquier componente con columnas de importe/precio/moneda/sin cargo/porcentaje,
      envolver en `@if (Permisos.Tiene('F')) { ... }`. Afecta:
      - Zoom del Viaje (campos importe, moneda, sin_cargo, porcentaje)
      - Reportes de reservas con columna de importe/precio
      - Cualquier grilla que muestre datos financieros
      Matriz de prueba: DAMIAN (`TCVLA`, sin F) y LUCIO (`TVM`, sin F) no deben ver importes.
      **Patrón exacto:**
      ```razor
      @inject IPermissionService Permisos
      @if (Permisos.Tiene('F'))
      {
          <MudTableColumn T="ViajeDto" Field="@nameof(ViajeDto.Importe)" Title="Importe" />
      }
      ```
- [x] **ABM de usuarios en Blazor — HECHO (01/07/2026), primer ABM de escritura del proyecto.**
      Página `UsuariosAbm.razor` (`/usuarios-abm`, guard `'S'`) + dialog `UsuarioEditorDialog.razor`
      (4 modos) + `AbmService` (escritura) + `PermisosCatalogo` (las 16 letras en orden fijo con sus
      reglas). Menú: sección **Sistema** en el drawer, solo con `'S'`. Escribe en el server local
      (sync de `usuario` apagada). El mapa checkbox→letra de esta skill quedó materializado en
      `Services/PermisosCatalogo.cs` (fuente única). Reglas C→T y X→SUPERVISOR implementadas en vivo.
      Detalle completo y trampas: skill `abm-metrocar` (§ Primer ABM de escritura).

> **Al crear una página nueva (checklist de seguridad):**
> 1. Inyectar `@inject IPermissionService Permisos`
> 2. En `OnInitializedAsync`: guard `if (!Permisos.Tiene('X')) { Nav.NavigateTo("/"); return; }`
> 3. En el drawer (`MainLayout.razor`): envolver la sección en `@if (Permisos.Tiene('X')) { ... }`
> 4. Si la página muestra importes/precios: aplicar regla `F`
