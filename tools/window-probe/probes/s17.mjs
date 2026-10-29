// Item 17 with the window HIDDEN. ACT=send types PROMPT and sends it, then returns at once so the caller can
// minimise the form through Win32. ACT=gap (default) reads the transcript's distance from its newest content,
// whether a turn is still running, document.visibilityState, and whether the jump-to-newest control is showing.
export default async (api) => {
  if (process.env.ACT === 'send') {
    await api.click('[data-testid=composer-input]');
    await api.type(process.env.PROMPT);
    await api.sleep(400);
    await api.evaluate(`document.querySelector('[data-testid=send-prompt]').click()`);
    return 'sent';
  }
  return api.evaluate(`(() => {
    const all = [...document.querySelectorAll('[data-testid]')];
    const ts = all.filter(e => /transcript/i.test(e.getAttribute('data-testid')));
    const sc = ts.find(e => e.scrollHeight > e.clientHeight + 4) || ts[0];
    return { visibility: document.visibilityState, running: !!document.querySelector('[data-testid=stop-session]'),
      scrollHeight: sc ? sc.scrollHeight : null, clientHeight: sc ? sc.clientHeight : null,
      gap: sc ? Math.round(sc.scrollHeight - sc.scrollTop - sc.clientHeight) : null,
      jump: all.filter(e => /jump/i.test(e.getAttribute('data-testid'))).length };
  })()`);
};
