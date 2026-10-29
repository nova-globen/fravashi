// Navigates the window to URL (a studio path, e.g. /onboarding/board?mode=settings) through the app's own
// router when it can, because a top-level navigation re-imposes the window's ?ws= and loses the circuit.
export default async (api) => {
  const url = process.env.URL;
  const how = await api.evaluate(`(() => {
    const a = [...document.querySelectorAll('a[href]')].find(x => x.getAttribute('href') === ${JSON.stringify(url)});
    if (a) { setTimeout(() => a.click(), 50); return 'in-app link'; }
    return null;
  })()`);
  if (how) {
    await api.sleep(2500);
    return { how, path: await api.evaluate('location.pathname + location.search') };
  }
  // No link to it: a real navigation, which the window will re-stamp with its own workspace query.
  await api.goto(new URL(url, await api.evaluate('location.origin')).toString(), 3500);
  return { how: 'navigated', path: await api.evaluate('location.pathname + location.search') };
};
