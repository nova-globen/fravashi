// Sets several inputs and selects by data-testid in one go, from FIELDS: a JSON object {"testid": "value", ...}. Each
// gets its value through the native setter, then input and change events (what Blazor's bindings listen for), one
// after another with a short pause so each re-render settles. Reports what it set and what it could not find.
// Set only; press any button in the next probe.
export default async (api) => api.evaluate(`(async () => {
  const fields = ${process.env.FIELDS || '{}'};
  const done = [], missing = [];
  for (const [id, value] of Object.entries(fields)) {
    const el = document.querySelector('[data-testid="' + id + '"]');
    if (!el) { missing.push(id); continue; }
    const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value').set;
    setter.call(el, String(value));
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    done.push(id + '=' + el.value);
    await new Promise(r => setTimeout(r, 250));
  }
  return { done, missing };
})()`);
