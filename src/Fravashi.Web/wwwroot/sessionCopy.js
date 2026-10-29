// Clipboard for the conversation (backlog 110).
//
// Why this file exists at all: the studio window removes the browser's own context menu
// (MainWindow.cs, ADR 0014), so right-clicking a selection in a transcript did nothing and there was
// no copy affordance anywhere. The studio therefore has to do the clipboard write itself.
//
// Two things matter about HOW it writes:
//
//  * navigator.clipboard.writeText is the modern path, and it can reject for reasons that are not
//    bugs: no secure context, no permission, the document not focused at the moment of the call.
//    So there is a fallback (a hidden textarea plus document.execCommand('copy')), which is what
//    WebView2 accepts in every case we can reach.
//  * The caller is told what happened. A copy button that says "Copied" when nothing was copied is
//    worse than no button, so this returns { ok, reason } and the component renders the reason.
//
// No CDN and no package: the container runs offline (ADR 0012).
window.fravashiSessionCopy = (function () {
    'use strict';

    function fallback(text) {
        // Positioned off screen rather than display:none, because a hidden element cannot be selected
        // and execCommand would copy nothing while reporting success.
        const area = document.createElement('textarea');
        area.value = text;
        area.setAttribute('readonly', '');
        area.style.position = 'fixed';
        area.style.top = '-1000px';
        area.style.left = '-1000px';
        area.style.opacity = '0';
        document.body.appendChild(area);

        // The caret is restored afterwards so copying from a message does not eat the composer's
        // selection out from under the user.
        const previous = document.activeElement;
        try {
            area.focus();
            area.select();
            const copied = document.execCommand && document.execCommand('copy');
            return copied
                ? { ok: true, reason: null }
                : { ok: false, reason: 'the browser refused the copy' };
        } catch (e) {
            return { ok: false, reason: (e && e.message) ? e.message : 'the browser refused the copy' };
        } finally {
            document.body.removeChild(area);
            if (previous && previous.focus) {
                try { previous.focus(); } catch (e) { /* the element went away mid-copy */ }
            }
        }
    }

    return {
        // Returns { ok: bool, reason: string|null }. It never throws: a rejected promise here would
        // reach C# as a JSException and the component would have nothing to say beyond "it failed".
        copy: async function (text) {
            const value = (text === null || text === undefined) ? '' : String(text);
            if (value.length === 0) {
                return { ok: false, reason: 'there was nothing to copy' };
            }

            if (navigator.clipboard && navigator.clipboard.writeText) {
                try {
                    await navigator.clipboard.writeText(value);
                    return { ok: true, reason: null };
                } catch (e) {
                    // Fall through: not a secure context, not permitted, or the document was not
                    // focused. The textarea path answers all three.
                }
            }

            return fallback(value);
        },

        // The plain text of whatever the reader has selected, which is what a "Copy" menu item copies.
        // Plain on purpose: toString() on a Selection gives the text the eye sees, not the markup.
        selection: function () {
            try {
                const selected = window.getSelection();
                return selected ? selected.toString() : '';
            } catch (e) {
                return '';
            }
        }
    };
})();

// The copy control on a NESTED code block (backlog 110).
//
// A code block at the top level of a message is split out before display and gets a real Blazor button.
// One inside a list item or a blockquote cannot be: cutting the markdown there would break the list
// apart (MarkdownPreviewRenderer.ToSegments says why), so its containing block is rendered whole and the
// control is written into that HTML with the block's SOURCE on it. HTML that arrives as a MarkupString
// carries no Blazor handlers, so the click is answered here.
//
// One delegated listener on the document, registered once: the transcript re-renders on every streamed
// token, and a listener per button would be re-attached hundreds of times a turn.
(function () {
    'use strict';

    const SOURCE = 'data-copy-source';
    const CONFIRMATION = 1600;

    function say(button, word) {
        button.textContent = word;
        // Token-guarded the same way the Blazor controls are: a second click while the first word is
        // still up must not have the first timer clear the second one's answer.
        const token = (parseInt(button.getAttribute('data-copy-token') || '0', 10) || 0) + 1;
        button.setAttribute('data-copy-token', String(token));
        window.setTimeout(function () {
            if (button.getAttribute('data-copy-token') === String(token)) {
                button.textContent = 'Copy';
            }
        }, CONFIRMATION);
    }

    document.addEventListener('click', function (e) {
        const target = e.target;
        const button = (target && target.closest) ? target.closest('[' + SOURCE + ']') : null;
        if (!button) {
            return;
        }

        e.preventDefault();
        // A copy that did not happen never says "Copied": the reader would walk away believing they
        // have the text.
        window.fravashiSessionCopy.copy(button.getAttribute(SOURCE)).then(function (outcome) {
            say(button, (outcome && outcome.ok) ? 'Copied' : ((outcome && outcome.reason) || 'Copy failed'));
        });
    });
})();
