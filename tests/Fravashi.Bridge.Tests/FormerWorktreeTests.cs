using AwesomeAssertions;
using Fravashi.Domain;
using Xunit;

namespace Fravashi.Bridge.Tests;

/// <summary>
/// The bridge release (<c>docs/plans/rename-internal-names.md</c>, phase 2), on disk in a user's repository: a worktree
/// made before the internal rename sits under the former sibling folder on the former branch. It is still found and
/// used as it stands (nothing is moved under a running git), it can still be pruned, and an item whose worktree was
/// pruned keeps its former branch's work. Real git, as the other worktree tests; a machine without it passes vacuously.
/// </summary>
public sealed class FormerWorktreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fravashi-former-worktrees", Guid.NewGuid().ToString("N"));
    private readonly string _repoRoot;
    private readonly WorkspaceRegistry _registry = new();
    private readonly BridgeWorktreeService _worktrees;
    private readonly string _repoId;

    public FormerWorktreeTests()
    {
        _repoRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repoRoot);
        if (!GitMissing)
        {
            RunGit(_repoRoot, "init", "-b", "main");
            RunGit(_repoRoot, "config", "user.name", "Test");
            RunGit(_repoRoot, "config", "user.email", "test@example.com");
            File.WriteAllText(Path.Combine(_repoRoot, "seed.txt"), "seed\n");
            RunGit(_repoRoot, "add", ".");
            RunGit(_repoRoot, "commit", "-m", "seed");
        }

        _repoId = _registry.Register(_repoRoot).Id;
        _worktrees = new BridgeWorktreeService(_registry);
    }

    private static bool GitMissing => !BridgeGitService.IsGitAvailable();

    private string FormerTarget(int item) => Path.Combine(_root, "repo" + FormerNames.WorktreeContainerSuffix, item.ToString());

    private static string FormerBranch(int item) => FormerNames.WorktreeBranchPrefix + item;

    /// <summary>A worktree as the bridge made it before the rename: the former folder, on the former branch.</summary>
    private void FormerWorktree(int item)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FormerTarget(item))!);
        RunGit(_repoRoot, "worktree", "add", "-b", FormerBranch(item), FormerTarget(item), "main");
    }

    [Fact]
    public async Task A_worktree_made_before_the_rename_is_found_where_it_is_and_nothing_new_is_made()
    {
        if (GitMissing)
        {
            return;
        }

        FormerWorktree(306);

        var found = await _worktrees.CreateAsync(_repoId, 306, "main", CancellationToken.None);

        found.Workspace.RootPath.Should().Be(FormerTarget(306));
        found.Branch.Should().Be(FormerBranch(306));
        found.BranchReused.Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "repo" + BridgeWorktreeService.ContainerSuffix)).Should().BeFalse(
            "no second worktree is made under the new folder");
    }

    [Fact]
    public async Task A_worktree_made_before_the_rename_can_still_be_pruned()
    {
        if (GitMissing)
        {
            return;
        }

        FormerWorktree(307);

        var result = await _worktrees.PruneAsync(_repoId, 307, CancellationToken.None);

        result.Outcome.Should().Be(BridgeWorktreePruneOutcome.Removed);
        Directory.Exists(FormerTarget(307)).Should().BeFalse();
    }

    [Fact]
    public async Task An_item_whose_former_worktree_was_pruned_continues_on_its_former_branch()
    {
        if (GitMissing)
        {
            return;
        }

        FormerWorktree(308);
        File.WriteAllText(Path.Combine(FormerTarget(308), "work.txt"), "earlier work\n");
        RunGit(FormerTarget(308), "add", ".");
        RunGit(FormerTarget(308), "commit", "-m", "earlier work");
        RunGit(_repoRoot, "worktree", "remove", FormerTarget(308));

        var made = await _worktrees.CreateAsync(_repoId, 308, "main", CancellationToken.None);

        made.Branch.Should().Be(FormerBranch(308));
        made.BranchReused.Should().BeTrue();
        made.Workspace.RootPath.Should().StartWith(Path.Combine(_root, "repo" + BridgeWorktreeService.ContainerSuffix));
        File.ReadAllText(Path.Combine(made.Workspace.RootPath, "work.txt")).Should().Be("earlier work\n");
    }

    public void Dispose()
    {
        try
        {
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

    private static void RunGit(string cwd, params string[] args)
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
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");
        }
    }
}
