using System.Diagnostics;
using AwesomeAssertions;
using Fravashi.Application.Git;
using Fravashi.Domain.Git;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

public sealed class GitWorkingTreeServiceTests : IDisposable
{
    private readonly string _root;

    public GitWorkingTreeServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fravashi-wt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        RunGit("init", "-b", "main");
        // Repo-local identity for the ARRANGE commits; the service's own commits must be able to
        // override it from settings.
        RunGit("config", "user.name", "Repo Config");
        RunGit("config", "user.email", "repo@example.com");
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "original\n");
        RunGit("add", ".");
        RunGit("commit", "-m", "seed");
    }

    public void Dispose()
    {
        try
        {
            // Clear read-only attributes .git objects carry on Windows.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup only.
        }
    }

    private void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
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

    private string GitOut(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private GitWorkingTreeService Service(string? authorName = null, string? authorEmail = null)
    {
        var settings = new Dictionary<string, string?>();
        if (authorName is not null)
        {
            settings[GitIdentityKeys.AuthorName] = authorName;
        }

        if (authorEmail is not null)
        {
            settings[GitIdentityKeys.AuthorEmail] = authorEmail;
        }

        return new GitWorkingTreeService(
            Options.Create(new RepositoryReaderOptions { Root = _root }),
            new LocalGitCommandRunner(Options.Create(new RepositoryReaderOptions { Root = _root })),
            new RepositoryWriteGate(),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    [Fact]
    public async Task Status_reports_branch_staged_unstaged_and_untracked()
    {
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "changed\n");   // unstaged modify
        File.WriteAllText(Path.Combine(_root, "new.txt"), "new\n");             // untracked
        File.WriteAllText(Path.Combine(_root, "staged.txt"), "staged\n");
        RunGit("add", "staged.txt");                                             // staged add

        var status = await Service().GetStatusAsync();

        status.Branch.Should().Be("main");
        status.StagedEntries.Should().ContainSingle(e => e.Path == "staged.txt" && e.State == GitEntryState.Added);
        status.UnstagedEntries.Should().Contain(e => e.Path == "committed.txt" && e.State == GitEntryState.Modified);
        status.UnstagedEntries.Should().Contain(e => e.Path == "new.txt" && e.State == GitEntryState.Untracked);
    }

    [Fact]
    public async Task Stage_and_unstage_move_entries_between_the_lists()
    {
        var service = Service();
        File.WriteAllText(Path.Combine(_root, "flow.txt"), "x\n");

        await service.StageAsync(["flow.txt"]);
        (await service.GetStatusAsync()).StagedEntries.Should().ContainSingle(e => e.Path == "flow.txt");

        await service.UnstageAsync(["flow.txt"]);
        var after = await service.GetStatusAsync();
        after.StagedEntries.Should().BeEmpty();
        after.UnstagedEntries.Should().ContainSingle(e => e.Path == "flow.txt" && e.State == GitEntryState.Untracked);
    }

    [Fact]
    public async Task Staging_a_deletion_works()
    {
        var service = Service();
        File.Delete(Path.Combine(_root, "committed.txt"));

        await service.StageAsync(["committed.txt"]);

        (await service.GetStatusAsync()).StagedEntries
            .Should().ContainSingle(e => e.Path == "committed.txt" && e.State == GitEntryState.Deleted);
    }

    [Fact]
    public async Task Commit_uses_the_settings_identity_over_the_repo_config()
    {
        var service = Service("Settings Author", "settings@example.com");
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "for commit\n");
        await service.StageAsync(["committed.txt"]);

        var outcome = await service.CommitAsync("test: settings identity");

        outcome.Succeeded.Should().BeTrue();
        outcome.Sha.Should().NotBeNullOrWhiteSpace();
        GitOut("log", "-1", "--format=%an <%ae>").Should().Be("Settings Author <settings@example.com>");
        GitOut("log", "-1", "--format=%cn <%ce>").Should().Be("Settings Author <settings@example.com>");
    }

    [Fact]
    public async Task Commit_without_settings_identity_falls_back_to_repo_config()
    {
        var service = Service();
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "fallback\n");
        await service.StageAsync(["committed.txt"]);

        var outcome = await service.CommitAsync("test: config identity");

        outcome.Succeeded.Should().BeTrue();
        GitOut("log", "-1", "--format=%an").Should().Be("Repo Config");
    }

    [Fact]
    public async Task Commit_failures_surface_gits_error()
    {
        var outcome = await Service().CommitAsync("nothing staged");

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().NotBeNullOrWhiteSpace();
    }

    // ---- MarkExecutableAsync (#327) ----

    [Fact]
    public async Task MarkExecutableAsync_stages_100755_even_with_core_filemode_false()
    {
        // core.filemode false is what a Windows host's git always has: on its own it never notices a
        // permission bit at all, which is exactly #327's symptom, reproduced here on Linux so the
        // behaviour this method exists for is actually proven rather than assumed.
        RunGit("config", "core.filemode", "false");
        var service = Service();
        File.WriteAllText(Path.Combine(_root, "run.sh"), "#!/bin/sh\n");
        await service.StageAsync(["run.sh"]);
        GitOut("ls-files", "-s", "run.sh").Should().StartWith("100644", "a plain add is 100644 regardless of the file's real mode");

        await service.MarkExecutableAsync(["run.sh"]);

        GitOut("ls-files", "-s", "run.sh").Should().StartWith("100755");
    }

    [Fact]
    public async Task A_path_never_asked_for_stays_100644_even_with_core_filemode_false()
    {
        RunGit("config", "core.filemode", "false");
        var service = Service();
        File.WriteAllText(Path.Combine(_root, "plain.txt"), "hello\n");
        await service.StageAsync(["plain.txt"]);

        GitOut("ls-files", "-s", "plain.txt").Should().StartWith("100644");
    }

    [Fact]
    public async Task MarkExecutableAsync_on_a_path_outside_the_index_fails_loudly()
    {
        var act = () => Service().MarkExecutableAsync(["never-staged.sh"]);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Working_diff_sides_match_vs_code_semantics()
    {
        var service = Service();
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "staged version\n");
        await service.StageAsync(["committed.txt"]);
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "worktree version\n");

        var staged = await service.GetWorkingDiffAsync("committed.txt", staged: true);
        staged.Original.Should().Contain("original");        // HEAD
        staged.Modified.Should().Contain("staged version");  // index

        var unstaged = await service.GetWorkingDiffAsync("committed.txt", staged: false);
        unstaged.Original.Should().Contain("staged version");   // index
        unstaged.Modified.Should().Contain("worktree version"); // working tree
    }

    // ---- Stash (also the undo-changes mechanism) ----

    [Fact]
    public async Task Stash_all_reverts_the_tree_and_lists_with_the_message()
    {
        var service = Service();
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "modified\n");
        File.WriteAllText(Path.Combine(_root, "untracked.txt"), "new\n");

        var outcome = await service.StashAsync("wip: everything");

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        // git re-materializes the file on revert; autocrlf may hand back CRLF on Windows.
        File.ReadAllText(Path.Combine(_root, "committed.txt")).Replace("\r\n", "\n").Should().Be("original\n");
        File.Exists(Path.Combine(_root, "untracked.txt")).Should().BeFalse();
        var stashes = await service.GetStashesAsync();
        stashes.Should().ContainSingle(s => s.Message.Contains("wip: everything"));
    }

    [Fact]
    public async Task A_path_scoped_stash_is_the_undo_for_that_file_only()
    {
        var service = Service();
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "modified\n");
        File.WriteAllText(Path.Combine(_root, "keep.txt"), "keep me\n");

        var outcome = await service.StashAsync("change backup: committed.txt", ["committed.txt"]);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        File.ReadAllText(Path.Combine(_root, "committed.txt")).Replace("\r\n", "\n").Should().Be("original\n"); // undone
        File.Exists(Path.Combine(_root, "keep.txt")).Should().BeTrue();                    // untouched
    }

    [Fact]
    public async Task Stash_pop_restores_and_drops_while_apply_keeps_the_entry()
    {
        var service = Service();
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "modified\n");
        (await service.StashAsync("backup one")).Succeeded.Should().BeTrue();

        (await service.StashApplyAsync(0)).Succeeded.Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "committed.txt")).Replace("\r\n", "\n").Should().Be("modified\n");
        (await service.GetStashesAsync()).Should().HaveCount(1); // apply keeps it

        RunGit("checkout", "--", "committed.txt"); // clean up for the pop
        (await service.StashPopAsync(0)).Succeeded.Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "committed.txt")).Replace("\r\n", "\n").Should().Be("modified\n");
        (await service.GetStashesAsync()).Should().BeEmpty(); // pop drops it
    }

    [Fact]
    public async Task Stashing_a_clean_tree_reports_honestly()
    {
        var outcome = await Service().StashAsync("nothing here");

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("no changes");
    }
}
