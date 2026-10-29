// Backlog 175, the one-time cleanup of the items imported at backlog 49's cut-over: drops the Status, Blocked by and
// Blocks fields from each description's metadata lines through the item's own Edit and Save, with the same rule as
// BacklogMarkdown.WithoutTrackedFields (kept in step by hand: a change there is a change here). NUMBERS=<n,n,...> or
// FROM=<n> TO=<n>; OUT=<file> appends one JSON line per item. An item whose text has nothing to drop is left untouched.
import { appendFileSync } from 'node:fs';

const tracked = ['Status', 'Blocked by', 'Blocks'];

function withoutTrackedFields(body) {
  const lines = body.split('\n');
  const kept = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.startsWith('**Type** ') && !line.startsWith('**Blocked by** ')) { kept.push(line); continue; }
    const fields = [];
    const separator = /\s\|\s\*\*/g;
    let start = 0;
    let match;
    while ((match = separator.exec(line)) !== null) {
      fields.push(line.slice(start, match.index).trimEnd());
      start = match.index + match[0].length - 2;
    }
    fields.push(line.slice(start).trimEnd());
    const left = fields.filter((f) => !tracked.some((name) => f.startsWith('**' + name + '** ')));
    if (left.length > 0) kept.push(left.join(' | '));
    else if (i + 1 < lines.length && lines[i + 1].length === 0 && (kept.length === 0 || kept[kept.length - 1].length === 0)) i++;
  }
  return kept.join('\n');
}

export default async (api) => {
  const numbers = process.env.NUMBERS
    ? process.env.NUMBERS.split(',').map(Number)
    : Array.from({ length: Number(process.env.TO) - Number(process.env.FROM) + 1 }, (_, k) => Number(process.env.FROM) + k);
  const origin = await api.evaluate('location.origin');
  const summary = { changed: 0, unchanged: 0, failed: [] };
  const log = (entry) => { if (process.env.OUT) appendFileSync(process.env.OUT, JSON.stringify(entry) + '\n'); };

  for (const n of numbers) {
    await api.goto(`${origin}/workitems/${n}`, 1500);
    if (!(await api.waitFor(`!!document.querySelector('[data-testid=work-item-edit]')`, 15000))) {
      summary.failed.push(n); log({ n, error: 'no item page' }); continue;
    }

    const before = await api.evaluate(`document.querySelector('[data-testid=work-item-version]')?.innerText.trim()`);
    await api.evaluate(`document.querySelector('[data-testid=work-item-edit]').click()`);
    if (!(await api.waitFor(`!!document.querySelector('[data-testid=work-item-description]')`, 10000))) {
      summary.failed.push(n); log({ n, error: 'no form' }); continue;
    }

    const text = await api.evaluate(`document.querySelector('[data-testid=work-item-description]').value`);
    const fixed = withoutTrackedFields(text);
    if (fixed === text) {
      await api.evaluate(`document.querySelector('[data-testid=work-item-cancel]').click()`);
      summary.unchanged++; log({ n, unchanged: true }); continue;
    }

    await api.evaluate(`(() => {
      const t = document.querySelector('[data-testid=work-item-description]');
      t.value = ${JSON.stringify(fixed)};
      t.dispatchEvent(new Event('input', { bubbles: true }));
    })()`);
    await api.sleep(300);
    await api.evaluate(`document.querySelector('[data-testid=work-item-save]').click()`);
    const closed = await api.waitFor(`!!document.querySelector('[data-testid=work-item-detail]') && !document.querySelector('[data-testid=work-item-form]')`, 10000);
    await api.sleep(300);
    const after = await api.evaluate(`document.querySelector('[data-testid=work-item-version]')?.innerText.trim()`);
    if (!closed || after === before) { summary.failed.push(n); log({ n, error: 'not saved', before, after }); continue; }

    summary.changed++;
    log({ n, before, after, removed: text.length - fixed.length, head: fixed.split('\n')[0].slice(0, 80) });
  }

  return summary;
};
