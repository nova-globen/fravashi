// Home's arrange mode (work item #360): the two things Blazor's own events cannot do well.
//
//  * Resizing a card from its corner. The pointer is followed here, the card previews its new span locally, and only
//    when the pointer is let go is the result handed to the page: written as "id|width|height" into the arrange
//    panel's hidden input, followed by a change event, which the page's @onchange reads. So a resize is ONE change
//    and one save, never one per pixel, and nothing here calls into .NET or waits on it (a call made while the window
//    is disconnected would not be replayed; a lost change event just leaves the card as it was).
//  * The keys on a card's handle. The arrow keys would scroll the page as well as move the card, so they are
//    prevented here (Blazor cannot prevent a default for some keys only), and the page's @onkeydown does the move.
//    Moving a card moves its element, which can take focus with it; for a moment afterwards the handle of the card
//    that was moved gets focus back if focus fell to the page.
//
// Delegated on document and registered once, like pageSplit.js, so it survives every re-render. No CDN, no package.
(function () {
    'use strict';

    var HANDLE = 'data-home-card-handle';
    var RESIZE = 'data-home-card-resize';
    var CARD = 'data-home-card';
    var INPUT = 'data-home-layout-resize';
    var COLUMNS = 12;
    var MIN_WIDTH = 2;
    var MAX_HEIGHT = 8;
    var KEYS = { ArrowLeft: true, ArrowRight: true, ArrowUp: true, ArrowDown: true, Delete: true };

    function clamp(value, low, high) {
        return Math.min(high, Math.max(low, value));
    }

    function px(value, fallback) {
        var parsed = parseFloat(value);
        return isNaN(parsed) ? fallback : parsed;
    }

    // ---- refocus after a keyboard move ----------------------------------------------------------------------------

    var pending = null;
    var observer = null;
    var stopTimer = 0;

    function refocus() {
        if (!pending) {
            return;
        }
        var active = document.activeElement;
        if (active && active !== document.body && active.hasAttribute && active.hasAttribute(HANDLE)) {
            return;
        }
        if (active && active !== document.body) {
            return;
        }
        var handle = document.querySelector('[' + HANDLE + '="' + CSS.escape(pending) + '"]');
        if (handle) {
            handle.focus();
        }
    }

    function watchFocus(id) {
        pending = id;
        if (!observer && window.MutationObserver) {
            observer = new MutationObserver(refocus);
            observer.observe(document.body, { childList: true, subtree: true });
        }
        window.clearTimeout(stopTimer);
        stopTimer = window.setTimeout(function () {
            pending = null;
            if (observer) {
                observer.disconnect();
                observer = null;
            }
        }, 2000);
    }

    document.addEventListener('keydown', function (e) {
        var handle = e.target && e.target.closest ? e.target.closest('[' + HANDLE + ']') : null;
        if (!handle || !KEYS[e.key]) {
            return;
        }
        e.preventDefault();
        watchFocus(handle.getAttribute(HANDLE));
    });

    // ---- resize from the corner -----------------------------------------------------------------------------------

    var resizing = false;

    document.addEventListener('pointerdown', function (start) {
        var grip = start.target && start.target.closest ? start.target.closest('[' + RESIZE + ']') : null;
        if (!grip || start.button !== 0) {
            return;
        }
        var card = grip.closest('[' + CARD + ']');
        var grid = card && card.parentElement;
        if (!card || !grid) {
            return;
        }

        start.preventDefault();
        start.stopPropagation();

        var style = getComputedStyle(grid);
        var gap = px(style.columnGap, 16);
        var rowGap = px(style.rowGap, 16);
        var rowUnit = px(style.getPropertyValue('--home-row'), 80);
        var column = (grid.getBoundingClientRect().width - gap * (COLUMNS - 1)) / COLUMNS;
        var startWidth = parseInt(card.getAttribute('data-width'), 10) || COLUMNS;
        var startHeight = parseInt(card.getAttribute('data-height'), 10) || 1;
        var originalStyle = card.getAttribute('style');
        var width = startWidth;
        var height = startHeight;

        resizing = true;
        grip.setPointerCapture(start.pointerId);
        document.body.style.cursor = 'nwse-resize';
        document.body.style.userSelect = 'none';

        function onMove(move) {
            width = clamp(startWidth + Math.round((move.clientX - start.clientX) / (column + gap)), MIN_WIDTH, COLUMNS);
            height = clamp(startHeight + Math.round((move.clientY - start.clientY) / (rowUnit + rowGap)), 1, MAX_HEIGHT);
            card.style.setProperty('--home-w', String(width));
            card.style.setProperty('--home-h', String(height));
        }

        function onUp(up) {
            resizing = false;
            try { grip.releasePointerCapture(up.pointerId); } catch (e) { /* the grip went away */ }
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);
            document.removeEventListener('pointercancel', onUp);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';

            // Back to what the page drew; the page redraws the card at its new size once it has saved it.
            if (originalStyle === null) {
                card.removeAttribute('style');
            } else {
                card.setAttribute('style', originalStyle);
            }

            if (up.type === 'pointercancel' || (width === startWidth && height === startHeight)) {
                return;
            }
            var input = document.querySelector('[' + INPUT + ']');
            if (!input) {
                return;
            }
            input.value = card.getAttribute(CARD) + '|' + width + '|' + height;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }

        document.addEventListener('pointermove', onMove);
        document.addEventListener('pointerup', onUp);
        document.addEventListener('pointercancel', onUp);
    });

    // A drag that starts on the resize corner must not also start the card's own drag-and-drop. The event's target is
    // the draggable card, not the corner, hence the flag.
    document.addEventListener('dragstart', function (e) {
        if (resizing) {
            e.preventDefault();
        }
    });
})();
