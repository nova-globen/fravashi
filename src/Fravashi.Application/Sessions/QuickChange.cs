using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.Sessions;

/// <summary>What a quick change produced: a real item, a real plan, and the binding between them.</summary>
/// <param name="Item">The work item, at Ready, with the done-check on it.</param>
/// <param name="Plan">The plan document, which names the item.</param>
public sealed record QuickChangeResult(WorkItem Item, WikiItem Plan);

/// <summary>
/// The quick-change path (backlog 85): one action that creates a minimal work item, an inline plan and a
/// done-check, and binds the session to both, so a typo fix costs a click rather than a conversation.
/// <para>
/// <b>It is not a bypass, and that is the whole design.</b> Nothing here skips a gate. It creates the
/// artifacts the gates ask for, properly linked, and the gates then pass for the ordinary reason. The
/// difference between this and doing it by hand is the number of steps, not the result.
/// </para>
/// <para>
/// Every artifact it creates SAYS it was made this way, in words, so it can be found and reconciled later.
/// A quick change that looked exactly like a considered one would quietly lower the standard of everything
/// on the board, which is the failure mode item 85's note is about.
/// </para>
/// </summary>
public sealed class QuickChange(
    ITrackerResolver trackers,
    IWikiLibraries wikis,
    ISessionWorkItems work,
    ISessionPlans plans)
{
    /// <summary>The sentence every quick-change artifact carries, so the set of them is findable.</summary>
    public const string Marker = "Made through the quick-change path and not yet reconciled.";

    /// <summary>The done-check a quick change gets when the caller offers none.</summary>
    public const string DefaultDoneCheck = "The change described above is in place and the suite is green.";

    /// <summary>
    /// Creates the item, the plan and the binding, in that order, and binds the session to both.
    /// <paramref name="summary"/> is the one line a person would have typed; everything else is derived.
    /// </summary>
    public async Task<QuickChangeResult?> StartAsync(
        AgentSessionId session,
        ProjectId project,
        string summary,
        string? doneCheck = null,
        string linkedBy = "user",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var title = summary.Trim();
        var tracker = trackers.For(project);

        // Ready rather than Triage: the work is being done now, and a board that showed it as unconsidered
        // would be telling the truth about its origin and a lie about its state.
        var item = await tracker.CreateAsync(
            new NewWorkItem
            {
                Level = WorkItemLevel.Task,
                Title = title,
                Description = title + "\n\n" + Marker,
                Status = WorkItemStatus.Ready,
                AcceptanceCriteria =
                [
                    new AcceptanceCriterion(
                        string.Empty,
                        string.IsNullOrWhiteSpace(doneCheck) ? DefaultDoneCheck : doneCheck.Trim()),
                ],
            },
            new MutationContext(WorkItemActor.User),
            ct).ConfigureAwait(false);

        if (!item.IsOk)
        {
            return null;
        }

        // The plan REFERENCES the item in its prose, which is what makes the coverage check pass, and
        // links it as well, so the item's own page shows the plan without anyone adding it.
        var plan = await wikis.For(project).CreateAsync(
            new NewWikiItem
            {
                Type = WikiItemType.Plan,
                Title = "Quick change: " + title,
                Body =
                    $"{title}\n\nThis plan covers #{item.Value.Number}.\n\n{Marker}",
                Links = [new WikiLink(WikiLinkKind.WorkItem, item.Value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), title)],
            },
            ct).ConfigureAwait(false);

        if (!plan.IsValid)
        {
            return null;
        }

        work.Link(session, item.Value.Id, project, linkedBy);
        plans.Link(session, plan.Value.Id, linkedBy);

        return new QuickChangeResult(item.Value, plan.Value);
    }
}
