// Bridge to the studio window host (ADR 0014). Present in every page load but inert outside the window
// shell: hosted detection is simply whether the WebView2 message bridge exists. The host answers
// window-state changes with {"maximized":bool}; the html.window-maximized class drives the
// maximize/restore icon swap in CSS. The html.window-host class itself is added by the no-flash
// bootstrap (bootstrap.js) before first paint.
window.fravashiWindow = (function () {
    'use strict';

    var hosted = !!(window.chrome && window.chrome.webview);

    function post(cmd) {
        if (hosted) {
            window.chrome.webview.postMessage({ cmd: cmd });
        }
    }

    if (hosted) {
        window.chrome.webview.addEventListener('message', function (e) {
            var data = e.data;
            if (data && typeof data.maximized === 'boolean') {
                document.documentElement.classList.toggle('window-maximized', data.maximized);
            }
        });
    }

    return {
        isHosted: function () { return hosted; },
        minimize: function () { post('minimize'); },
        toggleMaximize: function () { post('maximize'); },
        close: function () { post('close'); },
        // The studio names the native caption after the circuit's active workspace
        // ("<workspace> - Fravashi", or plain "Fravashi"); an older host ignores the command.
        setTitle: function (title) {
            if (hosted) {
                window.chrome.webview.postMessage({ cmd: 'title', title: String(title || '') });
            }
        },
        // Backlog 29: the title bar's workspace menu. The HOST owns which window shows which folder
        // (one window per folder, at most one home window), so both only ask; each returns whether a
        // host was there to ask, and the caller navigates itself when there was not.
        closeWorkspace: function () {
            if (hosted) {
                window.chrome.webview.postMessage({ cmd: 'closeWorkspace' });
            }
            return hosted;
        },
        switchWorkspace: function (ws) {
            if (hosted) {
                window.chrome.webview.postMessage({ cmd: 'switchWorkspace', ws: String(ws || '') });
            }
            return hosted;
        }
    };
})();

// Backlog 287: the title bar's buttons (WindowTitleBar.razor, MinimalWindowLayout.razor) name an action in
// data-window-action instead of carrying an inline onclick, which the studio's Content-Security-Policy refuses. One
// listener on the document serves every render of them, so they still work while the circuit is reconnecting, and
// the action is looked up in this closed set, never evaluated.
(function () {
    'use strict';

    function code(name) {
        var editor = window.fravashiCode;
        if (editor && typeof editor[name] === 'function') {
            editor[name]();
        }
    }

    var actions = {
        back: function () { history.back(); },
        forward: function () { history.forward(); },
        undo: function () { code('editorUndo'); },
        redo: function () { code('editorRedo'); },
        palette: function () { code('openCommandPalette'); },
        minimize: function () { window.fravashiWindow.minimize(); },
        maximize: function () { window.fravashiWindow.toggleMaximize(); },
        close: function () { window.fravashiWindow.close(); }
    };

    document.addEventListener('click', function (e) {
        var target = e.target && e.target.nodeType === 1 ? e.target.closest('[data-window-action]') : null;
        if (!target) {
            return;
        }

        var name = target.getAttribute('data-window-action');
        if (Object.prototype.hasOwnProperty.call(actions, name)) {
            actions[name]();
        }
    });
})();

// Backlog 287: a mockup preview's checkbox and radio controls are pictures of a control, so a click must not change
// them. They carry data-mk-inert instead of an inline onclick="return false", which the policy refuses.
document.addEventListener('click', function (e) {
    var target = e.target && e.target.nodeType === 1 ? e.target.closest('input[data-mk-inert]') : null;
    if (target) {
        e.preventDefault();
    }
});
