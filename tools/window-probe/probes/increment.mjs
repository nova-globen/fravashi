// One governed increment, watched (items 18 and 77). Sends PROMPT, then samples once a second for up to
// SECONDS (default 240). Every approval that appears is RECORDED (tool, subject, target, warnings) and then
// approved, because this probe plays the operator of a throwaway repository; set DENY=1 to deny instead.
// It records every distinct state of the work strip, so the strip CHANGING as the session binds itself is
// seen rather than assumed, and the turn's ending (answer, or the validation failure of item 139).
export default async (api) => {
  const seconds = Number(process.env.SECONDS || 240);
  const deny = process.env.DENY === '1';
  const t = `(e) => (e ? (e.innerText || '').replace(/[ \\t\\r\\n]+/g, ' ').trim() : null)`;

  await api.evaluate(`(() => { const s = document.querySelector('[data-testid=session-empty-start]'); if (s) s.click(); })()`);
  await api.sleep(800);
  await api.click('[data-testid=composer-input]');
  await api.type(process.env.PROMPT);
  await api.sleep(400);
  await api.evaluate(`document.querySelector('[data-testid=send-prompt]').click()`);

  const strips = [], approvals = [], activities = [];
  let ended = null, quiet = 0;
  for (let i = 0; i < seconds; i++) {
    await api.sleep(1000);
    const s = await api.evaluate(`(() => {
      const t = ${t};
      const q = (id) => document.querySelector('[data-testid=' + id + ']');
      const all = [...document.querySelectorAll('[data-testid]')];
      const prompt = q('approval-prompt');
      const scroller = all.find(e => /transcript/i.test(e.getAttribute('data-testid')));
      return {
        strip: [t(q('session-work-state')), t(q('session-work-summary')),
                all.filter(e => e.getAttribute('data-testid') === 'session-work-chip').map(t).join(' | '),
                all.filter(e => e.getAttribute('data-testid') === 'session-plan-chip').map(t).join(' | ')].join(' ## '),
        running: !!q('stop-session'),
        activity: all.filter(e => /activity|turn-status/i.test(e.getAttribute('data-testid'))).map(t).filter(Boolean).join('/').slice(0, 80),
        approval: prompt ? { text: t(prompt).slice(0, 420), target: t(q('approval-target-resolved')), warning: t(q('approval-warning')), outside: !!q('approval-outside') } : null,
        tail: scroller ? t(scroller).slice(-260) : null,
      };
    })()`);
    if (strips[strips.length - 1]?.strip !== s.strip) strips.push({ at: i + 1, strip: s.strip });
    if (s.activity && activities[activities.length - 1] !== s.activity) activities.push(s.activity);
    if (s.approval) {
      approvals.push({ at: i + 1, ...s.approval });
      await api.evaluate(`document.querySelector('[data-testid=${deny ? 'deny' : 'approve'}]').click()`);
      await api.sleep(700);
    }
    quiet = s.running ? 0 : quiet + 1;
    if (i > 6 && quiet >= 4) { ended = { at: i + 1, tail: s.tail }; break; }
  }
  return { approvals, strips, activities: activities.slice(0, 40), ended };
};
