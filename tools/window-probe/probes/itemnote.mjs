// Adds a note to the END of a work item's description through the item's own Edit and Save, as a person would: how a
// session on the HOST records a proof or a finding since backlog 49's cut-over (docs/v2.md section 7). N=<number>;
// NOTE=<text> or NOTE_FILE=<file> (markdown, appended after a blank line). The description's existing text is kept
// byte for byte; the version must move, or the save did not happen and the probe says so.
import { readFileSync } from 'node:fs';

export default async (api) => {
  const note = process.env.NOTE_FILE ? readFileSync(process.env.NOTE_FILE, 'utf8').trim() : (process.env.NOTE ?? '').trim();
  if (!note) return { error: 'set NOTE or NOTE_FILE' };

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

  const appended = await api.evaluate(`(() => {
    const t = document.querySelector('[data-testid=work-item-description]');
    t.value = t.value.replace(/\\s+$/, '') + '\\n\\n' + ${JSON.stringify(note)};
    t.dispatchEvent(new Event('input', { bubbles: true }));
    return t.value.length;
  })()`);
  await api.sleep(400);
  await api.evaluate(`document.querySelector('[data-testid=work-item-save]').click()`);

  if (!(await api.waitFor(`!!document.querySelector('[data-testid=work-item-detail]') && !document.querySelector('[data-testid=work-item-form]')`, 10000))) {
    const errors = await api.evaluate(`[...document.querySelectorAll('[data-testid$=-error], [data-testid=work-item-form-errors]')].map(e => e.innerText.trim())`);
    return { error: 'the save did not close the form', errors, before };
  }

  await api.sleep(500);
  const after = await version();
  const tail = await api.evaluate(`(document.querySelector('[data-testid=work-item-description-text]')?.innerText ?? '').slice(-240)`);
  return { saved: before !== after, before, after, length: appended, tail };
};
