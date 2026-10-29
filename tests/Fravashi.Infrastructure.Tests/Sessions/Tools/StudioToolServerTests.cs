using System.Globalization;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Updates;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions.Tools;

/// <summary>
/// Backlog 84's acceptance, against the server the Web route calls: two sessions cannot see each other's answers,
/// a restart mid-call resolves honestly instead of hanging or double-applying, an oversized result is truncated
/// with a stated limit, and a client speaking another protocol version is refused with a clear message.
/// The requests are the literal shapes the pinned claude 2.1.222 sent on 2026-09-15.
/// </summary>
public sealed class StudioToolServerTests : IDisposable
{
    private const string Version = "2025-11-25";

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "fravashi-tools-" + Guid.NewGuid().ToString("N"));

    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly AgentSessionId _sessionA = AgentSessionId.New();
    private readonly AgentSessionId _sessionB = AgentSessionId.New();
    private readonly string _secretA;
    private readonly string _secretB;

    public StudioToolServerTests()
    {
        _secretA = _broker.RegisterSession(_sessionA, "/workspaces/a");
        _secretB = _broker.RegisterSession(_sessionB, "/workspaces/b");
    }

    public void Dispose()
    {
        _broker.Dispose();
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    // ---- protocol and authentication ------------------------------------------------------

    [Fact]
    public async Task Initialize_with_the_version_the_pinned_cli_speaks_is_accepted_and_echoed()
    {
        var server = Server();

        var response = await server.HandleAsync(Post(_secretA, InitializeBody(Version), protocolVersion: null));

        response.StatusCode.Should().Be(200);
        using var json = JsonDocument.Parse(response.Json!);
        var result = json.RootElement.GetProperty("result");
        result.GetProperty("protocolVersion").GetString().Should().Be(Version);
        result.GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("fravashi");
        result.GetProperty("capabilities").TryGetProperty("tools", out _).Should().BeTrue();
        json.RootElement.GetProperty("id").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task An_older_client_version_is_refused_with_a_message_naming_both_versions_and_the_session_is_told()
    {
        var server = Server();
        (AgentSessionId Session, string Sentence)? refused = null;
        server.ClientRefused += (session, sentence) => refused = (session, sentence);

        var response = await server.HandleAsync(Post(_secretA, InitializeBody("2024-11-05"), protocolVersion: null));

        response.StatusCode.Should().Be(200, "a version refusal is a JSON-RPC error, which the CLI reports verbatim");
        using var json = JsonDocument.Parse(response.Json!);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(-32602);
        error.GetProperty("message").GetString().Should()
            .Contain("Unsupported protocol version").And.Contain("2024-11-05").And.Contain(Version);
        error.GetProperty("data").GetProperty("requested").GetString().Should().Be("2024-11-05");
        error.GetProperty("data").GetProperty("supported")[0].GetString().Should().Be(Version);

        refused.Should().NotBeNull();
        refused!.Value.Session.Should().Be(_sessionA);
        refused.Value.Sentence.Should().Contain("unavailable");
    }

    [Fact]
    public async Task A_later_request_repeating_an_unsupported_version_header_is_refused()
    {
        var response = await Server().HandleAsync(Post(_secretA, ListBody(), protocolVersion: "2025-03-26"));

        response.StatusCode.Should().Be(400);
        response.Json.Should().Contain("2025-03-26").And.Contain(Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-session")]
    public async Task A_request_without_a_known_session_secret_is_refused_and_nothing_runs(string? secret)
    {
        var tool = new CountingTool("counter");
        var response = await Server(tool).HandleAsync(Post(secret, CallBody("counter", "toolu_1")));

        response.StatusCode.Should().Be(401);
        tool.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task A_released_session_loses_its_tools_at_the_same_moment_it_loses_its_approvals()
    {
        var tool = new CountingTool("counter");
        var server = Server(tool);
        _broker.ReleaseSession(_sessionA);

        var response = await server.HandleAsync(Post(_secretA, CallBody("counter", "toolu_1")));

        response.StatusCode.Should().Be(401);
        tool.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task A_request_from_a_web_page_is_refused_even_with_a_valid_secret()
    {
        var tool = new CountingTool("counter");
        var response = await Server(tool).HandleAsync(
            new StudioToolHttpRequest("POST", _secretA, Version, "http://evil.example", CallBody("counter", "toolu_1")));

        response.StatusCode.Should().Be(403);
        tool.Invocations.Should().Be(0);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task The_stream_and_session_endpoints_answer_method_not_allowed(string method)
    {
        // Measured: 2.1.222 probes with a GET after initialize and carries on when it gets a 405.
        var response = await Server().HandleAsync(new StudioToolHttpRequest(method, _secretA, Version, null, string.Empty));

        response.StatusCode.Should().Be(405);
        response.Allow.Should().Be("POST");
    }

    [Fact]
    public async Task A_notification_is_accepted_without_a_body()
    {
        var response = await Server().HandleAsync(Post(_secretA, """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

        response.StatusCode.Should().Be(202);
        response.Json.Should().BeNull();
    }

    [Theory]
    [InlineData("{not json", 400, -32700)]
    [InlineData("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""", 400, -32600)]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"ping"}""", 400, -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"resources/list"}""", 200, -32601)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"nope"}}""", 200, -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"studio_version","arguments":[1]}}""", 200, -32602)]
    public async Task Malformed_or_unknown_requests_get_the_json_rpc_error_for_what_is_wrong(string body, int status, int code)
    {
        var response = await Server().HandleAsync(Post(_secretA, body));

        response.StatusCode.Should().Be(status);
        using var json = JsonDocument.Parse(response.Json!);
        json.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(code);
    }

    [Fact]
    public async Task A_request_body_over_the_ceiling_is_refused_before_it_is_parsed()
    {
        var body = new string(' ', StudioToolServer.MaxRequestBytes + 1);

        var response = await Server().HandleAsync(Post(_secretA, body));

        response.StatusCode.Should().Be(413);
    }

    // ---- tools ----------------------------------------------------------------------------

    [Fact]
    public async Task The_tool_list_carries_the_version_tool_marked_read_only()
    {
        var response = await Server().HandleAsync(Post(_secretA, ListBody()));

        using var json = JsonDocument.Parse(response.Json!);
        var tool = json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == StudioVersionTool.ToolName);
        tool.GetProperty("inputSchema").GetProperty("type").GetString().Should().Be("object");
        tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_tool_list_marks_exactly_the_builders_and_the_reviewers_tools_to_load_upfront()
    {
        // Work item #335's description names these five by hand as the tools nearly every builder calls (no
        // per-tool call count was taken); AgentReviewTools.Studio's four are the only studio tools a
        // held-to-reading conversation may call at all. The CLI's documented switch is a _meta entry on the
        // tool's own tools/list listing, so this is the server's own answer, not the command line.
        var response = await Server(
                new CountingTool(SessionWorkLinkTool.ToolName),
                new CountingTool(SessionPlanLinkTool.ToolName),
                new CountingTool(GitStatusTool.ToolName),
                new CountingTool(GitCommitTool.ToolName),
                new CountingTool(AttemptCommitTool.ToolName),
                new CountingTool(GitDiffTool.ToolName),
                new CountingTool(GitShowTool.ToolName),
                new CountingTool(WorkItemReadTool.ToolName),
                new CountingTool(WikiReadTool.ToolName),
                new CountingTool("wiki_write"))
            .HandleAsync(Post(_secretA, ListBody()));

        // The set itself, as literals: a test that only loops over AlwaysLoadedTools to decide what to check
        // would pass with that set empty, so the names are pinned here and the reviewer's own list is checked
        // against it rather than against itself.
        StudioToolServer.AlwaysLoadedTools.Should().BeEquivalentTo(
        [
            SessionWorkLinkTool.ToolName,
            SessionPlanLinkTool.ToolName,
            GitStatusTool.ToolName,
            GitCommitTool.ToolName,
            AttemptCommitTool.ToolName,
            GitDiffTool.ToolName,
            GitShowTool.ToolName,
            WorkItemReadTool.ToolName,
            WikiReadTool.ToolName,
        ]);
        foreach (var studioTool in AgentReviewTools.Studio)
        {
            StudioToolServer.AlwaysLoadedTools.Should().Contain(
                studioTool[(StudioToolServer.CliToolName(string.Empty).Length)..],
                $"{studioTool} is one of the only studio tools a held-to-reading conversation may call at all");
        }

        using var json = JsonDocument.Parse(response.Json!);
        var tools = json.RootElement.GetProperty("result").GetProperty("tools");

        bool MarkedAlwaysLoad(string name) => tools.EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == name)
            .TryGetProperty("_meta", out var meta)
            && meta.TryGetProperty("anthropic/alwaysLoad", out var flag)
            && flag.GetBoolean();

        foreach (var name in new[]
        {
            SessionWorkLinkTool.ToolName, SessionPlanLinkTool.ToolName, GitStatusTool.ToolName,
            GitCommitTool.ToolName, AttemptCommitTool.ToolName, GitDiffTool.ToolName, GitShowTool.ToolName,
            WorkItemReadTool.ToolName, WikiReadTool.ToolName,
        })
        {
            MarkedAlwaysLoad(name).Should().BeTrue($"{name} is one of the builder's or the reviewer's tools");
        }

        MarkedAlwaysLoad(StudioVersionTool.ToolName).Should().BeFalse("studio_version stays deferred behind tool search");
        MarkedAlwaysLoad("wiki_write").Should().BeFalse("every other studio tool stays deferred behind tool search");
    }

    [Fact]
    public async Task The_version_tool_answers_with_the_running_build()
    {
        var response = await Server().HandleAsync(Post(_secretA, CallBody(StudioVersionTool.ToolName, "toolu_v")));

        var (text, isError) = ReadResult(response);
        isError.Should().BeFalse();
        using var payload = JsonDocument.Parse(text);
        payload.RootElement.GetProperty("version").GetString().Should().Be("9.8.7");
        payload.RootElement.GetProperty("variant").GetString().Should().Be("full");
        payload.RootElement.GetProperty("toolServerProtocol").GetString().Should().Be(Version);
    }

    [Fact]
    public async Task A_replayed_call_returns_the_recorded_result_and_does_not_run_again()
    {
        var tool = new CountingTool("counter");
        var server = Server(tool);

        var first = ReadResult(await server.HandleAsync(Post(_secretA, CallBody("counter", "toolu_same"))));
        var second = ReadResult(await server.HandleAsync(Post(_secretA, CallBody("counter", "toolu_same"))));

        tool.Invocations.Should().Be(1);
        second.Should().Be(first);
    }

    [Fact]
    public async Task Two_sessions_presenting_the_same_call_id_never_see_each_others_answers()
    {
        var tool = new CountingTool("counter");
        var server = Server(tool);

        var a = ReadResult(await server.HandleAsync(Post(_secretA, CallBody("counter", "toolu_shared"))));
        var b = ReadResult(await server.HandleAsync(Post(_secretB, CallBody("counter", "toolu_shared"))));

        tool.Invocations.Should().Be(2, "session B's call is its own call, not a replay of A's");
        a.Text.Should().Contain(_sessionA.ToString());
        b.Text.Should().Contain(_sessionB.ToString()).And.NotContain(_sessionA.ToString());

        // And A replaying still gets A's answer, untouched by B.
        ReadResult(await server.HandleAsync(Post(_secretA, CallBody("counter", "toolu_shared")))).Should().Be(a);
        tool.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task The_claim_is_on_disk_before_the_tool_runs()
    {
        var ledger = new FileToolCallLedger(_dataDirectory);
        var tool = new BlockingTool("slow");
        var server = Server(ledger, tool);

        var call = server.HandleAsync(Post(_secretA, CallBody("slow", "toolu_slow")));
        await tool.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        File.ReadAllText(ledger.PathFor(_sessionA)).Should().Contain("\"state\":\"started\"").And.Contain("toolu_slow");

        tool.Release.SetResult();
        ReadResult(await call).IsError.Should().BeFalse();
        File.ReadAllText(ledger.PathFor(_sessionA)).Should().Contain("\"state\":\"completed\"");
    }

    [Fact]
    public async Task A_duplicate_arriving_while_the_first_is_still_running_is_refused_not_run_twice()
    {
        var tool = new BlockingTool("slow");
        var server = Server(tool);

        var first = server.HandleAsync(Post(_secretA, CallBody("slow", "toolu_dup")));
        await tool.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var duplicate = ReadResult(await server.HandleAsync(Post(_secretA, CallBody("slow", "toolu_dup"))));
        duplicate.IsError.Should().BeTrue();
        duplicate.Text.Should().Contain("already running");

        tool.Release.SetResult();
        ReadResult(await first).IsError.Should().BeFalse();
        tool.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task A_call_cut_off_by_a_studio_restart_is_answered_as_unknown_and_never_run_again()
    {
        // The process that claimed it: the claim reached the disk, then the studio went away before the outcome.
        var before = new FileToolCallLedger(_dataDirectory);
        before.Claim(_sessionA, "toolu_cut", "counter", Fingerprint("counter", "{}"))
            .Outcome.Should().Be(ToolCallClaimOutcome.Fresh);

        // The next studio process, with a fresh ledger over the same data directory.
        var tool = new CountingTool("counter");
        var server = Server(new FileToolCallLedger(_dataDirectory), tool);

        var replay = ReadResult(await server.HandleAsync(Post(_secretA, CallBody("counter", "toolu_cut"))));

        replay.IsError.Should().BeTrue();
        replay.Text.Should().Contain("cannot tell whether the call took effect");
        tool.Invocations.Should().Be(0, "an unknown outcome must never become a double apply");

        // And the answer is durable: a third process says the same thing.
        var third = Server(new FileToolCallLedger(_dataDirectory), tool);
        ReadResult(await third.HandleAsync(Post(_secretA, CallBody("counter", "toolu_cut")))).Should().Be(replay);
        tool.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task A_completed_call_replays_its_recorded_result_after_a_restart()
    {
        var tool = new CountingTool("counter");
        var first = ReadResult(await Server(new FileToolCallLedger(_dataDirectory), tool)
            .HandleAsync(Post(_secretA, CallBody("counter", "toolu_done"))));

        var afterRestart = ReadResult(await Server(new FileToolCallLedger(_dataDirectory), tool)
            .HandleAsync(Post(_secretA, CallBody("counter", "toolu_done"))));

        afterRestart.Should().Be(first);
        tool.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task A_key_reused_for_different_arguments_is_refused()
    {
        var tool = new CountingTool("counter");
        var server = Server(tool);
        await server.HandleAsync(Post(_secretA, CallBody("counter", "toolu_k", """{"n":1}""")));

        var reused = ReadResult(await server.HandleAsync(Post(_secretA, CallBody("counter", "toolu_k", """{"n":2}"""))));

        reused.IsError.Should().BeTrue();
        reused.Text.Should().Contain("already used for a different call");
        tool.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task A_mutating_tool_without_an_operation_key_is_refused_and_does_not_run()
    {
        var tool = new CountingTool("writer", StudioToolAccess.Mutating);

        var result = ReadResult(await Server(tool).HandleAsync(Post(_secretA, CallBody("writer", key: null))));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("no operation id");
        tool.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task A_read_only_tool_without_a_key_still_runs()
    {
        var tool = new CountingTool("counter");

        ReadResult(await Server(tool).HandleAsync(Post(_secretA, CallBody("counter", key: null)))).IsError.Should().BeFalse();
        tool.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task A_tool_that_throws_is_an_error_result_and_its_outcome_is_recorded()
    {
        var tool = new ThrowingTool("boom");
        var server = Server(tool);

        var first = ReadResult(await server.HandleAsync(Post(_secretA, CallBody("boom", "toolu_boom"))));
        var replay = ReadResult(await server.HandleAsync(Post(_secretA, CallBody("boom", "toolu_boom"))));

        first.IsError.Should().BeTrue();
        first.Text.Should().Contain("kaput");
        replay.Should().Be(first);
        tool.Invocations.Should().Be(1);
    }

    // ---- ceilings -------------------------------------------------------------------------

    [Fact]
    public async Task An_oversized_result_is_truncated_to_the_ceiling_with_a_stated_limit_and_replays_as_delivered()
    {
        var tool = new CountingTool("big", text: new string('x', 50_000));
        var server = Server(new FileToolCallLedger(_dataDirectory), 4096, tool);

        var delivered = ReadResult(await server.HandleAsync(Post(_secretA, CallBody("big", "toolu_big"))));

        Encoding.UTF8.GetByteCount(delivered.Text).Should().BeLessThanOrEqualTo(4096);
        delivered.Text.Should().Contain("[Fravashi truncated this tool result:")
            .And.Contain("of 50000 bytes shown")
            .And.Contain("limit is 4096 bytes per result");
        ReadResult(await server.HandleAsync(Post(_secretA, CallBody("big", "toolu_big")))).Should().Be(delivered);
    }

    [Fact]
    public void Truncation_never_splits_a_character()
    {
        var text = string.Concat(Enumerable.Repeat("å\U0001F600", 2000));

        var capped = StudioToolServer.Cap(text, 1000);

        Encoding.UTF8.GetByteCount(capped).Should().BeLessThanOrEqualTo(1000);
        capped.Should().NotContain("�");
        char.IsHighSurrogate(capped[capped.IndexOf("\n\n[Fravashi", StringComparison.Ordinal) - 1]).Should().BeFalse();
    }

    [Fact]
    public void A_result_inside_the_ceiling_is_untouched()
    {
        StudioToolServer.Cap("small", 1000).Should().Be("small");
    }

    // ---- helpers --------------------------------------------------------------------------

    private StudioToolServer Server(params IStudioTool[] extra) =>
        Server(new FileToolCallLedger(_dataDirectory), StudioToolServer.DefaultMaxResultBytes, extra);

    private StudioToolServer Server(IToolCallLedger ledger, params IStudioTool[] extra) =>
        Server(ledger, StudioToolServer.DefaultMaxResultBytes, extra);

    private StudioToolServer Server(IToolCallLedger ledger, int maxBytes, params IStudioTool[] extra) =>
        new(_broker, [new StudioVersionTool(new FixedProductInfo()), .. extra], ledger, "9.8.7", maxBytes);

    private static StudioToolHttpRequest Post(string? secret, string body, string? protocolVersion = Version) =>
        new("POST", secret, protocolVersion, null, body);

    private static string InitializeBody(string version) =>
        """{"method":"initialize","params":{"protocolVersion":"VERSION","capabilities":{"roots":{"listChanged":true},"elicitation":{}},"clientInfo":{"name":"claude-code","version":"2.1.222"}},"jsonrpc":"2.0","id":0}"""
            .Replace("VERSION", version, StringComparison.Ordinal);

    private static string ListBody() => """{"method":"tools/list","jsonrpc":"2.0","id":1}""";

    private static string CallBody(string tool, string? key, string arguments = "{}")
    {
        var meta = key is null ? string.Empty : $",\"_meta\":{{\"claudecode/toolUseId\":\"{key}\",\"progressToken\":2}}";
        return $"{{\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{arguments}{meta}}},\"jsonrpc\":\"2.0\",\"id\":2}}";
    }

    private static (string Text, bool IsError) ReadResult(StudioToolHttpResponse response)
    {
        response.StatusCode.Should().Be(200);
        using var json = JsonDocument.Parse(response.Json!);
        var result = json.RootElement.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    private static string Fingerprint(string tool, string arguments) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\n" + arguments)));

    private sealed class FixedProductInfo : IProductInfo
    {
        public string Version => "9.8.7";

        public string Variant => "full";
    }

    private sealed class CountingTool(string name, StudioToolAccess access = StudioToolAccess.ReadOnly, string? text = null) : IStudioTool
    {
        private int _invocations;

        public int Invocations => _invocations;

        public string Name => name;

        public string Description => "counts";

        public string InputSchemaJson => """{"type":"object"}""";

        public StudioToolAccess Access => access;

        public Task<StudioToolResult> InvokeAsync(StudioToolCall call, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _invocations);
            return Task.FromResult(StudioToolResult.Success(
                text ?? string.Create(CultureInfo.InvariantCulture, $"call {n} for {call.SessionId}")));
        }
    }

    private sealed class BlockingTool(string name) : IStudioTool
    {
        private int _invocations;

        public int Invocations => _invocations;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => name;

        public string Description => "blocks";

        public string InputSchemaJson => """{"type":"object"}""";

        public StudioToolAccess Access => StudioToolAccess.Mutating;

        public async Task<StudioToolResult> InvokeAsync(StudioToolCall call, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocations);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return StudioToolResult.Success("done");
        }
    }

    private sealed class ThrowingTool(string name) : IStudioTool
    {
        public int Invocations { get; private set; }

        public string Name => name;

        public string Description => "throws";

        public string InputSchemaJson => """{"type":"object"}""";

        public StudioToolAccess Access => StudioToolAccess.ReadOnly;

        public Task<StudioToolResult> InvokeAsync(StudioToolCall call, CancellationToken cancellationToken = default)
        {
            Invocations++;
            throw new InvalidOperationException("kaput");
        }
    }
}
