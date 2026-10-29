namespace Fravashi.Web.Components.Settings;

/// <summary>One Settings section: its route slug (<c>/settings/{slug}</c>) and the label its sidebar entry shows.</summary>
public sealed record SettingsSection(string Slug, string Label);

/// <summary>A group of the Settings sidebar, named by purpose, with its sections in order.</summary>
public sealed record SettingsGroup(string Label, IReadOnlyList<SettingsSection> Sections);

/// <summary>
/// Settings as ONE page with one sidebar grouped by purpose (work item #357, the layout approved
/// 2026-10-23). It replaced two tabs (Environment, whose sidebar listed the onboarding steps numbered and then a
/// "Studio" list, and Framework). The groups are presentation only: a section's address is <c>/settings/{slug}</c>
/// whatever group it sits in, so moving a section between groups never breaks a link. The Framework section keeps
/// its old address, <c>/settings/framework/{kind}/{action}/{id}</c>, because that address was already the section's
/// own; every <c>/settings/environment/...</c> address redirects through <see cref="LegacyEnvironmentTarget"/>.
/// </summary>
public static class SettingsSections
{
    public const string Overview = "overview";
    public const string Runtimes = "runtimes";
    public const string Models = "models";
    public const string Reviewer = "reviewer";
    public const string Spending = "spending";
    public const string Concurrency = "concurrency";
    public const string Framework = "framework";
    public const string Project = "project";
    public const string Board = "board";
    public const string Wiki = "wiki";
    public const string GitHosts = "git-hosts";
    public const string Broker = "broker";
    public const string Data = "data";
    public const string Backup = "backup";
    public const string Evidence = "evidence";
    public const string Search = "search";
    public const string Diagnostics = "diagnostics";

    /// <summary>The retired Environment tab's route segment; anything under it redirects.</summary>
    public const string LegacyEnvironment = "environment";

    /// <summary>The sidebar, top to bottom.</summary>
    public static IReadOnlyList<SettingsGroup> Groups { get; } =
    [
        new("General", [new(Overview, "Overview")]),
        new("Agents",
        [
            new(Runtimes, "Runtimes and sign-in"),
            new(Models, "Default models and effort"),
            new(Reviewer, "Commit reviewer"),
            new(Spending, "Spending and limits"),
            new(Concurrency, "Autopilot concurrency"),
            new(Framework, "Framework"),
        ]),
        new("Project",
        [
            new(Project, "Project and variables"),
            new(Board, "Board and status mapping"),
            new(Wiki, "Wiki and publishing"),
        ]),
        new("Connections",
        [
            new(GitHosts, "Git hosts"),
            new(Broker, "Credential broker"),
        ]),
        new("Data",
        [
            new(Data, "Data location"),
            new(Backup, "Backup and restore"),
            new(Evidence, "Evidence retention"),
        ]),
        new("Advanced",
        [
            new(Search, "Search ignore rules"),
            new(Diagnostics, "Diagnostics"),
        ]),
    ];

    /// <summary>Every section, in sidebar order.</summary>
    public static IReadOnlyList<SettingsSection> All { get; } = [.. Groups.SelectMany(g => g.Sections)];

    /// <summary>The section a bare <c>/settings</c> (or an unknown slug) shows.</summary>
    public static string Default => Overview;

    /// <summary>A route segment as a known section slug, falling back to the overview.</summary>
    public static string Normalize(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Default;
        }

        var lowered = slug.ToLowerInvariant();
        return All.Any(s => s.Slug == lowered) ? lowered : Default;
    }

    /// <summary>
    /// Where an old <c>/settings/environment/{slug}</c> address lives now. Every old slug kept its name except
    /// <c>mapping</c>, which is on the Board page with the board connection now; an unknown or missing one lands on
    /// the overview, which is what the old tab showed for it.
    /// </summary>
    public static string LegacyEnvironmentTarget(string? oldSlug)
    {
        var lowered = oldSlug?.Trim().ToLowerInvariant();
        return lowered switch
        {
            "mapping" => Path(Board),
            _ => Path(Normalize(lowered)),
        };
    }

    /// <summary>The address of a section.</summary>
    public static string Path(string slug) => "/settings/" + slug;
}
