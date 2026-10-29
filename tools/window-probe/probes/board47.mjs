// Is a Jira BOARD connection configured, and with which project? Reads the onboarding board step's fields as
// the page shows them. Values that look like a credential are never returned: a password field's value is
// reported only as whether it is set.
export default async (api) => {
  await api.sleep(2000);
  return api.evaluate(`(() => {
    const out = {};
    for (const e of document.querySelectorAll('input, select')) {
      const id = e.getAttribute('data-testid') || e.id || e.name;
      if (!id) continue;
      out[id] = e.type === 'password' ? (e.value ? '(set)' : '(empty)') : (e.value || '').slice(0, 60);
    }
    const t = (s) => { const e = document.querySelector(s); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 300) : null; };
    return { path: location.pathname + location.search, fields: out, body: t('.page-body-content') || t('main') };
  })()`);
};
