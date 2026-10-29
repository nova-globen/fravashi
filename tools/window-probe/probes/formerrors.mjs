// Opens work item N's Edit form, reports the description's length and any validation text the form shows after a
// Save with the text unchanged, then cancels. For finding out why a save through the form did not take.
export default async (api) => {
  const origin = await api.evaluate('location.origin');
  await api.goto(`${origin}/workitems/${process.env.N}`, 1500);
  await api.waitFor(`!!document.querySelector('[data-testid=work-item-edit]')`, 15000);
  await api.evaluate(`document.querySelector('[data-testid=work-item-edit]').click()`);
  await api.waitFor(`!!document.querySelector('[data-testid=work-item-description]')`, 10000);
  const length = await api.evaluate(`document.querySelector('[data-testid=work-item-description]').value.length`);
  const title = await api.evaluate(`document.querySelector('[data-testid=work-item-title]').value.length`);
  await api.evaluate(`document.querySelector('[data-testid=work-item-save]').click()`);
  await api.sleep(2000);
  const errors = await api.evaluate(`[...document.querySelectorAll('[data-testid$=-error], [data-testid=work-item-form-errors], [data-testid=board-error]')].map(e => e.innerText.trim()).filter(Boolean)`);
  const stillOpen = await api.evaluate(`!!document.querySelector('[data-testid=work-item-form]')`);
  if (stillOpen) await api.evaluate(`document.querySelector('[data-testid=work-item-cancel]')?.click()`);
  return { n: process.env.N, descriptionLength: length, titleLength: title, stillOpen, errors };
};
