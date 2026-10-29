// Observe only, on /sessions with a conversation open: its usage strip (session cost) and every transcript line that
// records a commit review ("Commit review: pass|fail, by ..., N s, N tokens, $N, ..."), for compiling a trial's cost.
export default async (api) => {
  await api.sleep(1500);
  return api.evaluate(`(() => {
    const t = (e) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null);
    const detail = document.querySelector('[data-testid=sessions-detail]');
    const text = t(detail) || '';
    const reviews = [...text.matchAll(/Commit review: (pass|fail|timed out|error)[^$]{0,160}\\$[0-9.,]+/g)].map(m => m[0]);
    return {
      working: (text.match(/working on #[0-9]+/) || [null])[0],
      cost: t(document.querySelector('[data-testid=session-cost]')),
      reviews,
    };
  })()`);
};
