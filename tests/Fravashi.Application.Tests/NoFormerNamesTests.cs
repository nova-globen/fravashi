using System.Diagnostics;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// The owner's rule of 2026-10-27: no trace of the product's former names, nor of the sister product's former name, in
/// any form, anywhere in the tree (<c>docs/plans/rename-internal-names.md</c> is the plan). Like
/// <see cref="NoDashesInSourceTests"/> this reads the working tree, paths as well as text, so it holds whichever lines a
/// commit touched. What git ignores (build output, the local archive, a downloaded setup) is not the tree.
/// <para>
/// The names are matched as patterns built from pieces, so this file does not carry them either. The company domain
/// stays (only its old product host goes, on infra's instructions), and a word that merely contains the letters is not
/// the name ("candidate" contains them).
/// </para>
/// </summary>
public class NoFormerNamesTests
{
    private static readonly string D = "d" + "i" + "d" + "a";
    private static readonly string K = "d" + "e" + "c" + "k";

    /// <summary>The company's domain, which merely contains the first piece: built from pieces too, so this file names no
    /// host either.</summary>
    private static readonly string Company = D + "bit.com";

    /// <summary>The former names, case-insensitive: the first piece anywhere except inside "candida..." and the
    /// company domain; the second as a word or a name part (alone, leading a type name, after a hyphen, before an
    /// underscore).</summary>
    private static readonly Regex Former = new(
        $"(?<!can){D}(?!bit\\.com|bit\\b)|(?<![a-z]){K}|{K}(?![a-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Vendored third-party assets (ADR 0012) carry their own words (Monaco's event names, a German word) and are never edited.
    /// The rest are the transition: the plan that names what it removes, the first-half plan, and the one file the
    /// bridge release reads old installs through. The cleanup release (phase 5) deletes these exceptions.
    /// </summary>
    private static readonly string[] Allowed =
    [
        "src/Fravashi.Web/wwwroot/vendor/monaco/",
        "src/Fravashi.Web/wwwroot/vendor/material-icons/",
        "docs/plans/rename-internal-names.md",
        "docs/plans/rename-to-fravashi.md",
        "src/Fravashi.Domain/FormerNames.cs",
    ];

    /// <summary>
    /// THE RECORD: names of external systems that existed under the old value and were retired with the rename's last
    /// infrastructure wave (2026-10-30, the old forge and hosts with it). They may stay ONLY where the tree is a
    /// record of what happened, the ADRs and the dated evidence (infra's ruling of 2026-10-30), and nowhere else. Until
    /// then this was a pending list allowed in any file.
    /// </summary>
    private static readonly string[] RecordOnly =
    [
        // The product's old host (the broker, the update channel and the website before F1).
        K + "." + Company,
        // Infra's workflow that promoted the update channel.
        "pin-" + K + "-update-channel",
        // The forge Actions secret the live prompt-audit tier once read.
        "NG_JEV_" + K.ToUpperInvariant() + "_PROMPT_AUDIT",
        // The broker's site, registry image and role at infra before F1.
        D + K + "-broker",
        // A throwaway repository on the forge the delivery proofs pushed to.
        K + "-delivery-proof",
    ];

    /// <summary>Where the record names may stay: the ADRs and the dated evidence, both records of what happened.</summary>
    private static readonly string[] RecordFolders = ["docs/adr/", "docs/evidences/"];

    /// <summary>
    /// THE TRANSITION LIST: exact (file, token) pairs for files that cannot read FormerNames (scripts, a Dockerfile) and
    /// must still carry a former name for the bridge release. Each says why; the cleanup release (phase 5) empties it.
    /// </summary>
    private static readonly (string File, string Token)[] Transition =
    [
        // An old fravashi.exe updates the image alone and refuses an image whose variant label (under its former name)
        // is not its own, before it touches the running studio: this label is how the new image is refused by it.
        ("Dockerfile", "org." + D + K + ".variant"),
        // The installer reads the container engine pin from the former data folder while fravashi.exe has not moved
        // it yet (the dev host, where the move failed), so the image loads into the engine the install uses.
        ("setup/install/install-windows.ps1", "\"" + char.ToUpperInvariant(D[0]) + D[1..] + char.ToUpperInvariant(K[0]) + K[1..] + "\""),
        // The public repository's CI refuses the old product stems, and its step (the publishing-public-source skill's,
        // verbatim) lists them in its pattern; the stems themselves are assembled from fragments there.
        (".github/workflows/ci.yml", "(core|" + K + "|bin|game|code)"),
    ];

    // "/.tmp/" is the CI run's TMPDIR inside the archive's root: its logs are not the tree, and the runtime's
    // clr-debug-pipe FIFOs there block File.ReadAllBytes forever (a Linux CI run hung on them).
    private static readonly string[] SkippedSegments =
        ["/bin/", "/obj/", "/node_modules/", "/TestResults/", "/.git/", "/.tmp/"];

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

    /// <summary>
    /// The tree's files, relative and with forward slashes: what git tracks or would track (ignored files are not the
    /// tree), or, where there is no git (a CI run from an archive), every file under the root.
    /// </summary>
    private static IEnumerable<string> TreeFiles(string root)
    {
        if (Directory.Exists(Path.Combine(root, ".git")) && GitListing(root) is { } listed)
        {
            return listed.Where(relative => File.Exists(Path.Combine(root, relative)));
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'));
    }

    private static IReadOnlyList<string>? GitListing(string root)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "-c", "core.quotePath=false", "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
            {
                start.ArgumentList.Add(argument);
            }

            using var git = Process.Start(start)!;
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            return git.ExitCode == 0 ? output.Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>A line with the record names (in a record folder only) and its file's transition tokens taken out, as the
    /// guard reads it.</summary>
    private static string Stripped(string relative, string line)
    {
        if (RecordFolders.Any(folder => relative.StartsWith(folder, StringComparison.Ordinal)))
        {
            foreach (var token in RecordOnly)
            {
                line = line.Replace(token, string.Empty, StringComparison.Ordinal);
            }
        }

        foreach (var (file, token) in Transition)
        {
            if (string.Equals(file, relative, StringComparison.Ordinal))
            {
                line = line.Replace(token, string.Empty, StringComparison.Ordinal);
            }
        }

        return line;
    }

    [Fact]
    public void No_path_or_text_in_the_tree_carries_a_former_name()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var relative in TreeFiles(root))
        {
            if (SkippedSegments.Any(s => ("/" + relative).Contains(s, StringComparison.Ordinal))
                || relative.StartsWith(".claude/settings.local", StringComparison.Ordinal)
                || Allowed.Any(a => relative.StartsWith(a, StringComparison.Ordinal)))
            {
                continue;
            }

            if (Former.IsMatch(relative))
            {
                offenders.Add($"{relative} (path)");
            }

            var bytes = File.ReadAllBytes(Path.Combine(root, relative));
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
            {
                continue;
            }

            var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (Former.IsMatch(Stripped(relative, lines[i])))
                {
                    offenders.Add($"{relative}:{i + 1}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "no former name may remain in the tree (docs/plans/rename-internal-names.md), and these do: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_transition_entry_is_still_needed()
    {
        // A pair whose token is gone from its file is dead weight that would let the name come back unnoticed.
        var root = RepositoryRoot();
        foreach (var (file, token) in Transition)
        {
            File.ReadAllText(Path.Combine(root, file)).Should().Contain(token, $"{file} is on the transition list for it");
        }
    }

    [Theory]
    [InlineData("candidate", false)]
    [InlineData("verdeckt", false)]
    [InlineData("onD" + "idAddListener", true)]
    public void The_pattern_tells_the_names_from_words_that_contain_their_letters(string text, bool former)
    {
        Former.IsMatch(text).Should().Be(former);
    }

    [Fact]
    public void The_company_domain_is_not_a_former_name()
    {
        Former.IsMatch("git." + Company).Should().BeFalse();
        Former.IsMatch("the " + D + "bit domain").Should().BeFalse();
    }

    [Fact]
    public void A_record_name_is_allowed_only_in_the_records_and_nothing_around_it()
    {
        var oldHost = "https://" + K + "." + Company + "/updates/";
        Former.IsMatch(Stripped("docs/adr/0025-x.md", "the update channel at " + oldHost)).Should().BeFalse();
        Former.IsMatch(Stripped("docs/evidences/2026-10-09-x/README.md", "minted at " + oldHost)).Should().BeFalse();
        Former.IsMatch(Stripped("README.md", "the update channel at " + oldHost)).Should().BeTrue("outside the records it is a former name");
        Former.IsMatch(Stripped("docs/adr/0025-x.md", oldHost + " and " + D + K)).Should().BeTrue("only the named systems are allowed");
        Former.IsMatch(Stripped("README.md", "org." + D + K + ".variant")).Should().BeTrue("a transition token counts only in its own file");
    }
}
