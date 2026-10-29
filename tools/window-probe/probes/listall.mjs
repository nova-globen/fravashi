// Every work item the list view shows, with every status ticked, written as JSON to OUT (number, title, level, status).
// Run with the window on /workitems. For checking an import against its source file row by row.
import { writeFileSync } from 'node:fs';

export default async (api) => {
  await api.waitFor(`!!document.querySelector('[data-testid=board-list]')`, 10000);
  await api.evaluate(`[...document.querySelectorAll('input[data-testid^=board-status-]')].filter(b => !b.checked).forEach(b => b.click())`);
  await api.sleep(1500);
  const rows = await api.evaluate(`[...document.querySelectorAll('[data-testid=board-list-row]')].map(r => {
    const c = [...r.children].map(td => td.innerText.trim());
    return { number: Number(c[0].replace('#', '')), title: c[1], level: c[2], status: c[3] };
  })`);
  writeFileSync(process.env.OUT, JSON.stringify(rows, null, 1));
  return { rows: rows.length, out: process.env.OUT };
};
