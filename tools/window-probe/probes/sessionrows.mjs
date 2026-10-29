// Observe only, on /sessions: how many trusted rows, automatic refusals, pending prompts and tool cards the open
// conversation shows, the trusted rows' text, and the transcript's tail (TAIL characters, default 1500).
export default async (api) => {
  await api.sleep(800);
  const tail = Number(process.env.TAIL || 1500);
  return api.evaluate(`(() => {
    const t = (e) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null);
    const all = [...document.querySelectorAll('[data-testid]')];
    const count = (id) => document.querySelectorAll('[data-testid=' + id + ']').length;
    const scroller = all.filter(e => /transcript/i.test(e.getAttribute('data-testid')))[0];
    return {
      path: location.pathname,
      trusted: [...document.querySelectorAll('[data-testid=trusted-note]')].map(e => t(e).slice(0, 160)),
      autoRefused: count('auto-refused-note'),
      refused: count('refused-note'),
      denied: count('denied-note'),
      pending: count('approval-prompt'),
      toolCards: count('tool-call'),
      stop: count('stop-session'),
      tail: scroller ? t(scroller).slice(-${tail}) : null,
    };
  })()`);
};
