using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Configuration;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Bridge;

public sealed class WorkspaceSelectionTests
{
    [Fact]
    public void Capture_TakesTheFirstValueOnly()
    {
        var selection = new WorkspaceSelection();
        selection.Capture("first");
        selection.Capture("second");

        selection.WorkspaceId.Should().Be("first");
        selection.Captured.Should().BeTrue();
    }

    [Fact]
    public void Capture_TreatsBlankAsNull_ButStillCaptured()
    {
        var selection = new WorkspaceSelection();
        selection.Capture("  ");

        selection.WorkspaceId.Should().BeNull();
        selection.Captured.Should().BeTrue();
    }

    [Fact]
    public void Context_PrefersTheSelection_OverTheSettingsRow()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9",
            [BridgeWorkspaceContext.TokenKey] = "t",
            [BridgeWorkspaceContext.ActiveIdKey] = "from-settings",
        }).Build();

        var selection = new WorkspaceSelection();
        selection.Capture("from-window");

        new BridgeWorkspaceContext(configuration, selection).ActiveWorkspaceId.Should().Be("from-window");
        new BridgeWorkspaceContext(configuration).ActiveWorkspaceId.Should().Be("from-settings");
    }

    [Fact]
    public void Context_UncapturedSelection_FallsBackToTheSettingsRow()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.ActiveIdKey] = "from-settings",
        }).Build();

        new BridgeWorkspaceContext(configuration, new WorkspaceSelection())
            .ActiveWorkspaceId.Should().Be("from-settings");
    }
}
