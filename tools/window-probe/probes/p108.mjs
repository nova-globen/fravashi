// Item 108 in the installed window, on /onboarding/runtimes?mode=settings: what the panel of a runtime says
// about keys under the credential broker. ACT=read (default), or ACT=remove to press "Remove the stored key".
// TOOL is the runtime as its test ids spell it (default Claude). No key value is ever read: the panel does not
// show one, and this reads sentences and whether controls exist.
export default async (api) => {
  const tool = process.env.TOOL || 'Claude';
  await api.sleep(1200);
  const read = () => api.evaluate(`(() => {
    const t = (id) => { const e = document.querySelector('[data-testid="' + id + '"]'); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null; };
    const has = (id) => !!document.querySelector('[data-testid="' + id + '"]');
    return { path: location.pathname, brokered: t('login-brokered-${tool}'), leftover: t('login-key-leftover-${tool}'),
      saysKeyAuthenticates: t('login-key-saved-${tool}'), keyField: has('login-apikey-${tool}'), saveButton: has('login-save-${tool}'),
      removeButton: has('login-clear-${tool}'), auth: t('runtime-auth-${tool}') };
  })()`);
  if ((process.env.ACT || 'read') === 'read') return read();
  const before = await read();
  await api.evaluate(`(() => { const b = document.querySelector('[data-testid="login-clear-${tool}"]'); if (b) b.click(); })()`);
  await api.sleep(2500);
  return { before, after: await read() };
};
