using System.Globalization;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Persistence;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>Summaries in <c>wiki_item_summaries</c> (schema step 26), one row per document.</summary>
public sealed class SqliteWikiSummaryStore(ISqliteConnectionFactory factory) : IWikiSummaryStore
{
    private readonly ISqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc />
    public Task<WikiSummary?> GetAsync(string itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT item_id, summary, item_version, written_by, written_utc FROM wiki_item_summaries WHERE item_id = $id;";
        command.Parameters.AddWithValue("$id", itemId ?? string.Empty);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return Task.FromResult<WikiSummary?>(null);
        }

        return Task.FromResult<WikiSummary?>(new WikiSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
    }

    /// <inheritdoc />
    public Task SaveAsync(WikiSummary summary, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                "INSERT INTO wiki_item_summaries (item_id, summary, item_version, written_by, written_utc) "
                + "VALUES ($id, $summary, $version, $by, $at) "
                + "ON CONFLICT (item_id) DO UPDATE SET summary = excluded.summary, item_version = excluded.item_version, "
                + "written_by = excluded.written_by, written_utc = excluded.written_utc;";
            upsert.Parameters.AddWithValue("$id", summary.ItemId);
            upsert.Parameters.AddWithValue("$summary", summary.Text);
            upsert.Parameters.AddWithValue("$version", summary.ItemVersion);
            upsert.Parameters.AddWithValue("$by", summary.WrittenBy);
            upsert.Parameters.AddWithValue("$at", summary.WrittenUtc.ToString("O", CultureInfo.InvariantCulture));
            upsert.ExecuteNonQuery();
        }

        // The index by meaning embeds the summary with the document, and it decides what is stale by the
        // DOCUMENT's version, which a new summary does not move. So the document's vectors are dropped in the
        // same transaction as the summary lands, and the indexer finds a document with none and embeds it
        // again. Derived data only: nothing a person wrote is touched here.
        using (var forget = connection.CreateCommand())
        {
            forget.Transaction = transaction;
            forget.CommandText = "DELETE FROM wiki_item_embeddings WHERE item_id = $id;";
            forget.Parameters.AddWithValue("$id", summary.ItemId);
            forget.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }
}
