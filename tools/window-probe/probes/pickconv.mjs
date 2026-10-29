// Open the first conversation in the Sessions list. Click only; observe in the next probe.
export default async (api) => api.evaluate(`(() => {
  const ids = [...new Set([...document.querySelectorAll('[data-testid]')].map(e => e.getAttribute('data-testid')))];
  const row = document.querySelector('[data-testid=session-card]')
    || [...document.querySelectorAll('a[href*="/sessions/"]')][0];
  if (!row) return { clicked: false, ids: ids.slice(0, 40) };
  setTimeout(() => row.click(), 50);
  return { clicked: true, what: (row.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').slice(0, 100) };
})()`);
