// Observe-and-click inside the prototype viewer's sandboxed frame (backlog 213) on the CURRENT page. The frame's one
// script is admitted by its hash alone (#287), and a refused script raises nothing the page can see, so this proves it
// RAN: it attaches to the frame's own target (a sandboxed frame runs in its own process, so it is not in the page's
// frame tree) and clicks each element carrying a click action until the prototype's state changes. A click changes
// state only through the listeners that script registers. Reports the candidates tried, whether any click changed a
// screen, a dialog, a state class or a selected tab, and the listeners the frame's document holds.
export default async (api) => {
  const { targetInfos } = await api.send('Target.getTargets');
  const frame = targetInfos.find((t) => t.type === 'iframe' && t.url === 'about:srcdoc');
  if (!frame) return { error: 'no srcdoc frame target', targets: targetInfos.map((t) => t.type + ' ' + t.url.slice(0, 40)) };
  const { sessionId } = await api.send('Target.attachToTarget', { targetId: frame.targetId, flatten: true });
  const r = await api.send('Runtime.evaluate', { returnByValue: true, awaitPromise: true, expression: `(async () => {
    const state = () => JSON.stringify([
      [...document.querySelectorAll('[data-mk-screen]')].map((s) => s.hidden),
      [...document.querySelectorAll('dialog')].map((d) => d.hasAttribute('open')),
      [...document.querySelectorAll('[class*="mk-state-"]')].map((e) => e.className),
      [...document.querySelectorAll('[data-mk-tab]')].map((t) => t.getAttribute('aria-selected')),
    ]);
    const candidates = [...document.querySelectorAll('[data-mk-on], [data-mk-tab]')]
      .filter((e) => e.hasAttribute('data-mk-tab') || /"click"/.test(e.getAttribute('data-mk-on')));
    const tried = [];
    let ran = false;
    for (const el of candidates.slice(0, 12)) {
      const before = state();
      el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
      await new Promise((r) => setTimeout(r, 150));
      ran = state() !== before;
      tried.push((el.getAttribute('data-mk-anchor') || el.tagName) + (ran ? ' changed' : ''));
      if (ran) break;
    }
    return { screens: document.querySelectorAll('[data-mk-screen]').length, candidates: candidates.length, tried, ran };
  })()` }, sessionId);
  // A prototype with no actions has nothing to click, so also ask DevTools which listeners the document holds: the
  // script registers click, change and keydown on it, and nothing else in the frame registers any.
  const listeners = await api.send('Runtime.evaluate', {
    includeCommandLineAPI: true, returnByValue: true,
    expression: `Object.fromEntries(Object.entries(getEventListeners(document)).map(([k, v]) => [k, v.length]))`,
  }, sessionId);
  await api.send('Target.detachFromTarget', { sessionId });
  return r.exceptionDetails ? { error: r.exceptionDetails.text } : { ...r.result.value, documentListeners: listeners.result.value };
};
