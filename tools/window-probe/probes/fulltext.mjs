// The page's visible text, up to CHARS (default 2000), plus every input's test id and whether it has a value.
// A password field's value is never returned, only whether one is set.
export default async (api) => {
  await api.sleep(1500);
  const chars = Number(process.env.CHARS || 2000);
  return api.evaluate(`(() => {
    const inputs = [...document.querySelectorAll('input, select, textarea')].map(e => ({
      id: e.getAttribute('data-testid') || e.id || e.name || e.placeholder || e.tagName,
      value: e.type === 'password' ? (e.value ? '(set)' : '(empty)') : (e.value || '').slice(0, 60),
    }));
    return { path: location.pathname, inputs, text: document.body.innerText.replace(/[ \\t\\r\\n]+/g, ' ').slice(0, ${chars}) };
  })()`);
};
