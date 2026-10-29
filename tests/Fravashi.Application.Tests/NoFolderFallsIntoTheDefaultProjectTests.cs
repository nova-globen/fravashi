using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// Item #373, no shared bucket (<c>docs/plans/first-open-setup.md</c> section 1.3), held as a build failure. A folder
/// nobody set up belongs to NO project; the one helper that turned "nobody bound this" into "default" is gone, and the
/// default project's id may be resolved only by the code that genuinely means the seeded project: the resolution file
/// itself (a hand-built single-project host), the window's dev fallback with no bridge, and the framework seed. Beside
/// <see cref="DefaultProjectIsDecidedInOnePlaceTests"/>, which guards the domain constant the same way.
/// </summary>
public class NoFolderFallsIntoTheDefaultProjectTests
{
    // Assembled rather than written out, so this file does not contain what it searches for.
    private static readonly string DefaultId = "DefaultProject" + "." + "Id";
    private static readonly string RemovedHelper = "DefaultProject" + "." + "Of(";

    private static readonly string[] Allowed =
    [
        "src/Fravashi.Application/Projects/ProjectResolution.cs",
        "src/Fravashi.Infrastructure/Bridge/WorkspaceCurrentProject.cs",
        "src/Fravashi.Web/Program.cs",
    ];

    [Fact]
    public void Nothing_under_src_resolves_the_default_project_for_a_folder()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)
                || !(file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                var code = line.TrimStart();
                if (code.StartsWith("///", StringComparison.Ordinal) || code.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains(RemovedHelper, StringComparison.Ordinal)
                    || (line.Contains(DefaultId, StringComparison.Ordinal) && !Allowed.Contains(relative, StringComparer.Ordinal)))
                {
                    offenders.Add($"{relative}:{number}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a folder that is not set up is in no project (ICurrentProject.Id and WorkspaceProjects.For answer null) and "
            + "shows the 'not set up' state; resolving the default project for it is the shared bucket item #373 removed");
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
