// Creates a wiki document through the editor: TITLE and BODY. Run with the window on /wiki. Returns the path it lands on.
// With EDIT=1 it edits the OPEN document's body to BODY instead.
export default async (api) => {
  const set = (id, value) => api.evaluate(`(() => { const e = document.querySelector('[data-testid=${id}]'); e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('input', { bubbles: true })); })()`);
  await api.evaluate(`document.querySelector('[data-testid=${process.env.EDIT ? 'wiki-edit' : 'new-wiki-item'}]').click()`);
  await api.waitFor(`!!document.querySelector('[data-testid=wiki-editor]')`, 8000);
  if (!process.env.EDIT) await set('wiki-title', process.env.TITLE);
  await set('wiki-body-input', process.env.BODY);
  await api.sleep(500);
  await api.evaluate(`document.querySelector('[data-testid=wiki-save]').click()`);
  await api.waitFor(`!document.querySelector('[data-testid=wiki-editor]')`, 8000);
  await api.sleep(1500);
  return api.evaluate('location.pathname');
};
