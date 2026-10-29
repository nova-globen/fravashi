// Types VALUE into the input whose data-testid is FILL (through the native setter, then input and change events, which
// is what Blazor's bindings listen for) and, when CLICK is set, presses that button a moment later. Click only;
// observe in the next probe.
export default async (api) => api.evaluate(`(() => {
  const input = document.querySelector('[data-testid="${process.env.FILL}"]');
  if (!input) return 'no input ${process.env.FILL}';
  const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(input), 'value').set;
  setter.call(input, ${JSON.stringify(process.env.VALUE || '')});
  input.dispatchEvent(new Event('input', { bubbles: true }));
  input.dispatchEvent(new Event('change', { bubbles: true }));
  const click = ${JSON.stringify(process.env.CLICK || '')};
  if (click) setTimeout(() => { const b = document.querySelector('[data-testid="' + click + '"]'); if (b) b.click(); }, 400);
  return 'filled ${process.env.FILL}' + (click ? ', pressing ' + click : '');
})()`);
