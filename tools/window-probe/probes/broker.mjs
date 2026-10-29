// The broker card on Settings > Environment. ACT=read (default), mint or revoke. One act per run.
export default async (api) => {
  const read = () => api.evaluate(`(() => {
    const t = (id) => { const e = document.querySelector('[data-testid=' + id + ']'); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 260) : null; };
    const card = document.querySelector('[data-testid=broker-card]');
    if (card) card.scrollIntoView({ block: 'center' });
    return { path: location.pathname, card: !!card, tier: t('broker-tier'), identity: t('broker-identity'), token: t('broker-token-state'),
      lastMint: t('broker-last-mint'), message: t('broker-message'), blocked: t('broker-runs-blocked'), misconfigured: t('broker-misconfigured'), inactive: t('broker-inactive') };
  })()`);
  await api.sleep(1200);
  const before = await read();
  const act = process.env.ACT || 'read';
  if (act === 'read' || !before.card) return { before };
  const started = Date.now();
  await api.evaluate(`document.querySelector('[data-testid=broker-${act}]').click()`);
  let after = before;
  for (let i = 0; i < 40; i++) {
    await api.sleep(1000);
    after = await read();
    if (after.message && after.message !== before.message) break;
  }
  return { before, act, seconds: Math.round((Date.now() - started) / 100) / 10, after };
};
