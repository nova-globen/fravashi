using System.Text;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.AgentSync;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Tests.Bridge;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentSync;

/// <summary>
/// Plan 2d Task 3: the sync session against a REAL hosted bridge and real temp folders. Host to
/// copy (initial pull + live events), copy to host (write-back with the manifest precondition),
/// and the HOST WINS conflict rule.
/// </summary>
public sealed class WorkspaceSyncSessionTests : IAsyncLifetime
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "fravashi-agent-sync-tests", Guid.NewGuid().ToString("N"));
    private string _host = null!;
    private string _copyRoot = null!;
    private HostedBridge _bridge = null!;

    public async Task InitializeAsync()
    {
        _host = Path.Combine(_base, "host");
        _copyRoot = Path.Combine(_base, "copies");
        Directory.CreateDirectory(Path.Combine(_host, "src"));
        Directory.CreateDirectory(Path.Combine(_host, ".git"));
        Directory.CreateDirectory(Path.Combine(_host, "node_modules", "left-pad"));
        Directory.CreateDirectory(_copyRoot);
        File.WriteAllText(Path.Combine(_host, "src", "Program.cs"), "class P { }\n");
        File.WriteAllText(Path.Combine(_host, "README.md"), "hello\n");
        File.WriteAllText(Path.Combine(_host, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(_host, "node_modules", "left-pad", "index.js"), "x\n");
        File.WriteAllText(Path.Combine(_host, ".gitignore"), "node_modules/\n");
        _bridge = await HostedBridge.StartAsync(_host);
    }

    private WorkspaceSyncSession Session(
        Action<string>? notify = null, BridgeClient? client = null) => new(
        client ?? _bridge.CreateClient(),
        _bridge.WorkspaceId,
        _copyRoot,
        WorkspaceSyncIgnore.FromGitignoreText("node_modules/\n"),
        notify);

    private string CopyPath(string relative) => Path.Combine(
        _copyRoot, _bridge.WorkspaceId, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Waits for a condition, and on failure says what the world actually looked like.
    /// <para>
    /// The <paramref name="describe"/> callback exists because of a CI failure nobody could act on
    /// (backlog 123): "the condition should hold within the timeout" is true of a test that timed out
    /// having diverged, of one that timed out still converging, and of one whose final read hit a
    /// transient lock, and those want three different fixes. These tests wait on real filesystem
    /// propagation and only ever fail on a machine nobody is sitting at, so the failure message is the
    /// whole evidence there will be. It is evaluated ONLY on the failing path, so it costs nothing.
    /// </para>
    /// </summary>
    private static async Task WaitUntilAsync(
        Func<bool> condition, int timeoutMs = 15000, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!Holds(condition) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50); // poll interval inside this wait helper, not an arm-window guess (backlog 107)
        }

        var held = Holds(condition);
        if (!held && describe is not null)
        {
            // Re-read once more before describing: if THIS says the condition holds, the failure was a
            // transient read and not a sync fault, and that is worth knowing rather than guessing at.
            var second = Holds(condition);
            held.Should().BeTrue(
                "the condition should hold within {0}ms. What was actually there: {1}. Re-evaluated right "
                + "after the failure it was {2}.",
                timeoutMs,
                Describe(describe),
                second ? "TRUE, so the timeout was a transient read rather than a sync failure" : "still false");
        }

        held.Should().BeTrue("the condition should hold within the timeout");
    }

    /// <summary>The describe callback's own failure must not replace the real one.</summary>
    private static string Describe(Func<string> describe)
    {
        try
        {
            return describe();
        }
        catch (Exception ex)
        {
            return "(could not be described: " + ex.Message + ")";
        }
    }

    /// <summary>
    /// The condition here reads a file the sync engine is writing, so a poll can land mid-write and the read
    /// throws instead of returning false. That is "not yet", not a failure: an uncaught IO exception failed
    /// <c>CopyEdit_WritesBackToTheHost</c> once in a loaded full-solution run while it passed alone. The
    /// timeout still fails the test when the condition genuinely never holds.
    /// </summary>
    private static bool Holds(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Prove the host-to-copy LIVE watch is actually connected before a one-shot host stimulus, and do
    /// NOT replace this with a fixed delay (backlog 107). The bridge arms its FileSystemWatcher
    /// synchronously inside <c>Subscribe</c>, before the SSE response headers flush
    /// (<c>BridgeApplication</c> maps <c>/events</c> so), but the STUDIO side issues that GET off the
    /// awaited <see cref="WorkspaceSyncSession.StartAsync"/> (a fire-and-forget <c>Task.Run</c>), so
    /// there is a real, unobservable window after StartAsync returns during which a host write is
    /// generated by nobody and never recovered (the engine has no rescan; a missed live event only
    /// self-heals on the next change or a restart). A fixed <c>Task.Delay</c> only guessed at that
    /// window's width. Instead re-touch a throwaway sentinel host-side until it reaches the copy: the
    /// moment it does, the whole pipeline (studio GET connected + bridge armed + SSE delivery + apply)
    /// is provably live, so the real stimulus that follows cannot fall into the arm gap. Deterministic:
    /// it completes as soon as the pipeline is live and never before.
    /// </summary>
    private async Task WaitForLiveHostWatchAsync()
    {
        var sentinelHost = Path.Combine(_host, "src", "__watcharm__.probe");
        await WaitUntilAsync(() =>
        {
            File.WriteAllText(sentinelHost, Guid.NewGuid().ToString("N"));
            return File.Exists(CopyPath("src/__watcharm__.probe"));
        });
    }

    [Fact]
    public async Task InitialPull_CopiesTheTree_HonorsIgnore_AndStampsTheManifest()
    {
        await using var session = Session();
        await session.StartAsync(CancellationToken.None);

        File.Exists(CopyPath("src/Program.cs")).Should().BeTrue();
        File.Exists(CopyPath("README.md")).Should().BeTrue();
        File.Exists(CopyPath(".gitignore")).Should().BeTrue();
        Directory.Exists(CopyPath(".git")).Should().BeFalse();
        Directory.Exists(CopyPath("node_modules")).Should().BeFalse();
        session.Status.InitialSyncComplete.Should().BeTrue();
        session.Status.SyncedFiles.Should().BeGreaterThanOrEqualTo(3);

        // A second session over the same copy re-pulls nothing (stamps match).
        await using var second = Session();
        await second.StartAsync(CancellationToken.None);
        second.Status.SyncedFiles.Should().Be(0);
    }

    [Fact]
    public async Task HostChange_ArrivesInTheCopy_ThroughTheLiveWatch()
    {
        await using var session = Session();
        await session.StartAsync(CancellationToken.None);

        // Prove the live watch is connected before the one-shot host write (backlog 107); a fixed
        // Task.Delay only guessed the SSE-connect + inotify-arm window and flaked when a loaded Linux
        // box exceeded it. See WaitForLiveHostWatchAsync: do not swap this back for a delay.
        await WaitForLiveHostWatchAsync();
        File.WriteAllText(Path.Combine(_host, "src", "New.cs"), "new content\n");

        await WaitUntilAsync(() => File.Exists(CopyPath("src/New.cs")));
        (await File.ReadAllTextAsync(CopyPath("src/New.cs"))).Should().Be("new content\n");
    }

    [Fact]
    public async Task CopyEdit_WritesBackToTheHost()
    {
        await using var session = Session();
        await session.StartAsync(CancellationToken.None);

        // No arm wait needed here (backlog 107): the COPY watcher is armed synchronously by
        // `_watcher.EnableRaisingEvents = true` inside StartAsync, which the line above already awaited,
        // and README.md lives in the copy root that existed at arm time. The old Task.Delay(300) was
        // pure superstition. The write-back arrival is the real awaited condition below (15s cap covers
        // the 300ms debounce even under Linux pool starvation); do not shorten it.
        await File.WriteAllTextAsync(CopyPath("README.md"), "edited by the agent\n");

        await WaitUntilAsync(() => File.ReadAllText(Path.Combine(_host, "README.md")) == "edited by the agent\n");
        session.Status.HostWinsConflicts.Should().Be(0);
    }

    // ---- backlog 108: what may leave the container ------------------------------------------
    // The value below is made up; nothing in this file is, or resembles, a real credential.

    private const string HeldValue = "not-a-real-key-0123456789abcdef";

    private WorkspaceSyncSession GuardedSession(
        Action<string> notify, Func<IReadOnlyCollection<AgentHeldCredential>>? held = null) => new(
        _bridge.CreateClient(),
        _bridge.WorkspaceId,
        _copyRoot,
        WorkspaceSyncIgnore.FromGitignoreText("node_modules/\n"),
        notify,
        held ?? (() => [new AgentHeldCredential("PROVIDER_API_KEY", HeldValue)]));

    [Fact]
    public async Task AFileCarryingAHeldCredential_NeverReachesTheHost_AndTheUserIsToldWhichFileAndWhichVariable()
    {
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var session = GuardedSession(notes.Enqueue);
        await session.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(CopyPath("notes.txt"), $"the key is {HeldValue}\n");
        await WaitUntilAsync(() => notes.Any(n => n.Contains("notes.txt")));

        // A clean file written AFTERWARDS and seen on the host proves the loop ran past the withheld one,
        // so its absence below is a decision and not a write-back that simply had not happened yet.
        await File.WriteAllTextAsync(CopyPath("after.txt"), "ordinary\n");
        await WaitUntilAsync(() => File.Exists(Path.Combine(_host, "after.txt")));

        File.Exists(Path.Combine(_host, "notes.txt")).Should().BeFalse();
        File.Exists(CopyPath("notes.txt")).Should().BeTrue("the studio does not delete or rewrite what an agent wrote");
        var note = notes.Single(n => n.Contains("notes.txt"));
        note.Should().Contain("PROVIDER_API_KEY").And.NotContain(HeldValue);
    }

    // ---- backlog 150: a commit waits for the copy to settle -----------------------------------

    [Fact]
    public async Task Settle_ReturnsOnlyOnceTheAgentsEditHasReachedTheHost()
    {
        await using var session = Session();
        await session.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(CopyPath("README.md"), "edited by the agent\n");

        // Settled means the host already holds the edit: that is the whole point of waiting before a commit,
        // and it is asserted the moment the wait returns, not after a further grace period.
        (await session.SettleAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(_host, "README.md"))).Should().Be("edited by the agent\n");
        session.Status.PendingWriteBacks.Should().Be(0);
    }

    [Fact]
    public async Task Settle_NeverAnswersBeforeTheWatcherHasHadTimeToSeeAWriteMadeJustBeforeTheCall()
    {
        await using var session = Session();
        await session.StartAsync(CancellationToken.None);

        // Counted from the last event alone, a session that had seen no event answered at once, including when the
        // agent's write was on disk but its watcher event not raised yet: the commit would then have gone without it.
        // Two debounce intervals (600 ms) from the call is the floor now, whatever happened before it.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (await session.SettleAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(550));
    }

    [Fact]
    public async Task Settle_SaysSoWhenTheWaitRunsOut()
    {
        await using var session = Session();
        await session.StartAsync(CancellationToken.None);

        // An edit just made cannot have settled within a wait shorter than the debounce: the answer is an
        // honest false, never a true that a commit would then act on.
        await File.WriteAllTextAsync(CopyPath("README.md"), "still being written\n");
        (await session.SettleAsync(TimeSpan.FromMilliseconds(20))).Should().BeFalse();

        // And it does settle afterwards.
        (await session.SettleAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
    }

    [Fact]
    public async Task AnExistingHostFile_IsLeftAsItWas_WhenTheCopyOfItStartsCarryingACredential()
    {
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var session = GuardedSession(notes.Enqueue);
        await session.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(CopyPath("README.md"), $"hello\n{HeldValue}\n");
        await WaitUntilAsync(() => notes.Any(n => n.Contains("README.md")));

        (await File.ReadAllTextAsync(Path.Combine(_host, "README.md"))).Should().Be("hello\n");
    }

    [Fact]
    public async Task OnceTheValueIsRemoved_TheFileSyncsLikeAnyOther()
    {
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var session = GuardedSession(notes.Enqueue);
        await session.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(CopyPath("notes.txt"), $"{HeldValue}\n");
        await WaitUntilAsync(() => notes.Any(n => n.Contains("notes.txt")));

        await File.WriteAllTextAsync(CopyPath("notes.txt"), "the key is in the environment\n");

        await WaitUntilAsync(() =>
            File.Exists(Path.Combine(_host, "notes.txt"))
            && File.ReadAllText(Path.Combine(_host, "notes.txt")) == "the key is in the environment\n");
    }

    [Fact]
    public async Task WhenTheCredentialsCannotBeRead_AChangeIsHeldBackAndArrivesOnceTheyCan()
    {
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var locked = true;
        await using var session = GuardedSession(
            notes.Enqueue,
            () => locked
                ? throw new InvalidOperationException("the secret store is locked")
                : [new AgentHeldCredential("PROVIDER_API_KEY", HeldValue)]);
        await session.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(CopyPath("README.md"), "edited by the agent\n");
        await WaitUntilAsync(() => notes.Any(n => n.Contains("could not read the credentials")));
        (await File.ReadAllTextAsync(Path.Combine(_host, "README.md"))).Should().Be(
            "hello\n", "could not check is not the same as clean");

        locked = false;

        await WaitUntilAsync(() => File.ReadAllText(Path.Combine(_host, "README.md")) == "edited by the agent\n");
        notes.Count(n => n.Contains("could not read the credentials")).Should().Be(1, "it is said once, not on every retry");
    }

    // ---- backlog 147: the container stopped between an agent's edit and its write-back ---------------------
    //
    // Each test runs a session, stops it, changes the copy while NOTHING is watching (which is exactly what an edit
    // the watcher never got to write back looks like after a crash), and starts a new session over the same copy.

    private async Task StoppedAsync()
    {
        await using var first = Session();
        await first.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnEditTheContainerStoppedBeforeWritingBack_ReachesTheHostOnTheNextStart()
    {
        await StoppedAsync();
        await File.WriteAllTextAsync(CopyPath("README.md"), "edited by an agent just before the crash\n");
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();

        await using var next = Session(notes.Enqueue);
        await next.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => File.ReadAllText(Path.Combine(_host, "README.md")) == "edited by an agent just before the crash\n");
        notes.Should().Contain(n => n.Contains("had not reached the host folder when the studio stopped"));
    }

    [Fact]
    public async Task AFileCreatedAndOneDeletedBeforeTheStop_AreCarriedToTheHostOnTheNextStart()
    {
        await StoppedAsync();
        await File.WriteAllTextAsync(CopyPath("src/NewFromTheAgent.cs"), "class N { }\n");
        File.Delete(CopyPath("src/Program.cs"));

        await using var next = Session();
        await next.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => File.Exists(Path.Combine(_host, "src", "NewFromTheAgent.cs"))
                                   && !File.Exists(Path.Combine(_host, "src", "Program.cs")));
        File.Exists(Path.Combine(_host, "README.md")).Should().BeTrue("only the file the agent deleted goes");
    }

    [Fact]
    public async Task AnEditMadeOnBothSidesWhileStopped_LetsTheHostWin_AndSaysSo()
    {
        await StoppedAsync();
        await File.WriteAllTextAsync(CopyPath("README.md"), "the agent's version\n");
        await Task.Delay(20); // a distinct host timestamp from the first sync, not a propagation wait
        await File.WriteAllTextAsync(Path.Combine(_host, "README.md"), "the person's version\n");
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();

        await using var next = Session(notes.Enqueue);
        await next.StartAsync(CancellationToken.None);

        File.ReadAllText(CopyPath("README.md")).Should().Be("the person's version\n");
        File.ReadAllText(Path.Combine(_host, "README.md")).Should().Be("the person's version\n");
        notes.Should().Contain(n => n.Contains("conflict on 'README.md'") && n.Contains("host version won"));
        next.Status.HostWinsConflicts.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// What a manifest written before backlog 147 looks like: a stamp per path and nothing about the copy. Two thirds of
    /// the entries in this repository's own copy were still like that on 2026-09-27.
    /// </summary>
    private void ForgetWhatTheCopyHeld()
    {
        var manifest = Path.Combine(_copyRoot, _bridge.WorkspaceId + ".manifest.json");
        var entries = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
        var bare = new System.Text.Json.Nodes.JsonObject();
        foreach (var (path, entry) in entries)
        {
            bare[path] = entry!["hostStamp"]!.GetValue<string>();
        }

        File.WriteAllText(manifest, bare.ToJsonString());
    }

    [Theory]
    [InlineData("edited by an agent before content was recorded\n")]
    [InlineData("hellO\n")] // the same size as the host's "hello", so only the bytes can tell
    public async Task AnEditToAFileSyncedBeforeContentWasRecorded_StillReachesTheHost(string edit)
    {
        await StoppedAsync();
        ForgetWhatTheCopyHeld();
        await File.WriteAllTextAsync(CopyPath("README.md"), edit);

        await using var next = Session();
        await next.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => File.ReadAllText(Path.Combine(_host, "README.md")) == edit);
    }

    [Fact]
    public async Task AnUntouchedFileSyncedBeforeContentWasRecorded_GainsItsRecord_AndIsLeftAlone()
    {
        await StoppedAsync();
        ForgetWhatTheCopyHeld();
        var hostFile = Path.Combine(_host, "README.md");
        var before = File.GetLastWriteTimeUtc(hostFile);

        await using (var next = Session())
        {
            await next.StartAsync(CancellationToken.None);
            await next.SettleAsync(TimeSpan.FromSeconds(15));
        }

        File.GetLastWriteTimeUtc(hostFile).Should().Be(before, "nothing about it changed, so nothing is written back");
        var manifest = File.ReadAllText(Path.Combine(_copyRoot, _bridge.WorkspaceId + ".manifest.json"));
        System.Text.Json.Nodes.JsonNode.Parse(manifest)!["README.md"]!["hash"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ACopyThatIsGoneWhileItsManifestSurvived_IsPulledAgain_AndNothingIsDeletedOnTheHost()
    {
        await StoppedAsync();
        Directory.Delete(Path.Combine(_copyRoot, _bridge.WorkspaceId), recursive: true);

        await using var next = Session();
        await next.StartAsync(CancellationToken.None);
        await Task.Delay(1000); // long enough for a wrongly queued delete to have run; nothing is waited FOR here

        File.Exists(Path.Combine(_host, "README.md")).Should().BeTrue();
        File.Exists(Path.Combine(_host, "src", "Program.cs")).Should().BeTrue();
        File.Exists(CopyPath("README.md")).Should().BeTrue();
    }

    [Fact]
    public async Task AgentCreatedFile_LandsOnTheHost()
    {
        await using var session = Session();
        await session.StartAsync(CancellationToken.None);

        // No arm wait needed here (backlog 107): the COPY watcher is armed synchronously by
        // `_watcher.EnableRaisingEvents = true` inside the already-awaited StartAsync, and a new
        // subtree written before its own inotify watch arms is still recovered by EnqueueDescendants.
        // The old Task.Delay(300) only guessed at an arm window; the WaitUntilAsync below awaits the
        // real host arrival. Do not swap it back for a delay.
        Directory.CreateDirectory(CopyPath("src/generated"));
        await File.WriteAllTextAsync(CopyPath("src/generated/output.txt"), "made by an agent\n");

        await WaitUntilAsync(() => File.Exists(Path.Combine(_host, "src", "generated", "output.txt")));
    }

    [Fact]
    public async Task StaleCopyEdit_LosesToTheHost_AndIsCounted()
    {
        // Backlog 123: this used to RACE a host write against a copy write and accept whichever
        // interleaving the machine took, with its only real assertions guarded on
        // `HostWinsConflicts > 0`. On the dev host the live watch always refreshed the manifest stamp
        // inside the 300ms debounce, the count stayed zero, and the test passed having asserted nothing
        // about losing to the host or being counted. So the interleaving is SELECTED now, not raced:
        // the host's live events are held at a gate, which is the only thing that can refresh the
        // stamp, so the write-back below MUST go out carrying a stale one.
        // Written by the engine's write-back thread and read here, so not a List.
        var conflicts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var client = new HeldHostEventsClient(_bridge);
        await using var session = Session(conflicts.Enqueue, client);
        await session.StartAsync(CancellationToken.None);

        // No WaitForLiveHostWatchAsync here, on purpose: that helper waits for a host event to reach
        // the copy, and this test exists to withhold exactly that. The copy watcher is armed
        // synchronously inside the awaited StartAsync (backlog 107), which is all this path needs.
        await File.WriteAllTextAsync(Path.Combine(_host, "README.md"), "host version 2\n");

        // The bridge's freshness precondition tolerates 2 seconds of timestamp drift on purpose
        // (BridgeFileService.WriteAsync, for coarse-mtime filesystems), and this host write lands
        // well inside 2 seconds of the stamp the initial pull recorded. Measured: with the events
        // held and nothing else changed, the stale write-back was ACCEPTED and the host took the
        // agent's text with no conflict. So say the host moved by more than the tolerance, as a
        // fact on the file rather than as a sleep.
        File.SetLastWriteTimeUtc(Path.Combine(_host, "README.md"), DateTime.UtcNow.AddSeconds(30));
        await File.WriteAllTextAsync(CopyPath("README.md"), "agent version 2\n");

        // Wait for the NOTIFICATION, not only for the count and the rewritten copy. On a refusal the engine
        // counts first, then rewrites the copy, saves the manifest and only then says so, and this test
        // first waited for the two early steps and asserted the last one at once. CI run 126 looked in that
        // gap ("Expected conflicts to contain a single item, but the collection is empty") on a commit where
        // run 125 had passed. Reproduced here on demand by delaying the engine's notify by 1.5 seconds. The
        // notification is the last thing that path does, so it IS the completion signal, and waiting on it
        // replaces a guess about how long the rest takes (the item-107 discipline).
        await WaitUntilAsync(
            () => !conflicts.IsEmpty
                && session.Status.HostWinsConflicts > 0
                && File.ReadAllText(CopyPath("README.md")) == "host version 2\n",
            describe: () => DescribeBothSides(session) + $", notifications={conflicts.Count}");

        // Unconditional now. The host kept its own text, the copy was rewritten from it, the refusal
        // was counted exactly once and it was SAID (never a silent merge).
        (await File.ReadAllTextAsync(Path.Combine(_host, "README.md"))).Should().Be("host version 2\n");
        session.Status.HostWinsConflicts.Should().Be(1);
        conflicts.Should().ContainSingle().Which.Should().Contain("README.md");
    }

    [Fact]
    public async Task CopyEdit_AfterTheHostChangeHasLanded_WritesBackWithoutAConflict()
    {
        // The OTHER interleaving of the old raced test (backlog 123), selected by waiting for it: the
        // host change reaches the copy first, so the stamp is fresh when the agent's edit goes back.
        var conflicts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var session = Session(conflicts.Enqueue);
        await session.StartAsync(CancellationToken.None);
        await WaitForLiveHostWatchAsync();

        await File.WriteAllTextAsync(Path.Combine(_host, "README.md"), "host version 2\n");
        await WaitUntilAsync(
            () => File.ReadAllText(CopyPath("README.md")) == "host version 2\n",
            describe: () => DescribeBothSides(session));

        await File.WriteAllTextAsync(CopyPath("README.md"), "agent version 3\n");

        await WaitUntilAsync(
            () => File.ReadAllText(Path.Combine(_host, "README.md")) == "agent version 3\n",
            describe: () => DescribeBothSides(session));
        session.Status.HostWinsConflicts.Should().Be(0);
        conflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task HostChange_EchoingInTheCopy_IsNotWrittenBack()
    {
        // The other half of the echo rule (backlog 123): the copy-watcher event raised by our OWN
        // host-to-copy apply must not go back to the host. A write-back replaces the host file, so a
        // leaked echo shows as the host's timestamp moving off the recognisable one set here.
        await using var session = Session();
        await session.StartAsync(CancellationToken.None);
        await WaitForLiveHostWatchAsync();

        var hostFile = Path.Combine(_host, "README.md");
        var recognisable = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        await File.WriteAllTextAsync(hostFile, "host version 2\n");
        File.SetLastWriteTimeUtc(hostFile, recognisable);
        await WaitUntilAsync(() => File.ReadAllText(CopyPath("README.md")) == "host version 2\n");

        // "It did not happen" needs a moment after which it would have. The write-back loop is
        // sequential and the echo was enqueued before either marker, so once the SECOND marker is on
        // the host, the pass that handled the echo has fully finished. Not a delay.
        foreach (var marker in new[] { "marker-1.txt", "marker-2.txt" })
        {
            await File.WriteAllTextAsync(CopyPath(marker), "x\n");
            await WaitUntilAsync(() => File.Exists(Path.Combine(_host, marker)));
        }

        File.GetLastWriteTimeUtc(hostFile).Should().Be(recognisable);
        session.Status.HostWinsConflicts.Should().Be(0);
    }

    /// <summary>What a timed-out wait should say (backlog 123): both sides' text and the conflict count.</summary>
    private string DescribeBothSides(WorkspaceSyncSession session)
    {
        var host = File.ReadAllText(Path.Combine(_host, "README.md")).Trim();
        var copy = File.ReadAllText(CopyPath("README.md")).Trim();
        return $"host held '{host}', copy held '{copy}', HostWinsConflicts={session.Status.HostWinsConflicts}";
    }

    /// <summary>
    /// A bridge client whose host-to-copy live events never reach the session. Everything else is the
    /// real client against the real hosted bridge, so the write-back, its precondition and the refusal
    /// are all genuine. Only the ONE thing that can refresh a manifest stamp behind the agent's back is
    /// withheld, which turns "the stamp happened to be stale" from a race into a fact.
    /// </summary>
    private sealed class HeldHostEventsClient(HostedBridge bridge)
        : BridgeClient(new HttpClient(), bridge.CreateContext())
    {
        public override Task SubscribeEventsAsync(
            Func<BridgeFileEventDto, Task> onEvent, CancellationToken ct) =>
            base.SubscribeEventsAsync(_ => Task.Delay(Timeout.Infinite, ct), ct);
    }

    [Fact]
    public async Task HostDeletion_RemovesTheCopyFile()
    {
        await using var session = Session();
        await session.StartAsync(CancellationToken.None);

        // A delete cannot be re-issued, so prove the live watch is connected FIRST (backlog 107): once
        // a sentinel round-trips into the copy the pipeline is live, so the single File.Delete below
        // cannot land in the arm gap. Not a fixed delay; see WaitForLiveHostWatchAsync.
        await WaitForLiveHostWatchAsync();

        File.Delete(Path.Combine(_host, "README.md"));

        await WaitUntilAsync(() => !File.Exists(CopyPath("README.md")));
    }

    [Fact]
    public async Task IgnoredCopyContent_NeverReachesTheHost()
    {
        await using var session = Session();
        await session.StartAsync(CancellationToken.None);

        // No arm wait needed here (backlog 107): the COPY watcher is armed synchronously inside the
        // already-awaited StartAsync, and visible.txt lives in the copy root that existed at arm time.
        // The old Task.Delay(300) only guessed at an arm window; the WaitUntilAsync below awaits the
        // real host arrival of visible.txt. Do not swap it back for a delay.
        Directory.CreateDirectory(CopyPath("node_modules/agent-dep"));
        await File.WriteAllTextAsync(CopyPath("node_modules/agent-dep/index.js"), "agent-only\n");
        await File.WriteAllTextAsync(CopyPath("visible.txt"), "should sync\n");

        await WaitUntilAsync(() => File.Exists(Path.Combine(_host, "visible.txt")));
        File.Exists(Path.Combine(_host, "node_modules", "agent-dep", "index.js")).Should().BeFalse();
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
