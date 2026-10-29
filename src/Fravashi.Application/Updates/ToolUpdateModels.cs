using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Updates;

/// <summary>
/// What one embedded agent-runtime CLI's version situation is (Type A, ADR 0006 / F1). Since backlog 61 /
/// ADR 0023 the product PINS each CLI, so this row answers two different questions and never conflates them:
/// <b>NewerPublished</b> means the registry has something newer than the pin, which the product deliberately
/// does NOT install (reporting is not promoting); <b>OffPin</b> means the installed CLI is not the pinned one,
/// which is a real repair the user can run. Both come from real observations, the installed version from
/// <c>&lt;cli&gt; --version</c> and the latest from a registry GET; a missing one is honestly nothing to offer.
/// These updates <b>notify but never lock</b> the app (D03-2): they can never be critical.
/// </summary>
/// <param name="Tool">Which coding-agent runtime.</param>
/// <param name="DisplayName">The vendor's name for the CLI, for the card.</param>
/// <param name="Package">The npm package that is checked and installed.</param>
/// <param name="PinnedVersion">The version the product pins, from <c>AgentRuntimeCatalog</c>.</param>
/// <param name="InstalledVersion">The version the CLI reports, or null when it is not installed / did not answer.</param>
/// <param name="LatestVersion">The latest published version from the registry, or null when the check could not run.</param>
/// <param name="NewerPublished">
/// True when the registry's latest is newer than the pin. Informational only: promoting it is a product change
/// (ADR 0023), not a click, so the card reports it and offers nothing.
/// </param>
/// <param name="OffPin">
/// True when the installed CLI is known and is not the pinned version. This is the only state in which the card
/// offers an action, and that action installs the PIN, never the registry's newest.
/// </param>
/// <param name="Note">A short honest note when there is nothing to offer (not installed, offline, registry error).</param>
public sealed record ToolUpdateInfo(
    AgentTool Tool,
    string DisplayName,
    string Package,
    string PinnedVersion,
    string? InstalledVersion,
    string? LatestVersion,
    bool NewerPublished,
    bool OffPin,
    string? Note);

/// <summary>The phase of an in-progress or finished tool update, for the streamed progress on the card.</summary>
public enum ToolUpdatePhase
{
    /// <summary>No update has been started for this tool in this process.</summary>
    Idle,

    /// <summary>The update is running; the log is streaming.</summary>
    Running,

    /// <summary>The update finished and the package manager reported success.</summary>
    Succeeded,

    /// <summary>The update finished and failed; the log carries the real error.</summary>
    Failed,
}

/// <summary>
/// A streamed view of one tool's update, updated live as the update runs and surfaced on the dashboard card
/// through the circuit. The <see cref="Log"/> is the real sequence of phase messages / package-manager output
/// observed during the run - never a fabricated progress animation.
/// </summary>
/// <param name="Tool">Which runtime is (or was) updating.</param>
/// <param name="Phase">Where the update is.</param>
/// <param name="Log">The progress log lines, oldest first.</param>
/// <param name="UpdatedUtc">When this progress snapshot was last written.</param>
public sealed record ToolUpdateProgress(
    AgentTool Tool,
    ToolUpdatePhase Phase,
    IReadOnlyList<string> Log,
    DateTimeOffset UpdatedUtc);
