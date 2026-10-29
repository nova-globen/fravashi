using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>
/// Backlog 271. A file write the pinned CLI refuses after any approval (a name on its protected folder or file list) is
/// refused up front, with the root Dockerfile named as the alternative for the devcontainer names; every other write,
/// and every tool that is not a file write, is left to the gates and the person as before.
/// </summary>
public sealed class AgentCliProtectedWriteTests
{
    private const string Devcontainer = AgentCliProtectedWrite.Sentence + AgentCliProtectedWrite.DevcontainerAlternative;
    private const string Other = AgentCliProtectedWrite.Sentence + AgentCliProtectedWrite.OtherwiseAskAPerson;

    [Theory]
    // #225's own call, verbatim apart from the id.
    [InlineData("Write", "/workspaces/abc/.devcontainer/devcontainer.json")]
    [InlineData("Write", ".devcontainer/devcontainer.json")]
    [InlineData("Edit", "/workspaces/abc/.devcontainer/devcontainer.json")]
    [InlineData("MultiEdit", "/workspaces/abc/.devcontainer/Dockerfile")]
    [InlineData("Write", "/workspaces/abc/.DevContainer/devcontainer.json")] // the CLI compares case-insensitively
    [InlineData("Write", "/workspaces/abc/.devcontainer.json")]
    [InlineData("Write", "/workspaces/abc/.devcontainer./devcontainer.json")] // the CLI strips trailing dots and spaces
    [InlineData("Write", "/workspaces/abc/.devcontainer:stream/devcontainer.json")] // and a colon suffix
    [InlineData("Write", "/workspaces/abc/.devcontainer.json ")]
    [InlineData("Write", "/workspaces/abc/services/api/.devcontainer/devcontainer.json")]
    [InlineData("Write", "C:\\repo\\.devcontainer\\devcontainer.json")]
    [InlineData("NotebookEdit", "/workspaces/abc/.devcontainer/setup.ipynb")]
    public void A_devcontainer_write_is_refused_with_the_dockerfile_alternative(string tool, string path)
    {
        AgentCliProtectedWrite.Check(tool, path).Should().Be(Devcontainer);
    }

    [Theory]
    // One other folder on the CLI's list and one file name (the supervising host widened the refusal to both lists; the
    // plan on #271 records it), and a few more.
    [InlineData("Write", "/workspaces/abc/.vscode/settings.json", ".vscode")]
    [InlineData("Edit", "/workspaces/abc/.npmrc", ".npmrc")]
    [InlineData("Write", "/workspaces/abc/.husky/pre-commit", ".husky")]
    [InlineData("Write", "/workspaces/abc/.claude/settings.json", ".claude")]
    [InlineData("Write", "/workspaces/abc/tools/.Bashrc", ".bashrc")]
    [InlineData("Write", "/workspaces/abc/gradle/wrapper/gradle-wrapper.properties", "gradle-wrapper.properties")]
    [InlineData("Write", "/workspaces/abc/.claude/skills/x/.npmrc", ".claude")] // the folder is caught before the file name is reached
    [InlineData("Write", "/workspaces/abc/.claude/scheduled_tasks.json/x", ".claude")] // the .claude folder is caught whatever follows it
    public void Any_other_name_the_cli_protects_is_refused_and_sent_to_a_person(string tool, string path, string name)
    {
        AgentCliProtectedWrite.ProtectedName(path).Should().Be(name);
        AgentCliProtectedWrite.Check(tool, path).Should().Be(Other);
        AgentCliProtectedWrite.Check(tool, path).Should().NotContain("Dockerfile");
    }

    [Theory]
    // #279: the carve-out for the binary's .claude branch is gone. Only a .claude/skills write was measured (twice,
    // approved, still refused); agents, commands, worktrees and scheduled_tasks.json follow by policy, not
    // measurement. Every .claude path is refused uniformly now, the same as the rest of the list.
    [InlineData("/workspaces/abc/.claude/skills/releasing/SKILL.md")]
    [InlineData("/workspaces/abc/.claude/agents/reviewer.md")]
    [InlineData("/workspaces/abc/.claude/commands/ship.md")]
    [InlineData("/workspaces/abc/.claude/worktrees/w1/notes.txt")]
    [InlineData("/workspaces/abc/.claude/scheduled_tasks.json")]
    [InlineData("/workspaces/abc/.Claude/Skills/x/SKILL.md")]
    public void What_the_cli_may_allow_under_claude_is_refused_too(string path)
    {
        AgentCliProtectedWrite.ProtectedName(path).Should().Be(".claude");
        AgentCliProtectedWrite.Check("Write", path).Should().Be(Other);
    }

    [Theory]
    [InlineData("Write", "/workspaces/abc/.claude/settings.local.json", ".claude")]
    [InlineData("Write", "/workspaces/abc/.claude/hooks/pre.sh", ".claude")]
    public void The_rest_of_claude_is_refused(string tool, string path, string name)
    {
        AgentCliProtectedWrite.ProtectedName(path).Should().Be(name);
        AgentCliProtectedWrite.Check(tool, path).Should().Be(Other);
    }

    [Theory]
    [InlineData("Write", "/workspaces/abc/Dockerfile")]
    [InlineData("Write", "/workspaces/abc/devcontainer.json")]
    [InlineData("Write", "/workspaces/abc/docs/.devcontainer.md")]
    [InlineData("Write", "/workspaces/abc/.devcontainers/x.json")]
    [InlineData("Write", "/workspaces/abc/.github/workflows/ci.yml")] // .github is not .git
    [InlineData("Write", "/workspaces/abc/.gitignore")]
    [InlineData("Write", "/workspaces/abc/docs/npmrc.md")]
    [InlineData("Edit", "/workspaces/abc/src/devcontainer/notes.txt")]
    [InlineData("Write", "")]
    [InlineData("Write", null)]
    public void Any_other_write_is_left_to_the_gates_and_the_person(string tool, string? path)
    {
        AgentCliProtectedWrite.Check(tool, path).Should().BeNull();
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Bash")]
    [InlineData("Glob")]
    [InlineData("mcp__fravashi__wiki_write")]
    [InlineData(null)]
    public void A_tool_that_is_not_a_file_write_is_never_refused_here(string? tool)
    {
        AgentCliProtectedWrite.Check(tool, "/workspaces/abc/.devcontainer/devcontainer.json").Should().BeNull();
    }

    [Fact]
    public void Where_the_path_resolves_counts_as_much_as_how_it_was_written()
    {
        // A link inside the copy to the devcontainer folder must not route around the refusal.
        AgentCliProtectedWrite.Check("Write", "/workspaces/abc/dc/devcontainer.json", ".devcontainer/devcontainer.json")
            .Should().Be(Devcontainer);
    }

    [Fact]
    public void The_sentence_names_the_cli_protection_and_says_not_to_retry()
    {
        AgentCliProtectedWrite.Sentence.Should().Contain("protects this location");
        AgentCliProtectedWrite.Sentence.Should().Contain("Do not retry");
        AgentCliProtectedWrite.DevcontainerAlternative.Should().Contain("Dockerfile at the repository root");
    }

    [Fact]
    public void The_person_is_told_in_a_sentence_of_their_own_for_either_refusal()
    {
        AgentWorkGate.ForThePerson("Hook refused: " + Devcontainer).Should().Be(AgentCliProtectedWrite.ForThePerson);
        AgentWorkGate.ForThePerson("Hook refused: " + Other).Should().Be(AgentCliProtectedWrite.ForThePerson);
    }
}
