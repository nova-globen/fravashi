// Settings > Environment > Board mapping. ACT=load presses "Load my board's statuses" and returns each row's selection;
// ACT=confirm presses Confirm mapping and returns whether it said saved. Run with the window on /settings/board.
export default async (api) => {
  const act = process.env.ACT || 'load';
  await api.waitFor(`!!document.querySelector('[data-testid=mapping-table]')`, 15000);
  if (act === 'load') {
    await api.evaluate(`document.querySelector('[data-testid=mapping-load]').click()`);
    await api.waitFor(`!!document.querySelector('[data-testid=mapping-unmapped]') || !!document.querySelector('[data-testid=mapping-load-error]')`, 30000);
    await api.sleep(1000);
    return api.evaluate(`({
      error: (document.querySelector('[data-testid=mapping-load-error]') || {}).innerText || null,
      unmapped: (document.querySelector('[data-testid=mapping-unmapped]') || {}).innerText || null,
      rows: [...document.querySelectorAll('[data-testid=mapping-table] tbody tr')].map(r => r.innerText.replace(/[ \\t\\r\\n]+/g, ' ').trim()) })`);
  }
  await api.evaluate(`document.querySelector('[data-testid=mapping-confirm]').click()`);
  return { saved: await api.waitFor(`!!document.querySelector('[data-testid=mapping-saved]')`, 15000) };
};
