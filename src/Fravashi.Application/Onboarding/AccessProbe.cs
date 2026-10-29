namespace Fravashi.Application.Onboarding;

/// <summary>
/// The outcome of a single access check run by an onboarding probe. The distinction between
/// <see cref="Passed"/> and <see cref="NotVerifiable"/> is load-bearing for the wizard's honesty rule: a
/// "verified" badge is shown ONLY for <see cref="Passed"/>, which means a real probe ran and succeeded.
/// <see cref="NotVerifiable"/> means the capability could not be checked without performing the very
/// mutation it would prove - the UI must say so plainly rather than assert success.
/// </summary>
public enum AccessProbeOutcome
{
    /// <summary>A real probe ran and the capability is confirmed granted.</summary>
    Passed,

    /// <summary>A real probe ran and the capability is confirmed denied or the request failed.</summary>
    Failed,

    /// <summary>
    /// The capability cannot be honestly determined without performing a write/delete against the user's
    /// live system, and no non-destructive permission probe exists for it. Recorded, not verified.
    /// </summary>
    NotVerifiable,

    /// <summary>The selected provider has no adapter for this capability yet. Recorded, not verified.</summary>
    NotSupported,
}

/// <summary>
/// One capability check within an <see cref="AccessProbeResult"/>. Every check names the exact
/// <see cref="Mechanism"/> that produced its <see cref="Outcome"/> - the assertion-gap rule: no claim
/// without a named mechanism behind it.
/// </summary>
/// <param name="Capability">The operation checked (e.g. <c>Read</c>, <c>Query</c>, <c>Write</c>).</param>
/// <param name="Outcome">What the probe found.</param>
/// <param name="Mechanism">The concrete mechanism used (e.g. <c>git ls-remote</c>, a REST endpoint), or why none exists.</param>
/// <param name="Detail">Human-readable detail (an error message, a granted-permission note).</param>
public sealed record AccessCheck(
    string Capability,
    AccessProbeOutcome Outcome,
    string Mechanism,
    string Detail);

/// <summary>
/// The result of an onboarding access probe: an ordered set of per-capability <see cref="AccessCheck"/>s.
/// The wizard renders each check with its mechanism; it never collapses a mix of passed and not-verifiable
/// checks into a single fake "success".
/// </summary>
/// <param name="Checks">Per-capability results, in the order they were run.</param>
public sealed record AccessProbeResult(IReadOnlyList<AccessCheck> Checks)
{
    /// <summary>True only when at least one check ran and none failed outright.</summary>
    public bool AnyRan => Checks.Count > 0;

    /// <summary>True when the read/connectivity capability was actually verified (a real probe passed).</summary>
    public bool ReadVerified =>
        Checks.Any(c => c.Outcome == AccessProbeOutcome.Passed);

    /// <summary>True when any check failed outright - the connection is not usable as configured.</summary>
    public bool AnyFailed =>
        Checks.Any(c => c.Outcome == AccessProbeOutcome.Failed);

    public static AccessProbeResult Single(string capability, AccessProbeOutcome outcome, string mechanism, string detail) =>
        new([new AccessCheck(capability, outcome, mechanism, detail)]);
}
