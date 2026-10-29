using Fravashi.Application.Migration;

namespace Fravashi.Web.Services;

/// <summary>
/// The setup's import scan for one window (item #373, <c>docs/plans/first-open-setup.md</c> section 3.5). It runs on
/// its own task, never on the circuit: the screen that shows it (the recommended setup or the Import step) starts it,
/// is told as rows arrive, and cancels it on leaving. A finished scan is kept for the window, so going from the
/// recommended setup to the steps and back does not scan again. Titles are read on request, for the rows a person can
/// see, a few at a time.
/// </summary>
public sealed class SetupScan(IRepositoryScans scans) : IDisposable
{
    /// <summary>How long the polite status line waits before it speaks again (section 8: at most every two seconds).</summary>
    public static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _titlesAsked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Count, string Note)> _workItems = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reads = new(4);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task<ScanResult?>? _running;
    private string? _workspaceId;
    private DateTimeOffset _lastChange = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAnnounced = DateTimeOffset.MinValue;

    /// <summary>Raised when rows arrive, the scan ends, or a title is read. Raised from a background thread.</summary>
    public event Action? Changed;

    /// <summary>The scan this window can run, once asked for; null when the folder cannot be read.</summary>
    public RepositoryScan? Scanner { get; private set; }

    /// <summary>The finished result, or null while it runs or before it ran.</summary>
    public ScanResult? Result { get; private set; }

    /// <summary>Where a running scan has got to.</summary>
    public ScanProgress? Progress { get; private set; }

    /// <summary>
    /// True while it runs. A flag of its own rather than the task's state: the last "changed" is raised from inside the
    /// task, and a screen repainting on it must already read the scan as finished.
    /// </summary>
    public bool IsRunning => _isRunning;

    private volatile bool _isRunning;

    /// <summary>True when the folder could not be read at all (no bridge and no dev root).</summary>
    public bool Unavailable { get; private set; }

    /// <summary>Why the scan failed, in words, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>True when the last scan was cancelled before it finished.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>The polite status text, changed at most every <see cref="AnnounceEvery"/>.</summary>
    public string? Announcement { get; private set; }

    /// <summary>Starts the scan for <paramref name="workspaceId"/>, unless one has finished or is running for it.</summary>
    public void Start(string workspaceId, string folderName)
    {
        lock (_gate)
        {
            if (_workspaceId == workspaceId && (Result is not null || IsRunning))
            {
                return;
            }

            _workspaceId = workspaceId;
            Result = null;
            Progress = null;
            Error = null;
            Cancelled = false;
            Unavailable = false;
            Announcement = $"Looking through {folderName} ...";
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _isRunning = true;
            _running = Task.Run(() => RunAsync(folderName, token), CancellationToken.None);
        }

        Changed?.Invoke();
    }

    /// <summary>Starts again from nothing, for Retry.</summary>
    public void Restart(string workspaceId, string folderName)
    {
        Cancel();
        lock (_gate)
        {
            _workspaceId = null;
        }

        Start(workspaceId, folderName);
    }

    /// <summary>Stops a running scan; what it found so far is dropped, and the next Start begins again.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                _cts?.Cancel();
                Cancelled = true;
                _workspaceId = null;
                _isRunning = false;
            }
        }
    }

    /// <summary>The finished result, waiting for a running scan; null when it was cancelled, failed or cannot run.</summary>
    public async Task<ScanResult?> WaitAsync()
    {
        var running = _running;
        if (running is null)
        {
            return Result;
        }

        try
        {
            return await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The title read for a row, or null while it is being read (asking starts the read).</summary>
    public string? TitleFor(string path)
    {
        lock (_gate)
        {
            if (_titles.TryGetValue(path, out var title))
            {
                return title;
            }

            if (Scanner is { } scanner && _titlesAsked.Add(path))
            {
                _ = ReadTitleAsync(scanner, path, _cts?.Token ?? CancellationToken.None);
            }

            return null;
        }
    }

    /// <summary>What a file gives as work items, once read (asking starts the read); for a row a person turned into work items.</summary>
    public (int Count, string Note)? WorkItemsFor(string path)
    {
        lock (_gate)
        {
            if (_workItems.TryGetValue(path, out var known))
            {
                return known;
            }

            if (Scanner is { } scanner && _titlesAsked.Add("work-items:" + path))
            {
                _ = ReadWorkItemsAsync(scanner, path);
            }

            return null;
        }
    }

    private async Task<ScanResult?> RunAsync(string folderName, CancellationToken ct)
    {
        try
        {
            Scanner ??= await scans.ForWindowAsync(ct).ConfigureAwait(false);
            if (Scanner is null)
            {
                Unavailable = true;
                Announcement = $"The files in {folderName} could not be read from here.";
                return null;
            }

            var progress = new Progress<ScanProgress>(p =>
            {
                Progress = p;
                var now = DateTimeOffset.UtcNow;
                if (now - _lastAnnounced >= AnnounceEvery)
                {
                    _lastAnnounced = now;
                    Announcement = $"Looking through {folderName} ... {p.FilesSeen:N0} files so far";
                }

                // Rows arrive per folder; a repaint four times a second is plenty and keeps a large folder cheap.
                if (now - _lastChange >= TimeSpan.FromMilliseconds(250))
                {
                    _lastChange = now;
                    Changed?.Invoke();
                }
            });
            var result = await Scanner.RunAsync(progress, ct).ConfigureAwait(false);
            Result = result;
            Announcement = result.Candidates.Count == 0
                ? $"No markdown documents or backlog files were found in {folderName}."
                : $"Found {result.Candidates.Count:N0} markdown files in {folderName}.";
            return result;
        }
        catch (OperationCanceledException)
        {
            Announcement = "Stopped looking.";
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = $"{folderName} could not be read just now: {ex.Message}";
            Announcement = Error;
            return null;
        }
        finally
        {
            _isRunning = false;
            Changed?.Invoke();
        }
    }

    private async Task ReadTitleAsync(RepositoryScan scanner, string path, CancellationToken ct)
    {
        await _reads.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var title = await scanner.TitleAsync(path, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _titles[path] = title;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            lock (_gate)
            {
                _titles[path] = DocumentTitleFallback(path);
            }
        }
        finally
        {
            _reads.Release();
        }

        Changed?.Invoke();
    }

    private async Task ReadWorkItemsAsync(RepositoryScan scanner, string path)
    {
        try
        {
            if (await scanner.WorkItemsOfAsync(path).ConfigureAwait(false) is { } found)
            {
                lock (_gate)
                {
                    _workItems[path] = found;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            lock (_gate)
            {
                _workItems[path] = (0, "Could not be read just now");
            }
        }

        Changed?.Invoke();
    }

    /// <summary>The file name as words, the same fallback a title read uses.</summary>
    public static string DocumentTitleFallback(string path) =>
        System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/')).Replace('-', ' ');

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _reads.Dispose();
    }
}
