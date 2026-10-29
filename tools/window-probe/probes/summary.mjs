// Item 40, the other half, in the installed window: press "Write a summary" on the open document and watch.
export default async (api) => {
  const read = () => api.evaluate(`(() => {
    const t = (id) => { const e = document.querySelector('[data-testid=' + id + ']'); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 700) : null; };
    const b = document.querySelector('[data-testid=wiki-summarise]');
    return { path: location.pathname, state: t('wiki-summary-state'), text: t('wiki-summary-text'), refusal: t('wiki-summary-refusal'),
      unavailable: t('wiki-summary-unavailable'), button: b ? { label: b.innerText.trim(), disabled: b.disabled } : null };
  })()`);

  const before = await read();
  if (!before.button || before.button.disabled) return { before, pressed: false };
  await api.evaluate(`document.querySelector('[data-testid=wiki-summarise]').click()`);
  const seen = [];
  for (let i = 0; i < 100; i++) {
    await api.sleep(1000);
    const now = await read();
    const s = JSON.stringify(now);
    if (seen[seen.length - 1] !== s) seen.push(s);
    if (i > 2 && now.button && !now.button.disabled && now.button.label !== 'Writing...') break;
  }
  return { before, pressed: true, progression: seen.map((s) => JSON.parse(s)) };
};
