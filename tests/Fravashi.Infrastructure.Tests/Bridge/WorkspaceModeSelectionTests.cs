using Fravashi.Application.Git;
using Fravashi.Application.Repository;
using Fravashi.Application.Search;
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
/// Pins the plan 2b-i mode selection: bridge config + an active workspace id flip every seam to
/// its bridge arm; anything less resolves the EXACT local types the studio always used.
/// </summary>
public sealed class WorkspaceModeSelectionTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "fravashi-remote-tests", Guid.NewGuid().ToString("N"));

    public WorkspaceModeSelectionTests() => Directory.CreateDirectory(_dataDir);

    private ServiceProvider Build(bool bridgeActive)
    {
        var values = new Dictionary<string, string?> { ["Repository:Root"] = _dataDir };
        if (bridgeActive)
        {
            values[BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9";
            values[BridgeWorkspaceContext.TokenKey] = "token";
            values[BridgeWorkspaceContext.ActiveIdKey] = "deadbeefdeadbeef";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton(new DataDirectory(_dataDir));
        services.AddRepositoryExplorer(configuration);
        services.AddGitService();
        services.AddBridgeWorkspaces();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void BridgeMode_ResolvesTheBridgeArms()
    {
        using var provider = Build(bridgeActive: true);
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<IRepositoryReader>().Should().BeOfType<BridgeRepositoryReader>();
        sp.GetRequiredService<IRepositoryWriter>().Should().BeOfType<BridgeRepositoryWriter>();
        sp.GetRequiredService<IRepositoryTrash>().Should().BeOfType<BridgeRepositoryTrash>();
        sp.GetRequiredService<IRepositorySearchService>().Should().BeOfType<BridgeSearchAdapter>();
        sp.GetRequiredService<IGitCommandRunner>().Should().BeOfType<BridgeGitCommandRunner>();
    }

    [Fact]
    public void LocalMode_ResolvesTheExactPreviousTypes()
    {
        using var provider = Build(bridgeActive: false);
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<IRepositoryReader>().Should().BeOfType<LocalRepositoryReader>();
        sp.GetRequiredService<IRepositoryWriter>().Should().BeOfType<LocalRepositoryWriter>();
        sp.GetRequiredService<IRepositoryTrash>().Should().BeOfType<RepositoryTrash>();
        sp.GetRequiredService<IRepositorySearchService>()
            .Should().BeOfType<Fravashi.Infrastructure.Search.RepositorySearchService>();
        sp.GetRequiredService<IGitCommandRunner>().Should().BeOfType<LocalGitCommandRunner>();
        sp.GetRequiredService<IRepositoryChangeNotifier>().Should().BeOfType<WorkspaceChangeNotifier>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
