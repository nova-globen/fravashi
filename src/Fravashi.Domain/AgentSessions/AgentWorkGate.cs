namespace Fravashi.Domain.AgentSessions;

/// <summary>Why the governance gates let a call through, or did not (ADR 0020, backlog 44 and 45).</summary>
public enum WorkGateVerdict
{
    /// <summary>The call changes nothing in the workspace, so the gate has no opinion about it.</summary>
    NotAMutation,

    /// <summary>It changes the GRAPH rather than the workspace, which is how a session clears this gate.</summary>
    GraphMutation,

    /// <summary>The session names the work it is doing and the plan it is doing it under.</summary>
    Linked,

    /// <summary>The session names no work item, so the change is refused before anyone is asked.</summary>
    Unlinked,

    /// <summary>It names work but no plan (gate G2), so the change is refused before anyone is asked.</summary>
    NoPlan,

    /// <summary>Every item it names is closed, so the link points at work nobody is doing (backlog 85).</summary>
    WorkNotWorkable,

    /// <summary>The plan it names says nothing, which satisfies the letter of G2 and none of its point.</summary>
    PlanIsEmpty,

    /// <summary>The plan it names is a plan for other work, so it is not a plan for this.</summary>
    PlanDoesNotCover,

    /// <summary>The change is outside what the work declares it may touch.</summary>
    OutOfScope,

    /// <summary>The change would take the session past the change budget its work declares.</summary>
    OverChangeBudget,
}

/// <summary>
/// Gates G1 and G2 (ADR 0020: "no change without a work item and a plan"). A session that names no work
/// item, or names work and no plan to do it under, may read, discuss, and write to the GRAPH, but may not
/// change the workspace.
/// <para>
/// The load-bearing distinction is ADR 0020's own: the gates govern WORKSPACE mutations and never graph
/// mutations, because a graph mutation is how an agent clears the gate. An agent told "you cannot edit this
/// file yet" has to be able to create the work item that lets it, in the same turn, without a person doing it
/// for them. So a studio graph tool passes this gate by construction, and the refusal says so.
/// </para>
/// <para>
/// It is a pure function of the tool's name and what the session names, so the same answer holds for the
/// approval hook, for a future autopilot policy, and for a test, and nothing here reads a store.
/// </para>
/// </summary>
public static class AgentWorkGate
{
    /// <summary>
    /// The prefix the CLI gives the studio's own tools. A call under it changes the graph, not the
    /// workspace, so it is outside this gate: matched ordinally, so a lookalike server name does not pass.
    /// </summary>
    public const string StudioToolPrefix = "mcp__fravashi__";

    /// <summary>
    /// True when the Bash command is nothing but <c>pwd</c>, optionally followed by flags such as <c>-P</c>
    /// (work item #340). It changes nothing in the workspace, so this gate should treat it as a read, but it
    /// is deliberately NOT joined to <see cref="AgentReadRedirect"/>'s list: that class sends a command back
    /// to Read, Grep or Glob because those tools can answer the same question unattended, and none of them
    /// can answer "what is the current directory". It is also not added to <see cref="AgentToolSafety"/>'s
    /// auto-allow list, because that would let it run WITHOUT asking a person at all, and a bare
    /// <c>pwd</c> should keep asking exactly like any other Bash call; only this gate's pre-plan refusal is
    /// meant to lift. So it is answered here, as one more fact the caller can fold into the <c>readOnly</c>
    /// input <see cref="Check(string?,bool,WorkGateFacts)"/> is handed. Anything beyond a bare command -
    /// another statement (<c>pwd &amp;&amp; touch x</c>), a pipe, or a redirection (<c>pwd &gt; f</c>) - is
    /// not bare, and falls through to this gate's ordinary answer.
    /// </summary>
    public static bool IsBarePwd(string? toolName, string? command)
    {
        if (!string.Equals(toolName, "Bash", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var segments = AgentReadRedirect.Segments(command).ToList();
        if (segments.Count != 1)
        {
            return false;
        }

        var segment = segments[0];
        if (segment.Contains('>', StringComparison.Ordinal)
            || segment.Contains('<', StringComparison.Ordinal)
            || segment.Contains("$(", StringComparison.Ordinal)
            || segment.Contains('`', StringComparison.Ordinal))
        {
            return false;
        }

        var words = AgentReadRedirect.Words(segment);
        return words.Count > 0
            && string.Equals(words[0], "pwd", StringComparison.Ordinal)
            && words.Skip(1).All(w => w.StartsWith('-'));
    }

    /// <summary>
    /// What the agent is told when the gate refuses. It is written as a job rather than an apology, because
    /// the point of the gate is not to stop the work: it is to make the work describable first, and the agent
    /// is the one that can do that. The acceptance for backlog 44 says so in as many words.
    /// </summary>
    public const string UnlinkedSentence =
        "This session is not linked to a work item, so it cannot change the workspace yet. This is not an "
        + "error and there is nothing to apologise for. Help the user say what this change is for, in one or "
        + "two sentences covering the problem and the outcome, then create the work item yourself with "
        + "mcp__fravashi__work_item_create and link this session to it. Reading, searching and discussing "
        + $"are unaffected, and so is writing to the {ProductName.Short} graph.";

    /// <summary>
    /// What the agent is told when gate G2 refuses: there is work, and no plan to do it under. Written as a
    /// job for the same reason as the sentence above, and it names the steps in the order they have to
    /// happen, because an agent that writes the plan and forgets to say it is working under it is refused
    /// again for a reason it will read as the same one.
    /// </summary>
    public const string NoPlanSentence =
        "This session names the work it is doing but no plan to do it under, so it cannot change the "
        + "workspace yet. This is not an error. Write the plan yourself: read the linked work items, then "
        + "create a wiki document of type plan with mcp__fravashi__wiki_write saying what will change and "
        + "why, link every work item it affects with mcp__fravashi__wiki_link stating the link kind, and "
        + "then say this session is working under it with mcp__fravashi__session_plan_link. Reading, "
        + $"searching and discussing are unaffected, and so is writing to the {ProductName.Short} graph.";

    /// <summary>What the agent is told when every item it named is closed (backlog 85's readiness).</summary>
    public const string NotWorkableSentence =
        "Every work item this session names is closed, so the link points at work nobody is doing and the "
        + "workspace cannot be changed under it. This is not an error. Either reopen the item if the work "
        + "really is being done again, or say what is being done now: create the item with "
        + "mcp__fravashi__work_item_create and link this session to it with mcp__fravashi__session_work_link.";

    /// <summary>What the agent is told when the plan it named says nothing.</summary>
    public const string EmptyPlanSentence =
        "The plan this session names has no body, so it satisfies the letter of having a plan and none of "
        + "its point. Write what will change and why into it with mcp__fravashi__wiki_write, against the "
        + "version you read, then try the change again.";

    /// <summary>What the agent is told when the plan is a plan for other work.</summary>
    public const string PlanDoesNotCoverSentence =
        "The plan this session names does not mention the work items this session is doing, so it is a plan "
        + "for something else. Either link the work items it affects with mcp__fravashi__wiki_link, saying "
        + "the link kind, or write a plan for this work and bind the session to it with "
        + "mcp__fravashi__session_plan_link.";

    /// <summary>What the agent is told when the change is outside the declared scope.</summary>
    public const string OutOfScopeSentence =
        "This change is outside the areas the work items declare it may touch, so it was not made. That "
        + "declaration is a decision someone wrote down, not an oversight. If the work really does reach "
        + "here, widen the item's scope patterns deliberately and say why; otherwise change something "
        + "inside the declared area.";

    /// <summary>
    /// Told once, proactively, on a brand new building session's first turn (<see cref="AgentFirstTurnBriefing"/>,
    /// work item #333), so a write is not refused for a missing item or plan and learned only by meeting the
    /// refusal. States the two steps in the same order <see cref="Check(string?,bool,WorkGateFacts)"/> asks for
    /// them: work before plan. Said as a CONDITION ("if it is not already linked"), never as an unconditional job,
    /// because an autopilot building session is started already linked to its item (<c>AgentAutopilotBuilder
    /// .BuildAsync</c>, line 454) and, only when that item carries a plan ref, to that plan too (lines 455 to 458):
    /// an unconditional "name or create the work item" sentence would invite such a session to redo a link it
    /// already has, and a sentence that assumed it also already has a plan would invite it to skip a plan it does
    /// not have, meeting the very refusal this note exists to avoid. Two review rounds found these as two
    /// separate mistakes in two earlier wordings of this sentence (plan @150's review history).
    /// </summary>
    public const string FirstTurnSteps =
        "Before changing a file, this session needs a work item and a plan. If it is not already linked to "
        + "one of each, name or create the work item and link this session to it with session_work_link (or "
        + "create one with work_item_create), then write a short plan as a wiki document with wiki_write, "
        + "link the work items it affects with wiki_link, and link this session to the plan with "
        + "session_plan_link. A session the studio itself started may already be linked to one, both, or "
        + "neither; read session_governance_read first and do only whatever part is still missing. Reading, "
        + $"searching and the {ProductName.Short} graph are open regardless.";

    /// <summary>What the agent is told when one more file would exceed the declared change budget.</summary>
    public const string OverBudgetSentence =
        "This change would take the session past the change budget its work declares, so it was not made. "
        + "The budget is a statement about how large one attempt should be. Finish and verify what is "
        + "already changed, or raise the budget on the work item deliberately and say why.";

    /// <summary>
    /// The same refusals said to the PERSON (backlog 127, decided 2026-09-21). The sentences above are
    /// written for the model, as a job with tool names in it, and the transcript used to show them to the
    /// person verbatim, twice. Each has one short counterpart here that says what is missing and nothing about
    /// how an agent should go and fix it.
    /// </summary>
    private static readonly (string ForTheModel, string ForThePerson)[] Voices =
    [
        (UnlinkedSentence, "this session is not linked to a work item yet, so it cannot change the workspace"),
        (NoPlanSentence, "this session has a work item but no plan yet, so it cannot change the workspace"),
        (NotWorkableSentence, "every work item this session names is closed"),
        (EmptyPlanSentence, "the plan this session names has nothing written in it"),
        (PlanDoesNotCoverSentence, "the plan this session names does not mention the work it is doing"),
        (OutOfScopeSentence, "the change is outside the areas its work item says it may touch"),
        (OverBudgetSentence, "the change would go past the change budget its work item declares"),
        // Not a work gate, but refused the same way and shown the same way (backlog 166).
        (AgentReadRedirect.Sentence, AgentReadRedirect.ForThePerson),
        (AgentCliProtectedWrite.Sentence, AgentCliProtectedWrite.ForThePerson),
        // Work item #323.
        (AgentGitInBash.Sentence, AgentGitInBash.ForThePerson),
    ];

    /// <summary>Every sentence this gate can tell the model. For the test that holds the two voices together.</summary>
    public static IReadOnlyList<string> ModelSentences => [.. Voices.Select(v => v.ForTheModel)];

    /// <summary>
    /// What to tell a person about a refusal whose text CONTAINS one of this gate's sentences, or null when it
    /// contains none, in which case the caller shows the cause verbatim as backlog 114 requires. "Contains",
    /// because the CLI wraps a hook's reason in words of its own before it becomes a tool result.
    /// </summary>
    public static string? ForThePerson(string? refusalText)
    {
        if (string.IsNullOrWhiteSpace(refusalText))
        {
            return null;
        }

        foreach (var (forTheModel, forThePerson) in Voices)
        {
            if (refusalText.Contains(forTheModel, StringComparison.Ordinal))
            {
                return forThePerson;
            }
        }

        return null;
    }

    /// <summary>A verdict from the two bindings alone, for a caller that knows nothing else about them.</summary>
    /// <param name="toolName">The tool's name exactly as the CLI sent it.</param>
    /// <param name="readOnly">Whether the tool is on the studio's read-only list.</param>
    /// <param name="hasWork">Whether the session names at least one work item (gate G1).</param>
    /// <param name="hasPlan">Whether the session names a plan to do it under (gate G2).</param>
    public static WorkGateVerdict Check(string? toolName, bool readOnly, bool hasWork, bool hasPlan = true) =>
        Check(
            toolName,
            readOnly,
            new WorkGateFacts(
                HasWork: hasWork,
                WorkIsWorkable: hasWork,
                HasPlan: hasPlan,
                PlanHasContent: hasPlan,
                PlanCoversWork: hasPlan,
                InDeclaredScope: true,
                WithinChangeBudget: true));

    /// <summary>
    /// The verdict for one call. <paramref name="readOnly"/> is the studio's own answer about the tool
    /// (<see cref="AgentToolSafety.IsReadOnly"/>), passed in rather than looked up so this stays pure.
    /// <para>
    /// The order the facts are checked in is the order a person would fix them, and each answer names ONE
    /// thing to do. A verdict that said "several things are wrong at once" would be true and useless.
    /// </para>
    /// </summary>
    public static WorkGateVerdict Check(string? toolName, bool readOnly, WorkGateFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // Read-only work is never blocked (backlog 44's acceptance). An uncontained read still asks a
        // person, which is a different gate and not this one's business.
        if (readOnly)
        {
            return WorkGateVerdict.NotAMutation;
        }

        if (toolName is { Length: > 0 }
            && toolName.StartsWith(StudioToolPrefix, StringComparison.Ordinal))
        {
            return WorkGateVerdict.GraphMutation;
        }

        // A tool nobody recognises is a mutation as far as this gate is concerned, which is the fail-closed
        // half: the list of things that cannot change anything is the short, explicit one.
        //
        // G1 is answered before G2 on purpose. A session with neither is told to describe the WORK first,
        // because a plan written before anyone has said what it is for is a plan for nothing.
        if (!facts.HasWork)
        {
            return WorkGateVerdict.Unlinked;
        }

        if (!facts.WorkIsWorkable)
        {
            return WorkGateVerdict.WorkNotWorkable;
        }

        if (!facts.HasPlan)
        {
            return WorkGateVerdict.NoPlan;
        }

        if (!facts.PlanHasContent)
        {
            return WorkGateVerdict.PlanIsEmpty;
        }

        if (!facts.PlanCoversWork)
        {
            return WorkGateVerdict.PlanDoesNotCover;
        }

        // Scope and budget come last because they are about THIS change rather than about the session, and
        // someone who has bound nothing should not be told about a scope they have not declared.
        if (!facts.InDeclaredScope)
        {
            return WorkGateVerdict.OutOfScope;
        }

        return facts.WithinChangeBudget ? WorkGateVerdict.Linked : WorkGateVerdict.OverChangeBudget;
    }

    /// <summary>True when the verdict means the call must be refused without asking anyone.</summary>
    public static bool Refuses(this WorkGateVerdict verdict) =>
        verdict is not (WorkGateVerdict.NotAMutation or WorkGateVerdict.GraphMutation or WorkGateVerdict.Linked);

    /// <summary>What to tell the agent about a refusal, or null when the verdict refuses nothing.</summary>
    public static string? SentenceFor(this WorkGateVerdict verdict) => verdict switch
    {
        WorkGateVerdict.Unlinked => UnlinkedSentence,
        WorkGateVerdict.NoPlan => NoPlanSentence,
        WorkGateVerdict.WorkNotWorkable => NotWorkableSentence,
        WorkGateVerdict.PlanIsEmpty => EmptyPlanSentence,
        WorkGateVerdict.PlanDoesNotCover => PlanDoesNotCoverSentence,
        WorkGateVerdict.OutOfScope => OutOfScopeSentence,
        WorkGateVerdict.OverChangeBudget => OverBudgetSentence,
        _ => null,
    };
}
