// Observe only: where is the window, and what does the top of the page say? Run AFTER a click-only probe.
export default async (api) => {
  await api.sleep(2500);
  return api.evaluate(`({
    path: location.pathname + location.search,
    title: document.title,
    host: document.documentElement.className,
    text: document.body.innerText.replace(/[ \\t\\r\\n]+/g, ' ').slice(0, 260),
  })`);
};
