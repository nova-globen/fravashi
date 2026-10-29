// Loaded synchronously in App.razor's head, before the stylesheets, so all of it runs before first paint. It was an
// inline script until backlog 287: the studio's Content-Security-Policy admits script files from 'self' only.

// The bridge release of the internal rename (docs/plans/rename-internal-names.md, phase 2): every key this window kept
// in localStorage before the rename (the theme, the sidebar, the editor's settings, its workspace and its unsaved-buffer
// backups, the sections) carried the former prefix, which App.razor hands over as data-former-storage. Each is copied to
// its new name ONCE, before anything below or any later script reads a key: a new key already there wins, and the former
// keys are left as they are (the cleanup release drops them with this block). Once only, so a key the studio later removes
// under its new name (a saved buffer's backup) is never brought back from the old copy.
(function () {
    try {
        var former = document.documentElement.getAttribute('data-former-storage');
        var done = 'fravashi-former-storage-carried';
        if (!former || localStorage.getItem(done) === '1') {
            return;
        }
        var keys = [];
        for (var i = 0; i < localStorage.length; i++) {
            var key = localStorage.key(i);
            if (key && key.indexOf(former) === 0) {
                keys.push(key);
            }
        }
        keys.forEach(function (key) {
            var renamed = 'fravashi' + key.slice(former.length);
            if (localStorage.getItem(renamed) === null) {
                localStorage.setItem(renamed, localStorage.getItem(key));
            }
        });
        localStorage.setItem(done, '1');
    } catch (e) { /* storage unavailable: nothing to carry over */ }
})();

// No-flash theme bootstrap - sets data-theme on <html> before first paint from the
// persisted choice (localStorage key "fravashi-theme"); studio.css keys off [data-theme].
window.fravashiTheme = {
    apply: function (mode) {
        try {
            mode = (mode || 'system').toLowerCase();
            var resolved = mode;
            if (mode === 'system') {
                resolved = (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) ? 'dark' : 'light';
            }
            document.documentElement.setAttribute('data-theme', resolved);
            document.documentElement.setAttribute('data-theme-mode', mode);
        } catch (e) { /* ignore */ }
    }
};
(function () {
    try {
        var stored = localStorage.getItem('fravashi-theme');
        var mode = 'system';
        if (stored) {
            try { mode = JSON.parse(stored).mode || 'system'; } catch (e) { mode = stored; }
        }
        window.fravashiTheme.apply(mode);
    } catch (e) { /* ignore */ }
})();
// No-flash page-split sidebar width (UI10) -- pageSplit.js (loaded at the end of body) owns the
// drag behavior; this just avoids a layout shift from the default width before it runs. Work item #364:
// the stored width is applied within the absolute 180 to 480 limits only, and studio.css caps what is
// RENDERED at min(480px, 25vw), the same rule pageSplit.js applies, so a stored 475 shows as 356 in a
// 1424px window and comes back in a wider one. The collapsed state is restored here too, before paint.
(function () {
    try {
        var stored = parseInt(localStorage.getItem('fravashi-page-sidebar-width'), 10);
        if (!isNaN(stored)) {
            var clamped = Math.min(480, Math.max(180, stored));
            document.documentElement.style.setProperty('--page-sidebar-width', clamped + 'px');
        }
        if (localStorage.getItem('fravashi-page-sidebar-collapsed') === '1') {
            document.documentElement.setAttribute('data-page-sidebar', 'collapsed');
        }
    } catch (e) { /* ignore */ }
})();
// No-flash window-shell detection (ADR 0014): inside the studio window the WebView2 bridge exists, and
// the html class shows the web-drawn title bar before first paint. windowShell.js adds behavior.
(function () {
    try {
        if (window.chrome && window.chrome.webview) {
            document.documentElement.classList.add('window-host');
        }
    } catch (e) { /* ignore */ }
})();
