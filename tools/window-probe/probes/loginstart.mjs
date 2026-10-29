// Presses the runtime's "Start interactive sign-in" (TOOL, as in the testid) and reports what the CLI prints for up
// to 30 seconds: the link and the prompt. Unlike login130.mjs it does NOT stop the sign-in, because a person is going
// to finish it in the window. Never types anything into the panel.
export default async (api) => {
  const tool = process.env.TOOL || 'Claude';
  const pressed = await api.evaluate(`(() => { const b = document.querySelector('[data-testid="login-start-${tool}"]'); if (!b) return false; b.scrollIntoView({block:'center'}); b.click(); return true; })()`);
  const read = () => api.evaluate(`(() => {
    const panel = document.querySelector('[data-testid="runtime-login-${tool}"]');
    const u = panel && panel.querySelector('[data-testid=login-url]');
    const out = panel && panel.querySelector('[data-testid="login-output-${tool}"]');
    return { url: u ? u.href : null, input: !!(panel && panel.querySelector('[data-testid="login-input-${tool}"]')),
      text: out ? out.innerText.replace(/[ \\t\\r\\n]+/g, ' ').slice(0, 1500) : null };
  })()`);
  let last = null;
  for (let i = 0; i < 30; i++) { await api.sleep(1000); last = await read(); if (last.url && last.input) break; }
  return { pressed, ...last };
};
