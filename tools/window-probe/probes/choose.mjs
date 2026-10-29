// Sets the select with data-testid TESTID to VALUE as a person would (a change event), then returns the text of WAITID.
export default async (api) => {
  const done = await api.evaluate(`(() => {
    const s = document.querySelector('[data-testid=${process.env.TESTID}]');
    if (!s) return false;
    s.value = ${JSON.stringify(process.env.VALUE)};
    s.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()`);
  if (!done) return { error: 'no select ' + process.env.TESTID };
  await api.sleep(1200);
  return api.evaluate(`(() => { const e = document.querySelector('[data-testid=${process.env.WAITID}]'); return e ? e.innerText.trim() : null; })()`);
};
