namespace Fravashi.Application.AgentRuntimes;

/// <summary>
/// Runs a one-shot <see cref="AgentCommand"/> against a coding-agent CLI or its package manager and returns
/// the captured result. The single seam through which the runtime service, the update service, and the board
/// mapping suggester shell out, so every one of them is testable against a fake CLI without spawning a process.
/// </summary>
public interface IAgentCommandRunner
{
    Task<AgentCommandResult> RunAsync(AgentCommand command, CancellationToken cancellationToken = default);
}
