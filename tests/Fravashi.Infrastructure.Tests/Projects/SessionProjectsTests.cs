using AwesomeAssertions;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Projects;

/// <summary>
/// Backlog 121: an agent session acts on the project of the workspace it runs in, resolved through the session
/// index and the workspace records, and an unknown session resolves NOTHING rather than the default.
/// </summary>
public sealed class SessionProjectsTests
{
    private static readonly AgentSessionId Session = new(Guid.NewGuid());

    private static SessionRecord In(string workspaceId) => new(
        Session, workspaceId, AgentTool.Claude, "a conversation", null, AgentSessionState.Running,
        DateTimeOffset.UnixEpoch, null);

    [Fact]
    public void A_session_is_in_the_project_its_workspace_is_bound_to()
    {
        var sessions = Substitute.For<ISessionStore>();
        sessions.Find(Session).Returns(In("ws-1"));
        var workspaces = Substitute.For<IWorkspaceStore>();
        workspaces.Get("ws-1").Returns(new WorkspaceRecord("ws-1", "repo", "C:/repo", DateTimeOffset.UnixEpoch, "p-second"));

        new SessionProjects(sessions, workspaces).For(Session).Should().Be(new ProjectId("p-second"));
    }

    [Fact]
    public void A_session_the_studio_does_not_know_has_no_project_at_all()
    {
        // Not the default: an unknown session acting on SOME graph is the failure this exists to prevent.
        var sessions = Substitute.For<ISessionStore>();
        sessions.Find(Session).Returns((SessionRecord?)null);

        new SessionProjects(sessions, Substitute.For<IWorkspaceStore>()).For(Session).Should().BeNull();
    }

    [Fact]
    public void A_session_whose_workspace_has_no_record_is_in_no_project()
    {
        // Item #373, no shared bucket: the default is never quietly substituted, so the tool server refuses in words.
        var sessions = Substitute.For<ISessionStore>();
        sessions.Find(Session).Returns(In("never-recorded"));
        var workspaces = Substitute.For<IWorkspaceStore>();
        workspaces.Get("never-recorded").Returns((WorkspaceRecord?)null);

        new SessionProjects(sessions, workspaces).For(Session).Should().BeNull();
        new SessionProjects(sessions, workspaces).Place(Session).Should().Be(SessionPlacement.Unknown);
    }

    [Fact]
    public void A_session_in_a_folder_that_is_not_set_up_is_in_no_project_and_says_why()
    {
        var sessions = Substitute.For<ISessionStore>();
        sessions.Find(Session).Returns(In("ws-core"));
        var workspaces = Substitute.For<IWorkspaceStore>();
        workspaces.Get("ws-core").Returns(new WorkspaceRecord("ws-core", "Stomme", "C:/Stomme", DateTimeOffset.UnixEpoch));

        new SessionProjects(sessions, workspaces).For(Session).Should().BeNull();
        new SessionProjects(sessions, workspaces).Place(Session).Should().Be(SessionPlacement.FolderNotSetUp);
    }

    [Fact]
    public void The_project_is_read_at_each_call_so_a_rebinding_is_seen()
    {
        var sessions = Substitute.For<ISessionStore>();
        sessions.Find(Session).Returns(In("ws-1"));
        var workspaces = Substitute.For<IWorkspaceStore>();
        workspaces.Get("ws-1").Returns(
            new WorkspaceRecord("ws-1", "repo", "C:/repo", DateTimeOffset.UnixEpoch, "p-one"),
            new WorkspaceRecord("ws-1", "repo", "C:/repo", DateTimeOffset.UnixEpoch, "p-two"));
        var projects = new SessionProjects(sessions, workspaces);

        projects.For(Session).Should().Be(new ProjectId("p-one"));
        projects.For(Session).Should().Be(new ProjectId("p-two"));
    }
}
