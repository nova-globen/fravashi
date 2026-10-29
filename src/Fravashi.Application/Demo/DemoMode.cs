namespace Fravashi.Application.Demo;

/// <summary>
/// Whether this install is a DEMO install (backlog 71), and the one fixed clock that makes its screenshots
/// stable.
/// <para>
/// It is an ENVIRONMENT variable rather than a settings-table row, and that is not an accident. The settings
/// table is written by onboarding, and a demo install is exactly one that never runs onboarding, so a row
/// could not be there before the thing that reads it. The variable is also the level this decision belongs
/// at: a demo is a whole install, decided when the container is started, not a preference somebody toggles
/// inside a running one.
/// </para>
/// <para>
/// What this type does NOT do is worth saying, because the natural shape here is the wrong one. It is not a
/// flag that seams check. Nothing asks <see cref="IsEnabled"/> before deciding whether a write is allowed.
/// The acceptance's "it cannot write to a real workspace, tracker or wiki" is kept by a demo install having
/// no provider, no publish target, no registered workspace and no credential, so the refusals are the
/// product's ordinary ones, already tested and already explained on screen. A flag consulted in N places is
/// a promise made N times, and this repository has learned five times over what that costs.
/// </para>
/// </summary>
public static class DemoMode
{
    /// <summary>The variable that turns it on. Any value but empty, "0" or "false" counts as on.</summary>
    public const string Variable = "FRAVASHI_DEMO";

    /// <summary>
    /// The demo's "now". Fixed, so a relative time on a surface ("3 hours ago") is a constant rather than a
    /// number that grows while the demo sits open, which is the whole of the determinism promise on any page
    /// that renders an age. It sits a few hours after <see cref="DemoSeed.Epoch"/> so those ages are not all
    /// "just now", which would hide the formatting a screenshot is meant to show.
    /// </summary>
    public static readonly DateTimeOffset Now = new(2026, 3, 2, 15, 30, 0, TimeSpan.Zero);

    /// <summary>True when this process is a demo install.</summary>
    public static bool IsEnabled => IsOn(Environment.GetEnvironmentVariable(Variable));

    /// <summary>The reading of the variable, exposed so it can be tested without touching the environment.</summary>
    public static bool IsOn(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value.Trim(), "0", StringComparison.Ordinal)
        && !string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>A <see cref="TimeProvider"/> stopped at <see cref="Now"/>.</summary>
    public static TimeProvider Clock { get; } = new StoppedClock(Now);

    private sealed class StoppedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
