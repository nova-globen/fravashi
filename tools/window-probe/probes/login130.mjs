// Item 130 in the installed window: the agent CLI sign-in panels. ACT=list (default) says which panels and
// start buttons are on the page. ACT=start TOOL=<name as in the testid> presses that start button, watches the
// output for up to 30 seconds, reports the URL it shows and only the SHAPE of the one-time code (letters as A,
// digits as 9), then presses the panel's stop button so no sign-in is left waiting.
export default async (api) => {
  await api.sleep(1000);
  if ((process.env.ACT || 'list') === 'list') {
    return api.evaluate(`({ path: location.pathname, ids: [...document.querySelectorAll('[data-testid]')].map(e => e.getAttribute('data-testid')).filter(i => /^(runtime-login|login-start|login-no-interactive|login-key-saved|runtime-)/.test(i)) })`);
  }
  const tool = process.env.TOOL;
  const pressed = await api.evaluate(`(() => { const b = document.querySelector('[data-testid="login-start-${tool}"]'); if (!b) return false; b.scrollIntoView({block:'center'}); b.click(); return true; })()`);
  if (!pressed) return { pressed: false };
  const read = () => api.evaluate(`(() => {
    const panel = document.querySelector('[data-testid="runtime-login-${tool}"]');
    const u = panel.querySelector('[data-testid=login-url]'), c = panel.querySelector('[data-testid=login-code]');
    const out = panel.querySelector('[data-testid="login-output-${tool}"]');
    return { url: u ? u.href : null, codeShape: c ? c.innerText.trim().replace(/[A-Za-z]/g, 'A').replace(/[0-9]/g, '9') : null,
      lines: out ? out.querySelectorAll('[data-testid=login-chunk]').length : 0,
      text: out ? out.innerText.replace(/[ \\t\\r\\n]+/g, ' ').replace(/[A-Z0-9]{4}-[A-Z0-9]{4,5}/g, '<code>').slice(0, 400) : null };
  })()`);
  let last = null;
  for (let i = 0; i < 30; i++) { await api.sleep(1000); last = await read(); if (last.url && last.codeShape) break; }
  const stopped = await api.evaluate(`(() => { const b = document.querySelector('[data-testid="login-stop-${tool}"]'); if (!b) return false; b.click(); return true; })()`);
  return { pressed, ...last, stopped };
};
