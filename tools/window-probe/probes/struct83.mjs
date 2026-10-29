// Item 83: did "Take the site's version" keep the page's STRUCTURE, or flatten it?
// Reads the rendered article's element tags, and the raw markdown out of the editor's textarea.
// ACT=read (default) tags only. ACT=raw also opens the editor, reads the markdown, and cancels.
export default async (api) => {
  const act = process.env.ACT || 'read';

  const tags = await api.evaluate(`(() => {
    const root = document.querySelector('[data-testid=wiki-article-body]');
    if (!root) return 'no article body';
    return [...root.querySelectorAll('*')].map(e => e.tagName.toLowerCase() + ': ' + (e.textContent || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 70));
  })()`);

  if (act === 'read') return { tags };

  await api.evaluate(`document.querySelector('[data-testid=wiki-edit]').click()`);
  if (!(await api.waitFor(`!!document.querySelector('[data-testid=wiki-body-input]')`, 8000))) return { tags, raw: 'the editor did not open' };
  const raw = await api.evaluate(`document.querySelector('[data-testid=wiki-body-input]').value`);
  const cancelled = await api.evaluate(`(() => { const b = document.querySelector('[data-testid=wiki-cancel]'); if (!b) return false; b.click(); return true; })()`);
  return { tags, cancelled, raw };
};
