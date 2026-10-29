using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Updates;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Tests.Updates;

/// <summary>A package registry whose latest-version answer per package is scripted.</summary>
internal sealed class FakePackageRegistry : IPackageRegistry
{
    public Dictionary<string, string?> Latest { get; } = new(StringComparer.Ordinal);
    public List<string> Queried { get; } = new();

    public Task<string?> GetLatestVersionAsync(string package, CancellationToken cancellationToken = default)
    {
        Queried.Add(package);
        return Task.FromResult(Latest.TryGetValue(package, out var v) ? v : null);
    }
}

/// <summary>An agent-runtime service whose status + update result per tool are scripted.</summary>
internal sealed class FakeAgentRuntimeService : IAgentRuntimeService
{
    public Dictionary<AgentTool, AgentRuntimeStatus> Statuses { get; } = new();
    public Dictionary<AgentTool, AgentRuntimeUpdateResult> UpdateResults { get; } = new();
    public List<AgentTool> Updated { get; } = new();

    public Task<AgentRuntimeStatus> GetStatusAsync(AgentTool tool, CancellationToken cancellationToken = default) =>
        Task.FromResult(Statuses.TryGetValue(tool, out var s)
            ? s
            : new AgentRuntimeStatus(tool, Installed: false, Version: null, AgentRuntimeAuthState.NotInstalled, null));

    public Task<AgentRuntimeUpdateResult> InstallOrUpdateAsync(AgentTool tool, CancellationToken cancellationToken = default)
    {
        Updated.Add(tool);
        var result = UpdateResults.TryGetValue(tool, out var r)
            ? r
            : new AgentRuntimeUpdateResult(tool, AgentRuntimeUpdateOutcome.Succeeded, "1.0.0", "1.1.0", null, DateTimeOffset.UtcNow);

        // After a successful update, reflect the new version in the status so a post-update check sees it.
        if (result.Outcome == AgentRuntimeUpdateOutcome.Succeeded && result.ToVersion is not null)
        {
            Statuses[tool] = new AgentRuntimeStatus(tool, Installed: true, result.ToVersion, AgentRuntimeAuthState.Unknown, null);
        }

        return Task.FromResult(result);
    }
}
