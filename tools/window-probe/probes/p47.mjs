// Item 47 in the installed window: the board's provider acts.
// ACT=sync (default)  presses "Sync now" on /board/sync and records what it says.
// ACT=import          presses "Bring in what <provider> has".
// ACT=read            what the sync page says now: the provider line, the mirrored items, the queue.
// ACT=send            on a work item's page, presses "Send to <provider>" and records the line.
// ACT=new TITLE=...   creates a work item from the board and opens it.
export default async (api) => {
  const act = process.env.ACT || 'sync';
  const t = (id, n) => `(() => { const e = document.querySelector('[data-testid=${id}]'); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, ${n || 400}) : null; })()`;

  const readSync = () => api.evaluate(`(() => {
    const t = (id, n) => { const e = document.querySelector('[data-testid=' + id + ']'); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, n || 400) : null; };
    return { path: location.pathname, follows: t('sync-now-section'), outcome: t('sync-outcome'),
      overview: t('sync-overview', 700), queue: t('sync-queue', 400), localOnly: t('sync-local-only'),
      buttons: [...document.querySelectorAll('[data-testid^=sync-]')].map(e => e.dataset.testid).filter(i => i === 'sync-now' || i === 'sync-import') };
  })()`);

  const readItem = () => api.evaluate(`(() => {
    const t = (id, n) => { const e = document.querySelector('[data-testid=' + id + ']'); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, n || 400) : null; };
    return { path: location.pathname, provider: t('work-item-provider-line'), title: t('work-item-detail', 90),
      canSend: !!document.querySelector('[data-testid=work-item-send]') };
  })()`);

  if (act === 'read') return readSync();
  if (act === 'readitem') return readItem();

  if (act === 'new') {
    await api.evaluate(`(() => { const b = document.querySelector('[data-testid=new-work-item]'); if (b) b.click(); })()`);
    await api.sleep(1200);
    await api.evaluate(`(() => {
      const set = (id, v) => { const e = document.querySelector('[data-testid=' + id + ']'); if (e) { e.value = v; e.dispatchEvent(new Event('input', { bubbles: true })); } };
      set('work-item-title', ${JSON.stringify(process.env.TITLE || 'Probe item')});
      set('work-item-description', 'Created by a probe for backlog 47.');
    })()`);
    await api.sleep(600);
    await api.evaluate(`(() => { const b = document.querySelector('[data-testid=work-item-save]'); if (b) b.click(); })()`);
    await api.sleep(2500);
    return readItem();
  }

  if (act === 'send') {
    const pressed = await api.evaluate(`(() => { const b = document.querySelector('[data-testid=work-item-send]'); if (!b) return false; b.click(); return true; })()`);
    await api.sleep(2500);
    return { pressed, ...(await readItem()) };
  }

  const id = act === 'import' ? 'sync-import' : 'sync-now';
  const pressed = await api.evaluate(`(() => { const b = document.querySelector('[data-testid=${id}]'); if (!b || b.disabled) return false; b.click(); return true; })()`);
  if (!pressed) return { pressed: false, ...(await readSync()) };
  for (let i = 0; i < 60; i++) {
    await api.sleep(1000);
    const now = await readSync();
    if (now.outcome) return { pressed: true, ...now };
  }
  return { pressed: true, timedOut: true, ...(await readSync()) };
};
