// Opens the conversation whose row in the Sessions list contains CONV (for when someone in the window has opened another
// one, and the composer the supervisor types into is gone). Click only; observe in the next probe.
export default async (api) => api.evaluate(`(() => {
  const want = ${JSON.stringify(process.env.CONV || '')};
  const rows = [...document.querySelectorAll('a[href*="/sessions/"], [data-testid=session-card]')];
  const row = rows.find(r => (r.innerText || '').includes(want));
  if (!row) return { clicked: false, seen: rows.slice(0, 5).map(r => (r.innerText || '').slice(0, 60)) };
  setTimeout(() => row.click(), 50);
  return { clicked: true, what: (row.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').slice(0, 100) };
})()`);
