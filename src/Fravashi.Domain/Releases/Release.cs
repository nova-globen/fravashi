using Fravashi.Domain.Projects;

namespace Fravashi.Domain.Releases;

/// <summary>
/// A release of one project (work item #368): which version, when, on which commit and tag, the notes, and the work
/// items it carried. It is a tracker record, stored with the work items, and saving one is what stamps "Released in" on
/// its items and archives them off the board's Done column. Nothing about a release is ever deleted.
/// </summary>
/// <param name="Id">The release's own identity.</param>
/// <param name="Project">The project it belongs to.</param>
/// <param name="Version">The version, unique within the project; also the tag's name.</param>
/// <param name="Tag">The git tag it names.</param>
/// <param name="Commit">The commit the tag points at, when known.</param>
/// <param name="TagCreated">True when the studio created and pushed the tag; false when the tag already existed.</param>
/// <param name="ReleasedUtc">When the release was saved.</param>
/// <param name="Notes">The release notes as saved, markdown.</param>
/// <param name="WikiNumber">The Release wiki document holding the notes, once it was written.</param>
/// <param name="ItemNumbers">The numbers of the work items it carried, ascending.</param>
/// <param name="CreatedBy">Who saved it, in words.</param>
public sealed record Release(
    string Id,
    ProjectId Project,
    string Version,
    string Tag,
    string? Commit,
    bool TagCreated,
    DateTimeOffset ReleasedUtc,
    string Notes,
    int? WikiNumber,
    IReadOnlyList<int> ItemNumbers,
    string CreatedBy)
{
    /// <summary>The title every surface shows for it, and the title of its wiki document.</summary>
    public string Title => TitleFor(Version);

    /// <summary>The title of the release with this version.</summary>
    public static string TitleFor(string version) => "Release " + version;
}
