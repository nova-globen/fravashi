using AwesomeAssertions;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Persistence;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>Projects and the workspace binding (schema v11, canonical artifact model plan section 2).</summary>
public sealed class SqliteProjectStoreTests : IDisposable
{
    private readonly SqliteTestContext _context = new();
    private readonly SqliteProjectStore _store;
    private readonly SqliteWorkspaceStore _workspaces;

    public SqliteProjectStoreTests()
    {
        _store = new SqliteProjectStore(_context.Factory, TimeProvider.System);
        _workspaces = new SqliteWorkspaceStore(_context.Factory);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void Every_database_has_the_default_project()
    {
        _store.List().Should().ContainSingle().Which.Id.Should().Be(ProjectId.Default);
        _store.Get(ProjectId.Default)!.Name.Should().Be("Default");
    }

    [Fact]
    public void Create_and_rename_a_project()
    {
        var created = _store.Create("  Platform  ");

        created.Name.Should().Be("Platform");
        _store.Get(created.Id).Should().Be(created);
        _store.Rename(created.Id, "Platform team").Should().BeTrue();
        _store.Get(created.Id)!.Name.Should().Be("Platform team");
        _store.Rename(new ProjectId("missing"), "x").Should().BeFalse();
        _store.List().Select(p => p.Name).Should().Equal("Default", "Platform team");
        var blank = () => _store.Create(" ");
        blank.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Binding_a_workspace_moves_it_rather_than_adding_a_second_membership()
    {
        var platform = _store.Create("Platform");
        _workspaces.Touch("ws1", "alpha", "/repos/alpha");
        _workspaces.Touch("ws2", "beta", "/repos/beta");

        // Item #373: a folder opened for the first time is in no project, the default one included.
        _store.WorkspacesOf(ProjectId.Default).Should().BeEmpty();
        _store.BindWorkspace("ws2", ProjectId.Default).Should().BeTrue();

        _store.BindWorkspace("ws1", platform.Id).Should().BeTrue();

        _store.WorkspacesOf(platform.Id).Select(w => w.Id).Should().Equal("ws1");
        _store.WorkspacesOf(ProjectId.Default).Select(w => w.Id).Should().Equal("ws2");
        _store.BindWorkspace("ws2", platform.Id).Should().BeTrue();
        _store.WorkspacesOf(ProjectId.Default).Should().BeEmpty("binding moves a folder, it never adds a membership");
        _workspaces.Get("ws1")!.ProjectId.Should().Be(platform.Id.Value);

        _store.BindWorkspace("ws1", new ProjectId("missing")).Should().BeFalse("a workspace cannot be bound to a project that does not exist");
        _store.BindWorkspace("nope", platform.Id).Should().BeFalse();
        _workspaces.Get("ws1")!.ProjectId.Should().Be(platform.Id.Value);
    }
}
