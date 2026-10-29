// On /wiki: create a document through the page's own editor. TITLE and BODY come from the environment.
// Fields are set by value plus an input event, because the real window ignores synthetic select-all.
export default async (api) => {
  const title = process.env.TITLE, body = process.env.BODY || '';
  if (!title) return 'TITLE is required';
  await api.evaluate(`document.querySelector('[data-testid=new-wiki-item]').click()`);
  if (!(await api.waitFor(`!!document.querySelector('[data-testid=wiki-title]')`, 8000))) return 'the editor did not open';
  await api.evaluate(`(() => {
    const set = (id, v) => { const e = document.querySelector('[data-testid=' + id + ']'); e.value = v; e.dispatchEvent(new Event('input', { bubbles: true })); };
    set('wiki-title', ${JSON.stringify(title)});
    set('wiki-body-input', ${JSON.stringify(body)});
  })()`);
  await api.sleep(600);
  await api.evaluate(`document.querySelector('[data-testid=wiki-save]').click()`);
  await api.waitFor(`!document.querySelector('[data-testid=wiki-editor]')`, 10000);
  await api.sleep(800);
  return api.evaluate(`({ path: location.pathname, entries: [...document.querySelectorAll('[data-testid=wiki-entry]')].map(e => e.innerText.replace(/[ \\t\\r\\n]+/g, ' ').trim()),
    error: (document.querySelector('[data-testid=wiki-error]') || {}).innerText || null })`);
};
