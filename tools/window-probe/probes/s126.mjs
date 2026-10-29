// Items 126 to 129 in the installed window: what the Sessions page and the workspace menu actually render.
export default async (api) => {
  await api.sleep(1500);
  return api.evaluate(`(() => {
    const t = (e) => e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 220) : null;
    const q = (id) => document.querySelector('[data-testid=' + id + ']');
    const ids = [...document.querySelectorAll('[data-testid]')].map(e => e.getAttribute('data-testid'));
    return {
      path: location.pathname,
      modelActual: t(q('model-actual')),
      workToggle: t(q('session-work-toggle')),
      workSummary: t(q('session-work-summary')),
      pinnedRow: ids.filter(i => /-pinned$/.test(i)),
      sessionIds: [...new Set(ids.filter(i => /^(session-work|model|composer)/.test(i)))].slice(0, 20),
    };
  })()`);
};
