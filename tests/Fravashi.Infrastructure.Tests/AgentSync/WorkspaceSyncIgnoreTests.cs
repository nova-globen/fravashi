using Fravashi.Infrastructure.AgentSync;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentSync;

/// <summary>Plan 2d proposed decisions 1+3: never .git, never gitignored content.</summary>
public class WorkspaceSyncIgnoreTests
{
    [Theory]
    [InlineData(".git", true)]
    [InlineData(".git/HEAD", false)]
    [InlineData("src/module/.git", false)]
    [InlineData(".github/workflows/ci.yml", false)]
    public void Git_directories_never_sync_but_dotgithub_does(string path, bool isDirectory)
    {
        var ignore = WorkspaceSyncIgnore.FromGitignoreText(null);

        var expected = path.StartsWith(".github", StringComparison.Ordinal);
        ignore.ShouldSync(path, isDirectory).Should().Be(expected);
    }

    [Fact]
    public void Gitignored_content_does_not_sync_but_plain_files_do()
    {
        var ignore = WorkspaceSyncIgnore.FromGitignoreText("node_modules/\nbin/\n*.log\n");

        ignore.ShouldSync("node_modules", isDirectory: true).Should().BeFalse();
        ignore.ShouldSync("src/app/node_modules", isDirectory: true).Should().BeFalse();
        ignore.ShouldSync("debug.log", isDirectory: false).Should().BeFalse();
        ignore.ShouldSync("src/Program.cs", isDirectory: false).Should().BeTrue();
        ignore.ShouldSync("README.md", isDirectory: false).Should().BeTrue();
    }

    [Fact]
    public void The_root_itself_is_not_a_sync_item()
    {
        WorkspaceSyncIgnore.FromGitignoreText(null).ShouldSync("", isDirectory: true).Should().BeFalse();
    }
}
