// Drag-to-resize for .page-split's sidebar (UI10). Delegated on document so it keeps working across
// Blazor re-renders that replace the .page-split subtree -- no per-instance JS interop call needed. The
// width lives on <html> as a CSS custom property, so it inherits into every current and future
// .page-split element on the page (studio.css also clamps it, in case this file is ever bypassed).
//
// Work item #364: the panel never takes more than a quarter of the window. studio.css caps the rendered
// width at min(480px, 25vw) live, so the STORED width is kept and a wider window gets it back; this file
// applies the same rule whenever it computes a new width (drag, arrow keys). Double-clicking the handle
// resets to the default, and the panel collapses (chevron, strip button, Ctrl+B) to a thin strip, a
// per-window preference kept on <html> as data-page-sidebar="collapsed" (bootstrap.js restores it pre-paint).
(function () {
    'use strict';

    var STORAGE_KEY = 'fravashi-page-sidebar-width';
    var COLLAPSED_KEY = 'fravashi-page-sidebar-collapsed';
    var DEFAULT_WIDTH = 264;
    var MIN_WIDTH = 180;
    var MAX_WIDTH = 480;
    var MAX_SHARE = 0.25;
    var STEP = 16;
    var root = document.documentElement;

    // The CSS rule, clamp(180px, value, min(480px, 25vw)): when the cap falls below the minimum, the
    // minimum wins, as it does in CSS.
    function clamp(value) {
        var max = Math.min(MAX_WIDTH, Math.floor(window.innerWidth * MAX_SHARE));
        return Math.max(MIN_WIDTH, Math.min(max, value));
    }

    function applyWidth(px) {
        root.style.setProperty('--page-sidebar-width', px + 'px');
    }

    function persistWidth(px) {
        try { localStorage.setItem(STORAGE_KEY, String(px)); } catch (e) { /* ignore */ }
    }

    // The stored preference, bounded only by the absolute limits: the window share is the stylesheet's
    // to apply, so a narrow window today does not shrink what a wide one shows tomorrow.
    function storedWidth() {
        var stored = parseInt(localStorage.getItem(STORAGE_KEY), 10);
        return isNaN(stored) ? DEFAULT_WIDTH : Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, stored));
    }

    function renderedWidth(split) {
        var sidebar = split && split.querySelector('.page-sidebar');
        return sidebar ? sidebar.getBoundingClientRect().width : storedWidth();
    }

    function isCollapsed() {
        return root.getAttribute('data-page-sidebar') === 'collapsed';
    }

    function setCollapsed(collapsed, moveFocus) {
        if (collapsed) {
            root.setAttribute('data-page-sidebar', 'collapsed');
        } else {
            root.removeAttribute('data-page-sidebar');
        }
        try { localStorage.setItem(COLLAPSED_KEY, collapsed ? '1' : '0'); } catch (e) { /* ignore */ }
        if (moveFocus) {
            // The control that was used is hidden now; hand focus to its counterpart so it is not lost.
            var next = document.querySelector('[data-page-split-toggle="' + (collapsed ? 'expand' : 'collapse') + '"]');
            if (next) { next.focus(); }
        }
    }

    (function restoreOnLoad() {
        try { applyWidth(storedWidth()); } catch (e) { /* ignore */ }
    })();

    document.addEventListener('pointerdown', function (startEvent) {
        var handle = startEvent.target.closest && startEvent.target.closest('.page-split-handle');
        if (!handle || startEvent.button !== 0) {
            return;
        }
        var split = handle.closest('.page-split');
        var sidebar = split && split.querySelector('.page-sidebar');
        if (!sidebar) {
            return;
        }

        startEvent.preventDefault();
        var startX = startEvent.clientX;
        var startWidth = sidebar.getBoundingClientRect().width;
        handle.setPointerCapture(startEvent.pointerId);
        document.body.style.cursor = 'col-resize';
        document.body.style.userSelect = 'none';

        function onMove(moveEvent) {
            applyWidth(clamp(startWidth + (moveEvent.clientX - startX)));
        }

        function onUp(upEvent) {
            try { handle.releasePointerCapture(upEvent.pointerId); } catch (e) { /* ignore */ }
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            // A click that did not move (half of a double-click, say) keeps the stored preference.
            if (upEvent.clientX !== startX) {
                persistWidth(clamp(startWidth + (upEvent.clientX - startX)));
            }
        }

        document.addEventListener('pointermove', onMove);
        document.addEventListener('pointerup', onUp);
    });

    // Double-click on the handle: back to the default width.
    document.addEventListener('dblclick', function (e) {
        if (!(e.target.closest && e.target.closest('.page-split-handle'))) {
            return;
        }
        e.preventDefault();
        applyWidth(DEFAULT_WIDTH);
        persistWidth(DEFAULT_WIDTH);
    });

    // The chevron at the panel's foot and the expand button on the collapsed strip.
    document.addEventListener('click', function (e) {
        var toggle = e.target.closest && e.target.closest('[data-page-split-toggle]');
        if (!toggle) {
            return;
        }
        setCollapsed(toggle.getAttribute('data-page-split-toggle') === 'collapse', true);
    });

    document.addEventListener('keydown', function (e) {
        // Ctrl+B toggles the panel on any page that has one, except inside a Monaco editor, whose own
        // bindings keep the key, and in rich text, where it means bold.
        if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && (e.key === 'b' || e.key === 'B')) {
            var target = e.target;
            if (!document.querySelector('.page-split')
                || (target && target.closest && target.closest('.monaco-editor, [contenteditable="true"], [contenteditable=""]'))) {
                return;
            }
            e.preventDefault();
            var active = document.activeElement;
            var focusInPanel = !!(active && active.closest && active.closest('.page-sidebar, .page-split-strip'));
            setCollapsed(!isCollapsed(), focusInPanel);
            return;
        }

        // Keyboard resize (left/right arrows) when the handle is focused -- the handle is a
        // role="separator" tabindex="0" element, so this keeps it usable without a pointer.
        var handle = document.activeElement;
        if (!handle || !handle.classList || !handle.classList.contains('page-split-handle')) {
            return;
        }
        if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') {
            return;
        }
        e.preventDefault();
        var delta = e.key === 'ArrowRight' ? STEP : -STEP;
        var next = clamp(renderedWidth(handle.closest('.page-split')) + delta);
        applyWidth(next);
        persistWidth(next);
    });
})();
