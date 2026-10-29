// Tiny CDP driver for the real fravashi.exe WebView2 window (loopback debug port).
// usage: node cdp.mjs <script.mjs>   where the script default-exports async (api) => result
import { pathToFileURL } from 'node:url';
import { writeFileSync } from 'node:fs';

const PORT = process.env.CDP_PORT || '9333';
const match = process.env.CDP_MATCH || '';
// CDP_ID picks a window by the start of its target id, for when two windows show the same path (the URL drops ?ws=
// after in-app navigation, so two folders' windows on /sessions cannot be told apart by URL).
const targetId = process.env.CDP_ID || '';
const targets = await (await fetch(`http://127.0.0.1:${PORT}/json`)).json();
const pages = targets.filter(t => t.type === 'page' && (!match || t.url.includes(match)) && (!targetId || t.id.startsWith(targetId)));
if (!pages.length) { console.log('NO PAGE TARGET', JSON.stringify(targets.map(t => [t.type, t.url]))); process.exit(2); }
const ws = new WebSocket(pages[0].webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
let id = 0; const pending = new Map(); const events = [];
ws.onmessage = m => {
  const d = JSON.parse(m.data);
  if (d.id && pending.has(d.id)) { const p = pending.get(d.id); pending.delete(d.id); d.error ? p.rej(new Error(JSON.stringify(d.error))) : p.res(d.result); }
  else events.push(d);
};
// sessionId addresses a target attached through this one (Target.attachToTarget or setAutoAttach with flatten): a
// sandboxed frame runs in its own process, so its log and its scripts are reached only through its own session.
const send = (method, params = {}, sessionId) => new Promise((res, rej) => {
  const i = ++id; pending.set(i, { res, rej }); ws.send(JSON.stringify(sessionId ? { id: i, method, params, sessionId } : { id: i, method, params }));
});
const sleep = ms => new Promise(r => setTimeout(r, ms));
const evaluate = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error('page threw: ' + JSON.stringify(r.exceptionDetails.exception?.description || r.exceptionDetails.text));
  return r.result.value;
};
const api = {
  send, sleep, evaluate, events, targets, url: pages[0].url,
  shot: async (file) => { const r = await send('Page.captureScreenshot', { format: 'png' }); writeFileSync(file, Buffer.from(r.data, 'base64')); return file; },
  goto: async (url, settle = 2500) => { await send('Page.navigate', { url }); await sleep(settle); },
  waitFor: async (expr, ms = 15000) => { const end = Date.now() + ms; while (Date.now() < end) { try { if (await evaluate(expr)) return true; } catch {} await sleep(200); } return false; },
  click: async (selector) => {
    const box = await evaluate(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); if (!e) return null; e.scrollIntoView({block:'center'}); const r = e.getBoundingClientRect(); return {x: r.x + r.width/2, y: r.y + r.height/2}; })()`);
    if (!box) throw new Error('no element: ' + selector);
    for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased'])
      await send('Input.dispatchMouseEvent', { type, x: box.x, y: box.y, button: type === 'mouseMoved' ? 'none' : 'left', clickCount: type === 'mouseMoved' ? 0 : 1 });
    return box;
  },
  key: async (key, code, vk) => {
    for (const type of ['keyDown', 'keyUp'])
      await send('Input.dispatchKeyEvent', { type, key, code, windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk, text: type === 'keyDown' && key === 'Enter' ? '\r' : undefined });
  },
  type: async (text) => { await send('Input.insertText', { text }); },
};
await send('Page.enable'); await send('Runtime.enable');
const mod = await import(pathToFileURL(process.argv[2]).href);
try { const out = await mod.default(api); console.log(typeof out === 'string' ? out : JSON.stringify(out, null, 1)); }
catch (e) { console.log('SCRIPT ERROR: ' + e.message); process.exitCode = 1; }
ws.close();
