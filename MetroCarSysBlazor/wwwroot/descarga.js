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
