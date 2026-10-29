// Item 67: presses the scratchpad's title-bar toggle once and returns whether the panel is open afterwards.
// Click-and-observe in one probe is safe here because the toggle does not navigate.
export default async (api) => {
  await api.evaluate(`document.querySelector('[data-testid=titlebar-scratchpad-toggle]').click()`);
  await api.sleep(1500);
  return api.evaluate(`(() => ({
    open: !!document.querySelector('[data-testid=scratchpad]'),
    pressed: document.querySelector('[data-testid=titlebar-scratchpad-toggle]')?.getAttribute('aria-pressed'),
    nextSibling: document.querySelector('[data-testid=titlebar-scratchpad-toggle]')?.nextElementSibling?.getAttribute('title'),
  }))()`);
};
