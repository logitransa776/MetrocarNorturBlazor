// Switcher de themes NORTUR — "clasico" (default) / "gris" (Compacto gris).
// Aplica la clase en <body> (las variables CSS están en app.css) y
// persiste la elección por navegador en localStorage.
window.norturTheme = {
    get: () => localStorage.getItem('nortur-theme') || 'clasico',
    set: (nombre) => {
        localStorage.setItem('nortur-theme', nombre);
        document.body.classList.toggle('theme-gris', nombre === 'gris');
    },
    init: () => {
        document.body.classList.toggle('theme-gris', window.norturTheme.get() === 'gris');
    }
};
window.norturTheme.init();
