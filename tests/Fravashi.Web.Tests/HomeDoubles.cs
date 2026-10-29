using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Web.Tests;

/// <summary>Work item #350: an install with the projects a test names, for Home's project filters.</summary>
internal sealed class NamedProjectStore(params (string Id, string Name)[] projects) : IProjectStore
{
    private readonly List<ProjectRecord> _projects =
        [.. projects.Select(p => new ProjectRecord(new ProjectId(p.Id), p.Name, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))];

    public IReadOnlyList<ProjectRecord> List() => _projects;

    public ProjectRecord? Get(ProjectId id) => _projects.FirstOrDefault(p => p.Id == id);

    public ProjectRecord Create(string name) => throw new NotSupportedException();

    public bool Rename(ProjectId id, string name) => false;

    public bool BindWorkspace(string workspaceId, ProjectId project) => false;

    public IReadOnlyList<WorkspaceRecord> WorkspacesOf(ProjectId project) => [];
}

/// <summary>
/// Work item #350: each project's board as a seeded list. Only the paged query the Progress card makes is answered,
/// and it is answered in pages of two with a cursor, so a card that read only the first page would count too few.
/// </summary>
internal sealed class SeededTrackers(IReadOnlyDictionary<ProjectId, IReadOnlyList<WorkItem>> boards) : ITrackerResolver
{
    public ITracker For(ProjectId project) =>
        new Board(project, boards.TryGetValue(project, out var items) ? items : []);

    /// <summary>An item in <paramref name="project"/> with the given status (and the Blocked flag when asked).</summary>
    public static WorkItem Item(ProjectId project, int number, WorkItemStatus status, bool blocked = false) =>
        WorkItem.Restore(new WorkItemState
        {
            Id = new WorkItemId($"{project.Value}-{number}"),
            Project = project,
            Number = number,
            Level = WorkItemLevel.Task,
            Title = $"Item {number}",
            Status = status,
            Resolution = status switch
            {
                WorkItemStatus.Done => WorkItemResolution.Completed,
                WorkItemStatus.Cancelled => WorkItemResolution.WontDo,
                _ => null,
            },
            Flags = blocked ? new WorkItemFlags(true, "waiting on a decision", false, null) : WorkItemFlags.None,
        }).Value;

    private sealed class Board(ProjectId project, IReadOnlyList<WorkItem> items) : ITracker
    {
        private const int PageSize = 2;

        public ProjectId Project => project;

        public string ProviderName => "local";

        public TrackerCapabilities Capabilities => TrackerCapabilities.ReadOnly;

        public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken ct = default)
        {
            var start = query.Cursor is { } cursor ? int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var page = items.Skip(start).Take(PageSize).ToList();
            var next = start + PageSize < items.Count
                ? (start + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
            return Task.FromResult(new WorkItemPage(page, next));
        }

        public Task<TrackerResult<WorkItem>> CreateAsync(NewWorkItem item, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkItem?> GetByNumberAsync(int number, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemEvent>> HistoryAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemLink>> LinksAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<WorkItem>> UpdateAsync(WorkItemId id, WorkItemPatch patch, long expectedVersion, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<WorkItem>> TransitionAsync(WorkItemId id, WorkItemStatus to, WorkItemResolution? resolution, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<WorkItem>> MoveAsync(WorkItemId id, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<WorkItemLink>> LinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<bool>> UnlinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<EvidenceRecord>> RecordEvidenceAsync(WorkItemId id, NewEvidence evidence, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<EvidenceRecord>> EvidenceAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StartabilityReport> WhatCanStartNowAsync(StartableQuery query, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
