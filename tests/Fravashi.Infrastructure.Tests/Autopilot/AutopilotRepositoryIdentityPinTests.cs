using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Domain.Autopilot;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Autopilot;

/// <summary>
/// ADR 0031 decision 16's host addendum, #307: the per-repository cap depends on
/// <see cref="AutopilotRepositoryIdentity.WorktreeContainerSuffix"/> matching
/// <c>BridgeWorktreeService.ContainerSuffix</c> exactly, the two literals that keep the Domain's identity
/// derivation agreeing with what the Bridge actually names a worktree's sibling folder. The two projects
/// share no reference (<c>Fravashi.Bridge</c> is Windows-only, hosted by fravashi.exe; <c>Fravashi.Domain</c>
/// ships inside the Linux container), so nothing but THIS test holds them together: a rename on either side
/// would otherwise silently stop every worktree from being grouped with its repository, with no other test
/// going red. Shaped after <c>AgentCliPinTests</c>' own habit of reading a sibling project's real source
/// rather than trusting a comment to keep two values equal.
/// </summary>
public sealed class AutopilotRepositoryIdentityPinTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test must be able to locate the repository root (Fravashi.sln)");
        return dir!.FullName;
    }

    [Fact]
    public void The_worktree_container_suffix_matches_the_bridges_own_literal()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "Fravashi.Bridge", "BridgeWorktreeService.cs");
        File.Exists(path).Should().BeTrue($"expected to find {path}");
        var source = File.ReadAllText(path);

        var match = Regex.Match(source, @"public const string ContainerSuffix = ""([^""]+)"";");
        match.Success.Should().BeTrue("BridgeWorktreeService.ContainerSuffix must be a simple const string literal");

        match.Groups[1].Value.Should().Be(AutopilotRepositoryIdentity.WorktreeContainerSuffix);
    }
}
