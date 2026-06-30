import { test, expect } from '@playwright/test';
import { login, irAInteractivo, captura } from './helpers';

/**
 * Validación de los filtros "Nº Reserva" y "Nº Reserva En Ruta" del menú
 * Aplicar Filtros de la Planilla de Tráfico (migrados desde trafico_nro_reserva.scx).
 *
 * - Nº Reserva (id_viaje): número único → trae 1 viaje.
 * - Nº Reserva En Ruta (id_viaje_i): correlativo de ruta → trae los N días de la ruta.
 *
 * Datos reales verificados en la base (replicaVPF, servidor local):
 *   id_viaje      = 1178999          → 1 viaje  (24 DE NOVIEMBRE, 2023-05-10, CANCELADO)
 *   id_viaje_i    = 9                → 3 viajes (2023-05-09/10/11)
 * Ambos ignoran el rango de fechas y NO descartan CANCELADO (réplica fiel).
 *
 * Requiere: app corriendo + NORTUR_USER / NORTUR_PASS.
 */

test.beforeEach(async ({ page }) => {
  await login(page);
});

test.beforeEach(async ({ page }) => {
  page.on('pageerror', err => {
    throw new Error(`Error JS no controlado en la página: ${err.message}`);
  });
});

// Abre el menú contextual sobre la primera fila de datos y entra al submenú
// "Aplicar Filtros" (MudMenu anidado → se despliega con hover, no con click).
// Devuelve cuando el ítem hijo pedido es visible.
async function abrirAplicarFiltros(page: import('@playwright/test').Page, item: RegExp) {
  const fila = page.locator('table.trafico-grid--virtual tbody tr.tg-row').first();
  await expect(fila).toBeVisible({ timeout: 15_000 });
  await fila.click({ button: 'right' });

  // El menú contextual de MudBlazor no expone role="menuitem" accesible de forma fiable:
  // localizamos por texto. "Aplicar Filtros" es un MudMenu anidado → se abre con hover.
  // (exact: true para no chocar con "Aplicar Filtros s/viaje").
  const submenu = page.getByText('Aplicar Filtros', { exact: true });
  await expect(submenu).toBeVisible({ timeout: 10_000 });
  await submenu.hover();

  // Esperar a que aparezca el ítem hijo (Nº Reserva / Nº Reserva En Ruta).
  const hijo = page.getByText(item);
  await expect(hijo).toBeVisible({ timeout: 10_000 });
  return hijo;
}

test('Filtro Nº Reserva — trae el viaje puntual', async ({ page }) => {
  await irAInteractivo(page, '/planilla-trafico');
  // ^…$ para no matchear "Nº Reserva En Ruta" (Nº Reserva es prefijo).
  const item = await abrirAplicarFiltros(page, /^Nº Reserva$/);
  await item.click();

  // Diálogo "Buscar Reserva por Número": un único campo numérico.
  const campo = page.getByLabel('Nº de Reserva');
  await campo.fill('1178999');
  await page.getByRole('button', { name: 'Aceptar' }).click();

  // Banner de filtro activo con el número buscado (.first(): el texto se repite en el KPI).
  await expect(page.getByText('Nº Reserva 1178999').first()).toBeVisible({ timeout: 15_000 });
  // El conteo del banner se actualiza al terminar de cargar la grilla: un solo servicio.
  await expect(page.locator('.planilla-page__filtro')).toContainText('1 servicios', { timeout: 15_000 });
  // La grilla muestra exactamente 1 fila de datos.
  await expect(page.locator('table.trafico-grid--virtual tbody tr.tg-row')).toHaveCount(1);
});

test('Filtro Nº Reserva En Ruta — trae los días de la ruta', async ({ page }) => {
  await irAInteractivo(page, '/planilla-trafico');
  const item = await abrirAplicarFiltros(page, /^Nº Reserva En Ruta$/);
  await item.click();

  const campo = page.getByLabel('Nº de Reserva En Ruta');
  await campo.fill('9');
  await page.getByRole('button', { name: 'Aceptar' }).click();

  await expect(page.getByText('Nº Reserva En Ruta 9').first()).toBeVisible({ timeout: 15_000 });
  // Los 3 días de la ruta id_viaje_i = 9 (el conteo se actualiza al terminar de cargar).
  await expect(page.locator('.planilla-page__filtro')).toContainText('3 servicios', { timeout: 15_000 });
  await expect(page.locator('table.trafico-grid--virtual tbody tr.tg-row')).toHaveCount(3);

  await captura(page, '/planilla-trafico', 'trafico-filtro-nro-reserva-ruta');
});
