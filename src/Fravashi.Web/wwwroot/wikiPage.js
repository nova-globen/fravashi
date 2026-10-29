// Work item #365: reading a wiki document without losing it. A reference in the document (#343, @159, !12) and a
// file path are real links, and a plain click on one opens its preview in the page's right-hand column instead, so
// the document stays where it was scrolled to. Delegated here because the body is rendered markdown, where Blazor
// cannot put a handler. A modified click keeps the link's own behaviour, and so does every click once the page is
// gone. The same handler serves the right-hand column itself, so a reference inside a preview previews too.
window.fravashiWiki = (function () {
    'use strict';

    const states = new WeakMap();

    function call(ref, method, arg) {
        try {
            const pending = ref.invokeMethodAsync(method, arg);
            if (pending && pending.catch) {
                pending.catch(function () { /* disposed component or dead circuit */ });
            }
        } catch (e) {
            // A click handler must never throw.
        }
    }

    return {
        attach: function (el, dotnetRef) {
            if (!el) {
                return;
            }

            const existing = states.get(el);
            if (existing) {
                existing.ref = dotnetRef;
                return;
            }

            const state = { ref: dotnetRef };
            state.click = function (event) {
                if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || !state.ref) {
                    return;
                }

                const target = event.target && event.target.closest ? event.target : null;
                if (!target) {
                    return;
                }

                const file = target.closest('a.wiki-file-ref');
                if (file && el.contains(file)) {
                    event.preventDefault();
                    event.stopPropagation();
                    call(state.ref, 'OpenFile', file.getAttribute('data-path') || '');
                    return;
                }

                const link = target.closest('a.wiki-ref');
                if (link && el.contains(link)) {
                    event.preventDefault();
                    event.stopPropagation();
                    call(state.ref, 'OpenReference', (link.getAttribute('data-ref') || link.textContent || '').trim());
                }
            };
            el.addEventListener('click', state.click);
            states.set(el, state);
        },

        detach: function (el) {
            const state = el ? states.get(el) : null;
            if (!state) {
                return;
            }

            el.removeEventListener('click', state.click);
            state.ref = null;
            states.delete(el);
        },

        // "On this page": the heading with this id, scrolled to the top of the reading column.
        scrollTo: function (el, id) {
            if (!el || !id) {
                return;
            }

            const escaped = window.CSS && CSS.escape ? CSS.escape(id) : id.replace(/"/g, '\\"');
            let heading = null;
            try {
                heading = el.querySelector('.wiki-article-body [id="' + escaped + '"]');
            } catch (e) {
                return;
            }
            if (heading) {
                heading.scrollIntoView({ block: 'start', behavior: 'smooth' });
            }
        },

        // A different document starts at its top, not where the last one was left.
        top: function (el) {
            const main = el ? el.querySelector('.wiki-reader-main') : null;
            if (main) {
                main.scrollTop = 0;
            }
        }
    };
})();
