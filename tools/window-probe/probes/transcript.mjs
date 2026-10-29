// Observe only: the wiki list (on /wiki) or the tail of the open conversation (on /sessions), as text.
// TAIL is how many characters of the transcript to return (default 2500).
export default async (api) => {
  await api.sleep(1000);
  const tail = Number(process.env.TAIL || 2500);
  return api.evaluate(`(() => {
    const t = (e) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null);
    const entries = [...document.querySelectorAll('[data-testid=wiki-entry]')].map(t);
    const all = [...document.querySelectorAll('[data-testid]')];
    const scroller = all.filter(e => /transcript/i.test(e.getAttribute('data-testid')))[0];
    const tools = all.filter(e => /tool/i.test(e.getAttribute('data-testid'))).map(e => t(e).slice(0, 160)).slice(-14);
    const text = scroller ? t(scroller) : null;
    return { path: location.pathname, entries, tools, transcript: text ? text.slice(-${tail}) : null };
  })()`);
};
