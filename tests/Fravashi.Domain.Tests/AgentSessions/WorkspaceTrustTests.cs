using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>ADR 0028, backlog 152: the rules that bound the Edits level, read as written.</summary>
public sealed class WorkspaceTrustTests
{
    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("MultiEdit")]
    [InlineData("NotebookEdit")]
    public void The_four_editing_tools_are_edits_by_exact_name(string tool) =>
        WorkspaceTrustLevels.IsEdit(tool).Should().BeTrue();

    [Theory]
    [InlineData("Bash")]
    [InlineData("write")]
    [InlineData("EDIT")]
    [InlineData("Agent")]
    [InlineData("mcp__fravashi__wiki_write")]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_else_is_an_edit_whatever_it_writes(string? tool) =>
        WorkspaceTrustLevels.IsEdit(tool).Should().BeFalse();

    [Theory]
    [InlineData(".githooks/pre-commit")]
    [InlineData(".forgejo/workflows/ci.yml")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".claude/settings.json")]
    [InlineData(".npmrc")]
    [InlineData(".env")]
    [InlineData("src/.hidden/x.cs")]
    [InlineData(@"src\.vscode\tasks.json")]
    public void A_hidden_path_is_the_configuration_plane_and_stays_asked(string path) =>
        WorkspaceTrustLevels.IsHiddenPath(path).Should().BeTrue();

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("README.md")]
    [InlineData("docs/plans/x.md")]
    [InlineData("Directory.Build.props")]
    [InlineData("src/a.b/c.cs")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_path_is_the_code_and_may_be_trusted(string? path) =>
        WorkspaceTrustLevels.IsHiddenPath(path).Should().BeFalse();

    [Fact]
    public void A_level_round_trips_through_its_stored_name_and_an_unknown_name_asks()
    {
        WorkspaceTrustLevels.Parse(WorkspaceTrustLevels.Name(WorkspaceTrust.Edits)).Should().Be(WorkspaceTrust.Edits);
        WorkspaceTrustLevels.Parse(WorkspaceTrustLevels.Name(WorkspaceTrust.Ask)).Should().Be(WorkspaceTrust.Ask);
        WorkspaceTrustLevels.Parse("EDITS").Should().Be(WorkspaceTrust.Edits);

        // A value this build does not know (a later level, a typo, a tampered row) must read as the level
        // that asks, never as one that runs.
        WorkspaceTrustLevels.Parse("commands").Should().Be(WorkspaceTrust.Ask);
        WorkspaceTrustLevels.Parse("everything").Should().Be(WorkspaceTrust.Ask);
        WorkspaceTrustLevels.Parse(null).Should().Be(WorkspaceTrust.Ask);
    }
}
