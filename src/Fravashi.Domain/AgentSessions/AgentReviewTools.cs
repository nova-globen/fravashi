namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// The tools a HELD-TO-READING conversation may use, and nothing else: the commit reviewer (backlog 243) and,
/// since backlog 300, the autopilot operator's three judgement steps, which end in different document kinds
/// (answer, advice, follow_up_drafts) but are read-only the same way. The reviewer, or the operator, is a
/// READER: it is started with only <see cref="BuiltIn"/> as its built-in tools, and the approval broker
/// refuses every other call it makes without asking anyone. One list for both, so the command line and the
/// gate cannot disagree.
/// </summary>
public static class AgentReviewTools
{
    /// <summary>
    /// The CLI's own tools a held-to-reading conversation is started with. <see cref="Studio"/>'s four are
    /// MARKED to load upfront in the tool server's own <c>tools/list</c> answer (work item #335), and the
    /// pinned 2.1.280 was seen honouring that marker, but only on a throwaway stdio probe with two tools and
    /// no <c>--tools</c> list: a reading
    /// conversation's own shape, this <c>--tools</c> list against the studio's Streamable HTTP server, has not
    /// itself been driven live. ToolSearch is kept here regardless, for a reason that does not depend on
    /// whether the marker takes under that shape: it is what the recorded reading command line and the live
    /// contract test that loads <c>studio_version</c> through it both depend on, and dropping it would move
    /// both. It reads the harness's tool list and nothing on disk.
    /// </summary>
    public static IReadOnlyList<string> BuiltIn { get; } = ["Read", "Grep", "Glob", "ToolSearch"];

    /// <summary>The studio's own tools a held-to-reading conversation may call, by their full names.</summary>
    public static IReadOnlyList<string> Studio { get; } =
    [
        AgentWorkGate.StudioToolPrefix + "git_diff",
        AgentWorkGate.StudioToolPrefix + "git_show",
        AgentWorkGate.StudioToolPrefix + "work_item_read",
        AgentWorkGate.StudioToolPrefix + "wiki_read",
    ];

    /// <summary>The full name of <c>git_commit</c>, the commit tool the trials used.</summary>
    public const string Commit = AgentWorkGate.StudioToolPrefix + "git_commit";

    /// <summary>
    /// The full name of <c>attempt_commit</c> (backlog 86), which commits what the session was approved to change. It is
    /// reviewed like <see cref="Commit"/> (backlog 250), or an agent could commit around the reviewer by choosing it.
    /// </summary>
    public const string AttemptCommit = AgentWorkGate.StudioToolPrefix + "attempt_commit";

    /// <summary>Every tool name a review is run for: each tool that lets an agent commit.</summary>
    public static IReadOnlyList<string> Commits { get; } = [Commit, AttemptCommit];

    /// <summary>
    /// Whether a call is an agent commit a review stands in front of, matched exactly and ordinally like the
    /// turn-ending document's name: a case variant, a bare name or another server's tool of the same name is not one.
    /// </summary>
    public static bool IsCommit(string? toolName) =>
        toolName is not null && Commits.Contains(toolName, StringComparer.Ordinal);

    /// <summary>Whether a call names one of the reviewer's built-in tools, exactly and ordinally.</summary>
    public static bool IsBuiltIn(string? toolName) =>
        toolName is not null && BuiltIn.Contains(toolName, StringComparer.Ordinal);

    /// <summary>Whether a call names one of the studio tools the reviewer may call, exactly and ordinally.</summary>
    public static bool IsStudio(string? toolName) =>
        toolName is not null && Studio.Contains(toolName, StringComparer.Ordinal);

    /// <summary>
    /// What a held-to-reading conversation is told when it reaches for anything else. Worded NEUTRALLY to the
    /// document it must end in (backlog 300): the commit reviewer ends in an answer or a findings document,
    /// the autopilot operator's three steps each end in a different one (answer, advice, follow_up_drafts), and
    /// a sentence naming "a verdict" specifically would point the operator at the wrong kind.
    /// </summary>
    public static string Refusal(string? toolName) =>
        $"This conversation is held to reading: {string.Join(", ", BuiltIn)} inside the "
        + "workspace's copy, and the studio's git_diff, git_show, work_item_read and wiki_read. "
        + $"{(string.IsNullOrWhiteSpace(toolName) ? "That call" : toolName)} is none of those, so it was refused without "
        + "asking anyone. Finish with the document you were asked for.";
}
