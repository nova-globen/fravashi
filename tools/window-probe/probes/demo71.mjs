// Item 71: visits the same pages of one demo install and records each one as a screenshot and as its visible
// text. Run it against two INDEPENDENT demo installs (BASE=http://127.0.0.1:<port>, LABEL=a or b, DIR=<folder>)
// and compare: identical pages are what "screenshots are stable across runs" means, measured on rendered
// surfaces rather than on the data layer. Headless Edge carries the window's user-agent marker, which is the
// only thing an ad hoc container without a window token asks for.
import { createHash } from 'node:crypto';

export default async (api) => {
  const base = process.env.BASE;
  const label = process.env.LABEL;
  const dir = process.env.DIR;
  const pages = (process.env.PAGES || '/welcome,/workitems,/workitems/1,/wiki,/wiki/1,/sessions').split(',');
  await api.send('Emulation.setDeviceMetricsOverride', { width: 1424, height: 888, deviceScaleFactor: 1, mobile: false });
  const results = [];
  for (const path of pages) {
    await api.goto(base + path, 5000);
    // A demo page is only worth comparing once Blazor has rendered it: wait for the circuit's markup.
    await api.waitFor(`document.readyState === 'complete' && !!document.querySelector('.studio-shell, main, [data-testid]')`, 15000);
    await api.sleep(1500);
    const text = await api.evaluate(`(document.body.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim()`);
    const name = `${label}-${path.replace(/[^a-z0-9]+/gi, '_').replace(/^_|_$/g, '') || 'root'}.png`;
    await api.shot(`${dir}/${name}`);
    results.push({ path, shot: name, textHash: createHash('sha256').update(text).digest('hex').slice(0, 16), text: text.slice(0, 160) });
  }
  return results;
};
