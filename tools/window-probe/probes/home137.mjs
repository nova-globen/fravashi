// Item 137 in the installed window: the panels, their rectangles, the scrolling list and the filter.
export default async (api) => {
  await api.sleep(800);
  const read = () => api.evaluate(`(() => {
    const rect = (el) => { if (!el) return null; const r = el.getBoundingClientRect(); return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) }; };
    const q = (id) => document.querySelector('[data-testid=' + id + ']');
    const list = q('home-recents-list');
    const bg = (el) => el ? getComputedStyle(el).backgroundColor : null;
    return {
      path: location.pathname,
      panels: Object.fromEntries(['home-recents', 'home-workspace', 'home-open-folder', 'home-clone'].map((id) => [id, rect(q(id))])),
      panelBackground: bg(q('home-recents')),
      list: list ? { rect: rect(list), scrollHeight: list.scrollHeight, clientHeight: list.clientHeight, overflowY: getComputedStyle(list).overflowY, rows: list.children.length } : null,
      count: q('home-recents-count') ? q('home-recents-count').innerText : null,
      nomatch: q('home-recents-nomatch') ? q('home-recents-nomatch').innerText : null,
      pageScrolls: document.documentElement.scrollHeight > document.documentElement.clientHeight,
      conversationsFilter: !!q('recent-conversations-filter'),
    };
  })()`);

  const before = await read();
  await api.click('[data-testid=home-recents-filter]');
  await api.type('proof');
  await api.sleep(600);
  const filtered = await read();
  await api.key('Control+a');
  await api.type('zzzz-nothing');
  await api.sleep(600);
  const nothing = await read();
  await api.key('Control+a');
  await api.key('Backspace');
  await api.sleep(400);
  return { before, filtered: { rows: filtered.list && filtered.list.rows, count: filtered.count }, nothing: { rows: nothing.list && nothing.list.rows, nomatch: nothing.nomatch, count: nothing.count } };
};
