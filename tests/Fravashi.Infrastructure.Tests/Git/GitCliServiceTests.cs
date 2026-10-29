using System.Diagnostics;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Repository;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

/// <summary>Exercises <see cref="GitCliService"/> against a throwaway real git repository (git CLI required).</summary>
public sealed class GitCliServiceTests : IDisposable
{
    private readonly string _dir;

    public GitCliServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fravashi-git-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        Git("init", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# Hello\n");
        File.WriteAllText(Path.Combine(_dir, "src", "Program.cs"), "class P { }\n");
        Git("add", "-A");
        Git("commit", "-m", "Initial commit");
        Git("checkout", "-b", "feature/x");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# Hello\n\nMore.\n");
        Git("commit", "-am", "Update readme");
        Git("checkout", "main");
    }

    private GitCliService Service() =>
        new(new LocalGitCommandRunner(Options.Create(new RepositoryReaderOptions { Root = _dir })));

    [Fact]
    public async Task Reports_branches_with_current_and_lists_tree_and_file()
    {
        var git = Service();
        git.IsAvailable.Should().BeTrue();

        (await git.GetCurrentBranchAsync()).Should().Be("main");

        var branches = await git.GetBranchesAsync();
        branches.Should().Contain(b => b.Name == "main" && b.IsCurrent);
        branches.Should().Contain(b => b.Name == "feature/x" && !b.IsCurrent);

        var root = await git.ListTreeAsync("main", string.Empty);
        root.Should().Contain(n => n.Name == "src" && n.IsDirectory);
        root.Should().Contain(n => n.Name == "README.md" && !n.IsDirectory);

        var src = await git.ListTreeAsync("main", "src");
        src.Should().Contain(n => n.Name == "Program.cs");

        var file = await git.ReadFileAsync("main", "README.md");
        file!.Content.Should().Contain("# Hello");
    }

    [Fact]
    public async Task Reads_commits_detail_and_diff()
    {
        var git = Service();

        var commits = await git.GetCommitsAsync("feature/x");
        commits.Should().HaveCountGreaterThanOrEqualTo(2);
        commits[0].Subject.Should().Be("Update readme");

        var detail = await git.GetCommitAsync(commits[0].Sha);
        detail.Should().NotBeNull();
        detail!.Files.Should().Contain(f => f.Path == "README.md");

        var diff = await git.GetDiffAsync(commits[0].Sha);
        diff.Should().Contain("diff --git");
        diff.Should().Contain("README.md");
    }

    [Fact]
    public async Task Browsing_a_ref_is_non_destructive()
    {
        var git = Service();

        // Reading feature content while HEAD stays on main must not move HEAD.
        var file = await git.ReadFileAsync("feature/x", "README.md");
        file!.Content.Should().Contain("More.");
        (await git.GetCurrentBranchAsync()).Should().Be("main");
    }

    [Fact]
    public async Task Create_branch_without_checkout_keeps_head()
    {
        var git = Service();

        await git.CreateBranchAsync("feature/y", "main", checkout: false);

        (await git.GetBranchesAsync()).Should().Contain(b => b.Name == "feature/y");
        (await git.GetCurrentBranchAsync()).Should().Be("main");
    }

    [Fact]
    public async Task Lists_lightweight_and_annotated_tags_with_the_commit_each_points_at()
    {
        var git = Service();
        var head = (await git.GetCommitsAsync("main"))[0].Sha;
        Git("tag", "v1.0");
        Git("tag", "-a", "v1.1", "-m", "Release one point one");

        var tags = await git.GetTagsAsync();

        tags.Should().HaveCount(2);
        tags.Should().Contain(t => t.Name == "v1.0" && t.Sha == head && t.Subject == "");
        tags.Should().Contain(t => t.Name == "v1.1" && t.Sha == head && t.Subject == "Release one point one");
    }

    [Fact]
    public async Task A_repository_without_tags_lists_none()
    {
        (await Service().GetTagsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Creates_an_annotated_release_tag_on_the_chosen_commit_and_refuses_an_existing_name()
    {
        // Work item #368: a release's tag is annotated, carries the release title, sits on the commit chosen (not
        // HEAD), and is never moved: the same name again is refused and the tag still points where it did.
        var git = Service();
        var first = (await git.GetCommitsAsync("main"))[0].Sha;

        await git.CreateTagAsync("v0.9.97", first, "Release v0.9.97");

        var tags = await git.GetTagsAsync();
        tags.Should().ContainSingle(t => t.Name == "v0.9.97" && t.Sha == first && t.Subject == "Release v0.9.97");

        var feature = (await git.GetCommitsAsync("feature/x"))[0].Sha;
        var again = () => git.CreateTagAsync("v0.9.97", feature, "Release v0.9.97");
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        (await git.GetTagsAsync()).Should().ContainSingle(t => t.Name == "v0.9.97" && t.Sha == first);
    }

    [Theory]
    [InlineData("v1.2")]
    [InlineData("--force")]
    [InlineData("v1.2.3:refs/heads/main")]
    [InlineData("v1.2.3 -f")]
    [InlineData("v01.2.3")]
    [InlineData("v1.2.3-")]
    [InlineData("v1.2.3-rc..1")]
    public async Task A_tag_name_outside_the_release_shape_never_reaches_git(string name)
    {
        var runner = new RecordingRunner();
        var git = new GitCliService(runner);

        var create = () => git.CreateTagAsync(name, "0123456789abcdef0123456789abcdef01234567", "Release");

        await create.Should().ThrowAsync<ArgumentException>();
        runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("main")]
    [InlineData("--all")]
    [InlineData("abc12")]
    public async Task A_commit_that_is_not_a_hexadecimal_object_name_never_reaches_git(string commit)
    {
        var runner = new RecordingRunner();

        var create = () => new GitCliService(runner).CreateTagAsync("v1.2.3", commit, "Release v1.2.3");

        await create.Should().ThrowAsync<ArgumentException>();
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Creating_a_release_tag_runs_exactly_two_fixed_argument_vectors()
    {
        var runner = new RecordingRunner { ExitCodes = new Queue<int>([1, 0]) };
        const string Commit = "0123456789abcdef0123456789abcdef01234567";

        await new GitCliService(runner).CreateTagAsync("v1.2.3-rc.1", Commit, "-m --force Release v1.2.3-rc.1");

        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].Should().Equal("rev-parse", "--verify", "--quiet", "refs/tags/v1.2.3-rc.1");
        runner.Calls[1].Should().Equal("tag", "-a", "-m", "-m --force Release v1.2.3-rc.1", "v1.2.3-rc.1", Commit);
    }

    private sealed class RecordingRunner : Fravashi.Application.Git.IGitCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Queue<int> ExitCodes { get; init; } = new();

        public bool IsAvailable() => true;

        public Task<Fravashi.Application.Git.GitCommandResult> RunAsync(
            IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            Calls.Add([.. args]);
            var code = ExitCodes.Count > 0 ? ExitCodes.Dequeue() : 0;
            return Task.FromResult(new Fravashi.Application.Git.GitCommandResult(code, string.Empty, string.Empty));
        }
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _dir, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
        }
    }

    public void Dispose()
    {
        try
        {
            // Make .git files deletable on Windows.
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
