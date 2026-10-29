using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Insights;

namespace Fravashi.Application.Sessions;

/// <summary>
/// A persisted agent session: enough to list it, reopen its transcript, and resume the conversation
/// with the CLI under <see cref="ProviderSessionId"/>.
/// </summary>
/// <param name="Title">
/// The title derived from the first prompt (the registry's <c>TitleFrom</c>). It is what an unrenamed
/// conversation shows, and a rename never overwrites it: it stays as the fallback.
/// </param>
/// <param name="CustomTitle">
/// A title the owner chose (backlog 109), or null while they never renamed the conversation. Surfaces
/// show <see cref="DisplayTitle"/>, never this or <paramref name="Title"/> directly.
/// </param>
public sealed record SessionRecord(
    AgentSessionId Id,
    string WorkspaceId,
    AgentTool Tool,
    string Title,
    string? ProviderSessionId,
    AgentSessionState State,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    string? CustomTitle = null)
{
    /// <summary>What to show for this conversation: the owner's chosen title, else the derived one.</summary>
    public string DisplayTitle => CustomTitle is { Length: > 0 } custom ? custom : Title;
}

/// <summary>
/// Durable index of agent sessions and their transcripts (plan 2e Task 5), in the studio's own
/// database on the data volume. It is what makes a session survive a studio restart: the list, the
/// transcript to reopen, and the CLI session id to resume.
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// Writes or updates a session's record. A null <see cref="SessionRecord.CustomTitle"/> means "no
    /// rename carried here", never "clear the name": a stored custom title survives a save that does not
    /// carry one, so a lifecycle save built from a session object can never put the derived title back.
    /// </summary>
    void Save(SessionRecord record);

    /// <summary>One session, or null when the id is unknown.</summary>
    SessionRecord? Find(AgentSessionId id);

    /// <summary>A workspace's sessions, newest first.</summary>
    IReadOnlyList<SessionRecord> ForWorkspace(string workspaceId, int max = 50);

    /// <summary>
    /// The most recent sessions across EVERY workspace, newest first (backlog 34). The home surface is
    /// not bound to one workspace, so it cannot be served by <see cref="ForWorkspace"/> a workspace at a
    /// time: the ten newest overall are not the newest of any list the caller could stitch together
    /// without reading every workspace's whole history first.
    /// </summary>
    IReadOnlyList<SessionRecord> Recent(int max = 10);

    /// <summary>
    /// Every row whose state is not terminal (backlog 156): after a restart these are the conversations whose
    /// process went away with the studio. The default reads the recent rows, which is enough for a store that
    /// holds few; the SQLite store asks the table.
    /// </summary>
    IReadOnlyList<SessionRecord> Unfinished() => [.. Recent(200).Where(r => !r.State.IsTerminal())];

    /// <summary>Appends transcript events in order. Transient events (partial text) are not persisted.</summary>
    void Append(AgentSessionId id, IEnumerable<AgentEvent> events);

    /// <summary>The persisted transcript, oldest first.</summary>
    IReadOnlyList<AgentEvent> Transcript(AgentSessionId id);

    /// <summary>Forgets a session and its transcript.</summary>
    void Delete(AgentSessionId id);

    /// <summary>
    /// Cost and billed tokens summed across every session in every workspace (backlog 19): the current
    /// day (sessions started at or after <paramref name="dayStartUtc"/>), the current month (at or after
    /// <paramref name="monthStartUtc"/>) and all time. Each session contributes its own persisted usage
    /// summary, the same figure its strip shows, so the totals reconcile with the sessions to the cent.
    /// Reads the small <c>agent_sessions</c> table, one row per session, never the transcript-events table.
    /// <para>
    /// A session is counted in the window it STARTED in, not spread across the windows it ran through.
    /// That is the same rule backlog 19 already used for the month, and it is the only one available
    /// here: the per-session summary is a single cumulative figure with no time inside it.
    /// </para>
    /// </summary>
    SessionUsageTotals ReadUsageTotals(DateTimeOffset dayStartUtc, DateTimeOffset monthStartUtc);

    /// <summary>
    /// Cost and session count per UTC day between <paramref name="fromDayUtc"/> and
    /// <paramref name="toDayUtc"/> inclusive (backlog 36), from the same per-session usage summary
    /// <see cref="ReadUsageTotals"/> reads, so a day's bar and the month's total are the same numbers.
    /// <para>
    /// SPARSE: only days that actually had a session come back, and a day with no session is absent
    /// rather than a zero row. A caller drawing a calendar fills its own gaps, and a caller asking
    /// "was there anything at all" can tell an empty period from a period of zero-cost runs.
    /// </para>
    /// </summary>
    IReadOnlyList<SessionDay> ReadSessionDays(DateOnly fromDayUtc, DateOnly toDayUtc);

    /// <summary>
    /// How many turns completed per UTC day between <paramref name="fromDayUtc"/> and
    /// <paramref name="toDayUtc"/> inclusive (backlog 36), counted from the persisted transcripts:
    /// one row per <c>TurnCompleted</c> event, which is the CLI's own <c>result</c> line and therefore
    /// a turn that actually finished rather than one that was merely asked for. Sparse, like
    /// <see cref="ReadSessionDays"/>.
    /// </summary>
    IReadOnlyList<DayCount> ReadTurnDays(DateOnly fromDayUtc, DateOnly toDayUtc);

    /// <summary>
    /// The median and longest wall-clock duration of the sessions STARTED at or after
    /// <paramref name="fromUtc"/> that have since ended (backlog 36). A session still running has no
    /// duration yet and is not counted: including it would report a figure that grows while nobody is
    /// doing anything.
    /// </summary>
    DurationStats ReadSessionDurations(DateTimeOffset fromUtc);
}
