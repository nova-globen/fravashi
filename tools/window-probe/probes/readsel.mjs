// Observe only: the text and the data-testids inside the first element matching SEL (a CSS selector).
export default async (api) => {
  await api.sleep(500);
  return api.evaluate(`(() => {
    const el = document.querySelector(${JSON.stringify(process.env.SEL || 'main')});
    if (!el) return { found: false };
    return {
      found: true,
      text: el.innerText.replace(/[ \\t\\r\\n]+/g, ' ').slice(0, 1500),
      ids: [...el.querySelectorAll('[data-testid]')].map(e => e.getAttribute('data-testid')),
    };
  })()`);
};
