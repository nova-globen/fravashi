using System.Globalization;
using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// What the product is doing right now, as the tray menu and the studio window need to know it
/// (backlog 15). Both used to speak as though the container were always up: the tray offered no way
/// to start it and no honest label, and a window pointed at a dead port fell through to the browser
/// engine's own error page.
/// </summary>
public enum ProductRunState
{
    /// <summary>No launcher.json: the product has never been started on this machine.</summary>
    NeverStarted,

    /// <summary>
    /// The container runtime is missing, or installed with an engine that is not answering. Nothing
    /// can be said about the container itself, so this is its own state rather than "stopped".
    /// </summary>
    RuntimeUnavailable,

    /// <summary>Configured, but no container exists yet (a fresh install, or one that was removed).</summary>
    NotCreated,

    /// <summary>The container exists and is not running (`fravashi stop`, the tray's Stop item, a reboot).</summary>
    Stopped,

    /// <summary>
    /// The container is running. It may still be coming up: only <c>/healthz</c> proves the studio
    /// actually serves, which is why every open path probes it rather than trusting this state.
    /// </summary>
    Running,
}

/// <summary>
/// What the tray menu says about the product for a given state (backlog 15). Start and Stop are
/// always BOTH present and exactly the applicable one is enabled: a menu whose items appear and
/// disappear is harder to trust than one whose items grey out, and the greyed-out one still tells
/// the user which state they are in.
/// </summary>
/// <param name="StateLabel">The menu's own state line, also used as the icon tooltip. This is native
/// tray chrome, so it names the command the owner types (backlog 32), which since the rename is the product name too.</param>
/// <param name="CanStart">Whether the Start item is enabled.</param>
/// <param name="CanStop">Whether the Stop item is enabled.</param>
public readonly record struct TrayProductMenu(string StateLabel, bool CanStart, bool CanStop)
{
    public static TrayProductMenu For(ProductRunState state) => state switch
    {
        ProductRunState.Running => new($"{ProductName.Short} is running", CanStart: false, CanStop: true),
        ProductRunState.Stopped => new($"{ProductName.Short} is stopped", CanStart: true, CanStop: false),
        ProductRunState.NotCreated => new($"{ProductName.Short} is not started yet", CanStart: true, CanStop: false),
        ProductRunState.NeverStarted => new($"{ProductName.Short} has never been started", CanStart: true, CanStop: false),
        // The engine is not answering, so the container's state is genuinely unknown. Start is still
        // offered because the launch path tries to start the engine's machine (item 6); Stop is not,
        // because there is nothing we can honestly claim to stop.
        _ => new($"{ProductName.Short} state unknown (the container runtime is not answering)", CanStart: true, CanStop: false),
    };
}

/// <summary>
/// What the studio window shows when a navigation fails, in place of the browser engine's own error
/// page (backlog 15: a borderless window with no address bar left the user staring at
/// ERR_CONNECTION_REFUSED with no way out). The wording follows the product state, because the usual
/// cause is a container that was stopped from the tray, and "check your network" would be a lie.
/// </summary>
/// <param name="Headline">One line naming what happened.</param>
/// <param name="Detail">One sentence saying why, in the user's terms.</param>
/// <param name="ActionLabel">What the single button does.</param>
public readonly record struct StudioUnavailableMessage(string Headline, string Detail, string ActionLabel)
{
    /// <summary>
    /// The markers that mean the container did not fail to ANSWER, it failed to RUN. A .NET process that
    /// dies during startup prints one of these, and the distinction is the whole point: "it may still be
    /// starting up" is true for a few seconds and a lie after that.
    /// </summary>
    private static readonly string[] Fatal =
    [
        "Unhandled exception",
        "FATAL:",
        "Application startup exception",
    ];

    /// <summary>
    /// What to say, given the product state and, when there is one, what the container last printed.
    /// <para>
    /// The container tail is the difference between a page that helps and one that does not. Measured on
    /// 2026-09-20: v0.9.23 could not build its service provider, the container was restarted 1501 times in
    /// 35 minutes under an unless-stopped policy, and this page said "it may still be starting up" for
    /// seventeen minutes while the reason sat in the container log the whole time. The product knew and
    /// did not say.
    /// </para>
    /// </summary>
    /// <param name="state">What the launcher believes the container is doing.</param>
    /// <param name="containerTail">The last lines the container printed, or null when they could not be read.</param>
    public static StudioUnavailableMessage For(ProductRunState state, string? containerTail = null)
    {
        if (Died(containerTail) && state is ProductRunState.Running or ProductRunState.Stopped)
        {
            // Two sentences for the two ends of the same failure, because what the user should DO differs.
            // While the engine is still retrying, waiting is reasonable. Once the container is stopped it
            // will not come back on its own, and a page offering "Try again" without saying so invites the
            // user to watch nothing happen.
            //
            // The stopped wording deliberately does NOT claim the engine gave up, even though a bounded
            // policy is now the usual way a crashing container ends up here: `fravashi stop` on a container
            // that happened to have crashed lands in the same state, and the page cannot tell the two
            // apart without an extra engine round trip. What it says instead is true of both.
            var attempts = LauncherDefaults.MaxContainerRestarts.ToString(CultureInfo.InvariantCulture);
            return state == ProductRunState.Running
                ? new(
                    $"{ProductName.Short} is not starting",
                    "The studio container starts and then stops again, so it never serves a page. The engine "
                    + $"retries it {attempts} times and then leaves it stopped. This is what it said last:"
                    + Environment.NewLine + Environment.NewLine + Excerpt(containerTail!),
                    "Try again")
                : new(
                    $"{ProductName.Short} is stopped, and it crashed",
                    "The studio container is not running, and the last thing it printed was a crash rather than "
                    + "a clean stop. Starting it again runs the same build, so it will very likely fail the same "
                    + "way until this is fixed:"
                    + Environment.NewLine + Environment.NewLine + Excerpt(containerTail!),
                    $"Start {ProductName.Short}");
        }

        return ForState(state);
    }

    /// <summary>True when the tail shows the process dying rather than merely being slow.</summary>
    private static bool Died(string? tail) =>
        tail is { Length: > 0 }
        && Fatal.Any(marker => tail.Contains(marker, StringComparison.Ordinal));

    /// <summary>
    /// The part of the tail worth putting on a page. The FIRST fatal line and the few after it, because a
    /// .NET stack trace is mostly framework frames and the sentence that names the fault is at the top;
    /// showing the last N lines instead would show the deepest frames and say nothing.
    /// </summary>
    private static string Excerpt(string tail, int lines = 6)
    {
        var all = tail.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(all, l => Fatal.Any(m => l.Contains(m, StringComparison.Ordinal)));
        if (start < 0)
        {
            start = 0;
        }

        return string.Join(Environment.NewLine, all.Skip(start).Take(lines).Select(l => l.TrimEnd()));
    }

    private static StudioUnavailableMessage ForState(ProductRunState state) => state switch
    {
        ProductRunState.Running => new(
            $"{ProductName.Short} did not answer",
            "The studio container is running but did not serve this page. It may still be starting up.",
            "Try again"),
        ProductRunState.Stopped or ProductRunState.NotCreated => new(
            $"{ProductName.Short} is stopped",
            "The studio container is not running. Starting it again takes a few seconds.",
            $"Start {ProductName.Short}"),
        ProductRunState.NeverStarted => new(
            $"{ProductName.Short} is not running",
            "The product has not been started on this machine yet.",
            $"Start {ProductName.Short}"),
        _ => new(
            $"{ProductName.Short} could not be reached",
            $"The container runtime is not answering. Starting {ProductName.Short} also tries to start it.",
            $"Start {ProductName.Short}"),
    };
}

/// <summary>
/// Which failed navigations the studio window speaks up about (backlog 15). WebView2 reports
/// <c>IsSuccess = false</c> for deliberate cancellations too, and the window shell cancels a
/// navigation on purpose every time it re-imposes its workspace query, so treating every failure as
/// catastrophic would paper the studio over with a recovery page during normal use.
/// <para>
/// It takes the status NAME rather than the enum because this library is cross-platform and must not
/// reference the WebView2 SDK; the caller passes <c>WebErrorStatus.ToString()</c>. The three benign
/// statuses are the ones WebView2's own documentation lists as non-catastrophic.
/// </para>
/// </summary>
public static class StudioNavigationFailure
{
    private static readonly HashSet<string> Benign = new(StringComparer.Ordinal)
    {
        "OperationCanceled",
        "ValidAuthenticationCredentialsRequired",
        "ValidProxyAuthenticationRequired",
    };

    /// <summary>True when the failure is worth replacing the engine's error page with our own.</summary>
    public static bool DeservesOurPage(string webErrorStatusName) => !Benign.Contains(webErrorStatusName);
}
