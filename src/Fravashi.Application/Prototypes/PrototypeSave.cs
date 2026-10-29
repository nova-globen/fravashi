using Fravashi.Application.Mockups;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Prototypes;

/// <summary>What became of a prototype save.</summary>
public enum PrototypeSaveOutcome
{
    /// <summary>Nothing was written: the document is the one that was opened (or last saved).</summary>
    Unchanged,

    /// <summary>A new version of the prototype was written.</summary>
    Saved,

    /// <summary>Somebody else saved the prototype since it was opened; nothing was written.</summary>
    Stale,

    /// <summary>The wiki refused the write for another reason; nothing was written.</summary>
    Refused,
}

/// <summary>A save's outcome, the prototype as it now stands when it was written, and the wiki's refusal otherwise.</summary>
public sealed record PrototypeSaveResult(PrototypeSaveOutcome Outcome, WikiItem? Item, IReadOnlyList<WikiError> Errors);

/// <summary>
/// Saving the direct edits made on a prototype's page (backlog 214, plan @39), the same rules as
/// <see cref="DesignMockupSave"/>: a save with no edits writes nothing, not even a new version; only the prototype
/// block's content is replaced (by <see cref="PrototypeWriter"/>'s canonical form), so the prose around it is byte for
/// byte what it was; and the version that was opened is the version written against, so a prototype somebody else
/// saved in between is refused as <see cref="PrototypeSaveOutcome.Stale"/> and never silently overwritten.
/// </summary>
public static class PrototypeSave
{
    /// <summary>
    /// Saves the session's document into the prototype it was opened from, against the version that was opened. On
    /// <see cref="PrototypeSaveOutcome.Saved"/> the session's clean point moves to what was written.
    /// </summary>
    public static async Task<PrototypeSaveResult> SaveAsync(
        IWikiDocuments wiki, WikiItem opened, PrototypeEditSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wiki);
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(session);

        if (!session.IsDirty)
        {
            return new PrototypeSaveResult(PrototypeSaveOutcome.Unchanged, opened, []);
        }

        var result = await WriteAsync(wiki, opened, session.Document, ct).ConfigureAwait(false);
        if (result.Outcome is PrototypeSaveOutcome.Saved or PrototypeSaveOutcome.Unchanged)
        {
            session.MarkSaved();
        }

        return result;
    }

    /// <summary>
    /// Moves the prototype's pin to <paramref name="designVersion"/> of its design, the overlay kept exactly as it is
    /// (backlog 215, plan @40: what <c>prototype_render</c> does to an existing prototype). Written the same way and
    /// against the same version as <see cref="SaveAsync"/>; a pin already there writes nothing.
    /// </summary>
    public static Task<PrototypeSaveResult> RepinAsync(
        IWikiDocuments wiki, WikiItem opened, PrototypeDocument document, int designVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wiki);
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(document);

        return document.Design.Version == designVersion
            ? Task.FromResult(new PrototypeSaveResult(PrototypeSaveOutcome.Unchanged, opened, []))
            : WriteAsync(wiki, opened, document with { Design = document.Design with { Version = designVersion } }, ct);
    }

    /// <summary>Replaces only the prototype block's content with <paramref name="document"/>, against the opened version.</summary>
    private static async Task<PrototypeSaveResult> WriteAsync(
        IWikiDocuments wiki, WikiItem opened, PrototypeDocument document, CancellationToken ct)
    {
        var blocks = PrototypeBody.FindBlocks(opened.Body);
        if (blocks.Count != 1)
        {
            return new PrototypeSaveResult(PrototypeSaveOutcome.Refused, null, [new WikiError(
                "no_prototype_block", "body", "This prototype no longer holds exactly one prototype block, so there is no place to save the edits.")]);
        }

        var block = blocks[0];
        var body = DesignMockupSave.WithContent(
            opened.Body, block.Start, block.Json.Length, block.Closed, PrototypeWriter.Write(document));
        if (string.Equals(body, opened.Body, StringComparison.Ordinal))
        {
            return new PrototypeSaveResult(PrototypeSaveOutcome.Unchanged, opened, []);
        }

        var updated = await wiki.UpdateAsync(opened.Id, new WikiItemEdit { Body = body }, opened.Version, ct).ConfigureAwait(false);
        if (updated.IsValid)
        {
            return new PrototypeSaveResult(PrototypeSaveOutcome.Saved, updated.Value, []);
        }

        var outcome = updated.Errors.Any(e => e.Code == WikiCodes.Conflict) ? PrototypeSaveOutcome.Stale : PrototypeSaveOutcome.Refused;
        return new PrototypeSaveResult(outcome, null, updated.Errors);
    }
}
