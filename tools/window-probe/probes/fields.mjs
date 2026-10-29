// Every element on the page that carries a value, including web components whose input lives in a shadow root.
// A password-like field is reported only as set or empty, never by value.
export default async (api) => {
  await api.sleep(1500);
  return api.evaluate(`(() => {
    const seen = [];
    const walk = (root) => {
      for (const e of root.querySelectorAll('*')) {
        if (e.shadowRoot) walk(e.shadowRoot);
        const v = e.value;
        if (typeof v !== 'string') continue;
        const id = e.getAttribute('data-testid') || e.id || e.name || e.getAttribute('placeholder') || e.tagName.toLowerCase();
        const secret = (e.type === 'password') || /token|key|secret|password/i.test(id);
        seen.push(id + ' = ' + (secret ? (v ? '(set)' : '(empty)') : JSON.stringify(v.slice(0, 60))));
      }
    };
    walk(document);
    return { path: location.pathname, fields: [...new Set(seen)] };
  })()`);
};
