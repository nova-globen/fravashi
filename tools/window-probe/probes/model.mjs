// The composer's model picker: lists what it offers, and when PICK is set chooses the first option whose
// text contains PICK (case-insensitive). Reports the picker's label and the read-only "on <model>" label.
export default async (api) => {
  const read = () => api.evaluate(`(() => {
    const t = (e) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null);
    const q = (id) => document.querySelector('[data-testid=' + id + ']');
    return { trigger: t(q('model-picker-trigger')), actual: t(q('model-actual')), locked: t(q('model-locked')) };
  })()`);
  const before = await read();
  await api.evaluate(`document.querySelector('[data-testid=model-picker-trigger]').click()`);
  await api.sleep(600);
  const options = await api.evaluate(`[...document.querySelectorAll('[data-testid^=model-picker-option], [data-testid=model-picker] [role=option]')].map(e => e.innerText.replace(/[ \\t\\r\\n]+/g, ' ').trim())`);
  const pick = (process.env.PICK || '').toLowerCase();
  let picked = null;
  if (pick) {
    picked = await api.evaluate(`(() => {
      const o = [...document.querySelectorAll('[data-testid^=model-picker-option], [data-testid=model-picker] [role=option]')].find(e => e.innerText.toLowerCase().includes(${JSON.stringify(pick)}));
      if (!o) return null; o.click(); return o.innerText.replace(/[ \\t\\r\\n]+/g, ' ').trim();
    })()`);
  } else {
    await api.evaluate(`document.querySelector('[data-testid=model-picker-trigger]').click()`);
  }
  await api.sleep(1000);
  return { before, options, picked, after: await read() };
};
