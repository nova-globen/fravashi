using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// Backlog 121's third acceptance clause, held as a build failure rather than a review comment: nothing under
/// <c>src</c> resolves the default project except the code that decides what the default IS. Before this, 22
/// places across 13 files named it directly, which is how a second project would have been invisible to the
/// board, the sync page, the gates, the git work and every agent tool at once. It reads the working tree, like
/// <see cref="NoDashesInSourceTests"/>, because the rule is about what is in the tree and not about which lines
/// a commit touched.
/// </summary>
public class DefaultProjectIsDecidedInOnePlaceTests
{
    // Assembled rather than written out, so this file does not contain what it searches for.
    private static readonly string Needle = "ProjectId" + "." + "Default";

    /// <summary>The definition, and the one place allowed to decide with it.</summary>
    private static readonly string[] Allowed =
    [
        "src/Fravashi.Domain/Projects/ProjectId.cs",
        "src/Fravashi.Application/Projects/ProjectResolution.cs",
    ];

    [Fact]
    public void Nothing_under_src_names_the_default_project_but_the_code_that_decides_it()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)
                || !(file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                || Allowed.Contains(relative, StringComparer.Ordinal))
            {
                continue;
            }

            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;

                // A doc comment may MENTION it (a cref explaining what a workspace joins by default); only code
                // that resolves with it is the defect.
                if (line.Contains(Needle, StringComparison.Ordinal) && !line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}:{number}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a surface follows the project of its workspace (ICurrentProject) and an agent call the project of its "
            + "session (the call's Project, or a session link's own); naming the default directly makes a second "
            + "project invisible to that surface");
    }

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
}
