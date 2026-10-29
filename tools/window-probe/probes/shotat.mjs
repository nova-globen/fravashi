// Scrolls the element with data-testid TESTID into the middle of the view, then saves a screenshot to SHOT.
// For evidence of a section that sits below the fold (a page screenshot only shows the viewport).
export default async (api) => {
  const found = await api.evaluate(`(() => {
    const e = document.querySelector('[data-testid=${process.env.TESTID}]');
    if (!e) return false;
    e.scrollIntoView({ block: 'center' });
    return true;
  })()`);
  await api.sleep(600);
  if (!found) return 'no element with that testid';
  return api.shot(process.env.SHOT);
};
