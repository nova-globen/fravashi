// Open the first document in the wiki list. Click only; observe in the next probe.
export default async (api) => api.evaluate(`(() => {
  const e = document.querySelector('[data-testid=wiki-entry]');
  if (!e) return 'no entry';
  const target = e.matches('a,button') ? e : (e.querySelector('a,button') || e);
  setTimeout(() => target.click(), 50);
  return 'clicking: ' + (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 80);
})()`);
