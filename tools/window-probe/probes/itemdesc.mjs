// Replaces a work item's WHOLE description through the item's own Edit and Save, as a person would: for an item
// whose description is wrong rather than incomplete (itemnote.mjs appends). N=<number>; DESC_FILE=<file> (markdown).
// The version must move, or the save did not happen and the probe says so.
import { readFileSync } from 'node:fs';

export default async (api) => {
  const text = process.env.DESC_FILE ? readFileSync(process.env.DESC_FILE, 'utf8').trim() : '';
  if (!text) return { error: 'set N and DESC_FILE' };

  const n = process.env.N;
  if (n && !(await api.evaluate('location.pathname')).endsWith('/workitems/' + n)) {
    await api.goto(new URL('/workitems/' + n, await api.evaluate('location.origin')).toString(), 3000);
  }
  if (!(await api.waitFor(`!!document.querySelector('[data-testid=work-item-edit]')`, 15000))) {
    return { error: 'no work item on ' + (await api.evaluate('location.pathname')) };
  }

  const version = () => api.evaluate(`document.querySelector('[data-testid=work-item-version]')?.innerText.trim() ?? null`);
  const before = await version();
  await api.evaluate(`document.querySelector('[data-testid=work-item-edit]').click()`);
  if (!(await api.waitFor(`!!document.querySelector('[data-testid=work-item-description]')`, 10000))) {
    return { error: 'the edit form did not open', before };
  }

  await api.evaluate(`(() => {
    const t = document.querySelector('[data-testid=work-item-description]');
    t.value = ${JSON.stringify(text)};
    t.dispatchEvent(new Event('input', { bubbles: true }));
  })()`);
  await api.sleep(400);
  await api.evaluate(`document.querySelector('[data-testid=work-item-save]').click()`);
  if (!(await api.waitFor(`!!document.querySelector('[data-testid=work-item-detail]') && !document.querySelector('[data-testid=work-item-form]')`, 10000))) {
    return { error: 'the save did not close the form', before };
  }

  await api.sleep(500);
  const after = await version();
  const shown = await api.evaluate(`(document.querySelector('[data-testid=work-item-description-text]')?.innerText ?? '')`);
  return { saved: before !== after, before, after, matches: shown.trim().length > 0, tail: shown.slice(-160) };
};
