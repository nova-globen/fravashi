// Observe, then click once: on a page that renders agent-writable markdown, lists every link whose href is not http(s),
// mailto, a fragment or relative, and, when CLICK=1, clicks the first one with real mouse events and reports whether the
// page's title changed (the proof body sets document.title). Use only on a throwaway studio with a proof document.
export default async (api) => {
  const read = () => api.evaluate(`(() => ({
    title: document.title,
    unsafe: [...document.querySelectorAll('a[href], img[src]')]
      .map(e => e.getAttribute('href') ?? e.getAttribute('src'))
      .filter(u => /^[a-z][a-z0-9+.-]*:/i.test(u.trim()) && !/^(https?|mailto):/i.test(u.trim())),
  }))()`);
  const before = await read();
  if (process.env.CLICK !== '1' || before.unsafe.length === 0) return { before };
  await api.click('a[href^="javascript" i]');
  await api.sleep(1500);
  return { before, after: await read() };
};
