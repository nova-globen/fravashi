// Proves a studio.css rule in the installed window before a release: screenshot BEFORE, append the rule CSS in a style
// element, screenshot AFTER. Scrolls the element with data-testid TESTID into view first.
export default async (api) => {
  await api.waitFor(`!!document.querySelector('[data-testid=${process.env.TESTID}]')`, 10000);
  await api.evaluate(`document.querySelector('[data-testid=${process.env.TESTID}]').scrollIntoView({ block: 'start' })`);
  await api.sleep(600);
  await api.shot(process.env.BEFORE);
  const measure = `(() => { const e = document.querySelector('[data-testid=${process.env.TESTID}]'); return { height: Math.round(e.getBoundingClientRect().height), whiteSpace: getComputedStyle(e).whiteSpace }; })()`;
  const before = await api.evaluate(measure);
  await api.evaluate(`(() => { const s = document.createElement('style'); s.textContent = ${JSON.stringify(process.env.CSS)}; document.head.appendChild(s); })()`);
  await api.sleep(600);
  await api.evaluate(`document.querySelector('[data-testid=${process.env.TESTID}]').scrollIntoView({ block: 'start' })`);
  await api.sleep(400);
  await api.shot(process.env.AFTER);
  return { before, after: await api.evaluate(measure) };
};
