using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// No credential-shaped value in any file of the tree: a key written into a draft or a fixture by mistake is found here,
/// before it is committed anywhere. Like <see cref="NoDashesInSourceTests"/> this reads the working tree, so it holds on a
/// machine without the hooks.
/// <para>
/// The redaction tests need values that LOOK like keys. A match passes only when the matched text itself says it is not
/// real (test, fake, dummy, example, or a run of x or 0), so a fixture is written that way or it fails here.
/// </para>
/// </summary>
public class NoSecretsInTreeTests
{
    private static readonly (string Kind, Regex Pattern)[] Shapes =
    [
        ("Anthropic API key", new Regex(@"sk-ant-api[0-9]{2}-[A-Za-z0-9_-]{40,}")),
        ("OpenAI project key", new Regex(@"sk-proj-[A-Za-z0-9_-]{40,}")),
        ("Atlassian API token", new Regex(@"ATATT3[A-Za-z0-9_=-]{40,}")),
        ("GitHub fine-grained token", new Regex(@"github_pat_[A-Za-z0-9_]{40,}")),
        ("GitHub token", new Regex(@"gh[pousr]_[A-Za-z0-9]{36}")),
        ("AWS access key id", new Regex(@"AKIA[0-9A-Z]{16}")),
        ("Telegram bot token", new Regex(@"[0-9]{8,10}:AA[A-Za-z0-9_-]{33}")),
        ("Azure DevOps token", new Regex(@"[A-Za-z0-9]{76}AZDO[A-Za-z0-9]{4}")),
        ("private key", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----\s*(\\n)?\s*[A-Za-z0-9+/]{40,}")),
    ];

    private static readonly Regex Dummy = new(@"test|fake|dummy|example|x{4}|0{4}", RegexOptions.IgnoreCase);

    // "/.tmp/" is the CI run's TMPDIR inside the archive's root: its logs are not the tree, and the runtime's
    // clr-debug-pipe FIFOs there block File.ReadAllBytes forever (a Linux CI run hung on them).
    private static readonly string[] SkippedSegments =
        ["/bin/", "/obj/", "/node_modules/", "/TestResults/", "/.git/", "/wwwroot/vendor/", "/.tmp/"];

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
    public void No_file_in_the_tree_holds_a_credential_shaped_value()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (SkippedSegments.Any(s => ("/" + relative).Contains(s, StringComparison.Ordinal))
                || relative.StartsWith(".claude/settings.local", StringComparison.Ordinal))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(file);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
            {
                continue;
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes);
            foreach (var (kind, pattern) in Shapes)
            {
                foreach (Match match in pattern.Matches(text))
                {
                    if (!Dummy.IsMatch(match.Value))
                    {
                        // The kind and the line, never the value: this message lands in CI logs.
                        var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                        offenders.Add($"{relative}:{line} ({kind})");
                    }
                }
            }
        }

        offenders.Should().BeEmpty("a credential never goes into the repository; revoke it if it ever did");
    }
}
