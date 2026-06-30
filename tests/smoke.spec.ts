import { test, expect } from '@playwright/test';
import { login, irA } from './helpers';

/**
 * Smoke tests de las pantallas clave migradas.
 *
 * Objetivo: detectar regresiones gruesas tras un cambio — que cada pantalla
 * LEVANTE sin error, que la sesión sea válida y que el contenido principal
 * (grilla, KPIs, filtros) aparezca. NO valida lógica de negocio fina.
 *
 * Requiere: app corriendo (dotnet run) + NORTUR_USER / NORTUR_PASS seteadas.
 */

// Una sola sesión para toda la suite (login es caro y abre circuito SignalR).
test.beforeEach(async ({ page }) => {
  await login(page);
});

// Falla el test si la consola del navegador tira un error JS (regresión típica
// de un cambio en .razor que rompe el render interactivo).
test.beforeEach(async ({ page }) => {
  page.on('pageerror', err => {
    throw new Error(`Error JS no controlado en la página: ${err.message}`);
  });
});

test('Planilla de Tráfico — carga grilla, KPIs y scroll', async ({ page }) => {
  await irA(page, '/planilla-trafico');

  // KPIs del día (réplica de las tarjetas Servicios / Total pax / etc.).
  await expect(page.getByText('Total pax')).toBeVisible();

  // La grilla virtualizada renderiza filas de datos (clase .tg-row que pusimos
  // junto al SpacerElement="tr"). Esto verifica que <Virtualize> pintó algo.
  const filas = page.locator('table.trafico-grid--virtual tbody tr.tg-row');
  await expect(filas.first()).toBeVisible({ timeout: 15_000 });

  // Smoke del scroll: el contenedor scrollea sin tirar error JS (el pageerror
  // hook de arriba lo cazaría). No medimos jank acá — eso es visual.
  const wrap = page.locator('.trafico-wrap').first();
  await wrap.evaluate(el => el.scrollBy(0, 600));
  await page.waitForTimeout(300);
  await expect(filas.first()).toBeVisible();
});

test('Reservas por fecha y servicio — levanta', async ({ page }) => {
  await irA(page, '/reservas-fecha-servicio');
  // El informe 1 es interactivo: basta con que no rebote al login y el body cargue.
  await expect(page.locator('body')).toBeVisible();
  await expect(page).not.toHaveURL(/\/login/);
});

test('Clientes (ABM solo lectura) — levanta y muestra grilla', async ({ page }) => {
  await irA(page, '/clientes-abm');
  await expect(page.locator('table').first()).toBeVisible({ timeout: 15_000 });
});

test('Choferes — levanta y muestra grilla', async ({ page }) => {
  await irA(page, '/choferes');
  await expect(page.locator('table').first()).toBeVisible({ timeout: 15_000 });
});

test('Vehículos - Flota — levanta y muestra grilla', async ({ page }) => {
  await irA(page, '/vehiculos');
  await expect(page.locator('table').first()).toBeVisible({ timeout: 15_000 });
});

test('Resumen de Liquidaciones — levanta', async ({ page }) => {
  await irA(page, '/resumen-liquidaciones');
  await expect(page).not.toHaveURL(/\/login/);
});

test('Liquidación a Clientes — levanta', async ({ page }) => {
  await irA(page, '/liquidacion-clientes');
  await expect(page).not.toHaveURL(/\/login/);
});

test('Liquidaciones estimadas — levanta', async ({ page }) => {
  await irA(page, '/facturacion-estimada');
  await expect(page).not.toHaveURL(/\/login/);
});
