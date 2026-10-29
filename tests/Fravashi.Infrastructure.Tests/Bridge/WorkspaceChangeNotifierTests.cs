using Fravashi.Application.Repository;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Bridge;

/// <summary>
/// Backlog 261: the change notifier the Code and Git pages subscribe to, resolved from the container the
/// registration chain really builds, against the REAL bridge. A window bound by <c>?ws=</c> carries its workspace
/// only in the scope's <see cref="WorkspaceSelection"/>, never in the settings row, and before 261 the notifier read
/// the row alone, so such a window never heard a host change.
/// </summary>
public sealed class WorkspaceChangeNotifierTests : IAsyncLifetime
{
    private readonly string _host = Path.Combine(
        Path.GetTempPath(), "fravashi-remote-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "fravashi-remote-tests", Guid.NewGuid().ToString("N"));
    private HostedBridge _bridge = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_host);
        Directory.CreateDirectory(_dataDir);
        _bridge = await HostedBridge.StartAsync(_host);
    }

    /// <summary>
    /// The registration groups the repository and bridge seams come from, as <see cref="WorkspaceModeSelectionTests"/>
    /// builds them, with scopes validated so a singleton holding the scoped selection fails. Not ValidateOnBuild:
    /// persistence is not in this chain, so the stores two unrelated services need are absent.
    /// </summary>
    private ServiceProvider Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton(new DataDirectory(_dataDir));
        services.AddRepositoryExplorer(configuration);
        services.AddGitService();
        services.AddBridgeWorkspaces();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Bridge coordinates as the launcher bakes them, and deliberately NO <c>Workspace:ActiveBridgeId</c>.</summary>
    private Dictionary<string, string?> BridgeWithoutTheRow() => new()
    {
        [BridgeWorkspaceContext.UrlKey] = _bridge.BaseUrl,
        [BridgeWorkspaceContext.TokenKey] = HostedBridge.Token,
    };

    private static IRepositoryChangeNotifier NotifierFor(IServiceScope scope, string? workspaceId)
    {
        if (workspaceId is not null)
        {
            scope.ServiceProvider.GetRequiredService<WorkspaceSelection>().Capture(workspaceId);
        }

        return scope.ServiceProvider.GetRequiredService<IRepositoryChangeNotifier>();
    }

    /// <summary>
    /// Subscribes and keeps writing <paramref name="name"/> under <paramref name="folder"/> until the subscription
    /// delivers it or ten seconds pass (backlog 107: the stream arms after Subscribe returns, so one write can fall
    /// into the gap). Answers whether it was delivered.
    /// </summary>
    private static async Task<bool> HearsAsync(IRepositoryChangeNotifier notifier, string folder, string name)
    {
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = notifier.Subscribe(change =>
        {
            if (change is { Kind: RepositoryChangeKind.ChangedOrCreated } && change.RelativePath == name)
            {
                heard.TrySetResult();
            }
        });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!heard.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, name), "x");
            // Longer than the local watcher's 300 ms quiet window, which a faster rewrite would keep restarting.
            await Task.WhenAny(heard.Task, Task.Delay(1000));
        }

        return heard.Task.IsCompleted;
    }

    [Fact]
    public async Task A_scope_bound_by_its_selection_hears_a_host_change_without_the_settings_row()
    {
        using var provider = Build(BridgeWithoutTheRow());
        using var scope = provider.CreateScope();
        var notifier = NotifierFor(scope, _bridge.WorkspaceId);

        // The scope's other seams already follow the selection (why the Code page's tree loads), so a deaf
        // subscription below is the notifier's alone.
        scope.ServiceProvider.GetRequiredService<IRepositoryReader>().Should().BeOfType<BridgeRepositoryReader>();

        var heard = await HearsAsync(notifier, _host, "made-on-the-host.txt");

        heard.Should().BeTrue("the scope's WorkspaceSelection names a workspace on a live bridge");
    }

    [Fact]
    public async Task The_settings_row_still_binds_a_scope_with_no_selection()
    {
        var values = BridgeWithoutTheRow();
        values[BridgeWorkspaceContext.ActiveIdKey] = _bridge.WorkspaceId;
        using var provider = Build(values);
        using var scope = provider.CreateScope();

        var heard = await HearsAsync(NotifierFor(scope, workspaceId: null), _host, "made-on-the-host.txt");

        heard.Should().BeTrue("the settings row is the fallback when the window carries no workspace");
    }

    [Fact]
    public async Task With_no_bridge_a_scope_hears_the_local_root_as_before()
    {
        var root = Path.Combine(_dataDir, "clone");
        Directory.CreateDirectory(root);
        using var provider = Build(new() { ["Repository:Root"] = root });
        using var scope = provider.CreateScope();

        var heard = await HearsAsync(NotifierFor(scope, workspaceId: null), root, "made-locally.txt");

        heard.Should().BeTrue("local mode still watches Repository:Root");
        provider.GetRequiredService<BridgeChangeSubscribers>().Count.Should().Be(0);
    }

    [Fact]
    public void Scopes_on_the_same_workspace_share_a_subscriber_and_different_workspaces_do_not()
    {
        const string other = "0123456789abcdef";
        using var provider = Build(BridgeWithoutTheRow());
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        using var third = provider.CreateScope();
        var registry = provider.GetRequiredService<BridgeChangeSubscribers>();

        using var a = NotifierFor(first, _bridge.WorkspaceId).Subscribe(_ => { });
        using var b = NotifierFor(second, _bridge.WorkspaceId).Subscribe(_ => { });
        using var c = NotifierFor(third, other).Subscribe(_ => { });

        registry.Count.Should().Be(2, "two windows on one workspace share its stream and a third workspace has its own");
        var firstSubscriber = registry.For(first.ServiceProvider.GetRequiredService<BridgeWorkspaceContext>());
        registry.For(second.ServiceProvider.GetRequiredService<BridgeWorkspaceContext>()).Should().BeSameAs(firstSubscriber);
        registry.For(third.ServiceProvider.GetRequiredService<BridgeWorkspaceContext>()).Should().NotBeSameAs(firstSubscriber);
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        try { Directory.Delete(_host, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
