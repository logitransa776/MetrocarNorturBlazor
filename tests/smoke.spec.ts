import { test, expect } from '@playwright/test';
import { login, irA, irAInteractivo } from './helpers';

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
  // Se ubica por el wrapper .trafico-wrap--nav (y no por la clase --virtual de la
  // <table>) para que el selector siga sirviendo si la grilla cambia de estrategia
  // de virtualización — ver docs/performance/PENDIENTE_GRILLA_TRAFICO_BLANQUEO.md.
  const filas = page.locator('.trafico-wrap--nav tbody tr.tg-row');
  await expect(filas.first()).toBeVisible({ timeout: 15_000 });

  // Smoke del scroll: el contenedor scrollea sin tirar error JS (el pageerror
  // hook de arriba lo cazaría). No medimos jank acá — eso es visual.
  const wrap = page.locator('.trafico-wrap').first();
  await wrap.evaluate(el => el.scrollBy(0, 600));
  await page.waitForTimeout(300);
  await expect(filas.first()).toBeVisible();
});

test('Planilla de Tráfico — la leyenda de estados filtra la grilla', async ({ page }) => {
  await irAInteractivo(page, '/planilla-trafico');

  const botones = page.locator('.leyenda--btn');
  const pie = page.locator('.trafico-pie__cuenta');
  await expect(botones).toHaveCount(6);   // SIN ASIGNAR · ASIGNADO · EN CURSO · FINALIZADO · FACTURADO · CHEQUEO

  // INVARIANTE: los contadores de la botonera suman EXACTO lo que muestra la grilla.
  // Se calculan sobre el conjunto filtrado por todo menos el estado (ver RecalcularVisibles),
  // así que si alguien rompe esa cuenta, este test lo caza en cualquier día.
  const textos = await botones.allTextContents();
  const suma = textos.reduce((acc, t) => acc + Number(t.match(/(\d+)\s*$/)?.[1] ?? 0), 0);
  const total = Number((await pie.textContent())?.match(/de (\d+) servicios/)?.[1] ?? -1);
  expect(suma).toBe(total);

  // Estados con servicios, de mayor a menor.
  const conCarga = textos
    .map((t, i) => ({ i, n: Number(t.match(/(\d+)\s*$/)?.[1] ?? 0), nombre: t.replace(/\s*\d+\s*$/, '').trim() }))
    .filter(x => x.n > 0)
    .sort((a, b) => b.n - a.n);
  test.skip(conCarga.length === 0, 'El día cargado no tiene servicios: nada que filtrar.');

  // 1er clic → la grilla queda SOLO con ese estado.
  const [uno, dos] = conCarga;
  await botones.nth(uno.i).click();
  await page.waitForTimeout(600);
  await expect(page.locator('.estado-foco')).toBeVisible();
  const estados = await page.locator('.trafico-grid tbody .estado-chip').evaluateAll(
    els => Array.from(new Set(els.map(e => e.textContent?.trim() ?? ''))));
  expect(estados).toEqual([uno.nombre]);
  expect(Number((await pie.textContent())?.match(/^(\d+) de/)?.[1] ?? -1)).toBe(uno.n);

  // 2º clic en OTRO estado → se SUMA (sin Ctrl). Es la multi-selección que pidió el usuario.
  if (dos) {
    await botones.nth(dos.i).click();
    await page.waitForTimeout(600);
    expect(Number((await pie.textContent())?.match(/^(\d+) de/)?.[1] ?? -1)).toBe(uno.n + dos.n);
    const estados2 = await page.locator('.trafico-grid tbody .estado-chip').evaluateAll(
      els => Array.from(new Set(els.map(e => e.textContent?.trim() ?? ''))));
    expect(estados2.sort()).toEqual([uno.nombre, dos.nombre].sort());

    // Reclic en el primero → lo SACA, queda solo el segundo.
    await botones.nth(uno.i).click();
    await page.waitForTimeout(600);
    expect(Number((await pie.textContent())?.match(/^(\d+) de/)?.[1] ?? -1)).toBe(dos.n);
  }

  // Quitar el filtro desde el chip → vuelve el total del día.
  await page.locator('.estado-foco__quitar').click();
  await page.waitForTimeout(600);
  await expect(page.locator('.estado-foco')).toHaveCount(0);
  expect(Number((await pie.textContent())?.match(/^(\d+) de/)?.[1] ?? -1)).toBe(total);
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

// ── Sistema: Parámetros Empresa y Generales (solo lectura + andamiaje ABM) ──
// Una sola página con 2 solapas: las dos pantallas del FoxPro escriben la MISMA fila
// de `parametro`. Ver docs/PlanoFoxPro/sistema/PARAMETROS.md.

test('Parámetros del sistema — solapa Empresa con los datos cargados', async ({ page }) => {
  await irA(page, '/parametros');
  await expect(page).not.toHaveURL(/\/login/);
  // Los datos de la fila única de `parametro` llegaron a la pantalla.
  await expect(page.getByLabel('Nombre', { exact: true })).toHaveValue(/\S/, { timeout: 15_000 });
  await expect(page.getByLabel('Nº de CUIT')).toHaveValue(/^\d{2}-\d{8}-\d$/);
  // parametro ya es de Buslink (watcher apagado): la escritura está HABILITADA.
  await expect(page.getByRole('button', { name: /^Grabar/i })).toBeEnabled();
});

test('Parámetros del sistema — solapa Generales con contadores editables y su aviso', async ({ page }) => {
  await irAInteractivo(page, '/parametros');
  await page.getByRole('tab', { name: 'Generales' }).click();
  await expect(page.getByText('Contadores y rutas del Metrocar')).toBeVisible({ timeout: 15_000 });
  // Los contadores son editables desde el 12/08/2026, pero con aviso de divergencia.
  await expect(page.getByLabel('Último Lote de Plantillas')).toBeEditable();
  await expect(page.getByText(/las dos copias divergen/i)).toBeVisible();
});

test('Parámetros del sistema — solapa GPS con el diagnóstico', async ({ page }) => {
  await irAInteractivo(page, '/parametros');
  await page.getByRole('tab', { name: 'GPS' }).click();
  await expect(page.getByText('Conexión al SQL del GPS')).toBeVisible({ timeout: 15_000 });
  // Los 2 diagnósticos de lectura están disponibles aunque la escritura esté apagada…
  await expect(page.getByRole('button', { name: /Probar conexión/i })).toBeEnabled();
  await expect(page.getByRole('button', { name: /Ver últimas filas/i })).toBeEnabled();
  // …y el destructivo NO (GpsTruncateActivo = false).
  await expect(page.getByRole('button', { name: /Vaciar tabla/i })).toBeDisabled();
});
