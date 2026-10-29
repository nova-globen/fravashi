export default async (api) => { await api.sleep(1000); return api.evaluate(`document.title`); };
