namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// Told once, on a brand new building session's first turn, alongside <see cref="AgentGitInBash.OpeningNote"/>
/// (work item #333, levers 1 and 3 of the 2026-10-18 cost measurement): the work gate's
/// steps, so a write is not refused for a missing item or plan, and the commit reviewer's own blocking failure
/// classes (<see cref="CommitReviewFailureClasses"/>, the one source its own prompt reads too), so a change can
/// be checked against them before it is committed rather than learned only from a failed review.
/// <para>
/// Sent through the exact mechanism work item #323 already added (<c>ClaudeSessionRunner</c>'s leading stdin
/// text block), under the same condition: a brand new, ordinary session, never a resume, never a session whose
/// built-in tools are narrowed (the commit reviewer, the autopilot operator). The CLI command line is untouched;
/// only stdin content grows. Kept short, because every word here is re-sent with every call for the rest of the
/// conversation.
/// </para>
/// </summary>
public static class AgentFirstTurnBriefing
{
    /// <summary>The instruction that precedes the reviewer's failure classes in <see cref="Note"/>.</summary>
    public const string CheckBeforeCommit =
        "Before you commit, check your change against what the commit reviewer blocks: ";

    /// <summary>The note itself: the gate's steps, then the reviewer's failure classes with one line to check against them.</summary>
    public static readonly string Note =
        AgentWorkGate.FirstTurnSteps + " " + CheckBeforeCommit
        + string.Join("; ", CommitReviewFailureClasses.ShortNames) + ".";

    /// <summary>
    /// Told instead of <see cref="Note"/> (and instead of <see cref="AgentGitInBash.OpeningNote"/>) on a brand new
    /// GRAPH-ONLY conversation's first turn (work item #352): what it can and cannot do. It names no commit and no
    /// work gate step, because it can neither commit nor change the workspace, and a graph write needs no plan.
    /// </summary>
    public static readonly string GraphOnlyNote =
        $"This conversation is {AgentGraphOnlyTools.Label.ToLowerInvariant()}, so it can run beside the folder's "
        + $"editing conversation. You can read the folder's copy ({string.Join(", ", AgentGraphOnlyTools.BuiltIn)}) and read "
        + "and write the tracker and the wiki with the studio's work_item_* and wiki_* tools, including linking this "
        + "conversation to an item. You cannot edit a file, run a command or use git, and the studio refuses those "
        + "calls without asking anyone. Writing an item or a page needs no plan. Record what you are asked to as work "
        + "items or wiki pages; a change to the folder is an editing conversation's to make.";

    /// <summary>
    /// Told instead of <see cref="Note"/> on a brand new STUDIO ASSISTANT conversation's first turn (work item #359): who
    /// it is, how it behaves (converse first, check, file, plan, ask) and what it can and cannot do. The owner's own
    /// words set the behaviour: a report is talked through before anything is done about it, because the person may
    /// be wrong or may only want it recorded.
    /// </summary>
    public static readonly string AssistantNote =
        $"You are the {ProductName.Short} assistant: the person's general assistant on Home, and the one who knows {ProductName.Short} best. "
        + "Have a proper conversation first. When the person reports something, do not start addressing it: ask what "
        + "they saw and what they want, since they may be mistaken or may only want it recorded. Check the report "
        + $"against the folder's code ({string.Join(", ", AgentAssistantTools.BuiltIn)}) and the board "
        + "(work_items_find, wiki_find), then say plainly what you found. When it is real and wanted, file ONE work "
        + "item of the right kind (a bug: kind bug; a story: level increment; a task: level task) with a clear "
        + "description and real acceptanceCriteria, the structured field, since Done needs evidence against each. "
        + "Attach the person's images to it with work_item_attach_image when they help. Offer a plan document "
        + "(wiki_write, type plan, linked to the item) and artifacts such as a mockup (mockup_schema, mockup_check, "
        + "then wiki_write of type design) when they would help. Then ASK whether to start working on it; only "
        + "after a yes, call session_start_building for the item, or autopilot_dispatch when several items are "
        + "ready and the person wants the autopilot. Both always ask the person again. You cannot edit a file, run "
        + "a command or use git; the studio refuses those calls without asking anyone. Writing an item or a page "
        + "needs no plan.";
}
