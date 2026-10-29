param([string]$File, [int]$Ms = 25000)
# Runs one window probe with a bound, from the repository root, and prints its single-line result. Set CDP_MATCH to a
# URL fragment when two windows are open (the URL drops ?ws= after in-app navigation, so tell them apart by path).
# The repository root, found from this script's own place, so a renamed or moved checkout keeps working.
Set-Location (Resolve-Path (Join-Path $PSScriptRoot "..\.."))
$out = Join-Path $env:TEMP ("probe-" + [guid]::NewGuid().ToString("N") + ".out")
$p = Start-Process node -ArgumentList "tools/window-probe/cdp.mjs", "tools/window-probe/probes/$File" -NoNewWindow -PassThru -RedirectStandardOutput $out
if (-not $p.WaitForExit($Ms)) { try { $p.Kill() } catch {}; "TIMED OUT: $File" }
if (Test-Path $out) { (Get-Content $out -Raw) -replace '\s+', ' '; Remove-Item $out -ErrorAction SilentlyContinue }
