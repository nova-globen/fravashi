using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// Backlog 337: one rule decides what counts as "a redirection to /dev/null" (<c>DevNullRedirects</c>, Domain),
/// read today by <c>AgentReadRedirect</c> and the autopilot judge's own command rewrite in
/// <c>AgentApprovalBroker</c>. Held as a build failure rather than trusted to two reviewers' eyes, the same way
/// <see cref="DefaultProjectIsDecidedInOnePlaceTests"/> holds its own single-source rule: the regex's own
/// backreference on its matched target (closing a quote opened earlier in the same alternative) is distinctive
/// regex syntax that no prose comment or doc example would reproduce by accident, so its presence anywhere
/// under <c>src</c> other than the one file that defines the rule is a verbatim second copy of THIS regex. That
/// is a narrower guarantee than "the two can never disagree": a second rule written a different way (a
/// different regex, or a hand-rolled check that never spells out a backreference) would not be caught here.
/// Reads the working tree, like <see cref="NoDashesInSourceTests"/>, because the rule is about what is in the
/// tree and not about which lines a commit touched.
/// </summary>
public class DevNullRuleIsDecidedInOnePlaceTests
{
    // Assembled rather than written out, so this file does not itself contain what it searches for.
    private static readonly string Needle = "/dev/null" + "\\1";

    private const string Allowed = "src/Fravashi.Domain/AgentSessions/DevNullRedirects.cs";

    [Fact]
    public void Nothing_under_src_defines_a_second_dev_null_redirect_regex()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)
                || relative == Allowed)
            {
                continue;
            }

            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                if (line.Contains(Needle, StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}:{number}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "AgentReadRedirect and the autopilot judge's command rewrite must read the SAME /dev/null "
            + "redirect rule (DevNullRedirects), never a second copy that could silently disagree with it");
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
