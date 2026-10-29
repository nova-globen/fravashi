// Backlog 178: adds acceptance criteria to work item N through the board's own Edit form, as a person would, and
// reports what the item's page then shows: its criteria and its startability verdict. CRITERIA=<text>|<text>|...
// SHOT=<file> saves a screenshot of the item afterwards.
export default async (api) => {
  const texts = (process.env.CRITERIA ?? '').split('|').map((t) => t.trim()).filter(Boolean);
  if (!texts.length) return { error: 'set CRITERIA' };

  const origin = await api.evaluate('location.origin');
  await api.goto(`${origin}/workitems/${process.env.N}`, 2000);
  if (!(await api.waitFor(`!!document.querySelector('[data-testid=work-item-edit]')`, 15000))) return { error: 'no item page' };
  const read = () => api.evaluate(`({
    criteria: [...document.querySelectorAll('[data-testid=work-item-criterion]')].map(e => e.innerText.trim()),
    verdict: document.querySelector('[data-testid=work-item-verdict]')?.innerText.replace(/\\s+/g, ' ').trim() ?? null,
    version: document.querySelector('[data-testid=work-item-version]')?.innerText.trim(),
  })`);
  const before = await read();

  await api.evaluate(`document.querySelector('[data-testid=work-item-edit]').click()`);
  if (!(await api.waitFor(`!!document.querySelector('[data-testid=work-item-criterion-add]')`, 10000))) return { error: 'no criteria field on the form', before };
  for (const text of texts) {
    const count = await api.evaluate(`document.querySelectorAll('[data-testid=work-item-criterion-input]').length`);
    await api.evaluate(`document.querySelector('[data-testid=work-item-criterion-add]').click()`);
    await api.waitFor(`document.querySelectorAll('[data-testid=work-item-criterion-input]').length > ${count}`, 5000);
    await api.evaluate(`(() => {
      const inputs = document.querySelectorAll('[data-testid=work-item-criterion-input]');
      const input = inputs[inputs.length - 1];
      input.value = ${JSON.stringify(text)};
      input.dispatchEvent(new Event('input', { bubbles: true }));
    })()`);
    await api.sleep(300);
  }

  await api.evaluate(`document.querySelector('[data-testid=work-item-save]').click()`);
  const closed = await api.waitFor(`!!document.querySelector('[data-testid=work-item-detail]') && !document.querySelector('[data-testid=work-item-form]')`, 10000);
  await api.sleep(800);
  const after = await read();
  const errors = closed ? [] : await api.evaluate(`[...document.querySelectorAll('[data-testid=work-item-criteria-error], [data-testid$=-error]')].map(e => e.innerText.trim())`);
  const result = { before, after, closed, errors };
  if (process.env.SHOT) result.shot = await api.shot(process.env.SHOT);
  return result;
};
