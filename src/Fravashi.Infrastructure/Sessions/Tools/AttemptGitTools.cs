using System.Text.Json.Nodes;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Infrastructure.Git;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// Puts this session's execution attempt on its own branch (backlog 86, ADR 0018 decision 9). A supervised
/// session can be branched by hand in the Git page; an unattended one cannot, which is why this exists.
/// </summary>
public sealed class AttemptStartTool(
    IWikiLibraries wiki, ITrackerResolver trackers, SessionGitWork work) : WikiToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "attempt_branch";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Puts this session's work on its own git branch, named after the work items it is doing, so an "
        + "attempt can be reviewed and abandoned without touching what was checked out. Calling it twice "
        + "returns the branch already started rather than making a second one.";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """{"type":"object","properties":{},"additionalProperties":false}""";

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        var started = await work.StartAsync(call.SessionId, cancellationToken).ConfigureAwait(false);

        return started.Branch is { } branch
            ? Changed("branched", new JsonObject { ["branch"] = branch })
            : Rejected("attempt_not_branched", "branch", started.Error ?? "The attempt could not be branched.");
    }
}

/// <summary>
/// Commits what this session was approved to change (backlog 86), or, for an autopilot builder running in
/// the git worktree #308 made for its own item (backlog 332), every path uncommitted in that worktree too,
/// because the worktree belongs to the item, never to whichever conversation happens to be building it (see
/// <see cref="SessionGitWork.DefaultPathsAsync"/>). A session that changed nothing produces no commit, and
/// that is a success: an empty commit would put a lie in the user's history.
/// </summary>
public sealed class AttemptCommitTool(
    IWikiLibraries wiki, ITrackerResolver trackers, SessionGitWork work) : WikiToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "attempt_commit";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Commits the files this session was approved to change, or, if this is an autopilot builder running "
        + "in its item's own git worktree, every path uncommitted in that worktree too (the worktree belongs "
        + "to the item, not to this conversation alone), with a message naming the work items and saying an "
        + "agent session produced it. A session that changed nothing produces no commit, and that is not an "
        + "error. If a previous call was refused by the commit reviewer and one of its findings is wrong, "
        + "`answers` disputes it once with a reason quoting the work item or plan it relies on; the next review "
        + "is told the finding and the answer and must withdraw or keep it.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "The commit's subject line." },
            "answers": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["finding", "reason"],
                "properties": {
                  "finding": { "type": "integer", "description": "The finding's number, exactly as printed in the refusal of the commit this review answers." },
                  "reason": { "type": "string", "description": "Why that finding is wrong, in at most 500 characters; quote the work item or plan text it relies on." }
                }
              },
              "description": "Disputes one or more blocking findings from the LATEST failed review of this session's own commit. Each finding named here can only be answered once."
            }
          },
          "required": ["summary"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        if (Text(call, "summary") is not { } summary)
        {
            return Rejected("summary_required", "summary", "Say in one line what this commit does.");
        }

        var result = await work.CommitAsync(call.SessionId, summary, paths: null, cancellationToken).ConfigureAwait(false);

        if (result.Refused)
        {
            // #318: a message with no subject of its own; the words say what to write, and nothing was committed.
            return Rejected("subject_required", "summary", result.Error ?? "Write a subject line first.");
        }

        if (result.NotSettled)
        {
            // Backlog 150: the session's edits are still on their way to the host; committing now would record a
            // tree without them. The agent asks again a moment later.
            return Rejected("not_settled", "summary", result.Error ?? SessionGitWork.NotSettledMessage);
        }

        if (result.Error is { Length: > 0 } error)
        {
            return Rejected("commit_failed", "summary", error);
        }

        return Changed(result.Nothing ? "nothing_to_commit" : "committed", new JsonObject
        {
            ["sha"] = result.Sha,
            ["files"] = result.Committed,
            ["note"] = result.Nothing
                ? "This session changed nothing, so there was nothing to commit."
                : null,
        });
    }
}
