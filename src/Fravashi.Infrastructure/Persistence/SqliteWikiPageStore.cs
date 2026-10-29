using Fravashi.Application.Wiki;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IWikiPageStore"/> over the <c>wiki_pages</c> table - the DB-backed local wiki store the sync
/// engine pulls into (ADR 0005; vision O9/O10). Uniqueness is per (<c>space</c>, <c>slug</c>), backed by the
/// <c>ux_wiki_pages_space_slug</c> index; <see cref="Upsert"/> keys on that pair so a re-pull overwrites the
/// same row (site-authoritative). Timestamps round-trip as ISO-8601 strings like the other stores.
/// </summary>
public sealed class SqliteWikiPageStore(ISqliteConnectionFactory factory) : IWikiPageStore
{
    private const string Columns =
        "id, space, title, slug, body, format, source_ref, source_hash, local_hash, has_conflict, updated_utc";

    public IReadOnlyList<WikiPage> List()
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM wiki_pages ORDER BY space, title;";
        return Read(command);
    }

    public WikiPage? Get(string id)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM wiki_pages WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var rows = Read(command);
        return rows.Count > 0 ? rows[0] : null;
    }

    public WikiPage? GetBySlug(string space, string slug)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM wiki_pages WHERE space = $space AND slug = $slug;";
        command.Parameters.AddWithValue("$space", space);
        command.Parameters.AddWithValue("$slug", slug);
        var rows = Read(command);
        return rows.Count > 0 ? rows[0] : null;
    }

    public void Upsert(WikiPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO wiki_pages ({Columns}) " +
            "VALUES ($id, $space, $title, $slug, $body, $format, $sourceRef, $sourceHash, $localHash, $conflict, $updated) " +
            "ON CONFLICT(space, slug) DO UPDATE SET " +
            "title = excluded.title, body = excluded.body, format = excluded.format, " +
            "source_ref = excluded.source_ref, source_hash = excluded.source_hash, " +
            "local_hash = excluded.local_hash, has_conflict = excluded.has_conflict, " +
            "updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$id", page.Id);
        command.Parameters.AddWithValue("$space", page.Space);
        command.Parameters.AddWithValue("$title", page.Title);
        command.Parameters.AddWithValue("$slug", page.Slug);
        command.Parameters.AddWithValue("$body", page.Body);
        command.Parameters.AddWithValue("$format", page.Format);
        command.Parameters.AddWithValue("$sourceRef", (object?)page.SourceRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceHash", page.SourceHash);
        command.Parameters.AddWithValue("$localHash", page.LocalHash);
        command.Parameters.AddWithValue("$conflict", page.HasConflict ? 1 : 0);
        command.Parameters.AddWithValue("$updated", page.UpdatedUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Remove(string id)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM wiki_pages WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public int Count()
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM wiki_pages;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static List<WikiPage> Read(SqliteCommand command)
    {
        var result = new List<WikiPage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new WikiPage(
                Id: reader.GetString(0),
                Space: reader.GetString(1),
                Title: reader.GetString(2),
                Slug: reader.GetString(3),
                Body: reader.GetString(4),
                Format: reader.GetString(5),
                SourceRef: reader.IsDBNull(6) ? null : reader.GetString(6),
                SourceHash: reader.GetString(7),
                LocalHash: reader.GetString(8),
                HasConflict: reader.GetInt32(9) != 0,
                UpdatedUtc: DateTimeOffset.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return result;
    }
}
