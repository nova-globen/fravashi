using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>
/// Backlog 288: where git runs a workspace's hooks from, placed in the copy, and the configuration plane that follows
/// from it. Every doubt lands on the side that asks.
/// </summary>
public sealed class WorkspaceHooksFolderTests
{
    private const string Top = "C:/Users/me/Repositories/app";

    [Theory]
    [InlineData("scripts/hooks", "", "scripts/hooks")]
    [InlineData("./scripts/hooks/", "", "scripts/hooks")]
    [InlineData("scripts\\hooks", "", "scripts/hooks")]
    [InlineData("tools/../scripts/hooks", "", "scripts/hooks")]
    [InlineData("C:/Users/me/Repositories/app/scripts/hooks", "", "scripts/hooks")]
    [InlineData("c:\\users\\me\\repositories\\APP\\scripts\\hooks", "", "scripts/hooks")]
    [InlineData("../app/scripts/hooks", "", "scripts/hooks")]
    [InlineData("sub/scripts/hooks", "sub/", "scripts/hooks")]
    [InlineData("sub", "sub/", "")]
    [InlineData(".", "sub/", "")]
    [InlineData(".", "", "")]
    public void A_hooks_path_inside_the_repository_is_placed_in_the_copy(string hooksPath, string prefix, string folder)
    {
        var hooks = WorkspaceHooksFolder.Resolve(hooksPath, Top, prefix);

        hooks.State.Should().Be(WorkspaceHooksFolderState.At);
        hooks.Folder.Should().Be(folder);
    }

    [Theory]
    [InlineData(null, "", "unset leaves ADR 0028's rule as it was")]
    [InlineData("", "", "empty is unset")]
    [InlineData("C:/Users/me/hooks", "", "an absolute path outside the repository")]
    [InlineData("/etc/git-hooks", "", "a path on another root")]
    [InlineData("../shared-hooks", "", "a relative climb out of the repository")]
    [InlineData("../../../../../../x", "", "a climb past the start")]
    [InlineData("~/hooks", "", "a home path git left unexpanded")]
    [InlineData("other/hooks", "sub/", "a folder in the repository but beside the workspace folder")]
    public void A_hooks_path_that_lands_outside_the_copy_changes_nothing(string? hooksPath, string prefix, string why)
    {
        var hooks = WorkspaceHooksFolder.Resolve(hooksPath, Top, prefix);

        hooks.Should().Be(WorkspaceHooksFolder.None, why);
        WorkspaceTrustLevels.ConfigurationPlaneReason("scripts/hooks/pre-commit", hooks, WorkspaceConfigIncludes.None).Should().BeNull();
    }

    [Fact]
    public void A_posix_repository_root_resolves_the_same_way() =>
        WorkspaceHooksFolder.Resolve("/home/me/app/scripts/hooks", "/home/me/app", "").Folder.Should().Be("scripts/hooks");

    [Theory]
    [InlineData("scripts/hooks/pre-commit", true)]
    [InlineData("scripts/hooks", true)]
    [InlineData("Scripts/Hooks/pre-push", true)]
    [InlineData("scripts/hooks/lib/common.sh", true)]
    [InlineData("scripts/build.sh", false)]
    [InlineData("scripts/hooksmith/x", false)]
    [InlineData("src/Program.cs", false)]
    public void Every_path_under_the_configured_folder_is_configuration_plane(string path, bool plane)
    {
        var reason = WorkspaceTrustLevels.ConfigurationPlaneReason(path, WorkspaceHooksFolder.At("scripts/hooks"), WorkspaceConfigIncludes.None);

        if (plane)
        {
            reason.Should().Be(WorkspaceTrustLevels.HooksFolderReason);
            reason.Should().Contain("git runs this folder's scripts on the host at the next commit");
        }
        else
        {
            reason.Should().BeNull();
        }
    }

    [Fact]
    public void A_hooks_folder_holding_the_whole_copy_makes_every_path_configuration_plane() =>
        WorkspaceTrustLevels.ConfigurationPlaneReason("src/Program.cs", WorkspaceHooksFolder.At(""), WorkspaceConfigIncludes.None)
            .Should().Be(WorkspaceTrustLevels.HooksFolderReason);

    [Theory]
    [InlineData("hooks/pre-commit")]
    [InlineData("githooks/pre-commit")]
    [InlineData("tools/git-hooks/commit-msg")]
    [InlineData("scripts/Hooks/x")]
    public void When_the_lookup_failed_a_path_that_looks_like_a_hooks_folder_asks(string path) =>
        WorkspaceTrustLevels.ConfigurationPlaneReason(path, WorkspaceHooksFolder.Unknown, WorkspaceConfigIncludes.None)
            .Should().Be(WorkspaceTrustLevels.UnknownHooksFolderReason);

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("scripts/build.sh")]
    [InlineData("docs/hooks.md")]
    public void When_the_lookup_failed_an_ordinary_path_is_not_configuration_plane(string path) =>
        WorkspaceTrustLevels.ConfigurationPlaneReason(path, WorkspaceHooksFolder.Unknown, WorkspaceConfigIncludes.None).Should().BeNull();

    [Theory]
    [InlineData(".githooks/pre-commit")]
    [InlineData("src/.env")]
    public void A_hidden_path_is_configuration_plane_whatever_git_says(string path)
    {
        WorkspaceTrustLevels.ConfigurationPlaneReason(path, WorkspaceHooksFolder.None, WorkspaceConfigIncludes.None).Should().NotBeNull();
        WorkspaceTrustLevels.ConfigurationPlaneReason(path, WorkspaceHooksFolder.At("scripts/hooks"), WorkspaceConfigIncludes.None).Should().NotBeNull();
    }
}
