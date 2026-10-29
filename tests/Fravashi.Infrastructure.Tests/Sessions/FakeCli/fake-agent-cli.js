// A stand-in for the agent CLI used by ClaudeSessionRunnerTests. It is a REAL process (node runs on
// the dev host and in the container), so the tests exercise the actual redirect / stdin / environment
// / kill plumbing rather than a mocked one. It speaks the same stream-json protocol the real CLI
// does; the shapes here were copied from output captured in the container on 2026-08-04.
//
// Env it honors:
//   FAKE_CLI_REPORT      path to write a JSON report of how it was invoked (args, cwd, env, settings).
//                        The report carries the child's FULL environment, so a test can prove what the
//                        child did and did not inherit from the studio (backlog 72).
//   FAKE_CLI_MODE        'reply' (default) answers each stdin turn, 'crash' exits 3, 'hang' never
//                        answers, 'prose' says something and then never finishes the turn,
//                        'exhaust' answers the first turn the way claude 2.1.222 ends a turn whose
//                        document failed validation on every attempt (ADR 0022 outcome iii: an
//                        error result with terminal_reason structured_output_retry_exhausted) and
//                        then EXITS 1, which is what the real CLI was measured doing; 'review' answers one
//                        turn as a commit reviewer would (backlog 243, see FAKE_CLI_VERDICT below)
//   FAKE_CLI_PROMPT      review mode: where to write the text of the turn it was given
//   FAKE_CLI_VERDICT     review mode: the verdict document (JSON) its turn ends in; unset ends with none
//   FAKE_CLI_TOKENS      review mode: the input tokens its turn reports
//   FAKE_CLI_EXIT_AFTER  exit 0 after this many answered turns (0, the default, means never)
//   FAKE_CLI_CONVERSATIONS  '1' makes the fake keep conversations the way the real CLI does: a run
//                        announces the id it was given (--session-id, or the one --resume names),
//                        every answered turn is appended to
//                        <config home>/projects/<cwd, every non-alphanumeric as '-'>/<id>.jsonl, and
//                        --resume <id> FAILS (exit 1, "No conversation found with session ID: <id>")
//                        unless that file already exists. The config home is CLAUDE_CONFIG_DIR, else
//                        FAKE_CLI_DEFAULT_HOME (standing in for ~/.claude so a test never touches the
//                        real one); with neither, nothing is kept and every resume fails
const fs = require('fs');
const readline = require('readline');

const args = process.argv.slice(2);
const report = process.env.FAKE_CLI_REPORT;
if (report) {
  const settingsIndex = args.indexOf('--settings');
  const settingsPath = settingsIndex >= 0 ? args[settingsIndex + 1] : null;
  fs.writeFileSync(report, JSON.stringify({
    args,
    cwd: process.cwd(),
    path: process.env.PATH || '',
    anthropicApiKey: process.env.ANTHROPIC_API_KEY || null,
    env: { ...process.env },
    // Proves the hook settings file really existed when the CLI started, not just that a path was passed.
    settingsPath,
    settingsExists: settingsPath ? fs.existsSync(settingsPath) : false,
    settings: settingsPath && fs.existsSync(settingsPath) ? fs.readFileSync(settingsPath, 'utf8') : null,
  }, null, 2));
}

const mode = process.env.FAKE_CLI_MODE || 'reply';
if (mode === 'crash') {
  process.stderr.write('fake cli: not logged in\n');
  process.exit(3);
}

if (mode === 'hang') {
  // Stays alive no matter what arrives or closes, and keeps stamping a heartbeat file, so a test can
  // prove the process REALLY died rather than inferring it from a bookkeeping flag. Its pid goes beside
  // it, so a test can ask the OS whether the process is still there the moment the runner returns.
  const heartbeat = (report || 'fake-cli') + '.alive';
  fs.writeFileSync((report || 'fake-cli') + '.pid', String(process.pid));
  const stamp = () => { try { fs.writeFileSync(heartbeat, String(process.hrtime.bigint())); } catch {} };
  stamp();
  setInterval(stamp, 50);
  return;
}

const flag = (name) => {
  const index = args.indexOf(name);
  return index >= 0 && index + 1 < args.length ? args[index + 1] : null;
};

// The real CLI keeps one JSONL per conversation under ITS OWN config home, which is what makes both of
// these true: a resume started with a different home cannot find the conversation (backlog 25 and the
// owner's 2026-09-15 report), and whatever the agent printed sits there in the clear until it is scrubbed
// (backlog 116). CLAUDE_CONFIG_DIR is what the studio projects; FAKE_CLI_DEFAULT_HOME and
// FAKE_CLI_CONFIG_HOME stand in for the CLI's own default home when the studio projected nothing.
const path = require('path');
const keepsConversations = process.env.FAKE_CLI_CONVERSATIONS === '1';
const configHome =
  process.env.CLAUDE_CONFIG_DIR
  || process.env.FAKE_CLI_DEFAULT_HOME
  || process.env.FAKE_CLI_CONFIG_HOME
  || null;
const resumeId = flag('--resume');
const sessionId = keepsConversations
  ? (resumeId || flag('--session-id') || 'fake-cli-session-id')
  : 'fake-cli-session-id';

// The real CLI names the folder after the working directory. A conversations run needs that, because the
// studio has to find the same file from outside; a plain run keeps the fixed name its tests look under.
const conversationFile = configHome
  ? path.join(
      configHome,
      'projects',
      keepsConversations ? process.cwd().replace(/[^a-zA-Z0-9]/g, '-') : '-fake-cwd',
      sessionId + '.jsonl')
  : null;

if (keepsConversations && resumeId && !(conversationFile && fs.existsSync(conversationFile))) {
  // The sentence the owner's resumed sessions failed with on 2026-09-15: the real CLI looks for the
  // conversation under ITS OWN config home only, so a resume started with a different one cannot find it.
  process.stderr.write('No conversation found with session ID: ' + resumeId + '\n');
  process.exit(1);
}

const record = (line) => {
  if (!conversationFile) {
    return;
  }

  fs.mkdirSync(path.dirname(conversationFile), { recursive: true });
  fs.appendFileSync(conversationFile, JSON.stringify(line) + '\n');
};

const remember = (text) => record({ type: 'user', text });
const write = (line) => { record(line); process.stdout.write(JSON.stringify(line) + '\n'); };
const exitAfter = Number(process.env.FAKE_CLI_EXIT_AFTER || '0');

write({ type: 'system', subtype: 'init', session_id: sessionId, model: 'model-the-cli-picked', cwd: process.cwd() });

if (mode === 'prose') {
  // A turn that says something and then never ends: what the user pressing Stop, a crash, an OOM kill
  // or a dropped pipe all look like from the studio's side. The assistant text is followed by a
  // thinking delta and nothing else, so a test can wait for an event that PROVES the prose line was
  // already parsed before it kills the process, and no result line ever arrives.
  readline.createInterface({ input: process.stdin }).on('line', () => {
    write({
      type: 'assistant',
      message: { role: 'assistant', content: [{ type: 'text', text: 'three paragraphs the user should keep' }] },
      session_id: sessionId,
    });
    write({
      type: 'stream_event',
      event: { type: 'content_block_delta', index: 0, delta: { type: 'thinking_delta', thinking: 'still working' } },
    });
  });
  return;
}

if (mode === 'exhaust') {
  readline.createInterface({ input: process.stdin }).on('line', () => {
    // The real CLI has written the turn into the conversation before it gives up on the document.
    remember('exhausted turn');
    write({
      type: 'assistant',
      message: { role: 'assistant', content: [{ type: 'text', text: 'I could not produce the document.' }] },
      session_id: sessionId,
    });
    write({
      type: 'result',
      subtype: 'error_max_structured_output_retries',
      is_error: true,
      terminal_reason: 'structured_output_retry_exhausted',
      errors: ['Failed to provide valid structured output after 5 attempts'],
      session_id: sessionId,
      total_cost_usd: 0.02,
      usage: { input_tokens: 5, output_tokens: 7, cache_read_input_tokens: 11 },
    });
    process.exit(1);
  });
  return;
}

if (mode === 'review') {
  // Backlog 243: a commit review conversation. The first turn's text goes to FAKE_CLI_PROMPT (so a test can see the
  // diff the reviewer was handed), and the turn ends the way a typed turn does: a result line carrying
  // structured_output, read from FAKE_CLI_VERDICT (JSON), or no document at all when that is unset.
  // FAKE_CLI_TOKENS sets the turn's input tokens, for the token bound.
  readline.createInterface({ input: process.stdin }).on('line', (line) => {
    let text = '';
    try {
      text = (JSON.parse(line).message.content || []).map((b) => b.text || '').join('');
    } catch {
      return;
    }

    if (process.env.FAKE_CLI_PROMPT) {
      fs.writeFileSync(process.env.FAKE_CLI_PROMPT, text);
    }

    const verdict = process.env.FAKE_CLI_VERDICT ? JSON.parse(process.env.FAKE_CLI_VERDICT) : null;
    const result = {
      type: 'result',
      subtype: 'success',
      is_error: false,
      terminal_reason: 'completed',
      result: verdict ? JSON.stringify(verdict) : 'I have no verdict.',
      session_id: sessionId,
      total_cost_usd: 0.03,
      usage: { input_tokens: Number(process.env.FAKE_CLI_TOKENS || '100'), output_tokens: 20, cache_read_input_tokens: 0 },
    };
    if (verdict) {
      result.structured_output = verdict;
    }

    write(result);
  });
  return;
}

let turns = 0;
readline.createInterface({ input: process.stdin }).on('line', (line) => {
  let text = '';
  try {
    const message = JSON.parse(line);
    // An image block (backlog 26) echoes as its media type and size, so a test can see it arrived intact.
    text = (message.message.content || [])
      .map((b) => (b.type === 'image' ? '[' + b.source.media_type + ' ' + b.source.data.length + ']' : b.text))
      .join('');
  } catch {
    return; // a line the fake cannot read is ignored, like the real one
  }

  turns += 1;
  remember(text);
  write({
    type: 'assistant',
    message: { role: 'assistant', content: [{ type: 'text', text: 'echo: ' + text }] },
    session_id: sessionId,
  });
  write({
    type: 'result',
    subtype: 'success',
    is_error: false,
    terminal_reason: 'completed',
    result: 'echo: ' + text,
    session_id: sessionId,
    total_cost_usd: 0.01,
    usage: { input_tokens: 5, output_tokens: 7, cache_read_input_tokens: 11 },
  });

  if (exitAfter > 0 && turns >= exitAfter) {
    process.exit(0);
  }
});

process.stdin.on('end', () => process.exit(0));
