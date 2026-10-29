// Backlog 173: what the agent pane says about the workspace's contained copy. Reports whether it shows the failure (and
// its reason), the generic "no copy yet" sentence, or the ordinary pane (a composer). PRESS=retry presses Try again first
// and waits for the pane to change. SHOT=<file> saves a screenshot.
export default async (api) => {
  const read = () => api.evaluate(`(() => {
    const t = (id) => document.querySelector('[data-testid=' + id + ']')?.innerText.replace(/\\s+/g, ' ').trim() ?? null;
    return {
      path: location.pathname,
      failed: t('session-copy-failed-reason'),
      retry: !!document.querySelector('[data-testid=session-copy-retry]'),
      noCopy: t('session-no-copy'),
      composer: !!document.querySelector('[data-testid=composer-input]'),
    };
  })()`);
  await api.sleep(1500);
  const before = await read();
  let after = null;
  if (process.env.PRESS === 'retry' && before.retry) {
    await api.evaluate(`document.querySelector('[data-testid=session-copy-retry]').click()`);
    await api.waitFor(`!document.querySelector('[data-testid=session-copy-retry]') || !!document.querySelector('[data-testid=composer-input]')`, 20000);
    await api.sleep(1500);
    after = await read();
  }
  const result = { before, after };
  if (process.env.SHOT) result.shot = await api.shot(process.env.SHOT);
  return result;
};
