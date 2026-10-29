# git's own stderr for remote operations (backlog 231)

Each file is the stderr of one failed `git fetch`, `git pull` or `git push`, read by `GitRemoteStderrTests`.

**Measured** (every file without the `source-` prefix): captured on 2026-10-07 in the studio container (git 2.43.0,
curl 8.5, `GIT_TERMINAL_PROMPT=0`) by an agent session working on #231:

- a name that cannot resolve (`nonexistent-host.invalid`, over https and `git://`);
- a closed port on loopback (`127.0.0.1:9`), an unroutable IPv6 documentation address (`2001:db8::1`) and a
  black-holed TEST-NET address (`192.0.2.1`, which took 134 seconds to fail). curl 8.5 words all three
  "Couldn't connect to server"; only the elapsed time differs;
- a scripted HTTP server on loopback that answered 401, 403, 404 or 500, reset or closed the connection before the
  push sent anything (during ref discovery), or advertised refs and then reset, closed or stalled AFTER the pack was
  sent (curl's low-speed limit set to 1000 bytes for 3 seconds for the stall);
- a local bare repository that had moved ahead (non-fast-forward), one with a `pre-receive` hook that exits 1, a
  diverged `pull --ff-only`, and a path that is not a repository.

**Source text** (`source-` prefix): NOT measured. The container has no ssh client and no TLS server to break, so
these are the messages as OpenSSH, git and curl write them, kept so the classifier is held to them too. A future
capture that differs replaces the file and drops the prefix.

The port numbers and paths are the ones the capture used; nothing reads them.
