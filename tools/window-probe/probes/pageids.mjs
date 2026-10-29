// Every data-testid on the page, in document order, with a short text for each; MATCH=<fragment> narrows to ids that
// contain it. For finding what a page really renders before a probe clicks anything on it.
export default async (api) => {
  await api.sleep(1500);
  return api.evaluate(`(() => {
    const match = ${JSON.stringify(process.env.MATCH || '')};
    return [...document.querySelectorAll('[data-testid]')]
      .filter(e => !match || e.getAttribute('data-testid').includes(match))
      .map(e => e.getAttribute('data-testid') + ' | ' + e.tagName.toLowerCase() + (e.disabled ? ' disabled' : '') + ' | ' + (e.innerText || e.value || '').replace(/\\s+/g, ' ').trim().slice(0, 70));
  })()`);
};
