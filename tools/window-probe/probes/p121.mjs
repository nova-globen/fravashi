// Item 121 in the installed window: Settings > Environment > Project.
// ACT=read (default)      what the section says.
// ACT=create NAME=<name>  "create and move", then what it says.
// ACT=move NAME=<name>    picks the project with that name from the list and moves, then what it says.
// ACT=titles              the page-bar title of whatever page is open (does the page say whose it is?).
export default async (api) => {
  const act = process.env.ACT || 'read';
  if (act === 'titles') {
    await api.sleep(1500);
    return api.evaluate(`({ path: location.pathname, bar: (document.querySelector('.page-bar-title, [data-testid=page-bar-title], .page-bar') || {}).innerText || null, title: document.title })`);
  }
  await api.evaluate(`(() => { const a = document.querySelector('[data-testid=settings-nav-project]'); if (a && !document.querySelector('[data-testid=project-settings]')) a.click(); })()`);
  await api.waitFor(`!!document.querySelector('[data-testid=project-settings]')`, 8000);
  const read = () => api.evaluate(`(() => {
    const t = (id) => { const e = document.querySelector('[data-testid=' + id + ']'); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null; };
    const pick = document.querySelector('[data-testid=project-pick]');
    return { path: location.pathname, folder: t('project-folder'), current: t('project-current'), noFolder: t('project-no-folder'),
      options: pick ? [...pick.options].map(o => o.text) : null, refusal: t('project-refusal'), said: t('project-said') };
  })()`);
  if (act === 'read') return read();
  const name = process.env.NAME || '';
  if (act === 'create') {
    await api.evaluate(`(() => { const i = document.querySelector('[data-testid=project-new-name]'); i.value = ${JSON.stringify(name)}; i.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    await api.sleep(600);
    await api.evaluate(`document.querySelector('[data-testid=project-create]').click()`);
  } else {
    await api.evaluate(`(() => { const s = document.querySelector('[data-testid=project-pick]'); const o = [...s.options].find(x => x.text === ${JSON.stringify(name)}); if (o) { s.value = o.value; s.dispatchEvent(new Event('change', { bubbles: true })); } })()`);
    await api.sleep(600);
    await api.evaluate(`document.querySelector('[data-testid=project-move]').click()`);
  }
  await api.sleep(1500);
  return read();
};
