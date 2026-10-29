// Observe the composer's model picker without a screenshot (a screenshot hangs on a window that is not on screen):
// whether its panel is open, and each unavailable or Fable/Opus 5 option with its disabled state and subtitle. With
// CLOSE=1 it closes an open panel first.
export default async (api) => api.evaluate(`(() => {
  const panel = () => document.querySelector('[data-testid=model-picker-panel]');
  if (${JSON.stringify(process.env.CLOSE || '')} === '1' && panel()) document.querySelector('[data-testid=model-picker-trigger]').click();
  const open = !!panel();
  if (!open) document.querySelector('[data-testid=model-picker-trigger]').click();
  const options = [...document.querySelectorAll('[data-testid^=model-picker-opt-]')].map(b => ({
    id: b.getAttribute('data-testid').replace('model-picker-opt-', ''),
    disabled: b.disabled,
    subtitle: (b.querySelector('.studio-dropdown-opt-subtitle') || {}).textContent || null }))
    .filter(o => o.disabled || /fable|opus-5/.test(o.id));
  if (!open) document.querySelector('[data-testid=model-picker-trigger]').click();
  return { wasOpen: open, options, openNow: !!panel() };
})()`);
