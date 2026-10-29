using System.Globalization;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// The authored wiki in the studio's own database (schema step 16, backlog 39).
/// <para>
/// Two things are derived rather than stored twice. The SLUG follows the title, with a numeric suffix when
/// another item already holds it, so an address stays readable and stays unique without anyone choosing one.
/// The REFERENCE INDEX is rewritten from the body on every save, so "what points at this" is a query and a
/// link is navigable from both ends without either end maintaining the other.
/// </para>
/// <para>
/// <b>One project at a time (schema step 27, backlog 121).</b> An instance is the wiki OF a project: a number,
/// a slug, a listing, a backlink and a creation all mean something only inside one, so each is scoped to
/// <paramref name="project"/>. What is addressed by ID is not scoped, because an id is unique everywhere; a
/// change by id keeps the document in the project its row says it is in, whatever this instance is bound to.
/// An instance with NO project is the by-id view (<see cref="IWikiDocuments"/>), and asking it for anything
/// project-relative throws rather than answering for some project nobody named.
/// </para>
/// </summary>
public sealed class SqliteWikiLibrary(ISqliteConnectionFactory factory, TimeProvider clock, ProjectId? project) : IWikiLibrary
{
    private readonly ISqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The project this instance answers for, or a refusal when it is the by-id view.</summary>
    private string Scope => project?.Value ?? throw new InvalidOperationException(
        "This is the wiki's by-id view, which belongs to no project; ask IWikiLibraries.For(project) for a project's wiki.");

    private const string Columns = "id, number, type, title, slug, body, version, created_utc, updated_utc, parent_id";

    /// <inheritdoc />
    public Task<WikiItem?> GetAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        return Task.FromResult(One(connection, "id = $v", id));
    }

    /// <inheritdoc />
    public Task<WikiItem?> GetVersionAsync(string id, long version, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();

        var current = One(connection, "id = $v", id ?? string.Empty);
        if (current is null || version < 1 || version > current.Version)
        {
            return Task.FromResult<WikiItem?>(null);
        }

        // The current row already IS this version: no snapshot to consult, and none would say anything
        // the row in front of us does not.
        if (version == current.Version)
        {
            return Task.FromResult<WikiItem?>(current);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title, body, created_utc FROM wiki_item_revisions WHERE item_id = $id AND version = $v;";
        command.Parameters.AddWithValue("$id", id ?? string.Empty);
        command.Parameters.AddWithValue("$v", version);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            // A version that was saved before revisions existed and never saved again: nothing survived to
            // read back. Refusing honestly beats quietly handing back a later body as if it were this one.
            return Task.FromResult<WikiItem?>(null);
        }

        var state = current.ToState() with
        {
            Title = reader.GetString(0),
            Body = reader.GetString(1),
            Version = version,
            UpdatedUtc = Stamp(reader.GetString(2)),
        };

        var built = WikiItem.Create(state);
        return Task.FromResult(built.IsValid ? built.Value : null);
    }

    /// <inheritdoc />
    public Task<WikiItem?> GetByNumberAsync(int number, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        return Task.FromResult(One(connection, "number = $v AND project_id = $p", number, scope: Scope));
    }

    /// <inheritdoc />
    public Task<WikiItem?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        return Task.FromResult(One(connection, "slug = $v AND project_id = $p", slug ?? string.Empty, scope: Scope));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WikiItem>> ListAsync(WikiItemQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();

        var where = new List<string> { "project_id = $p" };
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$p", Scope);

        if (query.Types is { Count: > 0 })
        {
            var names = query.Types.Select((t, i) => "$t" + i.ToString(CultureInfo.InvariantCulture)).ToList();
            where.Add("type IN (" + string.Join(", ", names) + ")");
            foreach (var (type, name) in query.Types.Zip(names))
            {
                command.Parameters.AddWithValue(name, WikiNames.ToName(type));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Add("(title LIKE $text ESCAPE '\\' OR body LIKE $text ESCAPE '\\')");
            command.Parameters.AddWithValue("$text", "%" + Escape(query.Text.Trim()) + "%");
        }

        command.CommandText =
            $"SELECT {Columns} FROM wiki_items "
            + (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : string.Empty)
            + "ORDER BY updated_utc DESC, number DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 1000));

        return Task.FromResult<IReadOnlyList<WikiItem>>(Many(connection, command));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WikiItem>> BacklinksAsync(WikiReference target, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();

        // Both readings of "points at": a reference written in a body, and a typed link that is a fact about
        // the item. They are one answer, because a reader does not care which way the author said it.
        using var command = connection.CreateCommand();
        command.CommandText =
            // Scoped to the project: "#12" written in another project's document points at THAT project's
            // item 12, so it is not a backlink to this one's.
            $"SELECT {Columns} FROM wiki_items WHERE project_id = $p AND id IN ("
            + "  SELECT item_id FROM wiki_item_refs WHERE kind = $kind AND number = $number"
            + "  UNION SELECT item_id FROM wiki_item_links WHERE kind = $linkKind AND target = $text"
            + ") ORDER BY number;";
        command.Parameters.AddWithValue("$p", Scope);
        command.Parameters.AddWithValue("$kind", WikiNames.ToName(target.Kind));
        command.Parameters.AddWithValue("$number", target.Number);
        command.Parameters.AddWithValue("$linkKind", WikiNames.ToName(LinkKindFor(target.Kind)));
        command.Parameters.AddWithValue("$text", target.Number.ToString(CultureInfo.InvariantCulture));

        return Task.FromResult<IReadOnlyList<WikiItem>>(Many(connection, command));
    }

    /// <inheritdoc />
    public Task<WikiResult<WikiItem>> CreateAsync(NewWikiItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();

        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();

        var scope = Scope;
        if (item.Type == WikiItemType.Branding && Exists(connection, transaction, WikiItemType.Branding, scope))
        {
            return Task.FromResult(WikiResult<WikiItem>.Refused(
                WikiCodes.BrandingNotRepeatable,
                "type",
                "This project already has a branding item; edit that one rather than adding a second."));
        }

        if (item.ParentId is { Length: > 0 } parent && One(connection, "id = $v AND project_id = $p", parent, transaction, scope) is null)
        {
            return Task.FromResult(WikiResult<WikiItem>.Refused(
                WikiCodes.ParentNotFound, "parent", "The document to put this one under is not in this project."));
        }

        var now = _clock.GetUtcNow();
        var number = NextNumber(connection, transaction, scope);
        var built = WikiItem.Create(new WikiItemState
        {
            Id = Guid.NewGuid().ToString("N"),
            Number = number,
            Type = item.Type,
            Title = item.Title ?? string.Empty,
            Slug = FreeSlug(connection, transaction, WikiItem.SlugFor(item.Title), null, scope),
            Body = item.Body ?? string.Empty,
            Links = item.Links ?? [],
            ParentId = item.ParentId,
            Version = 1,
            CreatedUtc = now,
            UpdatedUtc = now,
        });

        if (!built.IsValid)
        {
            return Task.FromResult(built);
        }

        Insert(connection, transaction, built.Value, scope);
        SaveChildren(connection, transaction, built.Value);
        InsertRevision(connection, transaction, built.Value);
        transaction.Commit();
        return Task.FromResult(built);
    }

    /// <inheritdoc />
    public Task<WikiResult<WikiItem>> UpdateAsync(
        string id, WikiItemEdit edit, long expectedVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ct.ThrowIfCancellationRequested();

        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();

        var current = One(connection, "id = $v", id ?? string.Empty, transaction);
        if (current is null)
        {
            return Task.FromResult(WikiResult<WikiItem>.Refused(
                WikiCodes.NotFound, "id", "There is no wiki item with that id."));
        }

        if (current.Version != expectedVersion)
        {
            return Task.FromResult(WikiResult<WikiItem>.Refused(
                WikiCodes.Conflict,
                "version",
                $"This item has moved on to version {current.Version.ToString(CultureInfo.InvariantCulture)}; read it again."));
        }

        // The document stays in the project its row says it is in, whatever this instance is bound to.
        var scope = ProjectOf(connection, transaction, current.Id);
        var type = edit.Type ?? current.Type;
        if (type == WikiItemType.Branding
            && current.Type != WikiItemType.Branding
            && Exists(connection, transaction, WikiItemType.Branding, scope))
        {
            return Task.FromResult(WikiResult<WikiItem>.Refused(
                WikiCodes.BrandingNotRepeatable,
                "type",
                "This project already has a branding item; edit that one rather than making a second."));
        }

        var parentId = current.ParentId;
        if (edit.Parent is { } move)
        {
            parentId = string.IsNullOrWhiteSpace(move.ParentId) ? null : move.ParentId;
            if (parentId is not null && ParentRefusal(connection, transaction, current.Id, parentId, scope) is { } refused)
            {
                return Task.FromResult(refused);
            }
        }

        var title = edit.Title ?? current.Title;
        var state = current.ToState() with
        {
            Type = type,
            Title = title,

            // The address follows the title, so a renamed page does not keep a slug that says something
            // else. It keeps its NUMBER, which is what anyone's prose actually points at.
            Slug = string.Equals(title, current.Title, StringComparison.Ordinal)
                ? current.Slug
                : FreeSlug(connection, transaction, WikiItem.SlugFor(title), current.Id, scope),
            Body = edit.Body ?? current.Body,
            Links = edit.Links ?? current.Links,
            ParentId = parentId,
            Version = current.Version + 1,
            UpdatedUtc = _clock.GetUtcNow(),
        };

        var built = WikiItem.Create(state);
        if (!built.IsValid)
        {
            return Task.FromResult(built);
        }

        Update(connection, transaction, built.Value, expectedVersion);
        SaveChildren(connection, transaction, built.Value);
        InsertRevision(connection, transaction, built.Value);
        transaction.Commit();
        return Task.FromResult(built);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();

        // Its children move up to where it was, rather than being left under a document that is gone.
        Execute(
            connection,
            transaction,
            "UPDATE wiki_items SET parent_id = (SELECT parent_id FROM wiki_items WHERE id = $id) WHERE parent_id = $id;",
            ("$id", id ?? string.Empty));

        var deleted = Execute(
            connection, transaction, "DELETE FROM wiki_items WHERE id = $id;", ("$id", id ?? string.Empty));
        Execute(connection, transaction, "DELETE FROM wiki_item_links WHERE item_id = $id;", ("$id", id ?? string.Empty));
        Execute(connection, transaction, "DELETE FROM wiki_item_refs WHERE item_id = $id;", ("$id", id ?? string.Empty));

        transaction.Commit();
        return Task.FromResult(deleted > 0);
    }

    /// <inheritdoc />
    public Task<ProjectId?> ProjectOfAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id FROM wiki_items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id ?? string.Empty);
        return Task.FromResult(command.ExecuteScalar() is string project ? new ProjectId(project) : null);
    }

    // ---------------------------------------------------------------------------------------------
    // Rows
    // ---------------------------------------------------------------------------------------------

    private WikiItem? One(
        SqliteConnection connection, string where, object value, SqliteTransaction? transaction = null, string? scope = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM wiki_items WHERE {where};";
        command.Parameters.AddWithValue("$v", value);
        if (scope is not null)
        {
            command.Parameters.AddWithValue("$p", scope);
        }

        return Many(connection, command, transaction).FirstOrDefault();
    }

    private List<WikiItem> Many(
        SqliteConnection connection, SqliteCommand command, SqliteTransaction? transaction = null)
    {
        command.Transaction ??= transaction;

        var rows = new List<WikiItemState>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(new WikiItemState
                {
                    Id = reader.GetString(0),
                    Number = reader.GetInt32(1),
                    Type = WikiNames.Parse<WikiItemType>(reader.GetString(2)),
                    Title = reader.GetString(3),
                    Slug = reader.GetString(4),
                    Body = reader.GetString(5),
                    Version = reader.GetInt64(6),
                    CreatedUtc = Stamp(reader.GetString(7)),
                    UpdatedUtc = Stamp(reader.GetString(8)),
                    ParentId = reader.IsDBNull(9) ? null : reader.GetString(9),
                });
            }
        }

        // The links are read after the row reader is closed: one connection, one reader at a time.
        var items = new List<WikiItem>(rows.Count);
        foreach (var row in rows)
        {
            var built = WikiItem.Create(row with { Links = Links(connection, transaction, row.Id) });
            if (built.IsValid)
            {
                items.Add(built.Value);
            }
            else
            {
                // A stored row the model refuses is a corrupt database, not a refusal to hand back.
                throw new InvalidDataException(
                    $"Wiki item {row.Id} cannot be built: {string.Join("; ", built.Errors.Select(e => e.Code))}.");
            }
        }

        return items;
    }

    private static List<WikiLink> Links(SqliteConnection connection, SqliteTransaction? transaction, string itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT kind, target, label FROM wiki_item_links WHERE item_id = $id ORDER BY ordinal, rowid;";
        command.Parameters.AddWithValue("$id", itemId);
        using var reader = command.ExecuteReader();

        var links = new List<WikiLink>();
        while (reader.Read())
        {
            links.Add(new WikiLink(
                WikiNames.Parse<WikiLinkKind>(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return links;
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, WikiItem item, string scope) =>
        Execute(
            connection,
            transaction,
            "INSERT INTO wiki_items (id, project_id, number, type, title, slug, body, version, created_utc, updated_utc, parent_id) "
            + "VALUES ($id, $project, $number, $type, $title, $slug, $body, $version, $created, $updated, $parent);",
            ("$id", item.Id),
            ("$project", scope),
            ("$number", item.Number),
            ("$type", WikiNames.ToName(item.Type)),
            ("$title", item.Title),
            ("$slug", item.Slug),
            ("$body", item.Body),
            ("$version", item.Version),
            ("$created", Stamp(item.CreatedUtc)),
            ("$updated", Stamp(item.UpdatedUtc)),
            ("$parent", (object?)item.ParentId ?? DBNull.Value));

    private static void Update(
        SqliteConnection connection, SqliteTransaction transaction, WikiItem item, long expectedVersion) =>
        Execute(
            connection,
            transaction,
            "UPDATE wiki_items SET type = $type, title = $title, slug = $slug, body = $body, parent_id = $parent, "
            + "version = $version, updated_utc = $updated WHERE id = $id AND version = $expected;",
            ("$type", WikiNames.ToName(item.Type)),
            ("$title", item.Title),
            ("$slug", item.Slug),
            ("$body", item.Body),
            ("$version", item.Version),
            ("$updated", Stamp(item.UpdatedUtc)),
            ("$id", item.Id),
            ("$expected", expectedVersion),
            ("$parent", (object?)item.ParentId ?? DBNull.Value));

    /// <summary>
    /// One snapshot of this save, so a later reader can ask for THIS version after the document has moved on
    /// (backlog 209). Written inside the same transaction as the row it snapshots, so the two can never
    /// disagree about what version means what.
    /// </summary>
    private static void InsertRevision(SqliteConnection connection, SqliteTransaction transaction, WikiItem item) =>
        Execute(
            connection,
            transaction,
            "INSERT INTO wiki_item_revisions (id, item_id, version, title, body, created_utc) "
            + "VALUES ($id, $item, $version, $title, $body, $created);",
            ("$id", Guid.NewGuid().ToString("N")),
            ("$item", item.Id),
            ("$version", item.Version),
            ("$title", item.Title),
            ("$body", item.Body),
            ("$created", Stamp(item.UpdatedUtc)));

    /// <summary>
    /// Why <paramref name="parentId"/> cannot hold <paramref name="id"/>, or null when it can: it must be a document
    /// of the same project, and walking up from it must never reach the document being moved.
    /// </summary>
    private WikiResult<WikiItem>? ParentRefusal(
        SqliteConnection connection, SqliteTransaction transaction, string id, string parentId, string scope)
    {
        if (One(connection, "id = $v AND project_id = $p", parentId, transaction, scope) is null)
        {
            return WikiResult<WikiItem>.Refused(
                WikiCodes.ParentNotFound, "parent", "The document to put this one under is not in this project.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var at = parentId; at is not null && seen.Add(at); at = ParentOf(connection, transaction, at))
        {
            if (at == id)
            {
                return WikiResult<WikiItem>.Refused(
                    WikiCodes.ParentCycle, "parent", "A document cannot sit under itself or under one of its own children.");
            }
        }

        return null;
    }

    private static string? ParentOf(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT parent_id FROM wiki_items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string parent ? parent : null;
    }

    /// <summary>Rewrites the link rows and the derived reference index for one item.</summary>
    private static void SaveChildren(SqliteConnection connection, SqliteTransaction transaction, WikiItem item)
    {
        Execute(connection, transaction, "DELETE FROM wiki_item_links WHERE item_id = $id;", ("$id", item.Id));
        Execute(connection, transaction, "DELETE FROM wiki_item_refs WHERE item_id = $id;", ("$id", item.Id));

        var ordinal = 0;
        foreach (var link in item.Links)
        {
            Execute(
                connection,
                transaction,
                "INSERT INTO wiki_item_links (id, item_id, kind, target, label, ordinal) "
                + "VALUES ($id, $item, $kind, $target, $label, $ordinal);",
                ("$id", Guid.NewGuid().ToString("N")),
                ("$item", item.Id),
                ("$kind", WikiNames.ToName(link.Kind)),
                ("$target", link.Target),
                ("$label", (object?)link.Label ?? DBNull.Value),
                ("$ordinal", ordinal++));
        }

        foreach (var reference in item.BodyReferences)
        {
            Execute(
                connection,
                transaction,
                "INSERT OR IGNORE INTO wiki_item_refs (item_id, kind, number) VALUES ($item, $kind, $number);",
                ("$item", item.Id),
                ("$kind", WikiNames.ToName(reference.Kind)),
                ("$number", reference.Number));
        }
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction transaction, WikiItemType type, string scope)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM wiki_items WHERE type = $type AND project_id = $p);";
        command.Parameters.AddWithValue("$type", WikiNames.ToName(type));
        command.Parameters.AddWithValue("$p", scope);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>
    /// The project's next document number. A counter, not MAX+1, so a deleted document's number is never handed
    /// out again: a reference to it must go on reading as broken rather than quietly pointing at something new.
    /// A project's first document starts its counter at one.
    /// </summary>
    private static int NextNumber(SqliteConnection connection, SqliteTransaction transaction, string scope)
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT next_number FROM wiki_numbers WHERE project_id = $p;";
        read.Parameters.AddWithValue("$p", scope);
        var next = read.ExecuteScalar() is { } stored and not DBNull
            ? Convert.ToInt32(stored, CultureInfo.InvariantCulture)
            : 1;

        Execute(
            connection,
            transaction,
            "INSERT INTO wiki_numbers (project_id, next_number) VALUES ($p, $n) "
            + "ON CONFLICT(project_id) DO UPDATE SET next_number = excluded.next_number;",
            ("$p", scope),
            ("$n", next + 1));
        return next;
    }

    /// <summary>The project a document's row says it is in.</summary>
    private static string ProjectOf(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT project_id FROM wiki_items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// A slug nothing else holds. A title that collides gets a numeric suffix rather than a refusal,
    /// because two documents may legitimately be called "Notes" and neither author should have to care.
    /// </summary>
    private static string FreeSlug(
        SqliteConnection connection, SqliteTransaction transaction, string wanted, string? exceptId, string scope)
    {
        var slug = wanted;
        var suffix = 2;
        while (Taken(connection, transaction, slug, exceptId, scope))
        {
            slug = wanted + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return slug;
    }

    private static bool Taken(
        SqliteConnection connection, SqliteTransaction transaction, string slug, string? exceptId, string scope)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM wiki_items WHERE project_id = $p AND slug = $slug AND ($except IS NULL OR id <> $except));";
        command.Parameters.AddWithValue("$p", scope);
        command.Parameters.AddWithValue("$slug", slug);
        command.Parameters.AddWithValue("$except", (object?)exceptId ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    private static WikiLinkKind LinkKindFor(WikiReferenceKind kind) => kind switch
    {
        WikiReferenceKind.WorkItem => WikiLinkKind.WorkItem,
        WikiReferenceKind.PullRequest => WikiLinkKind.PullRequest,
        _ => WikiLinkKind.WikiItem,
    };

    private static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Stamp(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Escapes the wildcards of a LIKE pattern so a search for "100%" finds that text.</summary>
    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
