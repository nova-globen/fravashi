// Backlog 170: presses "Suggest mapping with AI" on the board mapping step and waits for its answer (the CLI's own call,
// up to two minutes). Reports the error line if there is one, the source line, and the states each canonical row holds.
// A suggestion is applied to the rows only; nothing is saved until a person confirms, so this changes nothing stored.
export default async (api) => {
  // The suggestion needs the board's real states, which the step reads from the provider on "Load my board's states".
  await api.sleep(1500);
  if (await api.evaluate(`!!document.querySelector('[data-testid=mapping-load]')`)) {
    await api.evaluate(`document.querySelector('[data-testid=mapping-load]').click()`);
  }

  await api.waitFor(`!!document.querySelector('[data-testid=mapping-suggest], [data-testid=mapping-suggest-disabled]')`, 30000);
  // The step renders twice as it loads the board's states, and the button is replaced between the two.
  await api.sleep(3000);
  const disabled = await api.evaluate(`document.querySelector('[data-testid=mapping-suggest-disabled]')?.innerText.trim() ?? null`);
  if (disabled) return { disabled };

  const source = () => api.evaluate(`document.querySelector('[data-testid=mapping-source]')?.innerText.replace(/\\s+/g, ' ').trim() ?? null`);
  const before = await source();
  const started = Date.now();
  await api.evaluate(`document.querySelector('[data-testid=mapping-suggest]').click()`);
  await api.waitFor(`!!document.querySelector('[data-testid=mapping-suggest-error]') || !/name match/i.test(document.querySelector('[data-testid=mapping-source]')?.innerText ?? '')`, 150000);

  return {
    seconds: Math.round((Date.now() - started) / 1000),
    before,
    after: await source(),
    error: await api.evaluate(`document.querySelector('[data-testid=mapping-suggest-error]')?.innerText.trim() ?? null`),
    rows: await api.evaluate(`[...document.querySelectorAll('tr')].map(r => r.innerText.replace(/\\s+/g, ' ').trim()).filter(t => /^(Triage|Backlog|Ready|In Progress|In Review|Done|Cancelled)\\b/.test(t)).map(t => t.slice(0, 140))`),
  };
};
