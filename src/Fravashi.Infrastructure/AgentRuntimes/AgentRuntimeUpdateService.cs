using Fravashi.Application.AgentRuntimes;
using Fravashi.Domain.AgentSessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// Update-on-launch (N1/N2/S11). At container start this enforces the <b>pinned</b> version of every embedded
/// coding-agent CLI, in the background. Since backlog 61 / ADR 0023 it is no longer an upgrade: the image
/// already bakes the pin, and the writable <c>/data/.npm-global</c> prefix shadows those baked copies on PATH,
/// so this pass is the repair that puts a drifted or half-installed prefix back on the pin. It is
/// <b>owner-mandated</b> and supersedes ADR 0003 V4's "nothing at startup": the pass runs off the startup path
/// (a <see cref="BackgroundService"/>, so <see cref="StartAsync"/> returns immediately), each install is
/// timeout-bounded by the command runner, and a failure (offline, npm error) is recorded and swallowed,
/// <b>never</b> blocking the app.
/// <para>
/// It runs only inside the studio container (env <c>FRAVASHI_IN_CONTAINER=1</c>, set in the Dockerfile), so a
/// developer running the app on their host never has their own CLIs touched.
/// </para>
/// </summary>
public sealed class AgentRuntimeUpdateService : BackgroundService
{
    /// <summary>The environment variable the Dockerfile sets to mark an in-container run.</summary>
    public const string InContainerEnvVar = "FRAVASHI_IN_CONTAINER";

    private readonly IAgentRuntimeService _runtimes;
    private readonly IAgentRuntimeStatusReport _report;
    private readonly ILogger<AgentRuntimeUpdateService> _logger;

    public AgentRuntimeUpdateService(
        IAgentRuntimeService runtimes,
        IAgentRuntimeStatusReport report,
        ILogger<AgentRuntimeUpdateService> logger)
    {
        _runtimes = runtimes;
        _report = report;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsInContainer())
        {
            // On a developer host we deliberately touch nothing; mark the runtimes skipped so the UI is honest.
            foreach (var tool in AgentRuntimeCatalog.Tools)
            {
                _report.Record(new AgentRuntimeUpdateResult(
                    tool, AgentRuntimeUpdateOutcome.Skipped, null, null,
                    "Update-on-launch runs only inside the studio container.", DateTimeOffset.UtcNow));
            }

            return;
        }

        await RunUpdatesAsync(stoppingToken);
    }

    /// <summary>
    /// Runs one update pass over every embedded runtime, recording each result. Exposed for testing so the pass
    /// can be driven without the container gate. Failures are contained per-runtime.
    /// </summary>
    public async Task RunUpdatesAsync(CancellationToken cancellationToken)
    {
        _report.BeginRun();
        try
        {
            foreach (var tool in AgentRuntimeCatalog.Tools)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _report.Record(new AgentRuntimeUpdateResult(
                    tool, AgentRuntimeUpdateOutcome.Running, null, null, null, DateTimeOffset.UtcNow));
                try
                {
                    var result = await _runtimes.InstallOrUpdateAsync(tool, cancellationToken);
                    _report.Record(result);
                    if (result.Outcome == AgentRuntimeUpdateOutcome.Succeeded)
                    {
                        _logger.LogInformation(
                            "Agent runtime {Tool} update: {From} -> {To}.", tool, result.FromVersion, result.ToVersion);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Agent runtime {Tool} update did not succeed: {Message}", tool, result.Message);
                    }

                    // Drift is findable in studio-*.log rather than only in the UI (ADR 0023): a CLI that is
                    // not the pinned one speaks a protocol nobody verified against this build. This runs on
                    // EVERY outcome, not only a successful one, because the realistic way a drifted CLI keeps
                    // serving turns is an install that FAILED (offline, npm error) and left the older copy
                    // standing in the writable /data/.npm-global prefix: the pass that repaired nothing is
                    // exactly the one whose log has to name the version present, or the first of the three
                    // advertised drift surfaces is silent in the only case where it matters.
                    WarnIfOffPin(tool, PresentVersion(result));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Update-on-launch never blocks or crashes the app. No version was observed on this path,
                    // so nothing is claimed about drift either: probing again through the seam that just threw
                    // would cost a process and prove nothing.
                    _report.Record(new AgentRuntimeUpdateResult(
                        tool, AgentRuntimeUpdateOutcome.Failed, null, null, ex.Message, DateTimeOffset.UtcNow));
                    _logger.LogWarning(ex, "Agent runtime {Tool} update threw.", tool);
                }
            }
        }
        finally
        {
            _report.EndRun();
        }
    }

    /// <summary>
    /// The version actually present after an attempt, from the attempt's own observations only, so this adds
    /// no probe and no startup cost: <see cref="IAgentRuntimeService.InstallOrUpdateAsync"/> re-reads the CLI's
    /// version whether or not the package manager succeeded. On a SUCCESSFUL attempt only that after-reading
    /// can describe what was installed, so a missing one stays unknown; on a failed one the before-reading is a
    /// legitimate fallback, because an install that failed replaced nothing. Null means no version was observed
    /// at all (the CLI is absent, or never answered), and then nothing is claimed.
    /// </summary>
    private static string? PresentVersion(AgentRuntimeUpdateResult result) =>
        result.Outcome == AgentRuntimeUpdateOutcome.Succeeded
            ? result.ToVersion
            : result.ToVersion ?? result.FromVersion;

    /// <summary>
    /// Emits the one drift line an operator greps for, naming both the version present and the pin. Deliberately
    /// the same sentence on every outcome so one grep finds it whether the repair worked or not; an unknown
    /// version says nothing (see <c>AgentRuntimeDescriptor.IsOffPin</c>).
    /// </summary>
    private void WarnIfOffPin(AgentTool tool, string? installedVersion)
    {
        var descriptor = AgentRuntimeCatalog.For(tool);
        if (!descriptor.IsOffPin(installedVersion))
        {
            return;
        }

        _logger.LogWarning(
            "Agent runtime {Tool} reports {Installed} but the product pins {Pinned}; the protocol it speaks is unverified.",
            tool, installedVersion, descriptor.PinnedVersion);
    }

    private static bool IsInContainer() =>
        string.Equals(Environment.GetEnvironmentVariable(InContainerEnvVar), "1", StringComparison.Ordinal);
}
