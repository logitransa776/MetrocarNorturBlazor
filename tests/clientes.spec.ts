import { test, expect } from '@playwright/test';
import { login, irA, irAInteractivo, captura } from './helpers';

/** Extrae el "X" del chip "X de Y" (cantidad de filas filtradas). */
async function contadorFiltrado(page: import('@playwright/test').Page): Promise<number> {
  const txt = (await page.getByText(/\d+ de \d+/).first().innerText()).trim();
  const m = txt.match(/(\d+) de (\d+)/);
  return m ? parseInt(m[1], 10) : -1;
}

/**
 * Test FUNCIONAL de la pantalla Clientes (ABM solo lectura) — /clientes-abm.
 *
 * Va más allá del smoke (que solo verifica que la grilla aparece): ejercita los
 * controles reales de la pantalla — buscador por Razón Social, checkbox
 * Egresados, contador "X de Y" y apertura de la ficha — y deja capturas en
 * tests/__shots__/ para VER cómo se comporta.
 *
 * Requiere: app corriendo (dotnet run, puerto 5287) + NORTUR_USER / NORTUR_PASS.
 */

test.beforeEach(async ({ page }) => {
  await login(page);
});

// Cazar cualquier error JS no controlado del render interactivo.
test.beforeEach(async ({ page }) => {
  page.on('pageerror', err => {
    throw new Error(`Error JS no controlado en la página: ${err.message}`);
  });
});

test('Clientes — grilla carga con filas y contador', async ({ page }) => {
  await irA(page, '/clientes-abm');

  // La grilla (MudTable) debe aparecer con filas reales.
  // OJO: MudTable aplica el Class ("cli-grid") al DIV contenedor, no al <table>.
  // El <table> real es .mud-table-root → seleccionar como ".cli-grid table".
  const tabla = page.locator('.cli-grid table');
  await expect(tabla).toBeVisible({ timeout: 15_000 });

  const filas = page.locator('.cli-grid tbody tr');
  await expect(filas.first()).toBeVisible({ timeout: 15_000 });
  const total = await filas.count();
  console.log(`[clientes] filas visibles en la grilla: ${total}`);
  expect(total).toBeGreaterThan(0);

  // El chip "X de Y" refleja el conteo (filtrados de total).
  const chip = page.getByText(/\d+ de \d+/);
  await expect(chip.first()).toBeVisible();
  console.log(`[clientes] contador: ${(await chip.first().innerText()).trim()}`);

  await captura(page, '/clientes-abm', 'clientes-grilla');
});

test('Clientes — el buscador por Razón Social filtra', async ({ page }) => {
  await irAInteractivo(page, '/clientes-abm');
  await expect(page.locator('.cli-grid tbody tr').first()).toBeVisible({ timeout: 15_000 });

  const filtInicial = await contadorFiltrado(page);

  // Escribir un término DISCRIMINANTE en "Razón Social" (DebounceInterval=200ms).
  // "AEROLINEAS" matchea solo unos pocos → demuestra que el filtro ACOTA de verdad
  // (con "A" matchean los 413 y no probaríamos nada).
  const buscador = page.getByLabel('Razón Social');
  await buscador.click();
  await buscador.pressSequentially('AEROLINEAS', { delay: 30 });

  // El filtro debe ACOTAR. expect.poll reintenta hasta que el debounce + render
  // del servidor actualicen el contador (más robusto que un waitForTimeout fijo).
  await expect.poll(() => contadorFiltrado(page), { timeout: 8_000 })
    .toBeLessThan(filtInicial);
  const filtFinal = await contadorFiltrado(page);
  console.log(`[clientes] filtrados antes=${filtInicial}, tras "AEROLINEAS"=${filtFinal}`);
  expect(filtFinal).toBeGreaterThan(0);

  await page.screenshot({ path: 'tests/__shots__/clientes-busqueda.png', fullPage: true });
});

test('Clientes — checkbox Egresados cambia el conjunto', async ({ page }) => {
  await irAInteractivo(page, '/clientes-abm');
  await expect(page.locator('.cli-grid tbody tr').first()).toBeVisible({ timeout: 15_000 });
  const filtInicial = await contadorFiltrado(page);

  // Tildar "Egresados". OJO: clickear el texto del label NO togglea el MudCheckBox;
  // hay que clickear el elemento <label class="mud-checkbox"> que envuelve al input.
  await page.locator('label.mud-checkbox:has-text("Egresados")').click();

  // El filtro es EXCLUSIVO (solo inhabilitados) → el contador debe cambiar.
  await expect.poll(() => contadorFiltrado(page), { timeout: 8_000 })
    .not.toBe(filtInicial);
  const filtFinal = await contadorFiltrado(page);
  const filasEgresado = await page.locator('.cli-grid tbody tr.cli-grid__row--egresado').count();
  console.log(`[clientes] filtrados activos=${filtInicial} → egresados=${filtFinal} ` +
              `(filas con clase egresado en DOM=${filasEgresado})`);

  // Egresados es un subconjunto → menos que el total de activos.
  expect(filtFinal).toBeLessThan(filtInicial);

  await page.screenshot({ path: 'tests/__shots__/clientes-egresados.png', fullPage: true });
});

test('Clientes — seleccionar fila + "Ver ficha" abre el diálogo', async ({ page }) => {
  await irAInteractivo(page, '/clientes-abm');
  const primeraFila = page.locator('.cli-grid tbody tr').first();
  await expect(primeraFila).toBeVisible({ timeout: 15_000 });

  // Un clic SELECCIONA la fila (_sel) → habilita el botón "Ver ficha".
  // (El doble clic nativo es flaky en Blazor Server: el 1er clic re-renderiza la
  // fila por SignalR y el dblclick pierde el detail. La botonera es determinística.)
  await primeraFila.click();
  const verFicha = page.getByRole('button', { name: /Ver ficha/i });
  await expect(verFicha).toBeEnabled({ timeout: 10_000 });
  await verFicha.click();

  // El diálogo MudBlazor de la ficha debe abrirse.
  const dialogo = page.locator('.mud-dialog');
  await expect(dialogo).toBeVisible({ timeout: 10_000 });
  console.log('[clientes] ficha abierta correctamente');

  await page.screenshot({ path: 'tests/__shots__/clientes-ficha.png', fullPage: true });
});
