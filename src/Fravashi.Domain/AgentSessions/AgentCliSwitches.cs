namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// Switches the studio sets on EVERY agent CLI child, named here once so what the child is started with and what
/// the approval card says about it cannot come apart (backlog 192). <c>AgentProcessEnvironment.Restrict</c> sets
/// these; <see cref="AgentToolEffects"/> describes calls as the CLI treats them with these set.
/// </summary>
public static class AgentCliSwitches
{
    /// <summary>
    /// Backlog 189: the pinned CLI's switch that turns background tasks off. Read from the pinned 2.1.280 binary
    /// (backlog 192): with it set, the Bash and Agent tools both leave <c>run_in_background</c> out of the schema
    /// the model is offered, a Bash call past its timeout is stopped rather than moved to the background, and an
    /// Agent call is launched asynchronously only when its isolation is "remote"
    /// (<c>shouldRunAsync = remote || (wantsBackground &amp;&amp; !backgroundTasksDisabled)</c>).
    /// </summary>
    public const string DisableBackgroundTasksVariable = "CLAUDE_CODE_DISABLE_BACKGROUND_TASKS";

    /// <summary>The value <see cref="DisableBackgroundTasksVariable"/> is set to.</summary>
    public const string DisableBackgroundTasksValue = "1";
}
