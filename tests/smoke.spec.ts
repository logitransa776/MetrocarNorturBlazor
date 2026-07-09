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

test('Reservas por cliente — levanta con KPIs y pivote', async ({ page }) => {
  await irA(page, '/reservas-por-cliente');
  // El informe auto-carga al entrar: KPIs visibles = la query terminó sin excepción.
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.rfs-tabla')).toBeVisible();
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

test('Odómetros — levanta con KPIs y grilla', async ({ page }) => {
  await irA(page, '/odometros');
  // Auto-carga al entrar (últimos ~2 meses): KPIs = la query terminó sin excepción.
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Siniestros — levanta y muestra grilla', async ({ page }) => {
  await irA(page, '/siniestros');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
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

test('Viajes por chofer — levanta con KPIs y pivote', async ({ page }) => {
  await irA(page, '/viajes-por-chofer');
  // Auto-carga al entrar (mes anterior, con datos): KPIs = la query terminó sin excepción.
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.rfs-tabla')).toBeVisible();
});

test('Km Unidades vs Servicios — levanta con KPIs y tabla', async ({ page }) => {
  await irA(page, '/km-unidades-servicios');
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.rfs-tabla')).toBeVisible();
});

// ── Tráfico: Cabeceras · Francos · Viáticos (solo lectura + andamiaje ABM) ──

test('Cabeceras - Recorridos — levanta y muestra grilla', async ({ page }) => {
  await irA(page, '/cabeceras-recorridos');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Mantenimiento de Francos — levanta y muestra grilla', async ({ page }) => {
  await irA(page, '/francos');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Ingreso de Francos — levanta con el botón de alta', async ({ page }) => {
  await irA(page, '/francos-ingreso');
  await expect(page.getByRole('button', { name: /Abrir el ingreso de francos/i })).toBeVisible({ timeout: 15_000 });
});

test('Auditoría de Francos — levanta con KPIs', async ({ page }) => {
  await irA(page, '/francos-auditoria');
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
});

test('Viáticos — levanta con KPIs y grilla', async ({ page }) => {
  await irA(page, '/viaticos');
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Motivos de Viático — levanta y muestra grilla', async ({ page }) => {
  await irA(page, '/viaticos-motivo');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Formas de Liquidación de Viático — levanta y muestra grilla', async ({ page }) => {
  await irA(page, '/viaticos-forma-liquidacion');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

// ── Tráfico: Voucher · Guardia · Contactos · Lista de pasajeros (solo lectura + andamiaje) ──

test('Voucher Recepción — levanta con KPIs y filtros', async ({ page }) => {
  await irA(page, '/voucher-recepcion');
  // Arranca sin buscar (grilla vacía): basta con que los KPIs y la tabla existan sin excepción.
  await expect(page.getByText('Sin recepcionar', { exact: false }).first()).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Guardias — levanta con KPIs y grilla', async ({ page }) => {
  await irA(page, '/guardias');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Contactos y Proveedores — levanta y muestra grilla', async ({ page }) => {
  await irA(page, '/contactos');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Rubros de contactos — levanta y muestra grilla', async ({ page }) => {
  await irA(page, '/rubros-contacto');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Lista de pasajeros — levanta con el buscador de viajes', async ({ page }) => {
  await irA(page, '/lista-pasajeros');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

// ── Combustible: Consumos · Conciliación · Saldos · Depósitos (solo lectura + andamiaje ABM) ──

test('Promedio de Consumos — levanta con KPIs y ranking', async ({ page }) => {
  await irA(page, '/promedio-consumos');
  // Auto-carga al entrar (últimos 3 meses): KPIs = la query + el cálculo de consumo terminaron OK.
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('ABM y Conciliación de cargas — levanta con KPIs y grilla', async ({ page }) => {
  await irA(page, '/combustible-conciliacion');
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Saldos de Estaciones — levanta con KPIs y grilla', async ({ page }) => {
  await irA(page, '/saldos-estaciones');
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
});

test('Carga de Depósitos — levanta con KPIs y grilla', async ({ page }) => {
  await irA(page, '/depositos-estacion');
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Mantenimiento de Depósitos — levanta con KPIs y grilla', async ({ page }) => {
  await irA(page, '/depositos-mantenimiento');
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Control de cargas — levanta con KPIs y grilla', async ({ page }) => {
  await irA(page, '/control-cargas');
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

test('Consumo Mensual — levanta con KPIs y pivote', async ({ page }) => {
  await irA(page, '/consumo-mensual');
  await expect(page.locator('.rfs-kpis')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.rfs-tabla').first()).toBeVisible({ timeout: 15_000 });
});

test('Artículos por Rubro de Consumo — levanta y muestra grilla', async ({ page }) => {
  await irA(page, '/articulos-rubro');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
});

// ── Módulo Reservas: Reservas Especiales · Plantillas · Armado (solo lectura + andamiaje) ──

test('Reservas Especiales — grilla de reservas \'T\' + Nueva reserva deshabilitado', async ({ page }) => {
  await irA(page, '/reservas-especiales');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
  // Andamiaje apagado: el botón "Nueva reserva" debe estar deshabilitado.
  await expect(page.getByRole('button', { name: /Nueva reserva/i })).toBeDisabled();
});

test('Mantenimiento de Plantillas — grilla + botonera ABM deshabilitada', async ({ page }) => {
  await irA(page, '/plantillas-mantenimiento');
  await expect(page.locator('.cli-grid table').first()).toBeVisible({ timeout: 15_000 });
  // Andamiaje apagado: Eliminar Todo de plantillas deshabilitado.
  await expect(page.getByRole('button', { name: /^Eliminar Todo/i })).toBeDisabled();
});

test('Reservas por Plantillas (armado) — levanta con combo y días', async ({ page }) => {
  await irA(page, '/reservas-por-plantillas');
  await expect(page.locator('body')).toBeVisible();
  await expect(page).not.toHaveURL(/\/login/);
  // La barra de días de la semana está presente (Lun..Dom + Feriados).
  await expect(page.getByText('Feriados').first()).toBeVisible({ timeout: 15_000 });
});
