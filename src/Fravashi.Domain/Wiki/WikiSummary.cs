namespace Fravashi.Domain.Wiki;

/// <summary>
/// What a document is FOR, in a few sentences an agent wrote for another agent to retrieve by (backlog 40).
/// It sits beside the document rather than on it: the document is the person's, this is derived from it, and
/// it names the version it was derived from so nobody has to guess whether it still describes the text.
/// </summary>
/// <param name="ItemId">The document it describes.</param>
/// <param name="Text">The summary itself, plain text.</param>
/// <param name="ItemVersion">The document version that was read to write it.</param>
/// <param name="WrittenBy">The agent runtime that wrote it, by its display name.</param>
/// <param name="WrittenUtc">When.</param>
public sealed record WikiSummary(string ItemId, string Text, long ItemVersion, string WrittenBy, DateTimeOffset WrittenUtc)
{
    /// <summary>
    /// The longest summary that is kept. A summary is for deciding whether to open a document, and one that
    /// runs to pages has stopped being that; it would also crowd the document's own text out of the index.
    /// </summary>
    public const int MaxLength = 1200;

    /// <summary>True when the document has been edited since this was written, so it may describe an older text.</summary>
    public bool IsBehind(WikiItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Version != ItemVersion;
    }

    /// <summary>What the document page says about the summary, in one sentence.</summary>
    public static string Sentence(WikiSummary? summary, WikiItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (summary is null)
        {
            return "No summary yet. An agent can write one: a few sentences on what this document is for, which "
                + "is what other agents are shown when they search.";
        }

        return summary.IsBehind(item)
            ? $"Written by {summary.WrittenBy} for an earlier version of this document, so it may no longer describe it."
            : $"Written by {summary.WrittenBy} for this version of the document.";
    }
}
