// On /settings/project. ACTION is one of:
//   rename  - renames the folder's current project to NAME
//   create  - creates a project NAME and moves this folder into it
//   none    - sets the current project's board and wiki provider to None
//   preview - runs the import preview for this folder
//   import  - runs the import (after a preview)
// Reports the page's current project, its providers, and what the page said. One action per run: the move rebinds
// the page, so observe in the next run.
export default async (api) => {
  const action = process.env.ACTION || '';
  const set = (id, value) => api.evaluate(`(() => { const e = document.querySelector('[data-testid=${id}]'); if (!e) return false; e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
  const press = (id) => api.evaluate(`(() => { const e = document.querySelector('[data-testid=${id}]'); if (!e) return 'no ' + ${JSON.stringify(id)}; if (e.disabled) return ${JSON.stringify(id)} + ' is disabled'; e.click(); return 'pressed ' + ${JSON.stringify(id)}; })()`);
  let did = null;
  if (action === 'rename') { await set('project-rename-name', process.env.NAME); await api.sleep(300); did = await press('project-rename'); }
  if (action === 'create') { await set('project-new-name', process.env.NAME); await api.sleep(300); did = await press('project-create'); }
  if (action === 'none') {
    await api.evaluate(`(() => { for (const id of ['project-board-provider', 'project-wiki-provider']) { const s = document.querySelector('[data-testid=' + id + ']'); s.value = 'none'; s.dispatchEvent(new Event('change', { bubbles: true })); } })()`);
    did = 'set both to none';
  }
  // DOCS=off leaves the documents in the folder and brings the work items only (backlog 49's cut-over, 2026-09-30).
  if ((action === 'preview' || action === 'import') && process.env.DOCS === 'off') {
    await api.evaluate(`(() => { const c = document.querySelector('[data-testid=import-documents]'); if (c && c.checked) c.click(); })()`);
    await api.sleep(500);
  }
  if (action === 'preview') { did = await press('import-preview'); }
  if (action === 'import') { did = await press('import-run'); }
  await api.sleep(action === 'import' || action === 'preview' ? 6000 : 1500);
  return api.evaluate(`(() => {
    const t = (e) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null);
    const q = (id) => document.querySelector('[data-testid=' + id + ']');
    return {
      did: ${JSON.stringify(did)},
      project: t(q('project-current')),
      board: q('project-board-provider')?.value, wiki: q('project-wiki-provider')?.value,
      said: t(q('project-said')), refusal: t(q('project-refusal')),
      importSummary: t(q('import-summary')), importDone: t(q('import-done')), importRefusal: t(q('import-refusal')),
      importProblems: t(q('import-problems'))?.slice(0, 400),
    };
  })()`);
};
