// Backlog 172: what the Claude Code card's sign-in panel shows. Reads the badge, whether the panel is folded (the
// signed-in sentence) or open (the key field and the start button), and the panel's text. PRESS=change opens it,
// PRESS=keep folds it again. SHOT=<file> saves a screenshot.
export default async (api) => {
  await api.waitFor(`!!document.querySelector('[data-testid=runtime-auth-Claude]')`, 15000);
  await api.sleep(800);
  const press = process.env.PRESS;
  if (press) {
    await api.evaluate(`document.querySelector('[data-testid=login-${press}-Claude]')?.click()`);
    await api.sleep(800);
  }

  const state = await api.evaluate(`(() => {
    const has = (id) => !!document.querySelector('[data-testid=' + id + ']');
    return {
      badge: document.querySelector('[data-testid=runtime-auth-Claude]')?.innerText.trim(),
      folded: has('login-signed-in-Claude'),
      keyField: has('login-apikey-Claude'),
      startButton: has('login-start-Claude'),
      keepButton: has('login-keep-Claude'),
      panel: document.querySelector('[data-testid=runtime-login-Claude]')?.innerText.replace(/\\s+/g, ' ').trim().slice(0, 400),
    };
  })()`);
  if (process.env.SHOT) {
    await api.evaluate(`document.querySelector('[data-testid=runtime-card-Claude]')?.scrollIntoView({ block: 'center' })`);
    state.shot = await api.shot(process.env.SHOT);
  }
  return state;
};
