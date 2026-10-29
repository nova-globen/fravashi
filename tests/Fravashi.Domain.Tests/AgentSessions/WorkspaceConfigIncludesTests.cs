using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>
/// Backlog 289: the work-tree files git's own configuration includes, placed in the copy, and the configuration
/// plane that follows from it. Unlike #288's hooks folder this is a set of FILES, not a folder prefix, and a
/// failed lookup widens all the way rather than to a naming convention, because an include can name anything.
/// </summary>
public sealed class WorkspaceConfigIncludesTests
{
    private const string Top = "C:/Users/me/Repositories/app";
    private const string GitDir = Top + "/.git";

    [Fact]
    public void A_relative_value_resolves_against_the_directory_of_the_file_that_names_it()
    {
        // .git/config's own directory is .git/, so a value of ../config/git.inc climbs out of it to the repo root.
        var resolved = WorkspaceConfigIncludeTarget.Resolve("../config/git.inc", GitDir, Top, "");

        resolved.Should().NotBeNull();
        resolved!.CopyRelativePath.Should().Be("config/git.inc");
    }

    [Fact]
    public void A_nested_includes_value_resolves_against_ITS_OWN_files_directory_not_the_top_ones()
    {
        // config/git.inc including nested.inc means nested.inc, which resolves relative to config/, not .git/.
        var resolved = WorkspaceConfigIncludeTarget.Resolve("nested.inc", Top + "/config", Top, "");

        resolved.Should().NotBeNull();
        resolved!.CopyRelativePath.Should().Be("config/nested.inc");
    }

    [Theory]
    [InlineData("config/git.inc", "", "sub/scripts/config/git.inc")]
    [InlineData("../config/git.inc", "sub/", "config/git.inc")]
    public void A_value_is_placed_relative_to_the_workspace_prefix(string value, string prefix, string copyRelative)
    {
        var resolved = WorkspaceConfigIncludeTarget.Resolve(value, Top + "/sub/scripts", Top, prefix);

        resolved.Should().NotBeNull();
        resolved!.CopyRelativePath.Should().Be(copyRelative);
    }

    [Theory]
    [InlineData("C:/Users/me/elsewhere/x.inc", "", "an absolute path outside the repository")]
    [InlineData("other/x.inc", "sub/", "a file in the repository but beside the workspace folder")]
    public void A_value_that_lands_outside_the_copy_has_no_copy_relative_path(string value, string prefix, string why)
    {
        var resolved = WorkspaceConfigIncludeTarget.Resolve(value, GitDir, Top, prefix);

        resolved.Should().NotBeNull(why);
        resolved!.CopyRelativePath.Should().BeNull(why);
        resolved.AbsolutePath.Should().NotBeNullOrEmpty("the caller still needs it, to keep following the chain");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("~/hooks.inc")]
    [InlineData("../../../../../../x.inc")]
    public void A_value_that_cannot_be_placed_at_all_resolves_to_null(string value) =>
        WorkspaceConfigIncludeTarget.Resolve(value, GitDir, Top, "").Should().BeNull();

    [Fact]
    public void A_named_but_absent_file_is_placed_the_same_way_as_one_that_exists() =>
        // Resolve has no notion of existence at all: creating config/git.inc is the same act as editing it
        // (backlog 289), so the arithmetic for an absent file is identical to a present one.
        WorkspaceConfigIncludeTarget.Resolve("../config/git.inc", GitDir, Top, "")!.CopyRelativePath
            .Should().Be("config/git.inc");

    [Fact]
    public void Of_deduplicates_case_insensitively_and_normalizes_separators()
    {
        var includes = WorkspaceConfigIncludes.Of(["config/git.inc", "CONFIG\\GIT.INC", "/config/git.inc/"]);

        includes.State.Should().Be(WorkspaceConfigIncludesState.Known);
        includes.Files.Should().ContainSingle().Which.Should().Be("config/git.inc");
    }

    [Fact]
    public void Of_with_nothing_is_None() =>
        WorkspaceConfigIncludes.Of([]).Should().Be(WorkspaceConfigIncludes.None);

    [Theory]
    [InlineData("config/git.inc", true)]
    [InlineData("CONFIG/GIT.INC", true)]
    [InlineData("config/git.inc/pre-commit", false)]
    [InlineData("config/app.json", false)]
    public void Contains_is_an_exact_match_never_a_prefix(string path, bool contains) =>
        WorkspaceConfigIncludes.Of(["config/git.inc"]).Contains(path).Should().Be(contains);

    [Fact]
    public void An_included_file_is_configuration_plane()
    {
        var includes = WorkspaceConfigIncludes.Of(["config/git.inc"]);

        var reason = WorkspaceTrustLevels.ConfigurationPlaneReason("config/git.inc", WorkspaceHooksFolder.None, includes);

        reason.Should().Be(WorkspaceTrustLevels.ConfigIncludeReason);
    }

    [Fact]
    public void A_file_beside_an_included_one_is_not_configuration_plane()
    {
        var includes = WorkspaceConfigIncludes.Of(["config/git.inc"]);

        WorkspaceTrustLevels.ConfigurationPlaneReason("config/app.json", WorkspaceHooksFolder.None, includes).Should().BeNull();
    }

    [Fact]
    public void With_no_include_anywhere_the_rule_is_exactly_288s()
    {
        WorkspaceTrustLevels.ConfigurationPlaneReason("config/git.inc", WorkspaceHooksFolder.None, WorkspaceConfigIncludes.None)
            .Should().BeNull();
        WorkspaceTrustLevels.ConfigurationPlaneReason("scripts/hooks/pre-commit", WorkspaceHooksFolder.At("scripts/hooks"), WorkspaceConfigIncludes.None)
            .Should().Be(WorkspaceTrustLevels.HooksFolderReason);
    }

    [Theory]
    [InlineData("config/git.inc")]
    [InlineData("src/Program.cs")]
    [InlineData("docs/readme.md")]
    public void When_the_lookup_failed_every_path_asks_whatever_it_is_named(string path) =>
        WorkspaceTrustLevels.ConfigurationPlaneReason(path, WorkspaceHooksFolder.None, WorkspaceConfigIncludes.Unknown)
            .Should().Be(WorkspaceTrustLevels.UnknownConfigIncludesReason);

    [Theory]
    [InlineData(".githooks/pre-commit")]
    [InlineData("src/.env")]
    public void A_hidden_path_wins_over_every_other_source(string path)
    {
        WorkspaceTrustLevels.ConfigurationPlaneReason(path, WorkspaceHooksFolder.None, WorkspaceConfigIncludes.Unknown)
            .Should().Be(string.Empty);
        WorkspaceTrustLevels.ConfigurationPlaneReason(path, WorkspaceHooksFolder.None, WorkspaceConfigIncludes.Of([path]))
            .Should().Be(string.Empty);
    }

    [Fact]
    public void An_included_file_that_is_also_under_the_hooks_folder_reports_the_include_reason()
    {
        // Both checks fire on the same path; the include reason is reported first, because there is no narrower
        // naming convention for it the way there is for a hooks folder - it is the more specific of the two.
        var includes = WorkspaceConfigIncludes.Of(["scripts/hooks/git.inc"]);

        WorkspaceTrustLevels.ConfigurationPlaneReason("scripts/hooks/git.inc", WorkspaceHooksFolder.At("scripts/hooks"), includes)
            .Should().Be(WorkspaceTrustLevels.ConfigIncludeReason);
    }
}
