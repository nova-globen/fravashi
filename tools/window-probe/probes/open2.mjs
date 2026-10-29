// Click the recent-folder row named exactly FOLDER (default agent-session-proof). Click only; observe in the next probe.
// Work item #350: the recents live in the start dialog now (shown in place of the studio while a window has no folder,
// and over it once the title bar's workspace button is clicked), one [data-testid=start-recent] button per folder.
export default async (api) => api.evaluate(`(() => {
  const rows = [...document.querySelectorAll('[data-testid=start-recent]')];
  if (rows.length === 0) return 'no start dialog recents (with a folder open, click the title bar workspace button first)';
  const name = (row) => (row.querySelector('.welcome-recent-name') || row).innerText.trim();
  const row = rows.find(r => name(r) === ${JSON.stringify(process.env.FOLDER || 'agent-session-proof')});
  if (!row) return 'no row among: ' + rows.map(name).join(' | ');
  setTimeout(() => row.click(), 50);
  return 'clicking ' + row.tagName + ': ' + row.innerText.replace(/\\s+/g, ' ').slice(0, 90);
})()`);
