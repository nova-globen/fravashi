// A supervisor's glance at the open conversation: a pending approval (its card's text), whether a turn is running, and
// the last few transcript entries. Observe only; watch-trial.ps1 runs it every 15 seconds.
export default async (api) => api.evaluate(`(() => {
  const t = (e) => (e?.innerText ?? '').replace(/\\s+/g, ' ').trim();
  const approval = document.querySelector('.session-approval, [data-testid^=approval-]');
  const stop = [...document.querySelectorAll('button')].find(b => /^Stop$/.test(b.innerText.trim()) && !b.disabled);
  const thinking = !!document.querySelector('.session-thinking, [data-testid=session-thinking]');
  const entries = [...document.querySelectorAll('.chat-bubble, .tool-call-card, [data-testid=chat-bubble], [data-testid^=tool-call]')].slice(-4).map(e => t(e).slice(0, 220));
  // The pending call's RAW text, judged here because probe.ps1 collapses all whitespace in what a probe returns: a
  // second command on a new line would otherwise reach the watcher as one line that looks like a plain build.
  const body = document.querySelector('.session-approval-body pre');
  const multiline = body ? /[\\r\\n]/.test(body.textContent.trim()) : false;
  return { pending: approval ? t(approval).slice(0, 600) : null, multiline, running: !!stop || thinking, entries };
})()`);
