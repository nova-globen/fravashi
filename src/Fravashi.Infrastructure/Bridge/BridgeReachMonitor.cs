using Fravashi.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>Where the studio stands with fravashi.exe's host bridge (backlog 252).</summary>
public enum BridgeReachState
{
    /// <summary>No bridge on purpose (a host dev run, the <c>Repository:Root</c> fallback): nothing to say.</summary>
    NotConfigured,

    /// <summary>Not asked yet, or failing for less than the grace: nothing to say yet.</summary>
    Checking,

    /// <summary>The bridge answers: nothing to say.</summary>
    Reachable,

    /// <summary>The bridge has not answered for at least the grace: the words say why and what to do.</summary>
    Unreachable,
}

/// <summary>
/// Asks fravashi.exe's host bridge for a cheap answer from inside the container and keeps the verdict (backlog 252). Until
/// this existed the only trace of a firewalled bridge was "The sign-in hand-over did not finish within 00:00:20",
/// which reads as a slow start, while no workspace could open.
/// <para>
/// False alarms are the thing to avoid. A studio with no bridge coordinates (the dev fallback) is never asked. A
/// failure becomes a verdict only once failures have spanned <see cref="Grace"/> with no answer in between, so a
/// bridge that is slow to come up is never called blocked; one answer clears everything. While the verdict is
/// unreachable the words are left in <c>&lt;data dir&gt;/bridge-reach.txt</c> for <c>fravashi status</c>, and the file is
/// removed as soon as the bridge answers.
/// </para>
/// </summary>
public sealed class BridgeReachMonitor
{
    /// <summary>How long failures must last before they are said.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>The file <c>fravashi status</c> reads (the launcher's <c>BridgeReachFile.FileName</c>; change both together).</summary>
    public const string FileName = "bridge-reach.txt";

    private readonly IBridgeReachProbe _probe;
    private readonly string? _file;
    private readonly ILogger<BridgeReachMonitor>? _logger;
    private readonly TimeProvider _clock;
    private readonly Uri? _bridge;
    private readonly IReadOnlyList<int> _ports;
    private readonly object _gate = new();
    private DateTimeOffset? _failingSince;

    public BridgeReachMonitor(
        IConfiguration configuration,
        IBridgeReachProbe probe,
        DataDirectory? dataDirectory = null,
        ILogger<BridgeReachMonitor>? logger = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _probe = probe;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _file = dataDirectory is null ? null : Path.Combine(dataDirectory.Path, FileName);

        // The same test the workspace seams use: a URL AND a token, both baked by the launcher.
        if (!string.IsNullOrWhiteSpace(configuration[BridgeWorkspaceContext.TokenKey])
            && Uri.TryCreate(configuration[BridgeWorkspaceContext.UrlKey], UriKind.Absolute, out var bridge))
        {
            _bridge = bridge;
        }

        // Every port fravashi.exe listens on for this container, read from what was baked, so a moved port is named right.
        _ports = new[]
            {
                configuration[BridgeWorkspaceContext.UrlKey],
                configuration[AppRuns.AppRunnerClient.UrlKey],
                configuration[Updates.StudioUpgradeClient.UrlKey],
            }
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Port : -1)
            .Where(port => port > 0)
            .Distinct()
            .ToList();

        State = _bridge is null ? BridgeReachState.NotConfigured : BridgeReachState.Checking;
    }

    /// <summary>Whether there is a bridge to ask at all.</summary>
    public bool Configured => _bridge is not null;

    public BridgeReachState State { get; private set; }

    /// <summary>What to tell the person, or null when there is nothing to say (every state but unreachable).</summary>
    public string? Words { get; private set; }

    /// <summary>Asks the bridge once and updates the verdict. Never throws for the bridge's sake.</summary>
    public async Task<BridgeReachState> CheckAsync(CancellationToken cancellationToken)
    {
        if (_bridge is null)
        {
            return State;
        }

        var result = await _probe.ProbeAsync(_bridge, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (result.Outcome == BridgeReachOutcome.Answered)
            {
                if (State == BridgeReachState.Unreachable)
                {
                    _logger?.LogInformation("The host bridge at {Bridge} answers again.", _bridge);
                }

                _failingSince = null;
                State = BridgeReachState.Reachable;
                Words = null;
                RemoveFile();
                return State;
            }

            _failingSince ??= now;
            if (now - _failingSince.Value < Grace)
            {
                // Too early to say anything; a verdict already given stands until an answer clears it.
                if (State != BridgeReachState.Unreachable)
                {
                    State = BridgeReachState.Checking;
                }

                return State;
            }

            var words = BridgeReachWords.For(result.Outcome, _bridge, _ports, result.Reason, now - _failingSince.Value);
            if (State != BridgeReachState.Unreachable || !string.Equals(words, Words, StringComparison.Ordinal))
            {
                _logger?.LogWarning("{Words}", words);
                WriteFile(words);
            }

            State = BridgeReachState.Unreachable;
            Words = words;
            return State;
        }
    }

    private void WriteFile(string? words)
    {
        if (_file is null || words is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(_file, words);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Could not leave the bridge words for fravashi status at {Path}.", _file);
        }
    }

    private void RemoveFile()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            File.Delete(_file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Could not remove the bridge words at {Path}.", _file);
        }
    }
}

/// <summary>
/// Asks the bridge at start and keeps asking (backlog 252): every <see cref="SettlingInterval"/> until there is a
/// verdict, then every <see cref="SteadyInterval"/>, so the words appear about half a minute after a start that cannot
/// reach it and go away by themselves once the person has added the rule. Never part of the studio's start path, so a
/// firewalled bridge cannot hold the studio up.
/// </summary>
public sealed class BridgeReachHostedService(BridgeReachMonitor monitor, ILogger<BridgeReachHostedService>? logger = null)
    : BackgroundService
{
    public static readonly TimeSpan SettlingInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SteadyInterval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!monitor.Configured)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await monitor.CheckAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogDebug(ex, "Asking the host bridge failed; the next pass asks again.");
                }

                var wait = monitor.State == BridgeReachState.Checking ? SettlingInterval : SteadyInterval;
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }
}
