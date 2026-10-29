// The open wiki document's @number, title and backlinks. Run with the window on /wiki/{n}.
export default async (api) => {
  await api.waitFor(`!!document.querySelector('[data-testid=wiki-article]')`, 10000);
  await api.sleep(800);
  return api.evaluate(`({ path: location.pathname,
    heading: (document.querySelector('[data-testid=wiki-article] h1, [data-testid=wiki-article] h2') || {}).innerText || null,
    backlinks: [...document.querySelectorAll('[data-testid=wiki-backlink]')].map(b => b.innerText.trim()) })`);
};
