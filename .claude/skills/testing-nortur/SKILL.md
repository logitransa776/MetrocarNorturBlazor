---
name: testing-nortur
description: Cómo TESTEAR y VALIDAR la app Metrocar Nortur Blazor — smoke tests E2E (Playwright), capturas de pantalla a demanda, validación de escritura de ABMs y dónde viven los errores en Blazor Server. Usar SIEMPRE que se vaya a probar/validar/verificar un cambio, correr smoke tests, sacar una captura para "ver" una pantalla, testear un alta/baja/modificación (Graba) de un ABM, reproducir un bug, confirmar que algo funciona antes de commitear, o cuando el usuario diga "probá", "validá", "mostrame la pantalla", "andá a fijarte", "testealo". Define el protocolo de datos de prueba sobre la base productiva replicaVPF (NO ensuciarla) y la regla de validación con dos señales. Complementa abm-metrocar (construir) — esta skill es cómo VALIDAR.
---

# Testing y validación — Metrocar Nortur Blazor

Esta skill es el **proceso de validación** del proyecto. No es una skill de Playwright (esa
herramienta ya se sabe usar): es cómo se valida **ESTA** app — Blazor Server, base productiva,
escritura de ABMs por venir. Complementa `abm-metrocar` (cómo construir) y `blazor-nortur`
(cómo está hecha la UI).

Herramientas disponibles, en orden de uso real para un ABM:
1. **Log del servidor** (`dotnet run`) — la fuente #1 de verdad para bugs de lógica.
2. **`SELECT` de verificación** en SQL — confirma que el dato persistió.
3. **Capturas a demanda** (`captura()` en `tests/helpers.ts`) — para VER la pantalla.
4. **Smoke tests** (`npm test`) — red de seguridad contra regresiones.

---

## A. Dónde viven los errores (Blazor Server) — leer primero

En Blazor Server NO todos los errores aparecen en el mismo lugar. Buscar en el lugar correcto
ahorra horas:

| Tipo de error | Dónde aparece | Cómo se detecta |
| --- | --- | --- |
| Lógica de negocio (ABM, SQL, validación, valorización) | **Log del servidor** (terminal de `dotnet run`) | Leer la salida de `dotnet`. **Es la herramienta #1 para ABMs.** |
| JS no controlado / render interactivo roto | Consola del navegador | El hook `pageerror` de los smoke tests lo caza y falla el test |
| Problema visual (layout, columnas, colores, badges) | Solo se VE en pantalla | Captura `captura()` + leer el PNG |
| Regresión gruesa (una pantalla dejó de levantar) | El smoke test falla | `npm test` |

**Regla de oro:** si un ABM falla al grabar o un cálculo da mal, el error está en el **log de
`dotnet`**, NO en el navegador. La consola del browser en Blazor Server casi solo muestra ruido
de SignalR (reconexiones, keep-alives). Por eso `browser-tools-mcp` (leer consola/red del
browser) se descartó: lee la capa equivocada para este stack.

---

## B. 🔴 Protocolo de datos de prueba — la regla más importante

`replicaVPF` es una **base con datos REALES del cliente**, no un sandbox. Testear escritura
(ABMs) sin protocolo = ensuciar producción. Antes de CUALQUIER test que escriba:

### B1. Verificar contra qué servidor apuntás
Mirar `MetroCarSysBlazor/appsettings.json` → `ConnectionStrings:DefaultConnection`:
- `DESKTOP-CV6LF0O\SQLEXPRESS` (Nortur2021) = **servidor local** (réplica completa, para desarrollo).
- `172.25.69.217` (Nortur1024) = **servidor nuevo productivo**.

**Testear escritura SOLO contra el local.** Nunca probar un alta/baja contra el productivo
sin que el usuario lo pida explícito y entienda el riesgo.

### B2. Datos de prueba reconocibles y reversibles
- Usar un identificador **marcado y buscable**: PK o nombre con prefijo `ZZTEST` (ej:
  `id_zona = 'ZZTEST01'`). Así cualquier basura es localizable con un `WHERE ... LIKE 'ZZTEST%'`.
- **Limpiar siempre al terminar**: el ABM usa baja lógica (`f_delete = DATE()`), pero para
  datos de prueba conviene `DELETE` físico de las filas `ZZTEST` (no son históricos del negocio).
- **Nunca** dejar registros de prueba en la grilla que un operador pueda confundir con reales.

### B3. No tocar registros reales
No usar un cliente/chofer/vehículo real existente como conejillo de indias para probar un
UPDATE. Crear uno `ZZTEST` propio, probar sobre él, borrarlo.

---

## C. Validación con DOS SEÑALES (clave para ABMs)

Un "Graba" **no** está validado porque el botón se clickeó sin tirar excepción. Hay que
confirmar el resultado de **dos formas independientes** — si validás con una sola, tenés
falsos positivos (reporta OK y no grabó, o reporta error y sí grabó):

| Operación | Señal 1 (UI) | Señal 2 (datos) |
| --- | --- | --- |
| **Alta** | la fila nueva aparece en la grilla | `SELECT` en SQL devuelve la fila con `_deleted = 0` |
| **Modifica** | el cambio se ve en la grilla/ficha | `SELECT` muestra el valor nuevo + `f_modify` cargado |
| **Baja** | la fila se pinta **amarilla** (no desaparece) | `SELECT` muestra `f_delete` con fecha |
| **PK duplicada** | aparece el cartel de error y NO graba | `SELECT COUNT` sigue en 1 (no insertó) |

Nunca asumir éxito sin la señal de datos. Para el `SELECT` de verificación, usar una query
read-only directa (puede ser un método temporal en `ReportService` o SQL directo).

---

## D. Capturas a demanda — que Claude VEA la pantalla

Para validar lo **visual** sin instalar nada (no se usa `browser-tools-mcp`, ver §A):

Helper ya instalado: `captura(page, ruta, nombre)` en `tests/helpers.ts`. Guarda un PNG en
`tests/__shots__/` (ignorado por git). Flujo cuando el usuario pide "mostrame /la-ruta":

1. App corriendo (`dotnet run`, puerto **5287**) y credenciales seteadas (ver §E).
2. Crear un `.spec` efímero en `tests/` que haga `login(page)` + `captura(page, '<ruta>', '<nombre>')`.
3. Correr solo ese archivo: `npx playwright test tests/_shot.spec.ts --reporter=list`
   (pasar credenciales inline en el mismo comando, ver §E).
4. Leer el PNG resultante con la tool de imágenes y describir lo que se ve.
5. **Borrar el `.spec` efímero** (no es parte de la suite).

Trampa Blazor Server: **NO usar `waitForLoadState('networkidle')`** — el WebSocket de SignalR
nunca queda idle, se cuelga hasta el timeout. Usar `'load'` + una espera corta (ya resuelto
dentro de `captura()`).

---

## E. Correr los tests — comandos y credenciales

Credenciales por **variable de entorno** (NUNCA hardcodear; el repo no las commitea). En el
mismo comando que ejecuta el test, para que el proceso las herede:

```powershell
$env:NORTUR_USER="alejandra"; $env:NORTUR_PASS="ale"; npm test
```

| Comando | Qué hace |
| --- | --- |
| `npm test` | Toda la suite de smoke tests (headless) |
| `npm run test:headed` | Igual, con navegador visible |
| `npm run test:ui` | Modo interactivo de Playwright |
| `npm run report` | Abre el reporte HTML del último run (con screenshots de fallos) |
| `npx playwright test tests/<archivo>.spec.ts` | Corre un solo archivo (capturas efímeras) |

Qué cubren los smoke tests hoy (`tests/smoke.spec.ts`): Planilla de Tráfico, Reservas por
fecha, Clientes ABM, Choferes, Vehículos-Flota, Resumen de Liquidaciones, Liquidación a
Clientes, Liquidaciones estimadas. Cada uno verifica que la pantalla levanta, que la sesión
es válida (no rebota a `/login`) y que el contenido principal aparece.

Hay además **tests funcionales por pantalla** (van más allá del smoke: ejercitan filtros,
checkboxes, apertura de fichas). Plantilla ya hecha: `tests/clientes.spec.ts`. Calcarla para
testear otra pantalla.

**El login tiene un flake ya resuelto** en `tests/helpers.ts`: el autofocus de Blazor pisaba
el `fill()`, por eso se usa `pressSequentially` + re-verificación. No volver a `fill()` simple.

---

## F. 🔴 Tests INTERACTIVOS — esperar el circuito ANTES de tocar la UI

Esta es **la trampa #1 al testear interacción en Blazor Server**, y no se ve en el smoke
(que solo lee contenido ya renderizado por el servidor). Aprendida testeando Clientes (29/06/2026).

**El problema:** con `@rendermode InteractiveServer` el HTML llega renderizado en el servidor
(estático), pero los handlers `onclick`/`oninput` se cablean **recién cuando el circuito
SignalR conecta por WebSocket (`/_blazor`)**. Si interactuás antes de esa conexión:
- los **clicks se pierden** (el handler todavía no existe), y
- lo que **escribís en un input se BORRA** — el primer render del circuito re-bindea el input
  al valor del modelo (vacío). Síntoma exacto: el test tipea, pero la captura muestra el campo
  vacío y el filtro no aplicó. Falso negativo desconcertante (parece bug de la app, es timing).

**La solución (ya implementada):** usar `irAInteractivo(page, ruta)` de `tests/helpers.ts` en
vez de `irA` **siempre que el test vaya a interactuar**. Espera a que abra el WebSocket
`/_blazor` + un respiro para que cablee los handlers. `irA` (sin espera) solo sirve para tests
que únicamente LEEN contenido (smoke, capturas).

```ts
await irAInteractivo(page, '/clientes-abm');   // ← antes de escribir/clickear
// recién ahora: buscador, checkbox, click en fila, etc.
```

Para aserciones que dependen de un re-render del servidor (filtros, contadores), usar
**`expect.poll(...)`** en lugar de `waitForTimeout` fijo: reintenta hasta que el debounce +
round-trip de SignalR actualicen el DOM.

### Selectores MudBlazor — trampas verificadas

| Querés... | NO uses | Usá | Por qué |
| --- | --- | --- | --- |
| el `<table>` de una `MudTable Class="cli-grid"` | `table.cli-grid` | `.cli-grid table` (filas: `.cli-grid tbody tr`) | el `Class` va al **div contenedor**, no al `<table>` (que es `.mud-table-root`) |
| togglear un `MudCheckBox Label="Egresados"` | `getByText('Egresados')` | `label.mud-checkbox:has-text("Egresados")` | clickear el texto del label **no** togglea el input |
| abrir una ficha (dialog) desde una fila | `row.dblclick()` | un clic para seleccionar + click en el botón "Ver ficha" | el doble clic nativo es flaky: el 1er clic re-renderiza la fila por SignalR y el `dblclick` pierde el `detail` |

Regla general (de buenas prácticas): preferir selectores **estables** (texto visible, `id`,
rol) sobre clases CSS de MudBlazor que cambian entre versiones. Si un selector falla, probar
una **variante** (`text=`, rol, atributo) antes de reintentar el mismo 5 veces.

- **Cerrar el browser**: Playwright lo maneja por test, pero en scripts manuales usar
  `try/finally` con `browser.close()` para no dejar procesos huérfanos.
- **Evitar `function nombrada(){}` dentro de `page.evaluate()`**: puede dar
  `__name is not defined` por la transpilación; usar arrow functions o IIFE.

---

## G. Reporte honesto del resultado

Al terminar de validar algo, reportar con franqueza (alineado con cómo trabaja el proyecto):

- ✅ **Qué pasó** (con la señal verificable: "la fila aparece Y el SELECT la devuelve").
- ⚠️ **Estado ambiguo**: si una acción se ejecutó pero NO se pudo verificar la señal de datos,
  **decirlo explícito** — "el botón clickeó pero no confirmé en SQL". Es peor un falso OK que
  una alerta honesta.
- ❌ **Qué falló**, con la causa concreta (del log del servidor) y captura si es visual.

Nunca reportar "grabó OK" sin la segunda señal.

---

## Checklist de validación de un ABM (cuando llegue la escritura)

En orden, después de construir el ABM (ver `abm-metrocar` para construirlo):

1. Confirmar que `appsettings.json` apunta al **servidor local** (§B1).
2. **Alta**: crear `ZZTEST01` → verificar dos señales (aparece en grilla + `SELECT`).
3. **PK duplicada**: intentar `ZZTEST01` de nuevo → debe rechazar (cartel + COUNT sigue 1).
4. **Modifica**: cambiar un campo → verificar dos señales (UI + `f_modify`).
5. **Baja lógica**: dar de baja → fila amarilla + `f_delete` cargado.
6. **Permisos**: probar con un usuario sin nivel 2/3/4 → botones deshabilitados.
7. Revisar el **log de `dotnet`** durante todo: cero excepciones no controladas.
8. **Limpiar**: `DELETE` físico de las filas `ZZTEST`.
9. `npm test` → ninguna pantalla existente se rompió.
10. Captura de la grilla final si hubo cambios visuales.

---

## Estado y mantenimiento

- Flujo de capturas + smoke tests: **operativo** (29/06/2026).
- Validación de escritura de ABMs: **estrenada con el ABM de Usuarios (01/07/2026)**. Trampas reales:
  - **No se puede invocar el `AbmService` desde afuera de la app.** Se validó la lógica de escritura
    ejecutando el **mismo SQL** que genera el service (alta con `MAX(id)+1`, INSERT con `_deleted=0`,
    UPDATE `f_delete`) sobre `ZZTEST01` con `sqlcmd`, comprobando las dos señales y limpiando con
    DELETE físico. Es válido y rápido cuando el service es un traductor directo a SQL.
  - **Lógica pura (sin base) se valida aparte**: el orden del string `acceso` (`PermisosCatalogo.Construir`)
    se probó replicándolo en PowerShell (entrada desordenada → orden fijo). No hace falta base para eso.
  - **La app viva bloquea el `.exe`** → `dotnet build` normal falla con MSB3027 (lock). Para verificar
    que el CÓDIGO compila sin frenar la app: `dotnet build -p:BaseOutputPath="obj/verifybuild/" -p:UseAppHost=false`
    (compila a otro output, no toca el .exe lockeado). Para VER la UI nueva sí hay que reiniciar la app.
  - **Capturas del ABM**: `SUPERVISOR` es el mejor usuario para fotografiar el dialog (tiene `'S'` para
    entrar y `'X'` para ver el checkbox "Tablero de comando" habilitado). Su password (local, plano) sale
    de `SELECT password FROM usuario WHERE usuario='SUPERVISOR'`. Correr el `.spec` efímero con
    `NORTUR_USER="SUPERVISOR" NORTUR_PASS="..."` inline, y **borrarlo + borrar los PNG** al terminar.
- Decisión registrada: `browser-tools-mcp` descartado (lee el navegador; en Blazor Server la
  verdad está en el server log). `browser-automation` (skill de scraping/redes) no instalada;
  se absorbieron sus 5 reglas útiles (§C dos señales, §F selectores/cierre, §G reporte honesto).
