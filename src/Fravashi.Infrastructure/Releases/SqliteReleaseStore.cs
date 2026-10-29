using System.Text.Json.Nodes;
using Fravashi.Application.Releases;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Releases;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.WorkItems;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Releases;

/// <summary>
/// One project's releases in the studio's database (schema step 47, work item #368), beside its work items. Saving a
/// release is ONE transaction: the release row, a row per item it carried, and on each item <c>released_in</c> stamped,
/// its version bumped and a <c>released</c> event appended to its history. Any refusal (a bad version, a version taken,
/// an item that is not Done or already released) leaves everything exactly as it was. There is no delete.
/// </summary>
public sealed class SqliteReleaseStore(ProjectId project, ISqliteConnectionFactory factory, TimeProvider clock) : IReleaseStore
{
    private const string Columns =
        "id, project_id, version, tag, commit_sha, tag_created, released_utc, notes, wiki_number, created_by";

    /// <inheritdoc />
    public ProjectId Project { get; } = project ?? throw new ArgumentNullException(nameof(project));

    /// <inheritdoc />
    public Task<IReadOnlyList<Release>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM releases WHERE project_id = $p ORDER BY released_utc DESC, version DESC;";
        command.Parameters.AddWithValue("$p", Project.Value);
        var rows = new List<Release>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(Read(reader, []));
            }
        }

        return Task.FromResult<IReadOnlyList<Release>>([.. rows.Select(r => r with { ItemNumbers = ItemNumbers(connection, null, r.Id) })]);
    }

    /// <inheritdoc />
    public Task<Release?> GetAsync(string version, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(version))
        {
            return Task.FromResult<Release?>(null);
        }

        using var connection = factory.Open();
        return Task.FromResult(Find(connection, null, version.Trim()));
    }

    /// <inheritdoc />
    public Task<ReleaseResult> CreateAsync(NewRelease release, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ct.ThrowIfCancellationRequested();

        var version = release.Version?.Trim() ?? string.Empty;
        if (ReleaseVersion.Parse(version) is null)
        {
            return Task.FromResult(ReleaseResult.Refused(ReleaseCodes.InvalidVersion, ReleaseVersion.Rule));
        }

        if (string.IsNullOrWhiteSpace(release.Tag))
        {
            return Task.FromResult(ReleaseResult.Refused(ReleaseCodes.NoTag, "Say which tag the release names."));
        }

        var wanted = release.Items.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return Task.FromResult(ReleaseResult.Refused(ReleaseCodes.NoItems, "A release carries at least one Done work item."));
        }

        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction();

        if (Find(connection, transaction, version) is not null)
        {
            return Task.FromResult(ReleaseResult.Refused(
                ReleaseCodes.VersionExists, $"This project already has a release {version}; a release is never replaced."));
        }

        var items = new List<(WorkItemId Id, int Number, long Version)>();
        foreach (var id in wanted)
        {
            using var probe = connection.CreateCommand();
            probe.Transaction = transaction;
            probe.CommandText = "SELECT number, status, released_in, version FROM work_items WHERE id = $id AND project_id = $p;";
            probe.Parameters.AddWithValue("$id", id.Value);
            probe.Parameters.AddWithValue("$p", Project.Value);
            using var reader = probe.ExecuteReader();
            if (!reader.Read())
            {
                return Task.FromResult(ReleaseResult.Refused(ReleaseCodes.UnknownItem, "One of the chosen items is not in this project."));
            }

            var number = checked((int)reader.GetInt64(0));
            if (!string.Equals(reader.GetString(1), StableNames.ToName(WorkItemStatus.Done), StringComparison.Ordinal))
            {
                return Task.FromResult(ReleaseResult.Refused(ReleaseCodes.NotDone, $"#{number} is not Done, so no release can carry it yet."));
            }

            if (!reader.IsDBNull(2))
            {
                return Task.FromResult(ReleaseResult.Refused(
                    ReleaseCodes.AlreadyReleased, $"#{number} was already released in {reader.GetString(2)}."));
            }

            items.Add((id, number, reader.GetInt64(3)));
        }

        var now = clock.GetUtcNow();
        var stamp = WorkItemRowMapper.Stamp(now);
        var releaseId = Guid.NewGuid().ToString("N");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                $"INSERT INTO releases ({Columns}) VALUES ($id, $p, $version, $tag, $commit, $created, $at, $notes, NULL, $by);";
            insert.Parameters.AddWithValue("$id", releaseId);
            insert.Parameters.AddWithValue("$p", Project.Value);
            insert.Parameters.AddWithValue("$version", version);
            insert.Parameters.AddWithValue("$tag", release.Tag.Trim());
            insert.Parameters.AddWithValue("$commit", (object?)release.Commit?.Trim() ?? DBNull.Value);
            insert.Parameters.AddWithValue("$created", release.TagCreated ? 1 : 0);
            insert.Parameters.AddWithValue("$at", stamp);
            insert.Parameters.AddWithValue("$notes", release.Notes ?? string.Empty);
            insert.Parameters.AddWithValue("$by", string.IsNullOrWhiteSpace(release.CreatedBy) ? "user" : release.CreatedBy);
            insert.ExecuteNonQuery();
        }

        var changes = new JsonObject { ["released_in"] = new JsonObject { ["from"] = null, ["to"] = version } }.ToJsonString();
        foreach (var (id, _, _) in items)
        {
            Execute(connection, transaction, "INSERT INTO release_items (release_id, item_id) VALUES ($r, $i);", ("$r", releaseId), ("$i", id.Value));

            using var stampItem = connection.CreateCommand();
            stampItem.Transaction = transaction;
            stampItem.CommandText =
                "UPDATE work_items SET released_in = $v, version = version + 1, updated_utc = $u "
                + "WHERE id = $id AND project_id = $p AND released_in IS NULL RETURNING version;";
            stampItem.Parameters.AddWithValue("$v", version);
            stampItem.Parameters.AddWithValue("$u", stamp);
            stampItem.Parameters.AddWithValue("$id", id.Value);
            stampItem.Parameters.AddWithValue("$p", Project.Value);
            var bumped = stampItem.ExecuteScalar() as long?
                ?? throw new InvalidOperationException($"Work item {id.Value} changed inside its own release transaction.");

            Execute(
                connection,
                transaction,
                "INSERT INTO work_item_events (project_id, item_id, item_version, operation, actor_kind, actor_id, operation_id, changes_json, occurred_utc) "
                + "VALUES ($p, $item, $version, $operation, $actorKind, NULL, NULL, $changes, $at);",
                ("$p", Project.Value),
                ("$item", id.Value),
                ("$version", bumped),
                ("$operation", WorkItemEventOperation.Released.ToName()),
                ("$actorKind", StableNames.ToName(WorkItemActor.User.Kind)),
                ("$changes", changes),
                ("$at", stamp));
        }

        transaction.Commit();
        return Task.FromResult(new ReleaseResult(new Release(
            releaseId,
            Project,
            version,
            release.Tag.Trim(),
            release.Commit?.Trim(),
            release.TagCreated,
            WorkItemRowMapper.ParseStamp(stamp),
            release.Notes ?? string.Empty,
            null,
            [.. items.Select(i => i.Number).Order()],
            string.IsNullOrWhiteSpace(release.CreatedBy) ? "user" : release.CreatedBy)));
    }

    /// <inheritdoc />
    public Task<bool> SetWikiDocumentAsync(string releaseId, int wikiNumber, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE releases SET wiki_number = $n WHERE id = $id AND project_id = $p;";
        command.Parameters.AddWithValue("$n", wikiNumber);
        command.Parameters.AddWithValue("$id", releaseId ?? string.Empty);
        command.Parameters.AddWithValue("$p", Project.Value);
        return Task.FromResult(command.ExecuteNonQuery() == 1);
    }

    private Release? Find(SqliteConnection connection, SqliteTransaction? transaction, string version)
    {
        Release? found;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {Columns} FROM releases WHERE project_id = $p AND version = $v;";
            command.Parameters.AddWithValue("$p", Project.Value);
            command.Parameters.AddWithValue("$v", version);
            using var reader = command.ExecuteReader();
            found = reader.Read() ? Read(reader, []) : null;
        }

        return found is null ? null : found with { ItemNumbers = ItemNumbers(connection, transaction, found.Id) };
    }

    private static List<int> ItemNumbers(SqliteConnection connection, SqliteTransaction? transaction, string releaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT w.number FROM release_items ri JOIN work_items w ON w.id = ri.item_id WHERE ri.release_id = $r ORDER BY w.number;";
        command.Parameters.AddWithValue("$r", releaseId);
        var numbers = new List<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            numbers.Add(checked((int)reader.GetInt64(0)));
        }

        return numbers;
    }

    private static Release Read(SqliteDataReader reader, IReadOnlyList<int> numbers) => new(
        reader.GetString(0),
        new ProjectId(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt64(5) != 0,
        WorkItemRowMapper.ParseStamp(reader.GetString(6)),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : checked((int)reader.GetInt64(8)),
        numbers,
        reader.GetString(9));

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}

/// <summary>Hands out a project's release store, like the local tracker resolver does its board.</summary>
public sealed class SqliteReleaseStores(ISqliteConnectionFactory factory, TimeProvider clock) : IReleaseStores
{
    /// <inheritdoc />
    public IReleaseStore For(ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new SqliteReleaseStore(project, factory, clock);
    }
}
