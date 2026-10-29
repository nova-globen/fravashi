// RETIRED to v2. Decided, 2026-09-22: Codex and Copilot are not part of this version. The only tests
// that ran this stand-in are disabled behind the V2_CODEX_COPILOT symbol, so nothing starts it any more.
// It is kept because it answers the real protocol as read from the pinned binary; see docs/v2.md section 1.

// A stand-in for `copilot --acp`, used by CopilotSessionRunnerTests (backlog 66). A REAL process speaking
// JSON-RPC 2.0 over stdio.
//
// The message shapes are CONSTRUCTED from the pinned @github/copilot 1.0.78 package's bundled code (its ACP
// method table, the function that maps internal events to session/update kinds, and the permission request it
// builds: allow_once / allow_always / reject_once options, rawInput {command} / {fileName, diff} / {path}),
// which follows the public Agent Client Protocol. Nothing here was captured from a live Copilot turn.
//
// Env it honors:
//   FAKE_CLI_REPORT      JSON report path (args, cwd, env); every runner message is appended to <report>.messages.jsonl
//   FAKE_CLI_MODE        'edit' (default), 'read' (an out-of-directory read), 'always-only' (no allow_once offered),
//                        'no-options', 'fs' (asks the client to read a file), 'crash', 'hang', 'refuse-session'
//   FAKE_ACP_LOAD        'false' to advertise no loadSession capability
//   FAKE_CLI_EXIT_AFTER  exit 0 after this many prompts (0 means never)
//   FAKE_CLI_EXIT_DELAY_MS  how long after writing the prompt RESULT to exit (default 20). 0 closes
//                        stdout in the same tick as the result, which is the real CLI's behaviour when
//                        it has nothing left to do and is the window backlog 66's flake lives in.
const fs = require('fs');
const readline = require('readline');

const report = process.env.FAKE_CLI_REPORT;
const messages = report ? report + '.messages.jsonl' : null;
if (report) {
  fs.writeFileSync(report, JSON.stringify({ args: process.argv.slice(2), cwd: process.cwd(), env: { ...process.env } }, null, 2));
}

const mode = process.env.FAKE_CLI_MODE || 'edit';
if (mode === 'crash') {
  process.stderr.write('fake copilot: no authentication information found\n');
  process.exit(3);
}

if (mode === 'hang') {
  const heartbeat = (report || 'fake-copilot') + '.alive';
  const stamp = () => { try { fs.writeFileSync(heartbeat, String(process.hrtime.bigint())); } catch {} };
  stamp();
  setInterval(stamp, 50);
  return;
}

const exitAfter = Number(process.env.FAKE_CLI_EXIT_AFTER || '0');
const exitDelayMs = Number(process.env.FAKE_CLI_EXIT_DELAY_MS || '20');
const write = (message) => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...message }) + '\n');
const update = (sessionId, body) => write({ method: 'session/update', params: { sessionId, update: body } });
const waiting = new Map();
let nextId = 0;
let prompts = 0;

const ask = (method, params) => new Promise((resolve) => {
  const id = ++nextId;
  waiting.set(id, resolve);
  write({ id, method, params });
});

const allowOnce = { optionId: 'allow_once', kind: 'allow_once', name: 'Allow once' };
const allowAlways = { optionId: 'allow_always', kind: 'allow_always', name: 'Always allow' };
const rejectOnce = { optionId: 'reject_once', kind: 'reject_once', name: 'Deny' };

async function prompt(message) {
  const { sessionId } = message.params;
  const text = (message.params.prompt || []).map((b) => b.text).join('');
  update(sessionId, { sessionUpdate: 'agent_thought_chunk', content: { type: 'text', text: 'planning' } });
  update(sessionId, { sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: 'Let me look.' } });

  let outcome = 'none';
  if (mode === 'fs') {
    const reply = await ask('fs/read_text_file', { sessionId, path: '/etc/passwd' });
    outcome = reply.error ? 'fs-refused' : 'fs-served';
  } else {
    let toolCall;
    let options = [allowOnce, allowAlways, rejectOnce];
    if (mode === 'read') {
      toolCall = { toolCallId: 'call_r', title: 'Access paths outside trusted directories', kind: 'read', status: 'pending', rawInput: { path: '/workspaces/w1/a, /data/fravashi.db' }, locations: [] };
      options = [allowOnce, rejectOnce];
    } else {
      toolCall = { toolCallId: 'call_1', title: 'Edit README.md', kind: 'edit', status: 'pending', rawInput: { fileName: 'README.md', diff: '-a\n+b\n' }, locations: [{ path: 'README.md' }] };
      if (mode === 'always-only') options = [allowAlways, rejectOnce];
      if (mode === 'no-options') options = [];
    }
    update(sessionId, { sessionUpdate: 'tool_call', ...toolCall });
    const reply = await ask('session/request_permission', { sessionId, toolCall, options });
    const selected = reply.result && reply.result.outcome && reply.result.outcome.outcome === 'selected' ? reply.result.outcome.optionId : 'cancelled';
    outcome = selected;
    const allowed = selected === 'allow_once' || selected === 'allow_always';
    update(sessionId, { sessionUpdate: 'tool_call_update', toolCallId: toolCall.toolCallId, status: allowed ? 'completed' : 'failed', content: [{ type: 'content', content: { type: 'text', text: allowed ? 'edited' : 'The user rejected this tool call.' } }] });
  }

  update(sessionId, { sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: `${outcome}: ${text}` } });
  update(sessionId, { sessionUpdate: 'usage_update', used: 1200, size: 128000 });
  write({ id: message.id, result: { stopReason: 'end_turn', usage: { inputTokens: 1000, outputTokens: 40, totalTokens: 1040, cachedReadTokens: 300 } } });

  prompts++;
  if (exitAfter > 0 && prompts >= exitAfter) {
    setTimeout(() => process.exit(0), exitDelayMs);
  }
}

const rl = readline.createInterface({ input: process.stdin });
rl.on('line', (line) => {
  if (!line.trim()) return;
  if (messages) fs.appendFileSync(messages, line + '\n');
  const message = JSON.parse(line);

  if (message.id !== undefined && message.method === undefined) {
    const resolve = waiting.get(message.id);
    if (resolve) { waiting.delete(message.id); resolve(message); }
    return;
  }

  switch (message.method) {
    case 'initialize':
      write({ id: message.id, result: { protocolVersion: 1, agentCapabilities: { loadSession: process.env.FAKE_ACP_LOAD !== 'false' }, authMethods: [] } });
      break;
    case 'session/new':
      if (mode === 'refuse-session') {
        write({ id: message.id, error: { code: -32000, message: 'Authentication required' } });
        break;
      }
      write({ id: message.id, result: { sessionId: 'acp-session-1' } });
      break;
    case 'session/load': {
      const sessionId = message.params.sessionId;
      update(sessionId, { sessionUpdate: 'user_message_chunk', content: { type: 'text', text: 'the old question' } });
      update(sessionId, { sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: 'the old answer' } });
      write({ id: message.id, result: {} });
      break;
    }
    case 'session/set_model':
      write({ id: message.id, result: {} });
      break;
    case 'session/prompt':
      prompt(message);
      break;
    default:
      if (message.id !== undefined) {
        write({ id: message.id, error: { code: -32601, message: 'Method not found: ' + message.method } });
      }
  }
});
rl.on('close', () => process.exit(0));
