using System.Security.Cryptography;
using System.Text;

namespace Fravashi.Domain.Telemetry;

/// <summary>
/// The things that decide whether the product's loop works (backlog 74). Each kind is one measurable
/// fact about a run; the numbers that go with it ride on <see cref="ProductEvent"/> and nothing else
/// does, because the payload has no field text could travel in.
/// <para>
/// Two kinds at the bottom have <b>no producer</b> and are documented as such: verification flakiness
/// and time-to-a-verified-change describe a verification loop that does not exist until roadmap Phase E.
/// They are declared here so the reliability targets can name the event that will measure them
/// (<c>docs/reliability-targets.md</c>) rather than leaving the target unattached; nothing writes them
/// today, and a reader of the recorded events will correctly find none.
/// </para>
/// </summary>
public enum ProductEventKind
{
    /// <summary>The onboarding wizard finished. Duration: from the wizard's first advance to Done.</summary>
    OnboardingCompleted,

    /// <summary>
    /// A session was started against an agent CLI. Outcome says whether the CLI came up and announced
    /// itself; on success the duration is how long that took from the user pressing send.
    /// </summary>
    SessionStart,

    /// <summary>
    /// The agent CLI itself could not be found or launched, so no session was possible: the studio had
    /// no tool to discover. Distinct from <see cref="SessionStart"/> failing later, because a missing
    /// runtime is an install problem and a failed handshake is a protocol problem.
    /// </summary>
    ToolDiscoveryFailed,

    /// <summary>
    /// A parked tool call was answered. This ONE kind carries both facts the item asks for: the
    /// outcome distinguishes an approval from a denial and says who denied it (the user, or the studio
    /// failing closed), and the duration is the approval latency, request to answer. A second kind per
    /// denial would write two rows for one decision and make every latency query pick a side.
    /// </summary>
    ApprovalResolved,

    /// <summary>
    /// A turn burned validation attempts before it ended (ADR 0022: the CLI's harness owns the retry).
    /// Count: how many attempts its validator rejected.
    /// </summary>
    TurnRetried,

    /// <summary>
    /// A turn on a session that asked for a typed document ended successfully WITHOUT one (ADR 0022
    /// outcome ii): the model gave up on the schema and answered in prose. The studio commits that prose
    /// under an untyped marker and never resends (decided 2026-09-14, backlog 25), and this is the
    /// count that says how often that happens. Count: always one.
    /// </summary>
    TurnUntyped,

    /// <summary>A session ended without completing: cancelled by the user, or failed.</summary>
    SessionAborted,

    /// <summary>
    /// NO PRODUCER until roadmap Phase E. A verification that passes and fails over the same change is
    /// flaky; nothing in the product verifies anything yet, so nothing writes this.
    /// </summary>
    VerificationFlaked,

    /// <summary>
    /// NO PRODUCER until roadmap Phase E. Duration: from a change starting to that change being
    /// verified. The verify half of the loop does not exist yet, so nothing writes this.
    /// </summary>
    VerifiedChangeReached,
}

/// <summary>
/// How an event turned out. Enum-like by construction (backlog 74 decision c): an outcome is picked
/// from this closed set, so nothing about a run can be described in words that carry content with them.
/// </summary>
public enum ProductEventOutcome
{
    /// <summary>Nothing to say about the outcome: the event is a count or a duration on its own.</summary>
    Unspecified = 0,

    /// <summary>The thing worked.</summary>
    Succeeded,

    /// <summary>The thing did not work.</summary>
    Failed,

    /// <summary>The user stopped it.</summary>
    Cancelled,

    /// <summary>A tool call the user approved.</summary>
    Approved,

    /// <summary>A tool call the user refused.</summary>
    DeniedByUser,

    /// <summary>
    /// A tool call the studio refused on the user's behalf: a timeout, a released session, a shutdown.
    /// Kept apart from <see cref="DeniedByUser"/> for the same reason the transcript keeps them apart
    /// (backlog 114): they are the same "not approved" and mean entirely different things.
    /// </summary>
    DeniedByStudio,
}

/// <summary>
/// A workspace's identity in a recorded event, and the ONLY string-shaped thing an event can carry.
/// <para>
/// It is a hash, never an id, because a workspace id is derived from a folder path and the whole point
/// of decision (c) is that a recorded event cannot carry a path. <see cref="Of"/> is one-way (SHA-256,
/// first eight bytes, lower-case hex), so the value is exactly sixteen characters from the alphabet
/// <c>0-9a-f</c> whatever goes in, and the same workspace always hashes to the same sixteen characters,
/// which is what makes "did this keep failing in one workspace" answerable without naming it.
/// </para>
/// <para>
/// Nothing else can construct one: the constructor is private and <see cref="Parse"/> refuses anything
/// that is not already in that shape, so a caller cannot smuggle text through this field by assigning it.
/// </para>
/// </summary>
public readonly record struct WorkspaceHash
{
    /// <summary>The number of hex characters a hash always has.</summary>
    public const int Length = 16;

    private readonly string? _value;

    private WorkspaceHash(string value) => _value = value;

    /// <summary>No workspace: an event that is not about one (onboarding, for instance).</summary>
    public static WorkspaceHash None => default;

    /// <summary>True when this hash names a workspace.</summary>
    public bool HasValue => _value is not null;

    /// <summary>The sixteen hex characters, or an empty string when there is no workspace.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Hashes a workspace id. A blank id is <see cref="None"/>.</summary>
    public static WorkspaceHash Of(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return None;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(workspaceId));
        return new WorkspaceHash(Convert.ToHexString(digest.AsSpan(0, Length / 2)).ToLowerInvariant());
    }

    /// <summary>
    /// Reads a hash back from storage. Anything that is not already sixteen lower-case hex characters
    /// is <see cref="None"/>: a row hand-edited to hold a path reads back as no workspace at all,
    /// rather than putting that path in front of whoever opens the bundle.
    /// </summary>
    public static WorkspaceHash Parse(string? stored)
    {
        if (stored is null || stored.Length != Length)
        {
            return None;
        }

        foreach (var c in stored)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return None;
            }
        }

        return new WorkspaceHash(stored);
    }

    public override string ToString() => Value;
}

/// <summary>
/// One recorded product event (backlog 74). The payload is deliberately the smallest thing that can
/// answer the reliability questions: <b>what happened</b> (<see cref="Kind"/>), <b>when</b>
/// (<see cref="OccurredUtc"/>), <b>how it turned out</b> (<see cref="Outcome"/>, a closed enum),
/// <b>where</b> (<see cref="Workspace"/>, a one-way hash), and <b>how long or how many</b>
/// (<see cref="DurationMs"/> / <see cref="Count"/>).
/// <para>
/// <b>There is no free-text field, and that is the point.</b> Prompts, code, paths, commands,
/// transcripts, error messages and tool arguments have nowhere to go: the record has no
/// <see cref="string"/>-typed member at all, and the one string-SHAPED member is a
/// <see cref="WorkspaceHash"/> that can only ever hold sixteen hex characters. So "we only record
/// non-sensitive fields" is not a policy someone has to keep remembering; it is the type.
/// <c>ProductEventSchemaTests</c> is the standing guard.
/// </para>
/// <para>
/// Events are recorded LOCALLY, in the studio's own database, behind an opt-in that defaults off.
/// There is no remote destination and this type is not a wire format.
/// </para>
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="OccurredUtc">When, in UTC.</param>
/// <param name="Outcome">How it turned out, from the closed set.</param>
/// <param name="Workspace">The one-way workspace hash, or <see cref="WorkspaceHash.None"/>.</param>
/// <param name="DurationMs">How long it took in milliseconds, when the kind measures a duration.</param>
/// <param name="Count">How many, when the kind counts something.</param>
public sealed record ProductEvent(
    ProductEventKind Kind,
    DateTimeOffset OccurredUtc,
    ProductEventOutcome Outcome,
    WorkspaceHash Workspace,
    long? DurationMs = null,
    long? Count = null)
{
    /// <summary>The onboarding wizard finished, after <paramref name="duration"/> (null when unknown).</summary>
    public static ProductEvent OnboardingCompleted(DateTimeOffset at, TimeSpan? duration = null) =>
        new(ProductEventKind.OnboardingCompleted, at, ProductEventOutcome.Succeeded, WorkspaceHash.None,
            Milliseconds(duration));

    /// <summary>The CLI came up and announced itself, <paramref name="duration"/> after the user pressed send.</summary>
    public static ProductEvent SessionStarted(DateTimeOffset at, string? workspaceId, TimeSpan? duration = null) =>
        new(ProductEventKind.SessionStart, at, ProductEventOutcome.Succeeded, WorkspaceHash.Of(workspaceId),
            Milliseconds(duration));

    /// <summary>The session never got as far as a running agent.</summary>
    public static ProductEvent SessionStartFailed(DateTimeOffset at, string? workspaceId) =>
        new(ProductEventKind.SessionStart, at, ProductEventOutcome.Failed, WorkspaceHash.Of(workspaceId));

    /// <summary>There was no agent CLI to start: the runtime is missing or unlaunchable.</summary>
    public static ProductEvent ToolDiscoveryFailed(DateTimeOffset at, string? workspaceId) =>
        new(ProductEventKind.ToolDiscoveryFailed, at, ProductEventOutcome.Failed, WorkspaceHash.Of(workspaceId));

    /// <summary>A parked tool call was answered: the outcome is the decision, the duration is the latency.</summary>
    public static ProductEvent ApprovalResolved(
        DateTimeOffset at, string? workspaceId, ProductEventOutcome outcome, TimeSpan latency) =>
        new(ProductEventKind.ApprovalResolved, at, outcome, WorkspaceHash.Of(workspaceId), Milliseconds(latency));

    /// <summary>A turn's document failed validation <paramref name="attempts"/> times before the turn ended.</summary>
    public static ProductEvent TurnRetried(DateTimeOffset at, string? workspaceId, int attempts) =>
        new(ProductEventKind.TurnRetried, at, ProductEventOutcome.Failed, WorkspaceHash.Of(workspaceId),
            DurationMs: null, Count: attempts);

    /// <summary>A typed session's turn ended in prose rather than in a document (outcome ii).</summary>
    public static ProductEvent TurnUntyped(DateTimeOffset at, string? workspaceId) =>
        new(ProductEventKind.TurnUntyped, at, ProductEventOutcome.Failed, WorkspaceHash.Of(workspaceId),
            DurationMs: null, Count: 1);

    /// <summary>A session ended without completing.</summary>
    public static ProductEvent SessionAborted(DateTimeOffset at, string? workspaceId, ProductEventOutcome outcome) =>
        new(ProductEventKind.SessionAborted, at, outcome, WorkspaceHash.Of(workspaceId));

    private static long? Milliseconds(TimeSpan? duration) =>
        duration is { } d && d >= TimeSpan.Zero ? (long)d.TotalMilliseconds : null;
}
