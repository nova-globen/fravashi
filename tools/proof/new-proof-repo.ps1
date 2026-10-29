# Creates a throwaway repository for a live proof that mutates (an agent session that edits and commits, a pull request
# drill), so nothing real is touched: a working repository with one seed commit and a local bare repository as its
# `origin`, side by side.
#
#   .\tools\proof\new-proof-repo.ps1                       # ..\agent-session-proof, or -2, -3 ... if that one exists
#   .\tools\proof\new-proof-repo.ps1 -Name pr-drill        # ..\pr-drill (refuses a name that exists)
#   .\tools\proof\new-proof-repo.ps1 -Root D:\scratch      # somewhere else
#
# It writes nothing outside the two new folders, never deletes anything, and needs only git. Remove both folders when
# the proof is done. The repositories the earlier proofs used were deleted on 2026-10-28; this makes them on demand.
param(
    [string]$Name = '',
    [string]$Root = ''
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is not on PATH.' }

if ($Name) {
    if (Test-Path (Join-Path $Root $Name)) { throw "$(Join-Path $Root $Name) already exists. Pick another -Name." }
}
else {
    $Name = 'agent-session-proof'
    for ($n = 2; Test-Path (Join-Path $Root $Name); $n++) { $Name = "agent-session-proof-$n" }
}

$work = Join-Path $Root $Name
$origin = Join-Path $Root "$Name-origin.git"
if (Test-Path $origin) { throw "$origin already exists. Pick another -Name." }

git init --quiet --bare --initial-branch=main $origin
git init --quiet --initial-branch=main $work
Set-Content -Path (Join-Path $work 'README.md') -Encoding utf8 -Value @(
    "# $Name",
    '',
    'A throwaway repository for a live proof. Nothing here is real; delete it and its origin when the proof is done.')
git -C $work add README.md
git -C $work -c user.name=proof -c user.email=proof@example.test commit --quiet -m 'chore: seed the proof repository'
git -C $work remote add origin $origin
git -C $work push --quiet -u origin main

Write-Host "Created $work with origin $origin (branch main, one commit)."
