namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// The tools a GRAPH-ONLY conversation may use, and nothing else (work item #352, "Notes and items only"). One such
/// conversation may run in a folder beside its live editing one, because it can never touch the workspace: it reads
/// the folder's copy, and it reads and writes the tracker and the wiki, but it never edits a file, runs a command or
/// touches git. One list for all three layers that hold it there, so they cannot disagree:
/// <list type="number">
/// <item>the CLI is started with only <see cref="BuiltIn"/> as its built-in tools, so Edit, Write, NotebookEdit and
/// Bash do not exist for it at all;</item>
/// <item>the approval broker refuses, up front and in words, every call that is not on <see cref="Allows"/>, before
/// any gate, rule or card (default deny: a tool added tomorrow is refused until it is named here);</item>
/// <item>the live registry marks the conversation graph-only for good (a transcript row it never removes), so a
/// resume, a model change or an effort change keeps it graph-only and nothing can make it an editing one.</item>
/// </list>
/// What IS allowed is still answered the ordinary way: a read outside the copy asks, and a graph change that asks a
/// person in an editing conversation asks here too. Only the refusals are new.
/// </summary>
public static class AgentGraphOnlyTools
{
    /// <summary>What every surface calls such a conversation: the list's chip, the pane's chip and the start option.</summary>
    public const string Label = "Notes and items only";

    /// <summary>
    /// The CLI's own tools a graph-only conversation is started with: the same reading set the commit reviewer has
    /// (<see cref="AgentReviewTools.BuiltIn"/>), so the <c>--tools</c> value is one the studio already passes.
    /// </summary>
    public static IReadOnlyList<string> BuiltIn => AgentReviewTools.BuiltIn;

    /// <summary>
    /// The studio's own tools a graph-only conversation may call, by their full names: the work item and wiki tools,
    /// the session's own links and reads, and the studio's version. Deliberately absent: every <c>git_*</c> and
    /// <c>attempt_*</c> tool, <c>work_item_scenario_run</c> (it executes a command), the browser, the CI reads,
    /// <c>studio_upgrade</c>, <c>quick_change</c>, and the design and prototype tools.
    /// </summary>
    public static IReadOnlyList<string> Studio { get; } =
    [
        AgentWorkGate.StudioToolPrefix + "studio_version",
        AgentWorkGate.StudioToolPrefix + "work_items_find",
        AgentWorkGate.StudioToolPrefix + "work_items_startable",
        AgentWorkGate.StudioToolPrefix + "work_item_read",
        AgentWorkGate.StudioToolPrefix + "work_item_releases",
        AgentWorkGate.StudioToolPrefix + "work_item_create",
        AgentWorkGate.StudioToolPrefix + "work_item_update",
        AgentWorkGate.StudioToolPrefix + "work_item_transition",
        AgentWorkGate.StudioToolPrefix + "work_item_link",
        AgentWorkGate.StudioToolPrefix + "work_item_evidence",
        AgentWorkGate.StudioToolPrefix + "work_item_scenario_write",
        AgentWorkGate.StudioToolPrefix + "wiki_find",
        AgentWorkGate.StudioToolPrefix + "wiki_read",
        AgentWorkGate.StudioToolPrefix + "wiki_write",
        AgentWorkGate.StudioToolPrefix + "wiki_link",
        AgentWorkGate.StudioToolPrefix + "session_work_link",
        AgentWorkGate.StudioToolPrefix + "session_plan_link",
        AgentWorkGate.StudioToolPrefix + "session_work_read",
        AgentWorkGate.StudioToolPrefix + "session_governance_read",
        AgentWorkGate.StudioToolPrefix + "session_attention",
    ];

    /// <summary>
    /// Whether a graph-only conversation may make this call at all, matched exactly and ordinally: a case variant
    /// (<c>read</c>), a bare studio name (<c>work_item_create</c>) or another server's tool of the same name is not
    /// on the list. The turn-ending document is not here; the broker answers it before this is asked.
    /// </summary>
    public static bool Allows(string? toolName) =>
        toolName is not null
        && (BuiltIn.Contains(toolName, StringComparer.Ordinal) || Studio.Contains(toolName, StringComparer.Ordinal));

    /// <summary>
    /// What a graph-only conversation is told when it reaches for anything else. Short, because a refusal reason is
    /// cut at 500 characters on its way to the agent, and it names what to do instead.
    /// </summary>
    public static string Refusal(string? toolName) =>
        $"This conversation is notes and items only: it may read the folder's copy ({string.Join(", ", BuiltIn)}) and "
        + "read and write work items and the wiki with the studio's work_item_* and wiki_* tools. "
        + $"{(string.IsNullOrWhiteSpace(toolName) ? "That call" : toolName)} is none of those, so it was refused "
        + "without asking anyone. Write what needs changing into a work item; the folder's editing conversation "
        + "makes the change.";
}
