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
