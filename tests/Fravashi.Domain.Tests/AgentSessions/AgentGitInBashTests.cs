using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>
/// Work item #323: a Bash call that runs git inside a session's contained copy is refused up front, because the
/// copy has no .git by design and a git init there would make a commit look real and then lose it.
/// </summary>
public sealed class AgentGitInBashTests
{
    [Theory]
    [InlineData("git status")]
    [InlineData("git init")]
    [InlineData("cd workspace && git commit -m \"done\"")]
    [InlineData("dotnet build && git status")]
    [InlineData("git log -1; echo done")]
    [InlineData("GIT_PAGER=cat git log")]
    [InlineData("dotnet build\ngit status")]
    [InlineData("cd /workspaces/a\ngit init\ngit add .")]
    public void A_command_that_runs_git_is_refused(string command)
    {
        AgentGitInBash.RunsGit(command).Should().BeTrue();
        AgentGitInBash.Check("Bash", command).Should().Be(AgentGitInBash.Sentence);
    }

    [Theory]
    [InlineData("dotnet build Fravashi.sln -p:EnableWindowsTargeting=true")]
    [InlineData("dotnet test tests/Fravashi.Launcher.Tests | tail -15")]
    [InlineData("echo 'please use git_commit to commit'")]
    [InlineData("grep -n git CLAUDE.md")]
    [InlineData("ls -la")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_still_goes_on_as_before(string? command)
    {
        AgentGitInBash.RunsGit(command).Should().BeFalse();
        AgentGitInBash.Check("Bash", command).Should().BeNull();
    }

    [Fact]
    public void Only_bash_is_refused_this_way() =>
        AgentGitInBash.Check("Write", "git status").Should().BeNull();

    [Fact]
    public void The_person_is_told_in_a_sentence_of_their_own() =>
        AgentWorkGate.ForThePerson("Hook refused: " + AgentGitInBash.Sentence).Should().Be(AgentGitInBash.ForThePerson);

    [Fact]
    public void The_sentence_and_the_opening_note_both_name_git_commit()
    {
        AgentGitInBash.Sentence.Should().Contain("git_commit");
        AgentGitInBash.OpeningNote.Should().Contain("git_commit");
        AgentGitInBash.Sentence.Should().Contain("has no .git");
        AgentGitInBash.OpeningNote.Should().Contain("has no .git");
    }
}
