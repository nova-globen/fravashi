using AwesomeAssertions;
using Fravashi.Application.Projects;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Projects;

/// <summary>
/// Backlog 121: choosing a project is an act on the WORKSPACE, and a folder with a conversation running cannot
/// be moved, because that would point a running session's tools at another graph between two tool calls.
/// </summary>
public sealed class WorkspaceProjectBindingTests : IDisposable
{
    private readonly SqliteTestContext _context = new();
    private readonly SqliteProjectStore _projects;
    private readonly SqliteWorkspaceStore _workspaces;
    private readonly HashSet<string> _running = [];

    public WorkspaceProjectBindingTests()
    {
        _projects = new SqliteProjectStore(_context.Factory, TimeProvider.System);
        _workspaces = new SqliteWorkspaceStore(_context.Factory);
        _workspaces.Touch("ws-1", "repo", "C:/repo");
    }

    public void Dispose() => _context.Dispose();

    private WorkspaceProjectBinding Binding() => new(_projects, _workspaces, _running.Contains);

    [Fact]
    public void A_folder_can_be_moved_to_another_project_and_every_resolver_sees_it()
    {
        var second = _projects.Create("Second");

        var moved = Binding().Move("ws-1", second.Id);

        moved.Moved.Should().BeTrue(moved.Refusal);
        WorkspaceProjects.Of(_workspaces.Get("ws-1")).Should().Be(second.Id);
    }

    [Fact]
    public void Opening_the_folder_again_does_not_undo_its_project()
    {
        // Home re-registers a recent every time it is opened. If that upsert replaced the row, every choice made
        // here would be undone by the next launch, silently.
        var second = _projects.Create("Second");
        Binding().Move("ws-1", second.Id);

        _workspaces.Touch("ws-1", "repo renamed", "C:/repo");

        WorkspaceProjects.Of(_workspaces.Get("ws-1")).Should().Be(second.Id);
    }

    [Fact]
    public void A_folder_opened_for_the_first_time_is_in_no_project()
    {
        // Item #373: no shared bucket. The folder is recorded and in no project, and nothing resolves the default for it.
        _workspaces.Get("ws-1")!.ProjectId.Should().BeNull();
        WorkspaceProjects.For(_workspaces, "ws-1").Should().BeNull();
        WorkspaceProjects.Place(_workspaces, "ws-1").Should().Be(SessionPlacement.FolderNotSetUp);
        WorkspaceProjects.IsNotSetUp(_workspaces, "ws-1").Should().BeTrue();
        WorkspaceProjects.NewestFolderIn(_workspaces, DefaultProject.Id).Should().BeNull();
        _projects.WorkspacesOf(DefaultProject.Id).Should().BeEmpty();
    }

    [Fact]
    public void Joining_the_default_project_from_a_folder_that_is_not_set_up_is_a_real_move()
    {
        // Item #373 decision 3: the old default-project data is reachable by choosing to join Default, and only so.
        var moved = Binding().Move("ws-1", DefaultProject.Id);

        moved.Moved.Should().BeTrue(moved.Refusal);
        WorkspaceProjects.For(_workspaces, "ws-1").Should().Be(DefaultProject.Id);
        WorkspaceProjects.Place(_workspaces, "ws-1").Should().Be(SessionPlacement.InProject);
        Binding().Move("ws-1", DefaultProject.Id).Refusal.Should().Contain("already in");
    }

    [Fact]
    public void A_folder_with_a_conversation_running_is_not_moved_and_the_reason_is_said()
    {
        var second = _projects.Create("Second");
        _running.Add("ws-1");

        var moved = Binding().Move("ws-1", second.Id);

        moved.Moved.Should().BeFalse();
        moved.Refusal.Should().Contain("conversation running");
        WorkspaceProjects.Of(_workspaces.Get("ws-1")).Should().BeNull("the folder stays as it was: not set up");
    }

    [Fact]
    public void A_new_project_is_made_and_the_folder_moved_into_it_in_one_act()
    {
        var moved = Binding().MoveToNew("ws-1", "  Billing rewrite  ");

        moved.Moved.Should().BeTrue(moved.Refusal);
        var project = _projects.Get(WorkspaceProjects.Of(_workspaces.Get("ws-1"))!);
        project!.Name.Should().Be("Billing rewrite");
    }

    [Fact]
    public void A_running_conversation_stops_a_new_project_from_being_made_at_all()
    {
        // Refused BEFORE the project exists, or every refused attempt would leave an empty project behind.
        _running.Add("ws-1");
        var before = _projects.List().Count;

        Binding().MoveToNew("ws-1", "Never made").Moved.Should().BeFalse();

        _projects.List().Should().HaveCount(before);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_project_with_no_name_is_refused(string name) =>
        Binding().MoveToNew("ws-1", name).Moved.Should().BeFalse();

    [Fact]
    public void A_folder_or_a_project_nobody_knows_is_refused_in_words()
    {
        Binding().Move("nobody", DefaultProject.Id).Refusal.Should().Contain("folder");
        Binding().Move("ws-1", new ProjectId("no-such-project")).Refusal.Should().Contain("project");
    }

    [Fact]
    public void A_folder_is_not_moved_while_one_of_its_worktrees_has_a_conversation_running()
    {
        // Backlog 319: the worktree acts in this folder's project, so its conversation is this folder's as far as a
        // move is concerned.
        var second = _projects.Create("Second");
        _workspaces.Touch("wt-1", "1", "C:/repo.fravashi-worktrees/1");
        _running.Add("wt-1");

        var moved = Binding().Move("ws-1", second.Id);

        moved.Moved.Should().BeFalse();
        moved.Refusal.Should().Contain("conversation running");
        WorkspaceProjects.Of(_workspaces.Get("ws-1")).Should().BeNull("the folder stays as it was: not set up");
        Binding().MoveToNew("ws-1", "Never made").Moved.Should().BeFalse();
    }

    [Fact]
    public void A_worktree_of_another_folder_running_does_not_stop_a_move()
    {
        var second = _projects.Create("Second");
        _workspaces.Touch("wt-other", "1", "C:/other.fravashi-worktrees/1");
        _running.Add("wt-other");

        Binding().Move("ws-1", second.Id).Moved.Should().BeTrue();
    }

    [Fact]
    public void A_worktree_itself_is_never_moved_because_it_follows_its_folder()
    {
        var second = _projects.Create("Second");
        _workspaces.Touch("wt-1", "1", "C:/repo.fravashi-worktrees/1");

        var moved = Binding().Move("wt-1", second.Id);

        moved.Moved.Should().BeFalse();
        moved.Refusal.Should().Contain("repository folder");
        WorkspaceProjects.For(_workspaces, "wt-1").Should().BeNull("its folder is still not set up");
    }

    [Fact]
    public void Moving_a_folder_to_the_project_it_is_already_in_changes_nothing_and_says_so()
    {
        var second = _projects.Create("Second");
        Binding().Move("ws-1", second.Id).Moved.Should().BeTrue();

        var same = Binding().Move("ws-1", second.Id);

        same.Moved.Should().BeFalse();
        same.Refusal.Should().Contain("already");
    }
}
