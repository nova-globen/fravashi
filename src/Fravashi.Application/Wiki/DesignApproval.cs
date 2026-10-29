using System.Globalization;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.Wiki;

/// <summary>Why a design could not be approved, or that it was.</summary>
/// <param name="Code">A stable code an agent can act on.</param>
/// <param name="Message">A sentence that says what is wrong AND what to do about it.</param>
public sealed record DesignApprovalResult(bool Approved, string? Code = null, string? Message = null)
{
    /// <summary>It was approved.</summary>
    public static DesignApprovalResult Ok { get; } = new(true);

    /// <summary>It was not, for this reason.</summary>
    public static DesignApprovalResult Refused(string code, string message) => new(false, code, message);
}

/// <summary>
/// What a work item's approved design says about itself (backlog 209): the document it names, the version
/// it was approved AT, and the version the document is at now.
/// </summary>
/// <param name="DesignNumber">The approved design's number.</param>
/// <param name="Title">The approved design's current title.</param>
/// <param name="ApprovedVersion">
/// The version it was approved at, or null for a reference written before pinning existed: there is
/// nothing recorded to compare the document's current state against.
/// </param>
/// <param name="CurrentVersion">The document's version right now.</param>
public sealed record DesignApprovalStanding(int DesignNumber, string Title, int? ApprovedVersion, long CurrentVersion)
{
    /// <summary>
    /// Whether the document has been saved again since it was approved. Always false with no recorded
    /// approved version, because a reference that predates pinning has nothing to have moved on FROM.
    /// </summary>
    public bool ChangedSinceApproval => ApprovedVersion is { } approved && approved < CurrentVersion;
}

/// <summary>
/// The design gate (backlog 54, ADR 0021). A design document stays a draft until it is linked to the work
/// it is a design FOR; on approval it becomes the work item's approved-design reference, which is the
/// target the verifier checks a built UI against.
/// <para>
/// <b>Why the link is the gate.</b> An approved design nobody can trace to a requirement is a picture. The
/// point of approval is not that somebody liked it: it is that a specific work item now has a specific
/// thing it will be checked against, and that sentence cannot be written without both ends.
/// </para>
/// <para>
/// What a design is MADE of is deliberately not decided here. Today it is a wiki document, because that is
/// what the product has; item 50's mockup tree will change what is inside one and change nothing about
/// this gate, because the gate is about the link and the reference.
/// </para>
/// </summary>
public sealed class DesignApproval(ITrackerResolver trackers, IWikiLibraries wikis)
{
    /// <summary>A design has to name the work before it can be approved for it.</summary>
    public const string UnlinkedCode = "design_not_linked";

    /// <summary>The design document does not exist.</summary>
    public const string NotFoundCode = "design_not_found";

    /// <summary>The work item does not exist.</summary>
    public const string ItemNotFoundCode = "work_item_not_found";

    /// <summary>The tracker refused the reference.</summary>
    public const string RefusedCode = "design_ref_refused";

    /// <summary>
    /// Approves a design document AS the approved design of a work item. The document must already name
    /// work, a plan or a feature, in its typed links or in its prose; approving something that names
    /// nothing is what this refuses.
    /// </summary>
    public async Task<DesignApprovalResult> ApproveAsync(
        ProjectId project, int designNumber, int workItemNumber, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var design = await wikis.For(project).GetByNumberAsync(designNumber, ct).ConfigureAwait(false);
        if (design is null)
        {
            return DesignApprovalResult.Refused(
                NotFoundCode, $"There is no wiki document numbered {designNumber}.");
        }

        var tracker = trackers.For(project);
        var item = await tracker.GetByNumberAsync(workItemNumber, ct).ConfigureAwait(false);
        if (item is null)
        {
            return DesignApprovalResult.Refused(
                ItemNotFoundCode, $"This project has no work item #{workItemNumber}.");
        }

        if (!Links(design))
        {
            // The refusal names the action that fixes it, which is item 54's acceptance in as many words.
            return DesignApprovalResult.Refused(
                UnlinkedCode,
                $"Document @{designNumber} is not linked to any work item, plan or feature, so there is "
                + "nothing it is a design FOR and approving it would produce a picture rather than a target. "
                + $"Link it first with mcp__fravashi__wiki_link (kind work_item, target {workItemNumber}), or "
                + $"write #{workItemNumber} into the document, and then approve it.");
        }

        // The approved design is a single-valued role, so setting it replaces whatever was there. That is
        // the right behaviour: an item has ONE thing it is checked against, and a second would make
        // "the approved design" a question rather than an answer.
        //
        // The VERSION is pinned here too (backlog 209): approving records the document's version at this
        // exact moment, so an edit afterward cannot silently change what was agreed to. Approving again -
        // which is what re-approving a moved-on design IS, through this same gate - simply stamps whatever
        // the document's version is when THIS call runs.
        var refs = item.Refs
            .Where(r => r.Role != ArtifactRefRole.ApprovedDesign)
            .Append(new ArtifactRef(
                string.Empty,
                ArtifactRefRole.ApprovedDesign,
                ArtifactRefKind.Design,
                designNumber.ToString(CultureInfo.InvariantCulture),
                Label: design.Title,
                Version: checked((int)design.Version)))
            .ToList();

        var updated = await tracker.UpdateAsync(
            item.Id,
            new WorkItemPatch { Refs = Optional<IReadOnlyList<ArtifactRef>>.Of(refs) },
            item.Version,
            MutationContext.User,
            ct).ConfigureAwait(false);

        return updated.IsOk
            ? DesignApprovalResult.Ok
            : DesignApprovalResult.Refused(
                RefusedCode,
                updated.Errors.Count > 0
                    ? string.Join(" ", updated.Errors.Select(e => e.Message))
                    : "The tracker refused the approved-design reference.");
    }

    /// <summary>
    /// The approved design of a work item, as a wiki document, or null. This is the PINNED version
    /// (backlog 209) when one was recorded: the title and body as they stood at approval, not whatever the
    /// document says now, because that is exactly the bug this gate exists to prevent. A reference that
    /// predates pinning (<see cref="ArtifactRef.Version"/> null) reads as it always did: the document's
    /// current state, since there is nothing recorded to pin to.
    /// </summary>
    public async Task<WikiItem?> ApprovedFor(WorkItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.RefFor(ArtifactRefRole.ApprovedDesign) is not { Kind: ArtifactRefKind.Design } reference
            || !int.TryParse(reference.Target, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        // The design is a document of the item's OWN project: the number means nothing anywhere else.
        var current = await wikis.For(item.Project).GetByNumberAsync(number, ct).ConfigureAwait(false);
        if (current is null || reference.Version is not { } pinned || pinned == current.Version)
        {
            return current;
        }

        // The document moved on since it was approved. Falling back to the current document here would
        // hand the verifier exactly what backlog 209 was opened to stop it reading; the fallback below is
        // only for a snapshot that somehow did not survive, which the revision backfill is there to prevent.
        return await wikis.Documents.GetVersionAsync(current.Id, pinned, ct).ConfigureAwait(false) ?? current;
    }

    /// <summary>
    /// What the work item's approved design says about itself, for showing "approved at version N" and
    /// "the design has changed since" - or null when the item has no approved design (or it no longer
    /// exists).
    /// </summary>
    public async Task<DesignApprovalStanding?> StandingFor(WorkItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.RefFor(ArtifactRefRole.ApprovedDesign) is not { Kind: ArtifactRefKind.Design } reference
            || !int.TryParse(reference.Target, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var current = await wikis.For(item.Project).GetByNumberAsync(number, ct).ConfigureAwait(false);
        return current is null
            ? null
            : new DesignApprovalStanding(number, current.Title, reference.Version, current.Version);
    }

    /// <summary>
    /// Whether a design names the work it is for. A typed link to a work item or another document counts,
    /// and so does a reference written in its prose: either is the author saying so, and insisting on one
    /// form would make the other look like a mistake.
    /// </summary>
    private static bool Links(WikiItem design) =>
        design.Links.Any(l => l.Kind is WikiLinkKind.WorkItem or WikiLinkKind.WikiItem)
        || design.BodyReferences.Any(r =>
            r.Kind is WikiReferenceKind.WorkItem or WikiReferenceKind.WikiItem);
}
