using System.Diagnostics;
using Fravashi.Application.Onboarding;
using Fravashi.Infrastructure.Onboarding;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding;

/// <summary>
/// Exercises the <b>real</b> git push round-trip fully offline, against a local <b>bare</b> git repository that
/// stands in for a GitHub wiki remote (over <c>file://</c>). No network, no live GitHub. This proves the actual
/// clone → add → commit → push → rm → commit → push mechanism, that both probe commits land in the remote's
/// history (git history is append-only), that the probe page is gone from the tip, and that no credential is
/// ever written to the clone's <c>.git/config</c>.
/// </summary>
public sealed class GitWikiRoundTripRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fravashi-wiki-test-").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A leftover temp directory is harmless.
        }
    }

    [Fact]
    public async Task Real_round_trip_against_a_local_bare_repo_proves_write_and_delete_and_leaves_no_token_on_disk()
    {
        var bareUrl = SeedBareWiki();

        var result = await new GitWikiRoundTripRunner().RunAsync(bareUrl, token: null);

        result.Cloned.Should().BeTrue();
        result.WriteProven.Should().BeTrue(result.WriteDetail);
        result.DeleteProven.Should().BeTrue(result.DeleteDetail);
        result.TokenFoundOnDisk.Should().BeFalse();

        // The remote now holds two probe commits (append-only history), and the probe page is gone from the tip.
        var log = RunGit(_root, "-C", BarePath, "log", "--pretty=%an%x1f%s");
        log.ExitCode.Should().Be(0);
        log.StdOut.Should().Contain("Fravashi probe").And.Contain("verify write access").And.Contain("remove probe page");

        var tip = RunGit(_root, "-C", BarePath, "ls-tree", "--name-only", "HEAD");
        tip.StdOut.Should().NotContain("Fravashi-access-probe");
        tip.StdOut.Should().Contain("Home.md");
    }

    [Fact]
    public async Task Full_probe_through_the_wiki_access_probe_proves_read_write_delete_against_a_local_bare_repo()
    {
        var bareUrl = SeedBareWiki();
        var probe = new WikiAccessProbe(new HttpClient(), new GitLsRemoteRunner(), new GitWikiRoundTripRunner());

        // The bare repo is named "…/agent.wiki.git"; the normalizer strips and re-adds ".wiki.git", landing back
        // on the same URL - so this drives the whole Read (ls-remote) → round-trip path end to end. A non-empty
        // token clears the probe's "no token, cannot push" guard; the file:// transport simply ignores the
        // http.extraHeader credential the runner sets.
        var result = await probe.ProbeAsync(new WikiAccessRequest(OnboardingProviders.GitHubWiki, bareUrl, null, "local-test-token"));

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var delete = result.Checks.Single(c => c.Capability == "Delete");
        delete.Outcome.Should().Be(AccessProbeOutcome.Passed);
        delete.Detail.Should().Contain("append-only");
    }

    private string BarePath => Path.Combine(_root, "agent.wiki.git");

    /// <summary>Creates a bare repo with one seed page and returns its <c>file://</c> URL.</summary>
    private string SeedBareWiki()
    {
        var bare = BarePath;
        RunGit(_root, "-c", "init.defaultBranch=master", "init", "--bare", "--quiet", bare).ExitCode.Should().Be(0);

        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seed);
        RunGit(seed, "-c", "init.defaultBranch=master", "init", "--quiet").ExitCode.Should().Be(0);
        File.WriteAllText(Path.Combine(seed, "Home.md"), "# Home\n");
        RunGit(seed, "add", "Home.md").ExitCode.Should().Be(0);
        RunGit(seed, "commit", "--quiet", "-m", "seed").ExitCode.Should().Be(0);
        RunGit(seed, "remote", "add", "origin", bare).ExitCode.Should().Be(0);
        RunGit(seed, "push", "--quiet", "origin", "master").ExitCode.Should().Be(0);

        return "file:///" + bare.Replace('\\', '/');
    }

    private readonly record struct GitResult(int ExitCode, string StdOut, string StdErr);

    private static GitResult RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Explicit identity so seed commits do not depend on a global git config.
        psi.Environment["GIT_AUTHOR_NAME"] = "Seed";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "seed@fravashi.local";
        psi.Environment["GIT_COMMITTER_NAME"] = "Seed";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "seed@fravashi.local";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new GitResult(process.ExitCode, stdout, stderr);
    }
}
