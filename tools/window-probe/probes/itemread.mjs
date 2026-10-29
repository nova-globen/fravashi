// What a work item's page says: title bar, status, the links section, and every anchor in its description with
// where it points. Run with the window on /workitems/{n}. SHOT=<file> also saves a screenshot.
export default async (api) => {
  await api.waitFor(`!!document.querySelector('[data-testid=work-item-detail]')`, 10000);
  await api.sleep(800);
  const result = await api.evaluate(`(() => {
    const t = (id) => [...document.querySelectorAll('[data-testid=' + id + ']')].map(e => e.innerText.replace(/[ \\t\\r\\n]+/g, ' ').trim());
    const text = document.querySelector('[data-testid=work-item-description-text]');
    return {
      path: location.pathname, title: document.title, status: t('work-item-status'), links: t('work-item-link'),
      anchors: text ? [...text.querySelectorAll('a')].map(a => a.innerText.trim() + ' -> ' + (a.getAttribute('href') || '') + (a.className ? ' [' + a.className + ']' : '')) : [],
      atRefs: text ? (text.innerText.match(/@\\d+/g) || []) : [],
    };
  })()`);
  if (process.env.SHOT) result.shot = await api.shot(process.env.SHOT);
  return result;
};
