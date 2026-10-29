using AwesomeAssertions;
using Fravashi.Application.Repository;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

public sealed class RepositoryChangeWatcherTests : IDisposable
{
    private sealed class FixedOptionsMonitor(RepositoryReaderOptions value) : IOptionsMonitor<RepositoryReaderOptions>
    {
        public RepositoryReaderOptions CurrentValue => value;

        public RepositoryReaderOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<RepositoryReaderOptions, string?> listener) => null;
    }

    private readonly string _root;
    private readonly RepositoryChangeWatcher _watcher;

    public RepositoryChangeWatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fravashi-watch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        _watcher = new RepositoryChangeWatcher(
            new FixedOptionsMonitor(new RepositoryReaderOptions { Root = _root }),
            NullLogger<RepositoryChangeWatcher>.Instance);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup only.
        }
    }

    private async Task<List<RepositoryChange>> CollectAsync(
        Action act, Func<List<RepositoryChange>, bool> until)
    {
        var seen = new List<RepositoryChange>();
        using var subscription = _watcher.Subscribe(change =>
        {
            lock (seen)
            {
                seen.Add(change);
            }
        });
        act();
        for (var waited = 0; waited < 5000; waited += 100)
        {
            await Task.Delay(100); // poll interval inside this wait helper, not an arm-window guess (backlog 107)
            lock (seen)
            {
                if (until(seen))
                {
                    break;
                }
            }
        }

        lock (seen)
        {
            return [.. seen];
        }
    }

    [Fact]
    public async Task Reports_an_external_write_with_a_relative_path()
    {
        var seen = await CollectAsync(
            () => File.WriteAllText(Path.Combine(_root, "src", "new.txt"), "hello"),
            s => s.Any(c => c.RelativePath == "src/new.txt"));

        seen.Should().Contain(c => c.RelativePath == "src/new.txt" && c.Kind == RepositoryChangeKind.ChangedOrCreated);
    }

    [Fact]
    public async Task Reports_a_delete()
    {
        var doomed = Path.Combine(_root, "src", "doomed.txt");
        // Observe the create through the watcher rather than a fixed drain delay (backlog 107), so the
        // delete below cannot be coalesced with, or mistaken for, the create event. The `until`
        // predicate already exists in this file; do not swap this back for a Task.Delay.
        await CollectAsync(
            () => File.WriteAllText(doomed, "x"),
            s => s.Any(c => c.RelativePath == "src/doomed.txt" && c.Kind == RepositoryChangeKind.ChangedOrCreated));

        var seen = await CollectAsync(
            () => File.Delete(doomed),
            s => s.Any(c => c.Kind == RepositoryChangeKind.Deleted));

        seen.Should().Contain(c => c.RelativePath == "src/doomed.txt" && c.Kind == RepositoryChangeKind.Deleted);
    }

    [Fact]
    public async Task Filters_git_internals()
    {
        var seen = await CollectAsync(
            () =>
            {
                File.WriteAllText(Path.Combine(_root, ".git", "index.lock"), "x");
                File.WriteAllText(Path.Combine(_root, "visible.txt"), "x");
            },
            s => s.Any(c => c.RelativePath == "visible.txt"));

        seen.Should().OnlyContain(c => !c.RelativePath.StartsWith(".git"));
    }

    // The debounce tests below run the watcher on a clock the test moves by hand, so no quiet window can
    // elapse while the runner is slow (backlog 204: a starved CI runner split a 20-write burst into five
    // events on the real clock, and the watcher was right to report them). File events are still real.
    // A test knows every event of a write has been enqueued by writing a fresh MARKER file after it: the
    // watcher's events arrive in order on one thread, so once the marker's timer exists, everything
    // written before it has re-armed (or created) its own timer already.

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task Debounces_a_rapid_write_burst_into_one_event()
    {
        using var clocked = new ClockedWatcher(_root);
        var target = Path.Combine(_root, "src", "burst.txt");

        for (var i = 0; i < 20; i++)
        {
            File.WriteAllText(target, $"content {i}");
        }

        await clocked.MarkAsync();
        clocked.Clock.Advance(Quiet);

        clocked.Seen("src/burst.txt").Should().Be(1);
    }

    [Fact]
    public async Task A_write_inside_the_quiet_window_restarts_it()
    {
        using var clocked = new ClockedWatcher(_root);
        var target = Path.Combine(_root, "src", "burst.txt");

        File.WriteAllText(target, "first");
        await clocked.MarkAsync();
        clocked.Clock.Advance(TimeSpan.FromMilliseconds(200));
        clocked.Seen("src/burst.txt").Should().Be(0);

        File.WriteAllText(target, "second");
        await clocked.MarkAsync();
        clocked.Clock.Advance(TimeSpan.FromMilliseconds(200));
        clocked.Seen("src/burst.txt").Should().Be(0, "the second write restarted the window, 400 ms after the first");

        clocked.Clock.Advance(TimeSpan.FromMilliseconds(100));
        clocked.Seen("src/burst.txt").Should().Be(1);
    }

    [Fact]
    public async Task A_gap_longer_than_the_quiet_window_starts_a_new_event()
    {
        using var clocked = new ClockedWatcher(_root);
        var target = Path.Combine(_root, "src", "burst.txt");

        File.WriteAllText(target, "first");
        await clocked.MarkAsync();
        clocked.Clock.Advance(Quiet);
        clocked.Seen("src/burst.txt").Should().Be(1);

        File.WriteAllText(target, "second");
        await clocked.MarkAsync();
        clocked.Clock.Advance(Quiet);
        clocked.Seen("src/burst.txt").Should().Be(2);
    }

    /// <summary>A watcher over the same root on a <see cref="HandMovedClock"/>, recording what it dispatches.</summary>
    private sealed class ClockedWatcher : IDisposable
    {
        private readonly string _root;
        private readonly RepositoryChangeWatcher _watcher;
        private readonly IDisposable _subscription;
        private readonly List<RepositoryChange> _seen = [];
        private int _markers;

        public ClockedWatcher(string root)
        {
            _root = root;
            _watcher = new RepositoryChangeWatcher(
                new FixedOptionsMonitor(new RepositoryReaderOptions { Root = root }),
                NullLogger<RepositoryChangeWatcher>.Instance,
                Clock);
            _subscription = _watcher.Subscribe(change =>
            {
                lock (_seen)
                {
                    _seen.Add(change);
                }
            });
        }

        public HandMovedClock Clock { get; } = new();

        public int Seen(string relativePath)
        {
            lock (_seen)
            {
                return _seen.Count(c => c.RelativePath == relativePath);
            }
        }

        /// <summary>Writes a new marker file and waits until the watcher has armed a timer for it.</summary>
        public async Task MarkAsync()
        {
            var marker = $"marker-{++_markers}.txt";
            File.WriteAllText(Path.Combine(_root, marker), "m");
            for (var waited = 0; waited < 10_000 && !Clock.HasCreatedTimerFor(marker); waited += 20)
            {
                await Task.Delay(20); // poll interval inside this wait helper, not an arm-window guess (backlog 107)
            }

            Clock.HasCreatedTimerFor(marker).Should().BeTrue("the watcher should have seen {0}", marker);
        }

        public void Dispose()
        {
            _subscription.Dispose();
            _watcher.Dispose();
        }
    }

    /// <summary>A clock whose timers fire only when the test calls <see cref="Advance"/>.</summary>
    private sealed class HandMovedClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<HandTimer> _timers = [];
        private readonly HashSet<object> _createdFor = [];
        private DateTimeOffset _now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

        /// <summary>Whether a timer was ever created with this state (the watcher passes the relative path).</summary>
        public bool HasCreatedTimerFor(object state)
        {
            lock (_gate)
            {
                return _createdFor.Contains(state);
            }
        }

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
                if (state is not null)
                {
                    _createdFor.Add(state);
                }
            }

            return timer;
        }

        /// <summary>Moves time on and runs, on this thread, every timer that came due on the way.</summary>
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
