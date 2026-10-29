// Presses the element with data-testid TESTID, then waits (up to MS, default 60000) until the element with data-testid
// WAITID shows text different from what it showed before the press, and returns that text. For an act whose outcome is a
// sentence on the same page (Send everything, Sync now, Publish everything). INPUT=<text> first types into INPUTID.
export default async (api) => {
  const read = (id) => `(() => { const e = document.querySelector('[data-testid=${id}]'); return e ? e.innerText.replace(/[ \\t\\r\\n]+/g, ' ').trim() : null; })()`;
  const before = await api.evaluate(read(process.env.WAITID));
  if (process.env.INPUTID) {
    await api.evaluate(`(() => { const i = document.querySelector('[data-testid=${process.env.INPUTID}]'); i.value = ${JSON.stringify(process.env.INPUT || '')}; i.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    await api.sleep(500);
  }
  const pressed = await api.evaluate(`(() => { const e = document.querySelector('[data-testid=${process.env.TESTID}]'); if (!e) return false; e.click(); return true; })()`);
  if (!pressed) return { error: 'no element ' + process.env.TESTID };
  const ms = Number(process.env.MS || 60000);
  const changed = await api.waitFor(`(() => { const t = ${read(process.env.WAITID)}; return t !== null && t !== ${JSON.stringify(before)} && !/\\.\\.\\.$/.test(t); })()`, ms);
  return { changed, before, after: await api.evaluate(read(process.env.WAITID)) };
};
