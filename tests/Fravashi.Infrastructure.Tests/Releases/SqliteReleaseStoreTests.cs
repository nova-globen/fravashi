using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Releases;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Releases;
using Fravashi.Infrastructure.Sessions.Tools;
using Fravashi.Infrastructure.Tests.WorkItems;
using Fravashi.Infrastructure.Wiki;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Releases;

/// <summary>
/// Work item #368, over the real local board and the real database: saving a release stamps "Released in" on exactly
/// the chosen Done items and archives them off the board, records it in each item's history, leaves an unticked item
/// alone, refuses anything it cannot honour without changing a thing, and keeps every archived item findable.
/// </summary>
public sealed class SqliteReleaseStoreTests : IDisposable
{
    private readonly LocalBoardFixture _board = new();
    private readonly SqliteReleaseStores _stores;
    private readonly SqliteWikiLibraries _wikis;
    private readonly WorkItemChanges _changes = new();

    public SqliteReleaseStoreTests()
    {
        _stores = new SqliteReleaseStores(_board.Factory, _board.Clock);
        _wikis = new SqliteWikiLibraries(_board.Factory, _board.Clock);
    }

    public void Dispose() => _board.Dispose();

    private IReleaseStore Store => _stores.For(ProjectId.Default);

    private Task<WorkItem> DoneAsync(string title, WorkItemLevel level = WorkItemLevel.Task) =>
        _board.CreateAsync(new NewWorkItem
        {
            Level = level,
            Title = title,
            Description = "The problem and the outcome.",
            Status = WorkItemStatus.Done,
            Resolution = WorkItemResolution.Completed,
        });

    private static NewRelease Release(string version, params WorkItem[] items) => new()
    {
        Version = version,
        Tag = version,
        Commit = "0123456789abcdef0123456789abcdef01234567",
        Notes = "# Release " + version,
        Items = [.. items.Select(i => i.Id)],
    };

    private async Task<IReadOnlyList<WorkItem>> QueryAsync(WorkItemQuery query) =>
        (await _board.Board.QueryAsync(query with { Limit = WorkItemQuery.MaxLimit })).Items;

    [Fact]
    public async Task Saving_stamps_the_chosen_items_archives_them_and_leaves_the_unticked_one_on_the_board()
    {
        var shipped = await DoneAsync("Shipped one");
        var also = await DoneAsync("Shipped two");
        var waiting = await DoneAsync("Waits for the next release");
        var open = await _board.TaskAsync("Still open", WorkItemStatus.Ready);

        var saved = await Store.CreateAsync(Release("v0.9.97", shipped, also));

        saved.Saved.Should().BeTrue();
        saved.Value!.ItemNumbers.Should().Equal(shipped.Number, also.Number);

        var stamped = await _board.ReloadAsync(shipped);
        stamped.ReleasedIn.Should().Be("v0.9.97");
        stamped.Version.Should().Be(shipped.Version + 1);
        (await _board.ReloadAsync(waiting)).ReleasedIn.Should().BeNull();

        var board = await QueryAsync(new WorkItemQuery { Release = ReleaseFilter.NotArchived });
        board.Select(i => i.Number).Should().BeEquivalentTo([waiting.Number, open.Number]);

        var history = await _board.Board.HistoryAsync(shipped.Id);
        history[^1].Operation.Should().Be(WorkItemEventOperation.Released);
        history[^1].ItemVersion.Should().Be(stamped.Version);
    }

    [Fact]
    public async Task An_archived_item_is_still_found_by_number_by_search_and_by_its_release()
    {
        var shipped = await DoneAsync("The archive filter");
        await DoneAsync("Another done item");
        await Store.CreateAsync(Release("v0.9.97", shipped));

        (await _board.Board.GetByNumberAsync(shipped.Number))!.ReleasedIn.Should().Be("v0.9.97");
        (await QueryAsync(new WorkItemQuery { Text = "archive filter" })).Should().ContainSingle(i => i.Id == shipped.Id);
        (await QueryAsync(new WorkItemQuery { ReleasedIn = "v0.9.97" })).Should().ContainSingle(i => i.Id == shipped.Id);
        (await QueryAsync(new WorkItemQuery { Release = ReleaseFilter.Unreleased })).Should().NotContain(i => i.Id == shipped.Id);
        (await QueryAsync(new WorkItemQuery { Release = ReleaseFilter.Released })).Should().ContainSingle(i => i.Id == shipped.Id);

        var read = await Store.GetAsync("v0.9.97");
        read!.ItemNumbers.Should().Equal(shipped.Number);
        (await Store.ListAsync()).Should().ContainSingle(r => r.Version == "v0.9.97" && r.Tag == "v0.9.97");
    }

    [Fact]
    public async Task An_ordinary_update_keeps_the_stamp_and_a_reopened_item_comes_back_to_the_board()
    {
        var shipped = await DoneAsync("Reopen me");
        await Store.CreateAsync(Release("v1.0.0", shipped));
        var current = await _board.ReloadAsync(shipped);

        var edited = await _board.Board.UpdateAsync(
            current.Id, new WorkItemPatch { Title = "Reopen me, renamed" }, current.Version, MutationContext.User);
        edited.Outcome.Should().Be(TrackerOutcome.Ok);
        edited.Value.ReleasedIn.Should().Be("v1.0.0");

        var reopened = await _board.Board.TransitionAsync(
            current.Id, WorkItemStatus.InProgress, null, null, null, edited.Value.Version, MutationContext.User);
        reopened.Outcome.Should().Be(TrackerOutcome.Ok);

        (await QueryAsync(new WorkItemQuery { Release = ReleaseFilter.NotArchived }))
            .Should().ContainSingle(i => i.Id == shipped.Id && i.ReleasedIn == "v1.0.0");
    }

    [Fact]
    public async Task A_refusal_changes_nothing()
    {
        var shipped = await DoneAsync("Shipped");
        var notDone = await _board.TaskAsync("Not done", WorkItemStatus.InReview);
        (await Store.CreateAsync(Release("v1.0.0", shipped))).Saved.Should().BeTrue();
        var another = await DoneAsync("Another");

        (await Store.CreateAsync(Release("v1.0.0", another))).Code.Should().Be(ReleaseCodes.VersionExists);
        (await Store.CreateAsync(Release("v1.0.1", another, shipped))).Code.Should().Be(ReleaseCodes.AlreadyReleased);
        (await Store.CreateAsync(Release("v1.0.1", another, notDone))).Code.Should().Be(ReleaseCodes.NotDone);
        (await Store.CreateAsync(Release("1.0", another))).Code.Should().Be(ReleaseCodes.InvalidVersion);
        (await Store.CreateAsync(Release("v1.0.1"))).Code.Should().Be(ReleaseCodes.NoItems);

        (await _board.ReloadAsync(another)).ReleasedIn.Should().BeNull();
        (await _board.ReloadAsync(another)).Version.Should().Be(another.Version);
        _board.Count("SELECT COUNT(*) FROM releases;").Should().Be(1);
        _board.Count("SELECT COUNT(*) FROM release_items;").Should().Be(1);
    }

    [Fact]
    public async Task A_release_belongs_to_its_project()
    {
        var shipped = await DoneAsync("Shipped");
        await Store.CreateAsync(Release("v1.0.0", shipped));

        (await _stores.For(new ProjectId("other")).ListAsync()).Should().BeEmpty();
        (await _stores.For(new ProjectId("other")).CreateAsync(Release("v1.0.0", shipped))).Code.Should().Be(ReleaseCodes.UnknownItem);
    }

    [Fact]
    public async Task The_recorder_writes_the_notes_as_a_release_document_linked_to_every_item()
    {
        var feature = await DoneAsync("A release option on the board", WorkItemLevel.Feature);
        var fix = await DoneAsync("fix: the toggle");
        var recorder = new ReleaseRecorder(_stores, _board.Resolver, _wikis, _changes);
        var announced = 0;
        _changes.Changed += _ => announced++;

        (await recorder.CandidatesAsync(ProjectId.Default)).Select(i => i.Number).Should().Equal(feature.Number, fix.Number);

        var saved = await recorder.SaveAsync(ProjectId.Default, Release("v0.9.97", feature, fix) with
        {
            Notes = ReleaseNotes.Draft("v0.9.97", [feature, fix]),
        });

        saved.WikiProblem.Should().BeNull();
        var document = await _wikis.For(ProjectId.Default).GetByNumberAsync(saved.WikiNumber!.Value);
        document!.Type.Should().Be(WikiItemType.Release);
        document.Title.Should().Be("Release v0.9.97");
        document.Body.Should().Contain("## Features").And.Contain("(#" + fix.Number + ")");
        document.Links.Where(l => l.Kind == WikiLinkKind.WorkItem).Select(l => l.Number)
            .Should().BeEquivalentTo(new int?[] { feature.Number, fix.Number });
        (await Store.GetAsync("v0.9.97"))!.WikiNumber.Should().Be(saved.WikiNumber);
        (await recorder.CandidatesAsync(ProjectId.Default)).Should().BeEmpty();
        announced.Should().Be(1);
    }

    [Fact]
    public async Task The_suggestion_is_the_next_patch_of_the_latest_tag_or_release()
    {
        var shipped = await DoneAsync("Shipped");
        await Store.CreateAsync(Release("v0.9.98", shipped));
        var known = await Store.ListAsync();

        ReleaseRecorder.Suggest(["v0.9.97", "nightly"], known).Text.Should().Be("v0.9.99");
        ReleaseRecorder.Suggest([], []).Text.Should().Be(ReleaseRecorder.FirstVersion);
    }

    [Fact]
    public async Task The_agent_tool_lists_the_releases_and_reads_one_whole()
    {
        var feature = await DoneAsync("A release option on the board", WorkItemLevel.Feature);
        await Store.CreateAsync(Release("v0.9.97", feature) with { Notes = "What shipped." });
        var tool = new WorkItemReleasesTool(_board.Resolver, _changes, _stores);

        var list = await tool.InvokeAsync(Call("{}"));
        list.IsError.Should().BeFalse();
        list.Text.Should().Contain("\"version\":\"v0.9.97\"").And.Contain("other-authored-content");

        var one = await tool.InvokeAsync(Call("""{"version":"v0.9.97"}"""));
        one.IsError.Should().BeFalse();
        one.Text.Should().Contain("What shipped.").And.Contain("A release option on the board").And.Contain("\"group\":\"feature\"");

        var missing = await tool.InvokeAsync(Call("""{"version":"v9.9.9"}"""));
        missing.IsError.Should().BeTrue();
        missing.Text.Should().Contain("release_not_found");
        tool.Access.Should().Be(StudioToolAccess.ReadOnly);
    }

    private static StudioToolCall Call(string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new StudioToolCall(
            AgentSessionId.New(), WorkItemReleasesTool.ToolName, document.RootElement.Clone(), null, ProjectId.Default);
    }
}
