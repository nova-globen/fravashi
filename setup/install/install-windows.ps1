#!/usr/bin/env pwsh
# Installs Nova Globen Fravashi on Windows from a RELEASE bundle: loads the studio image from
# the bundled fravashi-image.tar into the detected container runtime, places the launcher (fravashi.exe) in
# %LOCALAPPDATA%\Programs\Fravashi, puts that folder on the USER PATH so `fravashi` runs from any terminal (F9), and
# creates Start Menu + Desktop shortcuts. Run from inside the
# bundle folder (the one holding fravashi-image.tar and launcher/). This is the offline/local-tar installer. The
# network installer is the release web installer under setup/webinstall (FravashiSetup.exe); the pre-merge
# setup/install.ps1 was retired (backlog 118).
#
# An upgrade closes the running fravashi.exe gracefully (hot exit) and replaces it. The DATA folder is not touched
# here, and neither is anything an install from before the internal rename still carries (its data folder, container,
# image and database names): fravashi.exe moves and renames those itself at its first start, with the container
# stopped and a backup taken first (Fravashi.Launcher/DataFolderMove and FormerInstallMigration,
# docs/plans/rename-internal-names.md), so no former name has to be known here. Every step is idempotent, so running
# this twice is safe.
# This script SHIPS INSIDE the bundle, so it is normally run from the folder it was downloaded into.
# Releases come only from a pushed, CI-green tag: built on the dev host by setup/release/build-release-local.ps1 and
# published on the public repository's GitHub Releases. See the releasing-fravashi skill.
#
# Runtime-agnostic (ADR 0009): the product runs on whatever engine is installed rather than hard-coding
# Docker. Precedence mirrors Fravashi.Launcher/ContainerRuntimeDetector - an explicit pin wins, otherwise the
# first installed engine in the order docker -> podman -> wslc is used.
param(
    # Force a container runtime (docker|podman|wslc); default: honour a launcher.json pin, else auto-detect.
    [string]$Runtime = "",
    # Never show the UAC prompt for the firewall rule (FravashiSetup.exe /quiet); an elevated run still sets it.
    [switch]$NoElevationPrompt
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

# Say hello FIRST: a silent start reads as "nothing happens" when a later step hangs (a wedged
# container engine made exactly that happen on 2026-08-01).
Write-Host "Nova Globen Fravashi installer starting (bundle: $here) ..."

# The install's data folder. One still in its folder from before the rename is not looked up here: fravashi.exe moves
# that folder at its first start and settles its launcher.json there (FormerInstallMigration clears the image pin with
# the image name), and the engine is detected below without its runtime pin.
$productHome = Join-Path $env:LOCALAPPDATA "Fravashi"

# The executable for a runtime name. wslc is installed at %ProgramFiles%\WSL\wslc.exe and WSL does not
# put that folder on PATH (measured on wslc 3.0.1, 2026-10-04), so look there when PATH has none, as
# Fravashi.Launcher/WslcExecutable does. Every other name is used as it is.
function Resolve-RuntimeCli([string]$name) {
    if ($name -eq "wslc" -and -not (Get-Command wslc -ErrorAction SilentlyContinue)) {
        $installed = Join-Path $env:ProgramFiles "WSL\wslc.exe"
        if (Test-Path $installed) { return $installed }
    }
    return $name
}

# Probe a runtime the way the launcher does: the CLI must be installed and answer `<cli> version`
# (exit 0). BOUNDED: a wedged engine pipe can hang `version` forever, so the probe runs as a job
# with a 20s timeout and a hung engine reads as unavailable, with a message instead of silence.
function Test-Runtime([string]$name) {
    $cli = Resolve-RuntimeCli $name
    if (-not (Get-Command $cli -ErrorAction SilentlyContinue)) { return $false }
    Write-Host "Probing container runtime '$cli' ..."
    $job = Start-Job -ScriptBlock { param($c) & $c version *> $null; $LASTEXITCODE } -ArgumentList $cli
    if (-not (Wait-Job $job -Timeout 20)) {
        Stop-Job $job; Remove-Job $job -Force
        Write-Host "'$cli' did not answer within 20s (engine hung or starting up); treating it as unavailable."
        return $false
    }

    $exit = Receive-Job $job
    Remove-Job $job -Force
    return ($exit -eq 0)
}

# A runtime whose CLI is installed but whose engine does not answer is usually a stopped machine, which is
# what every reboot leaves behind on Podman. Podman is the only engine where starting one is meaningful and
# scriptable (Docker has no CLI that starts its own engine, wslc starts its distro on demand), so only Podman
# is attempted. NEVER `podman machine init`: that downloads a multi-gigabyte image and picks CPU/memory on the
# user's behalf. BOUNDED the same way the probe is, and the job is stopped rather than left to hang the install.
function Start-RuntimeMachine([string]$cli) {
    if ($cli -ne "podman") { return $false }
    if (-not (Get-Command $cli -ErrorAction SilentlyContinue)) { return $false }

    $machines = @()
    try { $machines = @(podman machine list --format json | ConvertFrom-Json) } catch { $machines = @() }
    if ($machines.Count -eq 0) {
        Write-Host "Podman is installed but has no machine. Create one yourself with 'podman machine init'"
        Write-Host "(it downloads a multi-gigabyte image and picks CPU, memory and disk), then re-run this installer."
        return $false
    }

    $target = $machines | Where-Object { $_.Default } | Select-Object -First 1
    if (-not $target -and $machines.Count -eq 1) { $target = $machines[0] }
    if (-not $target) {
        Write-Host "Podman has several machines and none is marked default; start the right one by hand."
        return $false
    }
    if ($target.Running -or $target.Starting) {
        Write-Host "The Podman machine '$($target.Name)' says it is up but Podman is not answering; it looks wedged."
        return $false
    }

    Write-Host "Starting the Podman machine '$($target.Name)' ..."
    $job = Start-Job -ScriptBlock { param($m) & podman machine start $m *> $null; $LASTEXITCODE } -ArgumentList $target.Name
    if (-not (Wait-Job $job -Timeout 180)) {
        Stop-Job $job; Remove-Job $job -Force
        Write-Host "The Podman machine did not start within 3 minutes; cancelled it rather than hanging the install."
        return $false
    }

    $exit = Receive-Job $job
    Remove-Job $job -Force
    return ($exit -eq 0)
}

# The probe, plus one bounded attempt to start a stopped machine when the CLI is there but the engine is not.
function Test-RuntimeOrStart([string]$name) {
    if (Test-Runtime $name) { return $true }
    if (-not (Get-Command (Resolve-RuntimeCli $name) -ErrorAction SilentlyContinue)) { return $false }
    if (-not (Start-RuntimeMachine $name)) { return $false }
    return (Test-Runtime $name)
}

# Honour an existing launcher.json 'runtime' pin so the image loads into the same engine `fravashi` will drive,
# UNLESS that engine is not installed at all. Such a pin is stale, not a choice (2026-10-04: podman was
# removed from the dev host for wslc and the pin kept every install and start on the missing engine), so it
# is dropped here, as the launcher's detector also ignores it, and the installed engine is found instead.
$launcherJson = Join-Path $productHome "launcher.json"
# The bridge release (docs/plans/rename-internal-names.md): an install whose data is still in its folder from before the
# rename keeps its engine pin there until fravashi.exe moves the folder, and the image must load into THAT engine. The one
# former name this script knows, for this lookup only; the cleanup release removes it.
$formerLauncherJson = Join-Path (Join-Path $env:LOCALAPPDATA "DidaDeck") "launcher.json"
if (-not (Test-Path $launcherJson) -and (Test-Path $formerLauncherJson)) { $launcherJson = $formerLauncherJson }
if (-not $Runtime -and (Test-Path $launcherJson)) {
    try {
        $pinnedConfig = Get-Content -Raw $launcherJson | ConvertFrom-Json
        $pinned = $pinnedConfig.runtime
        if ($pinned -and -not (Get-Command (Resolve-RuntimeCli $pinned) -ErrorAction SilentlyContinue)) {
            Write-Host "launcher.json names the '$pinned' engine, which is not installed; detecting the installed one instead."
            $pinnedConfig.runtime = $null
            $pinnedConfig | ConvertTo-Json -Depth 10 | Set-Content $launcherJson
        } elseif ($pinned) {
            $Runtime = $pinned
        }
    } catch { }
}

if ($Runtime) {
    if (-not (Test-RuntimeOrStart $Runtime)) {
        Write-Host ""
        Write-Host "The container runtime '$Runtime' is not answering. If it is normally fine, its"
        Write-Host "machine/service is likely stuck: try 'podman machine stop' + 'podman machine start'"
        Write-Host "(or 'wsl --shutdown' from an elevated prompt, or a reboot), then re-run this installer."
        Write-Host ""
        throw "The requested container runtime '$Runtime' is not available."
    }
} else {
    foreach ($candidate in @("docker", "podman", "wslc")) {
        if (Test-RuntimeOrStart $candidate) { $Runtime = $candidate; break }
    }
}
if (-not $Runtime) {
    throw "No container runtime found. Install Docker Desktop, Podman, or WSLC, then re-run."
}

# From here on the engine is invoked through its executable, which for wslc may be a full path.
$engine = Resolve-RuntimeCli $Runtime

# The script sits inside the bundle it installs, beside fravashi-image.tar and launcher/. It used to
# fall back to a locally built bundle under setup/dist when run from its source location in the repo;
# that path is gone with the local builder, and keeping it would have pointed a confused user at a
# folder that no longer exists.
$bundleRoot = $here
if (-not (Test-Path (Join-Path $bundleRoot "fravashi-image.tar"))) {
    throw @"
fravashi-image.tar was not found beside this script.

Run this installer from inside the bundle you downloaded, not from the repository. The web installer
(setup/webinstall/webinstall.ps1) does all of this for you. By hand:
  1. Open the release on https://github.com/nova-globen/fravashi/releases; its release-manifest.json
     lists every file.
  2. Download fravashi-win-x64.tar.gz and fravashi-image-<arch>.tar.gz for THIS machine's
     architecture (check it with: podman info --format '{{ .Version.OsArch }}'). An older release's
     image may come in fravashi-image-<arch>.tar.partNN.gz parts instead.
  3. Extract the win-x64 archive, then (joining any image parts IN ORDER) name the image file exactly
     fravashi-image.tar, leaving it gzip compressed:
       cmd /c \"copy /b fravashi-image-<arch>.tar.part*.gz fravashi-image.tar\"
  4. Put fravashi-image.tar beside this script and run it again.

Releases are built only from a pushed, CI-green tag.
"@
}

Write-Host "Loading the Fravashi image via $Runtime ..."
& $engine load --input (Join-Path $bundleRoot "fravashi-image.tar")
if ($LASTEXITCODE -ne 0) { throw "$Runtime load failed." }

# The tarball may carry the image under a registry namespace the launcher never looks at:
# docker-built bundles say docker.io/library/fravashi:latest, podman resolves the launcher's bare
# fravashi:latest to localhost/fravashi:latest. Without this retag the launcher sees no newer image
# and KEEPS THE OLD CONTAINER while the new image sits loaded and invisible (found 2026-08-08, the
# first CI-built install). Retag whatever namespaced fravashi:latest is newest onto the bare name.
#
# And NEVER rmi the namespaced name afterwards. An earlier version did, and on podman that deleted
# the freshly loaded image entirely (found 2026-08-13, the v0.9.17 install): podman treats
# localhost/fravashi:latest and the bare fravashi:latest as the SAME reference, so the tag was a
# no-op and the rmi removed the image's only name, leaving the launcher refusing to start with "image not
# present". A leftover alias like docker.io/library/fravashi:latest on the same image id is
# harmless; a deleted image is not. The dangling-only prune below never touches tagged images.
# wslc takes no Go templates: its list is one JSON object per line (measured on 3.0.1).
if ($Runtime -eq "wslc") {
    $names = & $engine images --format json | ForEach-Object {
        try { $row = $_ | ConvertFrom-Json; "$($row.Repository):$($row.Tag)" } catch { }
    }
} else {
    $names = & $engine images --format "{{.Repository}}:{{.Tag}}"
}
$loaded = $names | Where-Object { $_ -match "/fravashi:latest$" } | Select-Object -First 1
if ($loaded) {
    & $engine tag $loaded fravashi:latest
    if ($LASTEXITCODE -eq 0) { Write-Host "Retagged $loaded as fravashi:latest for the launcher." }
}

# Belt and braces: whatever the namespace dance did, the name the launcher reads MUST resolve now.
# (image inspect, not podman-only image exists: this script also runs against docker and wslc.)
& $engine image inspect fravashi:latest *> $null
if ($LASTEXITCODE -ne 0) {
    throw "fravashi:latest does not resolve after the load; the launcher would refuse to start. Re-run the installer."
}

# Housekeeping: prune dangling <none> images left by earlier installs (dangling only; tagged and in-use
# images are never touched). The image this load just superseded is still referenced by the existing
# container, so it survives this prune; the launcher retires it after recreating the container from the new image.
& $engine image prune -f *> $null
if ($LASTEXITCODE -ne 0) { Write-Host "Note: '$Runtime image prune -f' failed; dangling leftovers stay on disk (harmless)." }

# A local-bundle install IS the trusted release event: clear the pinned image digest so the launcher
# re-pins the freshly loaded image on the next start (trust-on-first-use, ADR 0007). Without this the
# integrity check would refuse the new image against the previous install's pin.
$launcherJsonPath = Join-Path $productHome "launcher.json"
if (Test-Path $launcherJsonPath) {
    try {
        $config = Get-Content -Raw $launcherJsonPath | ConvertFrom-Json
        if ($config.PSObject.Properties.Name -contains 'pinnedImageDigest' -and $config.pinnedImageDigest) {
            $config.pinnedImageDigest = $null
            $config | ConvertTo-Json -Depth 10 | Set-Content $launcherJsonPath
            Write-Host "Cleared the previous image digest pin; fravashi re-pins the new image on next start."
        }
    } catch {
        Write-Host "Note: could not update $launcherJsonPath ($_). If fravashi refuses with an integrity"
        Write-Host "mismatch, remove the 'pinnedImageDigest' line from that file and start fravashi again."
    }
}

$target = Join-Path $env:LOCALAPPDATA "Programs\Fravashi"
New-Item -ItemType Directory -Force -Path $target | Out-Null

# An upgrade over a live install: the resident fravashi.exe holds a lock on the executable about to be replaced. Close it
# gracefully first (the studio window flushes the editor's hot-exit backups on close). Scoped to processes running FROM
# the install folder, so a developer's separate debug instance is never touched.
$running = Get-Process -Name 'fravashi' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($target, [System.StringComparison]::OrdinalIgnoreCase) }
foreach ($process in $running) {
    [void]$process.CloseMainWindow()
    if (-not $process.WaitForExit(5000)) { $process.Kill() }
    [void]$process.WaitForExit(5000)
    Write-Host "Stopped the running fravashi.exe so it can be replaced."
}
Start-Sleep -Milliseconds 300

Copy-Item (Join-Path $bundleRoot "launcher\*") $target -Recurse -Force
$fravashiExe = Join-Path $target "fravashi.exe"
if (-not (Test-Path $fravashiExe)) {
    throw "fravashi.exe is not in this bundle's launcher folder; this bundle is not a Fravashi release. Nothing was removed."
}

# --- F9: the install dir on the USER PATH, idempotently (no duplicate, user scope only) --------------------------------
# Mirrors Fravashi.Launcher/UserPathEditor: case-insensitive, trailing-separator-insensitive.
function Get-NormalizedPath([string]$dir) { return $dir.Trim().TrimEnd('/', '\').ToUpperInvariant() }

function Send-EnvironmentChanged {
    # Broadcast WM_SETTINGCHANGE so already-open shells can pick up the change (cheap best-effort).
    if (-not ([System.Management.Automation.PSTypeName]'Fravashi.Win32').Type) {
        Add-Type -Namespace Fravashi -Name Win32 -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError=true, CharSet=System.Runtime.InteropServices.CharSet.Auto)]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out System.IntPtr lpdwResult);
'@
    }
    $HWND_BROADCAST = [System.IntPtr]0xffff
    $WM_SETTINGCHANGE = 0x1A
    $result = [System.IntPtr]::Zero
    [void][Fravashi.Win32]::SendMessageTimeout($HWND_BROADCAST, $WM_SETTINGCHANGE, [System.IntPtr]::Zero, 'Environment', 2, 5000, [ref]$result)
}

function Set-UserPath([string]$add) {
    $current = [Environment]::GetEnvironmentVariable('Path', 'User')
    $kept = @()
    if ($current) { $kept = @($current.Split(';') | Where-Object { $_ -ne '' }) }
    $present = @($kept | Where-Object { (Get-NormalizedPath $_) -eq (Get-NormalizedPath $add) }).Count -gt 0
    if ($present) {
        Write-Host "PATH already contains $add (no change)."
        return
    }
    $kept += $add
    [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
    Write-Host "Added $add to your user PATH."
    Send-EnvironmentChanged
}
Set-UserPath -add $target

# --- "Open with Fravashi" Explorer context menu (plan 2b-ii): HKCU only, no elevation ---------------------------------
# %V is the right-clicked folder (Directory) or the browsed folder (Directory\Background). Idempotent: the entries are
# rewritten each run. Removal is manual until an uninstaller exists (delete the two Fravashi keys).
foreach ($shellBase in @('Software\Classes\Directory\shell', 'Software\Classes\Directory\Background\shell')) {
    $menuKey = "HKCU:\$shellBase\Fravashi"
    if (Test-Path $menuKey) { Remove-Item -Recurse -Force $menuKey }
    New-Item -Path "$menuKey\command" -Force | Out-Null
    Set-ItemProperty -Path $menuKey -Name 'MUIVerb' -Value 'Open with Fravashi'
    Set-ItemProperty -Path $menuKey -Name 'Icon' -Value $fravashiExe
    Set-ItemProperty -Path "$menuKey\command" -Name '(default)' -Value "`"$fravashiExe`" --open `"%V`""
}
Write-Host "Added the 'Open with Fravashi' folder context menu."

# --- Shortcuts: target fravashi.exe (a WinExe, so launching from a shortcut shows no console window) --------------------
# Named for the product, which since the rename is also the command (Fravashi). Written over each run, so an upgrade
# never leaves two.
$shell = New-Object -ComObject WScript.Shell
$shortcutDirs = @(
    [Environment]::GetFolderPath('Desktop'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs')
)
foreach ($dir in $shortcutDirs) {

    $shortcut = $shell.CreateShortcut((Join-Path $dir 'Fravashi.lnk'))
    $shortcut.TargetPath = $fravashiExe
    $shortcut.WorkingDirectory = $target
    $shortcut.IconLocation = $fravashiExe
    $shortcut.Description = 'Nova Globen Fravashi'
    $shortcut.Save()
}

# --- The firewall rule (backlog 374): the ONE step that needs elevation ------------------------------------------------
# firewall-rule.ps1 gives fravashi.exe its own inbound rule, so a first start on a locked screen is not silently blocked.
# Copied beside fravashi.exe so the advice's command works after the bundle is gone. Run directly when this install is
# already elevated, else through one UAC prompt inside a job with a time limit: an older FravashiSetup.exe (version-
# agnostic by design) runs this script without -NoElevationPrompt, and an install must never hang on a prompt nobody
# answers. Nothing here fails the install: without the rule the first start meets Windows' own prompt, as before.
$firewallScript = Join-Path $target 'firewall-rule.ps1'
$firewallWhy = $null
try {
    Copy-Item (Join-Path $bundleRoot 'firewall-rule.ps1') $firewallScript -Force
    $env:FRAVASHI_FIREWALL_FUNCTIONS_ONLY = '1'
    . $firewallScript
    $env:FRAVASHI_FIREWALL_FUNCTIONS_ONLY = $null
    $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $firewallArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$firewallScript`" -Program `"$fravashiExe`""
    switch (Get-FirewallStepMode -Elevated $elevated -NoElevationPrompt $NoElevationPrompt.IsPresent) {
        'direct' {
            & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $firewallScript -Program $fravashiExe
            if ($LASTEXITCODE -ne 0) { $firewallWhy = "it failed with exit code $LASTEXITCODE" }
        }
        'skip' { $firewallWhy = 'this install may not ask for administrator rights' }
        'prompt' {
            Write-Host "Windows asks once for administrator rights: only to let the Fravashi studio reach fravashi.exe through"
            Write-Host "Windows Firewall (one inbound rule for fravashi.exe, local networks only). Everything else is already installed."
            $job = Start-Job -ScriptBlock {
                param($ps, $arguments)
                try { (Start-Process -FilePath $ps -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru).ExitCode }
                catch { 'declined' }
            } -ArgumentList $windowsPowerShell, $firewallArgs
            if (Wait-Job $job -Timeout 300) {
                $outcome = Receive-Job $job
                # Declined, or never shown: on a locked console Windows refuses the elevation at once (measured on the
                # test VM, 2026-10-30), with nobody asked.
                if ($outcome -eq 'declined') { $firewallWhy = 'the administrator prompt was declined or could not be shown' }
                elseif ($outcome -ne 0) { $firewallWhy = "it failed with exit code $outcome" }
                else { Write-Host "Added the firewall rule for fravashi.exe." }
            }
            else {
                Stop-Job $job
                $firewallWhy = 'nobody answered the administrator prompt within five minutes'
            }
            Remove-Job $job -Force
        }
    }
}
catch {
    $env:FRAVASHI_FIREWALL_FUNCTIONS_ONLY = $null
    $firewallWhy = "the step failed: $_"
}
if ($firewallWhy) {
    if (Get-Command Get-FirewallAdvice -ErrorAction SilentlyContinue) {
        Get-FirewallAdvice -Program $fravashiExe -Why $firewallWhy | ForEach-Object { Write-Host $_ }
    }
    else {
        Write-Host "The firewall rule for fravashi.exe was not set ($firewallWhy). If Windows Firewall asks about fravashi.exe at the first start, choose Allow."
    }
}

Write-Host ""
Write-Host "Nova Globen Fravashi is installed. Launch Fravashi from the Start Menu or the Desktop shortcut,"
Write-Host "or run 'fravashi' from a new terminal. Commands:"
Write-Host "  fravashi              Start Fravashi: splash, then the studio window (stays in the tray)."
Write-Host "  fravashi onboarding   Open the onboarding / settings wizard."
Write-Host "  fravashi stop         Stop the container (the reserved port is kept)."
Write-Host "  fravashi exit         Close the resident window/tray process (the container keeps running)."
Write-Host "  fravashi status       Show container state, port, URL, data dir, image version."
Write-Host "  fravashi website      Open the product website."
Write-Host "  fravashi logs         Print recent container logs (--tail N)."
Write-Host "  fravashi help         Full command reference."
Write-Host ""
Write-Host "Note: open a NEW terminal so the updated PATH takes effect."
