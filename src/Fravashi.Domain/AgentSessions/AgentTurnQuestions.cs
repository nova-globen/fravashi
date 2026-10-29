namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// One choice offered by a <see cref="AgentTurnDocumentKind.Questions"/> document (backlog item 92).
/// </summary>
/// <param name="Label">The choice itself, and the text the answer is composed from.</param>
/// <param name="Description">
/// What picking it costs or buys, in the agent's own words. OPTIONAL, and measured absent: given the
/// questions schema and no other prompting the CLI produced options carrying only a label (2026-08-06),
/// so a renderer that assumes a description arrives is a renderer that will look broken in the common
/// case.
/// </param>
public sealed record AgentTurnQuestionOption(string Label, string? Description = null);

/// <summary>
/// One question the agent stopped to ask, as data rather than as prose (ADR 0022, backlog item 92).
/// <para>
/// The archived studio guessed at questions by regex-matching <c>(y/n)</c> and numbered lists out of the
/// model's prose. This is the replacement: the question, its options and their trade-offs come back as a
/// document the studio can render as controls, and the user's pick goes back as an ordinary user turn.
/// </para>
/// </summary>
/// <param name="Id">
/// The agent's own handle for the question. Kept for fidelity and NOT used to key the UI: the schema
/// requires it but a model that omits it still asked something answerable, and losing the whole question
/// over a missing handle would be the studio being strict where it costs the user.
/// </param>
/// <param name="Question">What is being asked. Without it there is nothing to render, so it is required on read.</param>
/// <param name="Options">The choices, in the order the agent listed them. At least one, or there is nothing to pick.</param>
/// <param name="Header">A short label above the question. Optional, and measured absent.</param>
/// <param name="MultiSelect">True when more than one option may be picked. Absent means false.</param>
public sealed record AgentTurnQuestion(
    string Id,
    string Question,
    IReadOnlyList<AgentTurnQuestionOption> Options,
    string? Header = null,
    bool MultiSelect = false);

/// <summary>What the user picked for one question, ready to be composed into the next user turn.</summary>
/// <param name="Question">The question that was asked.</param>
/// <param name="Labels">The chosen option labels, in the order the question listed them.</param>
public sealed record AgentTurnQuestionAnswer(AgentTurnQuestion Question, IReadOnlyList<string> Labels);
