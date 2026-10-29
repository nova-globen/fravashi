using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions.Tools;

/// <summary>
/// Backlog 84: how the tool server reaches a session (the MCP config beside the hook script, the
/// <c>--mcp-config</c> flag) and how its failures reach the user (the ledger's recovery sweep and a refused client,
/// both as transcript lines), plus the ledger's own durability edges.
/// </summary>
public sealed class ToolServerWiringTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-toolwiring-" + Guid.NewGuid().ToString("N"));

    public ToolServerWiringTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- the session's MCP config -------------------------------------------------------------

    [Fact]
    public void A_writer_with_a_tool_server_url_lays_down_an_mcp_config_carrying_the_session_secret()
    {
        var session = AgentSessionId.New();
        var writer = new SessionHookWriter(
            _directory, "http://127.0.0.1:8080/api/agent/approval", "http://127.0.0.1:8080/api/agent/tools");

        var files = writer.Write(session, "s3cret");

        files.ToolServerConfigPath.Should().Be(Path.Combine(files.Directory, "tools.mcp.json"));
        using var json = JsonDocument.Parse(File.ReadAllText(files.ToolServerConfigPath!));
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("fravashi");
        server.GetProperty("type").GetString().Should().Be("http");
        server.GetProperty("url").GetString().Should().Be("http://127.0.0.1:8080/api/agent/tools");
        server.GetProperty("headers").GetProperty("X-Fravashi-Session").GetString().Should().Be("s3cret");

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(files.ToolServerConfigPath!).Should().NotHaveFlag(UnixFileMode.OtherRead)
                .And.NotHaveFlag(UnixFileMode.GroupRead);
        }

        writer.Remove(session);
        File.Exists(files.ToolServerConfigPath!).Should().BeFalse("the secret goes with the session folder");
    }

    [Fact]
    public void A_writer_without_a_tool_server_url_writes_no_mcp_config()
    {
        var files = new SessionHookWriter(_directory, "http://127.0.0.1:8080/api/agent/approval")
            .Write(AgentSessionId.New(), "s3cret");

        files.ToolServerConfigPath.Should().BeNull();
        Directory.GetFiles(files.Directory).Select(Path.GetFileName).Should().BeEquivalentTo(["settings.json", "approve.sh"]);
    }

    [Fact]
    public void The_runner_passes_the_mcp_config_right_after_the_settings_file_when_there_is_one()
    {
        var request = new AgentSessionRequest(AgentSessionId.New(), "w", "/tmp", "hello", null, null);

        var arguments = ClaudeSessionRunner.BuildArguments(request, "/tmp/settings.json", "/tmp/tools.mcp.json").ToList();
        var settings = arguments.IndexOf("--settings");
        arguments[settings + 2].Should().Be("--mcp-config");
        arguments[settings + 3].Should().Be("/tmp/tools.mcp.json");

        ClaudeSessionRunner.BuildArguments(request, "/tmp/settings.json").Should().NotContain("--mcp-config");
    }

    [Theory]
    [InlineData(null, null, null, "http://127.0.0.1:8080/api/agent/tools")]
    [InlineData(null, "9090", null, "http://127.0.0.1:9090/api/agent/tools")]
    [InlineData("http://localhost:5090/api/agent/approval", null, null, "http://localhost:5090/api/agent/tools")]
    [InlineData("http://localhost:5090/api/agent/approval", null, "http://10.0.0.5:7000/tools", "http://10.0.0.5:7000/tools")]
    public void The_tool_server_url_follows_the_approval_endpoint_unless_named(
        string? approvalUrl, string? ports, string? toolUrl, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sessions:ApprovalUrl"] = approvalUrl,
                ["ASPNETCORE_HTTP_PORTS"] = ports,
                ["Sessions:ToolServerUrl"] = toolUrl,
            })
            .Build();

        SessionsServiceCollectionExtensions.ToolServerUrlFor(configuration).Should().Be(expected);
    }

    // ---- the ledger ---------------------------------------------------------------------------

    [Fact]
    public void A_torn_last_line_leaves_the_started_claim_standing_which_reads_as_interrupted()
    {
        var session = AgentSessionId.New();
        var first = new FileToolCallLedger(_directory);
        first.Claim(session, "toolu_1", "tool", "fp").Outcome.Should().Be(ToolCallClaimOutcome.Fresh);

        // A crash inside the completion write: half a line.
        File.AppendAllText(first.PathFor(session), "{\"key\":\"toolu_1\",\"tool\":\"tool\",\"fingerprint\":\"fp\",\"state\":\"compl");

        new FileToolCallLedger(_directory).Claim(session, "toolu_1", "tool", "fp")
            .Outcome.Should().Be(ToolCallClaimOutcome.Interrupted);
    }

    [Fact]
    public void Recovery_at_start_resolves_every_open_claim_of_a_previous_process_once()
    {
        var session = AgentSessionId.New();
        var before = new FileToolCallLedger(_directory);
        before.Claim(session, "toolu_open", "studio_version", "fp");
        before.Claim(session, "toolu_done", "studio_version", "fp2");
        before.Complete(session, "toolu_done", StudioToolResult.Success("ok"));

        var after = new FileToolCallLedger(_directory);
        after.RecoverInterrupted().Should().ContainSingle()
            .Which.Should().Be(new InterruptedToolCall(session, "toolu_open", "studio_version"));
        after.RecoverInterrupted().Should().BeEmpty("it was recorded as interrupted the first time");
        new FileToolCallLedger(_directory).RecoverInterrupted().Should().BeEmpty("and that record is durable");

        after.Claim(session, "toolu_done", "studio_version", "fp2").Should()
            .Be(new ToolCallClaim(ToolCallClaimOutcome.Replayed, StudioToolResult.Success("ok")));
    }

    [Fact]
    public void Forgetting_a_session_deletes_its_ledger()
    {
        var session = AgentSessionId.New();
        var ledger = new FileToolCallLedger(_directory);
        ledger.Claim(session, "toolu_1", "tool", "fp");

        ledger.Forget(session);

        File.Exists(ledger.PathFor(session)).Should().BeFalse();
        ledger.Claim(session, "toolu_1", "tool", "fp").Outcome.Should().Be(ToolCallClaimOutcome.Fresh);
    }

    // ---- what the user is told ----------------------------------------------------------------

    [Fact]
    public void A_call_interrupted_by_a_restart_is_told_to_the_conversation_it_belonged_to()
    {
        using var store = StoreScope.Create(_directory);
        var session = AgentSessionId.New();
        store.Store.Save(new SessionRecord(
            session, "w1", AgentTool.Claude, "t", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null));
        new FileToolCallLedger(_directory).Claim(session, "toolu_open", "studio_version", "fp");

        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5));
        using var registry = new LiveSessionRegistry(
            new IdleRunner(), store.Store, broker, toolLedger: new FileToolCallLedger(_directory));

        registry.RecoverInterruptedToolCalls().Should().Be(1);

        store.Store.Transcript(session).OfType<MessageEmitted>().Should().ContainSingle(m => m.Role == TranscriptRole.System)
            .Which.Text.Should().Contain("mcp__fravashi__studio_version").And.Contain("cannot tell");
    }

    [Fact]
    public async Task A_refused_client_puts_one_line_in_the_live_transcript_however_often_it_retries()
    {
        using var store = StoreScope.Create(_directory);
        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5));
        var server = new StudioToolServer(broker, [], new FileToolCallLedger(_directory), "1.0.0");
        var runner = new IdleRunner();
        using var registry = new LiveSessionRegistry(runner, store.Store, broker, toolServer: server);

        var session = registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "hello"));
        var secret = broker.RegisterSession(session.Id, "/workspaces/w1");
        var body = """{"method":"initialize","params":{"protocolVersion":"2024-11-05"},"jsonrpc":"2.0","id":0}""";

        await server.HandleAsync(new StudioToolHttpRequest("POST", secret, null, null, body));
        await server.HandleAsync(new StudioToolHttpRequest("POST", secret, null, null, body));

        store.Store.Transcript(session.Id).OfType<MessageEmitted>()
            .Where(m => m.Role == TranscriptRole.System && m.Text.Contains("2024-11-05", StringComparison.Ordinal))
            .Should().ContainSingle();
    }

    private sealed class IdleRunner : IAgentSessionRunner
    {
        public async IAsyncEnumerable<AgentEvent> RunAsync(
            AgentSessionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Ends at once: the registry keeps the session it started, which is all these tests need.
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId) => true;

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;
    }

    private sealed class StoreScope : IDisposable
    {
        private StoreScope(SqliteSessionStore store) => Store = store;

        public SqliteSessionStore Store { get; }

        public static StoreScope Create(string directory)
        {
            var factory = new SqliteConnectionFactory(
                new SqliteStoreOptions(Path.Combine(directory, "fravashi.db"), Pooling: false));
            SqliteSchema.EnsureCreated(factory);
            return new StoreScope(new SqliteSessionStore(factory));
        }

        public void Dispose()
        {
        }
    }
}
