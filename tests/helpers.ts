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
