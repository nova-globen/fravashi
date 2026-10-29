// Observe only. Installs a securitypolicyviolation recorder on every new document of this target, then loads each
// path in PATHS (comma-separated) with a full navigation, waits SETTLE ms (default 5000), and reports per path: the
// final location, the first heading, and every Content-Security-Policy violation the page raised (directive, blocked
// URI, source and line). For #287: a page that renders and raises nothing is what passing looks like.
// The recorder sees only the top document, so the browser's own log is read too. A sandboxed frame (the prototype
// viewer's srcdoc frame) runs in its OWN process and logs to its own target, which the page's log never sees, so every
// frame is auto-attached as it is created, held until its log is on, and its entries are counted with the page's.
// CONTROL=1 ends the walk with a positive control: it injects an inline script into the last page and reports whether
// both the recorder and the log saw it refused. A walk with no findings means nothing unless the control fired.
export default async (api) => {
  const base = process.env.BASE || new URL(api.url).origin;
  const paths = (process.env.PATHS || '/').split(',').map((p) => p.trim()).filter(Boolean);
  const settle = Number(process.env.SETTLE || 5000);
  await api.send('Log.enable');
  const attached = new Set();
  const pump = setInterval(() => {
    for (const e of api.events) {
      if (e.method !== 'Target.attachedToTarget' || attached.has(e.params.sessionId)) continue;
      const session = e.params.sessionId;
      attached.add(session);
      api.send('Log.enable', {}, session).catch(() => {})
        .then(() => api.send('Runtime.runIfWaitingForDebugger', {}, session).catch(() => {}));
    }
  }, 50);
  await api.send('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: true, flatten: true });
  await api.send('Page.addScriptToEvaluateOnNewDocument', {
    source: `window.__cspv = []; document.addEventListener('securitypolicyviolation', function (e) {
      window.__cspv.push([e.violatedDirective, e.blockedURI, (e.sourceFile || '') + ':' + e.lineNumber, (e.sample || '').slice(0, 60)].join(' | '));
    }, true);`,
  });
  const logged = () => api.events
    .filter((e) => e.method === 'Log.entryAdded' && /Content.Security.Policy|Refused to/i.test(e.params.entry.text))
    .map((e) => e.params.entry.text.slice(0, 220));
  // Enabling a log REPLAYS the entries its target already holds (a frame still open from an earlier run included), so
  // let those land before the first baseline is taken, or they are charged to the first path.
  await api.sleep(1000);
  const out = [];
  for (const path of paths) {
    const before = logged().length, attachedBefore = attached.size;
    await api.goto(base + path, settle);
    const seen = await api.evaluate(`(() => ({
      at: location.pathname,
      heading: ((document.querySelector('h1, [role=heading][aria-level="1"]') || {}).innerText || '').trim().slice(0, 60),
      violations: window.__cspv || ['(recorder missing)'],
      monaco: document.querySelectorAll('.monaco-editor').length,
      frames: document.querySelectorAll('iframe').length,
    }))()`);
    out.push({ path, ...seen, framesAttached: attached.size - attachedBefore, log: logged().slice(before) });
  }
  // CLICKS (comma-separated data-testid values) are pressed in order on the last page, for views reached inside the
  // page rather than by address (the onboarding wizard's steps redirect a direct load to the first incomplete one).
  for (const testId of (process.env.CLICKS || '').split(',').map((c) => c.trim()).filter(Boolean)) {
    const before = logged().length;
    const recorded = await api.evaluate(`(window.__cspv || []).length`);
    await api.click(`[data-testid="${testId}"]`).catch(() => null);
    await api.sleep(Number(process.env.CLICK_SETTLE || 2500));
    const seen = await api.evaluate(`(() => ({
      at: location.pathname,
      heading: ((document.querySelector('h1, h2') || {}).innerText || '').trim().slice(0, 60),
      violations: (window.__cspv || []).slice(${Number(recorded)}),
    }))()`);
    out.push({ click: testId, ...seen, log: logged().slice(before) });
  }
  if (process.env.CONTROL === '1') {
    const inject = `(() => { const s = document.createElement('script'); s.textContent = 'window.__ran = 1'; document.head.appendChild(s); })()`;
    let before = logged().length;
    await api.evaluate(inject);
    await api.sleep(800);
    const control = await api.evaluate(`({ ran: window.__ran === 1, recorded: (window.__cspv || []).length })`);
    out.push({ control: { ...control, log: logged().slice(before) } });
    // The same in the newest attached frame that is still alive, when the last page has one.
    const gone = new Set(api.events.filter((e) => e.method === 'Target.detachedFromTarget').map((e) => e.params.sessionId));
    const live = [...attached].filter((s) => !gone.has(s)).pop();
    if (live) {
      before = logged().length;
      await api.send('Runtime.evaluate', { expression: inject }, live);
      await api.sleep(800);
      const ran = (await api.send('Runtime.evaluate', { expression: 'window.__ran === 1', returnByValue: true }, live)).result.value;
      out.push({ frameControl: { ran, log: logged().slice(before) } });
    }
  }
  clearInterval(pump);
  return out;
};
