using Fravashi.Domain.Projects;
using Fravashi.Domain.Releases;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Releases;

/// <summary>What a caller supplies to record a release (work item #368). The id and the stamp are the store's.</summary>
public sealed record NewRelease
{
    /// <summary>The version, in <see cref="ReleaseVersion"/>'s one shape and unique within the project.</summary>
    public required string Version { get; init; }

    /// <summary>The git tag the release names: an existing tag, or the one the studio just created.</summary>
    public required string Tag { get; init; }

    /// <summary>The commit the tag points at, when known.</summary>
    public string? Commit { get; init; }

    /// <summary>True when the studio created and pushed the tag for this release.</summary>
    public bool TagCreated { get; init; }

    /// <summary>The release notes, markdown.</summary>
    public required string Notes { get; init; }

    /// <summary>The work items the release carries: each must be Done and not yet taken by any release.</summary>
    public required IReadOnlyList<WorkItemId> Items { get; init; }

    /// <summary>Who is saving it, in words.</summary>
    public string CreatedBy { get; init; } = "user";
}

/// <summary>The codes a release refusal carries, stable so a page and an agent can branch on them.</summary>
public static class ReleaseCodes
{
    /// <summary>The version is not in the accepted shape.</summary>
    public const string InvalidVersion = "invalid_version";

    /// <summary>The project already has a release with that version.</summary>
    public const string VersionExists = "version_exists";

    /// <summary>No item was chosen.</summary>
    public const string NoItems = "no_items";

    /// <summary>A chosen item is not in this project.</summary>
    public const string UnknownItem = "unknown_item";

    /// <summary>A chosen item is not Done.</summary>
    public const string NotDone = "item_not_done";

    /// <summary>A chosen item was already taken by a release.</summary>
    public const string AlreadyReleased = "item_already_released";

    /// <summary>The tag name is blank.</summary>
    public const string NoTag = "no_tag";
}

/// <summary>A release saved, or the reason it was not.</summary>
/// <param name="Value">The saved release, or null on a refusal.</param>
/// <param name="Code">A <see cref="ReleaseCodes"/> value on a refusal.</param>
/// <param name="Message">The refusal in words.</param>
public sealed record ReleaseResult(Release? Value, string? Code = null, string? Message = null)
{
    /// <summary>True when the release was saved.</summary>
    public bool Saved => Value is not null;

    /// <summary>A refusal.</summary>
    public static ReleaseResult Refused(string code, string message) => new(null, code, message);
}

/// <summary>
/// One project's releases (work item #368), stored with its work items. Saving a release stamps "Released in" on each of
/// its items and archives them off the board's Done column, in the same transaction, and records it in each item's
/// history. There is no update of what a release carried and no delete: a release is a record of what shipped.
/// </summary>
public interface IReleaseStore
{
    /// <summary>The project this store is bound to.</summary>
    ProjectId Project { get; }

    /// <summary>Every release of the project, the newest first.</summary>
    Task<IReadOnlyList<Release>> ListAsync(CancellationToken ct = default);

    /// <summary>The release with exactly this version, or null.</summary>
    Task<Release?> GetAsync(string version, CancellationToken ct = default);

    /// <summary>Saves a release and stamps its items, or refuses with a <see cref="ReleaseCodes"/> value and changes nothing.</summary>
    Task<ReleaseResult> CreateAsync(NewRelease release, CancellationToken ct = default);

    /// <summary>Records the Release wiki document that holds the notes. False when there is no such release.</summary>
    Task<bool> SetWikiDocumentAsync(string releaseId, int wikiNumber, CancellationToken ct = default);
}

/// <summary>The release store of a project, shaped like the tracker resolver so nothing can forget to say which.</summary>
public interface IReleaseStores
{
    /// <summary>The releases of <paramref name="project"/>.</summary>
    IReleaseStore For(ProjectId project);
}
