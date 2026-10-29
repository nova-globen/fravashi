// The provider-sync page's conflict section in full: which field, which two values, and the choices offered.
export default async (api) => {
  await api.sleep(1200);
  return api.evaluate(`(() => {
    const t = (e, n) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, n || 500) : null);
    const section = document.querySelector('[data-testid=sync-conflicts]');
    return {
      path: location.pathname,
      conflicts: t(section, 1500),
      rows: [...document.querySelectorAll('[data-testid^=sync-conflict]')].map(e => e.dataset.testid + ': ' + t(e, 220)),
    };
  })()`);
};
