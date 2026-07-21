// Copia texto al portapapeles — retorna true si tuvo éxito.
window.copiarAlPortapapeles = async (texto) => {
    try {
        await navigator.clipboard.writeText(texto);
        return true;
    } catch {
        return false;
    }
};

// Bloquea/libera el scroll de la ventana (clase en <body>). Lo usa la Planilla
// de Tráfico: con la clase puesta, la página ocupa exactamente el viewport y la
// ÚNICA región que scrollea es la grilla de viajes (no la ventana). Es idempotente,
// así que da igual cuántas veces se llame al montar/desmontar la página.
window.bloquearScrollVentana = (bloquear) => {
    document.body.classList.toggle('planilla-fixed', !!bloquear);
};

// Descarga un archivo desde un stream .NET (usado para el export a Excel).
window.descargarArchivo = async (nombreArchivo, streamRef) => {
    const arrayBuffer = await streamRef.arrayBuffer();
    const blob = new Blob([arrayBuffer]);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = nombreArchivo ?? 'archivo';
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
};

// Mantiene visible la fila activa de la Planilla de Tráfico al navegar con el teclado
// (flechas ↑/↓). La grilla usa <Virtualize>: la fila destino puede NO existir en el DOM,
// así que NO se hace scrollIntoView (fallaría). Se calcula la posición por aritmética:
//   top de la fila = indice * itemSize   (itemSize = alto fijo de fila, 22px)
// y se ajusta el scrollTop del contenedor SOLO si la fila quedó fuera del viewport,
// dejándola pegada al borde superior o inferior según hacia dónde se navegó.
// Devolver el scroll calculado es barato y no fuerza reflow innecesario.
//   selectorWrap : selector del contenedor scrolleable (.trafico-wrap de la grilla principal)
//   indice       : posición 0-based de la fila activa dentro de la lista visible
//   itemSize     : alto de fila en px (debe coincidir con ItemSize de <Virtualize>)
window.traficoScrollFila = (selectorWrap, indice, itemSize) => {
    const wrap = document.querySelector(selectorWrap);
    if (!wrap || indice < 0) return;

    // Si venía una animación de rueda en vuelo (traficoSuavizarScroll), cortarla: el
    // scroll por teclado manda y debe ser inmediato. Sin esto, la animación seguiría
    // corriendo hacia su destino viejo y pisaría el salto que hacemos acá abajo.
    if (wrap._traficoRaf) {
        cancelAnimationFrame(wrap._traficoRaf);
        wrap._traficoRaf = null;
        wrap._traficoDestino = null;
    }

    const filaTop = indice * itemSize;
    const filaBottom = filaTop + itemSize;
    const vistaTop = wrap.scrollTop;
    const vistaBottom = vistaTop + wrap.clientHeight;

    if (filaTop < vistaTop) {
        // La fila quedó por encima del viewport → pegarla al borde superior.
        wrap.scrollTop = filaTop;
    } else if (filaBottom > vistaBottom) {
        // La fila quedó por debajo → traerla justo al borde inferior.
        wrap.scrollTop = filaBottom - wrap.clientHeight;
    }
    // Si ya estaba visible, no se toca el scroll (evita saltos innecesarios).
};

// ─────────────────────────────────────────────────────────────────────────────
// Suavizado del scroll de rueda en la grilla de Tráfico.
//
// POR QUÉ: la grilla usa <Virtualize>. Al scrollear, el navegador ya movió el scroll
// pero las filas nuevas todavía no existen en el DOM: hay que ir y volver por SignalR
// para que el servidor renderice el tramo. Si el scroll avanza más rápido que ese
// round-trip, se ve el hueco en blanco. No es un bug de pintado, es una CARRERA.
//
// QUÉ HACE: intercepta la rueda, aplica el delta multiplicado por FACTOR (más lento)
// y lo anima con requestAnimationFrame en vez de saltar de golpe. Las dos cosas ayudan:
//   · más lento  → el round-trip tiene tiempo de ganar la carrera;
//   · continuo   → el overscan de <Virtualize> (20 filas = 400px) absorbe el movimiento
//                  gradual mucho mejor que un salto seco de 100px por notch.
//
// COSTO CERO PARA EL SERVIDOR: es 100% cliente. No toca Blazor ni SignalR, así que
// NO puede degradar el click, el Zoom del Viaje ni el menú contextual — que es
// exactamente donde fracasaron los dos intentos anteriores (ver
// docs/performance/PENDIENTE_GRILLA_TRAFICO_BLANQUEO.md).
//
// TRACKPAD: no se toca. El trackpad ya entrega scroll continuo y de grano fino;
// ralentizarlo se siente roto. Se detecta por la firma del delta (ver esRueda).
// ─────────────────────────────────────────────────────────────────────────────

// Cuánto del movimiento original se conserva. 1 = sin cambio; más bajo = más lento.
// 0.70 = 30% más lento. Es la perilla a calibrar: si con 0.75 el blanco ya no molesta,
// mejor (menos intrusivo); si sigue apareciendo, bajar a 0.60.
const TRAFICO_SCROLL_FACTOR = 0.70;

// Cuánto se acerca el scroll al destino en cada frame (0-1). Más alto = llega antes
// pero más brusco. 0.22 da un frenado suave sin sensación de "arrastre".
const TRAFICO_SCROLL_EASING = 0.22;

// Distancia (px) bajo la cual se considera que ya llegamos y se corta la animación.
const TRAFICO_SCROLL_EPSILON = 0.5;

window.traficoSuavizarScroll = (selectorWrap, activar) => {
    const wrap = document.querySelector(selectorWrap);
    if (!wrap) return;

    // Desactivar / limpiar: se llama al salir de la página para no dejar el listener
    // colgado sobre un nodo huérfano (el circuito Blazor puede sobrevivir a la página).
    if (!activar) {
        if (wrap._traficoWheel) {
            wrap.removeEventListener('wheel', wrap._traficoWheel);
            if (wrap._traficoRaf) cancelAnimationFrame(wrap._traficoRaf);
            delete wrap._traficoWheel;
            delete wrap._traficoRaf;
            delete wrap._traficoDestino;
        }
        return;
    }

    if (wrap._traficoWheel) return;   // idempotente: ya está enganchado

    // Distingue rueda de mouse (saltos grandes y discretos) de trackpad (deltas chicos
    // y continuos). El trackpad se deja pasar sin tocar: ya scrollea suave de por sí.
    const esRueda = (e) => {
        if (e.deltaMode !== 0) return true;          // el driver manda líneas/páginas → rueda
        return Math.abs(e.deltaY) >= 40;             // píxeles en saltos grandes → rueda
    };

    const onWheel = (e) => {
        // Scroll horizontal, zoom con Ctrl o gesto de trackpad: que lo maneje el navegador.
        if (e.ctrlKey || Math.abs(e.deltaX) > Math.abs(e.deltaY) || !esRueda(e)) return;

        // deltaMode 1 = líneas, 2 = páginas. Se normaliza a píxeles antes de escalar.
        let delta = e.deltaY;
        if (e.deltaMode === 1) delta *= 20;                    // ~1 línea = alto de fila
        else if (e.deltaMode === 2) delta *= wrap.clientHeight;

        const maxScroll = wrap.scrollHeight - wrap.clientHeight;
        if (maxScroll <= 0) return;

        // Si ya estamos pegados al borde y el gesto empuja hacia afuera, no capturamos:
        // así el navegador puede encadenar el scroll hacia arriba (comportamiento normal).
        const base = wrap._traficoDestino ?? wrap.scrollTop;
        if ((base <= 0 && delta < 0) || (base >= maxScroll && delta > 0)) return;

        e.preventDefault();   // requiere el listener en modo { passive: false }

        // El destino se ACUMULA sobre el destino previo, no sobre scrollTop: si el usuario
        // gira la rueda varias veces seguidas, los notches se suman en vez de pisarse.
        wrap._traficoDestino = Math.max(0, Math.min(maxScroll, base + delta * TRAFICO_SCROLL_FACTOR));

        if (wrap._traficoRaf) return;   // ya hay una animación corriendo

        const paso = () => {
            const destino = wrap._traficoDestino;
            const restante = destino - wrap.scrollTop;

            if (Math.abs(restante) <= TRAFICO_SCROLL_EPSILON) {
                wrap.scrollTop = destino;
                wrap._traficoRaf = null;
                wrap._traficoDestino = null;
                return;
            }

            wrap.scrollTop += restante * TRAFICO_SCROLL_EASING;
            wrap._traficoRaf = requestAnimationFrame(paso);
        };

        wrap._traficoRaf = requestAnimationFrame(paso);
    };

    // passive:false es OBLIGATORIO: sin esto el navegador ignora el preventDefault()
    // y el scroll nativo se aplica igual (quedaría doble movimiento).
    wrap.addEventListener('wheel', onWheel, { passive: false });
    wrap._traficoWheel = onWheel;
};

// Abre una URL externa en una pestaña/ventana nueva — lo usa "Ubicar en GPS" de la
// Planilla de Tráfico (réplica del Shell.Open(lcClave) del FoxPro: el sistema viejo
// abre el sitio GPS embedded.sytes.net en el navegador). noopener/noreferrer evita
// que la pestaña del GPS tenga acceso a window.opener (buena práctica de seguridad).
// Devuelve false si el navegador bloqueó el pop-up (para poder avisar al usuario).
window.abrirEnNuevaPestana = (url) => {
    if (!url) return false;
    const win = window.open(url, '_blank', 'noopener,noreferrer');
    return win !== null;
};

// Imprime SOLO el elemento indicado (por id) en una ventana aislada — lo usa el
// comprobante "Genera Resumen" de Liquidación a Clientes para imprimir/guardar PDF
// sin arrastrar el resto de la app (drawer, AppBar, etc.). Copia los <link>/<style>
// del documento para conservar el formato del comprobante.
window.imprimirElemento = (idElemento, titulo) => {
    const nodo = document.getElementById(idElemento);
    if (!nodo) return;

    const estilos = Array.from(document.querySelectorAll('link[rel="stylesheet"], style'))
        .map(el => el.outerHTML)
        .join('\n');

    const win = window.open('', '_blank', 'width=900,height=700');
    if (!win) return;   // bloqueado por el navegador

    win.document.open();
    win.document.write(
        '<!DOCTYPE html><html lang="es"><head><meta charset="utf-8">' +
        '<title>' + (titulo ?? 'Comprobante') + '</title>' +
        estilos +
        '<style>@page{size:A4;margin:12mm;} body{background:#fff;margin:0;padding:0;}</style>' +
        '</head><body class="' + document.body.className + '">' +
        nodo.outerHTML +
        '</body></html>'
    );
    win.document.close();

    // Esperar a que el navegador aplique los estilos antes de imprimir.
    win.focus();
    setTimeout(() => {
        win.print();
        win.close();
    }, 350);
};
