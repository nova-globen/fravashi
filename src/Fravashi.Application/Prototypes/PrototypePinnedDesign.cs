using Fravashi.Application.Mockups;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Prototypes;

/// <summary>The tree a prototype renders from, or why there is none, in words.</summary>
/// <param name="Design">The design document as it is now, or null when there is none.</param>
/// <param name="Tree">The design's tree at the prototype's pinned version, or null.</param>
/// <param name="Problem">Why there is no tree, when there is none.</param>
public sealed record PrototypePinnedTree(WikiItem? Design, MockupDocument? Tree, string? Problem);

/// <summary>
/// The tree a prototype renders from: its design AT THE PINNED VERSION (backlog 213; the version is the prototype's
/// whole claim about what it was generated from, and moving it is an explicit act). One place, so the prototype's
/// page and the agent tools (backlog 215, plan @40) render from the same tree and say the same thing when they cannot.
/// </summary>
public static class PrototypePinnedDesign
{
    /// <summary>Looks the design up in <paramref name="wiki"/> by the number the prototype names, then reads it.</summary>
    public static async Task<PrototypePinnedTree> ReadAsync(
        IWikiLibrary wiki, PrototypeDocument prototype, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wiki);
        ArgumentNullException.ThrowIfNull(prototype);

        var design = await wiki.GetByNumberAsync(prototype.Design.Number, ct).ConfigureAwait(false);
        return await ReadAsync(wiki, design, prototype, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the pinned tree of a design already looked up (null when there is none).</summary>
    public static async Task<PrototypePinnedTree> ReadAsync(
        IWikiDocuments documents, WikiItem? design, PrototypeDocument prototype, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(prototype);

        if (design is null)
        {
            return new PrototypePinnedTree(null, null, "There is no design to render the page from.");
        }

        var pinned = design.Version == prototype.Design.Version
            ? design
            : await documents.GetVersionAsync(design.Id, prototype.Design.Version, ct).ConfigureAwait(false);
        if (pinned is null)
        {
            return new PrototypePinnedTree(design, null, $"Version {prototype.Design.Version} of the design is not stored, "
                + "so the page cannot be rendered from it.");
        }

        var read = DesignMockup.Read(pinned.Body);
        return read.Tree?.Document is { } tree
            ? new PrototypePinnedTree(design, tree, null)
            : new PrototypePinnedTree(design, null, $"The design's tree at version {prototype.Design.Version} is not valid "
                + $"({read.Errors.Count} {(read.Errors.Count == 1 ? "error" : "errors")}), so the page cannot be rendered from it.");
    }
}
