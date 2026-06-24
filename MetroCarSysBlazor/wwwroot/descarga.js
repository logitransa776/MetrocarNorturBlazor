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
