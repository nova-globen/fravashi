using System.Text.RegularExpressions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 277: page titles had drifted to two different separators (a middle dot and a dash) and one
/// page had none at all. This reads the working tree of src/Fravashi.Web rather than rendering every
/// page, so a future page with its own PageTitle is checked without needing a bunit host for it. Since the rename to
/// Fravashi every title names the product through ProductName.Short, the one place the name is said.
/// </summary>
public class PageTitlesTests
{
    private const string ExpectedPrefix = "@(ProductName.Short) · ";

    private static readonly Regex PageTitlePattern =
        new("<PageTitle>(.*?)</PageTitle>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "the test must be able to locate the repository root (Fravashi.sln)");
        return dir!.FullName;
    }

    [Fact]
    public void Every_page_title_in_the_web_project_starts_with_the_product_name_and_a_middle_dot()
    {
        var root = RepositoryRoot();
        var webFolder = Path.Combine(root, "src", "Fravashi.Web");
        var offenders = new List<string>();
        var found = 0;

        foreach (var path in Directory.EnumerateFiles(webFolder, "*.razor", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var text = File.ReadAllText(path);
            var matches = PageTitlePattern.Matches(text);

            // A stray "<PageTitle" the pattern above fails to pair with its closing tag must fail loudly
            // rather than silently go unchecked.
            var opened = Regex.Matches(text, "<PageTitle(?=[\\s>])").Count;
            Assert.True(matches.Count == opened,
                $"{relative}: found {opened} <PageTitle> opening tag(s) but only matched {matches.Count}; "
                + "check for one that does not close on the same scan");

            foreach (Match match in matches)
            {
                found++;
                var title = match.Groups[1].Value.Trim();
                if (!title.StartsWith(ExpectedPrefix, StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}: \"{title}\"");
                }
            }
        }

        Assert.True(found > 0, "the scan must actually find PageTitle elements, or it is checking nothing");
        Assert.True(offenders.Count == 0,
            "every PageTitle in src/Fravashi.Web must read \"@(ProductName.Short) · <page>\" (backlog 277): "
            + string.Join(", ", offenders));
    }
}
