// Monaco host for the Code page. Loads the vendored AMD build (wwwroot/vendor/monaco/vs) on demand, so
// only the Code page pays the download. One editor instance; one Monaco model per open file (keyed by
// file URI, so language detection comes from the extension and tab switches keep undo/scroll state via
// saved view states). Phase 1 is read-only; Phase 2 adds edit/save wiring. The open-tabs list also
// persists to localStorage here (fravashi-code-workspace) so a refresh restores the session, VS Code
// style.
window.fravashiCode = (function () {
    'use strict';

    // Plan 2b-ii: one studio origin now serves several workspace windows, so session and backup
    // keys are qualified by a workspace scope ('local' = the container clone; otherwise the bridge
    // workspace id). setWorkspaceScope migrates the pre-scope legacy session key once.
    const WORKSPACE_KEY_BASE = 'fravashi-code-workspace';
    const BACKUP_PREFIX_BASE = 'fravashi-code-backup:';
    let workspaceScope = 'local';

    function workspaceKey() {
        return WORKSPACE_KEY_BASE + ':' + workspaceScope;
    }

    function backupPrefix() {
        return BACKUP_PREFIX_BASE + workspaceScope + ':';
    }

    // A legacy (pre-scope) backup key is BASE + path; scoped keys are BASE + scope + ':' + path.
    // Repo-relative paths never contain ':', so the first segment tells them apart.
    function isLegacyBackupKey(key) {
        const rest = key.slice(BACKUP_PREFIX_BASE.length);
        return rest.indexOf(':') < 0 || !/^([0-9a-f]{16}|local):/.test(rest);
    }

    // Extensions Monaco's bundled languages do not claim (audited against
    // monaco.languages.getLanguages() on 2026-07-26; everything else in the common web/.NET stack,
    // including .tsx/.jsx/.csproj/.props/.targets/.config/.vb/.fs/.sql/.scss/.less, is already
    // registered by the bundle). vue/svelte have no bundled grammar; html is an approximation that
    // beats plaintext for their single-file components. .sln stays plaintext (not an XML format).
    const LANGUAGE_OVERRIDES = {
        razor: 'razor',
        cshtml: 'razor',
        resx: 'xml',
        nuspec: 'xml',
        slnx: 'xml',
        vue: 'html',
        svelte: 'html'
    };

    let editor = null;
    let diffEditor = null;
    let loading = null;
    let themeObserver = null;
    const viewStates = new Map();
    // Dirty tracking (phase 2): per-model saved version ids; the set feeds the hot-exit flush.
    const savedVersions = new Map();
    const dirtyPaths = new Set();
    let beforeUnloadBound = false;
    // External reloads push an edit whose change event must not count as the user dirtying the file.
    let suppressDirtyEvents = false;

    // ---- Hot exit: unsaved buffers survive a close, refresh, or crash (VS Code behavior). ----
    // Each dirty buffer is backed up to localStorage (debounced on typing, flushed on unload); the
    // backup also remembers the disk timestamp the buffer was based on, so a restore can detect that
    // the file moved on while the app was closed and raise the phase-2 conflict banner instead of
    // silently overwriting either side. Backups clear on save, close, or reload-from-disk.
    const BACKUP_PREFIX = 'fravashi-code-backup:';
    const backupTimers = new Map();
    const baseStamps = new Map();

    function pathOf(model) {
        return model.uri.path.replace(/^\//, '');
    }

    function writeBackup(model) {
        try {
            localStorage.setItem(backupPrefix() + pathOf(model), JSON.stringify({
                content: model.getValue(),
                baseStamp: baseStamps.get(keyOf(model)) || null
            }));
        } catch (e) {
            // Quota exceeded: hot exit degrades for this file, editing must never break.
        }
    }

    function scheduleBackup(model) {
        const key = keyOf(model);
        clearTimeout(backupTimers.get(key));
        backupTimers.set(key, setTimeout(function () {
            if (dirtyPaths.has(key)) {
                writeBackup(model);
            }
        }, 400));
    }

    function readBackup(path) {
        try {
            const raw = localStorage.getItem(backupPrefix() + path)
                || (workspaceScope === 'local' ? localStorage.getItem(BACKUP_PREFIX_BASE + path) : null);
            return raw ? JSON.parse(raw) : null;
        } catch (e) {
            return null;
        }
    }

    function clearBackup(path) {
        try {
            localStorage.removeItem(backupPrefix() + path);
            if (workspaceScope === 'local') {
                localStorage.removeItem(BACKUP_PREFIX_BASE + path); // pre-scope legacy key
            }
        } catch (e) {
            // Best effort.
        }
    }

    function flushBackups() {
        if (!window.monaco) {
            return;
        }
        monaco.editor.getModels().forEach(function (model) {
            if (dirtyPaths.has(keyOf(model))) {
                writeBackup(model);
            }
        });
    }

    function keyOf(model) {
        return model.uri.toString();
    }

    function reportDirty(model, dotnetRef) {
        if (suppressDirtyEvents) {
            return;
        }
        const key = keyOf(model);
        const isDirty = model.getAlternativeVersionId() !== savedVersions.get(key);
        const was = dirtyPaths.has(key);
        if (isDirty === was) {
            return;
        }
        if (isDirty) {
            dirtyPaths.add(key);
        } else {
            dirtyPaths.delete(key);
        }
        if (dotnetRef) {
            dotnetRef.invokeMethodAsync('SetDirty', model.uri.path.replace(/^\//, ''), isDirty);
        }
    }

    // Recording the current version as saved can itself be a dirty-to-clean transition (undo back to
    // the saved state, external reload); route it through reportDirty so the server always hears it.
    function markModelSaved(model, dotnetRef) {
        savedVersions.set(keyOf(model), model.getAlternativeVersionId());
        reportDirty(model, dotnetRef);
    }

    function bindBeforeUnload() {
        if (beforeUnloadBound) {
            return;
        }
        beforeUnloadBound = true;
        // Hot exit replaces the old blocking prompt: closing is safe because every dirty buffer is
        // flushed to its backup here (synchronously; localStorage allows that) and restored on the
        // next launch.
        window.addEventListener('beforeunload', function () {
            flushBackups();
        });
    }

    function loadMonaco() {
        if (loading) {
            return loading;
        }
        loading = new Promise(function (resolve, reject) {
            const script = document.createElement('script');
            script.src = 'vendor/monaco/vs/loader.js';
            script.onload = function () {
                window.require.config({ paths: { vs: 'vendor/monaco/vs' } });
                window.require(['vs/editor/editor.main'], function () { resolve(); }, reject);
            };
            script.onerror = reject;
            document.head.appendChild(script);
        });
        return loading;
    }

    function currentTheme() {
        return document.documentElement.getAttribute('data-theme') === 'dark' ? 'vs-dark' : 'vs';
    }

    function watchTheme() {
        if (themeObserver) {
            return;
        }
        themeObserver = new MutationObserver(function () {
            if (editor || diffEditor) {
                monaco.editor.setTheme(currentTheme());
            }
        });
        themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    }

    function overrideLanguage(path) {
        const dot = path.lastIndexOf('.');
        if (dot < 0) {
            return undefined;
        }
        return LANGUAGE_OVERRIDES[path.slice(dot + 1).toLowerCase()];
    }

    // Content changes drive dirty reporting and the hot-exit backup; open() and rename() both wire
    // their models through here.
    function wireModel(model, self) {
        model.onDidChangeContent(function () {
            reportDirty(model, self._hotkeysRef);
            if (dirtyPaths.has(keyOf(model))) {
                scheduleBackup(model);
            } else {
                clearBackup(pathOf(model));
            }
        });
    }

    return {
        // Shared with the scratchpad (backlog 67). Monaco's AMD loader must be on the page exactly once, so
        // anything else that needs an editor loads it through here rather than adding its own script tag.
        loadMonaco: loadMonaco,

        init: async function (element) {
            await loadMonaco();
            if (editor) {
                editor.dispose();
            }
            // The document zoom level persists across restarts (stored as the effective font size).
            let storedFont = NaN;
            try {
                storedFont = parseFloat(localStorage.getItem('fravashi-editor-fontsize'));
            } catch (e) {
                // Persistence is best effort.
            }
            editor = monaco.editor.create(element, {
                automaticLayout: true,
                theme: currentTheme(),
                fontSize: storedFont > 6 && storedFont < 100 ? storedFont : 13,
                minimap: { enabled: true },
                scrollBeyondLastLine: false,
                renderWhitespace: 'selection',
                // Document-level zoom: Ctrl+wheel and touchpad pinch (browsers report pinch as a
                // ctrl-modified wheel) scale the editor font, not the whole window.
                mouseWheelZoom: true
            });
            editor.onDidChangeConfiguration(function () {
                try {
                    const size = editor.getOption(monaco.editor.EditorOption.fontInfo).fontSize;
                    localStorage.setItem('fravashi-editor-fontsize', String(size));
                } catch (e) {
                    // Persistence is best effort.
                }
            });
            // Line blame (VS Code style): a settled cursor asks the server who last changed the
            // line; the answer renders as faint injected text at the end of the line.
            const self = this;
            editor.onDidChangeCursorPosition(function (e) {
                self.clearLineBlame();
                clearTimeout(self._blameTimer);
                self._blameTimer = setTimeout(function () {
                    const model = editor ? editor.getModel() : null;
                    if (model && self._hotkeysRef) {
                        self._hotkeysRef.invokeMethodAsync('CursorLineChanged', pathOf(model), e.position.lineNumber);
                    }
                }, 300);
            });
            // Backlog 26: an image pasted into a MARKDOWN buffer is saved beside the file and a relative
            // link replaces the selection. Caught on the editor's host in the CAPTURE phase, which runs
            // before Monaco's own paste handler on its hidden textarea, so the image never becomes text.
            // Anywhere else (a .cs file, a paste of plain text) the event is left entirely alone.
            element.addEventListener('paste', function (event) {
                const model = editor ? editor.getModel() : null;
                if (!model || !self._hotkeysRef || !window.fravashiImagePaste || !/\.(md|markdown|mdx)$/i.test(pathOf(model))) {
                    return;
                }
                const images = window.fravashiImagePaste.imagesOf(event);
                if (images.length === 0) {
                    return;
                }
                event.preventDefault();
                event.stopPropagation();
                const blob = images[0];
                const id = window.fravashiImagePaste.hold(blob);
                self._pasteTargets = self._pasteTargets || new Map();
                self._pasteTargets.set(id, { uri: model.uri.toString(), range: editor.getSelection() });
                self._hotkeysRef.invokeMethodAsync('EditorImagePasted', pathOf(model), id, blob.size);
            }, true);
            // Work item #366: the person typing, scrolling or clicking in the editor pauses "follow the agent".
            // Throttled to one report a second; the server owns the pause window itself.
            let lastActivity = 0;
            const personActed = function () {
                const now = Date.now();
                if (now - lastActivity < 1000 || !self._hotkeysRef) {
                    return;
                }
                lastActivity = now;
                self._hotkeysRef.invokeMethodAsync('EditorPersonActivity');
            };
            element.addEventListener('keydown', personActed, true);
            element.addEventListener('wheel', personActed, { capture: true, passive: true });
            element.addEventListener('mousedown', personActed, true);
            watchTheme();
            bindBeforeUnload();
        },

        // Work item #366: the lines a followed change touched, highlighted for a few seconds and revealed when
        // they are off screen. Only on the buffer that is on screen; focus is never taken.
        highlightLines: function (path, first, last, ms) {
            if (!window.monaco || !editor) {
                return;
            }
            const model = editor.getModel();
            if (!model || pathOf(model) !== path) {
                return;
            }
            const count = model.getLineCount();
            const from = Math.max(1, Math.min(first, count));
            const to = Math.max(from, Math.min(last, count));
            const range = new monaco.Range(from, 1, to, 1);
            const ids = model.deltaDecorations([], [{
                range: range,
                options: { isWholeLine: true, className: 'fravashi-follow-changed', linesDecorationsClassName: 'fravashi-follow-changed-gutter' }
            }]);
            editor.revealRangeInCenterIfOutsideViewport(range);
            setTimeout(function () {
                if (!model.isDisposed()) {
                    model.deltaDecorations(ids, []);
                }
            }, ms || 4000);
        },

        // The server saved a pasted image; put its link where the paste happened, as one undoable edit.
        // The model is looked up again rather than assumed active: the save is a round trip, and the
        // reader may have switched tabs meanwhile.
        insertPastedImage: function (id, text) {
            const target = this._pasteTargets ? this._pasteTargets.get(id) : null;
            if (this._pasteTargets) {
                this._pasteTargets.delete(id);
            }
            if (!target || !window.monaco) {
                return false;
            }
            const model = monaco.editor.getModel(monaco.Uri.parse(target.uri));
            if (!model) {
                return false;
            }
            const edit = { range: target.range, text: text, forceMoveMarkers: true };
            if (editor && editor.getModel() === model) {
                editor.pushUndoStop();
                editor.executeEdits('fravashi-image-paste', [edit]);
                editor.pushUndoStop();
            } else {
                model.pushEditOperations([], [edit], function () { return null; });
            }
            return true;
        },

        forgetPastedImage: function (id) {
            if (this._pasteTargets) {
                this._pasteTargets.delete(id);
            }
        },

        // Render the blame answer for (path, line) as a content widget hugging the end of the line
        // (the vendored Monaco build does not render injected-text decorations, so a widget it is).
        // Ignored when the cursor has moved on to a different buffer meanwhile.
        showLineBlame: function (path, line, text) {
            if (!editor || !window.monaco) {
                return;
            }
            const model = editor.getModel();
            if (!model || pathOf(model) !== path || line > model.getLineCount()) {
                return;
            }
            this.clearLineBlame();
            const node = document.createElement('span');
            node.className = 'fravashi-blame-inline';
            node.textContent = '  ' + text;
            const widget = {
                getId: function () { return 'fravashi.lineBlame'; },
                getDomNode: function () { return node; },
                getPosition: function () {
                    const current = editor ? editor.getModel() : null;
                    if (!current || pathOf(current) !== path || line > current.getLineCount()) {
                        return null;
                    }
                    return {
                        position: { lineNumber: line, column: current.getLineMaxColumn(line) },
                        preference: [monaco.editor.ContentWidgetPositionPreference.EXACT]
                    };
                }
            };
            editor.addContentWidget(widget);
            this._blameWidget = widget;
        },

        clearLineBlame: function () {
            if (editor && this._blameWidget) {
                editor.removeContentWidget(this._blameWidget);
                this._blameWidget = null;
            }
        },

        // The title-bar search box and F1 both land here: Monaco's command palette, anchored under
        // the title bar by the .quick-input-widget CSS override.
        openCommandPalette: function () {
            if (editor) {
                editor.focus();
                editor.trigger('fravashi', 'editor.action.quickCommand', null);
            }
        },

        fontZoom: function (direction) {
            if (!editor) {
                return;
            }
            const action = direction === 0 ? 'editor.action.fontZoomReset'
                : direction > 0 ? 'editor.action.fontZoomIn' : 'editor.action.fontZoomOut';
            editor.trigger('fravashi', action, null);
        },

        // Title-bar undo/redo: they act on the active editor buffer (same as Ctrl+Z / Ctrl+Y).
        editorUndo: function () {
            if (editor) {
                editor.focus();
                editor.trigger('fravashi', 'undo', null);
            }
        },

        editorRedo: function () {
            if (editor) {
                editor.focus();
                editor.trigger('fravashi', 'redo', null);
            }
        },

        // Show a file. content/diskStamp are only used when the model does not exist yet (after a page
        // return or a refresh, models are gone and the server re-reads the file); an existing model
        // keeps its state. Returns hot-exit restore info: whether a backup was applied and the disk
        // timestamp that backup was based on (the server seeds its conflict tracking from it).
        // noFocus (work item #366): a file opened by following the agent never takes focus.
        open: async function (path, content, diskStamp, noFocus) {
            await loadMonaco();
            if (!editor) {
                return { backupApplied: false, baseStamp: null };
            }
            const previous = editor.getModel();
            if (previous) {
                viewStates.set(previous.uri.toString(), editor.saveViewState());
            }
            const uri = monaco.Uri.file(path);
            let model = monaco.editor.getModel(uri);
            let backupApplied = false;
            let backupBase = null;
            if (!model) {
                model = monaco.editor.createModel(content, overrideLanguage(path), uri);
                const self = this;
                markModelSaved(model, self._hotkeysRef);
                baseStamps.set(keyOf(model), diskStamp || null);
                wireModel(model, self);

                // Hot exit restore: a backup means the last session closed (or crashed) with unsaved
                // edits; apply them as ONE undoable edit on top of the disk content, so the buffer is
                // dirty again and undo walks back to the on-disk state.
                const backup = readBackup(path);
                if (backup && typeof backup.content === 'string') {
                    if (backup.content === content) {
                        clearBackup(path); // disk caught up (e.g. saved elsewhere); nothing to restore
                    } else {
                        model.pushEditOperations([], [{ range: model.getFullModelRange(), text: backup.content }], function () { return null; });
                        backupApplied = true;
                        backupBase = backup.baseStamp || null;
                        baseStamps.set(keyOf(model), backupBase);
                    }
                }
            }
            editor.setModel(model);
            const saved = viewStates.get(uri.toString());
            if (saved) {
                editor.restoreViewState(saved);
            }
            if (!noFocus) {
                editor.focus();
            }
            return { backupApplied: backupApplied, baseStamp: backupBase };
        },

        close: function (path) {
            if (!window.monaco) {
                return;
            }
            const uri = monaco.Uri.file(path);
            viewStates.delete(uri.toString());
            savedVersions.delete(uri.toString());
            dirtyPaths.delete(uri.toString());
            baseStamps.delete(uri.toString());
            clearTimeout(backupTimers.get(uri.toString()));
            backupTimers.delete(uri.toString());
            // Closing a tab is a deliberate discard; its hot-exit backup goes with it.
            clearBackup(path);
            const model = monaco.editor.getModel(uri);
            if (model) {
                model.dispose();
            }
        },

        // A rename (or a folder rename above the file) re-keys the open model: new URI means the
        // language re-derives from the new name and the hot-exit backup moves to the new key.
        // Content, dirty state, view state, and the base timestamp all survive; the undo stack is
        // the one casualty (Monaco models cannot change URI in place).
        rename: function (oldPath, newPath) {
            const backup = readBackup(oldPath);
            if (backup) {
                try {
                    localStorage.setItem(backupPrefix() + newPath, JSON.stringify(backup));
                } catch (e) {
                    // Quota exceeded: the backup stays behind under the old key at worst.
                }
            }
            if (!window.monaco) {
                clearBackup(oldPath);
                return;
            }
            const oldUri = monaco.Uri.file(oldPath);
            const model = monaco.editor.getModel(oldUri);
            if (!model) {
                clearBackup(oldPath);
                return;
            }
            const oldKey = oldUri.toString();
            const wasActive = editor && editor.getModel() === model;
            const state = wasActive ? editor.saveViewState() : viewStates.get(oldKey);
            const content = model.getValue();
            const wasDirty = dirtyPaths.has(oldKey);
            const base = baseStamps.get(oldKey);
            this.close(oldPath);
            const newUri = monaco.Uri.file(newPath);
            const newKey = newUri.toString();
            const newModel = monaco.editor.createModel(content, overrideLanguage(newPath), newUri);
            baseStamps.set(newKey, base || null);
            if (wasDirty) {
                // A saved-version id no model ever has keeps the buffer dirty until the next save.
                savedVersions.set(newKey, -1);
                dirtyPaths.add(newKey);
                writeBackup(newModel);
            } else {
                savedVersions.set(newKey, newModel.getAlternativeVersionId());
            }
            wireModel(newModel, this);
            if (state) {
                viewStates.set(newKey, state);
            }
            if (wasActive && editor) {
                editor.setModel(newModel);
                if (state) {
                    editor.restoreViewState(state);
                }
            }
        },

        // Small persisted UI preferences (search view mode and friends); values are plain strings.
        setPref: function (key, value) {
            try {
                localStorage.setItem('fravashi-pref:' + key, value);
            } catch (e) {
                // Persistence is best effort.
            }
        },

        getPref: function (key) {
            try {
                return localStorage.getItem('fravashi-pref:' + key);
            } catch (e) {
                return null;
            }
        },

        // ---- Save / external-change plumbing (phase 2, ADR 0016) ----

        // Returns the model's bytes for streaming interop (IJSStreamReference server-side); the text
        // must never ride invokeMethodAsync directly (SignalR's 32 KB client-to-server cap). A file
        // restored dirty by hot exit but not yet opened this session has no model; its backup is the
        // buffer, so Save All can persist it without the tab ever being activated.
        getContent: function (path) {
            if (window.monaco) {
                const model = monaco.editor.getModel(monaco.Uri.file(path));
                if (model) {
                    return new TextEncoder().encode(model.getValue());
                }
            }
            const backup = readBackup(path);
            return backup && typeof backup.content === 'string'
                ? new TextEncoder().encode(backup.content)
                : null;
        },

        // The server persisted the current content; from here on this version counts as clean and
        // its hot-exit backup is obsolete.
        markSaved: function (path, newStamp) {
            clearBackup(path);
            if (!window.monaco) {
                return;
            }
            const model = monaco.editor.getModel(monaco.Uri.file(path));
            if (model) {
                if (newStamp) {
                    baseStamps.set(keyOf(model), newStamp);
                }
                markModelSaved(model, this._hotkeysRef);
            }
        },

        // Hot-exit backups present from the last session: [{ path, baseStamp }]. The server marks
        // these tabs dirty on restore (their content applies lazily when each tab is first opened).
        // Selects which workspace's session/backups this circuit reads and writes (plan 2b-ii).
        // Must run before loadWorkspace/listBackups. Migrates the pre-scope session key once.
        setWorkspaceScope: function (scope) {
            workspaceScope = scope && scope.length ? scope : 'local';
            if (workspaceScope === 'local') {
                try {
                    const legacy = localStorage.getItem(WORKSPACE_KEY_BASE);
                    if (legacy !== null) {
                        localStorage.setItem(workspaceKey(), legacy);
                        localStorage.removeItem(WORKSPACE_KEY_BASE);
                    }
                } catch (e) {
                    // Migration is best effort; a fresh session is the worst case.
                }
            }
        },

        listBackups: function () {
            const entries = [];
            const prefix = backupPrefix();
            try {
                for (let i = 0; i < localStorage.length; i++) {
                    const key = localStorage.key(i);
                    if (!key) {
                        continue;
                    }
                    let path = null;
                    if (key.indexOf(prefix) === 0) {
                        path = key.slice(prefix.length);
                    } else if (workspaceScope === 'local'
                        && key.indexOf(BACKUP_PREFIX_BASE) === 0
                        && isLegacyBackupKey(key)) {
                        path = key.slice(BACKUP_PREFIX_BASE.length); // pre-scope legacy backup
                    }
                    if (path !== null) {
                        const backup = readBackup(path);
                        entries.push({
                            path: path,
                            baseStamp: backup ? backup.baseStamp || null : null
                        });
                    }
                }
            } catch (e) {
                // localStorage unavailable: no backups to restore.
            }
            return entries;
        },

        clearBackup: function (path) {
            clearBackup(path);
        },

        flushBackups: function () {
            flushBackups();
        },

        isDirty: function (path) {
            return window.monaco
                ? dirtyPaths.has(monaco.Uri.file(path).toString())
                : false;
        },

        // The file changed on disk and the buffer is clean: swap the content in as ONE undoable edit
        // (setValue would wipe the undo stack), keep the view state, and count the result as saved.
        applyExternalContent: function (path, content, newStamp) {
            if (!window.monaco) {
                return;
            }
            const model = monaco.editor.getModel(monaco.Uri.file(path));
            if (!model) {
                return;
            }
            clearBackup(path);
            if (newStamp) {
                baseStamps.set(keyOf(model), newStamp);
            }
            if (model.getValue() === content) {
                markModelSaved(model, this._hotkeysRef);
                return;
            }
            const isActive = editor && editor.getModel() === model;
            const state = isActive ? editor.saveViewState() : null;
            suppressDirtyEvents = true;
            try {
                model.pushEditOperations([], [{ range: model.getFullModelRange(), text: content }], function () { return null; });
            } finally {
                suppressDirtyEvents = false;
            }
            markModelSaved(model, this._hotkeysRef);
            if (isActive && state) {
                editor.restoreViewState(state);
            }
        },

        dispose: function () {
            // Leaving the page (navigation within the app, teardown): backups must SURVIVE this,
            // that is the whole point of hot exit; flush any pending ones first.
            flushBackups();
            if (editor) {
                editor.dispose();
                editor = null;
            }
            if (window.monaco) {
                monaco.editor.getModels().forEach(function (m) { m.dispose(); });
            }
            viewStates.clear();
            savedVersions.clear();
            dirtyPaths.clear();
            baseStamps.clear();
            backupTimers.forEach(function (t) { clearTimeout(t); });
            backupTimers.clear();
        },

        // ---- Open-tabs persistence (VS Code style session restore across refreshes) ----

        saveWorkspace: function (json) {
            try { localStorage.setItem(workspaceKey(), json); } catch (e) { /* ignore */ }
        },

        loadWorkspace: function () {
            try { return localStorage.getItem(workspaceKey()); } catch (e) { return null; }
        },

        // ---- Editor hotkeys, captured at document level while the Code page is open. ----
        //
        // HONEST LIMIT: in a regular Chrome/Edge browser tab, Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+F4,
        // Ctrl+W, Ctrl+T and Ctrl+N are RESERVED accelerators the browser handles before the page
        // sees any event; no web page (vscode.dev included) can intercept them. The Ctrl+Tab/Ctrl+F4
        // branches below still exist because app-mode/PWA windows and keyboard-lock contexts DO
        // deliver them. The Ctrl+Alt variants are the always-deliverable equivalents (they match
        // Visual Studio's own next/previous document bindings) and work in every browser.

        bindHotkeys: function (dotnetRef) {
            this.unbindHotkeys();
            this._hotkeysRef = dotnetRef;
            const self = this;
            self._switcherActive = false;
            const handler = function (e) {
                if (!document.querySelector('.code-page')) {
                    return;
                }
                // Alt+Left/Right: editor back/forward (VS Code's Go Back / Go Forward). Native in a
                // browser; in the window shell (accelerators disabled) we drive history ourselves.
                if (e.altKey && !e.ctrlKey && !e.metaKey && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) {
                    e.preventDefault();
                    e.stopPropagation();
                    if (e.key === 'ArrowLeft') {
                        history.back();
                    } else {
                        history.forward();
                    }
                    return;
                }
                if (self._switcherActive && e.key === 'Escape') {
                    e.preventDefault();
                    e.stopPropagation();
                    self._switcherActive = false;
                    dotnetRef.invokeMethodAsync('HotkeySwitcherCancel');
                    return;
                }
                // F1 opens the command palette from anywhere on the page; inside the editor Monaco
                // already handles it (this listener runs capture-first, so step aside there).
                if (e.key === 'F1' && !e.ctrlKey && !e.altKey && !e.metaKey) {
                    if (!(e.target && e.target.closest && e.target.closest('.monaco-editor'))) {
                        e.preventDefault();
                        e.stopPropagation();
                        self.openCommandPalette();
                    }
                    return;
                }
                if (!e.ctrlKey || e.metaKey) {
                    return;
                }
                if (e.altKey) {
                    // Ctrl+Alt combos: deliverable in every browser (Visual Studio's alternates).
                    // These cycle directly, no switcher popup, like VS's Next/Previous Document.
                    if (e.key === 'PageDown') {
                        e.preventDefault();
                        e.stopPropagation();
                        dotnetRef.invokeMethodAsync('HotkeyCycleTab', 1);
                    } else if (e.key === 'PageUp') {
                        e.preventDefault();
                        e.stopPropagation();
                        dotnetRef.invokeMethodAsync('HotkeyCycleTab', -1);
                    } else if (e.key === 'F4') {
                        e.preventDefault();
                        e.stopPropagation();
                        dotnetRef.invokeMethodAsync('HotkeyCloseActiveTab');
                    }
                    return;
                }
                if (e.key === 'Tab') {
                    // Ctrl+Tab opens/advances the MRU switcher; releasing Ctrl commits (keyup below).
                    e.preventDefault();
                    e.stopPropagation();
                    self._switcherActive = true;
                    dotnetRef.invokeMethodAsync('HotkeySwitcherStep', e.shiftKey ? -1 : 1);
                } else if (e.key === 'F4') {
                    e.preventDefault();
                    e.stopPropagation();
                    dotnetRef.invokeMethodAsync('HotkeyCloseActiveTab');
                } else if (e.shiftKey && (e.key === 'P' || e.key === 'p')) {
                    // Ctrl+Shift+P: the VS Code command palette binding.
                    e.preventDefault();
                    e.stopPropagation();
                    self.openCommandPalette();
                } else if (e.shiftKey && (e.key === 'F' || e.key === 'f')) {
                    // Ctrl+Shift+F: find in files (expands the Search section and focuses its input).
                    e.preventDefault();
                    e.stopPropagation();
                    dotnetRef.invokeMethodAsync('HotkeyOpenSearch');
                } else if (e.shiftKey && (e.key === 'S' || e.key === 's')) {
                    // Ctrl+Shift+S: save all open files (VS Code's binding, minus Save As).
                    e.preventDefault();
                    e.stopPropagation();
                    dotnetRef.invokeMethodAsync('HotkeySaveAll');
                } else if (!e.shiftKey && (e.key === '=' || e.key === '+' || e.code === 'NumpadAdd')) {
                    // Ctrl+= / Ctrl+- / Ctrl+0: zoom the document (editor font), never the window.
                    e.preventDefault();
                    e.stopPropagation();
                    self.fontZoom(1);
                } else if (!e.shiftKey && (e.key === '-' || e.code === 'NumpadSubtract')) {
                    e.preventDefault();
                    e.stopPropagation();
                    self.fontZoom(-1);
                } else if (!e.shiftKey && (e.key === '0' || e.code === 'Numpad0')) {
                    e.preventDefault();
                    e.stopPropagation();
                    self.fontZoom(0);
                } else if (!e.shiftKey && (e.key === 's' || e.key === 'S')) {
                    // Ctrl+S saves the active document (and never reaches the browser's save dialog).
                    e.preventDefault();
                    e.stopPropagation();
                    dotnetRef.invokeMethodAsync('HotkeySave');
                }
            };
            const upHandler = function (e) {
                if (self._switcherActive && (e.key === 'Control' || !e.ctrlKey)) {
                    self._switcherActive = false;
                    dotnetRef.invokeMethodAsync('HotkeySwitcherCommit');
                }
            };
            window.addEventListener('keydown', handler, true);
            window.addEventListener('keyup', upHandler, true);
            this._hotkeys = handler;
            this._hotkeysUp = upHandler;
        },

        unbindHotkeys: function () {
            if (this._hotkeys) {
                window.removeEventListener('keydown', this._hotkeys, true);
                this._hotkeys = null;
            }
            if (this._hotkeysUp) {
                window.removeEventListener('keyup', this._hotkeysUp, true);
                this._hotkeysUp = null;
            }
        },

        // ---- Tab-strip overflow: show the scroll buttons only when the row actually overflows. ----

        updateTabStrips: function () {
            document.querySelectorAll('.code-tabs-wrap').forEach(function (wrap) {
                const row = wrap.querySelector('.code-tabs');
                if (!row) {
                    return;
                }
                if (!wrap._fravashiWatched) {
                    wrap._fravashiWatched = true;
                    row.addEventListener('scroll', function () { window.fravashiCode.updateTabStrips(); }, { passive: true });
                    new ResizeObserver(function () { window.fravashiCode.updateTabStrips(); }).observe(row);
                }
                const overflow = row.scrollWidth > row.clientWidth + 1;
                wrap.classList.toggle('overflow', overflow);
                wrap.classList.toggle('at-start', row.scrollLeft <= 1);
                wrap.classList.toggle('at-end', row.scrollLeft + row.clientWidth >= row.scrollWidth - 1);
            });
        },

        scrollTabs: function (row, direction) {
            if (row && row.scrollBy) {
                row.scrollBy({ left: direction * 180, behavior: 'smooth' });
            }
        },

        // ---- Search integration (phase 3, ADR 0015) ----

        // Jump the editor to a match (1-based line/column) and select it, VS Code style.
        revealMatch: function (path, line, column, length) {
            if (!window.monaco || !editor) {
                return;
            }
            const model = monaco.editor.getModel(monaco.Uri.file(path));
            if (!model || editor.getModel() !== model) {
                return;
            }
            const range = new monaco.Range(line, column, line, column + length);
            editor.setSelection(range);
            editor.revealRangeInCenterIfOutsideViewport(range);
            editor.focus();
        },

        // Replace occurrences inside an OPEN DIRTY buffer (server-side writes would clobber the
        // unsaved edits, so search routes these through the model; the buffer stays dirty).
        replaceInBuffer: function (path, pattern, isRegex, matchCase, wholeWord, replacement, line, column) {
            if (!window.monaco) {
                return 0;
            }
            const model = monaco.editor.getModel(monaco.Uri.file(path));
            if (!model) {
                return 0;
            }
            const wordSeparators = wholeWord ? '`~!@#$%^&*()-=+[{]}\\|;:\'",.<>/?' : null;
            const matches = model.findMatches(pattern, false, isRegex, matchCase, wordSeparators, isRegex);
            let targets = matches;
            if (line && column) {
                targets = matches.filter(function (m) {
                    return m.range.startLineNumber === line && m.range.startColumn === column;
                });
            }
            if (targets.length === 0) {
                return 0;
            }
            const edits = targets.map(function (m) {
                let text = replacement;
                if (isRegex && m.matches) {
                    // Monaco returns capture groups; apply $1..$9 substitutions like VS Code.
                    text = replacement.replace(/\$(\d)/g, function (_, g) {
                        const value = m.matches[parseInt(g, 10)];
                        return value === undefined ? '' : value;
                    });
                }
                return { range: m.range, text: text };
            });
            model.pushEditOperations([], edits, function () { return null; });
            return targets.length;
        },

        // Bring the active tab into view when a file is opened/switched while its row is scrolled.
        revealActiveTab: function () {
            const tab = document.querySelector('.code-tab.active');
            if (tab && tab.scrollIntoView) {
                tab.scrollIntoView({ block: 'nearest', inline: 'nearest' });
            }
        },

        // Scroll the tree so the active file's row is visible (pairs with the server-side ancestor
        // expansion that runs when a file opens).
        revealTreeSelection: function () {
            const row = document.querySelector('.code-row.code-file.active');
            if (row && row.scrollIntoView) {
                row.scrollIntoView({ block: 'nearest' });
            }
        },

        copyText: function (text) {
            return navigator.clipboard.writeText(text);
        },

        // ---- Diff editor (the Git page's Changes view, phase 4) ----

        initDiff: async function (element) {
            await loadMonaco();
            if (diffEditor) {
                diffEditor.dispose();
            }
            diffEditor = monaco.editor.createDiffEditor(element, {
                automaticLayout: true,
                theme: currentTheme(),
                readOnly: true,
                renderSideBySide: true,
                fontSize: 13,
                scrollBeyondLastLine: false,
                mouseWheelZoom: true
            });
            watchTheme();
        },

        openDiff: function (path, original, modified) {
            if (!diffEditor || !window.monaco) {
                return;
            }
            const language = (function () {
                const byPath = monaco.editor.createModel('', undefined, monaco.Uri.parse('fravashi-lang://probe/' + path));
                const id = byPath.getLanguageId();
                byPath.dispose();
                return overrideLanguage(path) || id;
            })();
            const old = diffEditor.getModel();
            diffEditor.setModel({
                original: monaco.editor.createModel(original, language),
                modified: monaco.editor.createModel(modified, language)
            });
            if (old) {
                old.original.dispose();
                old.modified.dispose();
            }
        },

        // Work item #367: the Git page's detail pane switches between side by side and inline.
        setDiffInline: function (inline) {
            if (diffEditor) {
                diffEditor.updateOptions({ renderSideBySide: !inline });
            }
        },

        disposeDiff: function () {
            if (diffEditor) {
                const model = diffEditor.getModel();
                diffEditor.dispose();
                diffEditor = null;
                if (model) {
                    model.original.dispose();
                    model.modified.dispose();
                }
            }
        },

        // selectAll additionally selects the element's current text, so a pre-filled box (the
        // rename prompt) is replaced by typing rather than appended to.
        focusElement: function (selector, selectAll) {
            const element = document.querySelector(selector);
            if (element && element.focus) {
                element.focus();
                if (selectAll && element.select) {
                    element.select();
                }
            }
        }
    };
})();
