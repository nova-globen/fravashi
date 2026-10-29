# window-probe

Drives the REAL installed `fravashi.exe` studio window from a session. Not shipped, not built, not part of the
product. CLAUDE.md ("Driving the real window without the owner") says why it exists, what it can prove,
and the five traps; read that first.

```powershell
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=9333 --remote-debugging-address=127.0.0.1'
Start-Process "$env:LOCALAPPDATA\Programs\Fravashi\fravashi.exe"
```

- `cdp.mjs` attaches to the window over the Chrome DevTools Protocol (Node 24, built-in WebSocket, no
  packages) and runs one probe: `node cdp.mjs my-probe.mjs`. A probe default-exports
  `async (api) => result`; `api` offers `evaluate`, `click(selector)`, `type`, `key`, `shot(file)`,
  `waitFor`, `goto`, `send(method, params)` and `sleep`. `CDP_MATCH` picks a window by a URL fragment
  when several are open.
- `example-probe.mjs` reads the Code page (tree, title bar, agent toggle) and saves a screenshot to the
  path in `SHOT`.
- `resize-form.ps1 -Width 760 -Height 560` resizes the FORM through Win32, which is the only correct way
  to test a window size (resizing the embedded browser over CDP leaves dead space inside the window).

Always bound a run with a timeout (`timeout 25 node cdp.mjs probe.mjs`): a click that navigates leaves
the next call unanswered forever. Write probes with an editor or the Write tool, not a shell heredoc,
which eats regex backslashes. When finished, `fravashi exit` and start fravashi normally so the port closes.

Name the control you click. The window's own title bar has a button titled "Close" (`.studio-caption-close`), and it comes
before every page control in the document, so a loose selector such as `button[title=Close]` closes the studio window and
takes the debug port with it (seen 2026-10-08). fravashi stays resident in the tray: start `fravashi.exe --open <folder>` with the same
environment variable and the window and the port come back. The Code page's tab close is `.code-tab-close`. The Code page also
restores its open tabs, and which one is active, from the page's local storage (`fravashi-code-workspace:<workspace id>`). On
builds before v0.9.70 a navigation to `/code/<file>` then left the stored active tab on screen instead of the file (backlog 265,
whose fix ships in v0.9.70), so a probe's file never became the active one. On such a build, remove that key and reload before
a check that opens a file; on any build, removing it keeps tabs from earlier probes out of the way.

## The probes that settled real items (`probes/`, kept since 2026-09-21)

Each one is small and does ONE thing, because a click that navigates leaves the next CDP call unanswered:
a probe either CLICKS and returns, or OBSERVES. Run a click probe, wait a few seconds, run an observe probe.
`pageeval.mjs` is the smallest observe probe there is: it evaluates the `EXPR` environment variable in the page and returns
the value (a read-only check, a marker set before a fault and read after it); the network-gap check of v0.9.67 used it. With Git Bash set `MSYS_NO_PATHCONV=1` first, or a value starting with `/` is rewritten.

Run them from POWERSHELL, never Git Bash, whenever an argument starts with `/` (Git Bash rewrites it into a
Windows path before Node sees it), and always with a bound:

```powershell
function Probe($file, $ms) {
  $p = Start-Process node -ArgumentList "tools/window-probe/cdp.mjs", "tools/window-probe/probes/$file" `
        -NoNewWindow -PassThru -RedirectStandardOutput "$env:TEMP\probe.out"
  if (-not $p.WaitForExit($ms)) { $p.Kill(); "TIMED OUT: $file" }
  (Get-Content "$env:TEMP\probe.out" -Raw) -replace '\s+', ' '
}
$env:NAV = '^/sessions'; Probe 'nav.mjs' 20000; Start-Sleep 3; Probe 's126.mjs' 25000
```

| Probe | Kind | What it does |
|---|---|---|
| `nav.mjs` | click | Clicks the in-app link whose href matches `NAV` (a regular expression source). |
| `open2.mjs` | click | On Home, opens the recent folder named exactly `agent-session-proof`. |
| `newconv.mjs`, `pickconv.mjs` | click | Start a fresh conversation; open the first one in the list. |
| `wikiopen.mjs` | click | Opens the first document in the wiki list. |
| `where.mjs` | observe | Path, title, and the top of the page. |
| `turn.mjs` | both | Types `PROMPT`, sends it, samples once a second: Stop, activity, scroll gap, pending approvals. |
| `sync.mjs` | both | Presses "Sync now" on Home and records what the wiki card says (item 12). |
| `summary.mjs` | both | Presses "Write a summary" on the open document and records the progression (item 40). |
| `broker.mjs` | both | The broker card under Settings. `ACT=read` (default), `mint` or `revoke`. |
| `dead.mjs` | observe | Whether the reconnect overlay is up and what it says (item 136). |
| `home137.mjs`, `s126.mjs`, `s128.mjs` | both | Home panels and filter; Sessions model label and work strip; strip toggle and pinned menu row. |
| `pub83.mjs` | both | The Publication section of the open wiki document (item 83). `ACT=read`, `open` with `TITLE`, `edit` with `LINE`, or a button's `data-testid` to press it and record every panel state. |
| `wikinew.mjs` | both | On `/wiki`, creates a document through the page's own editor from `TITLE` and `BODY`. |
| `transcript.mjs` | observe | The wiki list, the tool rows, and the last `TAIL` characters of the open conversation. |
| `model.mjs` | both | Lists the composer's model picker; with `PICK` chooses the first option containing it. Probe turns belong on Haiku: the default is the largest model. |
| `s21.mjs` | both | Presses New conversation WHILE a turn runs and answers the strip (`ANSWER=no` keeps going). Check `before.confirm` is null, or the strip is a leftover of an earlier press. |
| `s17.mjs` | both | `ACT=send` sends `PROMPT` and returns so the FORM can be minimised through Win32; `ACT=gap` reads the transcript's distance from its newest content and `visibilityState`. |
| `login130.mjs` | both | The agent CLI sign-in panels on `/onboarding/runtimes`: lists them, or starts `TOOL`'s flow, reports the URL and only the SHAPE of the code, and stops it. |
| `cspwalk.mjs` | both | Loads each path in `PATHS` and reports every Content-Security-Policy violation, the page's and every frame's (#287). `CLICKS` presses buttons by `data-testid` on the last page; `CONTROL=1` proves the recorder fires. Its output in `docs/evidences/2026-10-11-287-csp-walk/` says how to read it. |
| `titlebar.mjs` | click | Presses one title bar button by `ACTION` (back, forward, undo, redo, palette) with a real click and reports the location before and after. Never close, minimize or maximize. |
| `framescript.mjs` | observe | Attaches to the prototype viewer's sandboxed frame and reports whether its script ran (its document listeners, and a click that changes the prototype's state). |

`send(method, params, sessionId)` reaches a target attached through the page (`Target.attachToTarget` or `setAutoAttach`
with `flatten`). A sandboxed frame runs in its own process, is absent from the page's frame tree and logs only to its own
target, so a check of anything inside one goes through its session.
| `increment.mjs` | both | One governed increment, watched (items 18, 77): sends `PROMPT`, RECORDS every approval (subject, resolved target, warnings) and then approves it as the operator of a throwaway repository (`DENY=1` denies), and records every distinct state of the work strip and how the turn ended. |
| `endconv.mjs` | both | Ends the folder's live conversation the way a person can (New conversation, then "Stop and start fresh"). Needed before anything that refuses while a conversation runs, such as moving a folder to another project. |
| `p47.mjs` | both | The board's provider acts: `ACT=read`/`sync`/`import` on `/board/sync`, and `ACT=new TITLE=...`/`send`/`readitem` on a work item. |
| `p48.mjs` | both | What a move this board makes means on the provider (items 47/48). `ACT=drag COLUMN=done` drags the first card and reads the question or the queued line; `choose PICK=Closed`; `later`; `read`; `conflicts`; `settle SIDE=provider` settles the first open disagreement. The drag is dispatched as real DOM drag events, because CDP cannot synthesise one the browser's drag machinery will run. |
| `conflicts.mjs` | observe | The sync page's disagreement section in full: which field, which two values, the note, and the choices offered. |
| `goto.mjs` | click | Navigates to `URL` through an in-app link when one exists, else a real navigation (which re-stamps the window's own `?ws=`). |
| `fields.mjs` | observe | Every value on the page INCLUDING inside web components' shadow roots, which a plain selector cannot see. Anything that looks like a credential is reported only as set or empty. |
| `fulltext.mjs` | observe | The page's visible text up to `CHARS`, with every input's id. |
| `p121.mjs` | both | Settings > Environment > Project: `ACT=read`, `create` or `move` with `NAME`, and `ACT=titles` for whether the open page says whose project it shows. |
| `p108.mjs` | observe | What a runtime's panel says about keys under the broker. It can press "Remove the stored key" (`ACT=remove`); do NOT do that in the owner's install, the key is theirs. |
| `backup79.mjs` | both | Settings > Environment > Backup: writes a backup and records what the page says (`GOTO=1` opens the section first). |
| `scratch67.mjs` | both | The scratchpad (item 67). `ACT=open` opens it and types markdown, then reads what renders (bold weight, italic, checklist boxes, struck-through done items); `tick` clicks an open box with a real mouse event; `copy` sends a GENUINE Ctrl+C and records what lands on the clipboard per type; `read` just reads. Three traps it documents in its own comments: focus through Monaco's `focus()`, never its `ime-text-area`; `document.execCommand('copy')` from a script fires no copy event; and a backslash inside the page code is eaten by the template string. |
| `shot.mjs` | observe | Saves a page screenshot to the path in `SHOT`. Hangs on a HIDDEN window; bound it. |
| `readsel.mjs` | observe | The text and every `data-testid` inside the first element matching `SEL` (a CSS selector); narrower than `fulltext.mjs`. |
| `selects.mjs` | both | Every select's testid and chosen option; with `SET=<testid>` and `TO=<option text prefix>` it chooses first (a change event, which Blazor binds). |
| `fillclick.mjs` | click | Types `VALUE` into the input with testid `FILL` through the native setter and presses `CLICK` a moment later. |
| `loginstart.mjs` | click | Starts a runtime's interactive sign-in (`TOOL`, default Claude) and reports the link it prints, then LEAVES it running for a person to finish; never types into it. `login130.mjs` stops it instead. |
| `pickerstate.mjs` | both | The composer's model picker read from the DOM (no screenshot, which hangs when the window is not on screen): each unavailable, Fable or Opus 5 option with its disabled state and subtitle. `CLOSE=1` closes a panel an earlier probe left open. |
| `pickeropen2.mjs` | observe | Whether the model picker's panel is open. |
| `folded.mjs` | observe | The text of every `details` block on the page, closed ones included: the approval card's "What it wants to do" and the transcript's folded earlier wording. |
| `capture-cli-request.sh` | container | Run INSIDE the container: a listener that records what the pinned agent CLI sends to its base URL (method, URL, header NAMES, the two `anthropic-*` values, never a credential). It is how the broker's dropped `anthropic-beta` header was proven instead of guessed. |

## Supervising a self-hosting trial (an agent inside Fravashi working on this repository)

How #184 was built, released and upgraded by an agent on 2026-10-02 while a host session approved:

| Tool | Kind | What it does |
|---|---|---|
| `send.mjs` | click | Types `PROMPT` (or `PROMPT_FILE`) into the composer and sends it. |
| `watch-trial.ps1` | both | Run in the background: approves ONLY a plain `dotnet build` or `dotnet test` unread, and stops with `PENDING: ...` on anything else or `TURN ENDED: ...` when the turn ends. Start it again after each answer. |
| `glance.mjs` | observe | The pending approval's text, whether a turn runs, the last four entries. What the watcher reads. |
| `approvalbody.mjs` | observe | The whole "What it wants to do" of the pending approval (a commit's message and files, an item's new text). Read it before answering. |
| `answer.mjs` | click | Presses `ANSWER=approve` or `deny` on the pending approval and returns what it said before the press. |
| `openconv.mjs` | click | Opens the conversation whose row contains `CONV`, when someone in the window opened another one. |

Before approving a `git_commit`, compare its files and message with the host's own `git diff`: the agent's edits are
already on the host by then. Clear an environment variable with `$env:ANSWER = $null`.

Two things `api.key` does NOT do in the real window: `Control+a` and `Backspace` were both ignored in an
input. To clear a field, set its value and dispatch an `input` event, and say in the record that you did.

`tools/release/get-release.sh vX.Y.Z <workdir>` downloads a release from the update channel, verifies it,
keeps a backup tag of the installed image, loads the new one and smoke-tests it in a throwaway container.
It stops BEFORE installing; the install is the release's own `install-windows.ps1`.
