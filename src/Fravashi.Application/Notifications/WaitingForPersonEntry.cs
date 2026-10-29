using System.Globalization;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Notifications;

/// <summary>What kind of thing is waiting on a person (ADR 0031 decision 12).</summary>
public enum WaitingForPersonKind
{
    /// <summary>A tool call parked in a session, waiting on an approval.</summary>
    Approval,

    /// <summary>The agent asked a question and is waiting for the answer.</summary>
    Question,

    /// <summary>An autopilot item parked, waiting on a person, while the run moved on.</summary>
    Parked,
}

/// <summary>
/// One thing waiting on a person: every approval and question across every live session, and every parked
/// autopilot item, in one shape (ADR 0031 decision 12, backlog 304).
/// </summary>
/// <param name="Kind">What kind of wait this is.</param>
/// <param name="What">
/// Words a person reads on the home page's list. Safe to show in full there; a toast channel must NOT reuse
/// this verbatim (it can carry a tool name or a shortened question), and builds its own generic sentence from
/// <see cref="Kind"/> instead.
/// </param>
/// <param name="SinceUtc">When this particular wait began.</param>
/// <param name="WorkspaceId">The workspace the entry's session or run belongs to; empty when none is known.</param>
/// <param name="SessionId">The session this entry is about, for <see cref="WaitingForPersonKind.Approval"/> and <see cref="WaitingForPersonKind.Question"/>.</param>
/// <param name="Item">The work item this entry is about, for <see cref="WaitingForPersonKind.Parked"/>.</param>
/// <param name="Project">The project the parked item's run belongs to.</param>
/// <param name="ItemNumber">
/// The parked item's board number, for <see cref="WaitingForPersonKind.Parked"/>: a channel may show this on
/// its own (it is not content, just which item), unlike <see cref="What"/>, which can carry a tool name or a
/// shortened question and must never cross into a toast.
/// </param>
/// <param name="Run">The autopilot run a parked item belongs to (work item #356): what a dismissal is recorded against.</param>
/// <param name="Reason">Why a parked item parked, as the stage wrote it, without the item's label around it (work item #356).</param>
/// <param name="RunEnded">True when the parked item's run has ended, so dispatching again is the only way to resume it.</param>
public sealed record WaitingForPersonEntry(
    WaitingForPersonKind Kind,
    string What,
    DateTimeOffset SinceUtc,
    string WorkspaceId,
    AgentSessionId? SessionId = null,
    WorkItemId? Item = null,
    ProjectId? Project = null,
    int? ItemNumber = null,
    Guid? Run = null,
    string? Reason = null,
    bool RunEnded = false)
{
    /// <summary>
    /// The toast dedupe identity (backlog 304): a new <see cref="SinceUtc"/> for the same session or item is a
    /// genuinely new wait (the earlier one was resolved and a different one appeared), so it is a new key; the
    /// same wait, read again on a later poll, is not.
    /// </summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Kind}|{SessionId}|{Item?.Value}|{SinceUtc:O}");
}
