namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// The tools the STUDIO ASSISTANT may use, and nothing else (work item #359): the general assistant behind Home's box,
/// which talks a report through with the person, checks it against the code and the board, files the item, plans it
/// and offers to start the work. It is built on the graph-only conversation (work item #352,
/// <see cref="AgentGraphOnlyTools"/>): the same reading set and the same tracker and wiki tools, held by the same
/// three layers, so it never edits a file, runs a command or touches git:
/// <list type="number">
/// <item>the CLI is started with only <see cref="BuiltIn"/> as its built-in tools, so Edit, Write, NotebookEdit and
/// Bash do not exist for it at all;</item>
/// <item>the approval broker refuses, up front and in words, every call that is not on <see cref="Allows"/>, before
/// any gate, rule or card (default deny);</item>
/// <item>the live registry marks the conversation an assistant for good (a transcript row it never removes), so a
/// resume, a model change or an effort change keeps it one.</item>
/// </list>
/// On top of the graph-only list it has the design checks and prototype tools (they read and write only the wiki,
/// never a file), attaching a conversation image to an item, and exactly two tools of its own,
/// <see cref="AlwaysAsk"/>: starting a building conversation for an item and dispatching the autopilot. Those two
/// ALWAYS ask a person: the broker puts them on a card whatever the folder's trust level, the autopilot's rules or
/// the graph allow-list say, and refuses them up front to any conversation that is not an assistant.
/// </summary>
public static class AgentAssistantTools
{
    /// <summary>What every surface calls such a conversation: the list's chip, the pane's chip and Home's box.</summary>
    public const string Label = $"{ProductName.Short} assistant";

    /// <summary>The tool that starts a building (editing) conversation for a named work item. Always asks.</summary>
    public const string StartBuilding = AgentWorkGate.StudioToolPrefix + "session_start_building";

    /// <summary>The tool that dispatches the autopilot for named work items. Always asks.</summary>
    public const string DispatchAutopilot = AgentWorkGate.StudioToolPrefix + "autopilot_dispatch";

    /// <summary>
    /// The tool that attaches one of this conversation's own images to a work item. Asks like any change to an item
    /// that already exists (it is not on <c>AgentGraphTools</c>' unattended list).
    /// </summary>
    public const string AttachImage = AgentWorkGate.StudioToolPrefix + "work_item_attach_image";

    /// <summary>The CLI's own tools the assistant is started with: the graph-only reading set.</summary>
    public static IReadOnlyList<string> BuiltIn => AgentGraphOnlyTools.BuiltIn;

    /// <summary>
    /// The two tools that act beyond the graph on the person's behalf and therefore ALWAYS ask: never unattended, on
    /// no allow-list, under no trust level and no autopilot rule. They belong to the assistant alone.
    /// </summary>
    public static IReadOnlyList<string> AlwaysAsk { get; } = [StartBuilding, DispatchAutopilot];

    /// <summary>
    /// The studio's own tools the assistant may call, by their full names: everything a graph-only conversation may,
    /// the design and prototype tools (wiki only), attaching a conversation image to an item, and
    /// <see cref="AlwaysAsk"/>. Deliberately absent, as for graph-only: every <c>git_*</c> and <c>attempt_*</c> tool,
    /// <c>work_item_scenario_run</c>, the browser, the CI reads, <c>studio_upgrade</c>, <c>quick_change</c>, and
    /// <c>design_approve</c> (approving a design is a person's act by definition).
    /// </summary>
    public static IReadOnlyList<string> Studio { get; } =
    [
        .. AgentGraphOnlyTools.Studio,
        AgentWorkGate.StudioToolPrefix + "mockup_schema",
        AgentWorkGate.StudioToolPrefix + "mockup_check",
        AgentWorkGate.StudioToolPrefix + "prototype_check",
        AgentWorkGate.StudioToolPrefix + "prototype_render",
        AgentWorkGate.StudioToolPrefix + "prototype_adjust",
        AttachImage,
        .. AlwaysAsk,
    ];

    /// <summary>True for one of <see cref="AlwaysAsk"/>, matched exactly and ordinally.</summary>
    public static bool MustAsk(string? toolName) =>
        toolName is not null && AlwaysAsk.Contains(toolName, StringComparer.Ordinal);

    /// <summary>
    /// Whether the assistant may make this call at all, matched exactly and ordinally, like
    /// <see cref="AgentGraphOnlyTools.Allows"/>.
    /// </summary>
    public static bool Allows(string? toolName) =>
        toolName is not null
        && (BuiltIn.Contains(toolName, StringComparer.Ordinal) || Studio.Contains(toolName, StringComparer.Ordinal));

    /// <summary>
    /// What the assistant is told when it reaches for anything else. Short, because a refusal reason is cut at 500
    /// characters on its way to the agent.
    /// </summary>
    public static string Refusal(string? toolName) =>
        $"This is the {ProductName.Short} assistant: it reads the folder's copy and reads and writes work items and the wiki. "
        + $"{(string.IsNullOrWhiteSpace(toolName) ? "That call" : toolName)} is none of those, so it was refused "
        + "without asking anyone. To change the folder, file the item and offer to start a building conversation "
        + "for it with session_start_building.";

    /// <summary>What any conversation that is NOT an assistant is told when it calls one of the assistant's own tools.</summary>
    public static string NotAnAssistant(string? toolName) =>
        $"{(string.IsNullOrWhiteSpace(toolName) ? "That tool" : toolName)} belongs to the {ProductName.Short} assistant on Home, and this "
        + "conversation is not one, so it was refused without asking anyone.";
}
