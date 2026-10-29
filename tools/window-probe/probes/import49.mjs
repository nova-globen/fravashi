// Item 49 in the installed window: Settings > Environment > Project, the import panel.
// ACT=preview (default)  presses Preview and reads what the panel says.
// ACT=run                presses Import and waits for it to finish, then reads what the panel says.
// ACT=read               reads what the panel says, pressing nothing.
// SHOT=<file>            also saves a screenshot of the panel afterwards.
export default async (api) => {
  const act = process.env.ACT || 'preview';
  await api.waitFor(`!!document.querySelector('[data-testid=import-panel]')`, 8000);
  const read = () => api.evaluate(`(() => {
    const t = (id) => { const e = document.querySelector('[data-testid=' + id + ']'); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null; };
    const problems = document.querySelector('[data-testid=import-problems]');
    return { summary: t('import-summary'), refusal: t('import-refusal'), done: t('import-done'),
      problems: problems ? [...problems.children].map(li => li.innerText.trim()) : [],
      runEnabled: !document.querySelector('[data-testid=import-run]').disabled };
  })()`);
  if (act !== 'read') {
    const button = act === 'run' ? 'import-run' : 'import-preview';
    await api.evaluate(`document.querySelector('[data-testid=${button}]').click()`);
    const until = act === 'run' ? `!!document.querySelector('[data-testid=import-done], [data-testid=import-refusal]')`
      : `!!document.querySelector('[data-testid=import-summary]')`;
    await api.waitFor(until, act === 'run' ? 170000 : 30000);
  }
  const result = await read();
  if (process.env.SHOT) {
    await api.evaluate(`document.querySelector('[data-testid=import-panel]').scrollIntoView({ block: 'center' })`);
    await api.sleep(600);
    result.shot = await api.shot(process.env.SHOT);
  }
  return result;
};
