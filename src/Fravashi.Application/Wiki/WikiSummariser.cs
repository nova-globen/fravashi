using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Wiki;

/// <summary>Where summaries are kept, one per document.</summary>
public interface IWikiSummaryStore
{
    /// <summary>The summary of one document, or null when nobody has asked for one.</summary>
    Task<WikiSummary?> GetAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// Keeps a summary, replacing the one before it. The store also makes the document searchable by its new
    /// summary, because a summary nobody can be found by is decoration.
    /// </summary>
    Task SaveAsync(WikiSummary summary, CancellationToken ct = default);
}

/// <summary>What the writer came back with: the text, or why there is none.</summary>
public sealed record WikiSummaryDraft(string? Text, string? WrittenBy, string? Refusal)
{
    /// <summary>A draft that did not happen, and the sentence a person is shown about it.</summary>
    public static WikiSummaryDraft Refused(string why) => new(null, null, why);
}

/// <summary>The agent that writes a summary. One call, no tools, no workspace: it is given text and returns text.</summary>
public interface IWikiSummaryWriter
{
    /// <summary>Null when a summary can be written now, otherwise the sentence that says why not.</summary>
    Task<string?> UnavailableBecauseAsync(CancellationToken ct = default);

    /// <summary>Asks the agent for a summary of <paramref name="item"/>.</summary>
    Task<WikiSummaryDraft> WriteAsync(WikiItem item, CancellationToken ct = default);
}

/// <summary>What the person who pressed the button is shown.</summary>
public sealed record WikiSummaryReport(WikiSummary? Summary, string? Refusal);

/// <summary>
/// Writes a document's summary ON DEMAND (backlog 40, decided 2026-09-21: "a button to do so"). Never on
/// save and never in the background: each summary is a paid agent call, and an install that spends the owner's
/// budget because somebody fixed a typo is an install that gets its agents switched off.
/// </summary>
public sealed class WikiSummariser(IWikiDocuments library, IWikiSummaryWriter writer, IWikiSummaryStore store, TimeProvider clock)
{
    private readonly IWikiDocuments _library = library ?? throw new ArgumentNullException(nameof(library));
    private readonly IWikiSummaryWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly IWikiSummaryStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The summary a document has now, if any.</summary>
    public Task<WikiSummary?> GetAsync(string itemId, CancellationToken ct = default) => _store.GetAsync(itemId, ct);

    /// <summary>Null when the button can work, otherwise why it cannot.</summary>
    public Task<string?> UnavailableBecauseAsync(CancellationToken ct = default) => _writer.UnavailableBecauseAsync(ct);

    /// <summary>
    /// Has the agent write the summary and keeps it. The summary is stamped with the version that was READ, not
    /// with whatever the document has become by the time the agent answers: an edit made during the call leaves
    /// a summary that honestly says it is behind, where stamping the newer version would hide exactly that.
    /// A refusal keeps the summary that was there; a failed refresh is not a reason to lose the last good one.
    /// </summary>
    public async Task<WikiSummaryReport> SummariseAsync(string itemId, CancellationToken ct = default)
    {
        var item = await _library.GetAsync(itemId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return new WikiSummaryReport(null, "That document is not in the wiki any more.");
        }

        var held = await _store.GetAsync(item.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(item.Body))
        {
            return new WikiSummaryReport(held, "This document has no text yet, so there is nothing to summarise.");
        }

        var draft = await _writer.WriteAsync(item, ct).ConfigureAwait(false);
        var text = Clean(draft.Text);
        if (draft.Refusal is not null || text is null)
        {
            return new WikiSummaryReport(held, draft.Refusal ?? "The agent answered with nothing that could be kept as a summary.");
        }

        var summary = new WikiSummary(item.Id, text, item.Version, draft.WrittenBy ?? "an agent", _clock.GetUtcNow());
        await _store.SaveAsync(summary, ct).ConfigureAwait(false);
        return new WikiSummaryReport(summary, null);
    }

    /// <summary>
    /// What is kept of an agent's answer: plain text, one paragraph, inside <see cref="WikiSummary.MaxLength"/>.
    /// A code fence around the whole answer is removed, because models add one however they are asked not to.
    /// A cut lands on a sentence end when there is one, so a summary never stops in the middle of a claim.
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal));
        var text = string.Join(' ', lines).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length <= WikiSummary.MaxLength)
        {
            return text;
        }

        var cut = text[..WikiSummary.MaxLength];
        var sentenceEnd = cut.LastIndexOf(". ", StringComparison.Ordinal);
        return sentenceEnd > WikiSummary.MaxLength / 2 ? cut[..(sentenceEnd + 1)] : cut.TrimEnd();
    }
}
