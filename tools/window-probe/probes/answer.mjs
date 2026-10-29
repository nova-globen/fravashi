// Reads the first pending approval prompt (its subject, warning and effects), then presses ANSWER ("approve" or
// "deny") when given. Returns what was on the prompt before the press, so the proof records what was answered.
// REASON=<text> with ANSWER=deny types the person's reason into the card's field first (backlog 220), so the agent
// reads why; a studio older than that has no field, and the result says so rather than denying without it.
export default async (api) => {
  const read = () => api.evaluate(`JSON.stringify((() => {
    const t = (e) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null);
    const p = document.querySelector('[data-testid=approval-prompt]');
    if (!p) return { pending: 0 };
    const q = (id) => p.querySelector('[data-testid=' + id + ']');
    return {
      pending: document.querySelectorAll('[data-testid=approval-prompt]').length,
      text: t(p).slice(0, 700),
      effect: t(q('approval-effect')),
      warning: t(q('approval-warning')),
      outside: !!q('approval-outside'),
    };
  })())`);
  const before = JSON.parse(await read());
  const answer = process.env.ANSWER;
  let pressed = null;
  if (answer === 'deny' && process.env.REASON && before.pending > 0) {
    const typed = await api.evaluate(`(() => {
      const i = document.querySelector('[data-testid=approval-prompt] [data-testid=deny-reason]');
      if (!i) return false;
      i.value = ${JSON.stringify(process.env.REASON)};
      i.dispatchEvent(new Event('input', { bubbles: true }));
      return true;
    })()`);
    if (!typed) return { before, pressed: 'no deny-reason field: this studio predates backlog 220, nothing pressed', after: before };
    await api.sleep(400);
  }
  if (answer && before.pending > 0) {
    pressed = await api.evaluate(`(() => { const b = document.querySelector('[data-testid=approval-prompt] [data-testid=${answer}]'); if (!b) return 'no ' + ${JSON.stringify(answer)} + ' button'; b.click(); return 'pressed ' + ${JSON.stringify(answer)}; })()`);
    await api.sleep(1200);
  }
  return { before, pressed, after: JSON.parse(await read()) };
};
