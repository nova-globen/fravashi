using System.Globalization;
using AwesomeAssertions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Xunit;

namespace Fravashi.Infrastructure.Tests.WorkItems;

/// <summary>
/// Plan section 3.3: a provider adapter fits <see cref="ITracker"/> without changing it. One shared set of read and
/// refusal assertions runs against the local board and against a test-only, provider-shaped, read-only tracker
/// (the way the archived Jira adapter could be written: reads, no create, capabilities false, Unsupported for every
/// mutation). Both serve the MIRRORED shapes the authoring rules would refuse, so the assertions prove the model can
/// hold provider data, not only that the port can refuse on a provider's behalf.
/// </summary>
public abstract class TrackerPortFitnessTests
{
    /// <summary>
    /// The mirrored shapes: an ADO Removed item as Cancelled with no resolution, a Blocked flag with no reason, a
    /// Jira Duplicate with no link, a Task directly under a Feature, and an item with no description.
    /// </summary>
    internal static readonly MirroredShape[] Shapes =
    [
        new(1, WorkItemLevel.Task, WorkItemStatus.Cancelled, null, false, "Removed in ADO.", null),
        new(2, WorkItemLevel.Task, WorkItemStatus.Triage, null, true, "d", null),
        new(3, WorkItemLevel.Task, WorkItemStatus.Cancelled, WorkItemResolution.Duplicate, false, "d", null),
        new(4, WorkItemLevel.Feature, WorkItemStatus.Triage, null, false, "d", null),
        new(5, WorkItemLevel.Task, WorkItemStatus.Backlog, null, false, "d", 4),
        new(6, WorkItemLevel.Task, WorkItemStatus.Ready, null, false, string.Empty, null),
    ];

    protected abstract ITracker Tracker { get; }

    internal static WorkItemId IdOf(int number) => new("raw-" + number.ToString(CultureInfo.InvariantCulture) + "-default");

    [Fact]
    public async Task Every_mirrored_shape_reads_back_as_the_provider_said()
    {
        foreach (var shape in Shapes)
        {
            var item = await Tracker.GetAsync(IdOf(shape.Number));
            item.Should().NotBeNull($"shape #{shape.Number} is served");
            item!.Number.Should().Be(shape.Number);
            item.Project.Should().Be(ProjectId.Default);
            item.Level.Should().Be(shape.Level);
            item.Status.Should().Be(shape.Status);
            item.Resolution.Should().Be(shape.Resolution);
            item.Flags.Blocked.Should().Be(shape.Blocked);
            item.Flags.BlockedReason.Should().BeNull();
            item.Description.Should().Be(shape.Description);
            if (shape.ParentNumber is { } parent)
            {
                item.Parent.Should().Be(IdOf(parent));
            }
            else
            {
                item.Parent.Should().BeNull();
            }

            (await Tracker.GetByNumberAsync(shape.Number)).Should().Be(item);
        }
    }

    [Fact]
    public async Task Query_serves_the_mirrored_shapes_in_board_order()
    {
        var all = await Tracker.QueryAsync(new WorkItemQuery());
        var cancelled = await Tracker.QueryAsync(new WorkItemQuery { Statuses = [WorkItemStatus.Cancelled] });

        all.Items.Select(i => i.Number).Should().Equal(2, 4, 5, 6, 1, 3);
        all.NextCursor.Should().BeNull();
        cancelled.Items.Select(i => i.Number).Should().Equal(1, 3);
    }

    [Fact]
    public async Task A_refused_mutation_is_a_result_never_an_exception_and_changes_nothing()
    {
        var before = new List<WorkItem?>();
        foreach (var shape in Shapes)
        {
            before.Add(await Tracker.GetAsync(IdOf(shape.Number)));
        }

        var context = new MutationContext(WorkItemActor.Session("fitness"));
        var create = await Tracker.CreateAsync(new NewWorkItem { Level = WorkItemLevel.Task, Title = "t", Description = " " }, context);
        var cancel = await Tracker.TransitionAsync(IdOf(6), WorkItemStatus.Cancelled, null, null, null, 1, context);
        var flag = await Tracker.UpdateAsync(IdOf(1), new WorkItemPatch { Flags = WorkItemFlags.None.WithDeferred("later") }, 1, context);
        var move = await Tracker.MoveAsync(IdOf(2), null, null, 1, context);
        var link = await Tracker.LinkAsync(IdOf(5), WorkItemLinkType.Blocks, IdOf(5), context);
        var unlink = await Tracker.UnlinkAsync(IdOf(3), WorkItemLinkType.Duplicates, IdOf(3), context);

        Refused(create.Outcome, create.Errors, Tracker.Capabilities.Create);
        Refused(cancel.Outcome, cancel.Errors, Tracker.Capabilities.Transition);
        Refused(flag.Outcome, flag.Errors, Tracker.Capabilities.Update);
        Refused(move.Outcome, move.Errors, Tracker.Capabilities.Move);
        Refused(link.Outcome, link.Errors, Tracker.Capabilities.Link);
        Refused(unlink.Outcome, unlink.Errors, Tracker.Capabilities.Link);

        for (var i = 0; i < Shapes.Length; i++)
        {
            (await Tracker.GetAsync(IdOf(Shapes[i].Number))).Should().Be(before[i]);
        }
    }

    private static void Refused(TrackerOutcome outcome, IReadOnlyList<ValidationError> errors, bool capable)
    {
        if (capable)
        {
            outcome.Should().Be(TrackerOutcome.Invalid, "a capable tracker refuses an authoring break with a reason");
            errors.Should().NotBeEmpty();
            errors.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.Code) && !string.IsNullOrWhiteSpace(e.Field) && !string.IsNullOrWhiteSpace(e.Message));
        }
        else
        {
            outcome.Should().Be(TrackerOutcome.Unsupported, "an operation a tracker reports it cannot do is Unsupported, never thrown");
        }
    }
}

/// <summary>One mirrored shape, served identically by both trackers.</summary>
internal sealed record MirroredShape(int Number, WorkItemLevel Level, WorkItemStatus Status, WorkItemResolution? Resolution, bool Blocked, string Description, int? ParentNumber);

/// <summary>The shared assertions against the local board, its mirrored shapes written straight into its tables.</summary>
public sealed class LocalBoardPortFitnessTests : TrackerPortFitnessTests, IDisposable
{
    private readonly LocalBoardFixture _fixture = new();

    public LocalBoardPortFitnessTests()
    {
        foreach (var shape in Shapes)
        {
            _fixture.SeedRaw(
                shape.Number,
                level: StableNames.ToName(shape.Level),
                title: "Mirrored " + shape.Number.ToString(CultureInfo.InvariantCulture),
                description: shape.Description,
                status: StableNames.ToName(shape.Status),
                resolution: shape.Resolution is { } r ? StableNames.ToName(r) : null,
                blocked: shape.Blocked,
                parentId: shape.ParentNumber is { } parent ? IdOf(parent).Value : null);
        }
    }

    protected override ITracker Tracker => _fixture.Board;

    [Fact]
    public void The_local_board_reports_what_it_implements()
    {
        Tracker.ProviderName.Should().Be("local");
        Tracker.Capabilities.Should().Be(new TrackerCapabilities(Create: true, Update: true, Transition: true, Move: true, Link: true, RecordEvidence: false));
    }

    public void Dispose() => _fixture.Dispose();
}

/// <summary>The shared assertions against a provider-shaped, read-only tracker.</summary>
public sealed class ReadOnlyProviderPortFitnessTests : TrackerPortFitnessTests
{
    protected override ITracker Tracker { get; } = new ReadOnlyProviderTracker(
        Shapes.Select(shape => WorkItem.Restore(new WorkItemState
        {
            Id = IdOf(shape.Number),
            Project = ProjectId.Default,
            Number = shape.Number,
            Level = shape.Level,
            Title = "Mirrored " + shape.Number.ToString(CultureInfo.InvariantCulture),
            Description = shape.Description,
            Parent = shape.ParentNumber is { } parent ? IdOf(parent) : null,
            Status = shape.Status,
            Resolution = shape.Resolution,
            Flags = new WorkItemFlags(shape.Blocked, null, false, null),
            Rank = 1.0,
            External = new ExternalReference("jira", "PROJ-" + shape.Number.ToString(CultureInfo.InvariantCulture), "Native"),
        }).Value).ToList());

    [Fact]
    public void The_provider_reports_no_capability()
    {
        Tracker.Capabilities.Should().Be(TrackerCapabilities.ReadOnly);
    }

    /// <summary>
    /// A provider-shaped tracker written against the port as it stands: it can read what the provider serves and
    /// can do nothing else, and says so with Unsupported rather than an exception.
    /// </summary>
    private sealed class ReadOnlyProviderTracker(IReadOnlyList<WorkItem> items) : ITracker
    {
        private const string Reason = "The provider connection is read-only.";

        public ProjectId Project => ProjectId.Default;

        public string ProviderName => "jira-shaped";

        public TrackerCapabilities Capabilities => TrackerCapabilities.ReadOnly;

        public Task<TrackerResult<WorkItem>> CreateAsync(NewWorkItem item, MutationContext context, CancellationToken ct = default) =>
            Task.FromResult(TrackerResult<WorkItem>.Unsupported(Reason));

        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult(items.FirstOrDefault(i => i.Id == id));

        public Task<WorkItem?> GetByNumberAsync(int number, CancellationToken ct = default) =>
            Task.FromResult(items.FirstOrDefault(i => i.Number == number));

        public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken ct = default)
        {
            var matched = items
                .Where(i => query.Statuses is not { Count: > 0 } || query.Statuses.Contains(i.Status))
                .OrderBy(i => i.Status)
                .ThenBy(i => i.Rank)
                .ThenBy(i => i.Number)
                .ToList();
            return Task.FromResult(new WorkItemPage(matched, null));
        }

        public Task<IReadOnlyList<WorkItemEvent>> HistoryAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemEvent>>([]);

        public Task<IReadOnlyList<WorkItemLink>> LinksAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemLink>>([]);

        public Task<TrackerResult<WorkItem>> UpdateAsync(WorkItemId id, WorkItemPatch patch, long expectedVersion, MutationContext context, CancellationToken ct = default) =>
            Task.FromResult(TrackerResult<WorkItem>.Unsupported(Reason));

        public Task<TrackerResult<WorkItem>> TransitionAsync(WorkItemId id, WorkItemStatus to, WorkItemResolution? resolution, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default) =>
            Task.FromResult(TrackerResult<WorkItem>.Unsupported(Reason));

        public Task<TrackerResult<WorkItem>> MoveAsync(WorkItemId id, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default) =>
            Task.FromResult(TrackerResult<WorkItem>.Unsupported(Reason));

        public Task<TrackerResult<WorkItemLink>> LinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default) =>
            Task.FromResult(TrackerResult<WorkItemLink>.Unsupported(Reason));

        public Task<TrackerResult<bool>> UnlinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default) =>
            Task.FromResult(TrackerResult<bool>.Unsupported(Reason));

        public Task<TrackerResult<EvidenceRecord>> RecordEvidenceAsync(WorkItemId id, NewEvidence evidence, MutationContext context, CancellationToken ct = default) =>
            Task.FromResult(TrackerResult<EvidenceRecord>.Unsupported(Reason));

        public Task<IReadOnlyList<EvidenceRecord>> EvidenceAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EvidenceRecord>>([]);

        /// <summary>
        /// Startability is worked out from what a provider already gives (status, flags, the blocked-by edges
        /// and the item's own person blockers), so a read-only provider answers it rather than refusing: that
        /// is the point of the domain evaluation being pure.
        /// </summary>
        public Task<StartabilityReport> WhatCanStartNowAsync(StartableQuery query, CancellationToken ct = default)
        {
            var startable = new List<StartableItem>();
            var held = new List<StartableItem>();
            foreach (var item in items)
            {
                var verdict = Startability.Evaluate(item, []);
                (verdict.CanStart ? startable : held).Add(new StartableItem(item, verdict));
            }

            return Task.FromResult(new StartabilityReport(
                [.. startable.OrderBy(e => e.Item.Rank).ThenBy(e => e.Item.Number).Take(query.Limit)],
                [.. held.OrderBy(e => e.Item.Rank).ThenBy(e => e.Item.Number)]));
        }
    }
}
