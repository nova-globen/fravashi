// The open wiki document's publication sentence and the link to its page on the site. Run with the window on /wiki/{n}.
export default async (api) => {
  await api.waitFor(`!!document.querySelector('[data-testid=wiki-publication]')`, 10000);
  return api.evaluate(`({
    state: (document.querySelector('[data-testid=wiki-publication-state]') || {}).innerText || null,
    url: (document.querySelector('[data-testid=wiki-publication-link]') || {}).href || null })`);
};
