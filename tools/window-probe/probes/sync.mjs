// Item 12: press Sync now in the installed build and read what the card says afterwards.
export default async (api) => {
  const read = () => api.evaluate(`(() => document.body.innerText.split('\\n').map(l => l.trim())
    .filter(l => /page\\(s\\)|synced|syncing|sync failed|confluence|fetch/i.test(l)).slice(0, 8))()`);
  const before = await read();
  const clicked = await api.evaluate(`(() => {
    const b = [...document.querySelectorAll('button')].find(x => x.innerText.trim() === 'Sync now');
    if (!b) return false; b.scrollIntoView({ block: 'center' }); b.click(); return true;
  })()`);
  const seen = [];
  for (let i = 0; i < 18; i++) {
    await api.sleep(1000);
    const now = JSON.stringify(await read());
    if (seen[seen.length - 1] !== now) seen.push(now);
  }
  return { before, clicked, progression: seen.map((s) => JSON.parse(s)) };
};
