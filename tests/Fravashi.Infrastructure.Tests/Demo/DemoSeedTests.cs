using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Demo;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Tests.WorkItems;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Demo;

/// <summary>
/// The demo seed (backlog 71) against the REAL stores on a real file-backed database with the production
/// schema, because that is the point of it: the plan's design decision is that a demo install is an ordinary
/// install whose data is seeded, so a seed that only works against a fake has proved nothing.
/// <para>
/// The acceptance's second promise is that "timestamps and content are deterministic, so screenshots are
/// stable across runs", and that is what the first test holds, by seeding twice into two INDEPENDENT fresh
/// databases and comparing. What it compares is the VISIBLE projection (numbers, titles, statuses, levels,
/// bodies, stamps) rather than the raw rows, and the reason is worth stating rather than hiding: a work item
/// id and a wiki id are minted by the store and differ between runs by design. They are also on no surface a
/// screenshot shows, which addresses a number and a slug. Comparing rows would fail on a difference nobody
/// can see, which is a test that teaches the reader to ignore it.
/// </para>
/// </summary>
public sealed class DemoSeedTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Seeding_two_fresh_installs_produces_the_same_visible_graph()
    {
        var first = await SeedAndReadAsync();
        var second = await SeedAndReadAsync();

        second.Should().Be(first, "a demo whose screenshots differ between two fresh installs is not a demo");
    }

    [Fact]
    public async Task The_seed_fills_every_board_column_and_one_document_of_each_type()
    {
        using var install = new DemoInstall(Fixed);

        var result = await install.Seed.SeedAsync(ProjectId.Default);

        result.Seeded.Should().BeTrue();

        // Every column, because an empty one on a screenshot reads as a defect in the product.
        var board = await install.Tracker.QueryAsync(new WorkItemQuery { Limit = 100 });
        foreach (var status in Enum.GetValues<Domain.WorkItems.WorkItemStatus>())
        {
            board.Items.Should().Contain(
                i => i.Status == status, $"the demo board should have something in {status}");
        }

        // Every document type, so the wiki's type filter has something to filter.
        var documents = await install.Wiki.ListAsync(new WikiItemQuery());
        foreach (var type in Enum.GetValues<WikiItemType>())
        {
            documents.Should().Contain(d => d.Type == type, $"the demo wiki should have a {type}");
        }

        // Backlog 193: the demo's design is one the reader accepts, so its page shows a mockup and not errors.
        Fravashi.Application.Mockups.DesignMockup.Read(documents.Single(d => d.Type == WikiItemType.Design).Body)
            .Errors.Should().BeEmpty();
    }

    /// <summary>
    /// The acceptance asks for references that resolve AND one that is deliberately broken, which is the half
    /// a demo usually leaves out. A product whose argument is that it reports what is missing has to be
    /// photographable doing exactly that.
    /// </summary>
    [Fact]
    public async Task The_seed_carries_a_reference_that_resolves_and_one_that_does_not()
    {
        using var install = new DemoInstall(Fixed);
        await install.Seed.SeedAsync(ProjectId.Default);

        var documents = await install.Wiki.ListAsync(new WikiItemQuery());
        var bodies = string.Join("\n", documents.Select(d => d.Body));

        var streaming = (await install.Tracker.QueryAsync(new WorkItemQuery { Limit = 100 })).Items
            .Single(i => i.Title.StartsWith("Stream a large file", StringComparison.Ordinal));

        bodies.Should().Contain(
            $"#{streaming.Number}", "a reference that resolves is what makes the graph visible on a screenshot");

        // @99 names a wiki document number the seed never creates, so it is absent by construction rather
        // than by luck however many documents the seed grows to, and the page renders it visibly broken.
        bodies.Should().Contain("@99");
        documents.Should().HaveCountLessThan(99, "the broken reference is only broken while nothing answers it");
    }

    [Fact]
    public async Task Seeding_an_install_that_already_has_work_refuses_rather_than_doubling_it()
    {
        using var install = new DemoInstall(Fixed);
        await install.Seed.SeedAsync(ProjectId.Default);
        var after = await install.Tracker.QueryAsync(new WorkItemQuery { Limit = 100 });

        var again = await install.Seed.SeedAsync(ProjectId.Default);

        again.Seeded.Should().BeFalse();
        again.WorkItems.Should().Be(0);
        (await install.Tracker.QueryAsync(new WorkItemQuery { Limit = 100 })).Items.Count
            .Should().Be(after.Items.Count, "seeding twice would double every column");
    }

    /// <summary>
    /// Marking onboarding complete is the only gate between a fresh install and the studio, and the seed does
    /// it LAST on purpose: a seed that failed part way should leave an install that still goes to onboarding,
    /// rather than one that opens on half a demo.
    /// </summary>
    [Fact]
    public async Task The_seed_opens_the_studio_by_marking_onboarding_complete()
    {
        using var install = new DemoInstall(Fixed);
        install.Onboarding.IsComplete.Should().BeFalse("a fresh install starts at onboarding");

        await install.Seed.SeedAsync(ProjectId.Default);

        install.Onboarding.IsComplete.Should().BeTrue();
    }

    private static async Task<string> SeedAndReadAsync()
    {
        using var install = new DemoInstall(Fixed);
        await install.Seed.SeedAsync(ProjectId.Default);
        return await install.VisibleGraphAsync();
    }

    /// <summary>One whole fresh install: its own database, its own real stores, a fixed clock.</summary>
    private sealed class DemoInstall : IDisposable
    {
        private readonly SqliteTestContext _context = new();

        public DemoInstall(DateTimeOffset now)
        {
            var clock = new FixedClock(now);
            var trackers = new LocalTrackerResolver(_context.Factory, clock);
            var wikis = new SqliteWikiLibraries(_context.Factory, clock);
            Onboarding = new SqliteOnboardingState(_context.Factory);
            Tracker = trackers.For(ProjectId.Default);
            Wiki = wikis.For(ProjectId.Default);
            Seed = new DemoSeed(trackers, wikis, Onboarding, clock);
        }

        public ITracker Tracker { get; }

        public IWikiLibrary Wiki { get; }

        public SqliteOnboardingState Onboarding { get; }

        public DemoSeed Seed { get; }

        public void Dispose() => _context.Dispose();

        /// <summary>
        /// Everything a surface would SHOW, in a stable order, as one string. Deliberately not the rows: store
        /// minted ids differ between installs and appear on no screen.
        /// </summary>
        public async Task<string> VisibleGraphAsync()
        {
            var text = new StringBuilder();

            var board = await Tracker.QueryAsync(new WorkItemQuery { Limit = 200 });
            foreach (var item in board.Items.OrderBy(i => i.Number))
            {
                text.Append(CultureInfo.InvariantCulture, $"#{item.Number} {item.Level} {item.Kind} {item.Status}")
                    .Append(CultureInfo.InvariantCulture, $" {item.Resolution} {item.Title} :: {item.Description}")
                    .Append(CultureInfo.InvariantCulture, $" created={item.CreatedUtc:O} updated={item.UpdatedUtc:O}")
                    .AppendLine();
            }

            var documents = await Wiki.ListAsync(new WikiItemQuery());
            foreach (var document in documents.OrderBy(d => d.Number))
            {
                text.Append(CultureInfo.InvariantCulture, $"@{document.Number} {document.Type} {document.Slug} {document.Title}")
                    .Append(CultureInfo.InvariantCulture, $" created={document.CreatedUtc:O} updated={document.UpdatedUtc:O}")
                    .AppendLine()
                    .AppendLine(document.Body);
            }

            return text.ToString();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
