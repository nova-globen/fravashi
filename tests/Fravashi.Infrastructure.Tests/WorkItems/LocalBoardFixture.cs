using System.Globalization;
using AwesomeAssertions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.WorkItems;

namespace Fravashi.Infrastructure.Tests.WorkItems;

/// <summary>A clock the tests move by hand, so every stamp is predictable.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The real local board over a real, file-backed temporary SQLite database with the production schema, plus raw
/// SQL helpers to count rows and to seed rows straight into the tables (the mirrored shapes of plan section 3.3,
/// which the port itself would refuse to author).
/// </summary>
internal sealed class LocalBoardFixture : IDisposable
{
    private readonly SqliteTestContext _context = new();

    public LocalBoardFixture()
    {
        Resolver = new LocalTrackerResolver(_context.Factory, Clock);
        Board = Resolver.For(ProjectId.Default);
    }

    public ManualClock Clock { get; } = new();

    /// <summary>The same database the board uses, so a sidecar store under test shares it (backlog 82).</summary>
    public Fravashi.Infrastructure.Persistence.ISqliteConnectionFactory Factory => _context.Factory;

    public LocalTrackerResolver Resolver { get; }

    public ITracker Board { get; }

    public static MutationContext User { get; } = MutationContext.User;

    public void Dispose() => _context.Dispose();

    public static NewWorkItem NewTask(string title = "A task", string description = "The problem and the outcome.", WorkItemStatus status = WorkItemStatus.Triage, WorkItemId? parent = null) =>
        new() { Level = WorkItemLevel.Task, Title = title, Description = description, Status = status, Parent = parent };

    public async Task<WorkItem> CreateAsync(NewWorkItem item, ITracker? board = null)
    {
        var result = await (board ?? Board).CreateAsync(item, User);
        result.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", result.Errors.Select(e => e.Code)));
        return result.Value;
    }

    public async Task<WorkItem> TaskAsync(string title = "A task", WorkItemStatus status = WorkItemStatus.Triage) =>
        await CreateAsync(NewTask(title, status: status));

    public async Task<WorkItem> ReloadAsync(WorkItem item) => (await Board.GetAsync(item.Id))!;

    public long Count(string sql)
    {
        using var connection = _context.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    public void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = _context.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    /// <summary>A snapshot of every work item table, to prove a refused call wrote nothing at all.</summary>
    public string StorageSnapshot()
    {
        using var connection = _context.Factory.Open();
        var text = new System.Text.StringBuilder();
        foreach (var table in new[] { "projects", "work_items", "work_item_links", "work_item_events", "work_item_operations" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY 1, 2;";
            using var reader = command.ExecuteReader();
            text.Append(table).Append(':');
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    text.Append(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).Append('|');
                }

                text.Append(';');
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes a row straight into <c>work_items</c>, bypassing the port and its authoring rules, the way item 47's
    /// mirroring path will write provider data. Advances the project's number so the board keeps allocating.
    /// </summary>
    public WorkItemId SeedRaw(
        int number,
        string level = "task",
        string kind = "standard",
        string title = "Mirrored",
        string description = "",
        string status = "triage",
        string? resolution = null,
        bool blocked = false,
        string? blockedReason = null,
        string? parentId = null,
        double rank = 1.0,
        string project = "default")
    {
        var id = "raw-" + number.ToString(CultureInfo.InvariantCulture) + "-" + project;
        Execute(
            "INSERT INTO work_items (id, project_id, number, level, kind, title, description, parent_id, status, resolution, " +
            "blocked, blocked_reason, deferred, rank, version, created_utc, updated_utc) VALUES " +
            "($id, $p, $n, $level, $kind, $title, $d, $parent, $status, $res, $blocked, $reason, 0, $rank, 1, " +
            "'2026-09-01T00:00:00.0000000+00:00', '2026-09-01T00:00:00.0000000+00:00');",
            ("$id", id), ("$p", project), ("$n", number), ("$level", level), ("$kind", kind), ("$title", title), ("$d", description),
            ("$parent", parentId), ("$status", status), ("$res", resolution), ("$blocked", blocked ? 1 : 0), ("$reason", blockedReason),
            ("$rank", rank));
        Execute("UPDATE projects SET next_item_number = MAX(next_item_number, $next) WHERE id = $p;", ("$next", number + 1), ("$p", project));
        return new WorkItemId(id);
    }

    /// <summary>Writes an edge straight into <c>work_item_links</c>, bypassing the cycle and duplicate checks.</summary>
    public void SeedEdge(WorkItemId source, string type, WorkItemId target, string project = "default") =>
        Execute(
            "INSERT INTO work_item_links (project_id, source_id, type, target_id, created_utc, created_by) VALUES ($p, $s, $t, $g, '2026-09-01T00:00:00Z', 'system');",
            ("$p", project), ("$s", source.Value), ("$t", type), ("$g", target.Value));

    public void CreateProject(string id) =>
        Execute("INSERT INTO projects (id, name, next_item_number, created_utc, updated_utc) VALUES ($id, $id, 1, '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');", ("$id", id));
}
