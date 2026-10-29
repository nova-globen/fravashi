export default async (api) => api.evaluate(`[...document.querySelectorAll('[data-testid=wiki-publish-all-refused] li')].map(l => l.innerText).slice(0, 8)`);
