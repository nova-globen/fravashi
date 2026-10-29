using AwesomeAssertions;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Git;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Git;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.WorkItems;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Git;

/// <summary>
/// #327, at the seam <c>git_commit</c> and <c>attempt_commit</c> both call: when the session has a
/// contained copy and marking a file executable on the host fails (an index.lock from a concurrent
/// writer is the realistic case), the commit answers as a FAILURE, never a quiet success that leaves the
/// file at <c>100644</c>, the very symptom #327 describes.
/// </summary>
public sealed class SessionGitWorkExecutableBitTests : IDisposable
{
    private readonly SqliteTestContext _store = new();
    private readonly AgentSessionId _session = AgentSessionId.New();
    private readonly string _copy =
        Path.Combine(Path.GetTempPath(), "fravashi-copy-" + Guid.NewGuid().ToString("N")[..8]);

    public SessionGitWorkExecutableBitTests() => Directory.CreateDirectory(_copy);

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_copy, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [CopyExecutableBitFact]
    public async Task A_failed_mark_executable_call_fails_the_commit_rather_than_answering_success()
    {
        WriteExecutableCopyFile("run.sh");

        var sessions = new SqliteSessionStore(_store.Factory);
        sessions.Save(new SessionRecord(
            _session, "w1", AgentTool.Claude, "t", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null));

        var tree = Substitute.For<IGitWorkingTreeService>();
        tree.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new GitWorkingStatus(
            "main", null, 0, 0,
            [new GitWorkingEntry("run.sh", GitEntryState.Added, Staged: true)],
            []));
        tree.MarkExecutableAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException(
                "git update-index failed: fatal: Unable to create '.git/index.lock': File exists.")));

        var gitService = Substitute.For<IGitService>();
        gitService.IsAvailable.Returns(true);
        var services = new WorkspaceGitServices(
            "w1", Substitute.For<IGitCommandRunner>(), gitService, tree, Substitute.For<IGitRemoteService>());

        var workspaceGit = Substitute.For<IWorkspaceGit>();
        workspaceGit.For("w1").Returns(services);

        var sync = Substitute.For<IWorkspaceAgentSync>();
        sync.CopyPathFor("w1").Returns(_copy);
        sync.SettleAsync("w1", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(true));

        var git = new SessionGitWork(
            new SessionGit(sessions, workspaceGit, sync),
            new SqliteSessionChangedFiles(_store.Factory, TimeProvider.System),
            new SqliteSessionBranches(_store.Factory),
            new SqliteSessionWorkItems(_store.Factory, TimeProvider.System),
            new LocalTrackerResolver(_store.Factory, TimeProvider.System),
            TimeProvider.System);

        var result = await git.CommitAsync(_session, "add a script", paths: ["run.sh"]);

        result.Sha.Should().BeNull();
        result.Error.Should().Contain("The commit failed").And.Contain("index.lock");
        await tree.DidNotReceive().CommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private void WriteExecutableCopyFile(string relativePath)
    {
        var full = Path.Combine(_copy, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "#!/bin/sh\necho hi\n");
        if (OperatingSystem.IsWindows())
        {
            // CopyExecutableBitFactAttribute already skips every caller on Windows; the platform
            // analyser wants the guard in the code too.
            return;
        }

        File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
