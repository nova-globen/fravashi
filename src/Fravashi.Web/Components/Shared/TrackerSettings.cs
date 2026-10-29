namespace Fravashi.Web.Components.Shared;

/// <summary>Configuration-key hints surfaced by the tracker pages' "not configured" fallback.</summary>
public static class TrackerSettings
{
    public static string[] Jira { get; } =
    [
        "Atlassian:JiraDomain",
        "Atlassian:JiraEmail",
        "Atlassian:JiraKey",
        "Atlassian:JiraProject",
    ];

    public static string[] AzureDevOps { get; } =
    [
        "Tracker:Provider = AzureDevOps",
        "AzureDevOps:Organization",
        "AzureDevOps:Project",
        "AzureDevOps:PersonalAccessToken",
    ];
}
