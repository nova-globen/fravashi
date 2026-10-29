// The mockup editor's keys (backlog 196).
//
// The editor answers its keys on the server (DesignEditorPage.razor), but a Blazor handler cannot stop the
// browser's own action for SOME keys and not others: preventDefault there is fixed at render time. So this one
// delegated listener does only that, for exactly the keys the editor takes, and nothing else:
//
//  * anywhere in the editor: Ctrl (or Cmd) with Z, Y or S, so the page undoes the TREE and does not offer to
//    save itself as a file;
//  * in the outline: Alt with an arrow (Alt+Left is Back in a browser), Delete, the plain arrows, Home and End
//    (which would scroll), and Tab and Shift+Tab, but ONLY while a node is selected. With nothing selected Tab
//    moves focus as usual, which is what keeps the outline from trapping the keyboard (Escape lets go).
//
// It also keeps the selected row in view after a key, once the server has drawn it.
//
// No CDN and no package: the container runs offline (ADR 0012).
(function () {
    'use strict';

    function editable(target) {
        if (!target || !target.tagName) {
            return false;
        }

        const tag = target.tagName.toLowerCase();
        return (tag === 'textarea' || (tag === 'input' && !target.readOnly)) || target.isContentEditable;
    }

    function reveal(outline) {
        window.setTimeout(function () {
            const row = outline.querySelector('[aria-selected="true"]');
            if (row && row.scrollIntoView) {
                row.scrollIntoView({ block: 'nearest' });
            }
        }, 120);
    }

    document.addEventListener('keydown', function (e) {
        const target = e.target;
        if (!target || !target.closest || !target.closest('[data-mockup-editor]')) {
            return;
        }

        const key = (e.key || '').toLowerCase();
        if ((e.ctrlKey || e.metaKey) && (key === 'z' || key === 'y' || key === 's')) {
            if (!editable(target)) {
                e.preventDefault();
            }

            return;
        }

        const outline = target.closest('[data-mockup-outline]');
        if (!outline || e.ctrlKey || e.metaKey) {
            return;
        }

        const arrows = e.key === 'ArrowUp' || e.key === 'ArrowDown' || e.key === 'ArrowLeft' || e.key === 'ArrowRight';
        const selected = !!outline.getAttribute('aria-activedescendant');
        if (arrows || e.key === 'Delete' || e.key === 'Home' || e.key === 'End' || (e.key === 'Tab' && selected)) {
            e.preventDefault();
            reveal(outline);
        }
    }, true);
})();
