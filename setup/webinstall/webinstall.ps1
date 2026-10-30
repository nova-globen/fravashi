# Nova Globen Fravashi web-install engine: detects this machine, downloads the matching assets, verifies them, and
# hands off to the bundle's own install-windows.ps1 (which does the real install: image load + retag,
# digest-pin clear, fravashi.exe placement, PATH, shortcuts, "Open with Fravashi" menu). It reads
# <channel>/<version or latest>/release-manifest.json on the update channel, verifies its signature against
# the release key every fravashi.exe embeds (the same check as the launcher's ReleaseManifestSignature), downloads the
# files from the URLs that signed manifest names, and checks each whole file against both the manifest's sha256 and the
# folder's SHA256SUMS.txt. Everything it reads is public, so no credential is ever involved. (The forge it could also
# install from retired at F1.)
#
# This is the engine INSIDE FravashiSetup.exe (src/Fravashi.WebSetup, which embeds this file and
# runs it with Windows PowerShell; see README.md beside this file), and it also runs standalone from a
# terminal:
#   ./webinstall.ps1                          # the newest release on the update channel (latest/)
#   ./webinstall.ps1 -Version 1.0.0           # a specific version
#   ./webinstall.ps1 -Arch arm64              # override the architecture detection
#
# Related-but-different files, so nobody merges them by mistake:
#   setup/install/install-windows.ps1   the real installer this engine downloads AND RUNS, so the
#                              install logic always matches the release being installed.
param(
    # A version on the update channel (1.0.0, no v); empty is its latest/ folder.
    [string]$Version = "",
    # The update channel. The default is production's; the setup EXE passes its own build's channel.
    [string]$DownloadBase = "https://nova-globen.se/fravashi/updates",
    # amd64 | arm64; empty detects the machine. The IMAGE must match the container runtime's
    # architecture; the launcher is win-x64 for everyone (Windows on ARM runs it under emulation).
    [ValidateSet("", "amd64", "arm64")]
    [string]$Arch = "",
    # Forwarded to install-windows.ps1 (docker | podman | wslc); empty auto-detects there.
    [string]$Runtime = "",
    # Forwarded to install-windows.ps1: never show the UAC prompt for the firewall rule (backlog 374). The setup EXE
    # passes it for /quiet; an elevated run still sets the rule.
    [switch]$NoElevationPrompt,
    # Keep the downloaded files instead of deleting the work folder (debugging a bad download).
    [switch]$KeepDownloads,
    # Resolve the release and print what WOULD be downloaded, then stop. Exists so a change to this
    # engine can be driven against the real channel without a multi-gigabyte download and an install:
    # it exercises the manifest lookup, its signature and the asset matching, which is where the
    # changeable logic is. It proves nothing about the download, join, verify or install steps.
    [switch]$ResolveOnly,
    # Hold the console open on the way out, for somebody who started the engine in a window of its own
    # that would otherwise close with the failure text still in it. The setup EXE does NOT pass it: it
    # reads the engine's output itself and gives it a closed stdin, so nothing would answer anyway.
    [switch]$Pause
)
$ErrorActionPreference = "Stop"

# A slice (or any single asset) whose transfer ends early is worth fetching again: the connection
# dropped, not the release (backlog 272, seen twice on the test VM: the exact same command installed
# clean on the next try), so a person on an ordinary flaky line used to have to notice the one failed
# file, rerun the whole installer by hand and redownload the other 800 MB. Get-CurlExitClass says WHICH
# kind of failure a curl exit code is:
#   Refusal   curl --fail's exit 22 (an HTTP status of 400 or more) WHEN the status itself is not one
#             of the transient ones below: 401, 403, 404 and any other 4xx curl reports this way. Never
#             retried here.
#   Transient the connection dropped mid-transfer (18 and the others curl uses for that), a resume
#             itself did not work (33, "HTTP range error"), or exit 22 carried a status of 500-599,
#             408 or 429 (backlog 276: statuses that MEAN a passing server or CDN problem rather than
#             a refusal, by HTTP's own semantics - not a set read off the VM that hit this, whose exit
#             22 was never read for a status at all; what was measured there is that the same slice
#             answered 206 to a separate range probe made right after the hung install was killed).
#             Worth trying again, with the same bound and backoff either way.
#   Other     our own bad arguments, DNS, "could not connect", disk: reported as itself (backlog 255),
#             never retried and never mistaken for a refusal.
# Both this and Invoke-CurlDownload right below it are placed here, ahead of the startup banner and
# everything else, so a test can dot-source this file with FRAVASHI_WEBINSTALL_FUNCTIONS_ONLY=1 and
# drive the retry decision directly (pointing $script:curlExe at a stand-in) with no other side
# effect: no banner printed, no network reached, nothing downloaded or installed for real.
$script:TransientCurlExits = @(18, 28, 35, 52, 55, 56)
$script:ResumeUnsupportedCurlExits = @(33)
$script:TransientHttpStatuses = @(408, 429)
$script:MaxTransientCurlAttempts = 5

# $HttpStatus is consulted only for exit 22: every other exit means no HTTP response was ever read, so
# a status would be meaningless there. It defaults to 0 ("unknown"), which classifies exit 22 as
# Refusal exactly as before this parameter existed (backlog 272): a status that cannot be read is not
# evidence of a passing problem, so the safer, pre-276 behavior is what a caller gets by not passing one.
function Get-CurlExitClass([int]$ExitCode, [int]$HttpStatus = 0) {
    if ($ExitCode -eq 22) {
        if (($HttpStatus -ge 500 -and $HttpStatus -le 599) -or $script:TransientHttpStatuses -contains $HttpStatus) {
            return "Transient"
        }
        return "Refusal"
    }
    if ($script:TransientCurlExits -contains $ExitCode -or $script:ResumeUnsupportedCurlExits -contains $ExitCode) {
        return "Transient"
    }
    return "Other"
}

# Runs curl (or whatever $script:curlExe names) and returns its exit code. Every download is public and passes no
# curl config. A caller that does pass a config line has it handed to curl in a short-lived FILE written as raw UTF-8
# bytes with no byte-order mark, and removed afterwards (never argv).
#
# It does NOT pipe the line into curl's stdin, which is what this did until 2026-10-08 (backlog 255): Windows
# PowerShell 5.1 encodes what it pipes to a native program with the CONSOLE's code page, and when that is UTF-8
# (Windows' "Use Unicode UTF-8 for worldwide language support", code page 65001) it puts a byte-order mark in front,
# which curl 8.21 reads as part of the first option and exits 2 ("config file option '<BOM>#' is unknown") before
# making any request. Starting curl through System.Diagnostics.Process does not escape it on 5.1: the process's
# StandardInput writer emits its encoding's preamble when the process starts. A file has no such layer. curl draws its
# progress bar on stderr, which is left alone, so it still appears in the console.
#
# A Transient exit code (per Get-CurlExitClass above) is retried, up to MaxTransientCurlAttempts total, with a short
# backoff and one line per retry, resuming (curl -C -) from whatever already landed in OutFile; the one exit that
# means the resume itself did not work (33) clears OutFile first so the next attempt starts the slice over rather
# than repeating the same refusal. A Refusal or an Other exit returns immediately, exactly as before this existed.
#
# `--write-out '%{http_code}'` (backlog 276) is what makes the HTTP status available to classify exit 22 by: curl
# still writes it after a failed request (the body, if any, went to --output, same as always), so capturing this
# call's stdout gets exactly that one short status line and nothing else. It is stashed in $script:lastHttpStatus
# (0 when no response was ever read, e.g. a DNS failure) for the caller to read.
#
# $ErrorActionPreference is Continue inside this function only (backlog 296). Windows PowerShell 5.1 can turn a native
# program's stderr into NativeCommandError records when its own stderr is redirected, and under the script's Stop the
# first progress-bar write would then end the engine. The setup EXE redirects stderr to read that bar. An install over
# a non-interactive SSH session went through without this on the test VM (2026-10-10), but the evidence does not record
# what that session's stderr was, so this is a guard for a path not yet measured, not a fix for one seen failing. It
# covers curl ONLY: tar below and the release's own install-windows.ps1 (which sets Stop itself and runs the container
# engine's load and tag) still run under Stop on the same stderr. curl's result is read from $LASTEXITCODE either way,
# and a .NET method that throws here still throws.
function Invoke-CurlDownload([string]$ConfigLine, [string]$OutFile, [string]$Url) {
    $ErrorActionPreference = "Continue"
    $baseArguments = @('--fail', '--location', '--progress-bar', '--write-out', '%{http_code}', '--output', $OutFile)
    $configFile = $null
    try {
        if ($ConfigLine) {
            $configFile = Join-Path ([System.IO.Path]::GetTempPath()) ('fravashi-curl-' + [guid]::NewGuid().ToString('N') + '.cfg')
            [System.IO.File]::WriteAllBytes($configFile, (New-Object System.Text.UTF8Encoding($false)).GetBytes($ConfigLine + "`n"))
        }

        for ($attempt = 1; $attempt -le $script:MaxTransientCurlAttempts; $attempt++) {
            $curlArguments = @()
            if ($configFile) { $curlArguments += @('--config', $configFile) }
            if ($attempt -gt 1) { $curlArguments += @('-C', '-') }
            $curlArguments += $baseArguments

            $statusOutput = & $script:curlExe @curlArguments $Url
            $exitCode = $LASTEXITCODE
            $httpStatus = 0
            if ($statusOutput) {
                $lastLine = (@($statusOutput) | Select-Object -Last 1).ToString().Trim()
                $parsedStatus = 0
                if ([int]::TryParse($lastLine, [ref]$parsedStatus)) { $httpStatus = $parsedStatus }
            }
            $script:lastHttpStatus = $httpStatus
            if ($exitCode -eq 0) { return 0 }

            $class = Get-CurlExitClass $exitCode $httpStatus
            if ($class -ne "Transient" -or $attempt -eq $script:MaxTransientCurlAttempts) { return $exitCode }

            if ($script:ResumeUnsupportedCurlExits -contains $exitCode) {
                Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
            }
            $maxAttempts = $script:MaxTransientCurlAttempts
            $backoffSeconds = [Math]::Min(8, [Math]::Pow(2, $attempt - 1))
            $statusNote = if ($httpStatus) { " (HTTP $httpStatus)" } else { "" }
            Write-Host "  (curl exit $exitCode$statusNote looked like a dropped transfer; retrying, attempt $($attempt + 1) of $maxAttempts, after $backoffSeconds s ...)"
            Start-Sleep -Seconds $backoffSeconds
        }
    } finally {
        if ($configFile) { Remove-Item -LiteralPath $configFile -Force -ErrorAction SilentlyContinue }
    }
}

# Whether this session has anybody who could answer a Read-Host prompt (backlog 276: a non-interactive
# SSH session hung at the token prompt for about ten minutes until the host killed it; the evidence
# names the session as "non-interactive" but does not record which of stdin's two shapes it had, so
# the claim below is about the EXPECTED shape of that kind of session, not a measurement of it - the
# VM install #276's acceptance still owes would measure it directly).
# [Console]::IsInputRedirected is true whenever stdin is a pipe rather than a real console, which is
# what an SSH exec with no pty hands a command: the pipe is open but nothing ever writes to it. Most
# tests override this with $script:ForceNonInteractiveForTest for a deterministic, table-driven style
# matching $script:curlExe's; at least one drives [Console]::IsInputRedirected itself, by giving the
# test's own child process a genuinely redirected stdin pipe.
function Test-IsNonInteractiveSession {
    if ($null -ne $script:ForceNonInteractiveForTest) { return [bool]$script:ForceNonInteractiveForTest }
    try { return [Console]::IsInputRedirected } catch { return $false }
}

function Wait-BeforeClosing {
    if (-not $Pause) { return }
    if (Test-IsNonInteractiveSession) {
        # -Pause asked to hold the window open for somebody to read it, but there is nobody who
        # could answer Read-Host here either (backlog 276: Die's "never blocks" guarantee has to
        # hold on every path that reaches it, this one included, or it is not a guarantee at all).
        Write-Host "(-Pause was set, but this session is not interactive; closing without waiting.)"
        return
    }
    [void](Read-Host "Press Enter to close this window")
}
function Die([string]$m) {
    Write-Host ""
    Write-Host $m -ForegroundColor Red
    Wait-BeforeClosing
    exit 1
}

# --- the downloads service (#370) ---------------------------------------------------------------------
# The release key's public half: setup/release/manifest-signing-public.pem, which every fravashi.exe embeds and
# Fravashi.Launcher.Releases.ReleaseManifestSignature verifies with. Written into this file because the engine runs
# alone (embedded in FravashiSetup.exe, or downloaded by itself); WebInstallDownloadsTests holds the two equal.
$script:ReleaseKeyPem = @"
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEpL4t4xmLEQhopcldaEX2oDlA8Olw
AGU/aQ5WMSNHTmltVPNwTnXGuW6z3R208B1bdpWw7dqLzbuLUU7/vvLatQ==
-----END PUBLIC KEY-----
"@

# The X and Y of a P-256 public key in SPKI PEM. Windows PowerShell 5.1's .NET Framework has no PEM or SPKI import, and
# the SPKI of an id-ecPublicKey on prime256v1 is a fixed 26-byte prefix and the uncompressed point 04||X||Y, so that
# exact shape is read and anything else refused.
function Get-P256PublicKeyPoint([string]$Pem) {
    $body = (@($Pem -split "`r?`n") | Where-Object { $_ -and -not $_.StartsWith("-----") }) -join ""
    $der = [Convert]::FromBase64String($body)
    $prefix = "3059301306072a8648ce3d020106082a8648ce3d030107034200"
    $head = (($der[0..25] | ForEach-Object { $_.ToString("x2") }) -join "")
    if ($der.Length -ne 91 -or $head -ne $prefix -or $der[26] -ne 4) { throw "the release key is not a P-256 public key in SPKI form." }
    return @{ X = [byte[]]$der[27..58]; Y = [byte[]]$der[59..90] }
}

# A DER ECDSA signature (SEQUENCE of INTEGER r, INTEGER s: what openssl dgst -sign writes and the launcher verifies as
# Rfc3279DerSequence) as the 64-byte r||s the .NET Framework's ECDsa.VerifyData takes. Null when it is not that shape.
function ConvertFrom-DerEcdsaSignature([byte[]]$Der) {
    if ($null -eq $Der -or $Der.Length -lt 8 -or $Der[0] -ne 0x30 -or $Der[1] -ge 0x80 -or ($Der[1] + 2) -ne $Der.Length) { return $null }
    $out = New-Object byte[] 64
    $pos = 2
    foreach ($half in 0, 1) {
        if ($pos + 2 -gt $Der.Length -or $Der[$pos] -ne 0x02) { return $null }
        $len = [int]$Der[$pos + 1]
        $pos += 2
        if ($len -lt 1 -or $pos + $len -gt $Der.Length) { return $null }
        $start = $pos
        $n = $len
        while ($n -gt 1 -and $Der[$start] -eq 0) { $start++; $n-- }
        if ($n -gt 32) { return $null }
        [Array]::Copy($Der, $start, $out, $half * 32 + (32 - $n), $n)
        $pos += $len
    }
    if ($pos -ne $Der.Length) { return $null }
    return , $out
}

# Verified, Unsigned or BadSignature: the launcher's ReleaseManifestSignature.Verify, over the manifest's exact bytes.
# The sidecar is left untyped on purpose: a [string] parameter turns $null into "", and no signature at all (Unsigned)
# is a different finding from an empty one (BadSignature), as in the launcher.
function Test-ReleaseManifestSignature([byte[]]$ManifestBytes, $Sidecar, [string]$PublicKeyPem = $script:ReleaseKeyPem) {
    if ($null -eq $Sidecar) { return "Unsigned" }
    try { $der = [Convert]::FromBase64String(([string]$Sidecar).Trim()) } catch { return "BadSignature" }
    $raw = ConvertFrom-DerEcdsaSignature $der
    if ($null -eq $raw) { return "BadSignature" }
    $point = Get-P256PublicKeyPoint $PublicKeyPem
    $q = New-Object System.Security.Cryptography.ECPoint
    $q.X = $point.X
    $q.Y = $point.Y
    $parameters = New-Object System.Security.Cryptography.ECParameters
    $parameters.Curve = [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256
    $parameters.Q = $q
    $key = [System.Security.Cryptography.ECDsa]::Create($parameters)
    try {
        if ($key.VerifyData($ManifestBytes, $raw, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) { return "Verified" }
        return "BadSignature"
    } catch [System.Security.Cryptography.CryptographicException] {
        return "BadSignature"
    } finally { $key.Dispose() }
}

# The manifest's bytes as an object (a byte-order mark left out, as the launcher does).
function ConvertFrom-ManifestBytes([byte[]]$Bytes) {
    $offset = 0
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) { $offset = 3 }
    return ([Text.Encoding]::UTF8.GetString($Bytes, $offset, $Bytes.Length - $offset) | ConvertFrom-Json)
}

# What to download, from a manifest whose signature verified: the full variant's win-x64 launcher and its linux/<Arch>
# image, each with its whole-file name, sha256 and URLs (the image's slices in order, or its one file). Throws, in words
# meant for the person installing, on anything that is not a manifest of this product for the version asked for.
function Select-ManifestDownloads($Manifest, [string]$Arch, [string]$ExpectedVersion) {
    $props = @($Manifest.PSObject.Properties.Name)
    if (-not ($props -contains "product") -or $Manifest.product -ne "fravashi") { throw "the manifest is not Nova Globen Fravashi's." }
    if (-not ($props -contains "version") -or -not $Manifest.version) { throw "the manifest names no version." }
    if ($ExpectedVersion -and $Manifest.version -ne $ExpectedVersion) {
        throw "the manifest in the $ExpectedVersion folder describes version $($Manifest.version); refusing it."
    }
    $variant = @($Manifest.variants) | Where-Object { $_.name -eq "full" } | Select-Object -First 1
    if (-not $variant) { throw "the manifest for $($Manifest.version) has no full variant." }
    $image = @($variant.images) | Where-Object { $_.os -eq "linux" -and $_.arch -eq $Arch } | Select-Object -First 1
    if (-not $image) { throw "release $($Manifest.version) has no linux/$Arch image (override the detection with -Arch if the container engine's architecture differs)." }
    $launcher = @($variant.launchers) | Where-Object { $_.rid -eq "win-x64" } | Select-Object -First 1
    if (-not $launcher -or -not ($launcher.PSObject.Properties.Name -contains "assetUrl") -or -not $launcher.assetUrl) {
        throw "release $($Manifest.version) lists no win-x64 launcher to download."
    }
    $imageUrls = @()
    if (($image.PSObject.Properties.Name -contains "partUrls") -and @($image.partUrls).Count -gt 0) { $imageUrls = @($image.partUrls) }
    elseif (($image.PSObject.Properties.Name -contains "assetUrl") -and $image.assetUrl) { $imageUrls = @($image.assetUrl) }
    else { throw "release $($Manifest.version) names no download URL for its linux/$Arch image." }
    $entries = @(
        @{ Asset = [string]$launcher.asset; Sha256 = [string]$launcher.sha256; Urls = @([string]$launcher.assetUrl); Sliced = $false },
        @{ Asset = [string]$image.asset; Sha256 = [string]$image.sha256; Urls = [string[]]$imageUrls; Sliced = ($imageUrls.Count -gt 1 -or (($image.PSObject.Properties.Name -contains "partUrls") -and @($image.partUrls).Count -gt 0)) }
    )
    foreach ($e in $entries) {
        if ($e.Asset -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw "the manifest names an asset '$($e.Asset)' that is not a plain file name." }
        if ($e.Sha256 -notmatch '^[0-9a-f]{64}$') { throw "the manifest's sha256 for $($e.Asset) is not one." }
        foreach ($u in $e.Urls) { if (-not $u.StartsWith("https://")) { throw "the manifest's URL for $($e.Asset) is not https: $u" } }
    }
    return @{ Version = [string]$Manifest.version; Launcher = $entries[0]; Image = $entries[1] }
}

# sha256sum output ("<hex>  <name>", a "*" before the name in binary mode) as name -> lower-case hex.
function Read-Sha256Sums([string[]]$Lines) {
    $sums = @{}
    foreach ($line in @($Lines)) {
        if (-not $line) { continue }
        $parts = $line.Trim() -split "\s+", 2
        if ($parts.Count -eq 2) { $sums[$parts[1].TrimStart('*')] = $parts[0].ToLowerInvariant() }
    }
    return $sums
}

# --- the update channel ------------------------------------------------------------------------------
# Public files, so no credential is involved at all. Defined above the FUNCTIONS_ONLY gate so
# WebInstallCurlRetryTests can drive it with a stand-in for curl.
# $NotFound, when given, is the whole message for an HTTP 404 and nothing else: the setup EXE reads its first words
# ("No Fravashi release is published at") as "nothing to install there yet".
function Invoke-PublicDownload([string]$Url, [string]$OutFile, [string]$What, [string]$Hint = "", [string]$NotFound = "") {
    Write-Host "Downloading $What ..."
    $why = ""
    $status = 0
    if ($script:curlExe) {
        $curlExit = Invoke-CurlDownload "" $OutFile $Url
        if ($curlExit -eq 0) { return }
        if ($curlExit -eq 22) { $status = [int]$script:lastHttpStatus }
        $why = "curl exit $curlExit" + $(if ($script:lastHttpStatus) { ", HTTP $($script:lastHttpStatus)" } else { "" })
    } else {
        $keptProgress = $ProgressPreference
        try {
            $ProgressPreference = "SilentlyContinue"
            Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $OutFile
            return
        } catch {
            $why = $_.Exception.Message
            if ($_.Exception.Response) { try { $status = [int]$_.Exception.Response.StatusCode } catch { $status = 0 } }
        } finally { $ProgressPreference = $keptProgress }
    }
    if ($NotFound -and $status -eq 404) { Die $NotFound }
    Die ("Could not download $What from $Url ($why)." + $(if ($Hint) { " $Hint" } else { "" }))
}

if ($env:FRAVASHI_WEBINSTALL_FUNCTIONS_ONLY -eq "1") { return }

# Anything that throws past the explicit checks (a failed image load inside install-windows.ps1,
# a full disk) must also leave its reason readable before the window goes.
trap {
    Write-Host ""
    Write-Host "FAILED: $_" -ForegroundColor Red
    Wait-BeforeClosing
    exit 1
}

Write-Host "Nova Globen Fravashi web installer starting (version: $(if ($Version) { $Version } else { 'latest' }), from $DownloadBase) ..."

# --- architecture -----------------------------------------------------------------------------------
# On Windows-on-ARM a shell may run under x64 emulation and PROCESSOR_ARCHITECTURE then reports
# AMD64; the real CPU (which the container runtime targets) is in PROCESSOR_ARCHITEW6432.
if (-not $Arch) {
    $rawArch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $Arch = if ($rawArch.ToUpperInvariant() -eq "ARM64") { "arm64" } else { "amd64" }
    Write-Host "Host CPU: $rawArch -> image architecture '$Arch' (override with -Arch if your podman machine differs)"
} else {
    Write-Host "Image architecture '$Arch' (explicit override)"
}

# The unpack and the hand-off to the release's own installer.
function Install-ReleaseBundle([string]$launcherPath, [string]$imagePath, [string]$work) {
    Write-Host "Unpacking the launcher bundle ..."
    # The Windows tar (bsdtar, in System32 since Windows 10 1803) and not whichever tar is first on PATH: GNU tar from Git
    # for Windows's Unix tools reads "C:\..." as host:path and fails with "Cannot connect to C: resolve failed", which
    # happens to anyone who put those tools ahead of System32 (seen 2026-10-08 when this engine ran from Git Bash).
    $tar = Join-Path $env:SystemRoot "System32\tar.exe"
    if (-not (Test-Path -LiteralPath $tar)) { $tar = "tar" }
    & $tar -xzf $launcherPath -C $work
    if ($LASTEXITCODE -ne 0) { Die "Could not extract $(Split-Path $launcherPath -Leaf) (tar exit $LASTEXITCODE)." }
    $bundle = Join-Path $work "fravashi-win-x64"
    if (-not (Test-Path (Join-Path $bundle "install-windows.ps1"))) {
        Die "The launcher bundle has no install-windows.ps1; this release is malformed."
    }
    # install-windows.ps1 expects the canonical name beside itself. A gzipped image asset is
    # decompressed to the plain tar: podman and docker would detect gzip by content anyway, but
    # the installed contract stays byte-identical to what the artifact route produces, so no
    # runtime (wslc included) is asked to guess.
    $canonical = Join-Path $bundle "fravashi-image.tar"
    if ((Split-Path $imagePath -Leaf) -like "*.gz") {
        Write-Host "Decompressing $(Split-Path $imagePath -Leaf) ..."
        $inStream = [IO.File]::OpenRead($imagePath)
        try {
            $gzStream = New-Object IO.Compression.GZipStream($inStream, [IO.Compression.CompressionMode]::Decompress)
            $outStream = [IO.File]::Create($canonical)
            try { $gzStream.CopyTo($outStream) } finally { $outStream.Dispose(); $gzStream.Dispose() }
        } finally { $inStream.Dispose() }
        Remove-Item -Force $imagePath
    } else {
        Move-Item -Force $imagePath $canonical
    }

    Write-Host ""
    Write-Host "Handing off to the release's install-windows.ps1 ..."
    Write-Host ""
    $installArgs = @{}
    if ($Runtime) { $installArgs.Runtime = $Runtime }
    if ($NoElevationPrompt) { $installArgs.NoElevationPrompt = $true }
    & (Join-Path $bundle "install-windows.ps1") @installArgs
}

function Save-ManifestAsset($Entry, [string]$Work) {
    $path = Join-Path $Work $Entry.Asset
    if (-not $Entry.Sliced) {
        Invoke-PublicDownload $Entry.Urls[0] $path $Entry.Asset
        return $path
    }
    $out = [IO.File]::Create($path)
    try {
        for ($i = 0; $i -lt $Entry.Urls.Count; $i++) {
            $partPath = Join-Path $Work ("{0}.slice{1:D2}" -f $Entry.Asset, $i)
            Invoke-PublicDownload $Entry.Urls[$i] $partPath ("{0} (slice {1} of {2})" -f $Entry.Asset, ($i + 1), $Entry.Urls.Count)
            $in = [IO.File]::OpenRead($partPath)
            try { $in.CopyTo($out) } finally { $in.Dispose() }
            Remove-Item -Force $partPath
        }
    } finally { $out.Dispose() }
    Write-Host "  reassembled $($Entry.Asset) from $($Entry.Urls.Count) slices"
    return $path
}

$folder = if ($Version) { $Version } else { "latest" }
if ($folder -cnotmatch '^(latest|[0-9][0-9A-Za-z._-]*)$') { Die "'$Version' is not a version (for example 0.9.99, without the v)." }
$script:lastHttpStatus = 0
$script:curlExe = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source
$folderUrl = "$($DownloadBase.TrimEnd('/'))/$folder"
$work = Join-Path ([IO.Path]::GetTempPath()) ("fravashi-webinstall-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    Write-Host "Looking up the release ..."
    $manifestPath = Join-Path $work "release-manifest.json"
    $sigPath = Join-Path $work "release-manifest.sig"
    Invoke-PublicDownload "$folderUrl/release-manifest.json" $manifestPath "release-manifest.json" "A 404 means no release is published in that folder: check the version." ("No Fravashi release is published at $folderUrl yet (HTTP 404). Check the version.")
    Invoke-PublicDownload "$folderUrl/release-manifest.sig" $sigPath "release-manifest.sig"
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $verdict = Test-ReleaseManifestSignature $manifestBytes ([IO.File]::ReadAllText($sigPath))
    if ($verdict -ne "Verified") {
        Die "The manifest at $folderUrl is not signed by Fravashi's release key ($verdict). Refusing to install anything it names."
    }
    try { $plan = Select-ManifestDownloads (ConvertFrom-ManifestBytes $manifestBytes) $Arch $Version } catch { Die "Cannot install from ${folderUrl}: $($_.Exception.Message)" }
    Write-Host "Release: $($plan.Version) (manifest signature verified against the release key)"

    if ($ResolveOnly) {
        Write-Host ""
        foreach ($e in @($plan.Launcher, $plan.Image)) {
            Write-Host ("  {0}  sha256 {1}" -f $e.Asset, $e.Sha256)
            foreach ($u in $e.Urls) { Write-Host "    $u" }
        }
        Write-Host "  checked by $folderUrl/SHA256SUMS.txt"
        Write-Host ""
        Write-Host "-ResolveOnly: stopping before any download."
        Wait-BeforeClosing
        exit 0
    }

    $sumsPath = Join-Path $work "SHA256SUMS.txt"
    Invoke-PublicDownload "$folderUrl/SHA256SUMS.txt" $sumsPath "SHA256SUMS.txt"
    $sums = Read-Sha256Sums (Get-Content $sumsPath)
    $launcherPath = Save-ManifestAsset $plan.Launcher $work
    $imagePath = Save-ManifestAsset $plan.Image $work
    foreach ($pair in @(@($plan.Launcher, $launcherPath), @($plan.Image, $imagePath))) {
        $entry = $pair[0]
        $actual = (Get-FileHash -Algorithm SHA256 $pair[1]).Hash.ToLowerInvariant()
        if ($actual -ne $entry.Sha256) {
            Die "sha256 MISMATCH for $($entry.Asset) (signed manifest $($entry.Sha256), got $actual). Refusing to install a corrupt or tampered download."
        }
        if (-not $sums.ContainsKey($entry.Asset)) { Die "$($entry.Asset) is not listed in SHA256SUMS.txt; this release is malformed." }
        if ($sums[$entry.Asset] -ne $actual) {
            Die "sha256 MISMATCH for $($entry.Asset) (SHA256SUMS.txt $($sums[$entry.Asset]), got $actual). Refusing to install it."
        }
        Write-Host "  verified sha256 $($entry.Asset)"
    }

    Install-ReleaseBundle $launcherPath $imagePath $work
    Write-Host ""
    Write-Host "Nova Globen Fravashi $($plan.Version) is installed."
    Wait-BeforeClosing
}
finally {
    if ($KeepDownloads) { Write-Host "Downloads kept at $work" } else { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}
exit 0
