// Reads one work item whole: how a session on the HOST reads work since backlog 49's cut-over (2026-09-30), when the
// backlog file became the Fravashi project's tracker and a host session has no other way in (docs/v2.md section 7).
// N=<number> opens /workitems/N first; OUT=<file> writes the item as JSON there and returns a summary (a long description
// survives a file, and the one-line output of probe.ps1 flattens it). Run it on the window of a folder in the project.
import { writeFileSync } from 'node:fs';

export default async (api) => {
  const n = process.env.N;
  if (n && !(await api.evaluate('location.pathname')).endsWith('/workitems/' + n)) {
    await api.goto(new URL('/workitems/' + n, await api.evaluate('location.origin')).toString(), 3000);
  }

  if (!(await api.waitFor(`!!document.querySelector('[data-testid=work-item-detail]')`, 15000))) {
    return { error: 'no work item on ' + (await api.evaluate('location.pathname')) };
  }

  await api.sleep(500);
  const item = await api.evaluate(`(() => {
    const one = (id) => document.querySelector('[data-testid=' + id + ']')?.innerText.trim() ?? null;
    const all = (id) => [...document.querySelectorAll('[data-testid=' + id + ']')].map(e => e.innerText.replace(/[ \\t]+/g, ' ').trim());
    return {
      heading: document.querySelector('.work-item-detail-heading')?.innerText.replace(/\\s+/g, ' ').trim() ?? null,
      status: one('work-item-status'),
      version: one('work-item-version'),
      verdict: one('work-item-verdict'),
      description: one('work-item-description-text'),
      criteria: all('work-item-criterion'),
      links: all('work-item-link'),
      blockers: all('work-item-person-blocker'),
      evidence: all('work-item-evidence'),
    };
  })()`);

  if (process.env.OUT) {
    writeFileSync(process.env.OUT, JSON.stringify(item, null, 1));
    return { heading: item.heading, status: item.status, version: item.version, out: process.env.OUT };
  }

  return item;
};
