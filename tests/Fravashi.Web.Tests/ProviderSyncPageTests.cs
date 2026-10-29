using Bunit;
using Fravashi.Application.Mirroring;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Mirroring;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 82's surface. ADR 0018 decision 8 asks for a VISIBLE freshness indicator and decision 5 for a
/// conflict to be SURFACED rather than silently resolved, and neither is true of something that only exists
/// in a database. Driven over the real stores, because the page's whole job is to report what they hold.
/// </summary>
public sealed class ProviderSyncPageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly WorkItemId Item = new("item-1");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;
    private readonly LocalTrackerResolver _resolver;
    private readonly SqliteMirrorStore _mirror;
    private readonly SqliteOutboxStore _outbox;

    public ProviderSyncPageTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _resolver = new LocalTrackerResolver(_factory, TimeProvider.System);
        _mirror = new SqliteMirrorStore(_factory);
        _outbox = new SqliteOutboxStore(_factory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp folder.
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The provider this project follows in a test (backlog 47); null means none, as before.</summary>
    private Fravashi.Application.Mirroring.IProviderGateway? _follows;

    /// <summary>Backlog 232: the outage record, when a test watches for one.</summary>
    private Fravashi.Application.Network.ProviderOutages? _outages;

    /// <summary>Backlog 246: the person's zone the page formats times in; UTC unless a test says otherwise.</summary>
    private Fravashi.Web.Services.PersonTimeZone _zone = PersonZones.Utc();

    private BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ITrackerResolver>(_resolver);
        ctx.Services.AddSingleton(TestProviderSync.Over(_factory, _follows, outages: _outages));
        // Backlog 121: a page follows its window's project; these tests have one project.
        ctx.Services.AddSingleton<Fravashi.Application.Projects.ICurrentProject>(Fravashi.Application.Projects.SingleProject.Instance);
        ctx.Services.AddSingleton<Fravashi.Application.Projects.IProjectStore>(new OneProjectStore());
        ctx.Services.AddSingleton<IMirrorStore>(_mirror);
        ctx.Services.AddSingleton<IOutboxStore>(_outbox);
        ctx.Services.AddSingleton(new GraphRepair(_mirror));
        ctx.Services.AddSingleton<TimeProvider>(new FixedClock());
        ctx.Services.AddSingleton(_zone);
        return ctx;
    }

    private static string Text(IRenderedComponent<ProviderSyncPage> cut, string testId) =>
        cut.Find($"[data-testid='{testId}']").TextContent;

    private Task MirrorAsync(string key = "PROJ-1", DateTimeOffset? confirmed = null) =>
        _mirror.ConfirmAsync(
            ProjectId.Default,
            Item,
            new ProviderConfirmation("jira", key, "etag-1", 1),
            confirmed ?? Now);

    private Task<OutboxEntry> QueueAsync(string id, OutboxState state = OutboxState.Pending) =>
        _outbox.QueueAsync(new OutboxEntry(
            id, ProjectId.Default, Item, OutboxKind.Update, "{}", 1, state, 0, Now, null, null,
            state == OutboxState.Failed ? "jira refused it: 'Epic' is not a type here." : null));

    // ---- no provider -----------------------------------------------------------------------------

    [Fact]
    public void A_board_with_no_provider_says_it_is_the_source_of_truth_rather_than_out_of_date()
    {
        // Showing "nothing has synced" for a product working exactly as designed would read as a fault.
        using var ctx = Context();

        var cut = ctx.Render<ProviderSyncPage>();

        Assert.Contains("no provider behind it", Text(cut, "sync-local-only"), StringComparison.Ordinal);
        Assert.Contains("nothing to reconcile", Text(cut, "sync-local-only"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='sync-overview']"));
    }

    // ---- backlog 47: sync now ---------------------------------------------------------------------

    [Fact]
    public void With_no_provider_followed_there_is_no_sync_button_to_press()
    {
        using var ctx = Context();

        var cut = ctx.Render<ProviderSyncPage>();

        Assert.Empty(cut.FindAll("[data-testid=sync-now]"));
    }

    [Fact]
    public async Task Sync_now_sends_what_was_queued_and_the_item_then_shows_under_its_key()
    {
        var provider = new AcceptingProvider();
        _follows = provider;
        var created = await _resolver.For(ProjectId.Default).CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Goes to the provider", Description = "Why it exists." },
            MutationContext.User);
        using var ctx = Context();
        await ctx.Services.GetRequiredService<ProviderSync>().SendAsync(ProjectId.Default, created.Value.Id);
        var cut = ctx.Render<ProviderSyncPage>();

        // A followed provider with something queued and nothing mirrored yet is NOT "the source of truth".
        Assert.Empty(cut.FindAll("[data-testid=sync-local-only]"));
        Assert.Contains("follows Jira", Text(cut, "sync-now-section"), StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=sync-now]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("reached Jira", Text(cut, "sync-outcome"), StringComparison.Ordinal);
            Assert.Contains("DD-1", Text(cut, "sync-overview"), StringComparison.Ordinal);
        });
        Assert.Single(provider.Created);
    }

    // ---- backlog 246: the outage's "since" is the person's hour, not the container's --------------

    [Theory]
    [InlineData("Europe/Stockholm", "14:00")]
    [InlineData("America/Los_Angeles", "05:00")]
    [InlineData("Asia/Kolkata", "17:30")]
    public async Task The_outage_line_says_since_in_the_persons_zone(string zone, string hour)
    {
        var provider = new FlakyProvider(new AcceptingProvider())
        {
            Down = new Fravashi.Application.Network.NetworkFailure(Fravashi.Application.Network.NetworkFailureKind.NameNotResolved, "test"),
        };
        _follows = provider;
        _outages = new Fravashi.Application.Network.ProviderOutages(new FixedClock());
        _zone = PersonZones.Fixed(zone);
        var created = await _resolver.For(ProjectId.Default).CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Waits for Jira", Description = "Why it exists." },
            MutationContext.User);
        using var ctx = Context();
        await ctx.Services.GetRequiredService<ProviderSync>().SendAsync(ProjectId.Default, created.Value.Id);
        var cut = ctx.Render<ProviderSyncPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=sync-now]").Click());

        cut.WaitForAssertion(() => Assert.StartsWith(
            $"Jira unreachable since {hour} (name not resolved); 1 change(s) waiting; nothing lost.",
            Text(cut, "sync-outage").Trim(),
            StringComparison.Ordinal));
        // The log line and anything an agent is handed keep the plain words, unchanged.
        Assert.StartsWith("Jira unreachable since 12:00 (name not resolved)", _outages.For("Jira")!.Words, StringComparison.Ordinal);
    }

    // ---- backlog 232: the outage is shown ----------------------------------------------------------

    [Fact]
    public async Task An_unreachable_provider_is_named_with_since_when_and_what_waits_and_the_line_goes_after_an_answered_sync()
    {
        var provider = new FlakyProvider(new AcceptingProvider())
        {
            Down = new Fravashi.Application.Network.NetworkFailure(Fravashi.Application.Network.NetworkFailureKind.NameNotResolved, "test"),
        };
        _follows = provider;
        _outages = new Fravashi.Application.Network.ProviderOutages(new FixedClock());
        var created = await _resolver.For(ProjectId.Default).CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Waits for Jira", Description = "Why it exists." },
            MutationContext.User);
        using var ctx = Context();
        await ctx.Services.GetRequiredService<ProviderSync>().SendAsync(ProjectId.Default, created.Value.Id);
        var cut = ctx.Render<ProviderSyncPage>();
        Assert.Empty(cut.FindAll("[data-testid=sync-outage]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=sync-now]").Click());

        cut.WaitForAssertion(() => Assert.StartsWith(
            "Jira unreachable since 12:00 (name not resolved); 1 change(s) waiting; nothing lost.",
            Text(cut, "sync-outage").Trim(),
            StringComparison.Ordinal));
        Assert.Contains("this board's own copy", Text(cut, "sync-outage"), StringComparison.Ordinal);

        // A fresh page says the same: the outage is the studio's, not this render's.
        var again = ctx.Render<ProviderSyncPage>();
        Assert.StartsWith("Jira unreachable since 12:00", Text(again, "sync-outage").Trim(), StringComparison.Ordinal);

        provider.Down = null;
        await cut.InvokeAsync(() => cut.Find("[data-testid=sync-now]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=sync-outage]"));
            Assert.Contains("reached Jira", Text(cut, "sync-outcome"), StringComparison.Ordinal);
        });
        Assert.Single(provider.Inner.Created);
    }

    /// <summary>A provider whose calls never leave while <see cref="Down"/> is set, and reach the one behind it otherwise.</summary>
    private sealed class FlakyProvider(AcceptingProvider inner) : IProviderGateway
    {
        public AcceptingProvider Inner => inner;

        public Fravashi.Application.Network.NetworkFailure? Down { get; set; }

        public string Connection => inner.Connection;

        public ProviderCapabilities Capabilities => inner.Capabilities;

        public Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default) =>
            Down is { } failure ? Unreachable<ProviderItem>(failure) : inner.CreateAsync(payload, operationId, ct);

        public Task<ProviderResult<ProviderItem>> UpdateAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            Down is { } failure ? Unreachable<ProviderItem>(failure) : inner.UpdateAsync(key, payload, providerVersion, operationId, ct);

        public Task<ProviderResult<ProviderItem>> TransitionAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            Down is { } failure ? Unreachable<ProviderItem>(failure) : inner.TransitionAsync(key, payload, providerVersion, operationId, ct);

        public Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default) =>
            Down is { } failure ? Unreachable<ProviderItem?>(failure) : inner.FindByOperationIdAsync(operationId, ct);

        public Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default) =>
            Down is { } failure ? Unreachable<ProviderItem>(failure) : inner.GetAsync(key, ct);

        private static Task<ProviderResult<T>> Unreachable<T>(Fravashi.Application.Network.NetworkFailure failure) =>
            Task.FromResult(ProviderResult<T>.Unreachable("Jira could not be reached.") with { Network = failure });
    }

    [Fact]
    public async Task A_board_mirrored_to_a_provider_it_no_longer_follows_offers_to_follow_the_new_one()
    {
        // Backlog 145: the owner's path moves this repository's project from Jira to Azure DevOps.
        _follows = new AcceptingProvider();
        var created = await _resolver.For(ProjectId.Default).CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Was elsewhere", Description = "Why it exists." }, MutationContext.User);
        await new SqliteMirrorStore(_factory).ConfirmAsync(
            ProjectId.Default, created.Value.Id, new ProviderConfirmation("Old tracker", "OLD-1", "v1", 1), DateTimeOffset.UtcNow);
        using var ctx = Context();
        var cut = ctx.Render<ProviderSyncPage>();

        Assert.Contains("1 item(s) are mirrored to Old tracker", Text(cut, "sync-foreign"), StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=sync-follow]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=sync-foreign-section]"));
            Assert.Contains("keep that as history", Text(cut, "sync-outcome"), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Send_everything_queues_the_whole_board_and_sync_now_sends_it()
    {
        // Backlog 49: the owner could not send an imported backlog, because sending was one item page at a time.
        var provider = new AcceptingProvider();
        _follows = provider;
        foreach (var title in new[] { "First", "Second" })
        {
            await _resolver.For(ProjectId.Default).CreateAsync(
                new NewWorkItem { Level = WorkItemLevel.Task, Title = title, Description = "Why it exists." }, MutationContext.User);
        }

        using var ctx = Context();
        var cut = ctx.Render<ProviderSyncPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=sync-send-all]").Click());
        cut.WaitForAssertion(() => Assert.StartsWith("2 item(s) are queued for Jira", Text(cut, "sync-outcome"), StringComparison.Ordinal));

        await cut.InvokeAsync(() => cut.Find("[data-testid=sync-now]").Click());
        cut.WaitForAssertion(() => Assert.Contains("reached Jira", Text(cut, "sync-outcome"), StringComparison.Ordinal));
        Assert.Equal(2, provider.Created.Count);
    }

    [Fact]
    public async Task What_the_provider_has_can_be_brought_in_from_the_page()
    {
        var provider = new AcceptingProvider { Holds = { ["DD-9"] = "Written on the provider" } };
        _follows = provider;
        using var ctx = Context();
        var cut = ctx.Render<ProviderSyncPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=sync-import]").Click());

        cut.WaitForAssertion(() => Assert.Contains("1 item(s) came from Jira", Text(cut, "sync-outcome"), StringComparison.Ordinal));
        var items = await _resolver.For(ProjectId.Default).QueryAsync(new WorkItemQuery());
        Assert.Contains(items.Items, i => i.Title == "Written on the provider");
    }

    [Fact]
    public void A_provider_that_cannot_be_asked_what_it_has_is_not_offered_an_import()
    {
        _follows = new AcceptingProvider { CanList = false };
        using var ctx = Context();

        var cut = ctx.Render<ProviderSyncPage>();

        Assert.NotEmpty(cut.FindAll("[data-testid=sync-now]"));
        Assert.Empty(cut.FindAll("[data-testid=sync-import]"));
    }

    // ---- freshness -------------------------------------------------------------------------------

    [Fact]
    public async Task A_mirrored_item_shows_what_the_provider_last_said_and_when()
    {
        await MirrorAsync();
        using var ctx = Context();

        var cut = ctx.Render<ProviderSyncPage>();

        Assert.Equal("current", Text(cut, "sync-level-item-1").Trim());
        Assert.Contains("PROJ-1", Text(cut, "sync-item-item-1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_nobody_has_heard_about_for_hours_says_so()
    {
        await MirrorAsync(confirmed: Now.AddHours(-6));
        using var ctx = Context();

        var cut = ctx.Render<ProviderSyncPage>();

        Assert.Equal("not recent", Text(cut, "sync-level-item-1").Trim());
        Assert.Contains("6 hours ago", Text(cut, "sync-item-item-1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_the_provider_no_longer_has_says_the_copy_is_kept()
    {
        await MirrorAsync();
        await _mirror.TombstoneAsync(ProjectId.Default, Item, "It was deleted there on the 16th.", Now);
        using var ctx = Context();

        var cut = ctx.Render<ProviderSyncPage>();

        Assert.Equal("deleted there", Text(cut, "sync-level-item-1").Trim());
        Assert.Contains("kept with its history", Text(cut, "sync-item-item-1"), StringComparison.Ordinal);
    }

    // ---- the conflict surface --------------------------------------------------------------------

    [Fact]
    public async Task A_disagreement_is_shown_as_a_question_with_both_values()
    {
        await MirrorAsync();
        await _mirror.RecordConflictAsync(
            ProjectId.Default,
            new FieldConflict("c-1", Item, nameof(WorkItem.Title), "Was this", "Mine", "Theirs", Now, null, null));
        using var ctx = Context();

        var cut = ctx.Render<ProviderSyncPage>();

        var text = Text(cut, "sync-conflict-c-1");
        Assert.Contains("'Mine' here", text, StringComparison.Ordinal);
        Assert.Contains("'Theirs' on jira", text, StringComparison.Ordinal);
        Assert.Contains("'Was this'", text, StringComparison.Ordinal);
        Assert.Contains("Keep what is here", text, StringComparison.Ordinal);
        Assert.Contains("Take the provider's", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disagreement_that_carries_a_note_shows_it_beside_the_two_values()
    {
        // Schema 28. Three of the disagreements the reconcile pass records have no provider VALUE to put
        // in the provider slot: a move the board refused, a status that cannot be expressed over there, a
        // native name nothing recognised. They used to put a whole sentence where a value goes, which read
        // badly here and, once settling began rebasing onto these values, would have been written in as
        // the agreed one.
        await MirrorAsync();
        await _mirror.RecordConflictAsync(
            ProjectId.Default,
            new FieldConflict("c-9", Item, nameof(WorkItem.Status), null, "in_review", "backlog", Now, null, null)
            {
                Note = "It was moved here and not sent. jira has more than one state that reads as in_review here.",
            });
        using var ctx = Context();

        var cut = ctx.Render<ProviderSyncPage>();

        var text = Text(cut, "sync-conflict-c-9");
        Assert.Contains("'in_review' here", text, StringComparison.Ordinal);
        Assert.Contains("'backlog' on jira", text, StringComparison.Ordinal);
        Assert.Contains("more than one state", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_a_side_settles_the_disagreement_and_says_what_happens_next()
    {
        await MirrorAsync();
        await _mirror.RecordConflictAsync(
            ProjectId.Default,
            new FieldConflict("c-1", Item, nameof(WorkItem.Title), "Was this", "Mine", "Theirs", Now, null, null));
        using var ctx = Context();
        var cut = ctx.Render<ProviderSyncPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='sync-keep-local-c-1']").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("Your value holds", Text(cut, "sync-outcome"), StringComparison.Ordinal));
        Assert.Empty(await _mirror.OpenConflictsAsync(ProjectId.Default));
        Assert.Empty(cut.FindAll("[data-testid='sync-conflict-c-1']"));
    }

    [Fact]
    public async Task Taking_the_provider_value_says_it_will_be_written_here()
    {
        await MirrorAsync();
        await _mirror.RecordConflictAsync(
            ProjectId.Default,
            new FieldConflict("c-1", Item, nameof(WorkItem.Title), null, "Mine", "Theirs", Now, null, null));
        using var ctx = Context();
        var cut = ctx.Render<ProviderSyncPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='sync-keep-provider-c-1']").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("written here at the next sync", Text(cut, "sync-outcome"), StringComparison.Ordinal));
    }

    // ---- the queue -------------------------------------------------------------------------------

    [Fact]
    public async Task A_queued_change_is_shown_as_still_on_its_way()
    {
        await MirrorAsync();
        await QueueAsync("op-1");
        using var ctx = Context();

        var cut = ctx.Render<ProviderSyncPage>();

        Assert.Contains("has not reached the provider yet", Text(cut, "sync-write-op-1"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='sync-abandon-op-1']"));
    }

    [Fact]
    public async Task A_change_that_will_not_go_says_why_and_offers_the_only_decision_left()
    {
        await MirrorAsync();
        await QueueAsync("op-1", OutboxState.Failed);
        using var ctx = Context();

        var cut = ctx.Render<ProviderSyncPage>();

        Assert.Contains("'Epic' is not a type here", Text(cut, "sync-write-op-1"), StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid='sync-abandon-op-1']"));
    }

    [Fact]
    public async Task Abandoning_a_change_is_a_decision_with_a_stated_cost()
    {
        // Never something a retry loop does on its own, and never silently: what it was going to change is
        // still different on the two sides afterwards, and the page says so.
        await MirrorAsync();
        await QueueAsync("op-1", OutboxState.Failed);
        using var ctx = Context();
        var cut = ctx.Render<ProviderSyncPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='sync-abandon-op-1']").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("still different on the two sides", Text(cut, "sync-outcome"), StringComparison.Ordinal));
        Assert.Empty(await _outbox.OpenAsync(ProjectId.Default));
    }

    // ---- the graph check -------------------------------------------------------------------------

    [Fact]
    public async Task Checking_the_graph_reports_what_it_looked_at_even_when_nothing_is_wrong()
    {
        // An empty report from a pass that never ran and one from a sound graph must not read the same.
        using var ctx = Context();
        var cut = ctx.Render<ProviderSyncPage>();

        Assert.Empty(cut.FindAll("[data-testid='sync-graph-summary']"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='sync-check-graph']").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("0 items and 0 links", Text(cut, "sync-graph-summary"), StringComparison.Ordinal));
    }
}
