// 129: the toggle collapses the strip to a one-line summary. 128: Close workspace is pinned under a scrolling menu.
export default async (api) => {
  const t = `(e) => e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 200) : null`;
  const strip = () => api.evaluate(`(() => { const t = ${t}; const q = (id) => document.querySelector('[data-testid=' + id + ']');
    const s = q('session-work'); return { toggle: t(q('session-work-toggle')), summary: t(q('session-work-summary')), height: s ? Math.round(s.getBoundingClientRect().height) : null }; })()`);
  const open = await strip();
  await api.click('[data-testid=session-work-toggle]');
  await api.sleep(500);
  const collapsed = await strip();
  await api.click('[data-testid=session-work-toggle]');
  await api.sleep(300);

  await api.click('[data-testid=titlebar-workspace-trigger]');
  await api.sleep(700);
  const menu = await api.evaluate(`(() => { const t = ${t};
    const pinned = document.querySelector('[data-testid$=-pinned]');
    const list = pinned && pinned.parentElement ? [...pinned.parentElement.children].find(c => c !== pinned && getComputedStyle(c).overflowY !== 'visible') : null;
    const r = (e) => { if (!e) return null; const b = e.getBoundingClientRect(); return { y: Math.round(b.y), h: Math.round(b.height) }; };
    return { pinnedId: pinned ? pinned.getAttribute('data-testid') : null, pinnedText: t(pinned), pinnedRect: r(pinned),
      scroller: list ? { rect: r(list), overflowY: getComputedStyle(list).overflowY, scrollHeight: list.scrollHeight, clientHeight: list.clientHeight } : null }; })()`);
  await api.key('Escape');
  return { open, collapsed, menu };
};
