// Pasting an image (backlog 26): the chat composer attaches it, the markdown editor saves it beside the file.
//
// Built on the DOM paste event and its clipboardData items, NOT navigator.clipboard.read: the studio window
// grants no clipboard permission, and the async API would additionally need a permission handler in the
// host. A paste event carries the data with no permission at all, because the user performed it.
//
// The bytes never ride invokeMethodAsync (SignalR's 32 KB client-to-server cap). The handler keeps the
// Blob under a short id and tells .NET the id; .NET then pulls it with take(id), which Blazor turns into a
// streamed IJSStreamReference. An id nobody takes within a minute is dropped, so a circuit that went away
// mid-paste does not keep the image alive in the page.
//
// No CDN and no package: the container runs offline (ADR 0012).
window.fravashiImagePaste = (function () {
    'use strict';

    const held = new Map();
    let counter = 0;
    const bound = new WeakMap();

    function hold(blob) {
        const id = 'img' + (++counter) + '-' + Date.now();
        held.set(id, blob);
        setTimeout(function () { held.delete(id); }, 60000);
        return id;
    }

    // The image files on a paste, oldest first. A screenshot tool puts exactly one; copying from a page
    // can put several representations, and each is a file only when it really is one.
    function imagesOf(event) {
        const data = event.clipboardData;
        if (!data || !data.items) {
            return [];
        }
        const images = [];
        for (let i = 0; i < data.items.length; i++) {
            const item = data.items[i];
            if (item.kind === 'file' && item.type && item.type.indexOf('image/') === 0) {
                const file = item.getAsFile();
                if (file) {
                    images.push(file);
                }
            }
        }
        return images;
    }

    function hasText(event) {
        const data = event.clipboardData;
        return !!(data && data.types && Array.prototype.indexOf.call(data.types, 'text/plain') >= 0
            && data.getData('text/plain').length > 0);
    }

    return {
        imagesOf: imagesOf,
        hold: hold,

        // The composer: every pasted image is handed to .NET; any text on the same paste still pastes
        // normally, so copying a slide with its caption loses neither.
        attachComposer: function (element, dotnetRef) {
            if (!element || bound.has(element)) {
                return;
            }
            const handler = function (event) {
                const images = imagesOf(event);
                if (images.length === 0) {
                    return;
                }
                if (!hasText(event)) {
                    event.preventDefault();
                }
                images.forEach(function (blob) {
                    dotnetRef.invokeMethodAsync('ImagePasted', hold(blob), blob.size);
                });
            };
            element.addEventListener('paste', handler);
            bound.set(element, handler);
        },

        // Work item #359 (folding in #355): the chat composer with references. An image is handed to .NET, which
        // attaches it and answers its reference ("[Image 3]"), typed where the cursor is. A long text (at least
        // maxChars characters or maxLines lines) is held like an image and handed over the same streamed way, so a
        // big paste never rides invokeMethodAsync's 32 KB cap; .NET keeps it as a chip and answers its reference,
        // which is typed instead of the text. A short paste is left to the browser and typed normally. The typing
        // goes through setRangeText and an input event, so Blazor's own binding sees it as if the person typed it.
        attachComposerReferences: function (element, dotnetRef, maxChars, maxLines) {
            if (!element || bound.has(element)) {
                return;
            }
            const type = function (label) {
                if (!label) {
                    return;
                }
                const start = element.selectionStart || 0;
                const end = element.selectionEnd || start;
                element.setRangeText(label, start, end, 'end');
                element.dispatchEvent(new Event('input', { bubbles: true }));
            };
            const lines = function (text) {
                let count = 1;
                for (let i = 0; i < text.length; i++) {
                    if (text.charCodeAt(i) === 10) {
                        count++;
                    }
                }
                return count;
            };
            const handler = function (event) {
                const images = imagesOf(event);
                if (images.length > 0) {
                    if (!hasText(event)) {
                        event.preventDefault();
                    }
                    (async function () {
                        for (const blob of images) {
                            try {
                                type(await dotnetRef.invokeMethodAsync('ImagePastedAt', hold(blob), blob.size));
                            } catch (e) {
                                // The circuit went away mid-paste; nothing was attached.
                            }
                        }
                    })();
                    return;
                }
                const data = event.clipboardData;
                const text = data ? data.getData('text/plain') : '';
                if (!text || (text.length < maxChars && lines(text) < maxLines)) {
                    return;
                }
                event.preventDefault();
                (async function () {
                    try {
                        type(await dotnetRef.invokeMethodAsync(
                            'TextPasted', hold(new Blob([text], { type: 'text/plain' })), text.length));
                    } catch (e) {
                        // The circuit went away mid-paste; nothing was pasted.
                    }
                })();
            };
            element.addEventListener('paste', handler);
            bound.set(element, handler);
        },

        detach: function (element) {
            const handler = element ? bound.get(element) : null;
            if (handler) {
                element.removeEventListener('paste', handler, true);
                element.removeEventListener('paste', handler);
                bound.delete(element);
            }
        },

        // Returns the held Blob once; Blazor streams it to .NET as an IJSStreamReference.
        take: function (id) {
            const blob = held.get(id);
            held.delete(id);
            return blob || null;
        }
    };
})();
