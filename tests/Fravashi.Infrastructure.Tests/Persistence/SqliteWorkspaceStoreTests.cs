using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

public sealed class SqliteWorkspaceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "fravashi-workspace-store-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWorkspaceStore _store;

    public SqliteWorkspaceStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _store = new SqliteWorkspaceStore(_factory);
    }

    [Fact]
    public void Touch_Upserts_AndBumpsTheRecentsOrder()
    {
        _store.Touch("aaaa", "alpha", @"C:\repos\alpha");
        _store.Touch("bbbb", "beta", @"C:\repos\beta");
        _store.Touch("aaaa", "alpha-renamed", @"C:\repos\alpha");

        var recent = _store.Recent();
        recent.Should().HaveCount(2);
        recent[0].Id.Should().Be("aaaa");
        recent[0].Name.Should().Be("alpha-renamed");
        _store.Get("bbbb")!.RootPath.Should().Be(@"C:\repos\beta");
    }

    [Fact]
    public void Recent_HonorsTheCap()
    {
        for (var i = 0; i < 12; i++)
        {
            _store.Touch($"id-{i:00}", $"ws-{i}", $@"C:\repos\ws{i}");
        }

        _store.Recent(max: 5).Should().HaveCount(5);
    }

    [Fact]
    public void Touch_LeavesANewWorkspaceInNoProject_AndReopeningNeverResetsItsProject()
    {
        // Item #373, no shared bucket: a new folder is not set up until someone puts it in a project.
        _store.Touch("ws", "alpha", "/repos/alpha");
        _store.Get("ws")!.ProjectId.Should().BeNull();
        _store.Get("ws")!.FormerProjectId.Should().BeNull();

        var projects = new SqliteProjectStore(_factory, TimeProvider.System);
        var platform = projects.Create("Platform");
        projects.BindWorkspace("ws", platform.Id).Should().BeTrue();

        _store.Touch("ws", "alpha-renamed", "/repos/alpha");

        var reopened = _store.Get("ws")!;
        reopened.Name.Should().Be("alpha-renamed");
        reopened.ProjectId.Should().Be(platform.Id.Value);
        _store.Recent().Single().ProjectId.Should().Be(platform.Id.Value);
    }

    [Fact]
    public void Remove_DeletesTheRecord()
    {
        _store.Touch("gone", "gone", @"C:\repos\gone");
        _store.Remove("gone");
        _store.Get("gone").Should().BeNull();
    }

    public void Dispose()
    {
        // No ClearAllPools: it is process-global and racing parallel SQLite classes; Pooling: false above
        // makes it unnecessary (backlog 89).
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
