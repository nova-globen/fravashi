export default async (api) => { await api.sleep(1500); return api.evaluate(`(document.querySelector('[data-testid=sync-body]') || document.body).innerText.slice(0, 2500)`); };
