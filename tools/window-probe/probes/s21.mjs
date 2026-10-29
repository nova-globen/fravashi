// Item 21 in the installed window: start a new conversation WHILE a turn runs. Sends PROMPT, waits until Stop
// is showing, presses New conversation, records the confirm strip, answers ANSWER (no = Keep going, the
// default; yes = Stop and start fresh), and records what is on screen afterwards.
export default async (api) => {
  const state = () => api.evaluate(`(() => {
    const t = (e) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null);
    const q = (id) => document.querySelector('[data-testid=' + id + ']');
    const scroller = [...document.querySelectorAll('[data-testid]')].find(e => /transcript/i.test(e.getAttribute('data-testid')));
    return { stop: !!q('stop-session'), confirm: t(q('new-conversation-confirm')), yes: t(q('new-conversation-confirm-yes')),
      no: t(q('new-conversation-confirm-no')), transcriptChars: scroller ? t(scroller).length : null, empty: !!q('session-empty-start') };
  })()`);

  await api.click('[data-testid=composer-input]');
  await api.type(process.env.PROMPT);
  await api.sleep(400);
  await api.evaluate(`document.querySelector('[data-testid=send-prompt]').click()`);
  const running = await api.waitFor(`!!document.querySelector('[data-testid=stop-session]')`, 10000);
  await api.sleep(1500);
  const before = await state();
  await api.evaluate(`document.querySelector('[data-testid=new-conversation]').click()`);
  await api.sleep(1200);
  const asked = await state();
  const answer = process.env.ANSWER === 'yes' ? 'new-conversation-confirm-yes' : 'new-conversation-confirm-no';
  if (asked.confirm) await api.evaluate(`document.querySelector('[data-testid=${answer}]').click()`);
  await api.sleep(2500);
  const after = await state();
  return { running, before, asked, answered: asked.confirm ? answer : null, after };
};
