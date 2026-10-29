using Fravashi.Application.Mirroring;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Tests.Mirroring;

/// <summary>
/// An in-memory outbox that behaves like the durable one, plus one thing the durable one must never do:
/// <see cref="DieAfterMarking"/> makes the next transition into a given state throw, which is how a test
/// puts the process death exactly where ADR 0018 decision 7 says the duplicate comes from.
/// </summary>
internal sealed class FakeOutboxStore : IOutboxStore
{
    private readonly List<OutboxEntry> _entries = [];

    /// <summary>When set, the next attempt to mark an entry into this state throws instead of recording.</summary>
    public OutboxState? DieAfterMarking { get; set; }

    /// <summary>Everything the store holds, for assertions.</summary>
    public IReadOnlyList<OutboxEntry> All => _entries;

    public OutboxEntry Entry(string id) => _entries.Single(e => e.Id == id);

    public Task<OutboxEntry> QueueAsync(OutboxEntry entry, CancellationToken ct = default)
    {
        var existing = _entries.FirstOrDefault(e => e.Id == entry.Id && e.Project == entry.Project);
        if (existing is not null)
        {
            return Task.FromResult(existing);
        }

        _entries.Add(entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<OutboxEntry>> ForItemAsync(ProjectId project, WorkItemId item, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OutboxEntry>>(
            [.. _entries.Where(e => e.Project == project && e.Item == item).OrderBy(e => e.QueuedUtc).ThenBy(e => e.Id, StringComparer.Ordinal)]);

    public Task<IReadOnlyList<OutboxEntry>> OpenAsync(ProjectId project, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OutboxEntry>>(
            [.. _entries
                .Where(e => e.Project == project && e.State is not (OutboxState.Confirmed or OutboxState.Abandoned))
                .OrderBy(e => e.QueuedUtc)
                .ThenBy(e => e.Id, StringComparer.Ordinal)]);

    public Task<int> RecoverInFlightAsync(CancellationToken ct = default)
    {
        var count = 0;
        for (var i = 0; i < _entries.Count; i++)
        {
            var recovered = OutboxDiscipline.OnRestart(_entries[i]);
            if (!ReferenceEquals(recovered, _entries[i]))
            {
                _entries[i] = recovered;
                count++;
            }
        }

        return Task.FromResult(count);
    }

    public Task<bool> MarkAsync(
        ProjectId project,
        string operationId,
        OutboxState state,
        string? resultKey,
        string? failure,
        bool countAttempt,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (DieAfterMarking == state)
        {
            DieAfterMarking = null;
            throw new ProcessDied($"Fravashi stopped before it could record '{state}'.");
        }

        var index = _entries.FindIndex(e => e.Id == operationId && e.Project == project);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        var entry = _entries[index];
        _entries[index] = entry with
        {
            State = state,
            ResultKey = resultKey ?? entry.ResultKey,
            Failure = failure,
            Attempts = countAttempt ? entry.Attempts + 1 : entry.Attempts,
            LastAttemptUtc = countAttempt ? now : entry.LastAttemptUtc,
        };

        return Task.FromResult(true);
    }
}

/// <summary>
/// A clock the test moves by hand. A real <c>TimeProvider</c> would make every recorded instant
/// unassertable, and the whole point of the mirroring sidecar is the instants it records.
/// </summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Stands in for the process ending at an inconvenient moment.</summary>
internal sealed class ProcessDied(string message) : Exception(message);

/// <summary>An in-memory mirroring sidecar. The derived counts are not modelled; the drain never reads them.</summary>
internal sealed class FakeMirrorStore : IMirrorStore
{
    private readonly Dictionary<WorkItemId, MirrorState> _states = [];
    private readonly List<FieldConflict> _conflicts = [];
    private readonly Dictionary<WorkItemId, IReadOnlyDictionary<string, string?>> _base = [];

    public IReadOnlyDictionary<WorkItemId, MirrorState> States => _states;

    public IReadOnlyList<FieldConflict> Conflicts => _conflicts;

    public Task<MirrorState?> GetAsync(ProjectId project, WorkItemId item, CancellationToken ct = default) =>
        Task.FromResult(_states.GetValueOrDefault(item));

    public Task<IReadOnlyDictionary<WorkItemId, MirrorState>> ForProjectAsync(ProjectId project, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<WorkItemId, MirrorState>>(_states);

    private readonly List<(WorkItemId Item, string Connection, string Key)> _history = [];

    public Task<int> RetireAsync(ProjectId project, string connection, DateTimeOffset now, CancellationToken ct = default)
    {
        var retired = _states.Values.Where(s => s.Connection == connection).ToList();
        foreach (var state in retired)
        {
            _history.Add((state.Item, state.Connection, state.Key));
            _states.Remove(state.Item);
        }

        return Task.FromResult(retired.Count);
    }

    public Task<IReadOnlyList<(string Connection, string Key)>> HistoryAsync(ProjectId project, WorkItemId item, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string Connection, string Key)>>(
            [.. _history.Where(h => h.Item == item).Reverse().Select(h => (h.Connection, h.Key))]);

    public Task ConfirmAsync(ProjectId project, WorkItemId item, ProviderConfirmation confirmation, DateTimeOffset now, CancellationToken ct = default)
    {
        _states[item] = new MirrorState(
            item,
            confirmation.Connection,
            confirmation.Key,
            now,
            confirmation.ProviderVersion,
            confirmation.LocalVersion,
            0,
            0,
            0,
            null,
            null);

        if (confirmation.BaseFields is not null)
        {
            _base[item] = confirmation.BaseFields;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string?>> BaseFieldsAsync(ProjectId project, WorkItemId item, CancellationToken ct = default) =>
        Task.FromResult(_base.GetValueOrDefault(item, new Dictionary<string, string?>(StringComparer.Ordinal)));

    public Task TombstoneAsync(ProjectId project, WorkItemId item, string reason, DateTimeOffset now, CancellationToken ct = default)
    {
        var existing = _states.GetValueOrDefault(item)
            ?? new MirrorState(item, "unknown", "unknown", null, null, 0, 0, 0, 0, null, null);

        _states[item] = existing with { TombstonedUtc = now, TombstoneReason = reason };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FieldConflict>> OpenConflictsAsync(ProjectId project, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FieldConflict>>([.. _conflicts.Where(c => c.IsOpen)]);

    public Task<FieldConflict> RecordConflictAsync(ProjectId project, FieldConflict conflict, CancellationToken ct = default)
    {
        var index = _conflicts.FindIndex(c => c.IsOpen && c.Item == conflict.Item && c.Field == conflict.Field);
        if (index >= 0)
        {
            _conflicts[index] = _conflicts[index] with
            {
                LocalValue = conflict.LocalValue,
                ProviderValue = conflict.ProviderValue,
            };
            return Task.FromResult(_conflicts[index]);
        }

        _conflicts.Add(conflict);
        return Task.FromResult(conflict);
    }

    public Task<bool> ResolveConflictAsync(ProjectId project, string conflictId, ConflictChoice choice, DateTimeOffset now, CancellationToken ct = default)
    {
        var index = _conflicts.FindIndex(c => c.Id == conflictId && c.IsOpen);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        _conflicts[index] = _conflicts[index] with { Choice = choice, ResolvedUtc = now };
        return Task.FromResult(true);
    }
}

/// <summary>
/// A provider that records every call and answers whatever the test tells it to. It also models the one
/// behaviour a real tracker has that makes recovery possible: a created item KEEPS the operation id it was
/// stamped with, so it can be found again.
/// </summary>
internal sealed class FakeProviderGateway(ProviderCapabilities? capabilities = null) : IProviderGateway
{
    private readonly Dictionary<string, ProviderItem> _byOperationId = [];

    public string Connection => "jira";

    public ProviderCapabilities Capabilities { get; set; } = capabilities ?? new ProviderCapabilities(true, true, true);

    /// <summary>Every call made, as "verb:argument", in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>What the next write answers. Null means an ordinary success.</summary>
    public ProviderResult<ProviderItem>? NextWriteResult { get; set; }

    /// <summary>What a lookup by operation id answers. Null means "look in what was actually created".</summary>
    public ProviderResult<ProviderItem?>? LookupResult { get; set; }

    public int NextKey { get; set; } = 1;

    public Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default)
    {
        Calls.Add("create:" + operationId);
        return Task.FromResult(Write(operationId));
    }

    public Task<ProviderResult<ProviderItem>> UpdateAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default)
    {
        Calls.Add("update:" + key);
        return Task.FromResult(Write(operationId, key));
    }

    public Task<ProviderResult<ProviderItem>> TransitionAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default)
    {
        Calls.Add("transition:" + key);
        return Task.FromResult(Write(operationId, key));
    }

    public Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default)
    {
        Calls.Add("find:" + operationId);
        if (LookupResult is { } forced)
        {
            return Task.FromResult(forced);
        }

        return Task.FromResult(ProviderResult<ProviderItem?>.Ok(_byOperationId.GetValueOrDefault(operationId)));
    }

    public Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default)
    {
        Calls.Add("get:" + key);
        var item = _byOperationId.Values.FirstOrDefault(i => i.Key == key);
        return Task.FromResult(item is null
            ? ProviderResult<ProviderItem>.Gone($"{key} is not there.")
            : ProviderResult<ProviderItem>.Ok(item));
    }

    private ProviderResult<ProviderItem> Write(string operationId, string? key = null)
    {
        if (NextWriteResult is { } forced)
        {
            NextWriteResult = null;

            // An indeterminate answer means the call LEFT. A real provider may well have acted on it, and
            // that is exactly the case recovery has to survive, so the item is recorded here regardless.
            if (forced.Outcome == ProviderOutcome.Indeterminate)
            {
                Record(operationId, key);
            }

            return forced;
        }

        return ProviderResult<ProviderItem>.Ok(Record(operationId, key));
    }

    private ProviderItem Record(string operationId, string? key)
    {
        var item = new ProviderItem(
            key ?? "PROJ-" + NextKey++.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "etag-" + operationId,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            "To Do");

        _byOperationId[operationId] = item;
        return item;
    }
}
