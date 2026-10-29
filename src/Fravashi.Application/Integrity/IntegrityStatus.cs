namespace Fravashi.Application.Integrity;

/// <summary>The outcome of one integrity check (a real comparison - a PASS means the comparison actually ran).</summary>
public enum IntegrityCheckStatus
{
    /// <summary>The observed value matched the launcher-recorded expectation.</summary>
    Pass,

    /// <summary>The observed value did NOT match - under enforcement this fails the app closed.</summary>
    Fail,

    /// <summary>The check could not run meaningfully (e.g. the expected value was not recorded); never fails closed.</summary>
    Skipped,
}

/// <summary>Whether the integrity service is enforcing (may terminate) or only reporting.</summary>
public enum IntegrityMode
{
    /// <summary>
    /// Running inside a launcher-provisioned container in a non-Development environment: a genuine mismatch
    /// fails the app CLOSED (locks + terminates).
    /// </summary>
    Enforced,

    /// <summary>
    /// Report-only: outside a launcher-managed container, in Development, or the launcher did not provision the
    /// integrity key/record (e.g. a container created before this feature existed). Checks are shown but a
    /// mismatch never terminates the app - the do-no-harm downgrade.
    /// </summary>
    ReportOnly,
}

/// <summary>
/// One integrity check's result. Every field is load-bearing for the honest surface (assertion-gap rule): a PASS
/// carries the <see cref="Expected"/> and <see cref="Actual"/> it compared, so the UI can show that a real
/// comparison ran rather than a decorative green tick.
/// </summary>
/// <param name="ComponentId">The checked component (<c>studio</c>, later <c>vpn-sidecar</c>).</param>
/// <param name="Name">Short check name (e.g. "Variant", "Non-root uid", "/data mount").</param>
/// <param name="Status">Pass / Fail / Skipped.</param>
/// <param name="Expected">The launcher-recorded expected value (or null).</param>
/// <param name="Actual">The value observed inside the running container (or null).</param>
/// <param name="Detail">A human explanation of what was compared and why it passed/failed/was skipped.</param>
public sealed record IntegrityCheck(
    string ComponentId,
    string Name,
    IntegrityCheckStatus Status,
    string? Expected,
    string? Actual,
    string Detail);

/// <summary>
/// The app's view of its own tamper-evidence self-check (F3 / ADR 0007). The launcher records the expected shape
/// (image digest, runtime, mounts, env, non-root uid, port) into <c>/data/integrity-record.json</c>, HMAC-signed
/// with a key it injects into the container; the integrity <c>BackgroundService</c> reads it back and compares it
/// to the actually-observed runtime shape. Under <see cref="IntegrityMode.Enforced"/> a genuine mismatch fails the
/// app closed (locks every route + terminates the process, recoverable by a legitimate <c>fravashi</c> relaunch);
/// under <see cref="IntegrityMode.ReportOnly"/> the same mismatch is only surfaced.
/// </summary>
/// <param name="Mode">Enforced vs report-only.</param>
/// <param name="ModeReason">Why this mode (e.g. "outside a container", "not provisioned", "enforced in-container").</param>
/// <param name="Provisioned">True when a launcher-written, HMAC-verifiable record was found.</param>
/// <param name="Checks">Every check performed, in display order.</param>
/// <param name="EvaluatedUtc">When the service last evaluated, or null before the first evaluation.</param>
/// <param name="Runtime">The runtime named in the record (or null).</param>
public sealed record IntegrityStatus(
    IntegrityMode Mode,
    string ModeReason,
    bool Provisioned,
    IReadOnlyList<IntegrityCheck> Checks,
    DateTimeOffset? EvaluatedUtc,
    string? Runtime)
{
    /// <summary>The pre-evaluation state: report-only, nothing checked yet - never locks.</summary>
    public static readonly IntegrityStatus NotEvaluated = new(
        IntegrityMode.ReportOnly,
        "The integrity self-check has not run yet.",
        Provisioned: false,
        Checks: [],
        EvaluatedUtc: null,
        Runtime: null);

    /// <summary>True when any check failed.</summary>
    public bool HasFailure => Checks.Any(c => c.Status == IntegrityCheckStatus.Fail);

    /// <summary>
    /// True only when enforcing AND a check failed - the single predicate the lock middleware + the terminate
    /// path key off. Report-only never fail-closes, no matter how many checks fail.
    /// </summary>
    public bool IsFailClosed => Mode == IntegrityMode.Enforced && HasFailure;

    /// <summary>Count of checks that genuinely compared and matched.</summary>
    public int PassCount => Checks.Count(c => c.Status == IntegrityCheckStatus.Pass);

    /// <summary>Count of checks that compared and did not match.</summary>
    public int FailCount => Checks.Count(c => c.Status == IntegrityCheckStatus.Fail);

    /// <summary>Count of checks that could not run (skipped, never fatal).</summary>
    public int SkippedCount => Checks.Count(c => c.Status == IntegrityCheckStatus.Skipped);
}

/// <summary>
/// Reads the latest integrity self-check result (F3). The lock middleware consults <see cref="Current"/> to
/// decide whether to redirect to the integrity-failed page; the Settings surface displays the per-check detail.
/// The value is set by the integrity <c>BackgroundService</c> after each evaluation.
/// </summary>
public interface IIntegrityStatusProvider
{
    /// <summary>The most recent evaluation, or <see cref="IntegrityStatus.NotEvaluated"/> before the first run.</summary>
    IntegrityStatus Current { get; }
}
