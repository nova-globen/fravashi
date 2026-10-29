using Fravashi.Application.Wiki;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

public sealed class WikiSyncServiceTests
{
    private static WikiSyncService NewService(ISqliteConnectionFactory factory, WikiSourceResolution resolution)
    {
        var services = new ServiceCollection();
        services.AddScoped<IWikiSourceResolver>(_ => new FakeResolver(resolution));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new WikiSyncService(
            scopeFactory,
            new SqliteWikiPageStore(factory),
            new SqliteConfigurationStore(factory),
            new InMemoryNotificationHistory(),
            NullLogger<WikiSyncService>.Instance);
    }

    private static WikiSourceResolution Ready(params WikiSourcePage[] pages) =>
        new(WikiSourceStatus.Ready, new FakeSource(pages), "Syncing from Fake.");

    [Fact]
    public async Task SyncNow_PopulatesTheStore_WithMatchingHashes_SiteAuthoritative()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteWikiPageStore(ctx.Factory);
        var config = new SqliteConfigurationStore(ctx.Factory);
        var service = NewService(ctx.Factory, Ready(
            new WikiSourcePage("DOCS", "Home", "home", "hello world", "markdown", "v1"),
            new WikiSourcePage("DOCS", "Guide", "guide", "the guide", "markdown", "v3")));

        await service.SyncNowAsync();

        store.Count().Should().Be(2);
        var home = store.GetBySlug("DOCS", "home")!;
        home.Body.Should().Be("hello world");
        home.SourceHash.Should().NotBeNullOrEmpty();
        home.SourceHash.Should().Be(home.LocalHash); // equal right after a pull
        home.HasConflict.Should().BeFalse();

        service.State.Phase.Should().Be(WikiSyncPhase.Succeeded);
        service.State.Synced.Should().Be(2);
        service.State.Conflicts.Should().Be(0);
        config.Get(WikiSyncService.LastSyncKey).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SyncNow_OverwritesLocal_WhenSiteChanged_AndLocalUnchanged()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteWikiPageStore(ctx.Factory);

        await NewService(ctx.Factory, Ready(
            new WikiSourcePage("DOCS", "Home", "home", "v1 body", "markdown", "v1"))).SyncNowAsync();

        // Site changes; the local copy was never edited (LocalHash == SourceHash) → authoritative overwrite.
        var second = NewService(ctx.Factory, Ready(
            new WikiSourcePage("DOCS", "Home", "home", "v2 body", "markdown", "v2")));
        await second.SyncNowAsync();

        var page = store.GetBySlug("DOCS", "home")!;
        page.Body.Should().Be("v2 body");
        page.HasConflict.Should().BeFalse();
        page.SourceHash.Should().Be(page.LocalHash);
        second.State.Conflicts.Should().Be(0);
    }

    [Fact]
    public async Task SyncNow_DetectsConflict_WhenLocalDiverged_AndSiteDiffers_WithoutOverwriting()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteWikiPageStore(ctx.Factory);

        // Seed a page whose LOCAL body diverged from what was last pulled (LocalHash != SourceHash).
        store.Upsert(new WikiPage(
            "id1", "DOCS", "Home", "home", "locally edited body", "markdown",
            "vOld", SourceHash: "SITE-OLD-HASH", LocalHash: "LOCAL-EDIT-HASH",
            HasConflict: false, UpdatedUtc: DateTimeOffset.UtcNow));

        var service = NewService(ctx.Factory, Ready(
            new WikiSourcePage("DOCS", "Home", "home", "new site body", "markdown", "vNew")));
        await service.SyncNowAsync();

        var page = store.GetBySlug("DOCS", "home")!;
        page.Body.Should().Be("locally edited body"); // NOT overwritten (O9: never silently)
        page.HasConflict.Should().BeTrue();
        service.State.Conflicts.Should().Be(1);
        service.State.Synced.Should().Be(0);
    }

    [Fact]
    public async Task SyncNow_WhenNoOnlineWiki_SkipsHonestly_NoStoreWrites()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteWikiPageStore(ctx.Factory);
        var config = new SqliteConfigurationStore(ctx.Factory);
        var service = NewService(ctx.Factory,
            new WikiSourceResolution(WikiSourceStatus.None, null, "Using the local wiki - nothing to sync."));

        await service.SyncNowAsync();

        service.State.Phase.Should().Be(WikiSyncPhase.Skipped);
        store.Count().Should().Be(0);
        config.Get(WikiSyncService.LastSyncKey).Should().BeNull();
        service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task SyncNow_WhenUnsupportedProvider_SkipsWithReason()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteWikiPageStore(ctx.Factory);
        var service = NewService(ctx.Factory,
            new WikiSourceResolution(WikiSourceStatus.Unsupported, null, "ADO wiki read-sync is a follow-up."));

        await service.SyncNowAsync();

        service.State.Phase.Should().Be(WikiSyncPhase.Skipped);
        service.State.Message.Should().Contain("follow-up");
        store.Count().Should().Be(0);
    }

    [Fact]
    public async Task EnsureStarted_RunsOnce_ThenNoOpsAfterTheInitialSync()
    {
        using var ctx = new SqliteTestContext();
        var source = new CountingSource(new WikiSourcePage("DOCS", "Home", "home", "b", "markdown", "v1"));
        var resolution = new WikiSourceResolution(WikiSourceStatus.Ready, source, "ok");

        var services = new ServiceCollection();
        services.AddScoped<IWikiSourceResolver>(_ => new FakeResolver(resolution));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var service = new WikiSyncService(
            scopeFactory, new SqliteWikiPageStore(ctx.Factory), new SqliteConfigurationStore(ctx.Factory),
            new InMemoryNotificationHistory(), NullLogger<WikiSyncService>.Instance);

        await service.EnsureStartedAsync();
        await service.EnsureStartedAsync(); // marker present → no second fetch

        source.Fetches.Should().Be(1);
    }

    [Fact]
    public async Task A_pull_that_cannot_reach_the_site_names_the_outage_and_an_answered_pull_ends_it()
    {
        // Backlog 232: the mirror's pull says the outage in the words the board's sync and publishing use.
        using var ctx = new SqliteTestContext();
        var outages = new Fravashi.Application.Network.ProviderOutages(new Fravashi.Testing.Network.FakeNetworkClock());
        var source = new DownSource { Failure = Fravashi.Testing.Network.ScriptedShapes.NameFails("acme.atlassian.net") };
        var services = new ServiceCollection();
        services.AddScoped<IWikiSourceResolver>(_ => new FakeResolver(new WikiSourceResolution(WikiSourceStatus.Ready, source, "ok")));
        var service = new WikiSyncService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new SqliteWikiPageStore(ctx.Factory), new SqliteConfigurationStore(ctx.Factory),
            new InMemoryNotificationHistory(), NullLogger<WikiSyncService>.Instance, outages);

        await service.SyncNowAsync();

        service.State.Phase.Should().Be(WikiSyncPhase.Failed);
        service.State.Message.Should().Be(
            "Confluence unreachable since 12:00 (name not resolved). The local wiki is served as it was last pulled; Sync again once it answers.");
        outages.For("Confluence").Should().NotBeNull();

        source.Failure = null;
        await service.SyncNowAsync();

        service.State.Phase.Should().Be(WikiSyncPhase.Succeeded);
        outages.For("Confluence").Should().BeNull();
    }

    private sealed class DownSource : IWikiSource
    {
        public Exception? Failure { get; set; }

        public string Provider => "Confluence";

        public Task<IReadOnlyList<WikiSourcePage>> FetchAllAsync(IProgress<WikiFetchProgress>? progress, CancellationToken cancellationToken = default) =>
            Failure is { } failure
                ? Task.FromException<IReadOnlyList<WikiSourcePage>>(failure)
                : Task.FromResult<IReadOnlyList<WikiSourcePage>>([new WikiSourcePage("DOCS", "Home", "home", "b", "markdown", "v1")]);
    }

    private sealed class FakeResolver(WikiSourceResolution resolution) : IWikiSourceResolver
    {
        public WikiSourceResolution Resolve() => resolution;
    }

    private sealed class FakeSource(WikiSourcePage[] pages) : IWikiSource
    {
        public string Provider => "Fake";
        public Task<IReadOnlyList<WikiSourcePage>> FetchAllAsync(IProgress<WikiFetchProgress>? progress, CancellationToken cancellationToken = default)
        {
            progress?.Report(new WikiFetchProgress(pages.Length, pages.Length, null));
            return Task.FromResult<IReadOnlyList<WikiSourcePage>>(pages);
        }
    }

    private sealed class CountingSource(params WikiSourcePage[] pages) : IWikiSource
    {
        public int Fetches { get; private set; }
        public string Provider => "Counting";
        public Task<IReadOnlyList<WikiSourcePage>> FetchAllAsync(IProgress<WikiFetchProgress>? progress, CancellationToken cancellationToken = default)
        {
            Fetches++;
            return Task.FromResult<IReadOnlyList<WikiSourcePage>>(pages);
        }
    }
}
