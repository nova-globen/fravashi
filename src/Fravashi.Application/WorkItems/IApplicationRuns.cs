namespace Fravashi.Application.WorkItems;

/// <summary>A running copy of a project's application, or why there is none.</summary>
/// <param name="RunId">The app runner's handle, or null when nothing was started.</param>
/// <param name="Url">Where a scenario step reaches it from inside the studio, or null.</param>
/// <param name="Failure">Why there is no run, in words a person can act on; null on success.</param>
public sealed record ApplicationRun(string? RunId, string? Url, string? Failure)
{
    public bool Started => RunId is not null && Url is not null;

    /// <summary>
    /// Backlog 226: the window marker for THIS run, minted by the app runner when it started the run, or null. Only the
    /// design check presents it, and only to <see cref="Url"/>'s origin; a scenario step never receives it.
    /// </summary>
    public Verification.WindowMarker? Marker { get; init; }

    public static ApplicationRun Failed(string why) => new(null, null, why);
}

/// <summary>
/// The verifier's way to run the project's OWN application (backlog 87, ADR 0027): built fresh from the workspace's
/// container spec by the host's app runner, started in sibling containers with no internet, reached through one
/// port, and removed afterwards. A port, so the scenario runner is tested without an engine.
/// </summary>
public interface IApplicationRuns
{
    /// <summary>False when no app runner is reachable (a dev <c>dotnet run</c>, an engine the app runner cannot drive).</summary>
    bool Available { get; }

    /// <summary>Builds the workspace's application from its spec, starts it, and waits until it accepts connections.</summary>
    Task<ApplicationRun> StartAsync(string workspaceId, CancellationToken cancellationToken);

    /// <summary>
    /// What the run's application has written to its output so far, or null when it cannot be read (backlog 224 keeps it
    /// as evidence beside a design check). Never throws.
    /// </summary>
    Task<string?> LogsAsync(ApplicationRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>Removes a run and everything it made. Never throws; a run that cannot be removed is the app runner's to report.</summary>
    Task RemoveAsync(ApplicationRun run, CancellationToken cancellationToken);
}
