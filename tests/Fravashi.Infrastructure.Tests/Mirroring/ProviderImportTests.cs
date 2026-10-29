using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Onboarding;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Mirroring;
using Fravashi.Infrastructure.Tests.WorkItems;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Mirroring;

/// <summary>
/// Backlog 47's other half: "items created in Fravashi appear in the provider AND VICE VERSA". Sending was
/// built first because the outbox already knew how; bringing items the other way needs the provider to be
/// ASKED what it has, which no part of the port could do until now.
/// </summary>
public sealed class ProviderImportTests : IDisposable
{
    private readonly LocalBoardFixture _board = new();
    private readonly SqliteMirrorStore _mirror;
    private readonly SqliteOutboxStore _outbox;
    private readonly ListingProvider _provider = new();
    private readonly Gateways _gateways;

    public ProviderImportTests()
    {
        _mirror = new SqliteMirrorStore(_board.Factory);
        _outbox = new SqliteOutboxStore(_board.Factory);
        _gateways = new Gateways(_provider);
    }

    public void Dispose() => _board.Dispose();

    private static readonly BoardStatusMapping Mapping = new("jira",
    [
        new StatusMappingEntry("Backlog", "To Do"),
        new StatusMappingEntry("In Progress", "Doing"),
    ]);

    private ProviderSync Sync() => new(
        _gateways,
        new LocalTrackerResolver(_board.Factory, _board.Clock),
        _outbox,
        _mirror,
        new OutboxDrain(_outbox, _mirror, _board.Clock),
        new ReconcilePass(_mirror, _outbox, _board.Clock, null, () => ProviderStatusMap.From(Mapping)),
        _board.Clock,
        statuses: () => ProviderStatusMap.From(Mapping));

    /// <summary>A <see cref="ProviderSync"/> over a gateway of the test's own choosing, rather than <see cref="_provider"/>.</summary>
    private ProviderSync SyncOver(IProviderGateway gateway) => new(
        new Gateways(gateway),
        new LocalTrackerResolver(_board.Factory, _board.Clock),
        _outbox,
        _mirror,
        new OutboxDrain(_outbox, _mirror, _board.Clock),
        new ReconcilePass(_mirror, _outbox, _board.Clock, null, () => ProviderStatusMap.From(Mapping)),
        _board.Clock,
        statuses: () => ProviderStatusMap.From(Mapping));

    private static Dictionary<string, string?> Fields(string title, string? assignee = null) => new(StringComparer.Ordinal)
    {
        [nameof(WorkItem.Title)] = title,
        [nameof(WorkItem.Description)] = "As the provider has it.",
        [nameof(WorkItem.Assignee)] = assignee,
    };

    [Fact]
    public async Task An_item_that_exists_only_on_the_provider_arrives_here_with_its_key_and_its_status()
    {
        _provider.Put("DD-7", Fields("Written on the provider"), "Doing", "indeterminate");

        var imported = await Sync().ImportAsync(ProjectId.Default);

        imported.Happened.Should().BeTrue(imported.Sentence);
        imported.Sentence.Should().Contain("1");
        var page = await _board.Board.QueryAsync(new WorkItemQuery());
        var item = page.Items.Should().ContainSingle().Subject;
        item.Title.Should().Be("Written on the provider");
        item.Status.Should().Be(WorkItemStatus.InProgress, "the provider's status is read through this board's mapping");
        (await _mirror.GetAsync(ProjectId.Default, item.Id))!.Key.Should().Be("DD-7");
    }

    [Fact]
    public async Task Importing_twice_brings_nothing_twice()
    {
        _provider.Put("DD-7", Fields("Written on the provider"), "To Do", "new");
        await Sync().ImportAsync(ProjectId.Default);

        var again = await Sync().ImportAsync(ProjectId.Default);

        again.Sentence.Should().Contain("Nothing new");
        (await _board.Board.QueryAsync(new WorkItemQuery())).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task A_provider_holding_more_than_one_page_is_brought_in_whole_and_a_second_import_then_truly_has_nothing_new()
    {
        // backlog 182: a provider larger than one page used to leave everything past the first page out of
        // reach for ever, and a second press falsely said "Nothing new" about what it never asked for.
        _provider.PageSize = 100;
        for (var i = 1; i <= 250; i++)
        {
            _provider.Put($"DD-{i}", Fields($"Item {i}"), "To Do", "new");
        }

        var imported = await Sync().ImportAsync(ProjectId.Default);

        imported.Happened.Should().BeTrue(imported.Sentence);
        imported.Sentence.Should().Contain("250");
        (await _board.Board.QueryAsync(new WorkItemQuery { Limit = WorkItemQuery.MaxLimit })).Items.Should().HaveCount(250);

        var again = await Sync().ImportAsync(ProjectId.Default);

        again.Sentence.Should().Contain("Nothing new", "every page was actually asked for this time, so this is now true");
        (await _board.Board.QueryAsync(new WorkItemQuery { Limit = WorkItemQuery.MaxLimit })).Items.Should().HaveCount(250);
    }

    [Fact]
    public async Task Importing_stops_rather_than_hanging_when_the_provider_keeps_repeating_the_same_page()
    {
        // The loop trusts the provider to advance the cursor; a provider that hands the same one back forever
        // must not be trusted to do so, or an import never returns.
        var imported = await SyncOver(new LoopingProvider(LoopingProvider.Mode.RepeatsCursor)).ImportAsync(ProjectId.Default);

        imported.Sentence.Should().Contain("kept answering with the same page");
        imported.Sentence.Should().Contain("stopped after");
        (await _board.Board.QueryAsync(new WorkItemQuery())).Items.Should().NotBeEmpty("what already arrived is kept, not discarded");
    }

    [Fact]
    public async Task Importing_stops_rather_than_hanging_when_the_provider_keeps_answering_with_empty_pages()
    {
        var imported = await SyncOver(new LoopingProvider(LoopingProvider.Mode.EmptyPages)).ImportAsync(ProjectId.Default);

        imported.Sentence.Should().Contain("kept answering with an empty page");
        imported.Sentence.Should().Contain("stopped after 0 item(s)");
    }

    [Fact]
    public async Task An_item_this_board_sent_is_not_imported_back_as_a_second_copy()
    {
        var mine = await _board.CreateAsync(LocalBoardFixture.NewTask("Made here"));
        await Sync().SendAsync(ProjectId.Default, mine.Id);
        await Sync().SyncNowAsync(ProjectId.Default);

        var imported = await Sync().ImportAsync(ProjectId.Default);

        imported.Sentence.Should().Contain("Nothing new");
        (await _board.Board.QueryAsync(new WorkItemQuery())).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task What_arrives_is_reconciled_from_then_on_like_anything_else()
    {
        _provider.Put("DD-7", Fields("As imported"), "To Do", "new");
        await Sync().ImportAsync(ProjectId.Default);
        _provider.Set("DD-7", nameof(WorkItem.Title), "Renamed on the provider");

        var synced = await Sync().SyncNowAsync(ProjectId.Default);

        synced.Happened.Should().BeTrue(synced.Sentence);
        var item = (await _board.Board.QueryAsync(new WorkItemQuery())).Items.Single();
        item.Title.Should().Be("Renamed on the provider", "the import left a base the pass can reason from");
    }

    [Fact]
    public async Task A_provider_that_cannot_be_reached_imports_nothing_and_says_so()
    {
        _provider.Put("DD-7", Fields("Never seen"), "To Do", "new");
        _provider.Unreachable = true;

        var imported = await Sync().ImportAsync(ProjectId.Default);

        imported.Happened.Should().BeFalse();
        imported.Sentence.Should().Contain("could not be reached");
        (await _board.Board.QueryAsync(new WorkItemQuery())).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_that_cannot_be_listed_at_all_says_that_rather_than_nothing_to_import()
    {
        _provider.CanList = false;

        var imported = await Sync().ImportAsync(ProjectId.Default);

        imported.Happened.Should().BeFalse();
        imported.Sentence.Should().Contain("cannot be asked");
    }

    [Fact]
    public async Task An_item_the_board_refuses_is_reported_and_the_rest_still_arrive()
    {
        // A title the board will not accept must not stop the other items, and must not pass unmentioned.
        _provider.Put("DD-1", Fields("   "), "To Do", "new");
        _provider.Put("DD-2", Fields("Perfectly fine"), "To Do", "new");

        var imported = await Sync().ImportAsync(ProjectId.Default);

        imported.Happened.Should().BeTrue(imported.Sentence);
        imported.Sentence.Should().Contain("DD-1");
        (await _board.Board.QueryAsync(new WorkItemQuery())).Items.Should().ContainSingle(i => i.Title == "Perfectly fine");
    }

    [Fact]
    public async Task A_project_that_follows_no_provider_has_nothing_to_import_from()
    {
        _gateways.Configured = false;

        (await Sync().ImportAsync(ProjectId.Default)).Sentence.Should().Contain("follows no provider");
    }

    private sealed class Gateways(IProviderGateway gateway) : IProviderGateways
    {
        public bool Configured { get; set; } = true;

        public IProviderGateway? For(ProjectId project) => Configured ? gateway : null;
    }

    /// <summary>A provider whose contents the test sets, and which can be asked what it has, a page at a time.</summary>
    private sealed class ListingProvider : IProviderGateway
    {
        private readonly Dictionary<string, (Dictionary<string, string?> Fields, string? Status, string? Category)> _items = [];
        private int _next = 1;

        public string Connection => "Jira";

        public bool Unreachable { get; set; }

        public bool CanList { get; set; } = true;

        /// <summary>How many items one page carries, so a test can hold more than this and prove paging.</summary>
        public int PageSize { get; set; } = 100;

        public ProviderCapabilities Capabilities => new(true, true, true, CanList);

        public void Put(string key, Dictionary<string, string?> fields, string? status, string? category) =>
            _items[key] = (fields, status, category);

        public void Set(string key, string field, string? value) => _items[key].Fields[field] = value;

        public Task<ProviderResult<ProviderPage>> ListAsync(string? cursor, CancellationToken ct = default)
        {
            if (Unreachable)
            {
                return Task.FromResult(ProviderResult<ProviderPage>.Unreachable("No route."));
            }

            var all = _items.Select(i => new ProviderItem(i.Key, "v1", i.Value.Fields, i.Value.Status, i.Value.Category)).ToList();
            var start = cursor is { Length: > 0 } carried ? int.Parse(carried, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var page = all.Skip(start).Take(PageSize).ToList();
            var next = start + page.Count;
            return Task.FromResult(ProviderResult<ProviderPage>.Ok(
                new ProviderPage(page, next < all.Count ? next.ToString(System.Globalization.CultureInfo.InvariantCulture) : null)));
        }

        public Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Unreachable
                ? ProviderResult<ProviderItem>.Unreachable("No route.")
                : _items.TryGetValue(key, out var it)
                    ? ProviderResult<ProviderItem>.Ok(new ProviderItem(key, "v1", it.Fields, it.Status, it.Category))
                    : ProviderResult<ProviderItem>.Gone(key + " is gone."));

        public Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default)
        {
            var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(payload)!;
            var key = "DD-" + (100 + _next++);
            _items[key] = (fields, "To Do", "new");
            return Task.FromResult(ProviderResult<ProviderItem>.Ok(new ProviderItem(key, "v1", fields, "To Do", "new")));
        }

        public Task<ProviderResult<ProviderItem>> UpdateAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            GetAsync(key, ct);

        public Task<ProviderResult<ProviderItem>> TransitionAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            GetAsync(key, ct);

        public Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default) =>
            Task.FromResult(ProviderResult<ProviderItem?>.Ok(null));
    }

    /// <summary>
    /// A provider that never properly ends a listing, so the import loop's guard against a non-advancing
    /// cursor is the only thing that stops it (backlog 182, host review of the first fix).
    /// </summary>
    private sealed class LoopingProvider(LoopingProvider.Mode mode) : IProviderGateway
    {
        public enum Mode
        {
            /// <summary>Every page after the first answers with the SAME cursor it gave before.</summary>
            RepeatsCursor,

            /// <summary>Every page is empty, but still promises a next one.</summary>
            EmptyPages,
        }

        public string Connection => "Jira";

        public ProviderCapabilities Capabilities => new(true, true, true, true);

        public Task<ProviderResult<ProviderPage>> ListAsync(string? cursor, CancellationToken ct = default)
        {
            if (mode == Mode.EmptyPages)
            {
                return Task.FromResult(ProviderResult<ProviderPage>.Ok(new ProviderPage([], "still-more")));
            }

            var item = new ProviderItem(
                cursor is null ? "DD-1" : "DD-2", "v1",
                new Dictionary<string, string?>(StringComparer.Ordinal) { [nameof(WorkItem.Title)] = "Looping" }, "To Do", "new");
            return Task.FromResult(ProviderResult<ProviderPage>.Ok(new ProviderPage([item], "stuck")));
        }

        public Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default) =>
            throw new NotSupportedException("Not used by an import.");

        public Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not used by an import.");

        public Task<ProviderResult<ProviderItem>> UpdateAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not used by an import.");

        public Task<ProviderResult<ProviderItem>> TransitionAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not used by an import.");

        public Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not used by an import.");
    }
}
