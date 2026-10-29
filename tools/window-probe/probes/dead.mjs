// Item 136: what does the page show once the circuit is lost? Read only, no clicks.
export default async (api) => api.evaluate(`(() => {
  const t = (e) => e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 300) : null;
  const overlays = [...document.querySelectorAll('[id*=reconnect], [class*=reconnect], [data-testid*=reconnect]')]
    .map(e => ({ id: e.id || e.className || e.getAttribute('data-testid'), shown: getComputedStyle(e).display !== 'none' && getComputedStyle(e).visibility !== 'hidden' && e.getBoundingClientRect().height > 0, text: t(e) }));
  return { path: location.pathname, overlays, bodyTail: t(document.body).slice(-200) };
})()`);
