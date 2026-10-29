// On /board/sync: ACT=<data-testid> presses that button and waits for sync-outcome to change (up to WAIT ms), then
// reports the outcome line and the overview. ACT empty only reads.
export default async (api) => {
  const act = process.env.ACT || '';
  const read = () => api.evaluate(`(() => { const t = (id) => document.querySelector('[data-testid=' + id + ']')?.innerText.replace(/\\s+/g, ' ').trim() ?? null; return { outcome: t('sync-outcome'), overview: t('sync-overview')?.slice(0, 600), queue: t('sync-queue')?.slice(0, 400), conflicts: t('sync-conflicts')?.slice(0, 400) }; })()`);
  const before = await read();
  if (!act) return before;
  const did = await api.evaluate(`(() => { const e = document.querySelector('[data-testid=${act}]'); if (!e) return 'no ${act}'; if (e.disabled) return '${act} is disabled'; e.click(); return 'pressed ${act}'; })()`);
  const wait = Number(process.env.WAIT || 60000), start = Date.now();
  let after = before;
  while (Date.now() - start < wait) { await api.sleep(1500); after = await read(); if (after.outcome && after.outcome !== before.outcome) break; }
  return { did, ms: Date.now() - start, ...after };
};
