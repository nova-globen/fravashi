namespace Fravashi.Web.Components.Shell;

/// <summary>The top-level studio areas selectable from the activity bar.</summary>
public enum StudioArea
{
    Home,
    Board,
    Source,
    Docs,
    Cockpit,
    Authoring,
    Sessions,
    Settings,
    Code,
    Git,
}

/// <summary>An activity-bar entry: which area, its label, glyph, and route.</summary>
public sealed record StudioActivity(StudioArea Area, string Label, string Glyph, string Route)
{
    /// <summary>Whether this area shows the contextual left sidebar (explorer pane).</summary>
    public bool HasSidebar { get; init; } = true;

    /// <summary>Extra route prefixes that also resolve to this activity (e.g. /workitems for the board).</summary>
    public IReadOnlyList<string> AltRoutes { get; init; } = [];

    /// <summary>The primary route plus any alternate routes.</summary>
    public IEnumerable<string> Routes => [Route, .. AltRoutes];
}

/// <summary>The fixed set of studio activities and route → activity resolution.</summary>
public static class StudioActivities
{
    /// <summary>
    /// Icon entries looped into the activity rail. Home is reached via the brand logo above the loop, and
    /// Settings via the gear button pinned to the rail's bottom (see <c>ActivityBar.razor</c>), so neither
    /// appears here. Pages carrying their own <c>PageSplit</c> sidebar set <c>HasSidebar = false</c> (the
    /// shell's contextual explorer column stays out of their way).
    /// </summary>
    public static IReadOnlyList<StudioActivity> All { get; } =
    [
        // Backlog 38: the board is the tracker's own surface, and "/workitems" is the same page's list and
        // detail view, so both routes light the same rail entry rather than looking like a different area.
        new(StudioArea.Board, "Board", "▤", "/board")
        {
            HasSidebar = false,
            AltRoutes = ["/workitems"],
        },
        // Backlog 39: the wiki is the other projection of the graph, so it sits beside the board.
        new(StudioArea.Docs, "Wiki", "📄", "/wiki") { HasSidebar = false },
        new(StudioArea.Code, "Code", "⌨", "/code") { HasSidebar = false },
        new(StudioArea.Git, "Git", "⎇", "/git") { HasSidebar = false },
        // Plan 2e: agent sessions get their own area (decision 3).
        new(StudioArea.Sessions, "Sessions", "✦", "/sessions") { HasSidebar = false },
        // Work item #358: backlog 303's autopilot page and its entry are gone; the autopilot is Home's Autopilot card.
    ];

    /// <summary>
    /// The merged home surface (backlog 33): recents/open/clone plus the dashboard cards on one page.
    /// "/welcome" stays as an alternate route because installed fravashi.exe binaries use it as the
    /// bare-launch URL; both routes render the same component, so the rail's brand stays highlighted.
    /// </summary>
    public static StudioActivity Home { get; } =
        new(StudioArea.Home, "Home", "🏠", "/") { HasSidebar = false, AltRoutes = ["/welcome"] };

    /// <summary>
    /// The Settings surface (Environment + Framework tabs). Reachable from the bottom action rail, not the
    /// left activity bar, so it is resolvable for layout/label purposes but stays out of <see cref="All"/>.
    /// It renders no studio contextual sidebar (the Settings page carries its own tab sidebar).
    /// </summary>
    public static StudioActivity Settings { get; } =
        new(StudioArea.Settings, "Settings", "⚙", "/settings") { HasSidebar = false };

    /// <summary>Every activity whose route participates in path resolution (the bar entries, Settings and Home's alt route).</summary>
    private static readonly IReadOnlyList<StudioActivity> Resolvable = [.. All, Settings, Home];

    /// <summary>
    /// Resolve the active activity for an absolute path. The longest non-root route prefix wins; the
    /// home activity ("/") is the fallback.
    /// </summary>
    public static StudioActivity ForPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return Home;
        }

        StudioActivity? best = null;
        var bestLength = 0;
        foreach (var activity in Resolvable)
        {
            foreach (var route in activity.Routes)
            {
                if (route == "/")
                {
                    continue;
                }

                var matches = path.Equals(route, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(route + "/", StringComparison.OrdinalIgnoreCase);

                if (matches && route.Length > bestLength)
                {
                    best = activity;
                    bestLength = route.Length;
                }
            }
        }

        return best ?? Home;
    }
}
