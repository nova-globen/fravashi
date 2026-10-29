// Owns the circuit-loss experience (directive 2026-07-26): the stock Blazor reconnect/resume
// dialog must never appear in the studio window. When the circuit drops, a studio-styled overlay shows,
// reconnection retries automatically, a reachable server whose circuit is gone triggers a full reload
// (hot exit restores unsaved buffers, the tab session restores itself), and a server that stays
// unreachable turns the overlay into restart guidance while polling keeps going: the moment the
// service is back, the window reloads by itself. Loaded after blazor.web.js (autostart="false");
// this file is the one that calls Blazor.start with the custom reconnection handler.
(function () {
    'use strict';

    const RESTART_ADVICE_AFTER_MS = 30000;
    const RETRY_DELAY_MS = 2000;
    // While the service is down the poll backs off to this, so a window left open on a stopped studio checks a few
    // times a minute and comes back within ten seconds of it.
    const MAX_RETRY_DELAY_MS = 10000;

    let overlay = null;
    let titleEl = null;
    let detailEl = null;
    let recovering = false;

    function ensureOverlay() {
        if (overlay) {
            return;
        }
        overlay = document.createElement('div');
        overlay.className = 'fravashi-reconnect-overlay';
        overlay.setAttribute('data-testid', 'reconnect-overlay');
        const card = document.createElement('div');
        card.className = 'fravashi-reconnect-card';
        const spinner = document.createElement('div');
        spinner.className = 'fravashi-reconnect-spinner';
        titleEl = document.createElement('h2');
        detailEl = document.createElement('p');
        card.appendChild(spinner);
        card.appendChild(titleEl);
        card.appendChild(detailEl);
        overlay.appendChild(card);
        document.body.appendChild(overlay);
    }

    function show(title, detail) {
        ensureOverlay();
        titleEl.textContent = title;
        detailEl.textContent = detail;
        overlay.style.display = 'grid';
    }

    function hide() {
        if (overlay) {
            overlay.style.display = 'none';
        }
    }

    async function serverHealthy() {
        try {
            const response = await fetch('healthz', { cache: 'no-store' });
            return response.ok;
        } catch (e) {
            return false;
        }
    }

    // The health check comes FIRST and the circuit is only touched once the service answers. Every failed
    // Blazor.reconnect logs five lines of its own to the console, which the window forwards to its log, and a studio
    // left stopped for an afternoon wrote 21,934 of them (2026-10-04, when the container engine changed under it).
    // A failed fetch here is caught and logs nothing, so a window waiting on a stopped studio is now silent.
    async function recover() {
        if (recovering) {
            return;
        }
        recovering = true;
        const started = Date.now();
        let delay = RETRY_DELAY_MS;
        show('Reconnecting to the studio…',
            'The connection dropped. Reconnecting automatically; nothing is lost.');
        while (recovering) {
            if (await serverHealthy()) {
                try {
                    if (await Blazor.reconnect()) {
                        // onConnectionUp hides the overlay and resets the flag.
                        return;
                    }
                } catch (e) {
                    // It answered a moment ago; a fresh load is the surest way back.
                }
                // The service is reachable but this session ended on the server: a fresh load gets a
                // new session, and hot exit brings unsaved buffers back.
                show('Reloading the studio…', 'The previous session ended; loading a fresh one.');
                location.reload();
                return;
            }
            if (Date.now() - started > RESTART_ADVICE_AFTER_MS) {
                show('The studio service is not responding',
                    'Automatic reconnection keeps failing. Restart Fravashi from the Start menu or '
                    + 'the tray. This window keeps checking and reloads by itself once the service is back.');
            }
            await new Promise(function (resolve) { setTimeout(resolve, delay); });
            delay = Math.min(delay * 2, MAX_RETRY_DELAY_MS);
        }
    }

    window.fravashiReconnect = {
        down: function () { recover(); },
        up: function () {
            recovering = false;
            hide();
        }
    };

    Blazor.start({
        circuit: {
            reconnectionHandler: {
                onConnectionDown: function () { window.fravashiReconnect.down(); },
                onConnectionUp: function () { window.fravashiReconnect.up(); }
            }
        }
    });
})();
