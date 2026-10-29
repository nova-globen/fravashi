using System.Globalization;
using System.Text;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Sessions;

/// <summary>
/// What kind of call a counted decision was about (backlog 244), decided from the tool's NAME alone. A closed set, so
/// a count by kind never grows a row for a tool nobody anticipated: that call is <see cref="Other"/>.
/// </summary>
public enum AttentionToolKind
{
    /// <summary>A read: the built-in reads, the studio's graph, git and CI reads.</summary>
    Read,

    /// <summary>An edit to an existing file (Edit, MultiEdit, NotebookEdit).</summary>
    Edit,

    /// <summary>A file written whole (Write).</summary>
    Write,

    /// <summary>A command (Bash and its companions, a scenario run).</summary>
    Shell,

    /// <summary>A git act other than a commit: a push, a tag, an attempt branch.</summary>
    Git,

    /// <summary>A commit (<c>git_commit</c> or <c>attempt_commit</c>), the act a commit review stands in front of.</summary>
    Commit,

    /// <summary>A work item moved to another status (<c>work_item_transition</c>).</summary>
    StatusMove,

    /// <summary>Any other write to the studio's graph: an item, a document, a link, a design, a prototype.</summary>
    GraphWrite,

    /// <summary>The session's browser.</summary>
    Browser,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>How one counted decision ended (backlog 244).</summary>
public enum AttentionOutcome
{
    /// <summary>A person approved the card.</summary>
    Approved,

    /// <summary>A person denied the card and gave no reason.</summary>
    Denied,

    /// <summary>A person denied the card and gave a reason (backlog 220's channel).</summary>
    DeniedWithReason,

    /// <summary>The studio's own rules let it run without asking anyone.</summary>
    Auto,

    /// <summary>
    /// The autopilot's own rule list let it run without asking anyone (ADR 0031 decision 7), never set for a
    /// session the autopilot does not mark.
    /// </summary>
    AutopilotRule,

    /// <summary>
    /// The autopilot's TypeSafe judge gave a unanimous safe answer (ADR 0031 decision 8), never set for a
    /// session the autopilot does not mark.
    /// </summary>
    AutopilotJudge,

    /// <summary>The studio refused it without asking anyone.</summary>
    Refused,

    /// <summary>A card nobody answered before the studio's timeout.</summary>
    TimedOut,

    /// <summary>A card the studio stopped waiting on for another reason: the session ended, the hook went away, a shutdown.</summary>
    Withdrawn,
}

/// <summary>Which of the three things a row of the attention record is.</summary>
public enum AttentionEntryKind
{
    /// <summary>One decision the approval broker made on a tool call.</summary>
    Decision,

    /// <summary>One turn a person typed. The earliest is the opening prompt; every later one is guidance.</summary>
    Turn,

    /// <summary>One commit review written on the building conversation (backlog 243).</summary>
    Review,
}

/// <summary>
/// One row of the attention record. It holds no content by construction: an id, closed-set names, a wait and a time.
/// </summary>
/// <param name="Session">The conversation it belongs to.</param>
/// <param name="Entry">What the row is.</param>
/// <param name="Kind">The tool kind, on a decision.</param>
/// <param name="Outcome">How it ended, on a decision.</param>
/// <param name="Review">The verdict, on a review.</param>
/// <param name="Waited">How long a person was waited on, on a decision that asked; zero otherwise.</param>
/// <param name="OccurredUtc">When it happened.</param>
/// <param name="Reconstructed">True for a row rebuilt from the transcript of a conversation that ran before counting existed.</param>
public sealed record AttentionEntry(
    AgentSessionId Session,
    AttentionEntryKind Entry,
    AttentionToolKind? Kind,
    AttentionOutcome? Outcome,
    CommitReviewOutcome? Review,
    TimeSpan Waited,
    DateTimeOffset OccurredUtc,
    bool Reconstructed = false)
{
    /// <summary>A broker decision.</summary>
    public static AttentionEntry Decision(
        AgentSessionId session, AttentionToolKind kind, AttentionOutcome outcome, TimeSpan waited, DateTimeOffset at,
        bool reconstructed = false) =>
        new(session, AttentionEntryKind.Decision, kind, outcome, null, waited < TimeSpan.Zero ? TimeSpan.Zero : waited, at, reconstructed);

    /// <summary>A person's turn.</summary>
    public static AttentionEntry Turn(AgentSessionId session, DateTimeOffset at, bool reconstructed = false) =>
        new(session, AttentionEntryKind.Turn, null, null, null, TimeSpan.Zero, at, reconstructed);

    /// <summary>A commit review's verdict.</summary>
    public static AttentionEntry ReviewOf(
        AgentSessionId session, CommitReviewOutcome review, DateTimeOffset at, bool reconstructed = false) =>
        new(session, AttentionEntryKind.Review, null, null, review, TimeSpan.Zero, at, reconstructed);
}

/// <summary>
/// The closed vocabulary the attention record is stored and quoted in: lower-snake names, never ordinals.
/// </summary>
public static class AttentionNames
{
    private static readonly Dictionary<AttentionToolKind, string> KindNames = new()
    {
        [AttentionToolKind.Read] = "read",
        [AttentionToolKind.Edit] = "edit",
        [AttentionToolKind.Write] = "write",
        [AttentionToolKind.Shell] = "shell",
        [AttentionToolKind.Git] = "git",
        [AttentionToolKind.Commit] = "commit",
        [AttentionToolKind.StatusMove] = "status_move",
        [AttentionToolKind.GraphWrite] = "graph_write",
        [AttentionToolKind.Browser] = "browser",
        [AttentionToolKind.Other] = "other",
    };

    private static readonly Dictionary<AttentionOutcome, string> OutcomeNames = new()
    {
        [AttentionOutcome.Approved] = "approved",
        [AttentionOutcome.Denied] = "denied",
        [AttentionOutcome.DeniedWithReason] = "denied_with_reason",
        [AttentionOutcome.Auto] = "auto",
        [AttentionOutcome.AutopilotRule] = "autopilot_rule",
        [AttentionOutcome.AutopilotJudge] = "autopilot_judge",
        [AttentionOutcome.Refused] = "refused",
        [AttentionOutcome.TimedOut] = "timed_out",
        [AttentionOutcome.Withdrawn] = "withdrawn",
    };

    private static readonly Dictionary<CommitReviewOutcome, string> ReviewNames = new()
    {
        [CommitReviewOutcome.Passed] = "pass",
        [CommitReviewOutcome.Failed] = "fail",
        [CommitReviewOutcome.Off] = "not_reviewed",
        [CommitReviewOutcome.TimedOut] = "no_verdict_bound",
        [CommitReviewOutcome.Errored] = "no_verdict_error",
    };

    private static readonly Dictionary<AttentionEntryKind, string> EntryNames = new()
    {
        [AttentionEntryKind.Decision] = "decision",
        [AttentionEntryKind.Turn] = "turn",
        [AttentionEntryKind.Review] = "review",
    };

    /// <summary>The stored name of a tool kind.</summary>
    public static string Of(AttentionToolKind kind) => KindNames[kind];

    /// <summary>The stored name of an outcome.</summary>
    public static string Of(AttentionOutcome outcome) => OutcomeNames[outcome];

    /// <summary>The stored name of a review verdict.</summary>
    public static string Of(CommitReviewOutcome review) => ReviewNames[review];

    /// <summary>The stored name of an entry kind.</summary>
    public static string Of(AttentionEntryKind entry) => EntryNames[entry];

    /// <summary>Reads a stored tool kind; null for a name this version does not know.</summary>
    public static AttentionToolKind? KindFrom(string? name) => Reverse(KindNames, name);

    /// <summary>Reads a stored outcome; null for a name this version does not know.</summary>
    public static AttentionOutcome? OutcomeFrom(string? name) => Reverse(OutcomeNames, name);

    /// <summary>Reads a stored review verdict; null for a name this version does not know.</summary>
    public static CommitReviewOutcome? ReviewFrom(string? name) => Reverse(ReviewNames, name);

    /// <summary>Reads a stored entry kind; null for a name this version does not know.</summary>
    public static AttentionEntryKind? EntryFrom(string? name) => Reverse(EntryNames, name);

    private static T? Reverse<T>(Dictionary<T, string> names, string? name)
        where T : struct, Enum
    {
        foreach (var (value, text) in names)
        {
            if (string.Equals(text, name, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>
/// Which <see cref="AttentionToolKind"/> a tool name is (backlog 244). By NAME only, never by argument: the count says
/// what kind of thing a person was asked about, and nothing here may read what the call carried.
/// </summary>
public static class AttentionToolKinds
{
    private const string Prefix = AgentWorkGate.StudioToolPrefix;

    private static readonly HashSet<string> StudioReads = new(StringComparer.Ordinal)
    {
        "studio_version", "wiki_find", "wiki_read", "mockup_check", "mockup_schema", "prototype_check",
        "work_items_find", "work_item_read", "work_items_startable", "work_item_releases", "session_work_read", "session_governance_read",
        "session_attention", "git_status", "git_log", "git_show", "git_diff", "git_blame", "ci_runs", "ci_run_log",
    };

    /// <summary>The kind of a call to <paramref name="toolName"/>.</summary>
    public static AttentionToolKind Of(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return AttentionToolKind.Other;
        }

        if (toolName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Studio(toolName[Prefix.Length..]);
        }

        return toolName switch
        {
            "Edit" or "MultiEdit" or "NotebookEdit" => AttentionToolKind.Edit,
            "Write" => AttentionToolKind.Write,
            "Bash" or "BashOutput" or "KillShell" or "KillBash" => AttentionToolKind.Shell,
            "WebFetch" or "WebSearch" => AttentionToolKind.Read,
            _ when AgentToolSafety.IsReadOnly(toolName) => AttentionToolKind.Read,
            _ => AttentionToolKind.Other,
        };
    }

    private static AttentionToolKind Studio(string name)
    {
        if (StudioReads.Contains(name))
        {
            return AttentionToolKind.Read;
        }

        // Backlog 250: attempt_commit is reviewed like git_commit, so it is counted as the same act.
        if (name is "git_commit" or "attempt_commit")
        {
            return AttentionToolKind.Commit;
        }

        if (name == "work_item_transition")
        {
            return AttentionToolKind.StatusMove;
        }

        if (name == "work_item_scenario_run")
        {
            return AttentionToolKind.Shell;
        }

        if (name.StartsWith("browser_", StringComparison.Ordinal))
        {
            return AttentionToolKind.Browser;
        }

        if (name.StartsWith("git_", StringComparison.Ordinal) || name.StartsWith("attempt_", StringComparison.Ordinal))
        {
            return AttentionToolKind.Git;
        }

        if (name.StartsWith("work_item", StringComparison.Ordinal)
            || name.StartsWith("wiki_", StringComparison.Ordinal)
            || name.StartsWith("session_", StringComparison.Ordinal)
            || name.StartsWith("design_", StringComparison.Ordinal)
            || name.StartsWith("prototype_", StringComparison.Ordinal)
            || name == "quick_change")
        {
            return AttentionToolKind.GraphWrite;
        }

        return AttentionToolKind.Other;
    }
}

/// <summary>
/// One conversation's attention, or several rolled up (backlog 244). Every surface that shows the counts (the session
/// pane, the work item page, the <c>session_attention</c> tool) reads this type and its <see cref="Line"/>, so they
/// cannot disagree with each other.
/// <para>
/// The headline is what a PERSON had to answer: <see cref="Asked"/>. Calls the studio allowed or refused on its own are
/// counted beside it and never inside it, because reads inside the copy run to hundreds a conversation and would bury
/// the one number the item exists for.
/// </para>
/// </summary>
public sealed record SessionAttention(
    IReadOnlyDictionary<AttentionOutcome, int> Outcomes,
    IReadOnlyDictionary<AttentionToolKind, int> AskedByKind,
    TimeSpan Waited,
    int GuidanceTurns,
    IReadOnlyDictionary<CommitReviewOutcome, int> Reviews,
    bool Reconstructed)
{
    /// <summary>Nothing counted.</summary>
    public static SessionAttention Empty { get; } = new(
        new Dictionary<AttentionOutcome, int>(),
        new Dictionary<AttentionToolKind, int>(),
        TimeSpan.Zero,
        0,
        new Dictionary<CommitReviewOutcome, int>(),
        false);

    /// <summary>What the transcript of a conversation that ran before counting cannot say, in words.</summary>
    public const string ReconstructedNote =
        "counted in part from the transcript of a conversation that ran before counting existed, which kept no calls the studio allowed without asking";

    /// <summary>The outcomes a person was asked for, in the order they are reported.</summary>
    public static IReadOnlyList<AttentionOutcome> AskedOutcomes { get; } =
    [
        AttentionOutcome.Approved,
        AttentionOutcome.DeniedWithReason,
        AttentionOutcome.Denied,
        AttentionOutcome.TimedOut,
        AttentionOutcome.Withdrawn,
    ];

    /// <summary>How many times an outcome occurred.</summary>
    public int Count(AttentionOutcome outcome) => Outcomes.TryGetValue(outcome, out var n) ? n : 0;

    /// <summary>How many reviews ended in a verdict.</summary>
    public int Count(CommitReviewOutcome review) => Reviews.TryGetValue(review, out var n) ? n : 0;

    /// <summary>Approvals a person was asked for: every card, however it ended.</summary>
    public int Asked => AskedOutcomes.Sum(o => Count(o));

    /// <summary>Calls the studio let run on its own rules.</summary>
    public int Auto => Count(AttentionOutcome.Auto);

    /// <summary>Calls the autopilot's own rule list let run (ADR 0031 decision 7).</summary>
    public int ByRule => Count(AttentionOutcome.AutopilotRule);

    /// <summary>Calls the autopilot's TypeSafe judge let run on a unanimous safe answer (ADR 0031 decision 8).</summary>
    public int ByJudge => Count(AttentionOutcome.AutopilotJudge);

    /// <summary>Calls the studio refused without asking anyone.</summary>
    public int Refused => Count(AttentionOutcome.Refused);

    /// <summary>Every commit review.</summary>
    public int ReviewCount => Reviews.Values.Sum();

    /// <summary>Counts one conversation's rows. The earliest turn is its opening prompt; every later one is guidance.</summary>
    public static SessionAttention From(IEnumerable<AttentionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var outcomes = new Dictionary<AttentionOutcome, int>();
        var kinds = new Dictionary<AttentionToolKind, int>();
        var reviews = new Dictionary<CommitReviewOutcome, int>();
        var waited = TimeSpan.Zero;
        var turns = 0;
        var reconstructed = false;

        foreach (var entry in entries)
        {
            reconstructed |= entry.Reconstructed;
            switch (entry.Entry)
            {
                case AttentionEntryKind.Decision when entry.Outcome is { } outcome:
                    Add(outcomes, outcome);
                    if (AskedOutcomes.Contains(outcome))
                    {
                        Add(kinds, entry.Kind ?? AttentionToolKind.Other);
                        waited += entry.Waited;
                    }

                    break;

                case AttentionEntryKind.Turn:
                    turns++;
                    break;

                case AttentionEntryKind.Review when entry.Review is { } review:
                    Add(reviews, review);
                    break;
            }
        }

        return new SessionAttention(outcomes, kinds, waited, Math.Max(0, turns - 1), reviews, reconstructed);
    }

    /// <summary>Two counts added together, which is how a work item's conversations roll up.</summary>
    public SessionAttention Plus(SessionAttention other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new SessionAttention(
            Sum(Outcomes, other.Outcomes),
            Sum(AskedByKind, other.AskedByKind),
            Waited + other.Waited,
            GuidanceTurns + other.GuidanceTurns,
            Sum(Reviews, other.Reviews),
            Reconstructed || other.Reconstructed);
    }

    /// <summary>Several conversations' counts rolled up.</summary>
    public static SessionAttention Total(IEnumerable<SessionAttention> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return parts.Aggregate(Empty, (sum, part) => sum.Plus(part));
    }

    /// <summary>
    /// The one line every surface shows, e.g. "17 approvals asked: 12 approved, 2 denied with a reason, 3 denied; waited
    /// 4 min 20 s in all; 140 auto, 3 refused without asking; 2 guidance turns; 1 review: pass". A part with nothing to
    /// count is left out, except the headline.
    /// </summary>
    public string Line
    {
        get
        {
            var text = new StringBuilder();
            text.Append(Plural(Asked, "approval")).Append(" asked");

            var answered = AskedOutcomes
                .Where(o => Count(o) > 0)
                .Select(o => Number(Count(o)) + " " + Words(o))
                .ToList();
            if (answered.Count > 0)
            {
                text.Append(": ").Append(string.Join(", ", answered));
            }

            if (Asked > 0)
            {
                text.Append("; waited ").Append(Duration(Waited)).Append(" in all");
            }

            var unasked = new List<string>();
            if (Auto > 0)
            {
                unasked.Add(Number(Auto) + " auto");
            }

            if (Refused > 0)
            {
                unasked.Add(Number(Refused) + " refused without asking");
            }

            if (ByRule > 0)
            {
                unasked.Add(Number(ByRule) + " by rule");
            }

            if (ByJudge > 0)
            {
                unasked.Add(Number(ByJudge) + " by judge");
            }

            if (unasked.Count > 0)
            {
                text.Append("; ").Append(string.Join(", ", unasked));
            }

            if (GuidanceTurns > 0)
            {
                text.Append("; ").Append(Plural(GuidanceTurns, "guidance turn"));
            }

            if (ReviewCount > 0)
            {
                var verdicts = Reviews
                    .Where(r => r.Value > 0)
                    .OrderBy(r => r.Key)
                    .Select(r => ReviewCount == 1 ? Verdict(r.Key) : Number(r.Value) + " " + Verdict(r.Key));
                text.Append("; ").Append(Plural(ReviewCount, "review")).Append(": ").Append(string.Join(", ", verdicts));
            }

            return text.ToString();
        }
    }

    /// <summary>The asked approvals by tool kind, e.g. "commit 2, edit 9, shell 6"; empty when none were asked.</summary>
    public string ByKind => string.Join(
        ", ",
        AskedByKind.Where(k => k.Value > 0).OrderBy(k => k.Key).Select(k => AttentionNames.Of(k.Key).Replace('_', ' ') + " " + Number(k.Value)));

    /// <summary>A wait in words: "45 s", "4 min 20 s", "1 h 3 min".</summary>
    public static string Duration(TimeSpan span)
    {
        var seconds = (long)Math.Round(Math.Max(0, span.TotalSeconds));
        if (seconds < 60)
        {
            return Number(seconds) + " s";
        }

        if (seconds < 3600)
        {
            var rest = seconds % 60;
            return Number(seconds / 60) + " min" + (rest > 0 ? " " + Number(rest) + " s" : string.Empty);
        }

        var minutes = seconds / 60 % 60;
        return Number(seconds / 3600) + " h" + (minutes > 0 ? " " + Number(minutes) + " min" : string.Empty);
    }

    /// <summary>The words an outcome is reported in.</summary>
    public static string Words(AttentionOutcome outcome) => outcome switch
    {
        AttentionOutcome.Approved => "approved",
        AttentionOutcome.Denied => "denied",
        AttentionOutcome.DeniedWithReason => "denied with a reason",
        AttentionOutcome.Auto => "auto",
        AttentionOutcome.AutopilotRule => "by rule",
        AttentionOutcome.AutopilotJudge => "by judge",
        AttentionOutcome.Refused => "refused without asking",
        AttentionOutcome.TimedOut => "timed out",
        _ => "withdrawn unanswered",
    };

    /// <summary>The word a review verdict is reported in, the same words the review's transcript note uses.</summary>
    public static string Verdict(CommitReviewOutcome review) => review switch
    {
        CommitReviewOutcome.Passed => "pass",
        CommitReviewOutcome.Failed => "fail",
        CommitReviewOutcome.Off => "not reviewed",
        _ => "no verdict",
    };

    private static string Plural(long n, string noun) => Number(n) + " " + noun + (n == 1 ? string.Empty : "s");

    private static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static void Add<T>(Dictionary<T, int> counts, T key)
        where T : notnull =>
        counts[key] = (counts.TryGetValue(key, out var n) ? n : 0) + 1;

    private static Dictionary<T, int> Sum<T>(IReadOnlyDictionary<T, int> a, IReadOnlyDictionary<T, int> b)
        where T : notnull
    {
        var sum = new Dictionary<T, int>(a);
        foreach (var (key, n) in b)
        {
            sum[key] = (sum.TryGetValue(key, out var m) ? m : 0) + n;
        }

        return sum;
    }
}

/// <summary>One conversation linked to a work item, with its counts.</summary>
public sealed record LinkedSessionAttention(AgentSessionId Session, SessionAttention Attention);

/// <summary>A work item's conversations and their counts rolled up (backlog 244).</summary>
public sealed record WorkItemAttention(IReadOnlyList<LinkedSessionAttention> Sessions)
{
    /// <summary>Every linked conversation's counts added together.</summary>
    public SessionAttention Total => SessionAttention.Total(Sessions.Select(s => s.Attention));
}

/// <summary>
/// The operator's attention per conversation (backlog 244): every decision the approval broker makes, every turn a person
/// types, every commit review. Kept beside the approval ledger in the studio's database.
/// <para>
/// <b>Recording never throws</b>: the broker calls it on every tool call, and a counting failure must never become a
/// refused or an allowed call. A failure is logged and the count is short by one, which is the honest cost.
/// </para>
/// </summary>
public interface ISessionAttention
{
    /// <summary>Raised after a row is recorded for a conversation, so a surface showing its line can follow.</summary>
    event Action<AgentSessionId>? Changed;

    /// <summary>Records one row.</summary>
    void Record(AttentionEntry entry);

    /// <summary>One conversation's counts.</summary>
    SessionAttention For(AgentSessionId session);

    /// <summary>Every conversation linked to the item, each with its counts.</summary>
    WorkItemAttention ForItem(WorkItemId item);

    /// <summary>Drops a conversation's rows, when the conversation itself is deleted.</summary>
    void Forget(AgentSessionId session);
}
