using System.Collections.Concurrent;
using Fravashi.Application.Git;
using Fravashi.Application.Repository;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Git;

/// <summary>
/// <see cref="IWorkspaceGit"/>: the parsed git services built over a runner PINNED to one workspace (backlog
/// 150). The runner comes from a factory so the same class serves the bridge in the product and a local
/// repository in a test; the services over it are the very classes the Git page uses, so log, status, commit
/// and push read and write exactly what the page would, only against the workspace the caller named.
/// <para>
/// One set per workspace is kept, because the bridge runner caches its availability probe and a client per
/// call would probe the host on every tool call. A folder is registered with the bridge before any session
/// runs in it (the session's contained copy is synced from it), so the cached answer does not go stale in
/// the direction that matters.
/// </para>
/// </summary>
public sealed class WorkspaceGit : IWorkspaceGit
{
    private readonly Func<string, IGitCommandRunner?> _runners;
    private readonly IOptions<RepositoryReaderOptions> _options;
    private readonly IRepositoryWriteGate _gate;
    private readonly IConfiguration _configuration;
    private readonly GitHostAccess _access;
    private readonly ILoggerFactory? _loggers;
    private readonly ConcurrentDictionary<string, WorkspaceGitServices> _services = new(StringComparer.Ordinal);

    public WorkspaceGit(
        Func<string, IGitCommandRunner?> runners,
        IOptions<RepositoryReaderOptions> options,
        IRepositoryWriteGate gate,
        IConfiguration configuration,
        GitHostAccess access,
        ILoggerFactory? loggers = null)
    {
        _runners = runners ?? throw new ArgumentNullException(nameof(runners));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _loggers = loggers;
    }

    /// <inheritdoc />
    public WorkspaceGitServices? For(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return null;
        }

        if (_services.TryGetValue(workspaceId, out var known))
        {
            return known;
        }

        if (_runners(workspaceId) is not { } runner)
        {
            return null;
        }

        var built = new WorkspaceGitServices(
            workspaceId,
            runner,
            new GitCliService(runner),
            new GitWorkingTreeService(_options, runner, _gate, _configuration),
            new GitRemoteService(runner, _gate, _access, logger: _loggers?.CreateLogger<GitRemoteService>()));
        return _services.GetOrAdd(workspaceId, built);
    }

    /// <summary>
    /// The product's runner factory: a bridge runner over a client bound to the named workspace, built the
    /// way the sync engine builds its own (a dedicated context, so no circuit's selection is consulted).
    /// Null when the container has no bridge coordinates, which is a dev host: there is then no host git to
    /// reach for any workspace, and callers say so rather than falling back to a local clone nobody has.
    /// </summary>
    public static Func<string, IGitCommandRunner?> BridgeRunners(IConfiguration configuration, ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return workspaceId =>
        {
            var url = configuration[BridgeWorkspaceContext.UrlKey];
            var token = configuration[BridgeWorkspaceContext.TokenKey];
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var context = new BridgeWorkspaceContext(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [BridgeWorkspaceContext.UrlKey] = url,
                    [BridgeWorkspaceContext.TokenKey] = token,
                    [BridgeWorkspaceContext.ActiveIdKey] = workspaceId,
                })
                .Build());
            return new BridgeGitCommandRunner(
                new BridgeClient(new HttpClient(), context), loggers?.CreateLogger<BridgeGitCommandRunner>());
        };
    }
}
