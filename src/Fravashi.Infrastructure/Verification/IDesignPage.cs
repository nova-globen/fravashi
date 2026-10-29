using Fravashi.Application.Verification;

namespace Fravashi.Infrastructure.Verification;

/// <summary>
/// The few things the design check's drive asks of one page (backlog 223, plan @47). The real one is a Playwright page
/// (<see cref="PlaywrightDesignPage"/>); the drive's unit tests script one. Every call is bounded by the page itself, and
/// a call that fails or runs out of time throws <see cref="DesignPageException"/> with words a report can carry.
/// </summary>
public interface IDesignPage
{
    /// <summary>Sets the viewport the next load is laid out in.</summary>
    Task SetViewportAsync(int width, int height, CancellationToken cancellationToken);

    /// <summary>Loads the page afresh, as a person arriving at it would see it.</summary>
    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>Waits for what a load or an act started; for a dialog phase, also for a dialog to show (bounded).</summary>
    Task SettleAsync(bool dialog, CancellationToken cancellationToken);

    /// <summary>The page body's aria snapshot in AI mode, with <c>[ref=eN]</c> on every element.</summary>
    Task<string> SnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Measures one element of the last snapshot by its reference.</summary>
    Task<DesignProbe> ProbeAsync(string reference, string role, DesignProbeAsk ask, CancellationToken cancellationToken);

    /// <summary>Acts on one element of the last snapshot by its reference.</summary>
    Task ActAsync(string reference, DesignPageAct act, CancellationToken cancellationToken);

    /// <summary>A picture of the viewport, PNG.</summary>
    Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken);

    /// <summary>What the page complained about since the last call, oldest first, and forgets it.</summary>
    IReadOnlyList<string> TakeConsole();
}

/// <summary>What to measure of an element.</summary>
/// <param name="Box">Its box.</param>
/// <param name="Contrast">The contrast of its visible text.</param>
/// <param name="Name">
/// The name the AI-mode snapshot did not print: Playwright 1.63 leaves out a name that <c>aria-labelledby</c> takes from
/// inside the element (a dialog titled by its own heading), although its role engine computes it.
/// </param>
public sealed record DesignProbeAsk(bool Box, bool Contrast, bool Name)
{
    /// <summary>True when there is anything to ask.</summary>
    public bool Any => Box || Contrast || Name;
}

/// <summary>What was measured of an element; each part null when it was not asked or could not be measured.</summary>
/// <param name="Box">Its box in CSS pixels, viewport-relative; null when it has no size.</param>
/// <param name="Contrast">The WCAG 2 contrast of its text; null when it shows no text or its colours could not be read.</param>
/// <param name="Name">Its accessible name from <c>aria-labelledby</c>, confirmed by the browser's own role engine.</param>
public sealed record DesignProbe(ObservedBox? Box, double? Contrast, string? Name);

/// <summary>How to act on a control.</summary>
public enum DesignPageAct
{
    /// <summary>Click it.</summary>
    Click,

    /// <summary>Choose its second option (a combobox the design changes).</summary>
    ChooseSecondOption,

    /// <summary>Type into it and leave it, so it reports a change.</summary>
    Type,
}

/// <summary>A page call failed or ran out of time; the message says so in a report's words.</summary>
public sealed class DesignPageException : Exception
{
    public DesignPageException()
    {
    }

    public DesignPageException(string message)
        : base(message)
    {
    }

    public DesignPageException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
