export default async (api) => api.evaluate(`[...document.querySelectorAll('[data-testid=wiki-publication] button')].map(b => b.getAttribute('data-testid') + ': ' + b.innerText.trim())`);
