using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>A snapshot of one requested version's install, as the banner shows it.</summary>
/// <param name="Requested">The version the workspace asked for, as it asked ("22", "22.12.0").</param>
/// <param name="Running">True while the install is in flight.</param>
/// <param name="Progress">The last progress report, while running.</param>
/// <param name="Result">How it ended, once it has.</param>
/// <param name="Dismissed">True once the reader closed the finished notice; see <see cref="NodeProvisionCoordinator.Dismiss"/>.</param>
public sealed record NodeProvisionStatus(
    string Requested,
    bool Running,
    NodeProvisionProgress? Progress,
    NodeProvisionResult? Result,
    bool Dismissed = false);

/// <summary>
/// Backlog 10: the studio-wide view of Node installs. A singleton, like <c>LiveSessionRegistry</c>, for the
/// same reason: the Sessions area and the Code page's panel are two views of one workspace, so the click
/// on either has to show its progress on both, and a second click on the other surface must join the
/// running install rather than start a parallel download of the same tarball. One install per requested
/// version at a time; a finished one stays readable until the next request for that version replaces it.
/// </summary>
public sealed class NodeProvisionCoordinator
{
    private readonly INodeProvisioner _provisioner;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, NodeProvisionStatus> _jobs = new(StringComparer.Ordinal);

    public NodeProvisionCoordinator(INodeProvisioner provisioner, ILogger<NodeProvisionCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(provisioner);
        _provisioner = provisioner;
        _logger = logger ?? NullLogger<NodeProvisionCoordinator>.Instance;
    }

    /// <summary>Raised after any status changes, on a thread pool thread; a component re-enters its renderer itself.</summary>
    public event Action? Changed;

    /// <summary>The install for a requested version, running or finished, or null when none was asked for.</summary>
    public NodeProvisionStatus? Find(string requested)
    {
        lock (_gate)
        {
            return _jobs.GetValueOrDefault(Normalize(requested));
        }
    }

    /// <summary>
    /// Starts installing the requested version, or returns the install already running for it. A finished
    /// install (success or failure) is replaced: the banner's Retry is this same call.
    /// </summary>
    public NodeProvisionStatus Start(string requested)
    {
        var key = Normalize(requested);
        NodeProvisionStatus status;
        lock (_gate)
        {
            if (_jobs.TryGetValue(key, out var existing) && existing.Running)
            {
                return existing;
            }

            status = new NodeProvisionStatus(key, true, new NodeProvisionProgress(NodeProvisionPhase.Resolving), null);
            _jobs[key] = status;
        }

        _ = RunAsync(key);
        Changed?.Invoke();
        return status;
    }

    /// <summary>
    /// Closes the finished notice for a version. It lives here and not in the banner because a finished
    /// install is kept for the life of the process while a pane is created and thrown away every time the
    /// reader moves between the Sessions area and the Code page's panel: as a field of one pane the green
    /// "is installed" notice came back on every new pane until a restart. A later install of the same
    /// version starts un-dismissed, so a Retry's outcome is shown again.
    /// </summary>
    public void Dismiss(string requested) => Update(Normalize(requested), s => s with { Dismissed = true });

    private async Task RunAsync(string key)
    {
        var progress = new SynchronousProgress(report => Update(key, s => s with { Progress = report }));
        NodeProvisionResult result;
        try
        {
            result = await _provisioner.InstallAsync(key, progress);
        }
        catch (Exception ex)
        {
            // The provisioner returns its expected failures; anything that still escapes is reported the
            // same way rather than lost on a fire-and-forget task, so the banner never spins forever.
            _logger.LogError(ex, "Provisioning Node {Requested} threw.", key);
            result = NodeProvisionResult.Failure($"Node {key} could not be installed: {ex.Message}. Nothing was installed.");
        }

        Update(key, s => s with { Running = false, Result = result });
    }

    private void Update(string key, Func<NodeProvisionStatus, NodeProvisionStatus> change)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(key, out var current))
            {
                _jobs[key] = change(current);
            }
        }

        Changed?.Invoke();
    }

    private static string Normalize(string requested) => requested.Trim().TrimStart('v', 'V');

    /// <summary>
    /// Reports on the caller's thread. <see cref="Progress{T}"/> posts through the captured
    /// SynchronizationContext, which a fire-and-forget task on the pool has none of, so reports would be
    /// queued and could land after the result did; the banner would then show "downloading" under "done".
    /// </summary>
    private sealed class SynchronousProgress(Action<NodeProvisionProgress> report) : IProgress<NodeProvisionProgress>
    {
        public void Report(NodeProvisionProgress value) => report(value);
    }
}
