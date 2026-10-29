using System.Collections.Concurrent;
using Fravashi.Infrastructure.AgentSync;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Tests.Bridge;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentSync;

/// <summary>
/// Backlog 247: a file an agent saved several times in quick succession vanished from the copy AND the host. Measured
/// in the studio's container on 2026-10-07 (the sequence is on the item): the agent CLI's Edit and Write are atomic (a
/// <c>&lt;name&gt;.tmp.&lt;pid&gt;.&lt;hex&gt;</c> file renamed over the target, which is never missing), and what deleted
/// the target was the ENGINE: the bridge writes the host atomically too, Windows reports that as "deleted" then
/// "renamed", and the engine applied the delete to the copy without looking. When it did so between the bridge write
/// and the write-back recording its stamp, its own copy delete went back to the host as the agent's.
/// <para>
/// The bridge here is the REAL one, but the host runs Linux in CI, where a rename over a file is not reported as a
/// delete. So the client below delivers the host events Windows raises for the bridge's own write, from another task
/// as the SSE stream does, and at the moment that used to lose the file. Real host events are not delivered at all,
/// so what the engine sees from the host is exactly what each test says.
/// </para>
/// </summary>
public sealed class WorkspaceSyncDeleteIsASaveTests : IAsyncLifetime
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "fravashi-agent-sync-tests", Guid.NewGuid().ToString("N"));
    private string _host = null!;
    private string _copyRoot = null!;
    private HostedBridge _bridge = null!;
    private WindowsShapedHostClient _client = null!;
    private readonly LinesLogger _log = new();

    public async Task InitializeAsync()
    {
        _host = Path.Combine(_base, "host");
        _copyRoot = Path.Combine(_base, "copies");
        Directory.CreateDirectory(Path.Combine(_host, "src"));
        Directory.CreateDirectory(_copyRoot);
        File.WriteAllText(Path.Combine(_host, "README.md"), "hello\n");
        File.WriteAllText(Path.Combine(_host, "src", "Program.cs"), "class P { }\n");
        _bridge = await HostedBridge.StartAsync(_host);
        _client = new WindowsShapedHostClient(_bridge);
    }

    private async Task<WorkspaceSyncSession> StartedAsync(TimeSpan? debounce = null)
    {
        var session = new WorkspaceSyncSession(
            _client, _bridge.WorkspaceId, _copyRoot, WorkspaceSyncIgnore.FromGitignoreText(null), logger: _log)
        {
            DebounceInterval = debounce ?? TimeSpan.FromMilliseconds(300),
        };
        await session.StartAsync(CancellationToken.None);
        return session;
    }

    private string CopyPath(string relative) => Path.Combine(
        _copyRoot, _bridge.WorkspaceId, relative.Replace('/', Path.DirectorySeparatorChar));

    private string HostPath(string relative) => Path.Combine(_host, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>What the CLI's Edit and Write do to a file, as measured: a temp file beside it, renamed over it.</summary>
    private void CliSave(string relative, string content, int save)
    {
        var temp = CopyPath($"{relative}.tmp.627794.{save:x12}");
        File.WriteAllText(temp, content);
        File.Move(temp, CopyPath(relative), overwrite: true);
    }

    /// <summary>
    /// Backlog 317: the engine decides from the copy as it is when a path falls due, so a stall of this thread longer
    /// than the debounce between <see cref="CliSave"/>'s write and its rename lets the TEMPORARY file fall due while it
    /// exists. It is then rightly mirrored to the host and, once renamed away, rightly sent as a delete (measured on
    /// 2026-10-13, recorded on #317: a 1000 ms sleep there failed TheMeasuredEditSequence 3 of 3 with CI run 488's
    /// message, and SevenRapid 3 of 3 on the same temporary file's delete). That is not what
    /// these tests are about, so they assert that the REAL file is never sent as a delete, that anything sent as one is
    /// an agent temporary, and that none of those is left on the host. The debounce stays at 300 ms: the measured gaps
    /// mean something only beside it.
    /// </summary>
    private void OnlyTemporaryFilesWereDeleted(string relative)
    {
        _client.Deletes.Should().NotContain(relative, "nothing was deleted, only saved");
        _client.Deletes.Where(d => !d.StartsWith($"{relative}.tmp.627794.", StringComparison.Ordinal))
            .Should().BeEmpty("only the agent's own temporary files may be sent as a delete");
        Directory.GetFiles(_host, Path.GetFileName(relative) + ".tmp.*", SearchOption.AllDirectories)
            .Should().BeEmpty("a temporary file that reached the host is removed from it again");
    }

    [Fact]
    public async Task TheMeasuredEditSequence_LeavesTheHostWithTheLastContent_AndNeverDeletesIt()
    {
        await using var session = await StartedAsync();
        using var watch = new Presence(HostPath("README.md"), CopyPath("README.md"));

        // Four Edits then a Write, at the gaps measured between them (ms): each one lands while the previous one's
        // write-back and its host echo are in flight.
        var gaps = new[] { 0, 700, 3080, 190, 1910 };
        for (var save = 0; save < gaps.Length; save++)
        {
            await Task.Delay(gaps[save]); // the measured cadence of the agent's saves, not a propagation wait
            CliSave("README.md", $"version {save}\n", save);
        }

        (await session.SettleAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
        await QuietAsync(session);

        (await File.ReadAllTextAsync(HostPath("README.md"))).Should().Be("version 4\n");
        (await File.ReadAllTextAsync(CopyPath("README.md"))).Should().Be("version 4\n");
        OnlyTemporaryFilesWereDeleted("README.md");
        watch.HostEverMissing.Should().BeFalse("the host file is never deleted, not even for a moment");
        watch.CopyEverMissing.Should().BeFalse("the agent's next Edit must never find 'File does not exist'");
        _client.EchoesDelivered.Should().BeGreaterThan(0, "the host echo of each write-back was really exercised");
    }

    [Fact]
    public async Task SevenRapidTempThenRenameSaves_AreWrittenBackAsTheLastContent()
    {
        await using var session = await StartedAsync();
        using var watch = new Presence(HostPath("README.md"), CopyPath("README.md"));

        for (var save = 0; save < 7; save++)
        {
            CliSave("README.md", $"rapid {save}\n", save);
            await Task.Delay(120); // inside the debounce for some, across it for others: both are what an agent does
        }

        (await session.SettleAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
        await QuietAsync(session);

        (await File.ReadAllTextAsync(HostPath("README.md"))).Should().Be("rapid 6\n");
        OnlyTemporaryFilesWereDeleted("README.md");
        watch.HostEverMissing.Should().BeFalse();
        watch.CopyEverMissing.Should().BeFalse();
    }

    /// <summary>
    /// Backlog 263: the engine decides from the copy as it is when a path falls due, so a file that really is absent for
    /// a whole debounce is rightly sent as a delete. Measured in the studio's container on 2026-10-08: a 1000 ms sleep
    /// between the delete and the recreate failed this test 5 of 5 with the release runner's message, at 300 ms. So the
    /// delete and the recreate are one synchronous block (no await to hop threads between them), and the debounce is two
    /// seconds, which a stall of this thread has to outlast before it changes what is sent.
    /// </summary>
    [Fact]
    public async Task DeleteThenCreate_IsOneSave_AndTheLogSaysTheDeleteWasTreatedAsASave()
    {
        var debounce = TimeSpan.FromSeconds(2);
        await using var session = await StartedAsync(debounce);
        using var watch = new Presence(HostPath("README.md"), copy: null);

        for (var save = 0; save < 7; save++)
        {
            File.Delete(CopyPath("README.md"));
            File.WriteAllText(CopyPath("README.md"), $"recreated {save}\n");
            await Task.Delay(50); // seven saves inside one debounce
        }

        (await session.SettleAsync(TimeSpan.FromSeconds(30))).Should().BeTrue();
        await QuietAsync(session, debounce);

        (await File.ReadAllTextAsync(HostPath("README.md"))).Should().Be("recreated 6\n");
        _client.Deletes.Should().BeEmpty();
        watch.HostEverMissing.Should().BeFalse();
        _log.Lines.Should().Contain(l =>
            l.Level == LogLevel.Information
            && l.Message.Contains("A delete of 'README.md' in the agent copy")
            && l.Message.Contains("was treated as a save because the file is there again"));
    }

    [Fact]
    public async Task ALoneDelete_StillReachesTheHost()
    {
        await using var session = await StartedAsync();

        File.Delete(CopyPath("README.md"));

        (await session.SettleAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
        File.Exists(HostPath("README.md")).Should().BeFalse();
        _client.Deletes.Should().Equal("README.md");
        File.Exists(HostPath("src/Program.cs")).Should().BeTrue("only the file the agent deleted goes");
        _log.Lines.Should().NotContain(l => l.Message.Contains("treated as a save"));
    }

    [Fact]
    public async Task AnAtomicSaveOnTheHost_ArrivesAsAChange_TheCopyIsNeverDeleted_AndItIsNotEchoedBack()
    {
        await using var session = await StartedAsync();
        using var watch = new Presence(host: null, CopyPath("README.md"));

        // An editor on the host saving atomically, as Windows reports it: a delete of the file, then its backup
        // renamed into place. The recognisable timestamp is how an echo written back would show (backlog 123).
        var recognisable = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var temp = HostPath("README.md~");
        await File.WriteAllTextAsync(temp, "saved by a person on the host\n");
        File.Move(temp, HostPath("README.md"), overwrite: true);
        File.SetLastWriteTimeUtc(HostPath("README.md"), recognisable);
        await _client.HostEventAsync("deleted", "README.md");
        await _client.HostEventAsync("renamed", "README.md", "README.md~");

        await WaitUntilAsync(() => File.ReadAllText(CopyPath("README.md")) == "saved by a person on the host\n");
        await WaitUntilAsync(() => _log.Lines.Any(l =>
            l.Level == LogLevel.Information
            && l.Message.Contains("A host delete of 'README.md'")
            && l.Message.Contains("was treated as a save because the file is there again")));

        // "It was not echoed back" needs a moment after which it would have been: the write-back loop is sequential,
        // so once a later copy file is on the host, the pass that handled the echo has finished. Not a delay.
        await File.WriteAllTextAsync(CopyPath("marker.txt"), "x\n");
        await WaitUntilAsync(() => File.Exists(HostPath("marker.txt")));

        File.GetLastWriteTimeUtc(HostPath("README.md")).Should().Be(recognisable);
        watch.CopyEverMissing.Should().BeFalse("a host delete is not applied until the host is asked again");
        _client.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task ALoneHostDelete_StillRemovesTheCopyFile()
    {
        await using var session = await StartedAsync();

        File.Delete(HostPath("README.md"));
        await _client.HostEventAsync("deleted", "README.md");

        await WaitUntilAsync(() => !File.Exists(CopyPath("README.md")));
        _client.Deletes.Should().BeEmpty("the host's delete is not sent back to it");
    }

    /// <summary>
    /// Waits for the engine to have handled the host echo of its LAST write-back: the echo's held delete is settled one
    /// debounce after it arrives, so the copy is quiet for that long with nothing pending.
    /// </summary>
    private async Task QuietAsync(WorkspaceSyncSession session, TimeSpan? debounce = null)
    {
        // The held delete is settled one debounce after it arrives, so the wait scales with it: 1000 ms at the 300 ms
        // the product uses, as before.
        var held = (debounce ?? TimeSpan.FromMilliseconds(300)) + TimeSpan.FromMilliseconds(700);
        var settle = held * 15;
        await _client.EchoesSettledAsync();
        (await session.SettleAsync(settle)).Should().BeTrue();
        await Task.Delay(held); // long enough for a wrongly held delete to have been applied; nothing is waited FOR
        (await session.SettleAsync(settle)).Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(50); // poll interval inside this wait helper (backlog 107)
        }

        condition().Should().BeTrue("the condition should hold within {0}ms", timeoutMs);
    }

    /// <summary>Samples whether a file was ever missing while a test ran: a delete and a re-create would otherwise go unseen.</summary>
    private sealed class Presence : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private volatile bool _hostMissing;
        private volatile bool _copyMissing;

        public Presence(string? host, string? copy)
        {
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    if (host is not null && !File.Exists(host))
                    {
                        _hostMissing = true;
                    }

                    if (copy is not null && !File.Exists(copy))
                    {
                        _copyMissing = true;
                    }

                    await Task.Delay(2);
                }
            });
        }

        public bool HostEverMissing => _hostMissing;

        public bool CopyEverMissing => _copyMissing;

        public void Dispose()
        {
            _stop.Cancel();
            _loop.Wait();
            _stop.Dispose();
        }
    }

    /// <summary>
    /// The real bridge client, with the host's live events replaced by the ones each test delivers, and the bridge's
    /// own write followed by what a WINDOWS host reports for it (<c>BridgeFileService.WriteAsync</c> writes
    /// <c>&lt;path&gt;.fravashi-tmp</c> and moves it over the target): created temp, deleted target, renamed temp to
    /// target. The delete is delivered before the write returns, from another task as the SSE stream would, which is the
    /// interleaving that lost #247's file; the rename follows a moment later.
    /// </summary>
    private sealed class WindowsShapedHostClient(HostedBridge bridge)
        : BridgeClient(new HttpClient(), bridge.CreateContext())
    {
        private readonly TaskCompletionSource<Func<BridgeFileEventDto, Task>> _subscriber =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<Task> _echoes = new();
        private int _echoesDelivered;

        public ConcurrentQueue<string> Deletes { get; } = new();

        public int EchoesDelivered => Volatile.Read(ref _echoesDelivered);

        public override async Task SubscribeEventsAsync(Func<BridgeFileEventDto, Task> onEvent, CancellationToken ct)
        {
            _subscriber.TrySetResult(onEvent);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async Task HostEventAsync(string type, string path, string? oldPath = null) =>
            await (await _subscriber.Task)(new BridgeFileEventDto(type, path, oldPath));

        public Task EchoesSettledAsync() => Task.WhenAll(_echoes.ToArray());

        public override async Task DeleteAsync(string relativePath, CancellationToken ct)
        {
            Deletes.Enqueue(relativePath);
            await base.DeleteAsync(relativePath, ct);
        }

        public override async Task<DateTimeOffset> WriteAsync(
            string relativePath, Stream content, DateTimeOffset? expectedModifiedUtc, CancellationToken ct)
        {
            var stamp = await base.WriteAsync(relativePath, content, expectedModifiedUtc, ct);
            var temp = relativePath + ".fravashi-tmp";
            var deleted = Task.Run(async () =>
            {
                await HostEventAsync("created", temp);
                await HostEventAsync("deleted", relativePath);
            });

            // The engine before #247 applied the delete right here, before it recorded the stamp. The engine now holds
            // host events while a write-back runs, so this gives up waiting rather than deadlock on it.
            await Task.WhenAny(deleted, Task.Delay(200, ct));
            _echoes.Enqueue(Task.Run(async () =>
            {
                try
                {
                    await deleted;
                    await Task.Delay(50); // the rename arrives after the delete, as Windows reports it
                    await HostEventAsync("renamed", relativePath, temp);
                    Interlocked.Increment(ref _echoesDelivered);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    // The session ended with the echo still on its way, as a real stream can.
                }
            }));
            return stamp;
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
}
