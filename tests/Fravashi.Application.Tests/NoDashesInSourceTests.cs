using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// The writing rule (CLAUDE.md) forbids em and en dashes everywhere, and until backlog 124 the only thing
/// enforcing it was <c>.githooks/pre-commit</c>, which looks at newly ADDED lines. So everything written
/// before the rule stayed: 993 lines across 278 files, about a third of them text a person reads. It
/// surfaced in the product, where <c>fravashi exit</c> and <c>fravashi status</c> printed one into a console that
/// rendered it as a replacement character.
/// <para>
/// This reads the working tree rather than a list of staged lines, so the rule no longer depends on which
/// lines a commit happened to touch, and it holds on a machine where the hooks were never installed.
/// </para>
/// </summary>
public class NoDashesInSourceTests
{
    // Written as code points, not as characters and not as escapes: this file is inside the tree it
    // checks, and an escape was decoded into the real character on its way to disk the first time.
    private const char EnDash = (char)0x2013;
    private const char EmDash = (char)0x2014;

    private static readonly string[] Roots = ["src", "setup", "tests"];

    /// <summary>
    /// Vendored assets are not ours to edit (ADR 0012), build output is not source, and the agent CLI
    /// fixtures are RECORDINGS of a real run: their bytes are the evidence the parser record is derived from
    /// (ADR 0023), so a dash the model wrote there is data and stays exactly as it was captured.
    /// </summary>
    private static readonly string[] SkippedSegments =
    [
        "/bin/", "/obj/", "/node_modules/", "/wwwroot/vendor/", "/Sessions/Fixtures/",
    ];

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".cshtml", ".js", ".css", ".html", ".json", ".jsonl", ".xml", ".csproj", ".props",
        ".targets", ".sln", ".ps1", ".sh", ".yml", ".yaml", ".md", ".txt", ".iss", ".config", ".editorconfig",
    };

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        (dir is not null).Should().BeTrue("the test must be able to locate the repository root (Fravashi.sln)");
        return dir!.FullName;
    }

    [Fact]
    public void No_source_setup_or_test_file_contains_an_em_or_en_dash()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var top in Roots)
        {
            var folder = Path.Combine(root, top);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var relative = "/" + Path.GetRelativePath(root, path).Replace('\\', '/');
                var isText = TextExtensions.Contains(Path.GetExtension(path))
                    || string.Equals(Path.GetFileName(path), "Dockerfile", StringComparison.Ordinal);
                if (!isText || SkippedSegments.Any(relative.Contains))
                {
                    continue;
                }

                var lineNumber = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNumber++;
                    if (line.Contains(EnDash) || line.Contains(EmDash))
                    {
                        offenders.Add($"{relative.TrimStart('/')}:{lineNumber}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "no em or en dash may appear in source, setup or test files: use a plain hyphen, a comma, a colon "
            + "or parentheses (CLAUDE.md, backlog 124). A recorded fixture is the one exception and it is "
            + "skipped by folder, not by waiving the rule");
    }
}
