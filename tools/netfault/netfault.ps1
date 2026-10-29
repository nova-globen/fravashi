<#
.SYNOPSIS
  Injects a network fault into the Fravashi studio container, and only that container (backlog 228, plan @49).

.DESCRIPTION
  Modes:
    dns-break           F1. Saves the container's /etc/resolv.conf and points it at 192.0.2.1, a documentation
                        address nothing answers on (the swap #218 proved). Stays broken until `restore`.
    link-down -Seconds  F3 (30) or F4 (300). Disconnects the container from its wslc network, waits, and
                        reconnects it. Reconnects in a finally block, so Ctrl+C still restores it.
    restore             Puts back whatever an earlier run left broken (a saved resolver, a recorded network
                        disconnect), then checks that the container resolves api.anthropic.com again.

  Safety, by construction:
    - The target is the container named in this install's launcher.json (containerName, default "fravashi").
      Passing -Container with any other name is refused; so is a container that is not running.
    - Every engine call goes to wslc and names that container. Nothing here runs a host network command or
      touches the host's adapters, DNS, routes or VPN.
    - The host measured on 2026-10-07 (wslc 3.0.1, recorded on #228) that `network disconnect` and `connect`
      work on a running container and that a published port comes back after the reconnect.

.EXAMPLE
  .\netfault.ps1 dns-break
  .\netfault.ps1 restore
  .\netfault.ps1 link-down -Seconds 30
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('dns-break', 'link-down', 'restore')]
    [string]$Mode,

    [ValidateRange(1, 3600)]
    [int]$Seconds = 30,

    # Only to confirm the target; anything but launcher.json's container is refused.
    [string]$Container,

    # Only when wslc inspect does not name the network; must be one of the container's own networks.
    [string]$Network
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Wslc = Join-Path $env:ProgramFiles 'WSL\wslc.exe'
$BrokenResolver = 'nameserver 192.0.2.1'
$SavedResolver = '/etc/resolv.conf.netfault'

function Say([string]$text) { Write-Host ("{0:HH:mm:ss} netfault: {1}" -f (Get-Date), $text) }

function Invoke-Wslc {
    param([string[]]$Arguments, [switch]$AllowFailure)
    $output = & $Wslc @Arguments 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0 -and -not $AllowFailure) {
        throw "wslc $($Arguments -join ' ') exited $code`: $output"
    }
    return [pscustomobject]@{ Code = $code; Output = ($output | Out-String).Trim() }
}

function Get-StudioContainerName {
    $config = Join-Path $env:LOCALAPPDATA 'Fravashi\launcher.json'
    $name = 'fravashi'
    if (Test-Path -LiteralPath $config) {
        $json = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
        if ($json.PSObject.Properties.Name -contains 'containerName' -and $json.containerName) {
            $name = [string]$json.containerName
        }
    }
    return $name
}

function Get-NetworkNames([object]$inspected) {
    $names = @()
    $settings = $inspected.PSObject.Properties | Where-Object Name -eq 'NetworkSettings'
    if ($settings -and $settings.Value -and ($settings.Value.PSObject.Properties.Name -contains 'Networks') -and $settings.Value.Networks) {
        $names += $settings.Value.Networks.PSObject.Properties.Name
    }
    $hostConfig = $inspected.PSObject.Properties | Where-Object Name -eq 'HostConfig'
    if ($names.Count -eq 0 -and $hostConfig -and $hostConfig.Value -and ($hostConfig.Value.PSObject.Properties.Name -contains 'NetworkMode')) {
        $names += [string]$hostConfig.Value.NetworkMode
    }
    return @($names | Where-Object { $_ -and $_ -notin @('host', 'none', 'default') })
}

if (-not (Test-Path -LiteralPath $Wslc)) {
    throw "wslc was not found at $Wslc. This tool drives the wslc engine only (ADR 0029)."
}

$target = Get-StudioContainerName
if ($Container -and $Container -cne $target) {
    throw "Refused: '$Container' is not the studio container. This tool only ever touches '$target', the container named in launcher.json."
}

$inspect = Invoke-Wslc @('inspect', '--type', 'container', $target) -AllowFailure
if ($inspect.Code -ne 0) {
    throw "Refused: no container named '$target' exists. Start Fravashi first."
}
# Windows PowerShell 5.1 hands a JSON array back as ONE object where PowerShell 7 enumerates it, so `@(...)[0]` was the
# whole array there, had no State, and the script refused with "is not running" for a container that was running.
$parsed = $inspect.Output | ConvertFrom-Json
$inspected = if ($parsed -is [array]) { $parsed[0] } else { $parsed }
$running = ($inspected.PSObject.Properties.Name -contains 'State') -and $inspected.State -and
    (($inspected.State.PSObject.Properties.Name -contains 'Running' -and $inspected.State.Running) -or
     ($inspected.State.PSObject.Properties.Name -contains 'Status' -and $inspected.State.Status -eq 'running'))
if (-not $running -and $Mode -ne 'restore') {
    throw "Refused: '$target' is not running."
}

$stateFile = Join-Path $env:TEMP "fravashi-netfault-$target.json"

function Read-State {
    if (Test-Path -LiteralPath $stateFile) { return Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json }
    return [pscustomobject]@{ network = $null }
}

function Test-Resolves {
    $probe = Invoke-Wslc @('exec', $target, 'getent', 'hosts', 'api.anthropic.com') -AllowFailure
    return $probe.Code -eq 0
}

function Restore-Resolver {
    # A container restart regenerates resolv.conf from the host, so the saved copy is only put back while the
    # broken marker is still in place; otherwise the fresh file is kept and the saved one dropped.
    $has = Invoke-Wslc @('exec', '-u', 'root', $target, 'test', '-f', $SavedResolver) -AllowFailure
    if ($has.Code -ne 0) { return }
    $current = Invoke-Wslc @('exec', $target, 'cat', '/etc/resolv.conf') -AllowFailure
    if ($current.Output.Trim() -eq $BrokenResolver) {
        Invoke-Wslc @('exec', '-u', 'root', $target, 'sh', '-c', "cat $SavedResolver > /etc/resolv.conf") | Out-Null
        Say "resolver put back from $SavedResolver"
    } else {
        Say 'resolver was already regenerated (the container restarted); keeping it'
    }
    Invoke-Wslc @('exec', '-u', 'root', $target, 'rm', '-f', $SavedResolver) | Out-Null
}

function Connect-Network([string]$name) {
    $connected = Invoke-Wslc @('network', 'connect', $name, $target) -AllowFailure
    if ($connected.Code -eq 0) {
        Say "reconnected '$target' to network '$name'"
    } else {
        Say "connect answered $($connected.Code) ($($connected.Output)); it may already be connected"
    }
    Remove-Item -LiteralPath $stateFile -ErrorAction SilentlyContinue
}

switch ($Mode) {
    'dns-break' {
        $has = Invoke-Wslc @('exec', '-u', 'root', $target, 'test', '-f', $SavedResolver) -AllowFailure
        if ($has.Code -eq 0) {
            throw "Refused: the resolver is already broken by an earlier run ($SavedResolver exists). Run restore first."
        }
        Invoke-Wslc @('exec', '-u', 'root', $target, 'sh', '-c', "cp /etc/resolv.conf $SavedResolver && echo '$BrokenResolver' > /etc/resolv.conf") | Out-Null
        Say "resolver in '$target' now points at 192.0.2.1 (saved the original to $SavedResolver)"
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $resolves = Test-Resolves
        Say ("getent hosts api.anthropic.com inside: {0} after {1:0.0} s" -f ($(if ($resolves) { 'STILL RESOLVES' } else { 'fails' })), $sw.Elapsed.TotalSeconds)
        Say 'run `netfault.ps1 restore` to put it back'
    }

    'link-down' {
        $detected = @(Get-NetworkNames $inspected)
        if ($Network) {
            if ($Network -in @('host', 'none') -or $Network -notmatch '^[A-Za-z0-9_.-]+$') {
                throw "Refused: '$Network' is not a container network this tool may disconnect."
            }
            if ($detected.Count -gt 0 -and $Network -notin $detected) {
                throw "Refused: '$target' is not on network '$Network' (it is on: $($detected -join ', '))."
            }
            $names = @($Network)
        } else {
            $names = $detected
        }
        if ($names.Count -ne 1) {
            throw "Could not tell which network '$target' is on (found: $($names -join ', ')). Look at ``wslc inspect --type container $target`` and pass -Network <name>."
        }
        $name = $names[0]
        if (Test-Path -LiteralPath $stateFile) {
            throw "Refused: an earlier link-down did not finish ($stateFile exists). Run restore first."
        }

        @{ network = $name; at = (Get-Date).ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8
        Invoke-Wslc @('network', 'disconnect', $name, $target) | Out-Null
        Say "disconnected '$target' from network '$name' for $Seconds s"
        try {
            $resolves = Test-Resolves
            Say ("getent hosts api.anthropic.com inside: {0}" -f ($(if ($resolves) { 'STILL RESOLVES' } else { 'fails' })))
            Start-Sleep -Seconds $Seconds
        }
        finally {
            Connect-Network $name
        }
        Say ("getent hosts api.anthropic.com inside after reconnecting: {0}" -f ($(if (Test-Resolves) { 'resolves' } else { 'STILL FAILS' })))
    }

    'restore' {
        if (-not $running) {
            Say "'$target' is not running; a start regenerates its resolver and network, so there is nothing to restore"
            Remove-Item -LiteralPath $stateFile -ErrorAction SilentlyContinue
            break
        }
        $state = Read-State
        if ($state.network) { Connect-Network ([string]$state.network) }
        Restore-Resolver
        Say ("getent hosts api.anthropic.com inside: {0}" -f ($(if (Test-Resolves) { 'resolves' } else { 'STILL FAILS' })))
    }
}
