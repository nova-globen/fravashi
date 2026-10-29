// Records evidence against one acceptance criterion of a work item through the item page's own Record evidence form
// (#349), as a person would: how a session on the HOST proves a criterion so the item can move to Done without a
// reason (#347). N=<number>; CRITERION=<1-based position in the item's list>; SUMMARY=<text> or SUMMARY_FILE=<file>;
// optional REFERENCE=<commit, docs/evidences path or test>; optional RESULT=fail (pass by default).
import { readFileSync } from 'node:fs';

export default async (api) => {
  const summary = process.env.SUMMARY_FILE ? readFileSync(process.env.SUMMARY_FILE, 'utf8').trim() : (process.env.SUMMARY ?? '').trim();
  const index = Number(process.env.CRITERION ?? '0') - 1;
  if (!summary || index < 0) return { error: 'set N, CRITERION (1-based) and SUMMARY or SUMMARY_FILE' };

  const n = process.env.N;
  if (n && !(await api.evaluate('location.pathname')).endsWith('/workitems/' + n)) {
    await api.goto(new URL('/workitems/' + n, await api.evaluate('location.origin')).toString(), 3000);
  }

  if (!(await api.waitFor(`document.querySelectorAll('[data-testid=work-item-criterion-row]').length > ${index}`, 15000))) {
    return { error: 'no criterion ' + (index + 1) + ' on ' + (await api.evaluate('location.pathname')) };
  }

  const row = `document.querySelectorAll('[data-testid=work-item-criterion-row]')[${index}]`;
  const clicked = await api.evaluate(`(() => { const b = ${row}.querySelector('[data-testid=work-item-criterion-record]'); if (!b) return false; b.click(); return true; })()`);
  if (!clicked) return { error: 'criterion ' + (index + 1) + ' has no Record evidence action' };
  if (!(await api.waitFor(`!!${row}.querySelector('[data-testid=work-item-evidence-form]')`, 10000))) {
    return { error: 'the evidence form did not open' };
  }

  await api.evaluate(`(() => {
    const form = ${row}.querySelector('[data-testid=work-item-evidence-form]');
    const set = (el, value, kind) => { el.value = value; el.dispatchEvent(new Event(kind, { bubbles: true })); };
    set(form.querySelector('[data-testid=work-item-evidence-result]'), ${JSON.stringify(process.env.RESULT === 'fail' ? 'Fail' : 'Pass')}, 'change');
    set(form.querySelector('[data-testid=work-item-evidence-summary]'), ${JSON.stringify(summary)}, 'input');
    set(form.querySelector('[data-testid=work-item-evidence-reference]'), ${JSON.stringify(process.env.REFERENCE ?? '')}, 'input');
  })()`);
  await api.sleep(400);
  await api.evaluate(`${row}.querySelector('[data-testid=work-item-evidence-save]').click()`);

  if (!(await api.waitFor(`!${row}.querySelector('[data-testid=work-item-evidence-form]') && !!${row}.querySelector('[data-testid=work-item-criterion-evidence]')`, 10000))) {
    const error = await api.evaluate(`${row}.querySelector('[data-testid=work-item-evidence-error]')?.innerText.trim() ?? null`);
    return { error: 'the evidence was not shown as saved', detail: error };
  }

  return {
    saved: true,
    criterion: await api.evaluate(`${row}.querySelector('[data-testid=work-item-criterion]')?.innerText.trim()`),
    chip: await api.evaluate(`${row}.querySelector('[data-testid=work-item-criterion-evidence]')?.innerText.trim()`),
  };
};
