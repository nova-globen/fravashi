param([int]$Minutes = 30)
# Supervises a self-hosting trial (an agent inside Fravashi working on this repository) from the host. Glances at the
# open conversation every 15 seconds and approves ONLY a plain build or test command (dotnet build|test with no ; & `
# $ < > and at most one trailing tail, head or grep), which the trials approve unread. Anything else that is pending
# ends the watch with "PENDING: <its text>" so the supervisor reads it (approvalbody.mjs) and answers it (answer.mjs),
# and so does the end of the turn ("TURN ENDED"). It reads whatever page the window shows, so navigating the
# window away from the conversation makes it report TURN ENDED at once: leave the window alone while it runs. Run it in the background with CDP_ID or CDP_MATCH naming the window,
# and start it again after each answer. Never widen what it approves unread: that list is the trials' standing rule.
Set-Location (Join-Path $PSScriptRoot '..\..')
$probe = Join-Path $PSScriptRoot 'probe.ps1'
# `tail -n 15` and `tail -15` are the same command, so both spellings count. Two exact wrappers count too, because
# builds in the studio need them (a long Bash call is otherwise moved to the background, where nothing can wait for it):
# a leading `timeout <seconds>` and a trailing `; echo "exit=${PIPESTATUS[0]}"`. Nothing else is added.
$plain = '^Needs your approval Bash (timeout \d+ )?dotnet (build|test) [^;&`$<>|]*( 2>&1)?( \| (tail|head) (-n \d+|-\d+)| \| grep -E "[^"$`\\]*")?(; echo "exit=\$\{PIPESTATUS\[0\]\}")? What it wants to do'
# Narrowings found by #299's commit reviews (2026-10-12) of the autopilot rule that copied this pattern. A command
# spanning more than one line is never plain (glance.mjs judges the raw text, because probe.ps1 collapses newlines into
# spaces and `[^...]*` would then swallow a second command). And the ARGUMENTS are an allowlist, not a forbidden-
# character class, mirroring AutopilotApprovalRules: -p:PostBuildEvent=... runs a command as part of the build, a -p:
# value splits on commas so a second property can ride on EnableWindowsTargeting, and a logger, a test adapter path or
# a results directory loads code or writes anywhere with none of the excluded characters.
function Test-PlainArguments([string]$pending) {
    if ($pending -notmatch '^Needs your approval Bash (?:timeout \d+ )?dotnet (?:build|test)(?<args>.*?)(?: 2>&1| \||; echo| What it wants to do)') { return $false }
    $tokens = [regex]::Matches($Matches['args'], '"[^"]*"|\S+') | ForEach-Object { $_.Value }
    for ($i = 0; $i -lt $tokens.Count; $i++) {
        $t = $tokens[$i]
        if ($t -ceq '-p:EnableWindowsTargeting=true' -or $t -in '--no-build', '--no-restore' -or $t -match '^-m:\d+$') { continue }
        if ($t -in '-c', '--configuration' -and $i + 1 -lt $tokens.Count -and $tokens[$i + 1] -in 'Debug', 'Release') { $i++; continue }
        if ($t -in '-v', '--verbosity' -and $i + 1 -lt $tokens.Count -and $tokens[$i + 1] -match '^(q|quiet|m|minimal|n|normal|d|detailed|diag|diagnostic)$') { $i++; continue }
        # Bash expands $(...), ${...} and a backtick INSIDE double quotes, so a quoted value may hold none of them.
        if ($t -eq '--filter' -and $i + 1 -lt $tokens.Count -and $tokens[$i + 1] -match '^"[^"$`\\]*"$') { $i++; continue }
        if ($t -match '^[A-Za-z0-9_][A-Za-z0-9_./-]*$' -and $t -notmatch '(^|/)\.\.(/|$)') { continue }
        return $false
    }
    return $true
}
$deadline = (Get-Date).AddMinutes($Minutes)
$idle = 0
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 15
    $g = & $probe glance.mjs 20000
    try { $o = $g | ConvertFrom-Json } catch { continue }
    if ($o.pending) {
        if ($o.pending -match $plain -and -not $o.multiline -and (Test-PlainArguments $o.pending)) {
            $env:ANSWER = 'approve'
            & $probe answer.mjs 20000 | Out-Null
            $env:ANSWER = $null
            "$(Get-Date -Format HH:mm:ss) approved unread: $($o.pending.Substring(20, [Math]::Min(140, $o.pending.Length - 20)))"
            continue
        }

        "PENDING: $($o.pending)"
        break
    }

    if (-not $o.running) {
        $idle++
        if ($idle -ge 2) { "TURN ENDED: $($o.entries -join ' | ')"; break }
    }
    else {
        $idle = 0
    }
}
