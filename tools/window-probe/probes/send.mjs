// Types PROMPT (or the contents of PROMPT_FILE) into the session composer and sends it, then returns at once:
// watching the turn is the supervisor's job (watch.ps1, sessionrows.mjs). Typed through CDP's own text input, so the
// composer sees what a person's typing produces.
import { readFileSync } from 'node:fs';

export default async (api) => {
  const prompt = process.env.PROMPT_FILE ? readFileSync(process.env.PROMPT_FILE, 'utf8').trim() : (process.env.PROMPT ?? '').trim();
  if (!prompt) return { error: 'set PROMPT or PROMPT_FILE' };
  await api.evaluate(`(() => { const s = document.querySelector('[data-testid=session-empty-start]'); if (s) s.click(); })()`);
  await api.sleep(800);
  await api.click('[data-testid=composer-input]');
  await api.type(prompt);
  await api.sleep(500);
  const enabled = await api.evaluate(`!document.querySelector('[data-testid=send-prompt]').disabled`);
  if (!enabled) return { error: 'the send button is disabled', typed: prompt.length };
  await api.evaluate(`document.querySelector('[data-testid=send-prompt]').click()`);
  await api.sleep(3000);
  return { sent: prompt.length, model: await api.evaluate(`document.querySelector('[data-testid=model-actual]')?.innerText.trim() ?? null`) };
};
