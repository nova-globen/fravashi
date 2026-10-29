// Backlog 159. On /sessions: reports the browser band (address, errors, whether a frame is showing and its size) and,
// with CLICKX and CLICKY (fractions of the picture, 0 to 1), clicks that spot of the picture as a person would.
export default async (api) => {
  const read = () => api.evaluate(`JSON.stringify((() => {
    const t = (e) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null);
    const q = (id) => document.querySelector('[data-testid=' + id + ']');
    const img = q('session-browser-frame');
    const r = img && img.getBoundingClientRect ? img.getBoundingClientRect() : null;
    return {
      band: !!q('session-browser'),
      url: t(q('session-browser-url')),
      errors: t(q('session-browser-errors')),
      frame: img && img.tagName === 'IMG' ? { w: Math.round(r.width), h: Math.round(r.height), srcLength: (img.getAttribute('src') || '').length } : null,
      said: t(q('session-browser-said')),
    };
  })())`);
  const before = JSON.parse(await read());
  let clicked = null;
  if (process.env.CLICKX && before.frame) {
    clicked = await api.evaluate(`(() => {
      const img = document.querySelector('[data-testid=session-browser-frame]');
      const r = img.getBoundingClientRect();
      const x = r.left + r.width * ${Number(process.env.CLICKX)}, y = r.top + r.height * ${Number(process.env.CLICKY)};
      img.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: x, clientY: y }));
      return { x: Math.round(x), y: Math.round(y) };
    })()`);
    await api.sleep(3500);
  }
  return { before, clicked, after: clicked ? JSON.parse(await read()) : null };
};
