using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// The ONE place the studio decides whether it is working against a host bridge workspace: the
/// bridge coordinates arrive as container env (injected by the launcher, plan 2a), and the active
/// workspace id comes from the circuit's window (<see cref="WorkspaceSelection"/>, plan 2b-ii)
/// with the settings row as fallback/dev seed. All three must be present for bridge mode;
/// anything less means the local clone exactly as before.
/// </summary>
public sealed class BridgeWorkspaceContext(IConfiguration configuration, WorkspaceSelection? selection = null)
{
    public const string ActiveIdKey = "Workspace:ActiveBridgeId";
    public const string ActiveNameKey = "Workspace:ActiveName";
    public const string UrlKey = "FRAVASHI_BRIDGE_URL";
    public const string TokenKey = "FRAVASHI_BRIDGE_TOKEN";

    public string? BaseUrl => configuration[UrlKey];

    public string? Token => configuration[TokenKey];

    public string? ActiveWorkspaceId => selection?.WorkspaceId ?? configuration[ActiveIdKey];

    /// <summary>Bridge coordinates present (the welcome page can list/register/pick), regardless of an active workspace.</summary>
    public bool IsBridgeConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Token);

    public bool IsBridgeActive =>
        IsBridgeConfigured && !string.IsNullOrWhiteSpace(ActiveWorkspaceId);
}
