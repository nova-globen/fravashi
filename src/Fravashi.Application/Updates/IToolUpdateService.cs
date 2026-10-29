using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Updates;

/// <summary>
/// The dashboard's window onto in-container CLI versions (Type A / F1). It answers "which embedded CLIs have a
/// newer published version" from a cached, real registry comparison so the card renders instantly and offline,
/// re-checks on demand and on a background schedule, and can reinstall the pinned version with its progress
/// streamed to the card. Since backlog 61 / ADR 0023 the newer-published answer is <b>informational</b>: the
/// product stays on its pin until a release is deliberately promoted. A singleton, so the cached result and the
/// live progress are visible to every circuit, the same shape as <c>INotificationHistory</c>.
/// </summary>
public interface IToolUpdateService
{
    /// <summary>Raised whenever the cached availability or any tool's progress changes, so the card re-renders.</summary>
    event Action? Changed;

    /// <summary>When the last availability check completed (from cache across restarts), or null if never.</summary>
    DateTimeOffset? LastCheckedUtc { get; }

    /// <summary>True while an availability check is running.</summary>
    bool IsChecking { get; }

    /// <summary>The most recent availability result per tool, from the settings-table cache (instant / offline).</summary>
    IReadOnlyList<ToolUpdateInfo> Available { get; }

    /// <summary>The live/last progress for a tool's update, or null if none has been attempted this process.</summary>
    ToolUpdateProgress? ProgressFor(AgentTool tool);

    /// <summary>
    /// Re-checks every embedded runtime against its registry, refreshes the cache, and emits a notification when
    /// new updates appear. Failures are contained and reported honestly, never thrown at the caller.
    /// </summary>
    Task<IReadOnlyList<ToolUpdateInfo>> CheckNowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the PINNED version of one tool (<c>npm install -g &lt;pkg&gt;@&lt;pinned&gt;</c>), streaming
    /// progress via <see cref="Changed"/> and <see cref="ProgressFor"/>, emitting a notification on completion,
    /// and refreshing that tool's cached row. This is a repair when the installed CLI has drifted off the pin;
    /// it can never promote the product to a newer release (ADR 0023). A failure is recorded with the real
    /// error; it never throws.
    /// </summary>
    Task UpdateAsync(AgentTool tool, CancellationToken cancellationToken = default);
}
