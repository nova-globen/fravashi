// The prototype viewer's Edit mode (backlog 214, plan @39): the ONE place a message from a prototype's page is
// heard. The page runs in a sandboxed frame with an opaque origin; in an edit render its constant script posts
// {mk: "select", anchor: "<string>"} to its parent when a person clicks an element. That is a trust boundary, so:
//
//  * a listener exists only while a viewer is in Edit mode: attach() when it is switched on, detach() when it is
//    switched off or the viewer goes away, one handle per viewer;
//  * accept() forwards a message only when its source IS that frame's window (read at the moment of the message),
//    and only in the exact shape above: a plain object with exactly the own keys mk and anchor, mk "select", and an
//    anchor string of 1 to 1024 characters;
//  * backlog 238 (plan @52): or a click on a node's PART, {mk: "select", anchor, part} with part one of "title",
//    "caption" and "tab", or {mk: "select", anchor, part: "columnHeader", column} with a column string of 1 to 1024
//    characters. No other key, and no column on any other part;
//  * only those strings cross to the studio, which ignores any anchor (or part) the current render did not emit. They
//    are never evaluated, inserted as markup, used as a selector here, or navigated to. Nothing is ever posted INTO the
//    frame and its DOM is never read.
//
// No CDN and no package: the container runs offline (ADR 0012).
(function () {
    'use strict';

    const MaxAnchor = 1024;
    const Parts = ['title', 'caption', 'tab', 'columnHeader'];
    const own = Object.prototype.hasOwnProperty;
    const handles = new Map();
    let next = 1;

    function text(value) {
        return typeof value === 'string' && value.length > 0 && value.length <= MaxAnchor;
    }

    // The selection as {anchor, part, column}, part and column null for the node itself; null for anything else.
    function accept(event, frame) {
        if (!event || !frame) {
            return null;
        }

        const source = frame.contentWindow;
        if (!source || event.source !== source) {
            return null;
        }

        const data = event.data;
        if (data === null || typeof data !== 'object' || Object.getPrototypeOf(data) !== Object.prototype) {
            return null;
        }

        const names = Object.getOwnPropertyNames(data);
        if (names.length < 2 || names.length > 4 || Object.getOwnPropertySymbols(data).length !== 0
            || !own.call(data, 'mk') || !own.call(data, 'anchor')) {
            return null;
        }

        if (data.mk !== 'select' || !text(data.anchor)) {
            return null;
        }

        if (names.length === 2) {
            return { anchor: data.anchor, part: null, column: null };
        }

        if (!own.call(data, 'part') || typeof data.part !== 'string' || Parts.indexOf(data.part) < 0) {
            return null;
        }

        const isHeader = data.part === 'columnHeader';
        if (names.length !== (isHeader ? 4 : 3) || (isHeader && (!own.call(data, 'column') || !text(data.column)))) {
            return null;
        }

        return { anchor: data.anchor, part: data.part, column: isHeader ? data.column : null };
    }

    function attach(frame, dotnet) {
        const handler = function (event) {
            const picked = accept(event, frame);
            if (picked !== null) {
                dotnet.invokeMethodAsync('SelectFromPage', picked.anchor, picked.part, picked.column).catch(function () { });
            }
        };

        window.addEventListener('message', handler);
        const handle = next++;
        handles.set(handle, handler);
        return handle;
    }

    function detach(handle) {
        const handler = handles.get(handle);
        if (handler) {
            window.removeEventListener('message', handler);
            handles.delete(handle);
        }
    }

    window.fravashiPrototypeEdit = { accept: accept, attach: attach, detach: detach };
})();
