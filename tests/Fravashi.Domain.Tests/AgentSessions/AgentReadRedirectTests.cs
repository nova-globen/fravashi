using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>
/// Backlog 166. The commands an agent inside Fravashi actually sent through Bash to read, in the two self-hosting
/// trials, are sent back to Read, Grep and Glob; a command that builds, writes, runs or does anything this rule is
/// unsure of still goes to a person.
/// </summary>
public sealed class AgentReadRedirectTests
{
    [Theory]
    // Trial 1 and trial 2, verbatim.
    [InlineData("ls /workspaces/9e4d0a84e941ace1/src/Fravashi.Launcher/ && ls /workspaces/9e4d0a84e941ace1/src/Fravashi.Launcher/*/")]
    [InlineData("sed -n '60,95p' src/Fravashi.Desktop/Program.cs; echo ---; sed -n '165,200p' src/Fravashi.Desktop/DesktopApplicationContext.cs")]
    [InlineData("grep -rn \"InternalsVisibleTo\" --include=*.props --include=*.csproj --include=*.cs src/ Directory.Build.props | head -20")]
    [InlineData("grep -n \"VpnLaunch\\|vpnLaunch\\|launch = \\|launch;\" src/Fravashi.Launcher/LauncherApp.cs")]
    [InlineData("sed -n '1,80p' tests/Fravashi.Application.Tests/BacklogIntegrityTests.cs")]
    [InlineData("grep -n \"open items\\|items written\\|In Review\" docs/backlog.md | head -30")]
    [InlineData("grep -n \"BrokerBaked\" CLAUDE.md")]
    [InlineData("cd src && cat README.md")]
    [InlineData("find . -name '*.csproj' | sort")]
    // Backlog 337: agents append a discard to /dev/null to a read constantly, and it does not disqualify one.
    [InlineData("grep -rn x src 2>/dev/null")]
    [InlineData("ls -la . 2>/dev/null")]
    // The exact command ADR 0031 decision 8's 2026-10-17 addendum measured the judge refusing before #337:
    // now it never reaches the judge at all, since every segment reads and the trailing /dev/null is stripped.
    [InlineData("ls -la . && echo --- && cat ./README.md 2>/dev/null")]
    public void A_command_that_only_reads_is_sent_back(string command)
    {
        AgentReadRedirect.OnlyReads(command).Should().BeTrue();
        AgentReadRedirect.Check("Bash", command).Should().Be(AgentReadRedirect.Sentence);
    }

    [Theory]
    [InlineData("dotnet --version && ls")] // it runs something
    [InlineData("dotnet build Fravashi.sln -p:EnableWindowsTargeting=true 2>&1 | tail -15")]
    [InlineData("dotnet test tests/Fravashi.Launcher.Tests | grep -E \"Passed!|Failed!\"")]
    [InlineData("sed -i 's/old/new/' src/thing.cs")]
    [InlineData("sed 's/old/new/' src/thing.cs")] // not the -n print form, so left to a person
    [InlineData("find . -name '*.tmp' -delete")]
    [InlineData("find . -name '*.sh' -exec chmod +x {} \\;")]
    [InlineData("cat a.txt > b.txt")]
    // Backlog 337: every redirection OTHER than a discard to exactly /dev/null still disqualifies a command.
    [InlineData("grep x src > out.txt")]
    [InlineData("cat a >> b")]
    [InlineData("grep x $(cat list.txt)")]
    [InlineData("grep x `cat list.txt`")]
    [InlineData("sort -o out.txt in.txt")]
    [InlineData("ls &")]
    [InlineData("echo hello")] // prints, reads nothing
    [InlineData("cd src")]
    [InlineData("npm ls")]
    [InlineData("")]
    // Work item #340: pwd is in Neutral, never Reading, so a bare pwd must never be redirected to Read, Grep
    // or Glob, none of which can answer "what is the current directory". It is answered by the work gate's
    // own AgentWorkGate.IsBarePwd instead, not by joining this list.
    [InlineData("pwd")]
    [InlineData("pwd -P")]
    public void Anything_else_still_goes_to_a_person(string command)
    {
        AgentReadRedirect.OnlyReads(command).Should().BeFalse();
        AgentReadRedirect.Check("Bash", command).Should().BeNull();
    }

    [Fact]
    public void Only_bash_is_redirected() =>
        AgentReadRedirect.Check("Read", "cat README.md").Should().BeNull();

    [Fact]
    public void The_person_is_told_in_a_sentence_of_their_own() =>
        AgentWorkGate.ForThePerson("Hook refused: " + AgentReadRedirect.Sentence).Should().Be(AgentReadRedirect.ForThePerson);
}
