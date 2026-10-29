// Backlog 152. On /sessions: opens the work strip's details when they are collapsed, sets the folder's trust
// level to LEVEL ("ask" or "edits") when given, and reports the picker's value and its hover text.
export default async (api) => {
  const level = process.env.LEVEL;
  await api.evaluate(`(() => { const t = document.querySelector('[data-testid=session-work-toggle]'); if (t && t.getAttribute('aria-expanded') !== 'true') t.click(); })()`);
  await api.sleep(600);
  if (level) {
    await api.evaluate(`(() => { const s = document.querySelector('[data-testid=session-trust]'); if (!s) return; s.value = ${JSON.stringify(level)}; s.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await api.sleep(800);
  }
  return api.evaluate(`(() => {
    const s = document.querySelector('[data-testid=session-trust]');
    const hint = document.querySelector('.session-composer-hint');
    return { present: !!s, value: s ? s.value : null, title: s ? s.title.slice(0, 200) : null, hint: hint ? hint.innerText.replace(/\\s+/g, ' ').trim().slice(0, 160) : null };
  })()`);
};
