using AwesomeAssertions;
using Fravashi.Infrastructure.Search;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Search;

/// <summary>
/// The studio's own reading of gitignore rules, which the search fallback, the search ignore settings and the sync
/// engine's synchronous first answer all use. Character classes were missing until 2026-09-29: the Visual Studio
/// template ignores build output through <c>[Dd]ebug/</c> and <c>[Rr]elease/</c>, the class became literal text, and
/// 6192 build files of this repository had been synced into the agent's copy (found by backlog 161's first start).
/// </summary>
public sealed class GitignoreMatcherTests
{
    [Theory]
    [InlineData("[Dd]ebug/", "src/App/bin/Debug/net10.0/App.dll", true)]
    [InlineData("[Dd]ebug/", "src/App/obj/debug/App.cs", true)]
    [InlineData("[Rr]elease/", "src/App/bin/Release/App.dll", true)]
    [InlineData("[Dd]ebug/", "src/App/bin/Xebug/App.dll", false)]
    [InlineData("*.[oa]", "lib/thing.o", true)]
    [InlineData("*.[oa]", "lib/thing.c", false)]
    [InlineData("log[0-9].txt", "log7.txt", true)]
    [InlineData("log[0-9].txt", "logx.txt", false)]
    [InlineData("file[!0-9]", "filex", true)]
    [InlineData("file[!0-9]", "file3", false)]
    [InlineData("file[^0-9]", "filex", true)]
    [InlineData("a[]]b", "a]b", true)]
    [InlineData("a[!/]b", "a/b", false)]
    [InlineData("weird[name", "weird[name", true)]
    public void A_character_class_matches_as_git_reads_it(string rule, string path, bool ignored) =>
        GitignoreMatcher.FromRuleText(rule).IsIgnored(path, isDirectory: false).Should().Be(ignored, "rule {0} on {1}", rule, path);

    [Fact]
    public void This_repositorys_own_rules_ignore_its_build_output()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), ".gitignore"));
        var rules = GitignoreMatcher.FromRuleText(text);

        rules.IsIgnored("src/Fravashi.Web/bin/Debug/net10.0/Fravashi.Web.dll", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("tests/Fravashi.Web.Tests/obj/Release/net10.0/x.cs", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("src/Fravashi.Web/Program.cs", isDirectory: false).Should().BeFalse();
        rules.IsIgnored("setup/release/variants.json", isDirectory: false).Should().BeFalse("the file re-includes it");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binaries.");
    }
}
