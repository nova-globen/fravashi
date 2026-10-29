// The scratchpad (backlog 67): one Monaco editor in markdown mode, beside every page.
//
// The design choice everything else follows from: the text being edited IS markdown. Copying therefore gives
// markdown with no conversion, pasting markdown keeps it, and the only rendering is done in place by the
// editor's own markdown colouring (bold shows bold, italic shows italic, markers stay visible) plus one thing
// asked for by name: a checklist, where "- [ ]" is open and "- [x]" is done and struck through.
//
// Monaco is loaded ONCE for the page, through fravashiCode.loadMonaco: a second copy of its AMD loader on the
// same page breaks the first.
window.fravashiScratchpad = (function () {
    'use strict';

    let editor = null;
    let dotnet = null;
    let saveTimer = null;
    let decorations = null;
    let themeObserver = null;

    // "- [ ] ", "* [x] ", "+ [X] ", optionally indented, then the item's text.
    const CHECK = /^(\s*[-*+]\s+)\[( |x|X)\](\s|$)/;

    function currentTheme() {
        return document.documentElement.getAttribute('data-theme') === 'dark' ? 'vs-dark' : 'vs';
    }

    function scheduleSave() {
        clearTimeout(saveTimer);
        saveTimer = setTimeout(flush, 600);
    }

    // Saves now. Called on a quiet moment, on blur and on close, so closing the panel never loses the last
    // few keystrokes.
    function flush() {
        clearTimeout(saveTimer);
        saveTimer = null;
        if (editor && dotnet) {
            dotnet.invokeMethodAsync('Save', editor.getValue());
        }
    }

    function redecorate() {
        const model = editor.getModel();
        const next = [];
        for (let line = 1; line <= model.getLineCount(); line++) {
            const match = CHECK.exec(model.getLineContent(line));
            if (!match) {
                continue;
            }
            const boxStart = match[1].length + 1;
            next.push({
                range: new monaco.Range(line, boxStart, line, boxStart + 3),
                options: { inlineClassName: 'scratch-box' },
            });
            if (match[2] !== ' ') {
                next.push({
                    range: new monaco.Range(line, boxStart + 3, line, model.getLineMaxColumn(line)),
                    options: { inlineClassName: 'scratch-done' },
                });
            }
        }
        decorations.set(next);
    }

    // A click on the box itself ticks or unticks it. Anywhere else on the line behaves as normal typing
    // territory, so ticking an item never costs the ability to put the cursor inside it.
    function toggleAt(position) {
        const model = editor.getModel();
        const match = CHECK.exec(model.getLineContent(position.lineNumber));
        if (!match) {
            return false;
        }
        const boxStart = match[1].length + 1;
        if (position.column < boxStart || position.column > boxStart + 3) {
            return false;
        }
        const mark = match[2] === ' ' ? 'x' : ' ';
        editor.executeEdits('scratchpad-toggle', [{
            range: new monaco.Range(position.lineNumber, boxStart + 1, position.lineNumber, boxStart + 2),
            text: mark,
        }]);
        return true;
    }

    function wrapSelection(before, after, placeholder) {
        const selection = editor.getSelection();
        const model = editor.getModel();
        const text = model.getValueInRange(selection) || placeholder;
        editor.executeEdits('scratchpad-format', [{ range: selection, text: before + text + after }]);
        const start = selection.getStartPosition();
        editor.setSelection(new monaco.Selection(
            start.lineNumber, start.column + before.length,
            start.lineNumber, start.column + before.length + text.length));
    }

    // Prefixes every line the selection touches. If they ALL already carry this prefix it is removed instead,
    // so the same button turns a list on and off.
    function prefixLines(kind) {
        const selection = editor.getSelection();
        const model = editor.getModel();
        const lines = [];
        for (let line = selection.startLineNumber; line <= selection.endLineNumber; line++) {
            lines.push(line);
        }
        const patterns = {
            bullet: /^(\s*)[-*+]\s+(?!\[[ xX]\])/,
            numbered: /^(\s*)\d+\.\s+/,
            check: /^(\s*)[-*+]\s+\[[ xX]\]\s+/,
        };
        const all = lines.every(function (line) { return patterns[kind].test(model.getLineContent(line)); });
        const edits = lines.map(function (line, index) {
            const content = model.getLineContent(line);
            const indent = /^\s*/.exec(content)[0];
            const bare = content.replace(patterns.check, '$1').replace(patterns.numbered, '$1').replace(/^(\s*)[-*+]\s+/, '$1').trimStart();
            let replacement;
            if (all) {
                replacement = indent + bare;
            } else if (kind === 'bullet') {
                replacement = indent + '- ' + bare;
            } else if (kind === 'numbered') {
                replacement = indent + (index + 1) + '. ' + bare;
            } else {
                replacement = indent + '- [ ] ' + bare;
            }
            return { range: new monaco.Range(line, 1, line, content.length + 1), text: replacement };
        });
        editor.executeEdits('scratchpad-format', edits);
    }

    return {
        open: async function (element, text, dotnetRef) {
            await window.fravashiCode.loadMonaco();
            if (editor) {
                flush();
                editor.dispose();
            }
            dotnet = dotnetRef;
            editor = monaco.editor.create(element, {
                value: text || '',
                language: 'markdown',
                theme: currentTheme(),
                automaticLayout: true,
                wordWrap: 'on',
                minimap: { enabled: false },
                lineNumbers: 'off',
                glyphMargin: false,
                folding: false,
                lineDecorationsWidth: 6,
                renderLineHighlight: 'none',
                scrollBeyondLastLine: false,
                overviewRulerLanes: 0,
                fontSize: 13,
                // The owner's rule: copying gives MARKDOWN. Monaco would otherwise also put syntax-coloured
                // HTML on the clipboard, and pasting into a mail or a document would paste coloured code.
                copyWithSyntaxHighlighting: false,
                // Quiet by default: a scratchpad is not a code editor, and suggestions pop up over notes.
                quickSuggestions: false,
                wordBasedSuggestions: 'off',
                unicodeHighlight: { ambiguousCharacters: false },
            });
            decorations = editor.createDecorationsCollection();
            redecorate();
            editor.onDidChangeModelContent(function () {
                redecorate();
                scheduleSave();
            });
            editor.onDidBlurEditorText(flush);
            editor.onMouseDown(function (e) {
                if (e.target && e.target.position && toggleAt(e.target.position)) {
                    e.event.preventDefault();
                }
            });

            if (!themeObserver) {
                themeObserver = new MutationObserver(function () {
                    if (editor) {
                        monaco.editor.setTheme(currentTheme());
                    }
                });
                themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
            }
            editor.focus();
        },

        format: function (action) {
            if (!editor) {
                return;
            }
            if (action === 'bold') { wrapSelection('**', '**', 'bold'); }
            else if (action === 'italic') { wrapSelection('*', '*', 'italic'); }
            else if (action === 'link') { wrapSelection('[', '](https://)', 'link'); }
            else if (action === 'bullet' || action === 'numbered' || action === 'check') { prefixLines(action); }
            editor.focus();
        },

        // Hands the final text BACK rather than saving it through the callback. The panel disposes its .NET
        // reference as it closes, so a save fired from here could reach an object that no longer exists and
        // be dropped without a word; returning it lets the caller save it while it still can.
        close: function () {
            clearTimeout(saveTimer);
            saveTimer = null;
            let text = null;
            if (editor) {
                text = editor.getValue();
                editor.dispose();
                editor = null;
            }
            dotnet = null;
            return text;
        },
    };
})();
