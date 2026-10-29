// Creates a work item through the board's own New form, as a person would: how a session on the HOST files new work
// since backlog 49's cut-over (docs/v2.md section 7). Run on /workitems. TITLE=<text>; DESC_FILE=<file> (markdown; the
// form has no acceptance field, so write the acceptance into the description); LEVEL=task|feature|increment (default
// task); STATUS=triage|backlog|ready (default backlog). Returns the new item's heading, which carries its number.
import { readFileSync } from 'node:fs';

export default async (api) => {
  const title = (process.env.TITLE ?? '').trim();
  const description = process.env.DESC_FILE ? readFileSync(process.env.DESC_FILE, 'utf8').trim() : '';
  if (!title) return { error: 'set TITLE' };

  if (!(await api.waitFor(`!!document.querySelector('[data-testid=new-work-item]')`, 15000))) {
    return { error: 'no New button on ' + (await api.evaluate('location.pathname')) };
  }

  await api.evaluate(`document.querySelector('[data-testid=new-work-item]').click()`);
  if (!(await api.waitFor(`!!document.querySelector('[data-testid=work-item-form]')`, 10000))) {
    return { error: 'the form did not open' };
  }

  await api.evaluate(`(() => {
    const set = (id, value, event) => {
      const e = document.querySelector('[data-testid=' + id + ']');
      e.value = value;
      e.dispatchEvent(new Event(event, { bubbles: true }));
    };
    set('work-item-title', ${JSON.stringify(title)}, 'input');
    set('work-item-description', ${JSON.stringify(description)}, 'input');
    set('work-item-level', ${JSON.stringify(process.env.LEVEL || 'task')}, 'change');
    set('work-item-status', ${JSON.stringify(process.env.STATUS || 'backlog')}, 'change');
  })()`);
  await api.sleep(500);
  await api.evaluate(`document.querySelector('[data-testid=work-item-save]').click()`);

  if (!(await api.waitFor(`!!document.querySelector('[data-testid=work-item-detail]') && !document.querySelector('[data-testid=work-item-form]')`, 10000))) {
    const errors = await api.evaluate(`[...document.querySelectorAll('[data-testid$=-error], [data-testid=work-item-form-errors]')].map(e => e.innerText.trim())`);
    return { error: 'the item was not created', errors };
  }

  await api.sleep(500);
  return api.evaluate(`({
    heading: document.querySelector('.work-item-detail-heading')?.innerText.replace(/\\s+/g, ' ').trim(),
    status: document.querySelector('[data-testid=work-item-status]')?.innerText.trim(),
  })`);
};
