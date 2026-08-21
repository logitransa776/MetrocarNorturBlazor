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
// Posiciona la Planilla de Tráfico en la hora actual al abrirla (o al apretar HOY).
//
// Réplica del PROCEDURE ir_hora_actual de trafico2.scx: el FoxPro recorre el cursor
// desde arriba y se planta en el primer servicio cuya hs_inicio todavía no pasó.
// Como VFP desplaza la grilla lo mínimo necesario, esa fila le queda pegada al
// renglón de ABAJO y no se ve nada de lo que viene.
//
// Acá lo dejamos CENTRADO (decisión del usuario, 29/07/2026): la fila de "ahora"
// va a mitad de grilla, con los servicios corriendo arriba y los que vienen abajo.
// Medido sobre un día real de 323 servicios, es la única variante que muestra
// servicios futuros a toda hora — las otras tres dejan la mitad de abajo vacía —
// y la ventana de tiempo se ajusta sola a la densidad de la hora (en el pico de la
// mañana son ~30 min para cada lado; en una hora floja, más de 2 h).
//
// Igual que traficoScrollFila, NO se puede usar scrollIntoView: con <Virtualize>
// la fila destino puede no existir todavía en el DOM. Se calcula por aritmética.
//   selectorWrap : contenedor scrolleable de la grilla (.trafico-wrap--nav)
//   idxAhora     : posición 0-based del primer servicio que aún no arrancó
//   itemSize     : alto de fila en px (debe coincidir con ItemSize de <Virtualize>)
window.traficoIrHoraActual = (selectorWrap, idxAhora, itemSize) => {
    const wrap = document.querySelector(selectorWrap);
    if (!wrap || idxAhora < 0) return;

    // Cortar cualquier animación de rueda en vuelo: este salto manda.
    if (wrap._traficoRaf) {
        cancelAnimationFrame(wrap._traficoRaf);
        wrap._traficoRaf = null;
        wrap._traficoDestino = null;
    }

    // Va en DOS PASOS, y el segundo es el que manda.
    //
    // POR QUÉ: el primer intento calculaba todo por aritmética —
    // (idxAhora - filasVisibles/2) * itemSize — y quedó ~18 filas más arriba de lo
    // debido (29/07/2026). El culpable es wrap.clientHeight: en el render inicial la
    // cadena flex de body.planilla-fixed todavía no terminó de asentarse y el
    // contenedor mide bastante más de lo que va a medir un instante después, así que
    // "media pantalla" sale mal. Cualquier fórmula que dependa de ese número en ese
    // momento es frágil.
    //
    //   Paso 1 — scrollTop = idxAhora * itemSize. NO usa el alto para nada: solo deja
    //            la fila arriba de todo. Su único trabajo es que <Virtualize> renderice
    //            ese tramo y la fila exista en el DOM.
    //   Paso 2 — cuando la fila aparece (viaje de ida y vuelta por SignalR), se la
    //            centra midiendo el rect REAL de la fila y del contenedor. Inmune a que
    //            el alto haya cambiado y a que la fila no mida exactamente itemSize.
    const nro = idxAhora + 1;   // data-nro es 1-based (la columna # visible se sacó el 18/08/2026;
                                // el atributo del <tr> sigue existiendo justo para esto)

    wrap.scrollTop = idxAhora * itemSize;

    let intentos = 60;   // ~1s a 60fps: de sobra para el round-trip de <Virtualize>

    const centrar = () => {
        const fila = wrap.querySelector('tr.tg-row[data-nro="' + nro + '"]');

        if (!fila) {
            if (--intentos > 0) requestAnimationFrame(centrar);
            return;   // se agotó: queda el paso 1 (la fila arriba), que ya es útil
        }

        const rf = fila.getBoundingClientRect();
        const rw = wrap.getBoundingClientRect();

        // Cuánto hay que mover el scroll para que el centro de la fila coincida con el
        // centro del área visible. El clamp del navegador se encarga de los bordes
        // (principio y fin del día).
        const delta = (rf.top + rf.height / 2) - (rw.top + rw.height / 2);
        if (Math.abs(delta) > 1) wrap.scrollTop += delta;
    };

    requestAnimationFrame(centrar);
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

// ─────────────────────────────────────────────────────────────────────────────
// SUAVIZADO DEL SCROLL DE LOS DESPLEGABLES (combos de la barra de Tráfico)
// ─────────────────────────────────────────────────────────────────────────────
// Los combos Empresa/Interno pasaron de <select> nativo a MudAutocomplete (19/08/2026).
// Motivo: la lista de un <select> nativo la dibuja el SISTEMA OPERATIVO, fuera del DOM —
// no la alcanza ni el CSS ni el JS, así que su scroll a saltos era intocable. El popover
// de MudBlazor sí es un <div> del DOM, y acá le aplicamos el mismo easing de la grilla.
//
// Diferencia con traficoSuavizarScroll: acá NO se ralentiza la distancia (factor 1.0),
// solo se anima. En la grilla el freno existe para darle tiempo a <Virtualize>; una lista
// de 86 ítems ya está entera en el DOM, frenarla se sentiría pesado.
//
// Va como listener ÚNICO sobre document (capture) porque el popover se crea y se destruye
// en cada apertura: enganchar el nodo sería una carrera. En cada rueda se resuelve el
// contenedor scrolleable a partir del target.
const _popoverScroll = new WeakMap();

window.suavizarScrollPopover = (selectorPopover, activar) => {
    if (!activar) {
        if (window._popoverWheel) {
            document.removeEventListener('wheel', window._popoverWheel, { capture: true });
            delete window._popoverWheel;
        }
        return;
    }
    if (window._popoverWheel) return;   // idempotente

    const onWheel = (e) => {
        if (e.ctrlKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
        if (e.deltaMode === 0 && Math.abs(e.deltaY) < 40) return;   // trackpad: ya es suave

        const pop = e.target instanceof Element ? e.target.closest(selectorPopover) : null;
        if (!pop) return;

        // El nodo scrolleable no siempre es el popover: MudBlazor mete la lista adentro.
        // Se busca hacia arriba desde el target, sin salirse del popover.
        let el = e.target;
        while (el && el !== pop.parentElement) {
            if (el instanceof Element && el.scrollHeight - el.clientHeight > 1) {
                const ov = getComputedStyle(el).overflowY;
                if (ov === 'auto' || ov === 'scroll') break;
            }
            el = el.parentElement;
        }
        if (!el || el === pop.parentElement) return;

        let delta = e.deltaY;
        if (e.deltaMode === 1) delta *= 20;
        else if (e.deltaMode === 2) delta *= el.clientHeight;

        const maxScroll = el.scrollHeight - el.clientHeight;
        const est = _popoverScroll.get(el) ?? {};
        const base = est.destino ?? el.scrollTop;
        // Pegado al borde: no capturamos, así el navegador encadena como siempre.
        if ((base <= 0 && delta < 0) || (base >= maxScroll && delta > 0)) return;

        e.preventDefault();
        est.destino = Math.max(0, Math.min(maxScroll, base + delta));
        _popoverScroll.set(el, est);
        if (est.raf) return;

        const paso = () => {
            const restante = est.destino - el.scrollTop;
            if (Math.abs(restante) <= TRAFICO_SCROLL_EPSILON) {
                el.scrollTop = est.destino;
                est.raf = null;
                est.destino = null;
                return;
            }
            el.scrollTop += restante * TRAFICO_SCROLL_EASING;
            est.raf = requestAnimationFrame(paso);
        };
        est.raf = requestAnimationFrame(paso);
    };

    document.addEventListener('wheel', onWheel, { passive: false, capture: true });
    window._popoverWheel = onWheel;
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
// comprobante "Genera Resumen" de Liquidación a Clientes y los botones "PDF" de los
// paneles de informes (ej. Viajes por chofer) para imprimir/guardar PDF sin arrastrar
// el resto de la app (drawer, AppBar, etc.). Copia los <link>/<style> del documento
// para conservar el formato original.
//   idElemento  : id del contenedor a imprimir (todo lo de adentro sale en el PDF)
//   titulo      : título de la ventana/pestaña de impresión
//   orientacion : 'portrait' (default, comprobantes tipo factura) | 'landscape'
//                 (gráficos/tablas anchas — ver el botón PDF de Viajes por chofer)
//   margenMm    : margen de hoja en mm (default 12).
//                 ⚠ Pasar 0 tiene un efecto extra buscado a propósito: Chrome dibuja SU
//                 encabezado/pie (fecha · about:blank · nº de hoja) en el ÁREA DE MARGEN de la
//                 hoja; sin margen no le queda dónde ponerlos y desaparecen. Quien pase 0 se
//                 hace cargo del respiro de la hoja con padding propio (ver .pdf-hoja).
window.imprimirElemento = (idElemento, titulo, orientacion, margenMm) => {
    const nodo = document.getElementById(idElemento);
    if (!nodo) return;

    const estilos = Array.from(document.querySelectorAll('link[rel="stylesheet"], style'))
        .map(el => el.outerHTML)
        .join('\n');
    const orient = orientacion === 'landscape' ? 'landscape' : 'portrait';
    const margen = Number.isFinite(margenMm) ? margenMm : 12;

    const win = window.open('', '_blank', 'width=900,height=700');
    if (!win) return;   // bloqueado por el navegador

    win.document.open();
    win.document.write(
        '<!DOCTYPE html><html lang="es"><head><meta charset="utf-8">' +
        '<title>' + (titulo ?? 'Comprobante') + '</title>' +
        estilos +
        // !important en el fondo: la hoja de estilos de la app pinta `html, body` con el gris
        // "Piedra" del fondo de página, y esa regla también viaja a esta ventana. Sin el
        // !important quedaba una franja gris al pie de la hoja impresa.
        '<style>@page{size:A4 ' + orient + ';margin:' + margen + 'mm;}' +
        'html,body{background:#fff !important;margin:0;padding:0;}</style>' +
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

/* ═══════════════════════════════════════════════════════════════════════════════
   Tooltip del Tablero de ocupación de flota (01/08/2026).

   Por qué no el `title` nativo: no se puede estilar, aparece con retraso y con 350
   barras el texto largo queda ilegible. Por qué no un componente Blazor por barra:
   sería un round-trip de SignalR por cada hover — la muerte en Blazor Server.

   Solución: listeners DELEGADOS en document (se enganchan una sola vez, funcionan
   aunque el diálogo se abra después) + una tarjeta única que se recicla. Los datos
   viajan en los data-tip-* que pinta OcupacionFlotaDialog.razor, y se escriben con
   textContent (nunca innerHTML: son datos de la base, no markup).
   ═══════════════════════════════════════════════════════════════════════════════ */
(function () {
    let tip = null;         // la tarjeta (una sola, reciclada)
    let barra = null;       // barra sobre la que está el mouse

    // [clave del dataset, etiqueta visible] de la grilla etiqueta/valor.
    const CAMPOS = [
        ['tipCliente', 'Cliente'],
        ['tipPax', 'Pax'],
        ['tipChofer', 'Chofer']
    ];

    const nodo = (tag, clase, texto) => {
        const n = document.createElement(tag);
        if (clase) n.className = clase;
        if (texto !== undefined && texto !== null && texto !== '') n.textContent = texto;
        return n;
    };

    function armar(el) {
        const d = el.dataset;
        tip.textContent = '';

        const hd = nodo('div', 'ocup-tip__hd');
        hd.append(nodo('span', 'ocup-tip__u', d.tipTitulo),
                  nodo('span', 'ocup-tip__dur', d.tipDur));

        const body = nodo('div', 'ocup-tip__body');
        body.appendChild(nodo('div', 'ocup-tip__rec', d.tipRec));

        const grid = nodo('div', 'ocup-tip__grid');
        for (const [clave, etiqueta] of CAMPOS) {
            grid.append(nodo('span', 'ocup-tip__k', etiqueta),
                        nodo('span', 'ocup-tip__v', d[clave] || '—'));
        }
        body.appendChild(grid);

        const pie = nodo('div', 'ocup-tip__pie');
        pie.append(nodo('span', 'ocup-tip__chip', d.tipEstado),
                   nodo('span', null, d.tipTipo),
                   nodo('span', null, 'viaje ' + d.tipViaje));
        body.appendChild(pie);

        if (d.tipAviso) body.appendChild(nodo('div', 'ocup-tip__aviso', '⚠ ' + d.tipAviso));
        body.appendChild(nodo('div', 'ocup-tip__cta', 'Clic para abrir el Zoom del Viaje'));

        tip.append(hd, body);
    }

    // Sigue al mouse y se da vuelta contra los bordes para no salirse de la ventana.
    function ubicar(e) {
        const r = tip.getBoundingClientRect();
        let x = e.clientX + 16;
        let y = e.clientY + 18;
        if (x + r.width > window.innerWidth - 10) x = e.clientX - r.width - 16;
        if (y + r.height > window.innerHeight - 10) y = e.clientY - r.height - 18;
        tip.style.left = Math.max(8, x) + 'px';
        tip.style.top = Math.max(8, y) + 'px';
    }

    function ocultar() {
        barra = null;
        if (tip) tip.classList.remove('ocup-tip--on');
    }

    const barraDe = (t) => (t && t.closest) ? t.closest('.ocup-bar') : null;

    document.addEventListener('mouseover', (e) => {
        const el = barraDe(e.target);
        if (!el) { if (barra) ocultar(); return; }
        if (el === barra) return;
        barra = el;
        if (!tip) {
            tip = nodo('div', 'ocup-tip');
            tip.setAttribute('role', 'tooltip');
            document.body.appendChild(tip);
        }
        armar(el);
        tip.classList.add('ocup-tip--on');
        ubicar(e);
    });

    document.addEventListener('mousemove', (e) => {
        if (!barra) return;
        // Si el diálogo se cerró con el mouse encima, la barra ya no está en el DOM.
        if (!barra.isConnected) { ocultar(); return; }
        ubicar(e);
    });

    // Al clickear se abre el Zoom del Viaje: la tarjeta tiene que irse.
    document.addEventListener('click', ocultar, true);
    // El scroll del tablero mueve las barras pero no dispara mousemove.
    window.addEventListener('scroll', ocultar, true);
})();

// ─────────────────────────────────────────────────────────────────────────────
// Campanita de los avisos de Tráfico (F4 · alarma por hora).
// El FoxPro hace `?Chr(7)` (formInicioServicio) o toca ringin.wav (trafico_aviso.scx). Acá se
// sintetiza con WebAudio: dos tonos cortos, sin ningún archivo de audio que servir.
//
// ⚠️ Política de autoplay: el navegador mantiene el AudioContext suspendido hasta que el
// usuario interactúe con la página. En la Planilla de Tráfico eso pasa enseguida (se hace clic
// para todo), pero si el aviso cae antes del primer clic, el sonido no suena y la alarma se ve
// igual. Nunca tira error: falla en silencio a propósito.
// ─────────────────────────────────────────────────────────────────────────────
let _traficoAudioCtx = null;

window.traficoBeep = function () {
    try {
        const AC = window.AudioContext || window.webkitAudioContext;
        if (!AC) return;
        if (!_traficoAudioCtx) _traficoAudioCtx = new AC();
        const ctx = _traficoAudioCtx;
        if (ctx.state === 'suspended') ctx.resume();

        // Dos pulsos (agudo-grave) de 120 ms, separados 160 ms: se distingue del resto de los
        // sonidos de Windows sin ser molesto en una oficina.
        const tonos = [[880, 0.0], [660, 0.16]];
        for (const [hz, offset] of tonos) {
            const osc = ctx.createOscillator();
            const gain = ctx.createGain();
            osc.type = 'sine';
            osc.frequency.value = hz;
            const t0 = ctx.currentTime + offset;
            // Rampa de volumen: sin esto el corte seco del oscilador hace un "click".
            gain.gain.setValueAtTime(0.0001, t0);
            gain.gain.exponentialRampToValueAtTime(0.18, t0 + 0.02);
            gain.gain.exponentialRampToValueAtTime(0.0001, t0 + 0.12);
            osc.connect(gain).connect(ctx.destination);
            osc.start(t0);
            osc.stop(t0 + 0.14);
        }
    } catch (e) {
        // Audio bloqueado o no soportado: la alarma visual alcanza.
    }
};

/* ── Ayuda contextual de informes (11/08/2026) ──────────────────────────────
   Salta a la sección del modal cuando la ayuda se abre desde el ⓘ de un panel.
   Va acá y no en un archivo nuevo por la misma razón que el resto de los
   helpers: un <script> más es una request más en cada carga. */
window.norturAyuda = {
    scrollA: function (id) {
        // El modal se monta en el siguiente frame: esperamos uno antes de buscar el nodo.
        requestAnimationFrame(function () {
            var el = document.getElementById(id);
            if (!el) return;
            var cont = document.getElementById('ayuda-rep-scroll');
            if (!cont) { el.scrollIntoView({ block: 'start' }); return; }
            // scrollTop manual en vez de scrollIntoView: el dialog de MudBlazor tiene su
            // propio contenedor con overflow y scrollIntoView le mueve TODO el modal.
            cont.scrollTop = el.offsetTop - cont.offsetTop;
        });
    }
};
