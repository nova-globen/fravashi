using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Fravashi.Application.Repository;
using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Configuration;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Bridge;

/// <summary>
/// Backlog 257: the Code page's half of what #253 did for the agent copy. The page learns about host files only from
/// the bridge's event stream, and the bridge keeps nothing for a client that is not connected, so a file created while
/// the stream was down never reached the explorer or an open buffer. Each (re)connection and each overflow notice now
/// becomes one "anything changed" change (an empty path, as the local watcher's overflow always was), at most once per
/// 30 s. And the stream has a heartbeat: a connection that dies without a close is dropped after a silence, but only
/// once this connection has PROVED it carries heartbeats, because an older fravashi.exe sends none.
/// </summary>
public sealed class BridgeStreamGapTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-stream-gap-tests", Guid.NewGuid().ToString("N"));
    private HostedBridge _bridge = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
        _bridge = await HostedBridge.StartAsync(_root);
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_file_created_on_the_host_while_the_stream_is_down_reaches_the_code_page_after_it_returns()
    {
        var events = new SeverableEvents();
        var client = new BridgeClient(new HttpClient(events), _bridge.CreateContext());
        using var subscriber = new BridgeChangeSubscriber(client) { MinimumLookAgainSpacing = TimeSpan.FromMilliseconds(200) };
        var seen = new ConcurrentQueue<RepositoryChange>();
        using var subscription = subscriber.Subscribe(seen.Enqueue);
        await WaitForLiveStreamAsync(seen);
        var connections = events.Connections;

        events.Sever();
        File.WriteAllText(Path.Combine(_root, "src", "made-in-the-gap.cs"), "class Gap { }\n");
        var lookAgainBefore = LookAgains(seen);
        events.Restore();

        await WaitUntilAsync(() => events.Connections > connections, "the stream never came back");
        await WaitUntilAsync(
            () => LookAgains(seen) > lookAgainBefore,
            "the Code page was never told to look again after the stream returned");

        // The page reloads its tree from the bridge on that change (CodePageTests shows the explorer gaining the file);
        // here, that the tree it reloads holds it.
        var tree = await new BridgeRepositoryReader(client).GetTreeAsync("src");
        tree.Children.Should().Contain(n => n.RelativePath == "src/made-in-the-gap.cs");
    }

    [Fact]
    public void The_look_again_is_not_repeated_within_30_seconds_and_a_request_inside_the_window_is_kept()
    {
        var clock = new HandMovedClock();
        var client = new PushedEventsClient();
        using var subscriber = new BridgeChangeSubscriber(client, clock);
        var seen = new ConcurrentQueue<RepositoryChange>();
        using var subscription = subscriber.Subscribe(seen.Enqueue);
        client.WaitUntilSubscribed();

        client.Push(BridgeClient.ConnectedEventType);
        LookAgains(seen).Should().Be(1, "the first connection asks at once");

        client.Push(BridgeClient.ConnectedEventType);
        client.Push(BridgeClient.OverflowEventType);
        clock.Advance(TimeSpan.FromSeconds(29));
        LookAgains(seen).Should().Be(1, "nothing repeats it within 30 s, whatever asks");

        clock.Advance(TimeSpan.FromSeconds(1));
        LookAgains(seen).Should().Be(2, "the requests made inside the window are one look, at its end, never dropped");

        clock.Advance(TimeSpan.FromMinutes(5));
        LookAgains(seen).Should().Be(2, "nothing asked since");

        client.Push(BridgeClient.OverflowEventType);
        LookAgains(seen).Should().Be(3, "a request after the window is answered at once");
    }

    [Fact]
    public async Task A_stream_that_goes_silent_after_a_heartbeat_is_dropped_and_reconnected_within_the_limit()
    {
        var bridge = new ScriptedEvents(heartbeatFirst: true);
        var limit = TimeSpan.FromMilliseconds(300);
        var client = new BridgeClient(new HttpClient(bridge), FakeContext()) { EventSilenceLimit = limit };
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(() => client.SubscribeEventsAsync(_ => Task.CompletedTask, cts.Token));

        await WaitUntilAsync(() => bridge.Connections >= 2, "a stream that went silent after its heartbeat was never dropped");

        var gap = bridge.ConnectedAt[1] - bridge.ConnectedAt[0];
        gap.Should().BeGreaterThanOrEqualTo(limit - TimeSpan.FromMilliseconds(50), "it waits out the silence limit first");
        gap.Should().BeLessThan(limit + TimeSpan.FromSeconds(2) + TimeSpan.FromSeconds(2), "the limit plus the usual retry delay");
        await cts.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task A_stream_from_a_bridge_that_never_sends_a_heartbeat_is_not_dropped_while_quiet()
    {
        // An older fravashi.exe: it sends events (here one) and never a comment line, then nothing for a long time.
        var bridge = new ScriptedEvents(heartbeatFirst: false);
        var client = new BridgeClient(new HttpClient(bridge), FakeContext()) { EventSilenceLimit = TimeSpan.FromMilliseconds(200) };
        using var cts = new CancellationTokenSource();
        var seen = new ConcurrentQueue<BridgeFileEventDto>();
        var loop = Task.Run(() => client.SubscribeEventsAsync(e =>
        {
            seen.Enqueue(e);
            return Task.CompletedTask;
        }, cts.Token));

        await WaitUntilAsync(() => seen.Any(e => e.Type == "created"), "the old bridge's event never arrived");
        // Fifteen silence limits of a quiet workspace, and longer than a limit plus the 2 s retry, so a drop would show.
        await Task.Delay(TimeSpan.FromSeconds(3));

        bridge.Connections.Should().Be(1, "a connection that never proved it carries heartbeats is never dropped for silence");
        await cts.CancelAsync();
        await loop;
    }

    private static int LookAgains(IEnumerable<RepositoryChange> seen) => seen.Count(c => c.RelativePath.Length == 0);

    /// <summary>Re-touches a probe until its change round-trips, so the stream is provably live (backlog 107).</summary>
    private async Task WaitForLiveStreamAsync(ConcurrentQueue<RepositoryChange> seen)
    {
        var probe = Path.Combine(_root, "probe.txt");
        await WaitUntilAsync(() =>
        {
            File.WriteAllText(probe, Guid.NewGuid().ToString("N"));
            return seen.Any(c => c.RelativePath == "probe.txt");
        }, "the stream never delivered the probe");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string describe, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50); // poll interval inside this wait helper, not an arm-window guess (backlog 107)
        }

        condition().Should().BeTrue("{0} within {1}ms", describe, timeoutMs);
    }

    private static BridgeWorkspaceContext FakeContext() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://bridge.invalid:1",
            [BridgeWorkspaceContext.TokenKey] = "t",
            [BridgeWorkspaceContext.ActiveIdKey] = "ws",
        })
        .Build());

    /// <summary>
    /// A bridge whose event stream the test writes: each connection gets a fresh stream that stays open and quiet
    /// after what it was scripted to send, as a connection that died without a close does (sleep and resume, a network
    /// switch). <paramref name="heartbeatFirst"/> is a new bridge (one heartbeat, then silence); false is an older one
    /// (one ordinary event and no comment line ever).
    /// </summary>
    private sealed class ScriptedEvents(bool heartbeatFirst) : HttpMessageHandler
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<TimeSpan> _connectedAt = [];

        public int Connections
        {
            get
            {
                lock (_connectedAt)
                {
                    return _connectedAt.Count;
                }
            }
        }

        public IReadOnlyList<TimeSpan> ConnectedAt
        {
            get
            {
                lock (_connectedAt)
                {
                    return [.. _connectedAt];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var stream = new QuietStream();
            stream.Push(heartbeatFirst
                ? ": heartbeat\n\n"
                : "data: {\"type\":\"created\",\"path\":\"a.txt\",\"oldPath\":null}\n\n");
            lock (_connectedAt)
            {
                _connectedAt.Add(_clock.Elapsed);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }
    }

    /// <summary>A stream that hands out what was pushed and otherwise waits, honouring cancellation, and never ends.</summary>
    private sealed class QuietStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[] _current = [];
        private int _offset;

        public void Push(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _current.Length)
            {
                _current = await _chunks.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A client whose stream events the test delivers by hand, on its own thread.</summary>
    private sealed class PushedEventsClient() : BridgeClient(new HttpClient(), FakeContext())
    {
        private readonly ManualResetEventSlim _subscribed = new();
        private Func<BridgeFileEventDto, Task>? _onEvent;

        public void WaitUntilSubscribed() => _subscribed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the subscriber never subscribed");

        public void Push(string type) => _onEvent!(new BridgeFileEventDto(type, string.Empty, null)).GetAwaiter().GetResult();

        public override async Task SubscribeEventsAsync(Func<BridgeFileEventDto, Task> onEvent, CancellationToken ct)
        {
            _onEvent = onEvent;
            _subscribed.Set();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>A clock whose timers fire only when the test calls <see cref="Advance"/>, on the test's thread.</summary>
    private sealed class HandMovedClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<HandTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 8, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new HandTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<HandTimer> due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(t => t.Due is { } at && at <= _now)];
                foreach (var timer in due)
                {
                    timer.Due = null;
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class HandTimer(HandMovedClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (clock._gate)
                {
                    Due = null;
                    clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
