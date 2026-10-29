using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Bridge;

namespace Fravashi.Web.Services;

/// <summary>How re-registering a recents row with the host came out.</summary>
public enum RecentReopenOutcome
{
    /// <summary>The host serves the folder; <see cref="RecentReopen.WorkspaceId"/> is the id to open.</summary>
    Ready,

    /// <summary>The host could not reach the folder right now (a drive not mapped, a disk not plugged in).</summary>
    Unreachable,

    /// <summary>The host can positively see the folder is gone.</summary>
    Missing,
}

public sealed record RecentReopen(RecentReopenOutcome Outcome, string? WorkspaceId);

/// <summary>
/// The one way a recents row is made openable again (backlog 14), shared by the home surface and the
/// title bar's workspace menu (backlog 29): the bridge's grant list is per fravashi.exe process, so a row
/// written by an earlier run is re-registered with the HOST before any window is pointed at it. The id
/// is a hash of the canonical path, so the same id comes back; a row stored before canonicalization is
/// replaced rather than duplicated. Transport failures propagate: they say nothing about the folder, and
/// each caller words them for its own surface.
/// </summary>
public static class RecentWorkspaceReopen
{
    public static async Task<RecentReopen> PrepareAsync(
        BridgeWorkspaceContext context,
        BridgeClient? bridge,
        IWorkspaceStore store,
        WorkspaceRecord workspace,
        CancellationToken cancellationToken)
    {
        if (!context.IsBridgeConfigured || bridge is null)
        {
            // Host `dotnet run` dev fallback: there is no bridge to register with (the client has no base
            // address), so the row opens exactly as it is.
            store.Touch(workspace.Id, workspace.Name, workspace.RootPath);
            return new RecentReopen(RecentReopenOutcome.Ready, workspace.Id);
        }

        // Only the HOST can tell: the studio is in a container where no host path exists. Its answer is
        // three-way, and the two failures must not be reported as the same thing.
        var registration = await bridge.RegisterWorkspaceAsync(workspace.RootPath, cancellationToken)
            .ConfigureAwait(false);
        if (registration.Outcome == BridgeRegistrationOutcome.Unreachable)
        {
            return new RecentReopen(RecentReopenOutcome.Unreachable, null);
        }

        if (registration.Outcome == BridgeRegistrationOutcome.Missing || registration.Workspace is not { } registered)
        {
            return new RecentReopen(RecentReopenOutcome.Missing, null);
        }

        if (registered.Id != workspace.Id)
        {
            store.Remove(workspace.Id);
        }

        store.Touch(registered.Id, registered.Name, registered.RootPath);
        return new RecentReopen(RecentReopenOutcome.Ready, registered.Id);
    }
}
