using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.AgentRuntimes;

/// <summary>
/// Observes and manages the coding-agent CLIs installed in this environment: their presence, version, and auth
/// state, plus on-demand install/update. Every reported fact is from a real observation (the CLI answering a
/// command), never assumed. Implementations shell out via <see cref="IAgentCommandRunner"/>.
/// </summary>
public interface IAgentRuntimeService
{
    /// <summary>Reads the live status of one runtime (installed? version? authenticated?).</summary>
    Task<AgentRuntimeStatus> GetStatusAsync(AgentTool tool, CancellationToken cancellationToken = default);

    /// <summary>Installs or updates one runtime to the latest published version, returning the observed result.</summary>
    Task<AgentRuntimeUpdateResult> InstallOrUpdateAsync(AgentTool tool, CancellationToken cancellationToken = default);
}
