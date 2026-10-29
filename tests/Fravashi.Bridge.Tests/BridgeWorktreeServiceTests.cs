using Fravashi.Bridge;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

public sealed class BridgeWorktreeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-bridge-tests", Guid.NewGuid().ToString("N"));

    private readonly string _repoRoot;
    private readonly WorkspaceRegistry _registry = new();
    private readonly BridgeWorktreeService _worktrees;
    private readonly string _repoId;

    public BridgeWorktreeServiceTests()
    {
        _repoRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repoRoot);
        // Host git is a product prerequisite (decided 2026-08-01: preflight-required, not
        // bundled), the same reason BridgeGitServiceTests gives. Without it this does not seed a real
        // repository, only registers the plain folder: the git-independent cases (the hyphen refusal,
        // an unknown repository, the nonexistent-executable cases) still run; every git-backed test
        // guards itself and returns early.
        if (!GitMissing)
        {
            SeedRepo(_repoRoot);
        }

        _repoId = _registry.Register(_repoRoot).Id;
        _worktrees = new BridgeWorktreeService(_registry);
    }

    private static bool GitMissing => !BridgeGitService.IsGitAvailable();

    // The sibling container this repo's worktrees land in, named after the repo folder (a test guid).
    private string Container => Path.Combine(_root, "repo" + BridgeWorktreeService.ContainerSuffix);

    [Theory]
    [InlineData(1, "fravashi/worktree/item-1")]
    [InlineData(306, "fravashi/worktree/item-306")]
    public void BranchFor_IsDeterministic(int item, string expected) =>
        BridgeWorktreeService.BranchFor(item).Should().Be(expected);

    [Fact]
    public async Task Create_MakesASiblingFolderOnItsOwnNewBranch()
    {
        if (GitMissing)
        {
            return; // xunit 2.x has no dynamic skip; a machine without git passes vacuously.
        }

        var worktree = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);

        worktree.Branch.Should().Be("fravashi/worktree/item-306");
        worktree.BaseBranch.Should().Be("main");
        worktree.BranchReused.Should().BeFalse();
        worktree.Workspace.RootPath.Should().Be(Path.Combine(Container, "306"));
        Directory.Exists(worktree.Workspace.RootPath).Should().BeTrue();
        File.Exists(Path.Combine(worktree.Workspace.RootPath, "seed.txt")).Should().BeTrue();

        // What git itself says is checked out, not only the string the service computed.
        (await CurrentBranchAsync(worktree.Workspace.RootPath)).Should().Be("fravashi/worktree/item-306");
        (await HeadShaAsync(worktree.Workspace.RootPath)).Should().Be(await HeadShaAsync(_repoRoot));
    }

    [Fact]
    public async Task Create_RegistersTheWorktreeWithTheBridge()
    {
        if (GitMissing)
        {
            return;
        }

        var worktree = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);

        _registry.TryGet(worktree.Workspace.Id).Should().NotBeNull();
        _registry.List().Select(w => w.Id).Should().Contain(worktree.Workspace.Id);
        _registry.ResolvePath(worktree.Workspace.Id, "seed.txt")
            .Should().Be(Path.Combine(worktree.Workspace.RootPath, "seed.txt"));
    }

    [Fact]
    public async Task Create_CalledTwiceForTheSameItem_IsIdempotent()
    {
        if (GitMissing)
        {
            return;
        }

        var first = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);
        var second = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);

        second.Workspace.Id.Should().Be(first.Workspace.Id);
        second.Branch.Should().Be(first.Branch);
        second.BranchReused.Should().BeTrue("the second call only finds the worktree the first one made");
        (await CurrentBranchAsync(second.Workspace.RootPath)).Should().Be(first.Branch);

        var listing = await RunGitAsync(_repoRoot, "worktree", "list", "--porcelain");
        listing.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Should().HaveCount(2, "the main checkout plus exactly one worktree, never two");
    }

    [Fact]
    public async Task Create_RefusesAPreexistingForeignFolder()
    {
        if (GitMissing)
        {
            return;
        }

        var target = Path.Combine(Container, "306");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "not-ours.txt"), "a person's own folder");

        var act = () => _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);

        await act.Should().ThrowAsync<WorktreeTargetConflictException>();
        File.Exists(Path.Combine(target, "not-ours.txt")).Should().BeTrue("the foreign folder must be untouched");
        _registry.List().Should().ContainSingle("the foreign folder must never be registered");
    }

    [Fact]
    public async Task Create_UnknownRepository_Throws()
    {
        var act = () => _worktrees.CreateAsync("deadbeefdeadbeef", 306, "main", CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("--upload-pack=calc")]
    public async Task Create_RefusesABaseBranchWithALeadingHyphen(string baseBranch)
    {
        var act = () => _worktrees.CreateAsync(_repoId, 306, baseBranch, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        _registry.List().Should().ContainSingle("the main repository only; nothing was created");
    }

    [Fact]
    public async Task Create_RefusesABaseBranchGitItselfRejects()
    {
        if (GitMissing)
        {
            return;
        }

        // Consecutive dots are invalid in a git ref name, per `git check-ref-format`, but pass the plain
        // text filter (no hyphen, no whitespace, no control characters), so only asking git itself catches it.
        var act = () => _worktrees.CreateAsync(_repoId, 306, "a..b", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        _registry.List().Should().ContainSingle();
    }

    [Fact]
    public async Task Prune_RemovesACleanWorktreeAndDropsItsGrant()
    {
        if (GitMissing)
        {
            return;
        }

        var worktree = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);

        var result = await _worktrees.PruneAsync(_repoId, 306, CancellationToken.None);

        result.Outcome.Should().Be(BridgeWorktreePruneOutcome.Removed);
        Directory.Exists(worktree.Workspace.RootPath).Should().BeFalse();
        _registry.TryGet(worktree.Workspace.Id).Should().BeNull();
    }

    [Fact]
    public async Task Prune_RefusesAWorktreeWithAnUncommittedEdit()
    {
        if (GitMissing)
        {
            return;
        }

        var worktree = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);
        File.WriteAllText(Path.Combine(worktree.Workspace.RootPath, "seed.txt"), "changed");

        var result = await _worktrees.PruneAsync(_repoId, 306, CancellationToken.None);

        result.Outcome.Should().Be(BridgeWorktreePruneOutcome.Dirty);
        result.Detail.Should().NotBeNullOrWhiteSpace();
        Directory.Exists(worktree.Workspace.RootPath).Should().BeTrue();
        _registry.TryGet(worktree.Workspace.Id).Should().NotBeNull();
    }

    [Fact]
    public async Task Prune_RefusesAWorktreeWithAnUntrackedFile()
    {
        if (GitMissing)
        {
            return;
        }

        var worktree = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);
        File.WriteAllText(Path.Combine(worktree.Workspace.RootPath, "new.txt"), "untracked");

        var result = await _worktrees.PruneAsync(_repoId, 306, CancellationToken.None);

        result.Outcome.Should().Be(BridgeWorktreePruneOutcome.Dirty);
        Directory.Exists(worktree.Workspace.RootPath).Should().BeTrue();
    }

    [Fact]
    public async Task Prune_NothingThere_IsNotFound()
    {
        if (GitMissing)
        {
            return;
        }

        var result = await _worktrees.PruneAsync(_repoId, 999, CancellationToken.None);

        result.Should().Be(BridgeWorktreePruneResult.NotFound);
    }

    [Fact]
    public async Task Create_WhenGitCannotRun_Throws()
    {
        var worktrees = new BridgeWorktreeService(_registry, executable: "fravashi-no-such-git");

        var act = () => worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().StartWith(BridgeGitResult.GitMissingMessage);
        _registry.List().Should().ContainSingle("the main repository only; nothing was created");
    }

    [Fact]
    public async Task Prune_WhenGitCannotRun_Throws()
    {
        var worktrees = new BridgeWorktreeService(_registry, executable: "fravashi-no-such-git");

        var act = () => worktrees.PruneAsync(_repoId, 306, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().StartWith(BridgeGitResult.GitMissingMessage);
    }

    [Fact]
    public async Task Create_AfterPrune_SucceedsForTheSameItem()
    {
        if (GitMissing)
        {
            return;
        }

        var first = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);
        (await _worktrees.PruneAsync(_repoId, 306, CancellationToken.None)).Outcome
            .Should().Be(BridgeWorktreePruneOutcome.Removed);

        var second = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);

        second.Branch.Should().Be(first.Branch);
        second.BranchReused.Should().BeTrue("the branch survives a clean prune, and this create reuses it");
        Directory.Exists(second.Workspace.RootPath).Should().BeTrue();
        File.Exists(Path.Combine(second.Workspace.RootPath, "seed.txt")).Should().BeTrue();

        // The reuse path (`worktree add <target> <branch>`, no `-b`) really lands on that branch.
        (await CurrentBranchAsync(second.Workspace.RootPath)).Should().Be(first.Branch);
    }

    [Fact]
    public async Task Create_AfterTheFolderWasRemovedByHand_RemakesIt()
    {
        if (GitMissing)
        {
            return;
        }

        var first = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);
        Directory.Delete(first.Workspace.RootPath, recursive: true);

        var second = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);

        Directory.Exists(second.Workspace.RootPath).Should().BeTrue();
        second.BranchReused.Should().BeTrue("git kept the branch even though the folder was gone");
        (await CurrentBranchAsync(second.Workspace.RootPath)).Should().Be(first.Branch);

        var listing = await RunGitAsync(_repoRoot, "worktree", "list", "--porcelain");
        listing.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Should().HaveCount(2, "the main checkout plus exactly one remade worktree");
    }

    [Fact]
    public async Task Prune_AWorktreeFolderRemovedByHand_CleansGitsRecordAndTheGrant()
    {
        if (GitMissing)
        {
            return;
        }

        var worktree = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);
        Directory.Delete(worktree.Workspace.RootPath, recursive: true);

        var result = await _worktrees.PruneAsync(_repoId, 306, CancellationToken.None);

        result.Outcome.Should().Be(BridgeWorktreePruneOutcome.Removed);
        _registry.TryGet(worktree.Workspace.Id).Should().BeNull();

        // Parsed into blocks, not a raw substring check: git prints forward slashes in this listing
        // regardless of platform, so a check against the backslash-bearing RootPath would never fail here
        // on the Windows host this bridge actually runs on.
        var listing = await RunGitAsync(_repoRoot, "worktree", "list", "--porcelain");
        listing.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Should().HaveCount(1, "only the main checkout should remain listed");
    }

    private static void SeedRepo(string root)
    {
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.name", "Test");
        RunGit(root, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "seed");
    }

    private static async Task<string> CurrentBranchAsync(string worktreeRoot) =>
        (await RunGitAsync(worktreeRoot, "symbolic-ref", "--short", "HEAD")).Trim();

    private static async Task<string> HeadShaAsync(string root) =>
        (await RunGitAsync(root, "rev-parse", "HEAD")).Trim();

    private static void RunGit(string cwd, params string[] args) => RunGitAsync(cwd, args).GetAwaiter().GetResult();

    private static async Task<string> RunGitAsync(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
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

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");
        }

        return stdout;
    }

    public void Dispose()
    {
        try
        {
            // git objects are read-only on Windows; clear the attribute so the temp tree deletes.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
