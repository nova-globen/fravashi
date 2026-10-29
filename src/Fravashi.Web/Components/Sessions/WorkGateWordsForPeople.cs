using Fravashi.Application.Sessions.Tools;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// What the session work strip tells a PERSON about a held session (work item #362). The agent keeps receiving the
/// gate's own sentence (<see cref="AgentWorkGate.SentenceFor"/>), which is written as a job for the agent
/// and names the exact tools to call; a person reading the strip was being shown those raw tool names. This says the
/// same verdict in plain words, naming what the strip itself offers, and never a tool. A verdict this does not word
/// falls back to the agent's sentence with every tool name put into words, so no raw name can reach the strip.
/// </summary>
public static class WorkGateWordsForPeople
{
    public static string? For(WorkGateVerdict verdict) => verdict switch
    {
        WorkGateVerdict.Unlinked =>
            "This conversation is not linked to a work item, so the agent cannot change the workspace yet. Link an "
            + "item here, or tell the agent what the change is for and it will create the item and link it. Reading, "
            + "searching and discussing are unaffected.",
        WorkGateVerdict.NoPlan =>
            "This conversation names its work but has no plan, so the agent cannot change the workspace yet. Link a "
            + "plan here, or ask the agent to write one for the linked items.",
        WorkGateVerdict.WorkNotWorkable =>
            "Every work item this conversation names is closed, so the agent cannot change the workspace under it. "
            + "Reopen the item if the work is being done again, or link the item for what is being done now.",
        WorkGateVerdict.PlanIsEmpty =>
            "The plan this conversation names is empty. It needs to say what will change and why before the agent "
            + "can change the workspace.",
        WorkGateVerdict.PlanDoesNotCover =>
            "The plan this conversation names does not mention its work items, so it reads as a plan for something "
            + "else. Link the items to the plan, or link a plan written for this work.",
        _ => verdict.SentenceFor() is { } sentence ? StudioToolDescriptions.Humanize(sentence) : null,
    };
}
