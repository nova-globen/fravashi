using Fravashi.Infrastructure.AgentSync;
using Fravashi.Infrastructure.Tests.Bridge;
using Microsoft.Extensions.Configuration;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentSync;

/// <summary>Plan 2d Task 4: the engine is inert without its environment and idempotent with it.</summary>
public sealed class WorkspaceAgentSyncTests : IAsyncLifetime
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "fravashi-agent-engine-tests", Guid.NewGuid().ToString("N"));
    private string _host = null!;
    private string _copies = null!;
    private HostedBridge _bridge = null!;

    public async Task InitializeAsync()
    {
        _host = Path.Combine(_base, "host");
        _copies = Path.Combine(_base, "copies");
        Directory.CreateDirectory(_host);
        Directory.CreateDirectory(_copies);
        File.WriteAllText(Path.Combine(_host, "app.cs"), "content\n");
        _bridge = await HostedBridge.StartAsync(_host);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            pairs.ToDictionary(p => p.Key, p => p.Value)).Build();

    [Fact]
    public void Inert_WithoutTheWorkspacesRoot_OrTheBridge()
    {
        var noRoot = new WorkspaceAgentSync(Config(
            ("FRAVASHI_BRIDGE_URL", _bridge.BaseUrl), ("FRAVASHI_BRIDGE_TOKEN", HostedBridge.Token)));
        noRoot.EnsureStarted("abc");
        noRoot.CopyPathFor("abc").Should().BeNull();

        var noBridge = new WorkspaceAgentSync(Config(("FRAVASHI_WORKSPACES_ROOT", _copies)));
        noBridge.EnsureStarted("abc");
        noBridge.CopyPathFor("abc").Should().BeNull();
    }

    [Fact]
    public async Task EnsureStarted_SyncsTheCopy_AndIsIdempotent()
    {
        await using var engine = new WorkspaceAgentSync(Config(
            ("FRAVASHI_WORKSPACES_ROOT", _copies),
            ("FRAVASHI_BRIDGE_URL", _bridge.BaseUrl),
            ("FRAVASHI_BRIDGE_TOKEN", HostedBridge.Token)));

        engine.EnsureStarted(_bridge.WorkspaceId);
        engine.EnsureStarted(_bridge.WorkspaceId); // second call: same session, no duplicate

        var expected = Path.Combine(_copies, _bridge.WorkspaceId, "app.cs");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(expected) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50); // poll interval inside a deadline wait, not an arm-window guess (backlog 107)
        }

        File.Exists(expected).Should().BeTrue();
        engine.CopyPathFor(_bridge.WorkspaceId).Should().Be(Path.Combine(_copies, _bridge.WorkspaceId));
        engine.StatusFor(_bridge.WorkspaceId).Should().BeOfType<WorkspaceSyncStatus>();
    }

    [Fact]
    public async Task A_start_the_bridge_refuses_is_kept_with_its_reason_until_it_is_tried_again()
    {
        // Backlog 173: the copy's failure used to be logged and forgotten, so the surfaces had no reason to show.
        var configuration = Config(
            ("FRAVASHI_WORKSPACES_ROOT", _copies),
            ("FRAVASHI_BRIDGE_URL", _bridge.BaseUrl),
            ("FRAVASHI_BRIDGE_TOKEN", "not-the-token"));
        await using var engine = new WorkspaceAgentSync(configuration);
        var changes = new List<string>();
        engine.StartChanged += id => { lock (changes) { changes.Add(id); } };

        engine.StartFailureFor(_bridge.WorkspaceId).Should().BeNull("nothing has been tried yet");
        engine.EnsureStarted(_bridge.WorkspaceId);

        // The change is raised last, after the reason is kept and the session gone, so waiting on it waits on both.
        await Until(() => { lock (changes) { return changes.Count > 0; } });
        // Backlog 177: the reason the pane shows names the request and the path, not only a status code.
        engine.StartFailureFor(_bridge.WorkspaceId).Should().Be("reading .gitignore was refused (401)");
        engine.CopyPathFor(_bridge.WorkspaceId).Should().BeNull();
        lock (changes)
        {
            changes.Should().Equal(_bridge.WorkspaceId);
        }

        // A render calling EnsureStarted again does not start another attempt: the failure stands.
        engine.EnsureStarted(_bridge.WorkspaceId);
        engine.CopyPathFor(_bridge.WorkspaceId).Should().BeNull();
        engine.StartFailureFor(_bridge.WorkspaceId).Should().NotBeNull();

        // Try again, with the cause gone, brings the copy up and the failure with it.
        configuration["FRAVASHI_BRIDGE_TOKEN"] = HostedBridge.Token;
        engine.Retry(_bridge.WorkspaceId);

        engine.StartFailureFor(_bridge.WorkspaceId).Should().BeNull("a copy that is starting is not called failed");
        engine.CopyPathFor(_bridge.WorkspaceId).Should().Be(Path.Combine(_copies, _bridge.WorkspaceId));
        var expected = Path.Combine(_copies, _bridge.WorkspaceId, "app.cs");
        await Until(() => File.Exists(expected));
        File.Exists(expected).Should().BeTrue();
        engine.StartFailureFor(_bridge.WorkspaceId).Should().BeNull();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50); // poll interval inside a deadline wait (backlog 107)
        }
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
