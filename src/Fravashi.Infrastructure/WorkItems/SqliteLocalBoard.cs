using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.WorkItems;

/// <summary>
/// The local board: <see cref="ITracker"/> over the SQLite store (schema v11, canonical artifact model plan
/// sections 3 to 5, increment B), bound to one project.
/// <para>
/// <b>Every mutation is one immediate transaction</b> (plan section 5.1): the operation ledger is read first (a
/// hit on the same operation and items is a replay that writes nothing, anything else is
/// <see cref="TrackerCodes.OperationIdReused"/>); the domain judges the change under the AUTHORING tier with
/// every parent, child and edge it needs read inside the same transaction; the row is written guarded by its
/// version; one event per touched item is appended; the ledger row is inserted last, so it commits with the
/// change or not at all. A refused call rolls back, so it writes nothing and consumes no number.
/// </para>
/// <para>
/// <b>Relational invariants are kept here</b> (plan section 1.8): a level or a parent is written only through a
/// placement check with the parent (and on a level change the children) supplied, and every item read back is
/// built with <see cref="WorkItem.Restore"/>.
/// </para>
/// </summary>
public sealed class SqliteLocalBoard : ITracker
{
    /// <summary>The provider name the local board reports.</summary>
    public const string LocalProviderName = "local";

    /// <summary>
    /// When the gap a placement would split falls below this, the column is renumbered first (decision D14).
    /// Far above double precision at ranks in the millions, so a midpoint is always strictly between.
    /// </summary>
    public const double RenumberGap = 1e-6;

    private const int SqliteConstraint = 19;

    private readonly ISqliteConnectionFactory _factory;
    private readonly TimeProvider _clock;

    /// <summary>Creates the board for <paramref name="project"/>.</summary>
    public SqliteLocalBoard(ProjectId project, ISqliteConnectionFactory factory, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(clock);
        Project = project;
        _factory = factory;
        _clock = clock;
    }

    /// <inheritdoc />
    public ProjectId Project { get; }

    /// <inheritdoc />
    public string ProviderName => LocalProviderName;

    /// <summary>Everything increment B implements; evidence arrives with increment C2.</summary>
    public TrackerCapabilities Capabilities { get; } = new(Create: true, Update: true, Transition: true, Move: true, Link: true, RecordEvidence: false);

    // ------------------------------------------------------------------------------------------------
    // Reads
    // ------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        return Task.FromResult(new Db(connection, null, Project).LoadItem(id));
    }

    /// <inheritdoc />
    public Task<WorkItem?> GetByNumberAsync(int number, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        var db = new Db(connection, null, Project);

        // The agent layer is loaded here exactly as it is for a read by id. Reading ONE item is one thing,
        // and which key the caller happened to hold must not change the shape that comes back: item 38's
        // detail view addresses an item by its number and was handed an item with no acceptance criteria.
        WorkItemId? id;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM work_items WHERE project_id = $p AND number = $n;";
            command.Parameters.AddWithValue("$p", Project.Value);
            command.Parameters.AddWithValue("$n", number);
            id = command.ExecuteScalar() is string found ? new WorkItemId(found) : null;
        }

        return Task.FromResult(id is null ? null : db.LoadItem(id));
    }

    /// <inheritdoc />
    public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        var limit = Math.Clamp(query.Limit, 1, WorkItemQuery.MaxLimit);
        var ordinal = StatusOrdinalSql("w.status");
        var where = new List<string> { "w.project_id = $p" };
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$p", Project.Value);

        void In<TEnum>(IEnumerable<TEnum>? values, string column, string prefix)
            where TEnum : struct, Enum
        {
            var list = values?.Distinct().ToList();
            if (list is not { Count: > 0 })
            {
                return;
            }

            var names = new List<string>();
            for (var i = 0; i < list.Count; i++)
            {
                var name = $"${prefix}{i.ToString(CultureInfo.InvariantCulture)}";
                names.Add(name);
                command.Parameters.AddWithValue(name, StableNames.ToName(list[i]));
            }

            where.Add($"{column} IN ({string.Join(", ", names)})");
        }

        In(query.Statuses, "w.status", "s");
        In(query.Categories?.SelectMany(c => WorkItemStatuses.All.Where(s => s.Category() == c)).ToList(), "w.status", "c");
        In(query.Levels, "w.level", "l");
        In(query.Kinds, "w.kind", "k");

        if (query.Parent is not null)
        {
            where.Add("w.parent_id = $parent");
            command.Parameters.AddWithValue("$parent", query.Parent.Value);
        }
        else if (query.NoParent)
        {
            where.Add("w.parent_id IS NULL");
        }

        switch (query.Flags)
        {
            case FlagFilter.Blocked: where.Add("w.blocked <> 0"); break;
            case FlagFilter.Deferred: where.Add("w.deferred <> 0"); break;
            case FlagFilter.Either: where.Add("(w.blocked <> 0 OR w.deferred <> 0)"); break;
            case FlagFilter.Neither: where.Add("w.blocked = 0 AND w.deferred = 0"); break;
        }

        if (query.Assignee is not null)
        {
            where.Add("w.assignee = $assignee");
            command.Parameters.AddWithValue("$assignee", query.Assignee);
        }

        if (!string.IsNullOrEmpty(query.Text))
        {
            where.Add("(w.title LIKE $text ESCAPE '\\' OR w.description LIKE $text ESCAPE '\\')");
            var escaped = query.Text.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            command.Parameters.AddWithValue("$text", "%" + escaped + "%");
        }

        if (query.LinkedTo is { } linked)
        {
            var forward = StableNames.ToName(linked.Type.ToForward());
            command.Parameters.AddWithValue("$linked", linked.Item.Value);
            command.Parameters.AddWithValue("$linkedType", forward);
            var outgoing = "w.id IN (SELECT target_id FROM work_item_links WHERE project_id = $p AND source_id = $linked AND type = $linkedType)";
            var incoming = "w.id IN (SELECT source_id FROM work_item_links WHERE project_id = $p AND target_id = $linked AND type = $linkedType)";
            where.Add(linked.Type.IsSymmetric() ? $"({outgoing} OR {incoming})" : linked.Type.IsForward() ? outgoing : incoming);
        }

        if (query.NamesWikiDocument is { } document)
        {
            // GLOB is case-sensitive and has character classes, which is what lets "@12" refuse "@123", "a@12" and
            // "#@12" without reading every description back into the process.
            var mention = "@" + document.ToString(CultureInfo.InvariantCulture);
            const string Before = "[^0-9A-Za-z#!@]";
            const string After = "[^0-9A-Za-z]";
            command.Parameters.AddWithValue("$wikiWhole", mention);
            command.Parameters.AddWithValue("$wikiStart", mention + After + "*");
            command.Parameters.AddWithValue("$wikiEnd", "*" + Before + mention);
            command.Parameters.AddWithValue("$wikiInside", "*" + Before + mention + After + "*");
            command.Parameters.AddWithValue("$wikiTarget", document.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$wikiTargetAt", mention);
            command.Parameters.AddWithValue("$wikiKind", StableNames.ToName(ArtifactRefKind.Wiki));
            command.Parameters.AddWithValue("$designKind", StableNames.ToName(ArtifactRefKind.Design));
            string Written(string column) =>
                $"{column} = $wikiWhole OR {column} GLOB $wikiStart OR {column} GLOB $wikiEnd OR {column} GLOB $wikiInside";
            where.Add(
                "(EXISTS (SELECT 1 FROM work_item_refs r WHERE r.item_id = w.id AND r.kind IN ($wikiKind, $designKind) "
                + "AND r.target IN ($wikiTarget, $wikiTargetAt)) OR "
                + Written("w.title") + " OR " + Written("w.description") + ")");
        }

        // Work item #368: archived items are a condition on the indexed column, never a scan in the page.
        switch (query.Release)
        {
            case ReleaseFilter.NotArchived:
                where.Add("(w.released_in IS NULL OR w.status <> $archivedStatus)");
                command.Parameters.AddWithValue("$archivedStatus", StableNames.ToName(WorkItemStatus.Done));
                break;
            case ReleaseFilter.Unreleased: where.Add("w.released_in IS NULL"); break;
            case ReleaseFilter.Released: where.Add("w.released_in IS NOT NULL"); break;
        }

        if (query.ReleasedIn is { Length: > 0 } releasedIn)
        {
            where.Add("w.released_in = $releasedIn");
            command.Parameters.AddWithValue("$releasedIn", releasedIn);
        }

        if (query.Cursor is { } cursor)
        {
            var (cursorOrdinal, cursorRank, cursorNumber) = DecodeCursor(cursor);
            where.Add($"(({ordinal}) > $co OR (({ordinal}) = $co AND w.rank > $cr) OR (({ordinal}) = $co AND w.rank = $cr AND w.number > $cn))");
            command.Parameters.AddWithValue("$co", cursorOrdinal);
            command.Parameters.AddWithValue("$cr", cursorRank);
            command.Parameters.AddWithValue("$cn", cursorNumber);
        }

        command.CommandText =
            $"SELECT {WorkItemRowMapper.ReadColumnsOf("w")} FROM work_items w WHERE {string.Join(" AND ", where)} " +
            $"ORDER BY ({ordinal}), w.rank, w.number LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit + 1);

        var items = new List<WorkItem>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(WorkItemRowMapper.Read(reader));
            }
        }

        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            next = EncodeCursor(StatusOrdinal(last.Status), last.Rank, last.Number);
        }

        return Task.FromResult(new WorkItemPage(items, next));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkItemEvent>> HistoryAsync(WorkItemId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ct.ThrowIfCancellationRequested();
        var events = new List<WorkItemEvent>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, item_version, operation, actor_kind, actor_id, operation_id, changes_json, occurred_utc " +
            "FROM work_item_events WHERE project_id = $p AND item_id = $id ORDER BY id;";
        command.Parameters.AddWithValue("$p", Project.Value);
        command.Parameters.AddWithValue("$id", id.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var operationName = reader.GetString(2);
            var operation = WorkItemEventOperations.Parse(operationName)
                ?? throw new InvalidDataException($"Stored work item event has an unknown operation '{operationName}'.");
            events.Add(new WorkItemEvent(
                reader.GetInt64(0),
                Project,
                id,
                reader.GetInt64(1),
                operation,
                ParseActor(reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                WorkItemRowMapper.ParseStamp(reader.GetString(7))));
        }

        return Task.FromResult<IReadOnlyList<WorkItemEvent>>(events);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkItemLink>> LinksAsync(WorkItemId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        return Task.FromResult<IReadOnlyList<WorkItemLink>>(new Db(connection, null, Project).EdgesTouching(id));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EvidenceRecord>> EvidenceAsync(WorkItemId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        return Task.FromResult<IReadOnlyList<EvidenceRecord>>(
            EvidenceRows.Load(connection, null, id.Value));
    }

    /// <inheritdoc />
    public Task<StartabilityReport> WhatCanStartNowAsync(StartableQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        using var connection = _factory.Open();
        var db = new Db(connection, null, Project);

        // Everything in the project once, then the graph in memory: the alternative is one query per item for
        // its blockers, and the answer is read on every board render. A project is thousands of items at most.
        var items = db.AllItems();
        if (items.Count == 0)
        {
            return Task.FromResult(StartabilityReport.Empty);
        }

        var byId = items.ToDictionary(i => i.Id);
        var blockersOf = db.BlockedByEdges();

        var considered = items.Where(item =>
            (query.Parent is null || item.Parent == query.Parent)
            && (query.Assignee is null || string.Equals(item.Assignee, query.Assignee, StringComparison.Ordinal)));

        var startable = new List<StartableItem>();
        var held = new List<StartableItem>();
        foreach (var item in considered)
        {
            var blockers = blockersOf.TryGetValue(item.Id, out var ids)
                ? ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList()
                : [];
            var verdict = Startability.Evaluate(item, blockers);
            var entry = new StartableItem(item, verdict);
            if (verdict.CanStart && (query.IncludeAdvisoriesOnly || verdict.Advisories.Count == 0))
            {
                startable.Add(entry);
            }
            else
            {
                held.Add(entry);
            }
        }

        // Board order: every startable item is in the Ready column, so its rank is that column's order.
        static IReadOnlyList<StartableItem> Ordered(IEnumerable<StartableItem> entries, int limit) =>
            [.. entries.OrderBy(e => e.Item.Rank).ThenBy(e => e.Item.Number).Take(limit)];

        return Task.FromResult(new StartabilityReport(
            Ordered(startable, query.Limit),
            Ordered(held, int.MaxValue)));
    }

    // ------------------------------------------------------------------------------------------------
    // Mutations
    // ------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<TrackerResult<WorkItem>> CreateAsync(NewWorkItem item, MutationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        var intended = new LedgerEntry(WorkItemEventOperation.Created, null, null, null);
        return Task.FromResult(Mutate(
            context,
            intended,
            (db, row) => db.LoadItem(new WorkItemId(row.ItemId!)) is { } created
                ? TrackerResult<WorkItem>.Ok(created, replayed: true)
                : throw new InvalidDataException($"The ledger names created item {row.ItemId}, which does not exist."),
            db =>
            {
                var number = db.NextItemNumber();
                if (number is null)
                {
                    return Refuse<WorkItem>(TrackerCodes.ProjectNotFound, "project", $"Project {Project.Value} does not exist.");
                }

                var parent = item.Parent is null ? null : db.LoadItem(item.Parent, anyProject: true);
                var now = _clock.GetUtcNow();
                var proposed = new WorkItemState
                {
                    Id = WorkItemId.New(),
                    Project = Project,
                    Number = number.Value,
                    Level = item.Level,
                    Kind = item.Kind,
                    Title = item.Title,
                    Description = item.Description,
                    Parent = item.Parent,
                    Status = item.Status,
                    Resolution = item.Resolution,
                    Flags = item.Flags ?? WorkItemFlags.None,
                    Priority = item.Priority,
                    Assignee = item.Assignee,
                    SpikeTimebox = WholeMinutes(item.SpikeTimebox),
                    Version = 1,
                    CreatedUtc = now,
                    UpdatedUtc = now,

                    // The agent layer can be authored with the item (increment C1), so a caller can create
                    // something an agent could pick up without a second round trip.
                    AcceptanceCriteria = item.AcceptanceCriteria,
                    Readiness = item.Readiness,
                    Ambiguity = item.Ambiguity,
                    PersonBlockers = item.PersonBlockers,
                    Refs = item.Refs,
                    ChangeBudget = item.ChangeBudget,
                    ScopePatterns = item.ScopePatterns,
                    NextAction = item.NextAction,
                    Scenarios = item.Scenarios,
                    SuggestedModel = item.SuggestedModel,
                    SuggestedEffort = item.SuggestedEffort,
                };

                var validated = WorkItemRules.ValidateNew(proposed, parent);
                if (!validated.IsValid)
                {
                    return (TrackerResult<WorkItem>.Invalid(validated.Errors), null);
                }

                var placed = db.Place(proposed.Id, validated.Value.Status, null, null);
                var state = validated.Value.ToState() with { Rank = placed.Rank };
                db.InsertItem(state);
                db.ConsumeItemNumber(number.Value);
                db.AppendEvent(state.Id, state.Version, WorkItemEventOperation.Created, context, WorkItemRowMapper.Diff(null, state), now);
                return (TrackerResult<WorkItem>.Ok(db.LoadItem(state.Id)!), intended with { ItemId = state.Id.Value });
            }));
    }

    /// <inheritdoc />
    public Task<TrackerResult<WorkItem>> UpdateAsync(WorkItemId id, WorkItemPatch patch, long expectedVersion, MutationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(patch);
        ct.ThrowIfCancellationRequested();
        var intended = new LedgerEntry(WorkItemEventOperation.Updated, id.Value, null, null);
        return Task.FromResult(Mutate(context, intended, ReplayItem(id), db =>
        {
            var current = db.LoadItem(id);
            if (current is null)
            {
                return (TrackerResult<WorkItem>.NotFound(), null);
            }

            if (current.Version != expectedVersion)
            {
                return (TrackerResult<WorkItem>.Conflict(current.Version), null);
            }

            var proposed = current.ToState() with
            {
                Title = patch.Title.Or(current.Title),
                Description = patch.Description.Or(current.Description),
                Level = patch.Level.Or(current.Level),
                Kind = patch.Kind.Or(current.Kind),
                Parent = patch.Parent.Or(current.Parent),
                Priority = patch.Priority.Or(current.Priority),
                Assignee = patch.Assignee.Or(current.Assignee),
                SpikeTimebox = patch.SpikeTimebox.IsSet ? WholeMinutes(patch.SpikeTimebox.Value) : current.SpikeTimebox,
                Flags = patch.Flags.IsSet ? patch.Flags.Value ?? WorkItemFlags.None : current.Flags,
                MarkedForAutopilot = patch.MarkedForAutopilot.Or(current.MarkedForAutopilot),

                // Each agent-layer list is replaced WHOLE when the patch mentions it (plan section 5.2), and
                // the ids the caller passed back are kept by the store, so evidence against an unchanged
                // criterion survives an edit of its neighbours.
                AcceptanceCriteria = patch.AcceptanceCriteria.Or(current.AcceptanceCriteria),
                Readiness = patch.Readiness.Or(current.Readiness),
                Ambiguity = patch.Ambiguity.IsSet ? patch.Ambiguity.Value : current.Ambiguity,
                PersonBlockers = patch.PersonBlockers.Or(current.PersonBlockers),
                Refs = patch.Refs.Or(current.Refs),
                ChangeBudget = patch.ChangeBudget.Or(current.ChangeBudget),
                ScopePatterns = patch.ScopePatterns.Or(current.ScopePatterns),
                NextAction = patch.NextAction.IsSet ? patch.NextAction.Value : current.NextAction,
                Scenarios = patch.Scenarios.Or(current.Scenarios),

                // Requested 2026-10-22: null is a real value here (Default), so IsSet decides, as for the next action.
                SuggestedModel = patch.SuggestedModel.IsSet ? patch.SuggestedModel.Value : current.SuggestedModel,
                SuggestedEffort = patch.SuggestedEffort.IsSet ? patch.SuggestedEffort.Value : current.SuggestedEffort,
            };

            WorkItem? proposedParent = null;
            IReadOnlyList<WorkItem> children = [];
            var levelChanged = proposed.Level != current.Level;
            if (levelChanged || proposed.Parent != current.Parent)
            {
                proposedParent = proposed.Parent is null ? null : db.LoadItem(proposed.Parent, anyProject: true);
                if (levelChanged)
                {
                    children = db.Children(id);
                }
            }

            var validated = WorkItemRules.ValidateUpdate(current, proposed, proposedParent, children);
            if (!validated.IsValid)
            {
                return (TrackerResult<WorkItem>.Invalid(validated.Errors), null);
            }

            // Evidence is append-only and points at entries by id, so dropping an entry it names would leave
            // it proving something that no longer exists (increment C2). The check lives here rather than in
            // the domain because only the store knows what evidence has been recorded.
            var (provenCriteria, provenScenarios) =
                EvidenceRows.Referenced(db.Connection, db.Transaction, id.Value);
            var keptCriteria = validated.Value.AcceptanceCriteria.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var keptScenarios = validated.Value.Scenarios.Select(sc => sc.Id).ToHashSet(StringComparer.Ordinal);

            if (provenCriteria.FirstOrDefault(c => !keptCriteria.Contains(c)) is { Length: > 0 } droppedCriterion)
            {
                return Refuse<WorkItem>(
                    TrackerCodes.CriterionHasEvidence,
                    "acceptanceCriteria",
                    $"Acceptance criterion '{droppedCriterion}' has evidence recorded against it, so it cannot "
                    + "be removed; edit its text or record newer evidence instead.");
            }

            if (provenScenarios.FirstOrDefault(sc => !keptScenarios.Contains(sc)) is { Length: > 0 } droppedScenario)
            {
                return Refuse<WorkItem>(
                    TrackerCodes.ScenarioHasEvidence,
                    "scenarios",
                    $"Test scenario '{droppedScenario}' has evidence recorded against it, so it cannot be "
                    + "removed; edit it or record newer evidence instead.");
            }

            return Write(db, current, validated.Value.ToState(), WorkItemEventOperation.Updated, context, intended);
        }));
    }

    /// <inheritdoc />
    public Task<TrackerResult<WorkItem>> TransitionAsync(WorkItemId id, WorkItemStatus to, WorkItemResolution? resolution, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ct.ThrowIfCancellationRequested();
        var intended = new LedgerEntry(WorkItemEventOperation.Transitioned, id.Value, null, null);
        return Task.FromResult(Mutate(context, intended, ReplayItem(id), db =>
        {
            var current = db.LoadItem(id);
            if (current is null)
            {
                return (TrackerResult<WorkItem>.NotFound(), null);
            }

            if (current.Version != expectedVersion)
            {
                return (TrackerResult<WorkItem>.Conflict(current.Version), null);
            }

            var applied = WorkItemRules.ApplyTransition(current, to, resolution, db.EdgesTouching(id));
            if (!applied.IsValid)
            {
                return (TrackerResult<WorkItem>.Invalid(applied.Errors), null);
            }

            // Requested 2026-10-22 (evidence on Done): a move into Done asks for evidence on every acceptance
            // criterion, or a written reason. It sits here and not in the domain for the same reason the
            // criterion-has-evidence check does: only the store knows what evidence has been recorded. A move
            // that arrives from a provider is exempt (DoneEvidence.Applies reads MutationContext.FromProvider).
            IReadOnlyList<AcceptanceCriterion> unproven = [];
            if (DoneEvidence.Applies(current, to, context))
            {
                unproven = DoneEvidence.CriteriaWithoutEvidence(
                    current, EvidenceRows.Load(db.Connection, db.Transaction, id.Value));
                if (unproven.Count > 0 && !DoneEvidence.HasReason(context.Reason))
                {
                    return Refuse<WorkItem>(
                        TrackerCodes.DoneNeedsEvidence, "reason", DoneEvidence.Refusal(db.Describe(id), unproven));
                }
            }

            var next = applied.Value.ToState();
            if (next.Status != current.Status)
            {
                var placed = db.Place(id, next.Status, after, before);
                if (placed.Errors.Count > 0)
                {
                    return (TrackerResult<WorkItem>.Invalid(placed.Errors), null);
                }

                next = next with { Rank = placed.Rank };
            }

            var written = Write(db, current, next, WorkItemEventOperation.Transitioned, context, intended);
            if (written.Result.IsOk && unproven.Count > 0)
            {
                // The reason joins the same transaction as the move, so neither can exist without the other.
                // It carries the version the move wrote: it is part of that one change, not a second one.
                var reason = DoneEvidence.ReasonRecord(context.Reason!, unproven);
                EvidenceRows.Insert(db.Connection, db.Transaction, id.Value, new EvidenceRecord(
                    AgentLayerEntry.NewId(),
                    reason.Kind,
                    reason.Result,
                    reason.Summary,
                    reason.Gate,
                    null,
                    null,
                    null,
                    reason.Details,
                    _clock.GetUtcNow(),
                    context.Actor));
            }

            return written;
        }));
    }

    /// <inheritdoc />
    public Task<TrackerResult<WorkItem>> MoveAsync(WorkItemId id, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ct.ThrowIfCancellationRequested();
        var intended = new LedgerEntry(WorkItemEventOperation.Moved, id.Value, null, null);
        return Task.FromResult(Mutate(context, intended, ReplayItem(id), db =>
        {
            var current = db.LoadItem(id);
            if (current is null)
            {
                return (TrackerResult<WorkItem>.NotFound(), null);
            }

            if (current.Version != expectedVersion)
            {
                return (TrackerResult<WorkItem>.Conflict(current.Version), null);
            }

            if (after is null && before is null)
            {
                return Refuse<WorkItem>(TrackerCodes.MoveAnchorRequired, "after", "A move needs an item to go after or before.");
            }

            var placed = db.Place(id, current.Status, after, before);
            if (placed.Errors.Count > 0)
            {
                return (TrackerResult<WorkItem>.Invalid(placed.Errors), null);
            }

            return Write(db, current, current.ToState() with { Rank = placed.Rank }, WorkItemEventOperation.Moved, context, intended);
        }));
    }

    /// <inheritdoc />
    public Task<TrackerResult<WorkItemLink>> LinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ct.ThrowIfCancellationRequested();
        var normalised = WorkItemLink.Normalise(from, type, to);
        if (!normalised.IsValid)
        {
            ArgumentNullException.ThrowIfNull(context);
            return Task.FromResult(TrackerResult<WorkItemLink>.Invalid(normalised.Errors));
        }

        var link = normalised.Value;
        var typeName = StableNames.ToName(link.Type);
        var intended = new LedgerEntry(WorkItemEventOperation.Linked, link.Source.Value, link.Target.Value, typeName);
        return Task.FromResult(Mutate(
            context,
            intended,
            (_, _) => TrackerResult<WorkItemLink>.Ok(link, replayed: true),
            db =>
            {
                var fromItem = db.LoadItem(from);
                var toItem = db.LoadItem(to, anyProject: true);
                if (fromItem is null || toItem is null)
                {
                    return (TrackerResult<WorkItemLink>.NotFound(), null);
                }

                var exists = db.EdgeExists(link);
                var edges = link.Type switch
                {
                    WorkItemLinkType.Blocks or WorkItemLinkType.Supersedes => db.ReachableEdges(link.Target, link.Type),
                    WorkItemLinkType.Duplicates => db.EdgesFrom(link.Target, WorkItemLinkType.Duplicates),
                    _ => [],
                };

                if (exists)
                {
                    edges.Add(link);
                }

                var validated = WorkItemRules.ValidateLink(fromItem, type, toItem, edges, db.Describe);
                if (!validated.IsValid)
                {
                    return (TrackerResult<WorkItemLink>.Invalid(validated.Errors), null);
                }

                if (exists)
                {
                    return (TrackerResult<WorkItemLink>.Ok(link), intended);
                }

                var now = _clock.GetUtcNow();
                db.InsertEdge(link, context.Actor, now);
                TouchBothEnds(db, link, WorkItemEventOperation.Linked, context, now);
                return (TrackerResult<WorkItemLink>.Ok(link), intended);
            }));
    }

    /// <inheritdoc />
    public Task<TrackerResult<bool>> UnlinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ct.ThrowIfCancellationRequested();
        var normalised = WorkItemLink.Normalise(from, type, to);
        if (!normalised.IsValid)
        {
            ArgumentNullException.ThrowIfNull(context);
            return Task.FromResult(TrackerResult<bool>.Invalid(normalised.Errors));
        }

        var link = normalised.Value;
        var typeName = StableNames.ToName(link.Type);
        var intended = new LedgerEntry(WorkItemEventOperation.Unlinked, link.Source.Value, link.Target.Value, typeName);
        return Task.FromResult(Mutate(
            context,
            intended,
            (_, row) => TrackerResult<bool>.Ok(row.ResultRef?.EndsWith("|removed", StringComparison.Ordinal) == true, replayed: true),
            db =>
            {
                var fromItem = db.LoadItem(from);
                var toItem = db.LoadItem(to, anyProject: true);

                if (!db.EdgeExists(link))
                {
                    // No edge and no items either: the caller is addressing nothing at all. With at least
                    // one end present, there was simply nothing left to remove, which is an idempotent
                    // success rather than a mistake.
                    return fromItem is null && toItem is null
                        ? (TrackerResult<bool>.NotFound(), null)
                        : (TrackerResult<bool>.Ok(false), intended with { ResultRef = typeName + "|absent" });
                }

                // A DANGLING edge is exactly what referential repair exists to clear (backlog 82), and it
                // only exists because the other store lost an item, never because this board authored it.
                // Refusing to remove one on the grounds that the thing it points at is missing would make a
                // hole in the graph permanent, and removal is the only repair there is.
                var source = link.Source == fromItem?.Id ? fromItem
                    : link.Source == toItem?.Id ? toItem
                    : null;

                if (source is not null)
                {
                    // The one authoring rule here is about the SOURCE item's own resolution. With no source
                    // to read, there is no rule left to protect.
                    var allowed = WorkItemRules.ValidateUnlink(source, link, db.EdgesFrom(link.Source, null));
                    if (!allowed.IsValid)
                    {
                        return (TrackerResult<bool>.Invalid(allowed.Errors), null);
                    }
                }

                var now = _clock.GetUtcNow();
                db.DeleteEdge(link);
                TouchBothEnds(db, link, WorkItemEventOperation.Unlinked, context, now);
                return (TrackerResult<bool>.Ok(true), intended with { ResultRef = typeName + "|removed" });
            }));
    }

    // ------------------------------------------------------------------------------------------------
    // The transaction scaffold
    // ------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<TrackerResult<EvidenceRecord>> RecordEvidenceAsync(
        WorkItemId id, NewEvidence evidence, MutationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(evidence);
        ct.ThrowIfCancellationRequested();
        var intended = new LedgerEntry(WorkItemEventOperation.EvidenceRecorded, id.Value, null, null);
        return Task.FromResult(Mutate<EvidenceRecord>(
            context,
            intended,
            // A replay returns the evidence that operation id already wrote, never a second copy of it.
            (db, row) => EvidenceRows.Load(db.Connection, db.Transaction, id.Value) is { Count: > 0 } recorded
                ? TrackerResult<EvidenceRecord>.Ok(recorded[^1], replayed: true)
                : throw new InvalidDataException($"The ledger names evidence on {row.ItemId}, which does not exist."),
            db =>
            {
                var item = db.LoadItem(id);
                if (item is null)
                {
                    return (TrackerResult<EvidenceRecord>.NotFound(), null);
                }

                if (string.IsNullOrWhiteSpace(evidence.Summary))
                {
                    return Refuse<EvidenceRecord>(
                        TrackerCodes.EvidenceSummaryRequired,
                        "summary",
                        "Evidence needs a sentence someone can read later; without one the record proves nothing.");
                }

                // A criterion or scenario that is not this item's would be a dangling pointer the moment it
                // was written, and evidence is append-only, so there would be no way to correct it.
                if (evidence.Criterion is { Length: > 0 } criterion
                    && item.AcceptanceCriteria.All(c => c.Id != criterion))
                {
                    return Refuse<EvidenceRecord>(
                        TrackerCodes.EvidenceCriterionNotFound,
                        "criterion",
                        $"Item {db.Describe(id)} has no acceptance criterion '{criterion}'.");
                }

                if (evidence.Scenario is { Length: > 0 } scenario
                    && item.Scenarios.All(s => s.Id != scenario))
                {
                    return Refuse<EvidenceRecord>(
                        TrackerCodes.EvidenceScenarioNotFound,
                        "scenario",
                        $"Item {db.Describe(id)} has no test scenario '{scenario}'.");
                }

                var now = _clock.GetUtcNow();
                var record = new EvidenceRecord(
                    AgentLayerEntry.NewId(),
                    evidence.Kind,
                    evidence.Result,
                    evidence.Summary.Trim(),
                    evidence.Gate,
                    evidence.ArtefactRef,
                    evidence.Criterion,
                    evidence.Scenario,
                    evidence.Details,
                    now,
                    context.Actor);

                EvidenceRows.Insert(db.Connection, db.Transaction, id.Value, record);

                // The item's own version moves, because something about it did: a reader holding the old
                // version has not seen this evidence. The event log says what was recorded, not the bytes.
                var version = db.BumpVersion(id, now);
                db.AppendEvent(
                    id,
                    version,
                    WorkItemEventOperation.EvidenceRecorded,
                    context,
                    new JsonObject
                    {
                        ["evidence"] = new JsonObject
                        {
                            ["from"] = null,
                            ["to"] = $"{StableNames.ToName(record.Kind)} {StableNames.ToName(record.Result)}: {record.Summary}",
                        },
                    },
                    now);

                return (TrackerResult<EvidenceRecord>.Ok(record), intended);
            }));
    }

    private TrackerResult<T> Mutate<T>(
        MutationContext context,
        LedgerEntry intended,
        Func<Db, LedgerEntry, TrackerResult<T>> replay,
        Func<Db, (TrackerResult<T> Result, LedgerEntry? Entry)> body)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Actor);

        // Two passes at most: a concurrent writer that recorded the same operation id between our read and our
        // insert loses the ledger primary key to it, and the second pass answers it as the replay it is.
        for (var attempt = 0; ; attempt++)
        {
            using var connection = _factory.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var db = new Db(connection, transaction, Project);

            if (context.OperationId is { } operationId)
            {
                var recorded = db.ReadLedger(operationId);
                if (recorded is not null)
                {
                    return intended.SameOperationAs(recorded)
                        ? replay(db, recorded)
                        : TrackerResult<T>.Invalid(
                            TrackerCodes.OperationIdReused,
                            "operationId",
                            $"Operation id '{operationId}' was already used for a different operation ({recorded.Operation.ToName()} on item {recorded.ItemId}).");
                }
            }

            var (result, entry) = body(db);
            if (!result.IsOk)
            {
                return result;
            }

            if (context.OperationId is { } id && entry is not null)
            {
                try
                {
                    db.InsertLedger(id, entry, _clock.GetUtcNow());
                }
                catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraint && attempt == 0)
                {
                    continue;
                }
            }

            transaction.Commit();
            return result;
        }
    }

    private static Func<Db, LedgerEntry, TrackerResult<WorkItem>> ReplayItem(WorkItemId id) =>
        (db, _) => db.LoadItem(id) is { } item
            ? TrackerResult<WorkItem>.Ok(item, replayed: true)
            : TrackerResult<WorkItem>.NotFound();

    private static (TrackerResult<T> Result, LedgerEntry? Entry) Refuse<T>(string code, string field, string message) =>
        (TrackerResult<T>.Invalid(code, field, message), null);

    /// <summary>
    /// Writes <paramref name="next"/> over <paramref name="current"/> guarded by its version and appends one event.
    /// A change that changes nothing writes nothing and bumps nothing, but is still a success.
    /// </summary>
    private (TrackerResult<WorkItem> Result, LedgerEntry? Entry) Write(
        Db db,
        WorkItem current,
        WorkItemState next,
        WorkItemEventOperation operation,
        MutationContext context,
        LedgerEntry intended)
    {
        var before = current.ToState();
        var changes = WorkItemRowMapper.Diff(before, next);
        if (changes.Count == 0)
        {
            return (TrackerResult<WorkItem>.Ok(current), intended);
        }

        var now = _clock.GetUtcNow();
        var written = next with { Version = current.Version + 1, UpdatedUtc = now };
        if (!db.UpdateItem(written, current.Version))
        {
            return (TrackerResult<WorkItem>.Conflict(db.LoadItem(current.Id)?.Version ?? current.Version), null);
        }

        db.AppendEvent(current.Id, written.Version, operation, context, changes, now);
        return (TrackerResult<WorkItem>.Ok(db.LoadItem(current.Id)!), intended);
    }

    /// <summary>
    /// Records the change on both ends of an edge. An end that is NOT THERE is skipped rather than treated
    /// as a fault: an edge can dangle when the other store loses an item (backlog 82), and removing such an
    /// edge is the only repair available for it, so failing here would make a hole in the graph permanent.
    /// Every other caller passes two ends that exist, and for them nothing changes.
    /// </summary>
    private static void TouchBothEnds(Db db, WorkItemLink link, WorkItemEventOperation operation, MutationContext context, DateTimeOffset now)
    {
        var rendered = $"{db.Describe(link.Source)} {StableNames.ToName(link.Type)} {db.Describe(link.Target)}";
        var added = operation == WorkItemEventOperation.Linked;
        foreach (var end in new[] { link.Source, link.Target })
        {
            if (db.BumpVersionIfPresent(end, now) is not { } version)
            {
                continue;
            }

            var changes = new JsonObject
            {
                ["link"] = new JsonObject
                {
                    ["from"] = added ? null : rendered,
                    ["to"] = added ? rendered : null,
                },
            };
            db.AppendEvent(end, version, operation, context, changes, now);
        }
    }

    private static TimeSpan? WholeMinutes(TimeSpan? timebox) =>
        timebox is { } value && value > TimeSpan.Zero
            ? TimeSpan.FromMinutes(Math.Ceiling(value.TotalMinutes))
            : timebox;

    private static WorkItemActor ParseActor(string kind, string? id) => kind switch
    {
        "user" => WorkItemActor.User,
        "system" => WorkItemActor.System,
        "session" when !string.IsNullOrWhiteSpace(id) => WorkItemActor.Session(id),
        _ => throw new InvalidDataException($"Stored work item event has an unreadable actor '{kind}'."),
    };

    private static int StatusOrdinal(WorkItemStatus status)
    {
        var ordinal = -1;
        for (var i = 0; i < WorkItemStatuses.All.Count; i++)
        {
            if (WorkItemStatuses.All[i] == status)
            {
                ordinal = i;
            }
        }

        return ordinal;
    }

    private static string StatusOrdinalSql(string column)
    {
        var sql = new StringBuilder("CASE ").Append(column);
        for (var i = 0; i < WorkItemStatuses.All.Count; i++)
        {
            sql.Append(CultureInfo.InvariantCulture, $" WHEN '{StableNames.ToName(WorkItemStatuses.All[i])}' THEN {i}");
        }

        return sql.Append(" END").ToString();
    }

    private static string EncodeCursor(int statusOrdinal, double rank, int number) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{statusOrdinal}|{rank:R}|{number}")));

    private static (int StatusOrdinal, double Rank, int Number) DecodeCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length == 3
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rank)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return (ordinal, rank, number);
            }
        }
        catch (FormatException)
        {
        }

        throw new ArgumentException("Not a cursor this board issued.", nameof(cursor));
    }

    // ------------------------------------------------------------------------------------------------
    // The ledger entry and the row-level access, all on one connection and transaction
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One <c>work_item_operations</c> row. Two entries are the same operation when the operation matches and,
    /// where the intended entry names them, the item, the related item and the link type do (a create names no
    /// item, because the caller does not know the id it will get).
    /// </summary>
    private sealed record LedgerEntry(WorkItemEventOperation Operation, string? ItemId, string? RelatedId, string? ResultRef)
    {
        public bool SameOperationAs(LedgerEntry recorded) =>
            Operation == recorded.Operation
            && (ItemId is null || string.Equals(ItemId, recorded.ItemId, StringComparison.Ordinal))
            && string.Equals(RelatedId, recorded.RelatedId, StringComparison.Ordinal)
            && (ResultRef is null || string.Equals(ResultRef, recorded.ResultRef?.Split('|')[0], StringComparison.Ordinal));
    }

    private sealed record Placement(double Rank, IReadOnlyList<ValidationError> Errors);

    private sealed class Db(SqliteConnection connection, SqliteTransaction? transaction, ProjectId project)
    {
        private SqliteCommand Command(string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$p", project.Value);
            return command;
        }

        /// <summary>The open connection, for the row helpers that work on their own tables.</summary>
        public SqliteConnection Connection => connection;

        /// <summary>The caller's transaction, so a helper's write joins the same atomic unit.</summary>
        public SqliteTransaction? Transaction => transaction;

        /// <summary>Every item in the project, agent layer included: what the startability answer reads.</summary>
        public List<WorkItem> AllItems()
        {
            List<WorkItem> items;
            using (var command = Command($"SELECT {WorkItemRowMapper.ReadColumns} FROM work_items WHERE project_id = $p;"))
            {
                using var reader = command.ExecuteReader();
                items = [];
                while (reader.Read())
                {
                    items.Add(WorkItemRowMapper.Read(reader));
                }
            }

            return [.. items.Select(WithAgentLayer)];
        }

        /// <summary>For each item, the items that block it, read from the stored forward edges.</summary>
        public Dictionary<WorkItemId, List<WorkItemId>> BlockedByEdges()
        {
            using var command = Command(
                "SELECT source_id, target_id FROM work_item_links WHERE project_id = $p AND type = $type;");
            command.Parameters.AddWithValue("$type", StableNames.ToName(WorkItemLinkType.Blocks));
            using var reader = command.ExecuteReader();
            var map = new Dictionary<WorkItemId, List<WorkItemId>>();
            while (reader.Read())
            {
                // Stored as "source blocks target", so the TARGET is the one that is blocked.
                var blocker = new WorkItemId(reader.GetString(0));
                var blocked = new WorkItemId(reader.GetString(1));
                if (!map.TryGetValue(blocked, out var list))
                {
                    list = [];
                    map[blocked] = list;
                }

                list.Add(blocker);
            }

            return map;
        }

        public WorkItem? LoadItem(WorkItemId id, bool anyProject = false)
        {
            WorkItem? item;
            using (var command = Command(
                $"SELECT {WorkItemRowMapper.ReadColumns} FROM work_items WHERE id = $id" + (anyProject ? ";" : " AND project_id = $p;")))
            {
                command.Parameters.AddWithValue("$id", id.Value);
                using var reader = command.ExecuteReader();
                item = reader.Read() ? WorkItemRowMapper.Read(reader) : null;
            }

            // The lists are loaded after the row's reader is closed: they are their own queries on the same
            // connection, which a reader still in flight would refuse.
            return item is null ? null : WithAgentLayer(item);
        }

        /// <summary>The item with its agent-layer lists read in (increment C1).</summary>
        public WorkItem WithAgentLayer(WorkItem item)
        {
            var restored = WorkItem.Restore(AgentLayerRows.Load(connection, transaction, item.ToState()));
            return restored.IsValid
                ? restored.Value
                : throw new InvalidDataException(
                    $"Work item {item.Id.Value} could not be restored with its agent layer: "
                    + string.Join("; ", restored.Errors.Select(e => e.Code)));
        }

        public List<WorkItem> Children(WorkItemId id)
        {
            using var command = Command($"SELECT {WorkItemRowMapper.ReadColumns} FROM work_items WHERE parent_id = $id ORDER BY number;");
            command.Parameters.AddWithValue("$id", id.Value);
            using var reader = command.ExecuteReader();
            var children = new List<WorkItem>();
            while (reader.Read())
            {
                children.Add(WorkItemRowMapper.Read(reader));
            }

            return children;
        }

        public string Describe(WorkItemId id)
        {
            using var command = Command("SELECT number FROM work_items WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id.Value);
            return command.ExecuteScalar() is long number
                ? "#" + number.ToString(CultureInfo.InvariantCulture)
                : id.Value;
        }

        public int? NextItemNumber()
        {
            using var command = Command("SELECT next_item_number FROM projects WHERE id = $p;");
            return command.ExecuteScalar() is long next ? checked((int)next) : null;
        }

        public void ConsumeItemNumber(int number)
        {
            using var command = Command("UPDATE projects SET next_item_number = $next WHERE id = $p;");
            command.Parameters.AddWithValue("$next", number + 1);
            command.ExecuteNonQuery();
        }

        public void InsertItem(WorkItemState state)
        {
            using (var command = Command($"INSERT INTO work_items ({WorkItemRowMapper.Columns}) VALUES ({WorkItemRowMapper.Parameters});"))
            {
                command.Parameters.Clear();
                WorkItemRowMapper.Bind(command, state);
                command.ExecuteNonQuery();
            }

            AgentLayerRows.Save(connection, transaction, state);
        }

        public bool UpdateItem(WorkItemState state, long expectedVersion)
        {
            var assignments = string.Join(
                ", ",
                WorkItemRowMapper.Columns.Split(", ")
                    .Where(c => c is not ("id" or "project_id" or "number" or "created_utc"))
                    .Select(c => $"{c} = ${c}"));
            bool written;
            using (var command = Command($"UPDATE work_items SET {assignments} WHERE id = $id AND project_id = $project_id AND version = $expected;"))
            {
                command.Parameters.Clear();
                WorkItemRowMapper.Bind(command, state);
                command.Parameters.AddWithValue("$expected", expectedVersion);
                written = command.ExecuteNonQuery() == 1;
            }

            // The lists follow the row, and only when the row's version guard let the write through: a
            // conflict must leave the item exactly as the other writer left it, lists included.
            if (written)
            {
                AgentLayerRows.Save(connection, transaction, state);
            }

            return written;
        }

        public long BumpVersion(WorkItemId id, DateTimeOffset now) =>
            BumpVersionIfPresent(id, now)
            ?? throw new InvalidOperationException($"Work item {id.Value} vanished inside its own transaction.");

        /// <summary>
        /// Bumps the version and answers the new one, or null when there is no such row. Null is a real
        /// answer for exactly one caller: clearing an edge whose other end the provider deleted.
        /// </summary>
        public long? BumpVersionIfPresent(WorkItemId id, DateTimeOffset now)
        {
            using var command = Command("UPDATE work_items SET version = version + 1, updated_utc = $u WHERE id = $id RETURNING version;");
            command.Parameters.AddWithValue("$id", id.Value);
            command.Parameters.AddWithValue("$u", WorkItemRowMapper.Stamp(now));
            return command.ExecuteScalar() is long version ? version : null;
        }

        public void AppendEvent(WorkItemId item, long version, WorkItemEventOperation operation, MutationContext context, JsonObject changes, DateTimeOffset now)
        {
            using var command = Command(
                "INSERT INTO work_item_events (project_id, item_id, item_version, operation, actor_kind, actor_id, operation_id, changes_json, occurred_utc) " +
                "VALUES ($p, $item, $version, $operation, $actorKind, $actorId, $operationId, $changes, $at);");
            command.Parameters.AddWithValue("$item", item.Value);
            command.Parameters.AddWithValue("$version", version);
            command.Parameters.AddWithValue("$operation", operation.ToName());
            command.Parameters.AddWithValue("$actorKind", StableNames.ToName(context.Actor.Kind));
            command.Parameters.AddWithValue("$actorId", (object?)context.Actor.SessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$operationId", (object?)context.OperationId ?? DBNull.Value);
            command.Parameters.AddWithValue("$changes", changes.ToJsonString());
            command.Parameters.AddWithValue("$at", WorkItemRowMapper.Stamp(now));
            command.ExecuteNonQuery();
        }

        public LedgerEntry? ReadLedger(string operationId)
        {
            using var command = Command(
                "SELECT operation, item_id, related_id, result_ref FROM work_item_operations WHERE project_id = $p AND operation_id = $op;");
            command.Parameters.AddWithValue("$op", operationId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var name = reader.GetString(0);
            return new LedgerEntry(
                WorkItemEventOperations.Parse(name) ?? throw new InvalidDataException($"The operation ledger holds an unknown operation '{name}'."),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }

        public void InsertLedger(string operationId, LedgerEntry entry, DateTimeOffset now)
        {
            using var command = Command(
                "INSERT INTO work_item_operations (project_id, operation_id, operation, item_id, related_id, result_ref, recorded_utc) " +
                "VALUES ($p, $op, $operation, $item, $related, $result, $at);");
            command.Parameters.AddWithValue("$op", operationId);
            command.Parameters.AddWithValue("$operation", entry.Operation.ToName());
            command.Parameters.AddWithValue("$item", entry.ItemId ?? throw new InvalidOperationException("A ledger row names its item."));
            command.Parameters.AddWithValue("$related", (object?)entry.RelatedId ?? DBNull.Value);
            command.Parameters.AddWithValue("$result", (object?)(entry.ResultRef ?? (entry.Operation == WorkItemEventOperation.Created ? entry.ItemId : null)) ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", WorkItemRowMapper.Stamp(now));
            command.ExecuteNonQuery();
        }

        public bool EdgeExists(WorkItemLink link)
        {
            using var command = Command("SELECT 1 FROM work_item_links WHERE source_id = $s AND type = $t AND target_id = $g;");
            command.Parameters.AddWithValue("$s", link.Source.Value);
            command.Parameters.AddWithValue("$t", StableNames.ToName(link.Type));
            command.Parameters.AddWithValue("$g", link.Target.Value);
            return command.ExecuteScalar() is not null;
        }

        public void InsertEdge(WorkItemLink link, WorkItemActor actor, DateTimeOffset now)
        {
            using var command = Command(
                "INSERT INTO work_item_links (project_id, source_id, type, target_id, created_utc, created_by) VALUES ($p, $s, $t, $g, $at, $by);");
            command.Parameters.AddWithValue("$s", link.Source.Value);
            command.Parameters.AddWithValue("$t", StableNames.ToName(link.Type));
            command.Parameters.AddWithValue("$g", link.Target.Value);
            command.Parameters.AddWithValue("$at", WorkItemRowMapper.Stamp(now));
            command.Parameters.AddWithValue("$by", actor.Kind == WorkItemActorKind.Session ? "session:" + actor.SessionId : StableNames.ToName(actor.Kind));
            command.ExecuteNonQuery();
        }

        public void DeleteEdge(WorkItemLink link)
        {
            using var command = Command("DELETE FROM work_item_links WHERE source_id = $s AND type = $t AND target_id = $g;");
            command.Parameters.AddWithValue("$s", link.Source.Value);
            command.Parameters.AddWithValue("$t", StableNames.ToName(link.Type));
            command.Parameters.AddWithValue("$g", link.Target.Value);
            command.ExecuteNonQuery();
        }

        public List<WorkItemLink> EdgesTouching(WorkItemId id)
        {
            using var command = Command(
                "SELECT source_id, type, target_id FROM work_item_links WHERE project_id = $p AND (source_id = $id OR target_id = $id) " +
                "ORDER BY created_utc, source_id, type, target_id;");
            command.Parameters.AddWithValue("$id", id.Value);
            return ReadEdges(command);
        }

        /// <summary>Edges whose source is <paramref name="source"/>, of one forward type or of every type when null.</summary>
        public List<WorkItemLink> EdgesFrom(WorkItemId source, WorkItemLinkType? type)
        {
            using var command = Command(
                "SELECT source_id, type, target_id FROM work_item_links WHERE project_id = $p AND source_id = $id" +
                (type is null ? ";" : " AND type = $t;"));
            command.Parameters.AddWithValue("$id", source.Value);
            if (type is { } forward)
            {
                command.Parameters.AddWithValue("$t", StableNames.ToName(forward));
            }

            return ReadEdges(command);
        }

        /// <summary>
        /// Every edge of <paramref name="type"/> reachable from <paramref name="start"/> (plan section 4.3 refusal
        /// 2). <c>UNION</c>, not <c>UNION ALL</c>, is what makes the recursion terminate on a mirrored cycle.
        /// </summary>
        public List<WorkItemLink> ReachableEdges(WorkItemId start, WorkItemLinkType type)
        {
            using var command = Command(
                "WITH RECURSIVE reach(id) AS (" +
                "  SELECT $start " +
                "  UNION " +
                "  SELECT l.target_id FROM work_item_links l JOIN reach r ON l.source_id = r.id WHERE l.project_id = $p AND l.type = $t" +
                ") " +
                "SELECT source_id, type, target_id FROM work_item_links WHERE project_id = $p AND type = $t AND source_id IN (SELECT id FROM reach);");
            command.Parameters.AddWithValue("$start", start.Value);
            command.Parameters.AddWithValue("$t", StableNames.ToName(type));
            return ReadEdges(command);
        }

        /// <summary>
        /// Where an item goes in a status column (plan section 5.3): immediately after <paramref name="after"/>,
        /// else immediately before <paramref name="before"/>, else at the bottom. The column is read without the
        /// item itself; when the gap to split is below <see cref="RenumberGap"/>, the column is renumbered first
        /// without bumping any version or writing any event, since the visible order does not change.
        /// </summary>
        public Placement Place(WorkItemId item, WorkItemStatus column, WorkItemId? after, WorkItemId? before)
        {
            var errors = new List<ValidationError>();
            foreach (var (anchor, field) in new[] { (after, "after"), (before, "before") })
            {
                if (anchor is null)
                {
                    continue;
                }

                if (anchor == item)
                {
                    errors.Add(new ValidationError(TrackerCodes.PlacementAnchorSelf, field, "An item cannot be placed next to itself."));
                    continue;
                }

                var found = LoadItem(anchor);
                if (found is null)
                {
                    errors.Add(new ValidationError(TrackerCodes.PlacementAnchorNotFound, field, $"Item {anchor.Value} is not on this board."));
                }
                else if (found.Status != column)
                {
                    errors.Add(new ValidationError(
                        TrackerCodes.PlacementAnchorStatus,
                        field,
                        $"#{found.Number.ToString(CultureInfo.InvariantCulture)} is in {StableNames.ToName(found.Status)} now, not in {StableNames.ToName(column)}; reload the column."));
                }
            }

            if (errors.Count > 0)
            {
                return new Placement(0, errors);
            }

            var ranks = ColumnRanks(item, column);
            var anchorId = after ?? before;
            if (anchorId is null)
            {
                return new Placement(ranks.Count == 0 ? 1.0 : ranks[^1].Rank + 1.0, []);
            }

            for (var pass = 0; pass < 2; pass++)
            {
                var index = ranks.FindIndex(r => r.Id == anchorId.Value);
                double? low;
                double? high;
                if (after is not null)
                {
                    low = ranks[index].Rank;
                    high = index + 1 < ranks.Count ? ranks[index + 1].Rank : null;
                }
                else
                {
                    high = ranks[index].Rank;
                    low = index > 0 ? ranks[index - 1].Rank : null;
                }

                if (low is null)
                {
                    return new Placement(high!.Value - 1.0, []);
                }

                if (high is null)
                {
                    return new Placement(low.Value + 1.0, []);
                }

                if (high.Value - low.Value >= RenumberGap || pass == 1)
                {
                    return new Placement((low.Value + high.Value) / 2.0, []);
                }

                ranks = Renumber(ranks, column);
            }

            throw new InvalidOperationException("Placement did not settle.");
        }

        private List<(string Id, double Rank)> ColumnRanks(WorkItemId excluded, WorkItemStatus column)
        {
            using var command = Command("SELECT id, rank FROM work_items WHERE project_id = $p AND status = $s AND id <> $id ORDER BY rank, number;");
            command.Parameters.AddWithValue("$s", StableNames.ToName(column));
            command.Parameters.AddWithValue("$id", excluded.Value);
            using var reader = command.ExecuteReader();
            var ranks = new List<(string Id, double Rank)>();
            while (reader.Read())
            {
                ranks.Add((reader.GetString(0), reader.GetDouble(1)));
            }

            return ranks;
        }

        private List<(string Id, double Rank)> Renumber(List<(string Id, double Rank)> ranks, WorkItemStatus column)
        {
            using var command = Command("UPDATE work_items SET rank = $rank WHERE id = $id AND project_id = $p AND status = $s;");
            command.Parameters.AddWithValue("$s", StableNames.ToName(column));
            var rank = command.Parameters.Add("$rank", SqliteType.Real);
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var renumbered = new List<(string Id, double Rank)>(ranks.Count);
            for (var i = 0; i < ranks.Count; i++)
            {
                var value = i + 1.0;
                rank.Value = value;
                id.Value = ranks[i].Id;
                command.ExecuteNonQuery();
                renumbered.Add((ranks[i].Id, value));
            }

            return renumbered;
        }

        private static List<WorkItemLink> ReadEdges(SqliteCommand command)
        {
            using var reader = command.ExecuteReader();
            var edges = new List<WorkItemLink>();
            while (reader.Read())
            {
                var typeName = reader.GetString(1);
                var type = StableNames.Parse<WorkItemLinkType>(typeName, "type");
                if (!type.IsValid || !type.Value.IsForward())
                {
                    throw new InvalidDataException($"A stored link has the type '{typeName}', which is not a forward link type.");
                }

                // A mirrored self edge cannot be normalised; it names no second item, so it is skipped rather than read.
                var edge = WorkItemLink.Normalise(new WorkItemId(reader.GetString(0)), type.Value, new WorkItemId(reader.GetString(2)));
                if (edge.IsValid)
                {
                    edges.Add(edge.Value);
                }
            }

            return edges;
        }
    }
}
