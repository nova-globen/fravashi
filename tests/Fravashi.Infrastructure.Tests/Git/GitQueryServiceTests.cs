using System.Diagnostics;
using AwesomeAssertions;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

/// <summary>
/// The phase-8 query surfaces of <see cref="GitCliService"/> against a real temp repo with a bare
/// origin: branch sync statuses, remote branches + tracking checkout, the graph rows, and blame.
/// </summary>
public sealed class GitQueryServiceTests : IDisposable
{
    private readonly string _base;
    private readonly string _work;
    private readonly string _origin;
    private readonly GitCliService _service;

    public GitQueryServiceTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "fravashi-query-" + Guid.NewGuid().ToString("N")[..8]);
        _origin = Path.Combine(_base, "origin.git");
        _work = Path.Combine(_base, "work");
        Directory.CreateDirectory(_origin);
        Directory.CreateDirectory(_work);
        RunGit(_origin, "init", "--bare", "-b", "main");
        RunGit(_work, "init", "-b", "main");
        RunGit(_work, "config", "user.name", "Ada");
        RunGit(_work, "config", "user.email", "ada@example.com");
        File.WriteAllText(Path.Combine(_work, "a.txt"), "one\ntwo\n");
        RunGit(_work, "add", ".");
        RunGit(_work, "commit", "-m", "seed commit (#42)");
        RunGit(_work, "remote", "add", "origin", _origin);
        RunGit(_work, "push", "-u", "origin", "main");
        _service = new GitCliService(
            new LocalGitCommandRunner(Options.Create(new RepositoryReaderOptions { Root = _work })));
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup only.
        }
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
        }
    }

    [Fact]
    public async Task Diff_files_and_merge_base_describe_a_pr_branch_against_its_target()
    {
        // A feature branch diverging from main: one modified file, one added; main moves on too, so
        // the triple-dot must diff against the merge base, not main's tip.
        RunGit(_work, "checkout", "-b", "feature/pr");
        File.WriteAllText(Path.Combine(_work, "a.txt"), "one\ntwo\nchanged\n");
        File.WriteAllText(Path.Combine(_work, "new.txt"), "fresh\n");
        RunGit(_work, "add", ".");
        RunGit(_work, "commit", "-m", "feature work");
        RunGit(_work, "push", "-u", "origin", "feature/pr");
        RunGit(_work, "checkout", "main");
        File.WriteAllText(Path.Combine(_work, "main-only.txt"), "mainline\n");
        RunGit(_work, "add", ".");
        RunGit(_work, "commit", "-m", "mainline moves on");
        RunGit(_work, "push", "origin", "main");

        var files = await _service.GetDiffFilesAsync("origin/main", "origin/feature/pr");

        files.Should().HaveCount(2);
        files.Should().Contain(f => f.Path == "a.txt" && f.Kind == Fravashi.Domain.Git.GitChangeKind.Modified);
        files.Should().Contain(f => f.Path == "new.txt" && f.Kind == Fravashi.Domain.Git.GitChangeKind.Added);
        files.Should().NotContain(f => f.Path == "main-only.txt"); // merge-base semantics

        var mergeBase = await _service.GetMergeBaseAsync("origin/main", "origin/feature/pr");
        mergeBase.Should().NotBeNullOrWhiteSpace();

        // An unknown ref fails loudly (the UI turns this into the fetch hint), never silently empty.
        var act = async () => await _service.GetDiffFilesAsync("origin/main", "origin/does-not-exist");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Branches_report_publish_and_ahead_behind_status()
    {
        // main: synced. local-only: never published. ahead: one unpushed commit on main.
        RunGit(_work, "branch", "local-only");
        File.WriteAllText(Path.Combine(_work, "a.txt"), "one\ntwo\nthree\n");
        RunGit(_work, "commit", "-am", "ahead commit");

        var branches = await _service.GetBranchesAsync();

        var main = branches.Single(b => b.Name == "main");
        main.IsCurrent.Should().BeTrue();
        main.IsPublished.Should().BeTrue();
        main.Upstream.Should().Be("origin/main");
        main.Ahead.Should().Be(1);
        main.Behind.Should().Be(0);

        var local = branches.Single(b => b.Name == "local-only");
        local.IsPublished.Should().BeFalse();
        local.Ahead.Should().Be(0);
    }

    [Fact]
    public async Task Remote_branches_list_and_tracking_checkout_works()
    {
        // A branch that exists only on the remote.
        RunGit(_work, "branch", "feature/remote-thing");
        RunGit(_work, "push", "origin", "feature/remote-thing");
        RunGit(_work, "branch", "-D", "feature/remote-thing");
        RunGit(_work, "fetch", "origin");

        var remotes = await _service.GetRemoteBranchesAsync();
        var remote = remotes.Single(r => r.Remote == "origin" && r.Name == "feature/remote-thing");

        // The slash in "feature/remote-thing" is the whole point: the service used to strip the first
        // path segment off the name it was given, so the Git page's remote Checkout button ran
        // `git checkout remote-thing` and failed with "pathspec did not match any file(s)".
        await _service.CheckoutTrackingAsync(remote);

        (await _service.GetCurrentBranchAsync()).Should().Be("feature/remote-thing");
        (await _service.GetBranchesAsync()).Single(b => b.Name == "feature/remote-thing")
            .Upstream.Should().Be("origin/feature/remote-thing");
    }

    [Fact]
    public async Task Tracking_checkout_of_a_branch_that_already_exists_locally_just_switches()
    {
        RunGit(_work, "branch", "feature/both-sides");
        RunGit(_work, "push", "origin", "feature/both-sides");
        RunGit(_work, "fetch", "origin");

        var remote = (await _service.GetRemoteBranchesAsync()).Single(r => r.Name == "feature/both-sides");

        await _service.CheckoutTrackingAsync(remote);

        (await _service.GetCurrentBranchAsync()).Should().Be("feature/both-sides");
    }

    [Fact]
    public async Task Tracking_checkout_names_the_remote_it_tracks_when_two_remotes_carry_the_branch()
    {
        // A second remote carrying the same branch is what makes git's DWIM checkout ambiguous, so the
        // remote the user picked has to reach git rather than being re-derived from the branch name.
        var second = Path.Combine(_base, "second.git");
        Directory.CreateDirectory(second);
        RunGit(second, "init", "--bare", "-b", "main");
        RunGit(_work, "remote", "add", "second", second);
        RunGit(_work, "branch", "feature/contested");
        RunGit(_work, "push", "origin", "feature/contested");
        RunGit(_work, "push", "second", "feature/contested");
        RunGit(_work, "branch", "-D", "feature/contested");
        RunGit(_work, "fetch", "--all");

        var remote = (await _service.GetRemoteBranchesAsync())
            .Single(r => r.Remote == "second" && r.Name == "feature/contested");

        await _service.CheckoutTrackingAsync(remote);

        (await _service.GetCurrentBranchAsync()).Should().Be("feature/contested");
        (await _service.GetBranchesAsync()).Single(b => b.Name == "feature/contested")
            .Upstream.Should().Be("second/feature/contested");
    }

    [Fact]
    public async Task The_graph_carries_parents_and_refs()
    {
        File.WriteAllText(Path.Combine(_work, "b.txt"), "b\n");
        RunGit(_work, "add", ".");
        RunGit(_work, "commit", "-m", "second");

        var graph = await _service.GetGraphAsync();

        graph.Should().HaveCount(2);
        graph[0].Subject.Should().Be("second");
        graph[0].Parents.Should().ContainSingle().Which.Should().Be(graph[1].Sha);
        graph[0].Refs.Should().Contain("main");
        graph[1].Parents.Should().BeEmpty();
        graph[1].Refs.Should().Contain("origin/main");
    }

    [Fact]
    public async Task Blame_names_the_last_commit_of_a_line_and_spots_a_pr_number()
    {
        var blame = await _service.GetLineBlameAsync("a.txt", 1);

        blame.Should().NotBeNull();
        blame!.Author.Should().Be("Ada");
        blame.Subject.Should().Contain("seed commit");
        blame.PullRequestNumber.Should().Be(42);

        // An uncommitted line blames to nothing.
        File.AppendAllText(Path.Combine(_work, "a.txt"), "uncommitted\n");
        (await _service.GetLineBlameAsync("a.txt", 3)).Should().BeNull();
    }
}
