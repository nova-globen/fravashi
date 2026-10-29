// RETIRED to v2. Decided, 2026-09-22: Codex and Copilot are not part of this version. The only tests
// that ran this stand-in are disabled behind the V2_CODEX_COPILOT symbol, so nothing starts it any more.
// It is kept because it answers the real protocol as read from the pinned binary; see docs/v2.md section 1.

// A stand-in for `codex app-server --listen stdio://`, used by CodexSessionRunnerTests (backlog 66).
// It is a REAL process, so the runner's redirect, stdin, environment, cwd and kill plumbing are genuine.
//
// The message shapes are CONSTRUCTED from the schema the pinned codex 0.146.0 generates about itself
// (`codex app-server generate-ts`): ServerRequest.ts, ServerNotification.ts, v2/ThreadItem.ts and the
// approval types. Nothing here was captured from a live Codex turn; docs/plans/codex-copilot-runners.md
// says what would replace it with a capture.
//
// Env it honors:
//   FAKE_CLI_REPORT      path of a JSON report (args, cwd, env); every message the runner sends is appended
//                        to <report>.messages.jsonl, so a test can read back exactly what was answered
//   FAKE_CLI_MODE        'command' (default) asks to run one shell command, 'patch' asks to apply a two-file
//                        patch, 'requests' sends the requests the runner must refuse, 'crash' exits 3,
//                        'hang' never answers and stamps a heartbeat, 'refuse-thread' errors thread/start
//   FAKE_CLI_EXIT_AFTER  exit 0 after this many completed turns (0, the default, means never)
const fs = require('fs');
const readline = require('readline');

const report = process.env.FAKE_CLI_REPORT;
const messages = report ? report + '.messages.jsonl' : null;
if (report) {
  fs.writeFileSync(report, JSON.stringify({ args: process.argv.slice(2), cwd: process.cwd(), env: { ...process.env } }, null, 2));
}

const mode = process.env.FAKE_CLI_MODE || 'command';
if (mode === 'crash') {
  process.stderr.write('fake codex: not logged in\n');
  process.exit(3);
}

if (mode === 'hang') {
  const heartbeat = (report || 'fake-codex') + '.alive';
  const stamp = () => { try { fs.writeFileSync(heartbeat, String(process.hrtime.bigint())); } catch {} };
  stamp();
  setInterval(stamp, 50);
  return;
}

const exitAfter = Number(process.env.FAKE_CLI_EXIT_AFTER || '0');
const write = (message) => process.stdout.write(JSON.stringify(message) + '\n');
const notify = (method, params) => write({ method, params });
const threadId = '019a0000-0000-7000-8000-00000000c0de';
const waiting = new Map();
let serverRequestId = 0;
let turns = 0;
let totalInput = 0;

// Server requests use STRING ids on purpose: the schema's RequestId is `string | number`, and the runner must
// echo whichever it was given.
const ask = (method, params) => new Promise((resolve) => {
  const id = 'srv-' + (++serverRequestId);
  waiting.set(id, resolve);
  write({ id, method, params });
});

const item = (type, fields) => ({ type, ...fields });

async function runTurn(turnId, text) {
  notify('turn/started', { threadId, turn: { id: turnId, items: [], itemsView: 'full', status: 'inProgress', error: null, startedAt: 1, completedAt: null, durationMs: null } });
  notify('item/reasoning/summaryTextDelta', { threadId, turnId, itemId: 'rs_1', delta: 'thinking', summaryIndex: 0 });

  let said = '';
  if (mode === 'patch') {
    const changes = [
      { path: 'notes/new.txt', kind: { type: 'add' }, diff: '+hello\n' },
      { path: 'README.md', kind: { type: 'update', move_path: null }, diff: '-old\n+new\n' },
    ];
    notify('item/started', { threadId, turnId, startedAtMs: 1, item: item('fileChange', { id: 'fc_1', changes, status: 'inProgress' }) });
    const answer = await ask('item/fileChange/requestApproval', { threadId, turnId, itemId: 'fc_1', startedAtMs: 1, reason: null, grantRoot: null });
    const accepted = answer.result && answer.result.decision === 'accept';
    notify('item/completed', { threadId, turnId, completedAtMs: 2, item: item('fileChange', { id: 'fc_1', changes, status: accepted ? 'completed' : 'declined' }) });
    said = accepted ? 'patched' : 'patch declined';
  } else if (mode === 'requests') {
    const replies = [];
    replies.push(await ask('item/tool/requestUserInput', { threadId, turnId, itemId: 'q_1', questions: [] }));
    replies.push(await ask('item/permissions/requestApproval', { threadId, turnId, itemId: 'p_1', environmentId: null, startedAtMs: 1, cwd: process.cwd(), reason: null, permissions: {} }));
    replies.push(await ask('execCommandApproval', { conversationId: threadId, callId: 'c_1', command: ['rm', '-rf', '/'], cwd: '/', reason: null }));
    replies.push(await ask('future/somethingNew', { threadId }));
    replies.push(await ask('item/commandExecution/requestApproval', { threadId, turnId, itemId: 'cmd_blank', startedAtMs: 1, environmentId: null }));
    said = 'asked ' + replies.length;
  } else {
    const command = 'echo hi > out.txt';
    notify('item/started', { threadId, turnId, startedAtMs: 1, item: item('commandExecution', { id: 'cmd_1', command, cwd: process.cwd(), processId: null, source: 'agent', status: 'inProgress', commandActions: [], aggregatedOutput: null, exitCode: null, durationMs: null, pluginId: null, scriptPath: null }) });
    const answer = await ask('item/commandExecution/requestApproval', { threadId, turnId, itemId: 'cmd_1', startedAtMs: 1, environmentId: null, command, cwd: process.cwd(), reason: null });
    const accepted = answer.result && answer.result.decision === 'accept';
    notify('item/completed', { threadId, turnId, completedAtMs: 2, item: item('commandExecution', { id: 'cmd_1', command, cwd: process.cwd(), processId: null, source: 'agent', status: accepted ? 'completed' : 'declined', commandActions: [], aggregatedOutput: accepted ? 'hi\n' : null, exitCode: accepted ? 0 : null, durationMs: 5, pluginId: null, scriptPath: null }) });
    said = accepted ? 'ran it' : 'it was declined';
  }

  const reply = `${said}: ${text}`;
  notify('item/started', { threadId, turnId, startedAtMs: 3, item: item('agentMessage', { id: 'msg_' + turnId, text: '', phase: null, memoryCitation: null }) });
  notify('item/agentMessage/delta', { threadId, turnId, itemId: 'msg_' + turnId, delta: reply.slice(0, 4) });
  notify('item/agentMessage/delta', { threadId, turnId, itemId: 'msg_' + turnId, delta: reply.slice(4) });
  notify('item/completed', { threadId, turnId, completedAtMs: 4, item: item('agentMessage', { id: 'msg_' + turnId, text: reply, phase: null, memoryCitation: null }) });

  totalInput += 1000;
  const breakdown = (input) => ({ totalTokens: input + 50, inputTokens: input, cachedInputTokens: 400, cacheWriteInputTokens: 0, outputTokens: 50, reasoningOutputTokens: 10 });
  notify('thread/tokenUsage/updated', { threadId, turnId, tokenUsage: { total: breakdown(totalInput), last: breakdown(1000), modelContextWindow: 272000 } });
  notify('turn/completed', { threadId, turn: { id: turnId, items: [], itemsView: 'full', status: 'completed', error: null, startedAt: 1, completedAt: 2, durationMs: 1000 } });

  turns++;
  if (exitAfter > 0 && turns >= exitAfter) {
    process.exit(0);
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
      write({ id: message.id, result: { userAgent: 'fake-codex/0.146.0', codexHome: '/tmp/codex-home', platformFamily: 'unix', platformOs: 'linux' } });
      break;
    case 'initialized':
      break;
    case 'thread/start':
    case 'thread/resume':
      if (mode === 'refuse-thread') {
        write({ id: message.id, error: { code: -32600, message: 'not logged in' } });
        break;
      }
      write({ id: message.id, result: { thread: { id: message.params.threadId || threadId, turns: [] }, model: message.params.model || 'gpt-the-cli-picked', modelProvider: 'openai', serviceTier: null, cwd: message.params.cwd, instructionSources: [], approvalPolicy: message.params.approvalPolicy, approvalsReviewer: 'user', sandbox: { type: 'workspaceWrite' }, reasoningEffort: null } });
      break;
    case 'turn/start': {
      const turnId = 'turn-' + (turns + 1);
      write({ id: message.id, result: { turn: { id: turnId, items: [], itemsView: 'full', status: 'inProgress', error: null, startedAt: 1, completedAt: null, durationMs: null } } });
      const text = (message.params.input || []).map((i) => i.text).join('');
      runTurn(turnId, text);
      break;
    }
    default:
      if (message.id !== undefined) {
        write({ id: message.id, error: { code: -32601, message: 'fake codex does not know ' + message.method } });
      }
  }
});
rl.on('close', () => process.exit(0));
