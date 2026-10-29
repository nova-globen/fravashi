# tools/netfault

A repeatable network fault check for the Fravashi studio container (backlog 228, slice A of #219; plan @49 under
@48). It breaks the network of the studio container on purpose so a host session can watch what the product does,
and puts it back afterwards. It is a HOST tool: an agent inside the studio has no container engine and cannot run it.

```powershell
.\tools\netfault\netfault.ps1 dns-break                 # F1: the container's resolver points at 192.0.2.1
.\tools\netfault\netfault.ps1 restore                   # put back anything an earlier run left broken
.\tools\netfault\netfault.ps1 link-down -Seconds 30     # F3: no network for 30 s, then reconnected
.\tools\netfault\netfault.ps1 link-down -Seconds 300    # F4: five minutes
```

F2 (a VPN reconnect) is `dns-break`, then `link-down -Seconds 15`, then `restore`.

It runs under Windows PowerShell 5.1 and PowerShell 7 (both checked on 2026-10-08 with a one second cut). Until then 5.1 refused with
"is not running" for a container that was running, because it read the engine's JSON array as one object.

A `link-down` also cuts the window's own connection to the studio (the published port goes with the network), so a check that
needs the page's circuit to stay up while only the studio's path to the host fails cannot be made with it (backlog 264).

## What each mode does, and how it is undone

| Mode | Fault | How | Restored by |
|---|---|---|---|
| `dns-break` | F1 | As root in the container: copies `/etc/resolv.conf` to `/etc/resolv.conf.netfault` and writes `nameserver 192.0.2.1` (the swap #218 proved). Then times `getent hosts api.anthropic.com` inside. Refuses to run twice. | `restore`: puts the saved file back while the broken line is still there; if the container restarted meanwhile (a start regenerates the resolver from the host) it keeps the fresh file and drops the saved one. |
| `link-down N` | F3, F4 | Reads the container's network from `wslc inspect --type container`, records it in `%TEMP%\fravashi-netfault-<container>.json`, runs `wslc network disconnect <net> <container>`, waits N seconds, then `wslc network connect <net> <container>`. | Itself, in a `finally` block (Ctrl+C included). A killed PowerShell leaves the state file, and `restore` reconnects from it. |
| `restore` | none | Reconnects a recorded network, puts a saved resolver back, and reports whether the container resolves again. | n/a |

While the link is down, the window's connection to the studio and every model call are cut too, which is what F3 and
F4 should look like. The studio's own DNS watch (#218) does not react to either mode: it acts only when the HOST's DNS
servers change, and nothing here changes them.

## What it will not do

- **Any container but the studio's.** The target is `containerName` from
  `%LOCALAPPDATA%\Fravashi\launcher.json` (default `fravashi`). `-Container` exists only to confirm it; any other name
  is refused, and so is a container that is not running.
- **Anything on the host's network.** Every engine call goes to `& "$env:ProgramFiles\WSL\wslc.exe"` and names that
  container. Nothing here touches the host's adapters, DNS, routes, firewall or VPN, and nothing takes such an argument.
- **Another engine.** It drives wslc only (ADR 0029). On Docker or Podman, a link-down would be the same two
  `network` verbs, untried.

## Measured before it was written

The host measured on the dev host on 2026-10-07 with wslc 3.0.1 (recorded on #228): `wslc network disconnect bridge <c>`
and `connect` both exit 0 on a running container, name resolution fails while disconnected and works again after, and
a published port answers again immediately after the reconnect. So @48's fallback (a black-hole resolver plus a dead
proxy) is not needed on this engine.

Run it when no agent turn is in the middle of something you care about, and record each run in `docs/evidences/`.
