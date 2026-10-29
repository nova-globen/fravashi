// Presses the button with data-testid TESTID and returns at once. Click only: the press may navigate, and a
// navigating click leaves the next CDP call unanswered, so observe in the next probe.
export default async (api) => api.evaluate(`(() => {
  const e = document.querySelector('[data-testid=${process.env.TESTID}]');
  if (!e) return 'no element with that testid';
  setTimeout(() => e.click(), 50);
  return 'pressing ' + (e.innerText || e.tagName).trim().slice(0, 60);
})()`);
