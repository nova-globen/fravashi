namespace Fravashi.Application.Autopilot;

/// <summary>
/// What returning a workspace to its base branch did (#310). <see cref="Note"/> is set whenever
/// <see cref="Returned"/> is false, in words naming why: a dirty tree ("left on &lt;branch&gt;, uncommitted
/// changes"), edits still on their way from the agent's copy ("left on &lt;branch&gt;, edits still reaching the
/// folder"), a working tree that could not be read ("not returned to &lt;branch&gt;: the working tree could not
/// be read"), or a workspace that could not be reached at all ("not returned to &lt;branch&gt;: ..."). It is
/// null only when the return succeeded, including when the workspace was already on the base branch.
/// </summary>
public sealed record AutopilotWorkspaceReturnResult(bool Returned, string? Note);

/// <summary>
/// Returns a workspace to the branch a run's first attempt was made from, once the run ends (#310, ADR 0031
/// decision 3's correction). Reached by the ENGINE's background tick, which has no circuit. Takes the EXACT
/// workspace id the dispatch resolved and recorded on the run, never a project to re-resolve (commit review
/// #310, third pass): the project-to-workspace lookup answers "the most recently opened workspace", which can
/// change while a run is open, and re-resolving it here could then check out a folder the run never touched.
/// Never forces a checkout over work and never stashes, resets or discards anything: a dirty working tree
/// refuses, in words, rather than being swept aside.
/// </summary>
public interface IAutopilotWorkspaceReturn
{
    Task<AutopilotWorkspaceReturnResult> ReturnAsync(string workspaceId, string baseBranch, CancellationToken ct = default);
}
