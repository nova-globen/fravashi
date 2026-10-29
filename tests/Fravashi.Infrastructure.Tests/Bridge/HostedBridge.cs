using Fravashi.Bridge;
using Fravashi.Infrastructure.Bridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Tests.Bridge;

/// <summary>
/// Test helper hosting the REAL bridge (Fravashi.Bridge) on an ephemeral loopback port over a temp
/// workspace, plus the studio-side context/client pointed at it. This is the pinning fixture that
/// keeps the studio's mirrored DTOs honest against the actual wire format.
/// </summary>
internal sealed class HostedBridge : IAsyncDisposable
{
    public const string Token = "remote-seams-test-token";

    /// <summary>
    /// A folder name the hosted registry treats as living on a volume that is not attached (no test
    /// can unmap a real drive). Lets the client tests pin the 404-vs-503 distinction, which is what
    /// tells the studio "gone" from "not reachable right now", against the REAL bridge.
    /// </summary>
    public const string UnreachableFolderName = "on-an-offline-drive";

    private readonly WebApplication _app;

    private HostedBridge(WebApplication app, string baseUrl, string workspaceId, string workspaceRoot)
    {
        _app = app;
        BaseUrl = baseUrl;
        WorkspaceId = workspaceId;
        WorkspaceRoot = workspaceRoot;
    }

    public string BaseUrl { get; }
    public string WorkspaceId { get; }
    public string WorkspaceRoot { get; }

    /// <param name="cliProjectsRoot">Backlog 43: a stand-in for the host CLI's projects folder; null keeps the default.</param>
    /// <param name="cliConversationMaxBytes">Backlog 43: the bridge's import cap, lowered by tests that exercise it.</param>
    /// <param name="gitExecutable">Backlog 266: a program name that does not exist stands in for a host with no git.</param>
    public static async Task<HostedBridge> StartAsync(
        string workspaceRoot, string? cliProjectsRoot = null,
        long cliConversationMaxBytes = BridgeCliConversationService.MaxBytes,
        string gitExecutable = "git")
    {
        var registry = new WorkspaceRegistry(probe: path =>
            path.EndsWith(UnreachableFolderName, StringComparison.OrdinalIgnoreCase)
                ? WorkspacePathState.Unreachable
                : WorkspacePathProbe.Probe(path));
        var workspace = registry.Register(workspaceRoot);
        var app = BridgeApplication.Create(
            new BridgeServerOptions(
                Token, ["http://127.0.0.1:0"], CliProjectsRoot: cliProjectsRoot,
                CliConversationMaxBytes: cliConversationMaxBytes, GitExecutable: gitExecutable),
            registry);
        await app.StartAsync();
        return new HostedBridge(app, app.Urls.First(), workspace.Id, workspace.RootPath);
    }

    public BridgeWorkspaceContext CreateContext() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = BaseUrl,
            [BridgeWorkspaceContext.TokenKey] = Token,
            [BridgeWorkspaceContext.ActiveIdKey] = WorkspaceId,
        })
        .Build());

    public BridgeClient CreateClient() => new(new HttpClient(), CreateContext());

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
