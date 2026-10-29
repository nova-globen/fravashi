using System.Globalization;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// Publication facts in the studio's own database (schema step 24, backlog 83). One row per document,
/// replaced whole on every save, and durable when <see cref="SaveAsync"/> returns: the publisher's first
/// rule is that the in-flight row is committed before the call leaves, and that rule is only as good as
/// this write.
/// </summary>
public sealed class SqliteWikiPublicationStore(ISqliteConnectionFactory factory) : IWikiPublicationStore
{
    /// <inheritdoc />
    public Task ArchiveAsync(WikiPublication publication, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ct.ThrowIfCancellationRequested();
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO wiki_item_publication_history (item_id, provider, space, page_id, web_url, state, archived_utc) "
            + "VALUES ($item, $provider, $space, $page, $url, $state, $now); "
            + "DELETE FROM wiki_item_publications WHERE item_id = $item;";
        command.Parameters.AddWithValue("$item", publication.ItemId);
        command.Parameters.AddWithValue("$provider", publication.Provider);
        command.Parameters.AddWithValue("$space", publication.Space);
        command.Parameters.AddWithValue("$page", (object?)publication.PageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$url", (object?)publication.WebUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", publication.State.ToString());
        command.Parameters.AddWithValue("$now", now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WikiPublication>> HistoryAsync(string itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT provider, space, page_id, web_url, state, archived_utc FROM wiki_item_publication_history "
            + "WHERE item_id = $item ORDER BY archived_utc DESC, rowid DESC;";
        command.Parameters.AddWithValue("$item", itemId ?? string.Empty);
        var rows = new List<WikiPublication>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new WikiPublication(
                itemId!, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), null, null,
                Enum.TryParse<WikiPublicationState>(reader.GetString(4), out var state) ? state : WikiPublicationState.Published,
                string.Empty, null, null, reader.IsDBNull(3) ? null : reader.GetString(3), null,
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return Task.FromResult<IReadOnlyList<WikiPublication>>(rows);
    }

    private readonly ISqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc />
    public Task<WikiPublication?> GetAsync(string itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT item_id, provider, space, page_id, site_version, pushed_item_version, state, operation_id, "
            + "sent_title, detail, web_url, replaced_body, updated_utc "
            + "FROM wiki_item_publications WHERE item_id = $id;";
        command.Parameters.AddWithValue("$id", itemId ?? string.Empty);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return Task.FromResult<WikiPublication?>(null);
        }

        return Task.FromResult<WikiPublication?>(new WikiPublication(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            Text(reader, 3),
            Number(reader, 4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            WikiNames.Parse<WikiPublicationState>(reader.GetString(6)),
            reader.GetString(7),
            Text(reader, 8),
            Text(reader, 9),
            Text(reader, 10),
            Text(reader, 11),
            DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
    }

    /// <inheritdoc />
    public Task SaveAsync(WikiPublication publication, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO wiki_item_publications (item_id, provider, space, page_id, site_version, "
            + "pushed_item_version, state, operation_id, sent_title, detail, web_url, replaced_body, updated_utc) "
            + "VALUES ($id, $provider, $space, $page, $site, $pushed, $state, $op, $title, $detail, $url, $replaced, $updated) "
            + "ON CONFLICT (item_id) DO UPDATE SET provider = excluded.provider, space = excluded.space, "
            + "page_id = excluded.page_id, site_version = excluded.site_version, "
            + "pushed_item_version = excluded.pushed_item_version, state = excluded.state, "
            + "operation_id = excluded.operation_id, sent_title = excluded.sent_title, detail = excluded.detail, "
            + "web_url = excluded.web_url, replaced_body = excluded.replaced_body, updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$id", publication.ItemId);
        command.Parameters.AddWithValue("$provider", publication.Provider);
        command.Parameters.AddWithValue("$space", publication.Space);
        command.Parameters.AddWithValue("$page", (object?)publication.PageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$site", (object?)publication.SiteVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$pushed", (object?)publication.PushedItemVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", WikiNames.ToName(publication.State));
        command.Parameters.AddWithValue("$op", publication.OperationId);
        command.Parameters.AddWithValue("$title", (object?)publication.SentTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)publication.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$url", (object?)publication.WebUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$replaced", (object?)publication.ReplacedBody ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", publication.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? Number(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
