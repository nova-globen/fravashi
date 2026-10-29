using System.Collections.Concurrent;
using System.Diagnostics;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.AgentSync;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Tests.Bridge;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentSync;

/// <summary>
/// Backlog 253: <c>setup/release/manifest-signing-public.pem</c>, committed on the host, never reached a live
/// conversation's copy. The engine learned about a host file only from the bridge's live events and never looked at the
/// host again after the start pull, so every way an event can be lost lost the file for the rest of the session. Each
/// test here loses one the way the real stack does (the real bridge, real folders, a real git where git matters) and
/// asserts the file still arrives; each failed before the engine rescanned the host. The rules are not weakened: what
/// the host's git ignores stays out, and a TRACKED file is never withheld, as in git.
/// </summary>
public sealed class WorkspaceSyncMissedHostEventTests : IAsyncLifetime
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "fravashi-agent-sync-tests", Guid.NewGuid().ToString("N"));
    private readonly LinesLogger _log = new();
    private string _host = null!;
    private string _copyRoot = null!;
    private HostedBridge _bridge = null!;

    public async Task InitializeAsync()
    {
        _host = Path.Combine(_base, "host");
        _copyRoot = Path.Combine(_base, "copies");
        Directory.CreateDirectory(Path.Combine(_host, "src"));
        Directory.CreateDirectory(_copyRoot);
        Write("README.md", "hello\n");
        Write("src/Program.cs", "class P { }\n");
        _bridge = await HostedBridge.StartAsync(_host);
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private WorkspaceSyncSession Session(BridgeClient client, WorkspaceSyncIgnore? ignore = null, TimeSpan? interval = null) =>
        new(client, _bridge.WorkspaceId, _copyRoot, ignore ?? WorkspaceSyncIgnore.FromGitignoreText(null), logger: _log)
        {
            MinimumRescanSpacing = TimeSpan.FromMilliseconds(200),
            RescanInterval = interval ?? TimeSpan.FromMinutes(10),
        };

    [Fact]
    public async Task A_file_created_on_the_host_while_the_stream_is_down_reaches_the_copy_after_it_returns()
    {
        var events = new SeverableEvents();
        var client = new BridgeClient(new HttpClient(events), _bridge.CreateContext());
        await using var session = Session(client);
        await session.StartAsync(CancellationToken.None);
        await WaitForLiveHostWatchAsync();
        var connections = events.Connections;

        events.Sever();
        Write("setup/release/manifest-signing-public.pem", "-----BEGIN PUBLIC KEY-----\n");
        events.Restore();

        await WaitUntilAsync(() => events.Connections > connections, describe: () => "the stream never came back");
        await WaitUntilAsync(
            () => File.Exists(CopyPath("setup/release/manifest-signing-public.pem")),
            describe: () => "the file created while the stream was down never reached the copy");
    }

    [Fact]
    public async Task A_file_created_between_the_start_pull_and_the_live_watch_reaches_the_copy()
    {
        var client = new LateFileClient(_bridge, () => Write("late.txt", "made while the pull ran\n"));
        await using var session = Session(client);
        await session.StartAsync(CancellationToken.None);

        File.Exists(CopyPath("late.txt")).Should().BeFalse("the pull had listed the root before the file was made");
        await WaitUntilAsync(() => File.Exists(CopyPath("late.txt")), describe: () => "the start gap was never closed");
    }

    [Fact]
    public async Task A_timeout_while_applying_one_host_event_does_not_end_the_live_watch()
    {
        var client = new TimingOutReadClient(_bridge, "slow.txt");
        await using var session = Session(client);
        await session.StartAsync(CancellationToken.None);
        await WaitForLiveHostWatchAsync();

        Write("slow.txt", "its read times out once\n");
        await WaitUntilAsync(() => client.TimedOut, describe: () => "the read never timed out");
        Write("after.txt", "made after the timeout\n");

        await WaitUntilAsync(() => File.Exists(CopyPath("after.txt")), describe: () => "the live watch ended at the timeout");
        await WaitUntilAsync(() => File.Exists(CopyPath("slow.txt")), describe: () => "the file whose read timed out was never fetched again");
    }

    [Fact]
    public async Task A_new_host_file_whose_ignore_question_went_unanswered_arrives_once_the_host_can_answer()
    {
        Git("init");
        var client = new FlakyCheckIgnoreClient(_bridge);
        var host = await HostGitIgnore.LoadAsync(client, _log, CancellationToken.None);
        await using var session = Session(client, WorkspaceSyncIgnore.FromGitignoreText(null, host));
        await session.StartAsync(CancellationToken.None);
        await WaitForLiveHostWatchAsync();

        client.Failing = true;
        Write("fresh.txt", "the host's git could not be asked about me\n");
        await WaitUntilAsync(() => client.Refused > 0, describe: () => "check-ignore was never asked");
        client.Failing = false;

        await WaitUntilAsync(() => File.Exists(CopyPath("fresh.txt")), describe: () => "an unanswered question dropped the file for good");
    }

    [Fact]
    public async Task A_tracked_file_under_an_ignore_rule_is_synced_and_an_untracked_one_stays_out()
    {
        Git("init");
        Write(".gitignore", "*.pem\nout/\n");
        Write("setup/release/public.pem", "-----BEGIN PUBLIC KEY-----\n");
        Write("setup/release/private.pem", "-----BEGIN PRIVATE" + " KEY-----\n");
        Write("out/kept.txt", "force-added\n");
        Write("out/built.dll", "x\n");
        Git("add", "-f", "setup/release/public.pem", "out/kept.txt");

        var client = _bridge.CreateClient();
        var host = await HostGitIgnore.LoadAsync(client, _log, CancellationToken.None);
        await using var session = Session(client, WorkspaceSyncIgnore.FromGitignoreText("*.pem\nout/\n", host));
        await session.StartAsync(CancellationToken.None);

        File.Exists(CopyPath("setup/release/public.pem")).Should().BeTrue("a tracked file is not subject to ignore rules, as in git");
        File.Exists(CopyPath("out/kept.txt")).Should().BeTrue("a folder holding a tracked file is entered");
        File.Exists(CopyPath("setup/release/private.pem")).Should().BeFalse("the host's git ignores it");
        File.Exists(CopyPath("out/built.dll")).Should().BeFalse("the host's git ignores it");
    }

    [Fact]
    public async Task A_withheld_path_is_logged_with_its_reason()
    {
        Git("init");
        Write(".gitignore", "*.pem\n");
        Write("private.pem", "-----BEGIN PRIVATE" + " KEY-----\n");

        var client = _bridge.CreateClient();
        var host = await HostGitIgnore.LoadAsync(client, _log, CancellationToken.None);
        await using var session = Session(client, WorkspaceSyncIgnore.FromGitignoreText("*.pem\n", host));
        await session.StartAsync(CancellationToken.None);
        await WaitForLiveHostWatchAsync();
        Write("later.pem", "made during the session\n");
        Write("marker.txt", "after it\n");
        await WaitUntilAsync(() => File.Exists(CopyPath("marker.txt")));

        File.Exists(CopyPath("later.pem")).Should().BeFalse();
        var withheld = _log.Lines.Where(l => l.Level == LogLevel.Debug && l.Message.Contains("withheld", StringComparison.Ordinal)).ToList();
        withheld.Should().Contain(l => l.Message.Contains("'private.pem'", StringComparison.Ordinal)
            && l.Message.Contains("*.pem", StringComparison.Ordinal), "the line names the path and the rule");
        withheld.Should().Contain(l => l.Message.Contains("'later.pem'", StringComparison.Ordinal)
            && l.Message.Contains("*.pem", StringComparison.Ordinal), "a host event's decision is logged too");
    }

    [Fact]
    public async Task A_file_that_becomes_tracked_during_the_session_is_asked_about_again_and_arrives()
    {
        Git("init");
        Write(".gitignore", "*.pem\n");
        Write("late.pem", "-----BEGIN PUBLIC KEY-----\n");
        Write("still-ignored.pem", "-----BEGIN PRIVATE" + " KEY-----\n");

        var client = _bridge.CreateClient();
        var host = await HostGitIgnore.LoadAsync(client, _log, CancellationToken.None);
        await using var session = Session(
            client, WorkspaceSyncIgnore.FromGitignoreText("*.pem\n", host), interval: TimeSpan.FromMilliseconds(500));
        await session.StartAsync(CancellationToken.None);
        File.Exists(CopyPath("late.pem")).Should().BeFalse("it is ignored and untracked at the start");

        Git("add", "-f", "late.pem");

        await WaitUntilAsync(() => File.Exists(CopyPath("late.pem")), describe: () => "a file that became tracked stayed withheld");
        File.Exists(CopyPath("still-ignored.pem")).Should().BeFalse("what the host's git still ignores stays out");
    }

    [Fact]
    public async Task A_rescan_that_replaces_an_agent_edit_the_host_never_got_says_so()
    {
        // The edit is withheld for carrying a held credential's value, so it is neither pending nor recorded: the one
        // copy edit a rescan can meet. The host wins, and it is said, as at start.
        var notes = new ConcurrentQueue<string>();
        await using var session = new WorkspaceSyncSession(
            new NoFileEventsClient(_bridge), _bridge.WorkspaceId, _copyRoot, WorkspaceSyncIgnore.FromGitignoreText(null),
            notes.Enqueue, () => [new AgentHeldCredential("TEST_TOKEN", "held-secret-value-253")], _log)
        {
            MinimumRescanSpacing = TimeSpan.FromMilliseconds(200),
            RescanInterval = TimeSpan.FromMilliseconds(500),
        };
        await session.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(CopyPath("README.md"), "the agent wrote held-secret-value-253\n");
        await WaitUntilAsync(() => notes.Any(n => n.Contains("was NOT written", StringComparison.Ordinal)));
        Write("README.md", "the host moved on\n");

        await WaitUntilAsync(() => session.Status.HostWinsConflicts > 0, describe: () => "the rescan replaced the edit silently");
        notes.Should().Contain(n => n.Contains("Agent copy conflict on 'README.md'", StringComparison.Ordinal));
        (await File.ReadAllTextAsync(CopyPath("README.md"))).Should().Be("the host moved on\n");
    }

    [Fact]
    public async Task A_rescan_that_keeps_failing_is_said_once_at_warning_and_its_recovery_too()
    {
        var client = new FailingListingClient(_bridge, "src");
        await using var session = Session(client, interval: TimeSpan.FromMilliseconds(300));
        await session.StartAsync(CancellationToken.None);

        client.Failing = true;
        await WaitUntilAsync(() => client.Refused >= 3, describe: () => "the rescan was not retried");
        client.Failing = false;
        await WaitUntilAsync(() => _log.Lines.Any(l => l.Level == LogLevel.Information && l.Message.Contains("works again", StringComparison.Ordinal)));

        _log.Lines.Where(l => l.Level == LogLevel.Warning && l.Message.Contains("rescan", StringComparison.Ordinal))
            .Should().ContainSingle("a lasting failure is said once, not once per retry")
            .Which.Message.Should().Contain("500");
    }

    [Fact]
    public async Task A_new_file_a_negation_lets_through_is_not_ignored_by_the_hosts_git_and_reaches_the_copy()
    {
        // This repository's own rule shape for the path #253 names, asked of a REAL git, plus a file-level negation: with
        // -v, git PRINTS a path whose deciding rule is a file-level negation (with its "!" pattern; measured, it does not
        // for the folder-level one above it), and such a path is not ignored. The engine's own reading of the root rules
        // is left out (null), so only the host's answer decides.
        Git("init");
        Write(".gitignore", "[Rr]elease/\n!setup/release/\n*.pem\n!*-public.pem\n");
        var client = _bridge.CreateClient();
        var host = await HostGitIgnore.LoadAsync(client, _log, CancellationToken.None);
        await using var session = Session(client, WorkspaceSyncIgnore.FromGitignoreText(null, host));
        await session.StartAsync(CancellationToken.None);
        await WaitForLiveHostWatchAsync();

        Write("build/release/output.txt", "build output\n");
        Write("setup/release/private.pem", "-----BEGIN PRIVATE" + " KEY-----\n");
        Write("setup/release/manifest-signing-public.pem", "-----BEGIN PUBLIC KEY-----\n");
        Write("marker.txt", "after both\n");

        await WaitUntilAsync(
            () => File.Exists(CopyPath("setup/release/manifest-signing-public.pem")) && File.Exists(CopyPath("marker.txt")),
            describe: () => "the negated path was taken as ignored");
        (await host.IgnoresAsync("setup/release/manifest-signing-public.pem", CancellationToken.None)).Should().BeFalse();
        (await host.IgnoresAsync("setup/release/private.pem", CancellationToken.None)).Should().BeTrue("the plain rule still holds");
        (await host.IgnoresAsync("build/release/output.txt", CancellationToken.None)).Should().BeTrue("the plain rule still holds");
        File.Exists(CopyPath("setup/release/private.pem")).Should().BeFalse("a sibling under the plain rule stays out");
        // On Linux a file written into a folder made a moment before can raise no event at all (the watch on the new
        // folder arms after), and then the rescan is what decides about it, so its line is waited for.
        await WaitUntilAsync(
            () => _log.Lines.Any(l => l.Level == LogLevel.Debug
                && l.Message.Contains("'build/release/output.txt'", StringComparison.Ordinal)
                && l.Message.Contains("[Rr]elease/", StringComparison.Ordinal)),
            describe: () => "the host's rule was not named for the withheld sibling");
        File.Exists(CopyPath("build/release/output.txt")).Should().BeFalse("a sibling under the plain rule stays out");
    }

    [Fact]
    public async Task The_files_of_a_folder_moved_into_the_host_folder_reach_the_copy()
    {
        // A move reports the folder and nothing for the files inside it, so no file event ever names them.
        var outside = Path.Combine(_base, "outside", "keys");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "public.pem"), "-----BEGIN PUBLIC KEY-----\n");
        Directory.CreateDirectory(Path.Combine(_host, "setup"));
        await using var session = Session(_bridge.CreateClient());
        await session.StartAsync(CancellationToken.None);
        await WaitForLiveHostWatchAsync();

        Directory.Move(outside, Path.Combine(_host, "setup", "keys"));

        await WaitUntilAsync(() => File.Exists(CopyPath("setup/keys/public.pem")), describe: () => "the moved folder's file never arrived");
    }

    [Fact]
    public void This_repositorys_own_rules_sync_the_file_253_found_missing()
    {
        // Candidate 3 on #253, killed: no rule here or in the bridge withholds a key-like name, and the
        // [Rr]elease/ rule this repository negates for setup/release/ is read the way git reads it.
        var ignore = WorkspaceSyncIgnore.FromGitignoreText(File.ReadAllText(Path.Combine(RepositoryRoot(), ".gitignore")));

        ignore.ShouldSync("setup/release", isDirectory: true).Should().BeTrue();
        ignore.ShouldSync("setup/release/manifest-signing-public.pem", isDirectory: false).Should().BeTrue();
        ignore.ShouldSync("src/Fravashi.Web/bin/Release", isDirectory: true).Should().BeFalse("the rule still means build output");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Fravashi.sln) was not found.");
    }

    /// <summary>Re-touches a sentinel until it reaches the copy, so the live watch is provably connected (backlog 107).</summary>
    private async Task WaitForLiveHostWatchAsync()
    {
        var sentinel = Path.Combine(_host, "src", "__watcharm__.probe");
        await WaitUntilAsync(() =>
        {
            File.WriteAllText(sentinel, Guid.NewGuid().ToString("N"));
            return File.Exists(CopyPath("src/__watcharm__.probe"));
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 20000, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!Holds(condition) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50); // poll interval inside this wait helper, not an arm-window guess (backlog 107)
        }

        Holds(condition).Should().BeTrue("{0} within {1}ms", describe?.Invoke() ?? "the condition should hold", timeoutMs);
    }

    private static bool Holds(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string CopyPath(string relative) => Path.Combine(
        _copyRoot, _bridge.WorkspaceId, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_host, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _host,
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
        process.ExitCode.Should().Be(0, "git {0} should succeed: {1}", string.Join(' ', args), process.StandardError.ReadToEnd());
    }

    /// <summary>The real client, which makes a host file the moment the start pull has listed the root.</summary>
    private sealed class LateFileClient(HostedBridge bridge, Action makeFile)
        : BridgeClient(new HttpClient(), bridge.CreateContext())
    {
        private int _made;

        public override async Task<IReadOnlyList<BridgeEntryDto>> ListAsync(string? relativePath, CancellationToken ct)
        {
            var entries = await base.ListAsync(relativePath, ct);
            if (string.IsNullOrEmpty(relativePath) && Interlocked.Exchange(ref _made, 1) == 0)
            {
                makeFile();
            }

            return entries;
        }
    }

    /// <summary>The real client, whose live stream delivers its connections and overflow notices but no file event.</summary>
    private sealed class NoFileEventsClient(HostedBridge bridge)
        : BridgeClient(new HttpClient(), bridge.CreateContext())
    {
        public override Task SubscribeEventsAsync(Func<BridgeFileEventDto, Task> onEvent, CancellationToken ct) =>
            base.SubscribeEventsAsync(
                e => e.Type is ConnectedEventType or OverflowEventType ? onEvent(e) : Task.CompletedTask, ct);
    }

    /// <summary>The real client, whose listing of one folder the bridge refuses with a 500 while <see cref="Failing"/> is set.</summary>
    private sealed class FailingListingClient(HostedBridge bridge, string folder)
        : BridgeClient(new HttpClient(), bridge.CreateContext())
    {
        private volatile bool _failing;
        private int _refused;

        public bool Failing
        {
            get => _failing;
            set => _failing = value;
        }

        public int Refused => Volatile.Read(ref _refused);

        public override Task<IReadOnlyList<BridgeEntryDto>> ListAsync(string? relativePath, CancellationToken ct)
        {
            if (_failing && relativePath == folder)
            {
                Interlocked.Increment(ref _refused);
                throw new HttpRequestException(
                    DescribeFailure($"listing {folder}", System.Net.HttpStatusCode.InternalServerError),
                    inner: null, System.Net.HttpStatusCode.InternalServerError);
            }

            return base.ListAsync(relativePath, ct);
        }
    }

    /// <summary>The real client, whose first read of one path fails the way the HTTP client's own timeout does.</summary>
    private sealed class TimingOutReadClient(HostedBridge bridge, string path)
        : BridgeClient(new HttpClient(), bridge.CreateContext())
    {
        private int _timedOut;

        public bool TimedOut => Volatile.Read(ref _timedOut) == 1;

        public override Task<BridgeFileContent?> ReadAsync(string relativePath, CancellationToken ct) =>
            relativePath == path && Interlocked.Exchange(ref _timedOut, 1) == 0
                ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException())
                : base.ReadAsync(relativePath, ct);
    }

    /// <summary>The real client, whose git refuses to answer check-ignore (exit 128) while <see cref="Failing"/> is set.</summary>
    private sealed class FlakyCheckIgnoreClient(HostedBridge bridge)
        : BridgeClient(new HttpClient(), bridge.CreateContext())
    {
        private volatile bool _failing;
        private int _refused;

        public bool Failing
        {
            get => _failing;
            set => _failing = value;
        }

        public int Refused => Volatile.Read(ref _refused);

        public override Task<BridgeGitResultDto> GitAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            if (_failing && args.Contains("check-ignore"))
            {
                Interlocked.Increment(ref _refused);
                return Task.FromResult(new BridgeGitResultDto(128, string.Empty, "fatal: index file corrupt (test)\n"));
            }

            return base.GitAsync(args, ct);
        }
    }

    private sealed class LinesLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _lines = new();

        public IReadOnlyCollection<(LogLevel Level, string Message)> Lines => _lines.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue((logLevel, formatter(state, exception)));
    }
}
