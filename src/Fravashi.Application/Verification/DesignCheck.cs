using System.Globalization;
using System.Text;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;

namespace Fravashi.Application.Verification;

/// <summary>
/// Opens a running application in a real browser and checks it against a pinned design, beside the design's own
/// prototype (backlog 223, ADR 0030 decisions 3, 4 and 7, plan @47). Each screen, phase and named viewport is driven
/// and read on both sides; the structural report is the verdict and the screenshots are evidence for a person.
/// </summary>
public interface IDesignCheck
{
    /// <summary>Why no check can run here (no browser installed), or null when one can.</summary>
    string? UnavailableReason { get; }

    /// <summary>Checks the build at each target against its screen of the design, and the prototype likewise.</summary>
    Task<DesignCheckResult> CheckAsync(DesignCheckRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What to check.</summary>
/// <param name="Design">The pinned design tree (ADR 0030 decision 2).</param>
/// <param name="Targets">
/// Which address of the build shows which design screen, one per screen to check. Saying it is the caller's job: the
/// design names screens, not routes.
/// </param>
public sealed record DesignCheckRequest(MockupDocument Design, IReadOnlyList<DesignCheckTarget> Targets)
{
    /// <summary>The longest a whole check may take unless a request says less.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The prototype's overlay of direct edits, drawn in the prototype's screenshots. Never judged: approval pins the tree
    /// (backlog 209), and the prototype's report is of the tree alone.
    /// </summary>
    public IReadOnlyList<PrototypeOperation> Overlay { get; init; } = [];

    /// <summary>The longest the whole check may take; what is still to do when it runs out is not reached, with that reason.</summary>
    public TimeSpan Budget { get; init; } = DefaultBudget;

    /// <summary>
    /// Backlog 226: the run's window marker, or null. The BUILD side's context presents it to the run's origin and
    /// refuses every other request; the prototype's context never carries it.
    /// </summary>
    public WindowMarker? Marker { get; init; }
}

/// <summary>One design screen and the build's address that shows it.</summary>
/// <param name="Screen">The design screen's index.</param>
/// <param name="Url">An absolute http or https address; it must pass the browser's URL policy.</param>
public sealed record DesignCheckTarget(int Screen, string Url);

/// <summary>What a check found.</summary>
/// <param name="Screens">One per target, in the request's order.</param>
/// <param name="Refusal">Why nothing was checked (a refused address, no browser, an unknown screen), or null.</param>
public sealed record DesignCheckResult(IReadOnlyList<DesignScreenCheck> Screens, string? Refusal)
{
    /// <summary>True when something was checked and the build matched the design on every screen.</summary>
    public bool Matches => Refusal is null && Screens.Count > 0 && Screens.All(s => s.Build.Report.Matches);

    /// <summary>A check that did not run, and why.</summary>
    public static DesignCheckResult Refused(string why) => new([], why);
}

/// <summary>One screen, checked on both sides.</summary>
/// <param name="Screen">The design screen's index.</param>
/// <param name="Url">The build's address that was checked.</param>
/// <param name="Build">The running application.</param>
/// <param name="Prototype">
/// The design's own prototype, checked the same way. It must match (ADR 0030 decision 3): when it does not, the checker
/// disagrees with its own renderer and the build's report is not to be trusted either.
/// </param>
public sealed record DesignScreenCheck(int Screen, string Url, DesignCheckSide Build, DesignCheckSide Prototype);

/// <summary>Which side of a check.</summary>
public enum DesignCheckSideKind
{
    /// <summary>The running application.</summary>
    Build,

    /// <summary>The design's rendered prototype.</summary>
    Prototype,
}

/// <summary>One side of one screen: what was read, the verdict, the pictures and what the page complained about.</summary>
/// <param name="Observation">What the page showed, phase by phase and viewport by viewport.</param>
/// <param name="Report">The matcher's verdict on it.</param>
/// <param name="Screenshots">One per phase and viewport reached, in the order they were taken.</param>
/// <param name="Console">
/// Console errors, uncaught page errors, failed requests and requests the URL policy refused, each prefixed with
/// where it happened, capped.
/// </param>
public sealed record DesignCheckSide(
    DesignObservation Observation,
    DesignMatchReport Report,
    IReadOnlyList<DesignCheckScreenshot> Screenshots,
    IReadOnlyList<string> Console);

/// <summary>
/// One picture of one phase at one viewport. The build's and the prototype's of the same moment share a
/// <see cref="Key"/>, so they pair up wherever they are kept (ADR 0030 decision 7).
/// </summary>
/// <param name="Key">The moment: <c>screen-0.wide.01-add-dialog-opened-by-add-person</c>.</param>
/// <param name="Screen">The design screen's index.</param>
/// <param name="Phase">The phase's name.</param>
/// <param name="Viewport">The viewport's name, or null for a design that names none.</param>
/// <param name="Png">The picture of the viewport, PNG.</param>
public sealed record DesignCheckScreenshot(string Key, int Screen, string Phase, string? Viewport, ReadOnlyMemory<byte> Png)
{
    /// <summary>The longest a key's phase part may be; a longer name is cut, the phase index keeps it unique.</summary>
    public const int MaxSlug = 60;

    /// <summary>The file name for this picture on <paramref name="side"/>: <c>{key}.build.png</c> or <c>{key}.prototype.png</c>.</summary>
    public string FileName(DesignCheckSideKind side) =>
        Key + (side == DesignCheckSideKind.Build ? ".build.png" : ".prototype.png");

    /// <summary>
    /// The key of a moment: the screen, the viewport (<c>base</c> when the design names none), the phase's position in
    /// the design's order and its name made safe for a file name.
    /// </summary>
    public static string KeyFor(int screen, string? viewport, int phaseIndex, string phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"screen-{screen}.{Slug(viewport ?? "base")}.{phaseIndex:00}-{Slug(phase)}");
    }

    /// <summary>Lower case letters and digits, anything else one hyphen, none at either end, at most <see cref="MaxSlug"/>.</summary>
    internal static string Slug(string text)
    {
        var slug = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(char.ToLowerInvariant(c));
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var made = slug.ToString().Trim('-');
        if (made.Length > MaxSlug)
        {
            made = made[..MaxSlug].TrimEnd('-');
        }

        return made.Length == 0 ? "phase" : made;
    }
}
