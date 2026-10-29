// Item 83 in the installed window: the Publication section of the open wiki document.
// ACT=read (default)            what the panel says, which buttons it offers, and the start of the body.
// ACT=open   TITLE=<text>       opens the document in the list whose row contains TITLE. Click only.
// ACT=edit   LINE=<text>        Edit, append LINE to the body, Save. The field is set by value plus an
//                               input event, because the real window ignores synthetic select-all.
// ACT=<data-testid>             presses that button (wiki-publish, wiki-publication-check, wiki-take-site,
//                               wiki-overwrite-site, wiki-restore-replaced, wiki-publish-as-new) and records
//                               every distinct panel state for up to 25 seconds.
export default async (api) => {
  const act = process.env.ACT || 'read';
  const read = () => api.evaluate(`(() => {
    const t = (id, n) => { const e = document.querySelector('[data-testid=' + id + ']'); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, n || 500) : null; };
    const buttons = [...document.querySelectorAll('[data-testid=wiki-publication] button')].map(b => b.dataset.testid + (b.disabled ? ' (busy)' : ''));
    const link = document.querySelector('[data-testid=wiki-publication-link]');
    return { path: location.pathname, host: document.documentElement.className, title: t('wiki-article', 90),
      state: t('wiki-publication-state'), refusal: t('wiki-publication-refusal'), losses: t('wiki-publication-losses'),
      buttons, link: link ? link.href : null, body: t('wiki-article-body', 900), error: t('wiki-error') };
  })()`);

  if (act === 'read') return read();

  if (act === 'open') {
    const title = process.env.TITLE || '';
    return api.evaluate(`(() => {
      const e = [...document.querySelectorAll('[data-testid=wiki-entry]')].find(x => (x.innerText || '').includes(${JSON.stringify(title)}));
      if (!e) return 'no entry with that title';
      setTimeout(() => e.click(), 50);
      return 'clicking: ' + (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 90);
    })()`);
  }

  if (act === 'edit') {
    const line = process.env.LINE || 'A line added here.';
    await api.evaluate(`document.querySelector('[data-testid=wiki-edit]').click()`);
    if (!(await api.waitFor(`!!document.querySelector('[data-testid=wiki-body-input]')`, 8000))) return 'the editor did not open';
    await api.evaluate(`(() => {
      const a = document.querySelector('[data-testid=wiki-body-input]');
      a.value = a.value.replace(/\\s+$/, '') + '\\n\\n' + ${JSON.stringify(line)} + '\\n';
      a.dispatchEvent(new Event('input', { bubbles: true }));
    })()`);
    await api.sleep(600);
    await api.evaluate(`document.querySelector('[data-testid=wiki-save]').click()`);
    await api.waitFor(`!document.querySelector('[data-testid=wiki-editor]')`, 10000);
    await api.sleep(800);
    return read();
  }

  const before = await read();
  const pressed = await api.evaluate(`(() => { const b = document.querySelector('[data-testid=${act}]'); if (!b || b.disabled) return false; b.click(); return true; })()`);
  if (!pressed) return { before, pressed: false };
  const seen = [];
  let quiet = 0;
  for (let i = 0; i < 50 && quiet < 8; i++) {
    await api.sleep(500);
    const s = JSON.stringify(await read());
    if (seen[seen.length - 1] !== s) { seen.push(s); quiet = 0; } else if (!s.includes('(busy)')) quiet++;
  }
  return { before, pressed: true, progression: seen.map((s) => JSON.parse(s)) };
};
