import { test, expect } from '@playwright/test';
import { login, irA } from './helpers';

/**
 * AUDITOR DE RESPONSIVIDAD — el test que convierte "se ve bien" en un hecho medible.
 *
 * Por qué existe: hasta el 11/08/2026 las tarjetas KPI se recortaban a 1360×768 y nadie
 * se enteraba hasta que un usuario lo veía en pantalla. El truncado de texto NO es
 * detectable leyendo el CSS —depende de la fuente, del dato real y del ancho que le tocó
 * a cada tarjeta—, pero SÍ es medible en el navegador: un elemento está recortado cuando
 * su scrollWidth supera su clientWidth. Eso es lo que chequea este test.
 *
 * CONTRATO DE PANTALLA (ver skill `responsive-nortur`):
 *   Resolución mínima soportada = 1360×768 físicos. Como en NORTUR conviven PCs con
 *   escala de Windows al 100% y al 125%, el PEOR CASO real es 1088×614 px CSS
 *   (1360/1.25 × 768/1.25). Se auditan las dos.
 *
 * Correr:  $env:NORTUR_USER="SUPERVISOR"; $env:NORTUR_PASS="..."; npx playwright test tests/responsive.spec.ts
 * Agregar una pantalla nueva: sumar su ruta a RUTAS. Nada más.
 */

const VIEWPORTS = [
  { width: 1360, height: 768, nombre: '1360x768 (escala 100%)' },
  { width: 1088, height: 614, nombre: '1088x614 (1360 fisico @125%)' },
];

// Las pantallas que usan la franja de KPIs (.rfs-kpis + KpiCard).
const RUTAS = [
  '/libro-novedades', '/envio-correos', '/correos-destinatarios',
  '/panel-flota', '/panel-clientes', '/panel-operador', '/panel-tercerizacion',
  '/reservas-fecha-servicio', '/reservas-banda-horaria',
  '/reservas-por-cliente', '/viajes-por-chofer', '/km-unidades-servicios', '/agenda-vencimientos',
  '/odometros', '/francos-auditoria', '/viaticos', '/guardias', '/voucher-recepcion',
  '/auditoria-accesos', '/consumo-mensual', '/promedio-consumos', '/control-cargas',
  '/combustible-conciliacion', '/saldos-estaciones', '/depositos-estacion',
  '/depositos-mantenimiento', '/resumen-liquidaciones', '/facturacion-estimada',
];

for (const vp of VIEWPORTS) {
  test(`KPIs sin truncar @ ${vp.nombre}`, async ({ page }) => {
    test.setTimeout(RUTAS.length * 12_000);
    await page.setViewportSize({ width: vp.width, height: vp.height });
    await login(page);

    const fallas: string[] = [];

    for (const ruta of RUTAS) {
      await irA(page, ruta);
      await page.waitForLoadState('load');
      // Los informes cargan datos async: sin este respiro se auditaría el esqueleto vacío.
      await page.waitForTimeout(2000);

      const r = await page.evaluate(() => {
        const out = { hayKpis: false, doc: document.documentElement.scrollWidth, cortes: [] as any[] };
        out.hayKpis = document.querySelectorAll('.rfs-kpis > *').length > 0;

        // Solo dentro de la franja de KPIs. La grilla de Tráfico trunca a propósito
        // (anchos fijos por colgroup, ver memoria grilla-anchos-colgroup-fixed): auditarla
        // acá daría ruido permanente y el test se volvería un test que todos ignoran.
        document.querySelectorAll('.rfs-kpis *').forEach((el: any) => {
          if (el.children.length > 0) return;
          const txt = (el.innerText || '').trim();
          if (!txt) return;
          const anchoMal = el.scrollWidth > el.clientWidth + 1;
          const altoMal = el.scrollHeight > el.clientHeight + 1;
          if (anchoMal || altoMal) {
            // Reportar la dimensión QUE FALLÓ. Antes se imprimía siempre el ancho, así que un
            // desborde de alto salía como "visible 33px, necesita 33px" (dos números iguales)
            // y mandaba a buscar el problema al lugar equivocado.
            out.cortes.push({
              txt: txt.slice(0, 40),
              eje: anchoMal ? 'ancho' : 'alto',
              visible: Math.round(anchoMal ? el.clientWidth : el.clientHeight),
              necesita: Math.round(anchoMal ? el.scrollWidth : el.scrollHeight),
            });
          }
        });
        return out;
      });

      if (!r.hayKpis) {
        console.log(`  ⓘ ${ruta}: sin KPIs visibles (¿sin datos en el período por defecto?)`);
        continue;
      }
      if (r.doc > vp.width) fallas.push(`${ruta}: DESBORDE horizontal (${r.doc}px sobre ${vp.width})`);
      for (const c of r.cortes) {
        fallas.push(`${ruta}: texto cortado "${c.txt}" — ${c.eje}: ${c.visible}px visibles de ${c.necesita}px`);
      }
      if (r.cortes.length === 0 && r.doc <= vp.width) console.log(`  ✅ ${ruta}`);
    }

    expect(fallas, `\nTruncados detectados a ${vp.nombre}:\n  - ${fallas.join('\n  - ')}\n`).toEqual([]);
  });
}
