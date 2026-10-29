# Nova Globen Fravashi web installer (FravashiSetup.exe)

One small `FravashiSetup.exe` that installs a Fravashi release on any Windows machine: it detects the machine's
architecture, reads the signed release manifest on the update channel, downloads the files that manifest names,
verifies them, and runs that release's own `install-windows.ps1`.

**One source since F1.** The engine reads
`<channel>/<-Version, or latest>/release-manifest.json` on the update channel of the build's service host
(`https://nova-globen.se/fravashi/updates` in production; the EXE passes its own build's channel as `-DownloadBase`),
verifies `release-manifest.sig` against the release key every fravashi.exe embeds (written into the engine, and held
equal to `setup/release/manifest-signing-public.pem` by `WebInstallDownloadsTests`), downloads only the URLs that
signed manifest names, and checks each whole file against the manifest's sha256 and the folder's `SHA256SUMS.txt`.
The channel redirects each file to the asset on the public repository's GitHub Releases. Everything is public, so no
credential is ever involved. The forge it could also install from retired at F1, and with it `/forge`, `/tag:`,
`/tokenfile:` and `/variant:` (refused with that reason), the token chain and exit code 3.

When the manifest answers 404 the engine prints `No Fravashi release is published at <folder> yet (HTTP 404).` (for a
404 only, never for a network failure or a bad signature), and the EXE ends with exit code 6, saying so.

`FravashiSetup.exe` is an asset of every release, built from the same tag for the same service host by
`setup/release/build-release-local.ps1`.

## Files

- `webinstall.ps1` (this folder) - the engine, and the only place the install logic lives. The EXE embeds this
  file and runs it, and it also runs standalone from a terminal. `-ResolveOnly` verifies the manifest and prints what
  it WOULD download, then stops, which is how a change here is driven against the real channel without a
  multi-gigabyte download.
- `src/Fravashi.WebSetup` - the EXE (`FravashiSetup.exe`, a WinForms WinExe on net10.0-windows): the wizard
  and the `/quiet` entry point, nothing else.
- `src/Fravashi.WebSetup.Core` - everything the EXE decides, as a cross-platform library: the command line, the
  embedded engine, how the engine's output becomes progress, and what an exit code means.
  `tests/Fravashi.WebSetup.Tests` drives it on Linux.

## How the EXE runs the engine

- The engine is EMBEDDED from this folder's `webinstall.ps1` (a test holds the resource byte for byte equal to
  the file), written at run time to a fresh folder under `%TEMP%`, and run with Windows PowerShell 5.1 by its full
  path, `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -ExecutionPolicy
  Bypass -File <engine> -DownloadBase <channel> [-Version <x.y.z>]`. Never `powershell` from PATH: PATH is what broke
  1.0.2. The arguments are a list, never a concatenated command line, and the version is a closed alphabet (letters,
  digits, `.`, `_`, `-`, starting with a digit). Afterwards the EXE deletes the folder, and a removal that fails is
  named in the log and the verdict, never passed over.
- stdin is a pipe closed at once, so the engine knows nobody can answer a prompt and never waits at one (backlog 276).
- Progress comes from the engine's own lines (looking up, downloading `<file>`, verifying, unpacking, installing,
  installed), and is MEANT to take a live percentage from curl's progress bar on stderr. The verdict comes from the
  exit code: 0 with the engine's "is installed." line is installed; 1 is a failure whose reason is the engine's last
  line, or "nothing published there" when the channel answered 404 for the manifest; anything else did not come from
  the engine.
- NOT YET MEASURED through the EXE: the engine with its stderr redirected by the EXE. Windows PowerShell 5.1 can turn
  a native program's stderr into error records when its own stderr is redirected, which under the engine's `Stop`
  would end it at curl's first progress-bar write. `Invoke-CurlDownload` runs curl with `Continue` as a guard, and that
  guard covers curl ONLY: the engine's `tar` and the release's own `install-windows.ps1` still run under `Stop` on the
  same redirected stderr. The first-install drill on the test VM through the EXE settles it, together with whether the
  live percentage arrives and the code page the EXE decodes the engine's output with.
- Every line goes to a log, `%TEMP%\FravashiSetup-<utc time>.log` unless `/log:` names another, and the wizard's last
  page names it.
- It runs `asInvoker` (an explicit manifest), never elevated.

## Unattended (silent) install

```
FravashiSetup.exe /quiet [/version:<x.y.z, default latest>] [/log:<path>]
```

No window. The engine's lines go to the log and to the parent console when there is one. It is a GUI program, so
wait for it explicitly, for example from PowerShell:

```
$p = Start-Process .\FravashiSetup.exe -ArgumentList '/quiet','/version:1.0.0','/log:C:\temp\setup.log' -PassThru; $p.WaitForExit(); $p.ExitCode
```

Exit codes: 0 installed, 1 install failed (the log says why), 2 bad arguments or only the usage asked for with `/?`
(nothing started), 4 Windows PowerShell did not run the engine, 5 cancelled, 6 no release is published at that
version. 3 is retired and never reused.

## Relationship to the other installers

- `setup/install/install-windows.ps1` - the REAL installer. The engine downloads the bundle and runs the copy
  inside it, so install logic always matches the release being installed.

## History, in short (git keeps the detail)

- **Inno Setup, 1.0.0 to 1.0.2 (retired 2026-10-13, backlog 296)**, replaced by our own .NET bootstrapper (2.0.0)
  rather than buying a commercial license (owner, 2026-10-11, recorded on #296).
- **Fixed on the way:** curl rejecting a config line (`dd62951`), a byte-order mark in front of what Windows PowerShell
  5.1 pipes to a native program (1.0.1, backlog 255), whichever `tar` came first on PATH (1.0.2, backlog 258), dropped
  transfers and passing server errors retried (backlogs 272 and 276), PowerShell 7's `PSModulePath` reaching 5.1
  (2.0.1, backlog 296).
- **2.1.0 (#370):** the downloads service by default. **F1:** the update channel only, and the EXE ships with every
  release.
