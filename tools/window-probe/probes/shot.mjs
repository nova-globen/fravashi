export default async (api) => { await api.shot(process.env.SHOT); return { saved: process.env.SHOT }; };
