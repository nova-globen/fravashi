export default async (api) => { await api.sleep(800); return api.evaluate(`document.querySelector('[data-testid=work-item-detail]').innerText.slice(0, 160)`); };
