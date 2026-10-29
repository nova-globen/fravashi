using AwesomeAssertions;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Projects;

/// <summary>Backlog 121: a window shows the project of the workspace it is bound to.</summary>
public sealed class WorkspaceCurrentProjectTests
{
    private static BridgeWorkspaceContext WindowOn(string? workspaceId)
    {
        var selection = new WorkspaceSelection();
        selection.Capture(workspaceId);
        return new BridgeWorkspaceContext(new ConfigurationBuilder().Build(), selection);
    }

    [Fact]
    public void A_window_follows_the_project_its_workspace_is_bound_to()
    {
        var workspaces = Substitute.For<IWorkspaceStore>();
        workspaces.Get("ws-1").Returns(new WorkspaceRecord("ws-1", "repo", "C:/repo", DateTimeOffset.UnixEpoch, "p-second"));

        new WorkspaceCurrentProject(WindowOn("ws-1"), workspaces).Id.Should().Be(new ProjectId("p-second"));
    }

    [Fact]
    public void A_studio_run_with_no_bridge_and_no_folder_shows_the_default_project()
    {
        // The host-side dev fallback (Repository:Root): there is no folder record anybody could set up.
        new WorkspaceCurrentProject(WindowOn(null), Substitute.For<IWorkspaceStore>()).Id.Should().Be(DefaultProject.Id);
    }

    [Fact]
    public void A_window_on_a_folder_that_is_not_set_up_shows_no_project()
    {
        // Item #373: no shared bucket. The folder is in no project, and the default is never substituted.
        var workspaces = Substitute.For<IWorkspaceStore>();
        workspaces.Get("ws-1").Returns(new WorkspaceRecord("ws-1", "Stomme", "C:/Stomme", DateTimeOffset.UnixEpoch));

        var current = new WorkspaceCurrentProject(WindowOn("ws-1"), workspaces);

        current.Id.Should().BeNull();
        current.IsSetUp().Should().BeFalse();
        var require = () => current.Require();
        require.Should().Throw<FolderNotSetUpException>().WithMessage("*not set up*");
    }

    [Fact]
    public void A_window_on_a_folder_with_no_record_shows_no_project()
    {
        new WorkspaceCurrentProject(WindowOn("ws-unknown"), Substitute.For<IWorkspaceStore>()).Id.Should().BeNull();
    }

    [Fact]
    public void A_bridged_window_with_no_folder_shows_no_project()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BridgeWorkspaceContext.UrlKey] = "http://host.containers.internal:5091",
                [BridgeWorkspaceContext.TokenKey] = "token",
            })
            .Build();
        var selection = new WorkspaceSelection();
        selection.Capture(null);

        new WorkspaceCurrentProject(new BridgeWorkspaceContext(configuration, selection), Substitute.For<IWorkspaceStore>())
            .Id.Should().BeNull();
    }

    [Fact]
    public void Binding_the_folder_elsewhere_is_seen_without_reopening_the_window()
    {
        var workspaces = Substitute.For<IWorkspaceStore>();
        workspaces.Get("ws-1").Returns(
            new WorkspaceRecord("ws-1", "repo", "C:/repo", DateTimeOffset.UnixEpoch, "p-one"),
            new WorkspaceRecord("ws-1", "repo", "C:/repo", DateTimeOffset.UnixEpoch, "p-two"));
        var current = new WorkspaceCurrentProject(WindowOn("ws-1"), workspaces);

        current.Id.Should().Be(new ProjectId("p-one"));
        current.Id.Should().Be(new ProjectId("p-two"));
    }
}
