using Fravashi.Application.Repository;
using Fravashi.Infrastructure.Repository;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// The scoped <see cref="IRepositoryChangeNotifier"/> that routes each subscription by the scope's workspace mode:
/// local mode delegates to the classic <see cref="RepositoryChangeWatcher"/>, bridge mode to the workspace's shared
/// subscriber in <see cref="BridgeChangeSubscribers"/>. Scoped since backlog 261 so it reads the SAME
/// <see cref="BridgeWorkspaceContext"/> as every other seam, the circuit's <c>?ws=</c> selection included; as a
/// singleton it could read only the settings row, and a window bound by <c>?ws=</c> alone heard nothing. Not
/// disposable: the watcher and the registry are singletons the container disposes.
/// </summary>
public sealed class WorkspaceChangeNotifier(
    RepositoryChangeWatcher local, BridgeChangeSubscribers bridge, BridgeWorkspaceContext context)
    : IRepositoryChangeNotifier
{
    public IDisposable Subscribe(Action<RepositoryChange> handler)
        => context.IsBridgeActive
            ? bridge.For(context).Subscribe(handler)
            : local.Subscribe(handler);
}
