namespace Fravashi.Application.Autopilot;

/// <summary>
/// Puts a sequential run's shared workspace back on the run's base branch before the next item starts (#315).
/// Without it the folder stayed on the previous item's attempt branch between items (#310 returns it only when
/// the RUN ends), and the next attempt was branched from there. Takes the exact workspace id the item's session
/// will run in. Never forces a checkout over work and never stashes, resets or discards anything: a folder that
/// is not clean, ON the base branch or off it, is one item's leftovers, and it is refused in words rather than
/// carried into the next item.
/// </summary>
public interface IAutopilotItemStart
{
    /// <summary>
    /// Null when the workspace is clean and on <paramref name="baseBranch"/> (checked out when it was not);
    /// otherwise why not, in words ("left on &lt;branch&gt;, uncommitted changes", and the like).
    /// </summary>
    Task<string?> PrepareAsync(string workspaceId, string baseBranch, CancellationToken ct = default);
}
