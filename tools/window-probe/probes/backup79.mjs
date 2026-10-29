// Item 79 in the installed window: Settings > Environment > Backup. Presses "write a backup" and records what
// the page says. Navigate to /settings/backup first (nav.mjs with NAV='^/settings', then the
// settings-nav-backup link, or GOTO below).
export default async (api) => {
  if (process.env.GOTO) {
    await api.evaluate(`(() => { const a = document.querySelector('[data-testid=settings-nav-backup]'); if (a) a.click(); })()`);
    await api.sleep(2500);
  }
  const read = () => api.evaluate(`(() => {
    const t = (id) => { const e = document.querySelector('[data-testid=' + id + ']'); return e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 600) : null; };
    const b = document.querySelector('[data-testid=backup-write]');
    return { path: location.pathname, folder: t('backup-folder'), excluded: t('backup-excluded'),
      button: b ? { label: b.innerText.trim(), disabled: b.disabled } : null,
      result: t('backup-result'), file: t('backup-path'), error: t('backup-error') };
  })()`);
  const before = await read();
  if (!before.button || before.button.disabled) return { before, pressed: false };
  await api.evaluate(`document.querySelector('[data-testid=backup-write]').click()`);
  let last = before;
  for (let i = 0; i < 120; i++) {
    await api.sleep(1000);
    last = await read();
    if (last.result || last.error) break;
  }
  return { before, pressed: true, after: last };
};
