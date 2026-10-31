#!/usr/bin/env pwsh
# The ONE elevated step of a Fravashi install (backlog 374): gives fravashi.exe its own inbound Windows Firewall rule, so
# the studio container can always reach fravashi.exe's bridge, app runner and upgrade listener.
#
# Why: without a rule, fravashi.exe's first listen meets Windows Firewall's "Query User" prompt, which records Block at
# once and turns it into Allow only when a person answers. On a locked console nobody can answer, so the Block stays for
# good and the studio cannot reach the bridge (measured on the test VM on 2026-10-30; the v1.0.0 drill's evidence 04).
#
# What it does, idempotently: deletes every inbound rule for this fravashi.exe that is a Block or one of the prompt's own
# "Query User" rules (a leftover Block outranks any allow rule), then creates ONE rule: inbound, Allow, TCP, the program,
# every profile (the Hyper-V Default Switch is Public), remote address LocalSubnet (the host-only container networks, never
# the internet). Scoped by PROGRAM rather than by port: the three listeners prefer 9091, 9095 and 9097 but fall back to a
# random free port and keep it (PortSelector.Select), and nothing could widen a port-pinned rule later without elevating
# again.
#
# install-windows.ps1 runs this directly when it is already elevated, and otherwise through one UAC prompt. Exit code 0
# is the rule in place; anything else is a failure the installer reports. FRAVASHI_FIREWALL_FUNCTIONS_ONLY=1 dot-sources
# the functions alone (FirewallRuleScriptTests). Runs under Windows PowerShell 5.1, so keep to its syntax.
param(
    # The installed fravashi.exe the rule is for.
    [string]$Program = "",
    # The uninstall (backlog 375): remove every inbound rule for the program, its own rule included, and add none.
    [switch]$Remove
)

$script:RuleName = 'Fravashi-Inbound'
$script:RuleGroup = 'Nova Globen Fravashi'

function Get-NormalizedProgram([string]$path) {
    # Firewall rules store the path as given (the prompt lower-cases it), sometimes with environment variables.
    return [Environment]::ExpandEnvironmentVariables($path).Trim().ToUpperInvariant()
}

function Select-RulesToRemove {
    <# From inbound rules ({ Name, Action, Program }), the ones to delete before the rule is (re)created: for this program,
       every Block, every "Query User" rule the prompt made, and this script's own rule from an earlier install. An Allow
       a person or another tool made for it is left alone. #>
    param([object[]]$Rules, [string]$Program)
    $target = Get-NormalizedProgram $Program
    @($Rules | Where-Object {
            $_.Program -and (Get-NormalizedProgram $_.Program) -eq $target -and (
                $_.Action -eq 'Block' -or $_.Name -like '*Query User*' -or $_.Name -eq $script:RuleName)
        })
}

function Select-RulesForUninstall {
    <# On uninstall (backlog 375) every inbound rule for this program goes, an Allow a person made included: the program
       it lets in is being removed. Rules for any other program stay. #>
    param([object[]]$Rules, [string]$Program)
    $target = Get-NormalizedProgram $Program
    @($Rules | Where-Object { ($_.Program -and (Get-NormalizedProgram $_.Program) -eq $target) -or $_.Name -eq $script:RuleName })
}

function New-RuleSpec([string]$Program) {
    <# The parameters New-NetFirewallRule gets: the one rule this install owns. #>
    @{
        Name          = $script:RuleName
        DisplayName   = 'Fravashi: the studio container to fravashi.exe'
        Description   = 'Lets the Fravashi studio container reach fravashi.exe (bridge, app runner, upgrade) from the local host-only networks. Made by the Fravashi installer.'
        Group         = $script:RuleGroup
        Direction     = 'Inbound'
        Action        = 'Allow'
        Protocol      = 'TCP'
        Program       = $Program
        Profile       = 'Any'
        RemoteAddress = 'LocalSubnet'
    }
}

function Get-FirewallStepMode([bool]$Elevated, [bool]$NoElevationPrompt) {
    <# How install-windows.ps1 runs this script: 'direct' when it is already elevated (an elevated quiet install, SSH),
       'skip' when it may not ask (FravashiSetup.exe /quiet), else 'prompt': one UAC prompt. #>
    if ($Elevated) { return 'direct' }
    if ($NoElevationPrompt) { return 'skip' }
    return 'prompt'
}

function Get-FirewallAdvice([string]$Program, [string]$Why) {
    <# What the installer says when the rule was NOT set: why, what the first start may meet, and the one command that
       sets it later. The script is copied beside fravashi.exe, so the command works after the bundle is gone. #>
    # Plain string work rather than Split-Path/Join-Path, which read a Windows path by the HOST's rules (the tests run on
    # Linux too, where they see no folder in it at all).
    $script = $Program.Substring(0, $Program.LastIndexOfAny([char[]]'\/') + 1) + 'firewall-rule.ps1'
    @(
        "The firewall rule for fravashi.exe was not set ($Why).",
        "At the first start Windows Firewall may ask about fravashi.exe: choose Allow. If nobody can answer (a locked screen),",
        "Windows blocks it silently and the studio cannot reach fravashi.exe. To set the rule later, run once in an",
        "administrator PowerShell:",
        "  powershell -NoProfile -ExecutionPolicy Bypass -File `"$script`" -Program `"$Program`""
    )
}

if ($env:FRAVASHI_FIREWALL_FUNCTIONS_ONLY -eq '1') { return }

$ErrorActionPreference = 'Stop'
try {
    if (-not $Program) { throw 'pass -Program with the installed fravashi.exe' }
    $target = Get-NormalizedProgram $Program
    $inbound = @(Get-NetFirewallApplicationFilter | Where-Object { $_.Program -and (Get-NormalizedProgram $_.Program) -eq $target } |
        ForEach-Object {
            $rule = $_ | Get-NetFirewallRule
            if ($rule.Direction -eq 'Inbound') {
                [pscustomobject]@{ Name = $rule.Name; Action = [string]$rule.Action; Program = $_.Program; DisplayName = $rule.DisplayName }
            }
        })
    $selected = if ($Remove) {
        # The script's own rule by name too, in case its program path was written differently.
        $own = @(Get-NetFirewallRule -Name $script:RuleName -ErrorAction SilentlyContinue |
            ForEach-Object { [pscustomobject]@{ Name = $_.Name; Action = [string]$_.Action; Program = $null; DisplayName = $_.DisplayName } })
        Select-RulesForUninstall -Rules ($inbound + $own) -Program $Program
    }
    else { Select-RulesToRemove -Rules $inbound -Program $Program }
    foreach ($rule in @($selected | Sort-Object Name -Unique)) {
        Remove-NetFirewallRule -Name $rule.Name
        Write-Host "Removed the firewall rule '$($rule.DisplayName)' ($($rule.Action))."
    }
    if ($Remove) {
        Write-Host "Removed $(@($selected).Count) firewall rule(s) for fravashi.exe."
        exit 0
    }
    $spec = New-RuleSpec $Program
    New-NetFirewallRule @spec | Out-Null
    Write-Host "Added the firewall rule '$($spec.DisplayName)': inbound TCP for fravashi.exe from the local networks."
    exit 0
}
catch {
    Write-Host "Could not set the firewall rule: $_"
    exit 1
}
