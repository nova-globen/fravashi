// Backlog 171: the text and background colour of every element matching SEL (default a.studio-button), and their
// WCAG contrast ratio, under the theme the window is in (THEME=light|dark switches it first, through the same
// data-theme attribute the theme toggle sets). SHOT=<file> saves a screenshot.
export default async (api) => {
  const sel = process.env.SEL || 'a.studio-button';
  if (process.env.THEME) {
    await api.evaluate(`document.documentElement.setAttribute('data-theme', ${JSON.stringify(process.env.THEME)})`);
    await api.sleep(400);
  }

  const rows = await api.evaluate(`(() => {
    const rgb = (s) => (s.match(/[\\d.]+/g) || []).slice(0, 3).map(Number);
    const lum = ([r, g, b]) => {
      const f = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
      return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
    };
    return [...document.querySelectorAll(${JSON.stringify(sel)})].map(e => {
      const cs = getComputedStyle(e);
      const [a, b] = [lum(rgb(cs.color)), lum(rgb(cs.backgroundColor))].sort((x, y) => y - x);
      return { text: e.innerText.trim(), color: cs.color, background: cs.backgroundColor, ratio: Math.round(((a + 0.05) / (b + 0.05)) * 100) / 100 };
    });
  })()`);
  const theme = await api.evaluate(`document.documentElement.getAttribute('data-theme')`);
  const result = { path: await api.evaluate('location.pathname'), theme, rows };
  if (process.env.SHOT) result.shot = await api.shot(process.env.SHOT);
  return result;
};
