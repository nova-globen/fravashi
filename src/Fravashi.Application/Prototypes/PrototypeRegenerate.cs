using Fravashi.Application.Mockups;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Prototypes;

/// <summary>What became of a regenerate.</summary>
public enum PrototypeRegenerateOutcome
{
    /// <summary>The pin moved to the newer design; the overlay is kept whole.</summary>
    Regenerated,

    /// <summary>Nothing was written: the design is not newer than the pin.</summary>
    Current,

    /// <summary>Somebody saved the prototype since it was read; nothing was written.</summary>
    Stale,

    /// <summary>Nothing was written, for the reason in <see cref="PrototypeRegenerateResult.Problem"/>.</summary>
    Refused,
}

/// <summary>A regenerate's outcome.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Item">The prototype as it now stands when it was written, else as it was read.</param>
/// <param name="Unplaced">The overlay operations the newer tree no longer places, in overlay order; all still stored.</param>
/// <param name="Problem">Why nothing was written, in words, when that is the outcome.</param>
public sealed record PrototypeRegenerateResult(
    PrototypeRegenerateOutcome Outcome, WikiItem? Item, IReadOnlyList<PrototypeOperation> Unplaced, string? Problem);

/// <summary>
/// Regenerating a prototype from a newer version of its design (backlog 216, plan @41; ADR 0021 decision 6: "reapplies
/// the overlay and reports anything it can no longer place, rather than silently discarding a user's work"). In order:
/// read the newer tree from the design as it was read, render it with the stored overlay, move the pin through
/// <see cref="PrototypeSave.RepinAsync"/> against the prototype version that was read, and answer what no longer
/// places. The overlay is written back whole: an operation that no longer places stays stored until a person drops it
/// or re-anchors it on the page.
/// </summary>
public static class PrototypeRegenerate
{
    /// <summary>Regenerates <paramref name="opened"/> (read as <paramref name="document"/>) from <paramref name="design"/>.</summary>
    public static async Task<PrototypeRegenerateResult> RunAsync(
        IWikiDocuments wiki, WikiItem opened, PrototypeDocument document, WikiItem? design, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wiki);
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(document);

        if (design is null)
        {
            return Refused(opened, "There is no design to regenerate the page from.");
        }

        if (design.Version <= document.Design.Version)
        {
            return new PrototypeRegenerateResult(PrototypeRegenerateOutcome.Current, opened, [], null);
        }

        // The tree is read and rendered BEFORE the pin moves, so a prototype is never pinned to a version it cannot
        // render from; and the pin names the version read here, exactly the tree that was rendered.
        var read = DesignMockup.Read(design.Body);
        if (read.Tree?.Document is not { } tree)
        {
            return Refused(opened, $"The design's tree at version {design.Version} is not valid "
                + $"({read.Errors.Count} {(read.Errors.Count == 1 ? "error" : "errors")}), so the page cannot be regenerated from it. Nothing was written.");
        }

        var unplaced = PrototypeRenderer.Render(tree, document.Overlay).Unplaced;
        var saved = await PrototypeSave.RepinAsync(wiki, opened, document, checked((int)design.Version), ct).ConfigureAwait(false);
        return saved.Outcome switch
        {
            PrototypeSaveOutcome.Saved or PrototypeSaveOutcome.Unchanged =>
                new PrototypeRegenerateResult(PrototypeRegenerateOutcome.Regenerated, saved.Item, unplaced, null),
            PrototypeSaveOutcome.Stale =>
                new PrototypeRegenerateResult(PrototypeRegenerateOutcome.Stale, opened, [], "Somebody saved this prototype since it was read. Nothing was written."),
            _ => Refused(opened, string.Join(" ", saved.Errors.Select(e => e.Message))),
        };
    }

    private static PrototypeRegenerateResult Refused(WikiItem opened, string problem) =>
        new(PrototypeRegenerateOutcome.Refused, opened, [], problem);
}
