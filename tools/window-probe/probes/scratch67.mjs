// Backlog 67: the scratchpad in a real Chromium engine. Every behaviour the acceptance names, in one run.
// ACT=open (default)  shows the title bar, opens the scratchpad, types markdown, and reads back what renders.
// ACT=tick            clicks the box of the first open to-do and reads the text back.
// ACT=copy            selects everything and records what a copy puts on the clipboard, per type.
// ACT=read            the panel's state and the editor's text, without touching anything.
//
// Headless Edge has no WebView2 bridge, so the page never adds html.window-host by itself and the title bar
// stays hidden. The probe adds that ONE class and says so: this checks the scratchpad, not the window.
//
// Focus goes through Monaco's own focus(). This Monaco takes typing through an "edit context" element; its
// 'ime-text-area' textarea is only for input-method composition, and focusing THAT directly (as the first
// draft did) takes focus away from where typing lands, so every keystroke silently vanishes.
export default async (api) => {
  const act = process.env.ACT || 'open';
  const editorJs = `monaco.editor.getEditors().find(e => e.getContainerDomNode().closest('[data-testid=scratchpad]'))`;
  const text = () => api.evaluate(`(() => { const e = ${editorJs}; return e ? e.getValue() : null; })()`);

  await api.evaluate(`document.documentElement.classList.add('window-host')`);
  // Headless pages do not count as focused unless told to, and Monaco ignores input it has no text focus for.
  await api.send('Emulation.setFocusEmulationEnabled', { enabled: true });

  if (act === 'read') {
    const state = await api.evaluate(`(() => ({
      open: !!document.querySelector('[data-testid=scratchpad]'),
      pressed: document.querySelector('[data-testid=titlebar-scratchpad-toggle]')?.getAttribute('aria-pressed'),
      path: location.pathname,
    }))()`);
    return { ...state, text: state.open ? await text() : null };
  }

  if (act === 'open') {
    if (!(await api.waitFor(`!!document.querySelector('[data-testid=titlebar-scratchpad-toggle]')`, 10000))) {
      return 'no scratchpad toggle in the title bar';
    }
    const nextToMinimise = await api.evaluate(
      `document.querySelector('[data-testid=titlebar-scratchpad-toggle]').nextElementSibling?.getAttribute('title')`);
    if (!(await api.evaluate(`!!document.querySelector('[data-testid=scratchpad]')`))) {
      await api.evaluate(`document.querySelector('[data-testid=titlebar-scratchpad-toggle]').click()`);
    }
    if (!(await api.waitFor(`!!(window.monaco && ${editorJs})`, 15000))) {
      return { nextToMinimise, error: 'the editor never appeared' };
    }
    // Start empty, then TYPE: typing is what runs the change handler, the decorations and the save.
    await api.evaluate(`${editorJs}.setValue('')`);
    await api.evaluate(`${editorJs}.focus()`);
    await api.type('# Today\n\nSome **bold** and *italic* text.\n\n- [ ] ring the bank\n- [x] ship the fix\n');
    await api.sleep(1200); // past the 600 ms quiet-moment save
    const rendered = await api.evaluate(`(() => {
      const root = document.querySelector('[data-testid=scratchpad]');
      const lineWith = (needle) => [...root.querySelectorAll('.view-line')].find(l => l.textContent.includes(needle));
      const spanWith = (line, needle) => [...(line?.querySelectorAll('span span') || [])].find(s => s.textContent.includes(needle));
      const bold = spanWith(lineWith('bold'), 'bold');
      const italic = spanWith(lineWith('italic'), 'italic');
      const done = root.querySelector('.scratch-done');
      return {
        panelOpen: true,
        boldWeight: bold ? getComputedStyle(bold).fontWeight : null,
        italicStyle: italic ? getComputedStyle(italic).fontStyle : null,
        checkboxes: root.querySelectorAll('.scratch-box').length,
        doneLines: root.querySelectorAll('.scratch-done').length,
        doneStrike: done ? getComputedStyle(done).textDecorationLine : null,
        doneText: done?.textContent || null,
      };
    })()`);
    return { nextToMinimise, ...rendered, text: await text() };
  }

  if (act === 'tick') {
    // A real mouse click on the box's own screen position, so Monaco's mouse handling is what runs.
    const at = await api.evaluate(`(() => {
      // Monaco draws a space as U+00A0, so an open box is '[', U+00A0, ']' in the DOM. Compared with no
      // escapes on purpose: inside this template string a backslash is eaten before the page sees it, and the
      // first two drafts sent the page a regex that could never match.
      const nbsp = String.fromCharCode(160);
      const box = [...document.querySelectorAll('[data-testid=scratchpad] .scratch-box')]
        .find(b => b.textContent.split(nbsp).join(' ') === '[ ]');
      if (!box) return null;
      const r = box.getBoundingClientRect();
      return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    })()`);
    if (!at) return 'no open to-do box on screen';
    for (const type of ['mousePressed', 'mouseReleased']) {
      await api.send('Input.dispatchMouseEvent', { type, x: at.x, y: at.y, button: 'left', clickCount: 1 });
    }
    await api.sleep(1200);
    return {
      text: await text(),
      doneLines: await api.evaluate(`document.querySelectorAll('[data-testid=scratchpad] .scratch-done').length`),
    };
  }

  if (act === 'copy') {
    // A GENUINE Ctrl+C: document.execCommand('copy') from a script is not a user gesture, so Chromium never
    // fires the copy event for it (the first draft got null). The key event carries the 'copy' editing
    // command, which is what a real keystroke does.
    await api.evaluate(`(() => {
      const ed = ${editorJs};
      ed.focus();
      ed.setSelection(ed.getModel().getFullModelRange());
      window.__copySeen = null;
      document.addEventListener('copy', (e) => {
        // Bubbling: runs after Monaco's own handler has filled this event's clipboard data.
        window.__copySeen = { types: [...e.clipboardData.types], plain: e.clipboardData.getData('text/plain'),
                              html: e.clipboardData.getData('text/html') };
      }, { once: true });
    })()`);
    await api.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', key: 'c', code: 'KeyC', modifiers: 2,
                                               windowsVirtualKeyCode: 67, commands: ['copy'] });
    await api.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'c', code: 'KeyC', modifiers: 2, windowsVirtualKeyCode: 67 });
    await api.sleep(300);
    return api.evaluate(`window.__copySeen`);
  }

  return 'unknown ACT';
};
