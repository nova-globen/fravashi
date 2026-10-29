// Backlog 47/48 in the installed window: what a move this board makes means on the provider.
//
// ACT=drag COLUMN=done      drags a card into that column and reads whatever came back: CARD=#5 picks
//                           the card whose text contains that, else the first one on the page.
//                           the question with the provider's own state names, or the queued line.
// ACT=choose PICK=Closed    presses that state in the question.
// ACT=later                 presses "Decide later" and checks the move here stood anyway.
// ACT=read                  the board's provider lines as they stand now.
// ACT=conflicts             every open disagreement on /board/sync, in full.
// ACT=settle SIDE=provider  settles the FIRST open disagreement (SIDE=local keeps ours).
//
// A drag is dispatched as real DOM drag events rather than driven by the mouse: the studio's own
// component test does the same, and CDP cannot synthesise a drag the browser's drag machinery will run.
export default async (api) => {
  const act = process.env.ACT || 'read';

  const readBoard = () => api.evaluate(`(() => {
    const t = (id, n) => { const e = document.querySelector('[data-testid=' + id + ']'); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, n || 400) : null; };
    return {
      path: location.pathname,
      ask: t('board-move-ask', 700),
      choices: [...document.querySelectorAll('[data-testid=board-move-choice]')].map(e => e.textContent.trim()),
      queued: t('board-move-queued'),
      error: t('board-error'),
      columns: [...document.querySelectorAll('[data-testid^=board-column-]')]
        .map(e => e.dataset.testid.replace('board-column-', '') + ': '
          + (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 90)),
    };
  })()`);

  const readConflicts = () => api.evaluate(`(() => {
    const t = (e, n) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, n || 600) : null);
    return {
      path: location.pathname,
      outcome: t(document.querySelector('[data-testid=sync-outcome]'), 400),
      conflicts: [...document.querySelectorAll('[data-testid^=sync-conflict-]')].map(e => t(e, 500)),
      overview: t(document.querySelector('[data-testid=sync-overview]'), 600),
      queue: t(document.querySelector('[data-testid=sync-queue]'), 500),
    };
  })()`);

  if (act === 'read') return readBoard();
  if (act === 'conflicts') return readConflicts();

  if (act === 'drag') {
    const column = process.env.COLUMN || 'done';
    const moved = await api.evaluate(`(() => {
      const wanted = ${JSON.stringify(process.env.CARD || '')};
      const cards = [...document.querySelectorAll('[data-testid=board-card]')];
      const card = wanted ? cards.find(c => (c.innerText || '').includes(wanted)) : cards[0];
      const column = document.querySelector('[data-testid=board-column-${column}]');
      if (!card || !column) return { ok: false, card: !!card, column: !!column };
      const title = (card.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 60);
      // A REAL DataTransfer, which is what a browser gives a genuine drag. An event built without one
      // carries dataTransfer: null and Blazor's interop does not deliver it: the listeners fire, the
      // handler never runs, and the board simply does nothing. bunit cannot catch that, because it
      // hands the handler a DragEventArgs directly and serialises nothing. Measured 2026-09-22.
      const dt = new DataTransfer();
      const make = (type) => new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer: dt });
      card.dispatchEvent(make('dragstart'));
      column.dispatchEvent(make('dragover'));
      column.dispatchEvent(make('drop'));
      return { ok: true, dragged: title };
    })()`);
    if (!moved.ok) return moved;
    // A provider is asked over the network here, so this waits rather than assuming.
    for (let i = 0; i < 40; i++) {
      await api.sleep(1000);
      const now = await readBoard();
      if (now.ask || now.queued || now.error) return { ...moved, ...now };
    }
    return { ...moved, timedOut: true, ...(await readBoard()) };
  }

  if (act === 'choose') {
    const pick = process.env.PICK || '';
    const pressed = await api.evaluate(`(() => {
      const wanted = ${JSON.stringify(pick)};
      const buttons = [...document.querySelectorAll('[data-testid=board-move-choice]')];
      const one = wanted ? buttons.find(b => b.textContent.trim() === wanted) : buttons[0];
      if (!one) return { pressed: false, offered: buttons.map(b => b.textContent.trim()) };
      one.click();
      return { pressed: true, picked: one.textContent.trim() };
    })()`);
    if (!pressed.pressed) return pressed;
    for (let i = 0; i < 40; i++) {
      await api.sleep(1000);
      const now = await readBoard();
      if (now.queued || now.error || !now.ask) return { ...pressed, ...now };
    }
    return { ...pressed, timedOut: true, ...(await readBoard()) };
  }

  if (act === 'later') {
    await api.evaluate(`(() => { const b = document.querySelector('[data-testid=board-move-later]'); if (b) b.click(); })()`);
    await api.sleep(1200);
    return readBoard();
  }

  if (act === 'settle') {
    const side = (process.env.SIDE || 'provider') === 'local' ? 'sync-keep-local' : 'sync-keep-provider';
    const pressed = await api.evaluate(`(() => {
      const one = document.querySelector('[data-testid^=${side}-]');
      if (!one) return { pressed: false };
      one.click();
      return { pressed: true, which: one.dataset.testid };
    })()`);
    if (!pressed.pressed) return { ...pressed, ...(await readConflicts()) };
    await api.sleep(2500);
    return { ...pressed, ...(await readConflicts()) };
  }

  return { unknownAct: act };
};
