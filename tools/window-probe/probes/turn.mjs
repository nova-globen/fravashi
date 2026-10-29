// One read-only turn, sampled once a second. Items 17 (pinned to newest), 20 (what it is doing), 21 (Stop).
export default async (api) => {
  const prompt = process.env.PROMPT;
  await api.evaluate(`(() => { const s = document.querySelector('[data-testid=session-empty-start]'); if (s) s.click(); })()`);
  await api.sleep(800);
  await api.click('[data-testid=composer-input]');
  await api.type(prompt);
  await api.sleep(400);
  const sendEnabled = await api.evaluate(`!document.querySelector('[data-testid=send-prompt]').disabled`);
  await api.evaluate(`document.querySelector('[data-testid=send-prompt]').click()`);

  const sample = () => api.evaluate(`JSON.stringify((() => {
    const t = (e) => (e ? (e.innerText || '').replace(/\\s+/g, ' ').trim() : null);
    const q = (s) => document.querySelector(s);
    const all = [...document.querySelectorAll('[data-testid]')];
    const ids = (re) => all.filter(e => re.test(e.getAttribute('data-testid')));
    const stop = ids(/stop/i).map(e => e.getAttribute('data-testid') + (e.disabled ? ':disabled' : ':enabled'));
    const activity = ids(/activity|working|turn-status|thinking/i).map(e => t(e).slice(0, 90)).filter(Boolean);
    const scroller = ids(/transcript/i).map(e => e).find(e => e.scrollHeight > e.clientHeight + 4) || ids(/transcript/i)[0];
    const gap = scroller ? Math.round(scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight) : null;
    const jump = ids(/jump/i).length;
    const pending = ids(/approval|approve/i).length;
    const last = scroller ? t(scroller).slice(-140) : null;
    return { stop, activity, gap, jump, pending, last };
  })())`);

  const seen = [];
  for (let i = 0; i < 55; i++) {
    await api.sleep(1000);
    const s = await sample();
    if (seen.length === 0 || seen[seen.length - 1].s !== s) seen.push({ at: i + 1, s });
    const o = JSON.parse(s);
    if (i > 8 && o.stop.length === 0 && o.activity.length === 0) break;
  }
  return { sendEnabled, samples: seen.map(x => ({ at: x.at, ...JSON.parse(x.s) })) };
};
