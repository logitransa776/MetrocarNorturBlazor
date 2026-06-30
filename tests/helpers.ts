import { Page, expect } from '@playwright/test';

/**
 * Credenciales de prueba. Se leen de variables de entorno para no commitear
 * passwords. Definí NORTUR_USER y NORTUR_PASS antes de correr los tests.
 */
export const USER = process.env.NORTUR_USER ?? '';
export const PASS = process.env.NORTUR_PASS ?? '';

/**
 * Login real contra /auth/login (POST HTML nativo con AntiforgeryToken).
 * Completa el form de /login y espera a salir de esa página (cookie seteada).
 * Reutilizar al inicio de cada test que requiera sesión.
 */
export async function login(page: Page) {
  if (!USER || !PASS) {
    throw new Error(
      'Faltan credenciales: definí NORTUR_USER y NORTUR_PASS como variables de entorno. ' +
      'Ej (PowerShell): $env:NORTUR_USER="alejandra"; $env:NORTUR_PASS="..."'
    );
  }

  await page.goto('/login');

  const user = page.locator('#login-user');
  const pass = page.locator('#login-pass');

  // El input usuario tiene autofocus: Blazor puede mover el foco / re-renderizar
  // el input tarde y pisar lo que escribimos con fill() (de ahí el flake donde el
  // campo quedaba vacío). Estrategia robusta:
  //  1) esperar a que el input esté presente y estable,
  //  2) clickear para fijar el foco,
  //  3) escribir tecla por tecla (pressSequentially reacciona al DOM real),
  //  4) re-verificar el value y reintentar el llenado si quedó vacío.
  await user.waitFor({ state: 'visible' });

  for (const [campo, valor] of [[user, USER], [pass, PASS]] as const) {
    await campo.click();
    await campo.fill('');                    // limpiar cualquier residuo del autofocus
    await campo.pressSequentially(valor, { delay: 15 });
    // Si el render de Blazor lo vació, reintentar una vez.
    if ((await campo.inputValue()) !== valor) {
      await campo.fill(valor);
    }
    await expect(campo).toHaveValue(valor);
  }

  // El submit es un POST HTML nativo → esperamos la navegación que provoca.
  await Promise.all([
    page.waitForURL(url => !url.pathname.startsWith('/login'), { timeout: 15_000 }),
    page.click('.login-btn'),
  ]);
  await expect(page).not.toHaveURL(/\/login/);
}

/**
 * Navega a una ruta protegida y verifica que NO rebotó al login (es decir, que
 * la sesión es válida y la pantalla cargó). Devuelve cuando el DOM está listo.
 */
export async function irA(page: Page, ruta: string) {
  await page.goto(ruta);
  await expect(page, `La ruta ${ruta} rebotó al login (¿sin permiso o sin sesión?)`)
    .not.toHaveURL(/\/login/);
}

/**
 * Navega a una ruta protegida y ESPERA a que el circuito interactivo de Blazor
 * Server quede conectado antes de devolver. Usar SIEMPRE que el test vaya a
 * INTERACTUAR (escribir, clickear, togglear) — no alcanza con `irA`.
 *
 * Por qué: en Blazor Server (@rendermode InteractiveServer) el HTML llega
 * renderizado en el servidor (estático), pero los handlers onclick/oninput se
 * cablean recién cuando el circuito SignalR conecta por WebSocket (/_blazor).
 * Si interactuás antes:
 *   - los clicks se pierden (el handler no existe aún), y
 *   - el primer render del circuito puede LIMPIAR lo que escribiste (re-bindea
 *     el input al valor del modelo, que está vacío).
 * Esperar a que abra el WebSocket /_blazor + un respiro evita ambos problemas.
 */
export async function irAInteractivo(page: Page, ruta: string) {
  // Registrar la espera del WS ANTES de navegar (si no, se pierde el evento).
  const ws = page
    .waitForEvent('websocket', {
      predicate: w => w.url().includes('_blazor'),
      timeout: 15_000,
    })
    .catch(() => null); // si no aparece, seguimos: el settle de abajo da margen

  await irA(page, ruta);
  await ws;
  // Respiro para que el circuito termine el primer render y cablee los handlers.
  await page.waitForTimeout(800);
}

/**
 * Captura a demanda de una pantalla, para que Claude pueda VER cómo quedó un
 * cambio visual sin instalar MCPs ni extensiones. Guarda un PNG en
 * tests/__shots__/ (carpeta ignorada por git: son artefactos, no código).
 *
 * No usa `fs`: `page.screenshot({ path })` ya crea la carpeta si no existe, así
 * el helper no agrega ninguna dependencia (no hace falta @types/node).
 *
 * Uso (en un .spec efímero o agregando una línea a un test con sesión):
 *   await captura(page, '/vehiculos', 'vehiculos-grilla');
 * Luego Claude lee tests/__shots__/vehiculos-grilla.png con su tool de imágenes.
 *
 * @param ruta   ruta protegida a fotografiar (login ya hecho en beforeEach)
 * @param nombre nombre de archivo sin extensión (ej. 'vehiculos-grilla')
 * @returns      la ruta del PNG generado
 */
export async function captura(page: Page, ruta: string, nombre: string) {
  await irA(page, ruta);
  // Blazor Server mantiene el WebSocket de SignalR abierto → 'networkidle' nunca
  // dispara. Esperamos 'load' + un respiro para que termine el render interactivo.
  await page.waitForLoadState('load');
  await page.waitForTimeout(500);
  const file = `tests/__shots__/${nombre}.png`;
  await page.screenshot({ path: file, fullPage: true });
  return file;
}
