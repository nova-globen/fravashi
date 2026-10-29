using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.AgentSync;

/// <summary>A point-in-time snapshot of one workspace copy's sync state (plan 2d).</summary>
public sealed record WorkspaceSyncStatus(
    string WorkspaceId,
    string CopyPath,
    bool InitialSyncComplete,
    int SyncedFiles,
    int PendingWriteBacks,
    int HostWinsConflicts,
    DateTimeOffset? LastActivityUtc);

/// <summary>
/// One workspace's contained-agent copy under <c>{copyRoot}/{workspaceId}</c>, kept in sync with
/// the HOST folder through the bridge (plan 2d, decision 2). Host to copy: an initial manifest-
/// stamped pull plus the bridge's SSE watch, and a rescan of the host whenever that watch may have missed something
/// (backlog 253). Copy to host: debounced watcher events pushed
/// through the bridge write API with the manifest stamp as the freshness precondition; a refusal
/// means the HOST WINS (the copy is rewritten from the host and the event is surfaced, never a
/// silent merge; ADR 0016's honesty rule). <c>.git</c> and gitignored content never sync in
/// either direction (<see cref="WorkspaceSyncIgnore"/>).
/// </summary>
public sealed class WorkspaceSyncSession : IAsyncDisposable
{
    // How long a write-back waits before asking the host's git again when it could not answer (backlog 161).
    private static readonly TimeSpan UnansweredRetry = TimeSpan.FromSeconds(5);

    private readonly BridgeClient _bridge;
    private readonly string _workspaceId;
    private readonly WorkspaceSyncIgnore _ignore;
    private readonly Action<string>? _notify;
    private readonly WorkspaceSyncManifest _manifest;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingWriteBacks = new(StringComparer.Ordinal);
    // The hash of the bytes this session last wrote INTO the copy, per path: how a copy-watcher event
    // is recognised as the echo of our own host-to-copy apply. It used to be a three second window
    // per path, which also swallowed a GENUINE agent edit made inside it, and with no rescan that
    // edit never reached the host and nothing said so (backlog 123). An echo is a fact about content.
    private readonly ConcurrentDictionary<string, byte[]> _appliedContent = new(StringComparer.Ordinal);

    // Backlog 247: a delete EVENT is never acted on by the event alone, in either direction. The bridge writes the host
    // atomically (a temp file renamed over the target), and on Windows that rename is reported as "deleted <path>" then
    // "renamed <temp> -> <path>", so every write-back used to come back as a host delete of the file just written: the
    // copy file was deleted and recreated after every save, and when that delete was applied between the bridge write
    // and the write-back recording its stamp, the copy stayed deleted with a manifest entry and the engine's own delete
    // went back to the host as the agent's. So a host delete is HELD here for the debounce interval and the host is
    // asked again before anything happens, and host events and write-backs never interleave (_apply).
    private readonly ConcurrentDictionary<string, DateTimeOffset> _heldHostDeletes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _apply = new(1, 1);

    // The host stamp each path's last write-back produced, so a held host delete of our own write says so quietly.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _wroteBack = new(StringComparer.Ordinal);

    // Copy-side paths a delete (or rename away) was seen for since their last write-back, so a write-back that finds
    // the file there again can say it treated the delete as a save.
    private readonly ConcurrentDictionary<string, byte> _copyDeleteSeen = new(StringComparer.Ordinal);

    // The bridge's own temp file (BridgeFileService.WriteAsync): never content, so never synced in either direction.
    private const string BridgeTempSuffix = ".fravashi-tmp";
    private FileSystemWatcher? _watcher;
    private Task? _hostEventLoop;
    private Task? _writeBackLoop;
    private int _syncedFiles;
    private int _recordedAtStart;
    private int _conflicts;
    private volatile bool _initialComplete;
    private DateTimeOffset? _lastActivityUtc;

    // Backlog 150: what SettleAsync waits on. A write-back that has left the queue but not yet reached the
    // host is IN FLIGHT and counted here (the queue alone drops a path before its write finishes), and the
    // last copy-side event is what says the agent has stopped writing for long enough for the watcher to
    // have raised everything it will.
    private int _inFlight;
    private long _lastLocalEventTicks;

    // Backlog 108: the credentials the studio hands its agents, asked for at each write-back. A file that
    // carries one's value is never written to the HOST folder, whatever wrote it and however it got there:
    // this is a check on CONTENT at the one door out of the container, so it does not depend on the studio
    // having understood a command. The hash is what was last withheld per path, so one file says so once.
    private readonly Func<IReadOnlyCollection<AgentHeldCredential>>? _heldCredentials;
    private readonly ConcurrentDictionary<string, byte[]> _withheld = new(StringComparer.Ordinal);
    private int _uncheckable;

    // Backlog 161: new copy-side paths the host's git ignores, logged once each. They stay in the copy.
    private readonly ConcurrentDictionary<string, byte> _keptInCopy = new(StringComparer.Ordinal);
    private readonly ILogger _log;

    // Backlog 253: a host file reached the copy ONLY through a live event, and nothing looked at the host again after
    // the start pull, so an event lost for any reason (the stream reconnecting, the gap before it first connected, a
    // handler that failed, an ignore question nobody could answer, the bridge's watcher overflowing) lost a created file
    // for the rest of the session. A rescan compares the host's tree with the manifest and fetches what differs. It is
    // asked for by each of those, runs on the write-back loop one at a time, never sooner than MinimumRescanSpacing
    // after the last one ended, and also every RescanInterval as the backstop for a loss nobody reported.
    private int _rescanRequested;
    private string _rescanReason = string.Empty;
    private DateTimeOffset? _lastRescanEnded;
    private DateTimeOffset _lastFullLook;
    private int _rescanFailures;

    // Backlog 253: each path withheld from the copy, with its reason, is logged once at Debug. Capped, so a build's
    // output on the host cannot grow it without bound.
    private const int WithheldLogCap = 20_000;
    private readonly ConcurrentDictionary<string, byte> _withheldSaid = new(StringComparer.Ordinal);

    public WorkspaceSyncSession(
        BridgeClient bridge, string workspaceId, string copyRoot,
        WorkspaceSyncIgnore ignore, Action<string>? notify = null,
        Func<IReadOnlyCollection<AgentHeldCredential>>? heldCredentials = null,
        ILogger? logger = null)
    {
        _log = logger ?? NullLogger.Instance;
        _heldCredentials = heldCredentials;
        _bridge = bridge;
        _workspaceId = workspaceId;
        _ignore = ignore;
        _notify = notify;
        CopyPath = Path.Combine(copyRoot, workspaceId);
        _manifest = new WorkspaceSyncManifest(Path.Combine(copyRoot, workspaceId + ".manifest.json"));
    }

    public string CopyPath { get; }

    /// <summary>
    /// How long a copy-side path must go without an event before it is written back, which is also how long a host
    /// delete is held before the host is asked again (backlog 247). Settable only so a test can make a stall of its own
    /// thread have to last seconds before it changes what is sent (backlog 263); nothing in the product sets it.
    /// </summary>
    public TimeSpan DebounceInterval { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The least time between the end of one rescan of the host and the start of the next, whatever asks for it
    /// (backlog 253): what bounds the cost of a bridge whose stream drops and returns over and over.
    /// </summary>
    public TimeSpan MinimumRescanSpacing { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the host is rescanned with nothing asking (backlog 253): the backstop for a lost event nobody reported.</summary>
    public TimeSpan RescanInterval { get; init; } = TimeSpan.FromMinutes(10);

    public WorkspaceSyncStatus Status => new(
        _workspaceId, CopyPath, _initialComplete, _syncedFiles,
        _pendingWriteBacks.Count, _conflicts, _lastActivityUtc);

    /// <summary>
    /// Waits until everything the agent wrote into the copy has reached the host (backlog 150): the initial
    /// pull is done, the write-back queue is empty, no write-back is in flight, and the copy has been quiet
    /// for two debounce intervals, counted from the call as well as from the last event, which is longer than
    /// the watcher takes to raise an event for a write that already happened. So it never answers in less than
    /// that. True when that holds, false when <paramref name="timeout"/> passes first.
    /// <para>
    /// What it deliberately does not promise: a file WITHHELD because it carries a credential (backlog 108)
    /// is not pending and never will be, so a settled copy can still differ from the host by exactly those
    /// files, and the engine has already told the user which.
    /// </para>
    /// </summary>
    public async Task<bool> SettleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var called = DateTimeOffset.UtcNow;
        var deadline = called + timeout;
        while (true)
        {
            // The quiet period counts from the later of the last copy-side event and this call. Counted from the
            // event alone, a write made just before the call whose watcher event had not been raised yet found the
            // last event at "never", and the wait said settled before the write had even been seen: measured once in
            // a loaded test run, where the host did not hold the edit the moment the wait returned.
            var lastEvent = new DateTimeOffset(Interlocked.Read(ref _lastLocalEventTicks), TimeSpan.Zero);
            if (lastEvent < called)
            {
                lastEvent = called;
            }

            if (_initialComplete
                && _pendingWriteBacks.IsEmpty
                && Volatile.Read(ref _inFlight) == 0
                && DateTimeOffset.UtcNow - lastEvent >= DebounceInterval * 2)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var copyExisted = Directory.Exists(CopyPath);
        Directory.CreateDirectory(CopyPath);

        var seenHostPaths = new HashSet<string>(StringComparer.Ordinal);
        var recovered = new List<string>();
        var lostCopy = CopyWasLost(copyExisted);
        await PullTreeAsync(string.Empty, seenHostPaths, recovered, lostCopy, linked.Token).ConfigureAwait(false);

        // Prune: files this session SYNCED before (manifest) that the host no longer has. Agent-
        // created files outside the manifest are pending write-backs and must never be pruned.
        var noLongerSynced = new List<string>();
        foreach (var stale in _manifest.Paths.Where(p => !seenHostPaths.Contains(p)))
        {
            if (!_ignore.ShouldSync(stale, isDirectory: false))
            {
                // Backlog 161: synced under the rules before this one, and the host's git ignores it. It leaves the
                // copy, where the agent CLI would otherwise go on loading it; the host's own file is not touched.
                // One line per file at Debug and a summary below: this repository's first start removed 6663 files
                // (mostly build output its [Dd]ebug/ rule had always meant), which as Information buried the log.
                noLongerSynced.Add(stale);
                _log.LogDebug(
                    "Removed '{Path}' from the agent copy of workspace {WorkspaceId}: the host's git ignores it.",
                    stale, _workspaceId);
            }
            else if (_manifest.CopyUnchanged(stale, LocalPath(stale)) == false && File.Exists(LocalPath(stale)))
            {
                // Deleted on the host while the agent's edit to it waited to be written back: the host wins, and
                // it is said rather than done quietly (backlog 147).
                Conflict(stale, "the host deleted it while an agent's edit to it had not been written back");
            }

            TryDeleteLocal(stale);
            _manifest.Remove(stale);
        }

        if (noLongerSynced.Count > 0)
        {
            _log.LogInformation(
                "Removed {Count} file(s) from the agent copy of workspace {WorkspaceId} because the host's git ignores "
                + "them, so they are no longer synced; the files in the host folder are untouched. Among them: {Examples}",
                noLongerSynced.Count, _workspaceId, string.Join(", ", noLongerSynced.Order(StringComparer.Ordinal).Take(5)));
        }

        // Backlog 147: what the agent wrote into the copy that never reached the host (the container stopped before
        // the debounced write-back ran): a file the manifest has never seen.
        if (!lostCopy)
        {
            foreach (var created in CopyFiles(CopyPath).Where(p => !seenHostPaths.Contains(p) && _manifest.Entry(p) is null))
            {
                recovered.Add(created);
            }
        }

        _manifest.Save();
        _initialComplete = true;
        _lastFullLook = DateTimeOffset.UtcNow;
        Touch();
        if (_recordedAtStart > 0)
        {
            _log.LogInformation(
                "Recorded the content of {Count} file(s) in the agent copy of workspace {WorkspaceId} that were synced "
                + "before content was recorded; an edit stranded in the copy is now recognised for every one of them.",
                _recordedAtStart, _workspaceId);
        }

        // Host to copy, live: the bridge's SSE watch (auto-reconnecting). Its first connection asks for a rescan, which
        // finds what the host changed between this pull and the watch arming (backlog 253).
        _hostEventLoop = Task.Run(() => _bridge.SubscribeEventsAsync(OnHostEventAsync, _cts.Token), CancellationToken.None);

        // Copy to host: watcher events, debounced per path.
        _watcher = new FileSystemWatcher(CopyPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => EnqueueLocal(e.FullPath);
        _watcher.Changed += (_, e) => EnqueueLocal(e.FullPath);
        _watcher.Deleted += (_, e) => EnqueueLocal(e.FullPath, deleted: true);
        _watcher.Renamed += (_, e) =>
        {
            EnqueueLocal(e.OldFullPath, deleted: true);
            EnqueueLocal(e.FullPath);
        };
        _watcher.EnableRaisingEvents = true;

        // The write-backs the previous process never ran go first, through the same path as any other, so the
        // freshness precondition, the echo check and the credential check all still apply.
        foreach (var path in recovered)
        {
            _pendingWriteBacks[path] = DateTimeOffset.UtcNow - DebounceInterval;
        }

        if (recovered.Count > 0)
        {
            _notify?.Invoke(
                $"{recovered.Count} agent change(s) had not reached the host folder when the studio stopped; they are being written back now.");
        }

        _writeBackLoop = Task.Run(() => WriteBackLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Whether the copy itself is gone while its manifest survived (a copy folder removed by hand, say): then every
    /// synced file is missing, and reading that as "the agent deleted all of it" would delete the host's files. The
    /// copy is simply pulled again instead. Most of the manifest missing counts as lost, so a handful of genuine
    /// deletions is still recognised as such.
    /// </summary>
    private bool CopyWasLost(bool copyExisted)
    {
        var synced = _manifest.Paths;
        if (synced.Count == 0)
        {
            return false;
        }

        if (!copyExisted)
        {
            return true;
        }

        var missing = synced.Count(p => !File.Exists(LocalPath(p)));
        return (missing == synced.Count && missing > 1) || (missing > 10 && missing * 2 > synced.Count);
    }

    /// <summary>Every file in the copy the sync would carry, as repository-relative paths; ignored folders are not entered.</summary>
    private List<string> CopyFiles(string directory)
    {
        var files = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var relative = Normalize(Path.GetRelativePath(CopyPath, file));
                if (_ignore.ShouldSync(relative, isDirectory: false))
                {
                    files.Add(relative);
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                if (_ignore.ShouldSync(Normalize(Path.GetRelativePath(CopyPath, sub)), isDirectory: true))
                {
                    files.AddRange(CopyFiles(sub));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read is left for the watcher; not worse than not having looked.
        }

        return files;
    }

    private void Conflict(string path, string what)
    {
        Interlocked.Increment(ref _conflicts);
        _notify?.Invoke($"Agent copy conflict on '{path}': {what}. The host version won; nothing was merged silently.");
    }

    private async Task PullTreeAsync(
        string relative, HashSet<string> seen, List<string> recovered, bool lostCopy, CancellationToken ct)
    {
        var entries = await _bridge.ListAsync(relative, ct).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = Normalize(entry.Path);
            if (_ignore.WhyWithheld(path, entry.IsDirectory) is { } why)
            {
                Withheld(path, why);
                continue;
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(LocalPath(path));
                await PullTreeAsync(path, seen, recovered, lostCopy, ct).ConfigureAwait(false);
                continue;
            }

            seen.Add(path);
            var local = LocalPath(path);
            var exists = File.Exists(local);
            var copyUnchanged = exists ? _manifest.CopyUnchanged(path, local) : null;

            if (_manifest.Get(path) == entry.ModifiedUtc)
            {
                if (exists && copyUnchanged is null)
                {
                    copyUnchanged = await SettleUnrecordedAsync(path, entry, local, ct).ConfigureAwait(false);
                }

                // The host has not moved since the last sync. What the copy holds decides (backlog 147).
                if (exists && copyUnchanged != false)
                {
                    continue; // unchanged since the last sync
                }

                if (exists || (!lostCopy && _manifest.Entry(path)?.Hash is not null))
                {
                    // Changed or deleted in the copy and never written back: it goes back now. A deletion is carried
                    // only for a path the manifest recorded content for, so a manifest older than that record pulls
                    // the file again as it always did.
                    recovered.Add(path);
                    continue;
                }

                await PullFileAsync(path, ct).ConfigureAwait(false);
                continue;
            }

            if (exists && copyUnchanged == false)
            {
                // Both sides moved: the host changed, and the copy holds an edit that never reached it.
                Conflict(path, "it changed on the host while an agent's edit to it had not been written back");
            }

            await PullFileAsync(path, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A path synced before the manifest recorded content (backlog 147 addendum, 2026-09-27): two thirds of the entries in
    /// this repository's own copy had none, and for them an agent's edit stranded by a stop was still neither written
    /// back nor noticed. The host has not moved since that sync, so the host's bytes ARE what the copy held then, and one
    /// comparison settles it: a different size needs no read at all, equal bytes are recorded so the question is never
    /// asked again, and different bytes are the agent's. Null when it cannot be told (the host file moved or vanished
    /// meanwhile), which leaves the path to the rules as they were.
    /// </summary>
    private async Task<bool?> SettleUnrecordedAsync(string path, BridgeEntryDto entry, string local, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(local).Length != entry.Size)
            {
                return false;
            }

            var host = await _bridge.ReadAsync(path, ct).ConfigureAwait(false);
            if (host is null || host.ModifiedUtc != entry.ModifiedUtc)
            {
                return null;
            }

            var copy = await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false);
            if (!copy.AsSpan().SequenceEqual(host.Bytes))
            {
                return false;
            }

            _manifest.Set(path, entry.ModifiedUtc, copy, local);
            Interlocked.Increment(ref _recordedAtStart);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task PullFileAsync(string path, CancellationToken ct)
    {
        var content = await _bridge.ReadAsync(path, ct).ConfigureAwait(false);
        if (content is null)
        {
            return; // vanished between listing and read
        }

        // No echo mark here: the copy watcher is not armed during the initial pull, and a stale
        // mark would swallow a genuine agent edit made right after startup.
        var local = LocalPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await File.WriteAllBytesAsync(local, content.Bytes, ct).ConfigureAwait(false);
        if (content.ModifiedUtc is { } stamp)
        {
            _manifest.Set(path, stamp, content.Bytes, local);
        }

        Interlocked.Increment(ref _syncedFiles);
        Touch();
    }

    private async Task OnHostEventAsync(BridgeFileEventDto fileEvent)
    {
        switch (fileEvent.Type)
        {
            case BridgeClient.ConnectedEventType:
                RequestRescan("the host's live watch connected, and the bridge reports nothing from before");
                return;
            case BridgeClient.OverflowEventType:
                RequestRescan("the bridge reported that it lost host events");
                return;
        }

        var path = Normalize(fileEvent.Path);
        if (_ignore.WhyWithheld(path, isDirectory: false) is { } why)
        {
            Withheld(path, why);
            return;
        }

        // A rename away from a path is a delete of that path as far as the copy is concerned, and is held like one.
        var oldPath = fileEvent.Type == "renamed" && fileEvent.OldPath is { Length: > 0 } old ? Normalize(old) : null;
        if (oldPath is not null && !IsBridgeTemp(oldPath) && _ignore.ShouldSync(oldPath, isDirectory: false))
        {
            _heldHostDeletes[oldPath] = DateTimeOffset.UtcNow;
        }

        if (IsBridgeTemp(path))
        {
            return;
        }

        await _apply.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            // Backlog 161: a path this session never synced is new on the host, and the snapshot taken at the start
            // cannot know whether the host's git ignores it. Nothing crosses until that is answered. A path the host
            // says it CREATED is asked about afresh, not from an earlier answer (backlog 253).
            if (_manifest.Get(path) is null)
            {
                if (fileEvent.Type is "created" or "renamed")
                {
                    _ignore.Host.Forget(path);
                }

                switch (await _ignore.Host.IgnoresAsync(path, _cts.Token).ConfigureAwait(false))
                {
                    case true:
                        Withheld(path, _ignore.Host.Why(path) ?? "the host's git ignores it");
                        return;
                    case null:
                        // Backlog 253: this used to drop the event for good. The rescan asks again.
                        Withheld(path, "the host's git could not be asked whether it ignores it; it is asked again at the next rescan");
                        RequestRescan($"the host's git could not answer for '{path}'");
                        return;
                }
            }

            if (fileEvent.Type == "deleted")
            {
                // Held, not applied: ResolveHeldHostDeletesAsync asks the host again once the debounce has passed.
                _heldHostDeletes[path] = DateTimeOffset.UtcNow;
            }
            else
            {
                await ApplyHostChangeAsync(path).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            // Never fatal, and never only "the next change heals it": a file created once and never changed again has no
            // next change (backlog 253). A timeout here is an OperationCanceledException, which used to end the live
            // watch for the rest of the session. The rescan fetches what this event could not. That includes a FOLDER: its
            // event's read fails here, and a folder moved into the workspace is reported as that one event and nothing
            // for the files inside it, so the rescan is what brings them.
            _log.LogDebug(
                ex, "A host change to '{Path}' in workspace {WorkspaceId} could not be applied to the agent copy; the host is rescanned.",
                path, _workspaceId);
            RequestRescan($"a host change to '{path}' could not be applied");
        }
        finally
        {
            _apply.Release();
        }

        Touch();
    }

    /// <summary>Asks the write-back loop for a rescan of the host (backlog 253); requests made before it runs are one rescan.</summary>
    private void RequestRescan(string reason)
    {
        _rescanReason = reason;
        Interlocked.Exchange(ref _rescanRequested, 1);
    }

    /// <summary>Logs, once per path and reason at Debug, that a path is kept out of the copy and why (backlog 253).</summary>
    private void Withheld(string path, string reason)
    {
        if (!_log.IsEnabled(LogLevel.Debug) || _withheldSaid.Count >= WithheldLogCap || !_withheldSaid.TryAdd(path + "\n" + reason, 0))
        {
            return;
        }

        _log.LogDebug(
            "'{Path}' is withheld from the agent copy of workspace {WorkspaceId}: {Reason}.", path, _workspaceId, reason);
    }

    /// <summary>Runs a rescan when one was asked for, or the backstop interval has passed, and the spacing allows it.</summary>
    private async Task RescanIfDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastRescanEnded is { } ended && now - ended < MinimumRescanSpacing)
        {
            return;
        }

        string reason;
        if (Interlocked.Exchange(ref _rescanRequested, 0) == 1)
        {
            reason = _rescanReason;
        }
        else if (now - _lastFullLook >= RescanInterval)
        {
            reason = "the periodic look at the host";
        }
        else
        {
            return;
        }

        try
        {
            await RescanAsync(reason, ct).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _rescanFailures, 0) > 0)
            {
                _log.LogInformation("The rescan of the host for workspace {WorkspaceId} works again.", _workspaceId);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Said once at Warning, because a rescan that keeps failing withholds every file it was meant to bring, and a
            // Debug line alone left nothing to read (the complaint #253 opened with). Retried as before.
            if (Interlocked.Increment(ref _rescanFailures) == 1)
            {
                _log.LogWarning(
                    "A rescan of the host for workspace {WorkspaceId} ({Reason}) failed: {Error}. Files the live watch missed "
                    + "are not in the agent copy until it works; it is retried.",
                    _workspaceId, reason, ex.Message);
            }
            else
            {
                _log.LogDebug(ex, "A rescan of the host for workspace {WorkspaceId} failed again.", _workspaceId);
            }

            RequestRescan(reason);
        }
        finally
        {
            _lastRescanEnded = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Compares the host's tree with the manifest and fetches what the live watch did not deliver (backlog 253). It
    /// LISTS the host (a folder the rules withhold is not entered) and reads a file only when its host stamp differs
    /// from the manifest's, or when the manifest has never seen it and the host's git does not ignore it; each fetch
    /// goes through <see cref="ApplyHostChangeAsync"/> under the apply gate, so it re-reads the host and the echo check
    /// holds. A path with a write-back pending is the write-back's to settle. A synced path the host no longer lists is
    /// HELD as a host delete, so the host is asked again before the copy loses it, as for a live delete. A listing that
    /// fails part way changes nothing and asks again.
    /// </summary>
    private async Task RescanAsync(string reason, CancellationToken ct)
    {
        // Tracking first: a file that became tracked since the last look is no longer withheld (backlog 253).
        if (!await _ignore.Host.RefreshTrackedAsync(ct).ConfigureAwait(false))
        {
            _log.LogDebug("The host's tracked files could not be listed for workspace {WorkspaceId}; the last list stands.", _workspaceId);
        }

        var synced = _manifest.Paths.ToHashSet(StringComparer.Ordinal);
        var listed = new Dictionary<string, BridgeEntryDto>(StringComparer.Ordinal);
        await ListHostAsync(string.Empty, listed, ct).ConfigureAwait(false);
        _lastFullLook = DateTimeOffset.UtcNow;

        var fresh = listed.Keys.Where(p => _manifest.Get(p) is null).ToList();
        if (fresh.Count > 1)
        {
            await _ignore.Host.AskAsync(fresh, ct).ConfigureAwait(false); // one host call for the lot; a failure is asked per path below
        }

        var fetched = new List<string>();
        var askAgain = false;
        foreach (var (path, entry) in listed)
        {
            ct.ThrowIfCancellationRequested();
            var known = _manifest.Get(path);
            if (known == entry.ModifiedUtc || _pendingWriteBacks.ContainsKey(path) || _heldHostDeletes.ContainsKey(path))
            {
                continue;
            }

            if (known is null)
            {
                switch (await _ignore.Host.IgnoresAsync(path, ct).ConfigureAwait(false))
                {
                    case true:
                        Withheld(path, _ignore.Host.Why(path) ?? "the host's git ignores it");
                        continue;
                    case null:
                        Withheld(path, "the host's git could not be asked whether it ignores it; it is asked again at the next rescan");
                        askAgain = true;
                        continue;
                }
            }

            await _apply.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Both sides moved (an edit that is not on its way to the host: one withheld for carrying a credential,
                // say): the host wins and it is said, as at start. Only when the host really had moved, so a listing
                // that went stale under a write-back is not called a conflict.
                var local = LocalPath(path);
                var copyEdited = known is not null && File.Exists(local) && _manifest.CopyUnchanged(path, local) == false;
                if (!_pendingWriteBacks.ContainsKey(path) && await ApplyHostChangeAsync(path).ConfigureAwait(false))
                {
                    fetched.Add(path);
                    if (copyEdited)
                    {
                        Conflict(path, "it changed on the host while an agent's edit to it had not been written back");
                    }
                }
            }
            finally
            {
                _apply.Release();
            }
        }

        foreach (var gone in synced.Where(p => !listed.ContainsKey(p) && _ignore.ShouldSync(p, isDirectory: false)))
        {
            if (!_pendingWriteBacks.ContainsKey(gone))
            {
                _heldHostDeletes.TryAdd(gone, DateTimeOffset.UtcNow);
            }
        }

        if (askAgain)
        {
            RequestRescan("the host's git could not answer for every new path");
        }

        if (fetched.Count > 0)
        {
            Touch();
            _log.LogInformation(
                "A rescan of the host for workspace {WorkspaceId} ({Reason}) brought {Count} file(s) into the agent copy that "
                + "the live watch had not delivered. Among them: {Examples}",
                _workspaceId, reason, fetched.Count, string.Join(", ", fetched.Order(StringComparer.Ordinal).Take(5)));
        }
    }

    /// <summary>Every host file the rules let through, by path; a folder that is gone by the time it is listed is skipped.</summary>
    private async Task ListHostAsync(string relative, Dictionary<string, BridgeEntryDto> listed, CancellationToken ct)
    {
        IReadOnlyList<BridgeEntryDto> entries;
        try
        {
            entries = await _bridge.ListAsync(relative, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound && relative.Length > 0)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var path = Normalize(entry.Path);
            if (_ignore.WhyWithheld(path, entry.IsDirectory) is { } why)
            {
                Withheld(path, why);
            }
            else if (entry.IsDirectory)
            {
                await ListHostAsync(path, listed, ct).ConfigureAwait(false);
            }
            else if (!IsBridgeTemp(path))
            {
                listed[path] = entry;
            }
        }
    }

    /// <summary>
    /// Settles the host deletes held since at least one debounce interval (backlog 247) by looking at the host again:
    /// a path the host no longer has is deleted from the copy, as a host delete always was; a path the host HAS is a
    /// save that the host's filesystem reported as a delete and a create (the bridge's own atomic write, or an editor's
    /// on the host), so it is applied as a change, through the usual echo check, and nothing is deleted.
    /// </summary>
    private async Task ResolveHeldHostDeletesAsync(CancellationToken ct)
    {
        foreach (var (path, heldAt) in _heldHostDeletes.ToArray())
        {
            if (DateTimeOffset.UtcNow - heldAt < DebounceInterval || !_heldHostDeletes.TryRemove(path, out _))
            {
                continue;
            }

            try
            {
                if (await _bridge.StatAsync(path, ct).ConfigureAwait(false) is not { } entry)
                {
                    TryDeleteLocal(path);
                    _manifest.Remove(path);
                    _manifest.Save();
                }
                else
                {
                    await ApplyHostChangeAsync(path).ConfigureAwait(false);
                    if (_wroteBack.TryGetValue(path, out var ours) && ours == entry.ModifiedUtc)
                    {
                        // The host reported our own write-back's atomic rename. Every save does that on Windows, so it
                        // is not worth a line at Information each time.
                        _log.LogDebug(
                            "A host delete of '{Path}' in workspace {WorkspaceId} was treated as a save because the file is there again.",
                            path, _workspaceId);
                    }
                    else
                    {
                        _log.LogInformation(
                            "A host delete of '{Path}' in workspace {WorkspaceId} was treated as a save because the file is there again.",
                            path, _workspaceId);
                    }
                }

                Touch();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
            {
                // The host could not be asked: hold it again rather than guess either way.
                _heldHostDeletes.TryAdd(path, DateTimeOffset.UtcNow);
            }
        }
    }

    private static bool IsBridgeTemp(string path) => path.EndsWith(BridgeTempSuffix, StringComparison.Ordinal);

    /// <summary>Brings the host's current content of the path into the copy; true when it wrote something.</summary>
    private async Task<bool> ApplyHostChangeAsync(string path)
    {
        var content = await _bridge.ReadAsync(path, _cts.Token).ConfigureAwait(false);
        if (content is null)
        {
            return false;
        }

        if (content.ModifiedUtc is { } stamp && _manifest.Get(path) == stamp)
        {
            return false; // the echo of our own write-back, or already synced
        }

        var local = LocalPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        _appliedContent[path] = SHA256.HashData(content.Bytes);
        await File.WriteAllBytesAsync(local, content.Bytes, _cts.Token).ConfigureAwait(false);
        if (content.ModifiedUtc is { } newStamp)
        {
            _manifest.Set(path, newStamp, content.Bytes, local);
        }

        _manifest.Save();
        Interlocked.Increment(ref _syncedFiles);
        return true;
    }

    private void EnqueueLocal(string fullPath, bool deleted = false)
    {
        // Every copy-side event resets the quiet period SettleAsync measures, including the ones below
        // that turn out to be ignored: what matters is that the agent is still writing.
        Interlocked.Exchange(ref _lastLocalEventTicks, DateTimeOffset.UtcNow.UtcTicks);

        string relative;
        try
        {
            relative = Normalize(Path.GetRelativePath(CopyPath, fullPath));
        }
        catch (ArgumentException)
        {
            return;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal) || relative == ".")
        {
            return;
        }

        if (Directory.Exists(fullPath))
        {
            // A directory is never itself a write-back: directories materialize host-side through the
            // file writes inside them. But this event is also the only reliable notice that a new
            // subtree exists, and on Linux that matters, because IncludeSubdirectories is not a kernel
            // feature there. FileSystemWatcher keeps ONE INOTIFY WATCH PER DIRECTORY and adds the watch
            // for a directory created a moment ago only after handling this event, so a file written
            // into it before that watch arms raises nothing at all. Nothing else recovers it: this
            // engine rescans the host (backlog 253) but never the copy, and the file cannot be pruned either (it never entered the
            // manifest), so it sits in the copy forever while the host never sees it. Measured 3 and 4
            // failures in 10 runs on a 2-CPU container, against 0 in 10 when the same write goes to a
            // directory that already existed, and agents create a directory and immediately write into
            // it constantly. So walk what is already inside instead of trusting the watch to catch up.
            // Skipped for an ignored directory, so an agent's node_modules is not enumerated.
            if (_ignore.ShouldSync(relative, isDirectory: true))
            {
                EnqueueDescendants(fullPath);
            }

            return;
        }

        if (!_ignore.ShouldSync(relative, isDirectory: false) || IsBridgeTemp(relative))
        {
            return;
        }

        if (deleted)
        {
            _copyDeleteSeen[relative] = 0;
        }

        // Any event restamps the path, so a delete followed by a create (or a temp file renamed over it) inside the
        // debounce is ONE write-back, and what it does is decided by what is in the copy then, never by this event.
        _pendingWriteBacks[relative] = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Enqueue every file already inside <paramref name="directory"/>, so a subtree that appeared
    /// before its watch armed is still written back. Each file goes back through
    /// <see cref="EnqueueLocal"/>, so ignore rules still apply to all of them (and the echo check, at write-back), and
    /// a file that was seen normally is simply re-stamped rather than sent twice.
    /// </summary>
    private void EnqueueDescendants(string directory)
    {
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The tree is live and the agent owns it: a directory can vanish mid-walk. Losing this
            // sweep is not worse than not having attempted it.
            return;
        }

        foreach (var file in files)
        {
            EnqueueLocal(file);
        }
    }

    private async Task WriteBackLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DebounceInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _apply.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await ResolveHeldHostDeletesAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _apply.Release();
            }

            // Lists without the apply gate and takes it per fetched file, so host events keep moving while it lists.
            await RescanIfDueAsync(ct).ConfigureAwait(false);

            var due = _pendingWriteBacks.ToArray();
            await AskAboutNewPathsAsync(due, ct).ConfigureAwait(false);
            foreach (var pair in due)
            {
                if (DateTimeOffset.UtcNow - pair.Value < DebounceInterval)
                {
                    continue; // still settling
                }

                // In flight from BEFORE the path leaves the queue, so SettleAsync never sees a moment where
                // the queue is empty and the write has not happened.
                Interlocked.Increment(ref _inFlight);
                var gated = false;
                try
                {
                    if (!_pendingWriteBacks.TryRemove(pair.Key, out _))
                    {
                        continue;
                    }

                    // Never interleaved with a host event (backlog 247): the stamp a write-back records and the host
                    // event that reads it are always in that order.
                    await _apply.WaitAsync(ct).ConfigureAwait(false);
                    gated = true;
                    await WriteBackAsync(pair.Key, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (gated)
                    {
                        _apply.Release();
                    }

                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }
    }

    /// <summary>
    /// Asks the host's git, in one call, about every due path this session has never synced (backlog 161), so a burst
    /// of new files costs one host call and not one each. Only a warm-up: <see cref="WriteBackAsync"/> asks for itself
    /// about any path left unanswered, so a failure here changes nothing but the number of calls.
    /// </summary>
    private async Task AskAboutNewPathsAsync(KeyValuePair<string, DateTimeOffset>[] due, CancellationToken ct)
    {
        var fresh = due
            .Where(p => DateTimeOffset.UtcNow - p.Value >= DebounceInterval && _manifest.Get(p.Key) is null)
            .Select(p => p.Key)
            .ToList();
        if (fresh.Count < 2)
        {
            return;
        }

        try
        {
            await _ignore.Host.AskAsync(fresh, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
        }
    }

    private async Task WriteBackAsync(string path, CancellationToken ct)
    {
        var local = LocalPath(path);
        var deleteSeen = _copyDeleteSeen.TryRemove(path, out _);
        try
        {
            // Backlog 247: what happens is decided by the copy NOW, not by the event that queued it. A path that is
            // there is written with its content, whatever events came before; a path that is gone is deleted.
            if (!File.Exists(local))
            {
                // The agent deleted a previously synced file: propagate; a never-synced path is noise.
                if (_manifest.Get(path) is not null)
                {
                    await _bridge.DeleteAsync(path, ct).ConfigureAwait(false);
                    _manifest.Remove(path);
                    _manifest.Save();
                    Touch();
                }

                return;
            }

            if (deleteSeen)
            {
                _log.LogInformation(
                    "A delete of '{Path}' in the agent copy of workspace {WorkspaceId} was treated as a save because the file is there again.",
                    path, _workspaceId);
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                _pendingWriteBacks[path] = DateTimeOffset.UtcNow; // still being written; retry
                return;
            }

            if (_appliedContent.TryGetValue(path, out var applied))
            {
                if (applied.AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                {
                    return; // our own host-to-copy apply echoing back: the copy still holds exactly that
                }

                // The agent moved on from what we applied. Forget the hash, or an agent that later puts
                // these exact bytes back would be mistaken for an echo and never written back.
                _appliedContent.TryRemove(path, out _);
            }

            if (_manifest.Get(path) is null)
            {
                // Backlog 161: a file the agent created. The host's git is asked whether it ignores that name, by any of
                // its sources, and an ignored file stays in the copy: the person keeps such files off every
                // repository, and a write here would put one in their folder. "Could not ask" is held back and tried
                // again, never taken as "not ignored".
                switch (await _ignore.Host.IgnoresAsync(path, ct).ConfigureAwait(false))
                {
                    case true:
                        if (_keptInCopy.TryAdd(path, 0))
                        {
                            _log.LogInformation(
                                "'{Path}' stays in the agent copy of workspace {WorkspaceId}: the host's git ignores it.",
                                path, _workspaceId);
                        }

                        return;
                    case null:
                        _pendingWriteBacks[path] = DateTimeOffset.UtcNow + UnansweredRetry;
                        return;
                }
            }

            if (!MayLeaveTheContainer(path, bytes))
            {
                return;
            }

            using var stream = new MemoryStream(bytes);
            var stamp = await _bridge.WriteAsync(path, stream, _manifest.Get(path), ct).ConfigureAwait(false);
            _wroteBack[path] = stamp;
            _manifest.Set(path, stamp, bytes, local);
            _manifest.Save();
            Touch();
        }
        catch (BridgeConflictException conflict)
        {
            Interlocked.Increment(ref _conflicts);
            if (conflict.Missing)
            {
                // The host deleted it while the agent edited: host wins, the copy follows.
                TryDeleteLocal(path);
                _manifest.Remove(path);
            }
            else
            {
                // The host changed underneath the agent: host wins, rewrite the copy from it.
                try
                {
                    await ApplyHostChangeAsync(path).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                }
            }

            _manifest.Save();
            _notify?.Invoke(
                $"Agent copy conflict on '{path}': the host version won; nothing was merged silently.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Bridge briefly away: put it back for a later round.
            _pendingWriteBacks[path] = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Whether these bytes may be written to the host folder (backlog 108). A file carrying the value of a
    /// credential the agent runs with is WITHHELD: it stays in the copy, the host file is left as it was, and
    /// the user is told which file and which variable, never the value. It is not deleted and not rewritten,
    /// because the studio does not edit what an agent wrote; a later save without the value goes through
    /// like any other. When the credentials cannot be read the file is held back and tried again, because
    /// "could not check" is not "clean".
    /// </summary>
    private bool MayLeaveTheContainer(string path, byte[] bytes)
    {
        if (_heldCredentials is null)
        {
            return true;
        }

        IReadOnlyCollection<AgentHeldCredential> held;
        try
        {
            held = _heldCredentials();
        }
        catch (Exception)
        {
            _pendingWriteBacks[path] = DateTimeOffset.UtcNow;
            if (Interlocked.Exchange(ref _uncheckable, 1) == 0)
            {
                _notify?.Invoke(
                    "Agent changes are being held in the copy: the studio could not read the credentials it "
                    + "checks outgoing files against. They are written to the host as soon as it can.");
            }

            return false;
        }

        Interlocked.Exchange(ref _uncheckable, 0);

        // Latin-1 maps every byte to one character, so a value is found in a file of any encoding that
        // stores it as ASCII, and a binary file is searched as it is rather than mangled by a decoder.
        if (AgentCredentialExposure.CarriedBy(Encoding.Latin1.GetString(bytes), held) is not { } name)
        {
            _withheld.TryRemove(path, out _);
            return true;
        }

        var hash = SHA256.HashData(bytes);
        if (!_withheld.TryGetValue(path, out var told) || !told.AsSpan().SequenceEqual(hash))
        {
            _withheld[path] = hash;
            _notify?.Invoke(
                $"'{path}' was NOT written to the host folder: it contains the value of {name}, a credential the "
                + "agent runs with. It is still in the agent's copy. Remove the value and the file syncs normally.");
        }

        return false;
    }

    private void TryDeleteLocal(string path)
    {
        var local = LocalPath(path);
        try
        {
            if (File.Exists(local))
            {
                // No echo mark needed for a delete: every caller drops the manifest entry straight after,
                // and the write-back treats a missing file with no manifest entry as noise.
                _appliedContent.TryRemove(path, out _);
                File.Delete(local);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string LocalPath(string relative) =>
        Path.Combine(CopyPath, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private void Touch() => _lastActivityUtc = DateTimeOffset.UtcNow;

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _watcher?.Dispose();
        foreach (var loop in new[] { _hostEventLoop, _writeBackLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _manifest.Save();
        _cts.Dispose();
        _apply.Dispose();
    }
}
