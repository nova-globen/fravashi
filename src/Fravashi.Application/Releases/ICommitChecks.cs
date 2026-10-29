namespace Fravashi.Application.Releases;

/// <summary>
/// What the forge's CI says about one commit (work item #368), for the release dialog's "create the tag" choice: the
/// newest run's status in the forge's own word ("success", "failure", "running"), or null when the forge reports
/// nothing for it, the origin is not a forge the studio knows, or the forge could not be reached. Read-only.
/// </summary>
public interface ICommitChecks
{
    /// <summary>The newest CI run's status for <paramref name="sha"/>, or null when there is nothing to say.</summary>
    Task<string?> StateAsync(string sha, CancellationToken ct = default);
}
