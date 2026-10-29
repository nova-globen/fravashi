using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Fravashi.Application.Embeddings;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Persistence;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// The index by meaning over the authored wiki (backlog 40, schema step 25): one unit vector per chunk of
/// each document, stored as a BLOB beside it, scored by dot product in managed code.
/// <para>
/// There is no vector extension on purpose (decided 2026-09-19). At the scale this item states, a few
/// thousand documents of at most eight chunks at 384 dimensions, a full scan is a few million
/// multiply-adds, and a native index would be a second native dependency to vendor and prove in the image
/// for nothing. The vectors are held in memory and refreshed per document when its version moves.
/// </para>
/// <para>
/// A row records the document VERSION and the MODEL it was made from, so an edited document and a changed
/// model both re-index by themselves. What is embedded is the title and the body together, because a
/// title is often the only place a document says what it is about.
/// </para>
/// </summary>
public sealed class SqliteWikiMeaningIndex : IWikiMeaningSearch
{
    /// <summary>
    /// Below this a document is not relevant and is not returned. Measured on 2026-09-21 against the pinned
    /// model rather than chosen: across seven questions and five documents the right document scored 0.36 to
    /// 0.66, every wrong one 0.20 or less, and two nonsense questions never passed 0.14. An agent handed the
    /// least-irrelevant document for a question nothing answers is worse off than one told "none".
    /// </summary>
    public const float RelevanceFloor = 0.25f;

    /// <summary>How long a search will spend indexing documents that changed since the background pass.</summary>
    public static readonly TimeSpan SearchCatchUpBudget = TimeSpan.FromSeconds(2);

    private readonly ISqliteConnectionFactory _factory;
    private readonly ITextEmbedder _embedder;
    private readonly IWikiDocuments _library;
    private readonly ConcurrentDictionary<string, (long Version, float[][] Vectors)> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _indexing = new(1, 1);

    public SqliteWikiMeaningIndex(ISqliteConnectionFactory factory, ITextEmbedder embedder, IWikiDocuments library)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    /// <inheritdoc />
    public bool IsAvailable => _embedder.IsAvailable;

    /// <inheritdoc />
    public async Task<WikiMeaningResult?> FindAsync(
        Fravashi.Domain.Projects.ProjectId project,
        string text,
        IReadOnlyList<WikiItemType>? types,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (!_embedder.IsAvailable || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var waiting = await CatchUpAsync(SearchCatchUpBudget, ct).ConfigureAwait(false);
        var question = _embedder.Embed(text);
        if (question.Count == 0)
        {
            return new WikiMeaningResult([], waiting);
        }

        Refresh();

        // Only this project's documents are ranked. The set is read fresh each search, so a document created a
        // moment ago in another project can never be in it.
        var inProject = IdsIn(project);
        var ranked = _cache
            .Where(entry => entry.Value.Vectors.Length > 0 && inProject.Contains(entry.Key))
            .Select(entry => (Id: entry.Key, Score: entry.Value.Vectors.Max(v => Dot(v, question[0]))))
            .Where(scored => scored.Score >= RelevanceFloor)
            .OrderByDescending(scored => scored.Score)
            .ToList();

        var items = new List<WikiItem>();
        foreach (var (id, _) in ranked)
        {
            if (items.Count >= limit)
            {
                break;
            }

            var item = await _library.GetAsync(id, ct).ConfigureAwait(false);
            if (item is not null && (types is not { Count: > 0 } || types.Contains(item.Type)))
            {
                items.Add(item);
            }
        }

        return new WikiMeaningResult(items, waiting);
    }

    /// <inheritdoc />
    public async Task<int> CatchUpAsync(TimeSpan budget, CancellationToken ct = default)
    {
        if (!_embedder.IsAvailable)
        {
            return 0;
        }

        await _indexing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var clock = Stopwatch.StartNew();
            RemoveOrphans();
            var stale = Stale();
            var done = 0;
            foreach (var (id, version, title, summary, body) in stale)
            {
                // The first one always runs, so a budget of zero still makes progress and a search never
                // leaves a document it could have indexed in a moment.
                if (done > 0 && clock.Elapsed > budget)
                {
                    break;
                }

                ct.ThrowIfCancellationRequested();
                // The summary goes in FRONT of the body: it is the densest statement of what the document is
                // for, and the first chunk is the one a short question lands nearest to.
                Store(id, version, _embedder.Embed(
                    summary.Length == 0 ? title + "\n\n" + body : title + "\n\n" + summary + "\n\n" + body));
                done++;
            }

            return stale.Count - done;
        }
        finally
        {
            _indexing.Release();
        }
    }

    private List<(string Id, long Version, string Title, string Summary, string Body)> Stale()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT i.id, i.version, i.title, COALESCE(s.summary, ''), i.body FROM wiki_items i "
            + "LEFT JOIN wiki_item_summaries s ON s.item_id = i.id "
            + "LEFT JOIN (SELECT item_id, MAX(item_version) AS v, MIN(model) AS m FROM wiki_item_embeddings GROUP BY item_id) e "
            + "ON e.item_id = i.id WHERE e.item_id IS NULL OR e.v <> i.version OR e.m <> $model "
            + "ORDER BY i.updated_utc DESC;";
        command.Parameters.AddWithValue("$model", _embedder.Model);

        var stale = new List<(string, long, string, string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            stale.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return stale;
    }

    private void Store(string id, long version, IReadOnlyList<float[]> vectors)
    {
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM wiki_item_embeddings WHERE item_id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        // A document the model made nothing of still gets a row, with an empty vector, so it is not
        // embedded again on every pass for ever. An empty vector is never scored and never returned.
        IReadOnlyList<float[]> rows = vectors.Count == 0 ? [[]] : vectors;
        for (var chunk = 0; chunk < rows.Count; chunk++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO wiki_item_embeddings (item_id, chunk, model, item_version, vector) "
                + "VALUES ($id, $chunk, $model, $version, $vector);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$chunk", chunk);
            insert.Parameters.AddWithValue("$model", _embedder.Model);
            insert.Parameters.AddWithValue("$version", version);
            insert.Parameters.AddWithValue("$vector", MemoryMarshal.AsBytes(rows[chunk].AsSpan()).ToArray());
            insert.ExecuteNonQuery();
        }

        transaction.Commit();

        // The cache is keyed by the DOCUMENT's version, and a new summary re-embeds a document whose version
        // did not move. Forgetting the entry here is what makes the next search load the new vectors.
        _cache.TryRemove(id, out _);
    }

    private void RemoveOrphans()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM wiki_item_embeddings WHERE item_id NOT IN (SELECT id FROM wiki_items);";
        command.ExecuteNonQuery();
    }

    private HashSet<string> IdsIn(Fravashi.Domain.Projects.ProjectId project)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM wiki_items WHERE project_id = $p;";
        command.Parameters.AddWithValue("$p", project.Value);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>Brings the in-memory vectors in line with the table, reading only documents whose version moved.</summary>
    private void Refresh()
    {
        using var connection = _factory.Open();
        var current = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var versions = connection.CreateCommand())
        {
            versions.CommandText =
                "SELECT item_id, MAX(item_version) FROM wiki_item_embeddings WHERE model = $model GROUP BY item_id;";
            versions.Parameters.AddWithValue("$model", _embedder.Model);
            using var reader = versions.ExecuteReader();
            while (reader.Read())
            {
                current[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        foreach (var gone in _cache.Keys.Where(id => !current.ContainsKey(id)).ToList())
        {
            _cache.TryRemove(gone, out _);
        }

        foreach (var (id, version) in current)
        {
            if (_cache.TryGetValue(id, out var held) && held.Version == version)
            {
                continue;
            }

            using var load = connection.CreateCommand();
            load.CommandText = "SELECT vector FROM wiki_item_embeddings WHERE item_id = $id AND model = $model ORDER BY chunk;";
            load.Parameters.AddWithValue("$id", id);
            load.Parameters.AddWithValue("$model", _embedder.Model);
            var vectors = new List<float[]>();
            using var reader = load.ExecuteReader();
            while (reader.Read())
            {
                var floats = MemoryMarshal.Cast<byte, float>((byte[])reader[0]).ToArray();
                if (floats.Length > 0)
                {
                    vectors.Add(floats);
                }
            }

            _cache[id] = (version, [.. vectors]);
        }
    }

    private static float Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return 0f;
        }

        float sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
