// Every document the wiki library lists (type, title, @number), written as JSON to OUT. Run with the window on /wiki.
import { writeFileSync } from 'node:fs';

export default async (api) => {
  await api.waitFor(`document.querySelectorAll('[data-testid=wiki-entry]').length > 0`, 10000);
  await api.sleep(800);
  const rows = await api.evaluate(`[...document.querySelectorAll('[data-testid=wiki-entry]')].map(e => ({
    type: e.querySelector('.wiki-entry-type').innerText.trim(), title: e.querySelector('.wiki-entry-title').innerText.trim(),
    number: Number(e.querySelector('.wiki-entry-number').innerText.trim().slice(1)) }))`);
  writeFileSync(process.env.OUT, JSON.stringify(rows, null, 1));
  return { rows: rows.length, out: process.env.OUT };
};
