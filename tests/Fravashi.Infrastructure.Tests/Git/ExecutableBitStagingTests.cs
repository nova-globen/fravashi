using AwesomeAssertions;
using Fravashi.Application.Git;
using Fravashi.Domain.Git;
using Fravashi.Infrastructure.Git;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Git;

/// <summary>
/// A fact that sets a real Unix file mode on a temp file to stand in for the contained copy's owner-
/// executable bit; it skips on Windows, which has no such bit.
/// </summary>
internal sealed class CopyExecutableBitFactAttribute : FactAttribute
{
    public CopyExecutableBitFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Sets a Unix file mode to stand in for the copy's real executable bit, which Windows does not have.";
        }
    }
}

/// <summary>
/// #327, over a FAKE <see cref="IGitWorkingTreeService"/> that only records what it is asked to mark
/// executable, never a real git process: proves exactly which paths <see cref="ExecutableBitStaging"/>
/// hands to <see cref="IGitWorkingTreeService.MarkExecutableAsync"/>, which it leaves alone, and that a
/// failure from that call is never swallowed.
/// </summary>
public sealed class ExecutableBitStagingTests : IDisposable
{
    private readonly string _copy =
        Path.Combine(Path.GetTempPath(), "fravashi-copy-" + Guid.NewGuid().ToString("N")[..8]);

    public ExecutableBitStagingTests() => Directory.CreateDirectory(_copy);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_copy, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- FilesToMark (#330): the one decision ExecutableBitStaging.MarkAsync and the commit reviewer both read ----

    [CopyExecutableBitFact]
    public void FilesToMark_names_a_path_executable_in_the_copy()
    {
        WriteCopyFile("scripts/run.sh", executable: true);

        ExecutableBitStaging.FilesToMark(_copy, ["scripts/run.sh"]).Should().Equal("scripts/run.sh");
    }

    [CopyExecutableBitFact]
    public void FilesToMark_leaves_out_a_path_that_is_not_executable_in_the_copy()
    {
        WriteCopyFile("readme.md", executable: false);

        ExecutableBitStaging.FilesToMark(_copy, ["readme.md"]).Should().BeEmpty();
    }

    [Fact]
    public void FilesToMark_leaves_out_a_path_with_no_file_in_the_copy()
    {
        ExecutableBitStaging.FilesToMark(_copy, ["never-synced.sh"]).Should().BeEmpty();
    }

    [CopyExecutableBitFact]
    public void FilesToMark_names_only_the_executable_ones_among_several()
    {
        WriteCopyFile("scripts/run.sh", executable: true);
        WriteCopyFile("readme.md", executable: false);

        ExecutableBitStaging.FilesToMark(_copy, ["scripts/run.sh", "readme.md", "never-synced.sh"])
            .Should().Equal("scripts/run.sh");
    }

    [CopyExecutableBitFact]
    public async Task A_new_file_executable_in_the_copy_is_staged_with_chmod_plus_x()
    {
        var file = WriteCopyFile("scripts/run.sh", executable: true);
        var tree = new RecordingTreeService();

        await ExecutableBitStaging.MarkAsync(
            tree, _copy, [new GitWorkingEntry("scripts/run.sh", GitEntryState.Added, Staged: true)], CancellationToken.None);

        tree.MarkedExecutable.Should().Equal("scripts/run.sh");
        File.Exists(file).Should().BeTrue();
    }

    [CopyExecutableBitFact]
    public async Task A_new_file_that_is_not_executable_in_the_copy_is_not_chmod_staged()
    {
        WriteCopyFile("readme.md", executable: false);
        var tree = new RecordingTreeService();

        await ExecutableBitStaging.MarkAsync(
            tree, _copy, [new GitWorkingEntry("readme.md", GitEntryState.Added, Staged: true)], CancellationToken.None);

        tree.MarkedExecutable.Should().BeEmpty();
    }

    [CopyExecutableBitFact]
    public async Task A_tracked_file_whose_copy_lacks_the_bit_is_left_alone_never_demoted()
    {
        // Standing in for a file the repository already tracks as 100755: the sync engine wrote the
        // copy's version back with File.WriteAllBytesAsync, which sets no mode, so the copy's own bit is
        // unset even though the host still holds 100755 for it. Never calling --chmod=-x is the whole point.
        WriteCopyFile("deploy.sh", executable: false);
        var tree = new RecordingTreeService();

        await ExecutableBitStaging.MarkAsync(
            tree, _copy, [new GitWorkingEntry("deploy.sh", GitEntryState.Modified, Staged: true)], CancellationToken.None);

        tree.MarkedExecutable.Should().BeEmpty("the method only ever adds the bit; it never asks for --chmod=-x");
    }

    [CopyExecutableBitFact]
    public async Task A_path_the_argument_checks_refuse_is_never_passed_to_git_and_fails_loudly()
    {
        WriteCopyFile("-evil.sh", executable: true);
        var tree = new RecordingTreeService();

        var act = () => ExecutableBitStaging.MarkAsync(
            tree, _copy, [new GitWorkingEntry("-evil.sh", GitEntryState.Added, Staged: true)], CancellationToken.None);

        // A path with a leading hyphen fails GitToolArguments.NormalizePath and is never handed to git;
        // because the file really is executable in the copy, dropping it silently would leave it
        // committed as 100644 while the commit reported success, so it throws instead.
        await act.Should().ThrowAsync<InvalidOperationException>();
        tree.MarkedExecutable.Should().BeEmpty();
    }

    [CopyExecutableBitFact]
    public async Task A_non_executable_path_the_argument_checks_refuse_is_skipped_quietly()
    {
        // The same odd name, but not executable in the copy: there is nothing to mark either way, so
        // this is not a failure, just skipped like any other non-executable file.
        WriteCopyFile("-harmless.sh", executable: false);
        var tree = new RecordingTreeService();

        await ExecutableBitStaging.MarkAsync(
            tree, _copy, [new GitWorkingEntry("-harmless.sh", GitEntryState.Added, Staged: true)], CancellationToken.None);

        tree.MarkedExecutable.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deleted_entry_is_never_asked_about()
    {
        var tree = new RecordingTreeService();

        await ExecutableBitStaging.MarkAsync(
            tree, _copy, [new GitWorkingEntry("gone.sh", GitEntryState.Deleted, Staged: true)], CancellationToken.None);

        tree.MarkedExecutable.Should().BeEmpty();
    }

    [Fact]
    public async Task A_path_with_no_file_in_the_copy_is_never_asked_about()
    {
        var tree = new RecordingTreeService();

        await ExecutableBitStaging.MarkAsync(
            tree, _copy, [new GitWorkingEntry("never-synced.sh", GitEntryState.Added, Staged: true)], CancellationToken.None);

        tree.MarkedExecutable.Should().BeEmpty();
    }

    [CopyExecutableBitFact]
    public async Task A_failure_marking_the_host_index_propagates_rather_than_being_swallowed()
    {
        WriteCopyFile("run.sh", executable: true);
        var tree = new RecordingTreeService { Fails = true };

        var act = () => ExecutableBitStaging.MarkAsync(
            tree, _copy, [new GitWorkingEntry("run.sh", GitEntryState.Added, Staged: true)], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*index.lock*");
    }

    private string WriteCopyFile(string relativePath, bool executable)
    {
        var full = Path.Combine(_copy, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "#!/bin/sh\necho hi\n");
        if (OperatingSystem.IsWindows())
        {
            // CopyExecutableBitFactAttribute already skips every caller on Windows; the platform
            // analyser wants the guard in the code too.
            return full;
        }

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (executable)
        {
            mode |= UnixFileMode.UserExecute;
        }

        File.SetUnixFileMode(full, mode);
        return full;
    }

    /// <summary>
    /// Only <see cref="MarkExecutableAsync"/> is exercised by <see cref="ExecutableBitStaging"/>; every
    /// other member is unused here and throws if that ever stops being true.
    /// </summary>
    private sealed class RecordingTreeService : IGitWorkingTreeService
    {
        public List<string> MarkedExecutable { get; } = [];

        public bool Fails { get; set; }

        public Task MarkExecutableAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            if (Fails)
            {
                throw new InvalidOperationException("git update-index failed: fatal: Unable to create '.git/index.lock': File exists.");
            }

            MarkedExecutable.AddRange(paths);
            return Task.CompletedTask;
        }

        public Task<GitWorkingStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task StageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<GitCommitOutcome> CommitAsync(string message, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<(string Original, string Modified)> GetWorkingDiffAsync(
            string path, bool staged, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<GitCommitOutcome> StashAsync(
            string message, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<GitStash>> GetStashesAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<GitCommitOutcome> StashPopAsync(int index, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<GitCommitOutcome> StashApplyAsync(int index, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
