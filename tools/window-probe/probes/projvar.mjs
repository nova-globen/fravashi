// Backlog 154. On /settings/project: adds the variable NAME with VALUE (SECRET=1 marks it secret)
// when NAME is given, and reports the rows, the refusal and the confirmation the page shows.
export default async (api) => {
  const set = (id, value) => api.evaluate(`(() => { const e = document.querySelector('[data-testid=${id}]'); if (!e) return false; e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
  if (process.env.REMOVE) {
    await api.evaluate(`(() => {
      const row = [...document.querySelectorAll('[data-testid=project-var-row]')].find(r => r.getAttribute('data-name') === ${JSON.stringify(process.env.REMOVE)});
      const button = row && row.nextElementSibling && row.nextElementSibling.querySelector('[data-testid=project-var-remove]');
      if (button) button.click();
    })()`);
    await api.sleep(800);
  }
  if (process.env.NAME) {
    await set('project-var-name', process.env.NAME);
    await set('project-var-input', process.env.VALUE || '');
    if (process.env.SECRET === '1') {
      await api.evaluate(`(() => { const c = document.querySelector('[data-testid=project-var-secret]'); if (c && !c.checked) { c.click(); } })()`);
    }
    await api.sleep(400);
    await api.evaluate(`document.querySelector('[data-testid=project-var-add]').click()`);
    await api.sleep(1000);
  }
  return api.evaluate(`(() => {
    const t = (e) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null);
    const rows = [...document.querySelectorAll('[data-testid=project-var-row]')].map(r => r.getAttribute('data-name') + ' = ' + t(r.nextElementSibling));
    return { path: location.pathname, rows, refusal: t(document.querySelector('[data-testid=project-refusal]')), said: t(document.querySelector('[data-testid=project-said]')), page: t(document.querySelector('[data-testid=project-settings]'))?.slice(0, 300) };
  })()`);
};
