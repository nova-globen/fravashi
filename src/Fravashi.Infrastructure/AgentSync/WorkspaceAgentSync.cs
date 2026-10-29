using System.Collections.Concurrent;
using System.Text;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Notifications;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.AgentSync;

/// <summary>
/// <see cref="IWorkspaceAgentSync"/>: lazily starts one <see cref="WorkspaceSyncSession"/> per
/// workspace id under the workspaces volume root (<c>FRAVASHI_WORKSPACES_ROOT</c>, injected by
/// the launcher, plan 2d Task 1). Each session gets its OWN BridgeClient bound to that workspace
/// id (the circuit-scoped client follows the circuit's workspace and cannot serve a singleton).
/// Inert without the root or the bridge env: dev hosts run without agent copies, honestly.
/// </summary>
public sealed class WorkspaceAgentSync : IWorkspaceAgentSync, IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly INotificationHistory? _notifications;
    private readonly ILogger<WorkspaceAgentSync> _logger;
    private readonly ConcurrentDictionary<string, WorkspaceSyncSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal);
    private readonly Lock _startLock = new();
    private bool _inertLogged;

    // Backlog 108: handed to every session so no file carrying a held credential's value reaches the host.
    private readonly Func<IReadOnlyCollection<AgentHeldCredential>>? _heldCredentials;

    public WorkspaceAgentSync(
        IConfiguration configuration,
        INotificationHistory? notifications = null,
        ILogger<WorkspaceAgentSync>? logger = null,
        Func<IReadOnlyCollection<AgentHeldCredential>>? heldCredentials = null)
    {
        _heldCredentials = heldCredentials;
        _configuration = configuration;
        _notifications = notifications;
        _logger = logger ?? NullLogger<WorkspaceAgentSync>.Instance;
    }

    private string? WorkspacesRoot => _configuration["FRAVASHI_WORKSPACES_ROOT"];

    private bool BridgeConfigured =>
        !string.IsNullOrWhiteSpace(_configuration[BridgeWorkspaceContext.UrlKey])
        && !string.IsNullOrWhiteSpace(_configuration[BridgeWorkspaceContext.TokenKey]);

    public void EnsureStarted(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(WorkspacesRoot) || !BridgeConfigured)
        {
            if (!_inertLogged)
            {
                _inertLogged = true;
                _logger.LogInformation(
                    "Agent workspace sync is inert: no workspaces volume or no bridge in this environment.");
            }

            return;
        }

        lock (_startLock)
        {
            // A recorded failure waits for Retry (backlog 173): a surface calls this on every render, so
            // retrying here would turn one failure into a loop of them.
            if (_sessions.ContainsKey(workspaceId) || _failures.ContainsKey(workspaceId))
            {
                return;
            }

            var client = ClientFor(workspaceId);
            var session = new WorkspaceSyncSession(
                client, workspaceId, WorkspacesRoot!,
                WorkspaceSyncIgnore.FromGitignoreText(null),
                message => _notifications?.Add(message, NotificationSeverity.Warning),
                _heldCredentials);
            _sessions[workspaceId] = session;

            _ = Task.Run(async () =>
            {
                try
                {
                    // What the host's git ignores decides what syncs (proposed decision 3, backlog 161): the
                    // repository's .gitignore and, through the host's own git, every other source it honours,
                    // including the person's global excludes. Both are read before the pull starts.
                    var gitignore = await client.ReadAsync(".gitignore", CancellationToken.None).ConfigureAwait(false);
                    var hostIgnore = await HostGitIgnore.LoadAsync(client, _logger, CancellationToken.None).ConfigureAwait(false);
                    var ignore = WorkspaceSyncIgnore.FromGitignoreText(
                        gitignore is null ? null : Encoding.UTF8.GetString(gitignore.Bytes), hostIgnore);
                    var configured = new WorkspaceSyncSession(
                        client, workspaceId, WorkspacesRoot!, ignore,
                        message => _notifications?.Add(message, NotificationSeverity.Warning),
                        _heldCredentials, _logger);
                    _sessions[workspaceId] = configured;
                    await configured.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Agent workspace copy ready for {WorkspaceId} at {CopyPath} ({Files} file(s)).",
                        workspaceId, configured.CopyPath, configured.Status.SyncedFiles);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Agent workspace sync failed to start for {WorkspaceId}.", workspaceId);

                    // The reason is kept for the surfaces before the session goes, so there is no moment in
                    // which the workspace reads as neither starting nor failed (backlog 173).
                    _failures[workspaceId] = ex.Message;
                    _sessions.TryRemove(workspaceId, out _);
                    StartChanged?.Invoke(workspaceId);
                }
            });
        }
    }

    public string? StartFailureFor(string workspaceId) =>
        _failures.TryGetValue(workspaceId, out var reason) ? reason : null;

    public void Retry(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return;
        }

        _failures.TryRemove(workspaceId, out _);
        EnsureStarted(workspaceId);
        StartChanged?.Invoke(workspaceId);
    }

    public event Action<string>? StartChanged;

    public string? CopyPathFor(string workspaceId) =>
        _sessions.TryGetValue(workspaceId, out var session) ? session.CopyPath : null;

    public object? StatusFor(string workspaceId) =>
        _sessions.TryGetValue(workspaceId, out var session) ? session.Status : null;

    public async Task<bool?> SettleAsync(string workspaceId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || !_sessions.TryGetValue(workspaceId, out var session))
        {
            return null;
        }

        return await session.SettleAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private BridgeClient ClientFor(string workspaceId)
    {
        // A dedicated context: the singleton engine syncs SPECIFIC workspaces regardless of any
        // circuit's selection.
        var context = new BridgeWorkspaceContext(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BridgeWorkspaceContext.UrlKey] = _configuration[BridgeWorkspaceContext.UrlKey],
                [BridgeWorkspaceContext.TokenKey] = _configuration[BridgeWorkspaceContext.TokenKey],
                [BridgeWorkspaceContext.ActiveIdKey] = workspaceId,
            })
            .Build());
        return new BridgeClient(new HttpClient(), context);
    }

    /// <inheritdoc />
    public async Task StopAsync(string workspaceId)
    {
        if (_sessions.TryRemove(workspaceId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _failures.TryRemove(workspaceId, out _);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }
}
