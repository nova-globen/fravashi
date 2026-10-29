using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Framework;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Framework;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Plan 2e Task 4. The runner is driven against a REAL child process (a node stand-in speaking the
/// same stream-json protocol), so redirect, stdin, environment, working directory and kill are the
/// genuine article; only the model call is faked. What the process was actually handed is read back
/// from the report it writes, not from the arguments the test passed in.
/// </summary>
public class ClaudeSessionRunnerTests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fravashi-runner-" + Guid.NewGuid().ToString("N"));

    private readonly string _workspaceCopy;
    private readonly string _reportPath;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));

    public ClaudeSessionRunnerTests()
    {
        _workspaceCopy = Path.Combine(_scratch, "workspaces", "w1");
        _reportPath = Path.Combine(_scratch, "cli-report.json");
        Directory.CreateDirectory(_workspaceCopy);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _broker.Dispose();
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    // ---- harness ------------------------------------------------------------------------

    private static string FakeCliScript() =>
        Path.Combine(AppContext.BaseDirectory, "Sessions", "FakeCli", "fake-agent-cli.js");

    private SessionHookWriter Hooks() => new(_scratch, "http://127.0.0.1:8080/api/agent/approval");

    private ClaudeSessionRunner Runner(
        string mode = "reply",
        int exitAfter = 1,
        IAgentRuntimeCredentialStore? credentials = null,
        NodePathProvisioner? nodePaths = null,
        bool missingExecutable = false,
        IAgentApprovalBroker? broker = null,
        SessionFrameworkProjector? framework = null,
        Func<string, IReadOnlyDictionary<string, string>>? workspaceEnvironment = null) =>
        new(
            broker ?? _broker,
            Hooks(),
            launcher: missingExecutable
                ? new AgentCliLauncher("fravashi-no-such-cli-on-this-machine", [])
                : new AgentCliLauncher("node", [FakeCliScript()]),
            credentials: credentials,
            nodePaths: nodePaths,
            environment: new Dictionary<string, string>
            {
                ["FAKE_CLI_REPORT"] = _reportPath,
                ["FAKE_CLI_MODE"] = mode,
                ["FAKE_CLI_EXIT_AFTER"] = exitAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            framework: framework,
            workspaceEnvironment: workspaceEnvironment);

    [Fact]
    public async Task The_projects_variables_reach_the_child_and_never_displace_what_the_studio_sets()
    {
        // Backlog 154: a variable a person wrote for the project is in the child's environment, asked for by the
        // workspace the session runs in; the credential the studio hands out is set after it and wins.
        var credentials = new StubCredentials(new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test" });
        string? askedFor = null;
        var runner = Runner(credentials: credentials, workspaceEnvironment: workspaceId =>
        {
            askedFor = workspaceId;
            return new Dictionary<string, string>
            {
                ["MY_SERVICE_TOKEN"] = "tok-1",
                ["ANTHROPIC_API_KEY"] = "a-project-cannot-set-this",
            };
        });

        await DrainAsync(runner.RunAsync(Request()));

        askedFor.Should().Be("w1");
        var env = Report().Env;
        env["MY_SERVICE_TOKEN"].Should().Be("tok-1");
        env["ANTHROPIC_API_KEY"].Should().Be("sk-test", "the studio's credential is set after the project's variables");
    }

    [Fact]
    public async Task A_brokered_session_runs_a_choice_the_broker_does_not_cover_on_the_covered_model_and_says_so()
    {
        // Every role ends here, so a stored Opus choice (the autopilot builder's default) runs on
        // Haiku 5.5 through the free tier, and the transcript says which model ran and why. Never a silent swap.
        var broker = new BrokerOptions { Enabled = true, BaseUrl = "https://broker.example.test/fravashi/api" };
        var secrets = new AgentRuntimes.FakeSecretStore();
        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "minted-bearer");
        var brokered = new AgentRuntimeCredentialStore(secrets, broker);

        var events = await DrainAsync(Runner(credentials: brokered).RunAsync(Request(model: "claude-opus-5-5")));

        var report = Report();
        report.After("--model").Should().Be("claude-haiku-5-5");
        report.Env["ANTHROPIC_MODEL"].Should().Be("claude-haiku-5-5");
        events.OfType<MessageEmitted>().Should().Contain(m =>
            m.Role == TranscriptRole.System && m.Text.Contains("claude-opus-5-5") && m.Text.Contains("free tier"));
    }

    private AgentSessionRequest Request(
        string? model = null,
        string? resume = null,
        string prompt = "hello",
        IReadOnlyList<AgentTurnDocumentKind>? kinds = null) =>
        new(AgentSessionId.New(), "w1", _workspaceCopy, prompt, resume, model, kinds);

    /// <summary>
    /// Work item #323, extended by #333: a brand new session's opening turn carries the studio's note (the git
    /// fact, then the work gate's steps and the commit reviewer's failure classes) ahead of the user's own
    /// words, so the fake CLI's echo (which joins every content block's text with no separator) echoes the
    /// note immediately before whatever the test actually sent on that first turn.
    /// </summary>
    private static string FirstTurnEcho(string text) =>
        "echo: " + AgentGitInBash.OpeningNote + " " + AgentFirstTurnBriefing.Note + text;

    private CliReport Report()
    {
        File.Exists(_reportPath).Should().BeTrue("the CLI should have been started");
        using var document = JsonDocument.Parse(File.ReadAllText(_reportPath));
        var root = document.RootElement;
        return new CliReport(
            [.. root.GetProperty("args").EnumerateArray().Select(a => a.GetString()!)],
            root.GetProperty("cwd").GetString()!,
            root.GetProperty("path").GetString()!,
            root.GetProperty("anthropicApiKey").GetString(),
            root.GetProperty("settingsExists").GetBoolean(),
            root.GetProperty("settings").GetString(),
            root.GetProperty("env").EnumerateObject().ToDictionary(
                p => p.Name,
                p => p.Value.GetString() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase));
    }

    private sealed record CliReport(
        string[] Args,
        string Cwd,
        string Path,
        string? ApiKey,
        bool SettingsExisted,
        string? Settings,
        IReadOnlyDictionary<string, string> Env)
    {
        public string? After(string flag)
        {
            var index = Array.IndexOf(Args, flag);
            return index >= 0 && index + 1 < Args.Length ? Args[index + 1] : null;
        }
    }

    private static async Task<List<AgentEvent>> DrainAsync(
        IAsyncEnumerable<AgentEvent> events,
        CancellationToken cancellationToken = default)
    {
        var collected = new List<AgentEvent>();
        await foreach (var e in events.WithCancellation(cancellationToken))
        {
            collected.Add(e);
        }

        return collected;
    }

    // ---- the command --------------------------------------------------------------------

    [Fact]
    public async Task The_cli_is_driven_over_its_structured_protocol_not_a_terminal()
    {
        await DrainAsync(Runner().RunAsync(Request()));

        var report = Report();
        report.Args.Should().Contain("-p");
        report.After("--input-format").Should().Be("stream-json");
        report.After("--output-format").Should().Be("stream-json");
        report.Args.Should().Contain("--verbose");
        report.Args.Should().Contain("--include-partial-messages");
        report.Args.Should().Contain("--include-hook-events");
    }

    [Fact]
    public async Task The_session_starts_under_the_id_the_studio_gave_it()
    {
        var request = Request();

        await DrainAsync(Runner().RunAsync(request));

        Report().After("--session-id").Should().Be(request.SessionId.Value.ToString("D"));
    }

    [Fact]
    public async Task A_resumed_session_continues_the_previous_conversation()
    {
        var events = await DrainAsync(Runner().RunAsync(Request(resume: "ea12436c-b6eb-455e-b670-687beea9c6d2")));

        var report = Report();
        report.After("--resume").Should().Be("ea12436c-b6eb-455e-b670-687beea9c6d2");
        report.Args.Should().NotContain("--session-id", "a resumed run keeps the id it is resuming");

        // Work item #323, extended by #333: the opening note (git fact, work gate steps, reviewer failure
        // classes) goes out on a brand new session's opening turn only; a resume keeps the same conversation
        // and carries none of it, whether or not it was ever told.
        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant).Select(m => m.Text)
            .Should().Equal("echo: hello");
    }

    [Fact]
    public async Task The_model_is_the_clis_own_default_unless_the_user_picked_one()
    {
        await DrainAsync(Runner().RunAsync(Request()));
        Report().Args.Should().NotContain("--model", "decision 7: the subscription's default");

        await DrainAsync(Runner().RunAsync(Request(model: "claude-haiku-4-5-20251001")));
        Report().After("--model").Should().Be("claude-haiku-4-5-20251001");
    }

    [Fact]
    public async Task The_agent_is_pointed_at_the_hook_settings_file_and_the_file_is_really_there()
    {
        await DrainAsync(Runner().RunAsync(Request()));

        var report = Report();
        report.SettingsExisted.Should().BeTrue();
        report.Settings.Should().Contain("PreToolUse");
    }

    // ---- the typed turn (ADR 0022) --------------------------------------------------------

    [Fact]
    public async Task A_session_the_studio_did_not_type_is_driven_exactly_as_before()
    {
        // The gate is off by default (see TypedTurnGateTests for why), and off has to mean the flag is
        // absent rather than empty: an empty --json-schema would be a schema nothing can satisfy.
        await DrainAsync(Runner().RunAsync(Request()));

        Report().Args.Should().NotContain("--json-schema");
    }

    [Fact]
    public async Task A_typed_session_hands_the_cli_the_schema_its_turns_must_end_in()
    {
        // ADR 0022's mechanism: the CLI turns this schema into a StructuredOutput tool the model must
        // call to finish, so the shape is enforced by the harness and not by a sentence in a prompt.
        await DrainAsync(Runner().RunAsync(Request(kinds: AgentTurnDocumentSchema.DefaultKinds)));

        Report().After("--json-schema").Should()
            .Be(AgentTurnDocumentSchema.For(AgentTurnDocumentSchema.DefaultKinds));
    }

    [Fact]
    public void The_schema_sent_carries_only_the_kinds_that_turn_could_produce()
    {
        // Decision 1 of the spec, and it is a cost decision: the schema is charged on EVERY turn, so a
        // runner shipping the whole library would tax every user for kinds nothing renders.
        var request = new AgentSessionRequest(
            AgentSessionId.New(), "w1", "/workspaces/w1", "hello", null, null, [AgentTurnDocumentKind.Answer]);

        var arguments = ClaudeSessionRunner.BuildArguments(request, "/tmp/settings.json");
        var schema = arguments[arguments.ToList().IndexOf("--json-schema") + 1];

        schema.Should().Contain("\"answer\"");
        schema.Should().NotContain("questions").And.NotContain("findings");
    }

    // ---- the per-session spend ceiling (backlog 62) ----------------------------------------

    [Fact]
    public void No_configured_ceiling_means_no_budget_flag_at_all()
    {
        // The absent flag is the CLI's own no-ceiling behaviour. A "0" here would read as a limit and
        // behave as a refusal of everything, and an empty value would be a parse error at launch, so
        // "unset" has to mean the flag is not on the command line.
        var arguments = ClaudeSessionRunner.BuildArguments(
            new AgentSessionRequest(AgentSessionId.New(), "w1", "/workspaces/w1", "hello"),
            "/tmp/settings.json");

        arguments.Should().NotContain("--max-budget-usd");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_ceiling_that_is_not_above_zero_is_not_a_ceiling(decimal budget)
    {
        // Zero and negatives arrive from a settings row someone typed into. Treating them as ceilings
        // would hand the CLI a budget it refuses every call against, which would read as the agent
        // being broken rather than as a limit being wrong.
        var arguments = ClaudeSessionRunner.BuildArguments(
            new AgentSessionRequest(
                AgentSessionId.New(), "w1", "/workspaces/w1", "hello", MaxBudgetUsd: budget),
            "/tmp/settings.json");

        arguments.Should().NotContain("--max-budget-usd");
    }

    [Theory]
    [InlineData("5", "5")]
    [InlineData("5.00", "5")]
    [InlineData("2.50", "2.5")]
    [InlineData("0.125", "0.125")]
    public void A_configured_ceiling_is_passed_as_the_clis_own_budget_flag(string configured, string expected)
    {
        // The value is written with a fixed pattern rather than the decimal's own scale, so what the CLI
        // receives does not depend on whether the settings row was typed as "5", "5.0" or "5.00", and it
        // is invariant because this host runs a Swedish culture where the default would emit "2,5".
        var budget = decimal.Parse(configured, System.Globalization.CultureInfo.InvariantCulture);
        var arguments = ClaudeSessionRunner.BuildArguments(
            new AgentSessionRequest(
                AgentSessionId.New(), "w1", "/workspaces/w1", "hello", MaxBudgetUsd: budget),
            "/tmp/settings.json");

        arguments.ToList().IndexOf("--max-budget-usd").Should().BeGreaterThan(-1);
        arguments[arguments.ToList().IndexOf("--max-budget-usd") + 1].Should().Be(expected);
    }

    [Fact]
    public async Task The_budget_flag_reaches_the_real_command_line_and_not_only_the_builder()
    {
        // BuildArguments is public and unit-testable, which is exactly why this exists: the flag has to
        // ride on the process the runner actually starts.
        await DrainAsync(Runner().RunAsync(Request() with { MaxBudgetUsd = 3.5m }));

        Report().After("--max-budget-usd").Should().Be("3.5");
    }

    // ---- containment --------------------------------------------------------------------

    [Fact]
    public async Task The_agent_runs_inside_the_workspace_copy()
    {
        // Plan 2d decision 2: an agent never sees a host path.
        await DrainAsync(Runner().RunAsync(Request()));

        Path.TrimEndingDirectorySeparator(Report().Cwd)
            .Should().Be(Path.TrimEndingDirectorySeparator(_workspaceCopy));
    }

    [Fact]
    public async Task The_approval_broker_is_told_which_copy_the_session_is_confined_to()
    {
        // Withholding the bridge credential from the child's environment is only half of containment:
        // the other half is the approval broker refusing to auto-allow a read that leaves the copy, and
        // it can only do that if the runner tells it where the copy is.
        var broker = Substitute.For<IAgentApprovalBroker>();
        broker.RegisterSession(Arg.Any<AgentSessionId>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>()).Returns("a-secret");

        await DrainAsync(Runner(broker: broker).RunAsync(Request()));

        // And which WORKSPACE it is (ADR 0028): the folder whose trust level the broker reads for this session.
        broker.Received().RegisterSession(Arg.Any<AgentSessionId>(), _workspaceCopy, Arg.Any<bool>(), Arg.Is<string?>(w => !string.IsNullOrWhiteSpace(w)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_broker_is_told_whether_this_sessions_turns_end_in_a_document(bool typed)
    {
        // ADR 0022's StructuredOutput call goes through the same "*" PreToolUse hook as everything else,
        // so the broker has to know the studio is the one that asked for it. The registration and the
        // --json-schema flag are read off ONE predicate, and this asserts they cannot drift: an untyped
        // session must NOT hand out that free pass, or a tool call the CLI never built would be waved
        // through on its name alone.
        var broker = Substitute.For<IAgentApprovalBroker>();
        broker.RegisterSession(Arg.Any<AgentSessionId>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>()).Returns("a-secret");
        var kinds = typed ? AgentTurnDocumentSchema.DefaultKinds : null;

        await DrainAsync(Runner(broker: broker).RunAsync(Request(kinds: kinds)));

        broker.Received().RegisterSession(Arg.Any<AgentSessionId>(), _workspaceCopy, typed, Arg.Any<string?>());
        Report().Args.Contains("--json-schema").Should().Be(
            typed, "the flag and the registration are the same fact and must never disagree");
    }

    // ---- containment: the environment ----------------------------------------------------

    [Fact]
    public async Task The_agent_process_cannot_see_the_host_bridge_credential()
    {
        // Backlog 72's acceptance, against a real child: the studio holds a live credential for the
        // user's REAL folders, and a child started with UseShellExecute = false used to inherit it.
        var bridgeToken = "bridge-token-" + Guid.NewGuid().ToString("N");
        var windowToken = "window-token-" + Guid.NewGuid().ToString("N");
        var previous = Snapshot(BridgeUrlVar, BridgeTokenVar, WindowTokenVar);
        try
        {
            Environment.SetEnvironmentVariable(BridgeUrlVar, "http://172.23.160.1:9091");
            Environment.SetEnvironmentVariable(BridgeTokenVar, bridgeToken);
            Environment.SetEnvironmentVariable(WindowTokenVar, windowToken);
            Environment.GetEnvironmentVariable(BridgeTokenVar).Should().Be(bridgeToken, "the premise has to be real");

            await DrainAsync(Runner().RunAsync(Request()));

            var env = Report().Env;
            env.Should().NotContainKey(BridgeUrlVar);
            env.Should().NotContainKey(BridgeTokenVar);
            env.Should().NotContainKey(WindowTokenVar);
            env.Values.Should().NotContain(v => v.Contains(bridgeToken, StringComparison.Ordinal),
                "the token must not ride along under some other name");
            env.Values.Should().NotContain(v => v.Contains(windowToken, StringComparison.Ordinal));
        }
        finally
        {
            Restore(previous);
        }
    }

    [Fact]
    public async Task The_agent_process_still_gets_what_it_needs_to_run()
    {
        // The other direction: an allowlist that strips the CLI's own world would break every session.
        var credentials = new StubCredentials(new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test" });

        await DrainAsync(Runner(credentials: credentials).RunAsync(Request()));

        var env = Report().Env;
        env.Should().ContainKey("PATH");
        env["PATH"].Should().NotBeNullOrWhiteSpace();
        env["ANTHROPIC_API_KEY"].Should().Be("sk-test");
        (env.ContainsKey("HOME") || env.ContainsKey("USERPROFILE")).Should()
            .BeTrue("the CLI's credentials live under the home directory");
    }

    private const string BridgeUrlVar = "FRAVASHI_BRIDGE_URL";
    private const string BridgeTokenVar = "FRAVASHI_BRIDGE_TOKEN";
    private const string WindowTokenVar = "FRAVASHI_WINDOW_TOKEN";

    private static Dictionary<string, string?> Snapshot(params string[] names) =>
        names.ToDictionary(n => n, Environment.GetEnvironmentVariable, StringComparer.Ordinal);

    private static void Restore(Dictionary<string, string?> previous)
    {
        foreach (var (name, value) in previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task A_stored_headless_credential_is_injected_into_the_run()
    {
        var credentials = new StubCredentials(new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test" });

        await DrainAsync(Runner(credentials: credentials).RunAsync(Request()));

        Report().ApiKey.Should().Be("sk-test");
    }

    [Fact]
    public async Task A_project_node_version_provisioned_on_the_volume_leads_the_path()
    {
        var nvsBin = Path.Combine(_scratch, "nvs", "node", "22.12.0", "arm64", "bin");
        Directory.CreateDirectory(nvsBin);
        File.WriteAllText(Path.Combine(_workspaceCopy, ".nvmrc"), "22\n");
        var provisioner = new NodePathProvisioner(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NVS_HOME"] = Path.Combine(_scratch, "nvs") })
            .Build());

        await DrainAsync(Runner(nodePaths: provisioner).RunAsync(Request()));

        Report().Path.Should().StartWith(nvsBin);
    }

    // ---- the framework projection (backlog 69) -------------------------------------------

    [Fact]
    public async Task A_projected_framework_points_the_cli_at_the_per_session_config_home()
    {
        // Backlog 69: a stored skill must actually reach the CLI. The lever is CLAUDE_CONFIG_DIR, pointed at
        // the config home the projector wrote the SKILL.md into, on the data volume and never the copy.
        var store = new InMemoryFrameworkStore();
        store.Put(FrameworkArtifactKind.Skill, "haiku-writer", FrameworkPayload<SkillDefinition>.Serialize(
            new SkillDefinition { Name = "haiku-writer", Description = "Write a haiku.", Content = "Body." }));
        var framework = new SessionFrameworkProjector(_scratch, store);

        var request = Request();
        await DrainAsync(Runner(framework: framework).RunAsync(request));

        // The env the child was actually handed points at this session's config home. (The projected files
        // themselves are asserted by SessionFrameworkProjectorTests; here the session's dir is torn down on
        // end, so the env the fake CLI captured mid-run is the durable evidence.)
        var configHome = framework.ConfigDirectoryFor(request.SessionId);
        Report().Env.Should().ContainKey("CLAUDE_CONFIG_DIR");
        Path.TrimEndingDirectorySeparator(Report().Env["CLAUDE_CONFIG_DIR"])
            .Should().Be(Path.TrimEndingDirectorySeparator(configHome));
    }

    [Fact]
    public async Task An_empty_framework_leaves_the_config_home_alone()
    {
        // The conservative default: with nothing to project the child keeps the CLI's own config home, so a
        // subscription login is untouched and the run is byte-identical to one with no projector at all.
        var framework = new SessionFrameworkProjector(_scratch, new InMemoryFrameworkStore());
        var request = Request();

        await DrainAsync(Runner(framework: framework).RunAsync(request));

        // The runner must not have pointed the CLI at this session's config home. (An ambient CLAUDE_CONFIG_DIR
        // on the test host may still be inherited; what matters is that the projector did not impose one.)
        var sessionHome = framework.ConfigDirectoryFor(request.SessionId);
        if (Report().Env.TryGetValue("CLAUDE_CONFIG_DIR", out var value))
        {
            Path.TrimEndingDirectorySeparator(value)
                .Should().NotBe(Path.TrimEndingDirectorySeparator(sessionHome));
        }
    }

    // ---- the session's own saved tool output (backlog 338) --------------------------------

    [Fact]
    public async Task A_fresh_sessions_own_tool_results_root_is_set_once_the_cli_announces_its_id()
    {
        // A fresh start does not know the CLI's own conversation id until the first stream line
        // announces it (SessionRegistered), which is why RegisterSession at Start() cannot carry the
        // root: PumpChildrenAsync has to set it reactively (SetToolResultsRoot), and this proves the
        // broker really answers a read under it unattended the moment that happens, not just that a
        // string was computed correctly.
        var store = new InMemoryFrameworkStore();
        store.Put(FrameworkArtifactKind.Skill, "haiku-writer", FrameworkPayload<SkillDefinition>.Serialize(
            new SkillDefinition { Name = "haiku-writer", Description = "Write a haiku.", Content = "Body." }));
        var framework = new SessionFrameworkProjector(_scratch, store);
        var request = Request();

        ToolApprovalDecision? decision = null;
        IReadOnlyList<PendingToolApproval>? pending = null;

        await foreach (var e in Runner(framework: framework).RunAsync(request))
        {
            if (e is SessionRegistered registered)
            {
                var configHome = framework.ConfigDirectoryFor(request.SessionId);
                var expectedRoot = SessionFrameworkProjector.ToolResultsDirectoryFor(
                    configHome, _workspaceCopy, registered.ProviderSessionId);

                decision = await _broker.RequestAsync(
                    request.SessionId, "Read", "toolu_1",
                    AgentToolInput.Create(
                        new Dictionary<string, string> { ["file_path"] = Path.Combine(expectedRoot, "out.txt") },
                        ["file_path"]));
                pending = _broker.PendingFor(request.SessionId);
            }
        }

        decision.Should().NotBeNull("the fake CLI must have announced its session");
        decision!.Approved.Should().BeTrue(
            "a fresh session's own saved-tool-output root must be set the moment the CLI announces its id");
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resumed_sessions_own_tool_results_root_is_registered_up_front()
    {
        // The id being resumed is already known before the child so much as starts, so RegisterSession
        // at Start() must carry the root itself, never waiting on the reactive path a fresh session
        // needs. Asserted at SessionStarted, before a single byte of the child's own output is parsed.
        var store = new InMemoryFrameworkStore();
        store.Put(FrameworkArtifactKind.Skill, "haiku-writer", FrameworkPayload<SkillDefinition>.Serialize(
            new SkillDefinition { Name = "haiku-writer", Description = "Write a haiku.", Content = "Body." }));
        var framework = new SessionFrameworkProjector(_scratch, store);
        var request = Request(resume: "fake-cli-session-id");

        ToolApprovalDecision? decision = null;
        IReadOnlyList<PendingToolApproval>? pending = null;

        await foreach (var e in Runner(framework: framework).RunAsync(request))
        {
            if (e is SessionStarted)
            {
                var configHome = framework.ConfigDirectoryFor(request.SessionId);
                var expectedRoot = SessionFrameworkProjector.ToolResultsDirectoryFor(
                    configHome, _workspaceCopy, "fake-cli-session-id");

                decision = await _broker.RequestAsync(
                    request.SessionId, "Read", "toolu_1",
                    AgentToolInput.Create(
                        new Dictionary<string, string> { ["file_path"] = Path.Combine(expectedRoot, "out.txt") },
                        ["file_path"]));
                pending = _broker.PendingFor(request.SessionId);
            }
        }

        decision.Should().NotBeNull();
        decision!.Approved.Should().BeTrue(
            "a resumed session already knows the id it is resuming, so RegisterSession sets the root up front");
        pending.Should().BeEmpty();
    }

    // ---- the CLI's own transcript (backlog 116) ------------------------------------------

    [Fact]
    public async Task When_a_session_ends_the_clis_own_transcript_is_redacted_in_place_and_kept()
    {
        // The fake CLI keeps a JSONL transcript under its config home exactly as the real one does, holding
        // the prompt and the echo verbatim. After the session ends the file must still be there (Continue
        // resumes from it) and must no longer hold the key.
        const string fakeKey = "sk-" + "ant-api03-Fake0123456789Fake0123456789Fake0123456789FakeAA";
        var home = Path.Combine(_scratch, "home", ".claude");
        var runner = new ClaudeSessionRunner(
            _broker,
            Hooks(),
            launcher: new AgentCliLauncher("node", [FakeCliScript()]),
            environment: new Dictionary<string, string>
            {
                ["FAKE_CLI_REPORT"] = _reportPath,
                ["FAKE_CLI_EXIT_AFTER"] = "1",
                ["FAKE_CLI_CONFIG_HOME"] = home,
                // An ambient CLAUDE_CONFIG_DIR on the test host must not redirect the fake's transcript.
                ["CLAUDE_CONFIG_DIR"] = string.Empty,
            },
            transcripts: new CliTranscriptScrubber(_scratch, home));

        var events = await DrainAsync(runner.RunAsync(Request(prompt: "use " + fakeKey)));

        events.OfType<MessageEmitted>().Should().NotBeEmpty();
        var transcript = Path.Combine(home, "projects", "-fake-cwd", "fake-cli-session-id.jsonl");
        File.Exists(transcript).Should().BeTrue("the CLI's conversation file survives the session ending");
        var text = await File.ReadAllTextAsync(transcript);
        text.Should().NotContain("Fake0123456789");
        text.Should().Contain("[redacted:sk-ant-api-key]");
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var parsed = JsonDocument.Parse(line);
        }
    }

    // ---- the conversation ---------------------------------------------------------------

    [Fact]
    public async Task A_review_conversations_opening_turn_carries_no_note()
    {
        // Work item #323: a review conversation (BuiltInTools narrowed to AgentReviewTools.BuiltIn, backlog
        // 243) cannot commit at all, so telling it to use git_commit would name a tool it does not have.
        var request = Request(prompt: "read the diff") with { BuiltInTools = AgentReviewTools.BuiltIn };

        var events = await DrainAsync(Runner().RunAsync(request));

        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant).Select(m => m.Text)
            .Should().Equal("echo: read the diff");
    }

    /// <summary>
    /// Work item #352, layer one: a graph-only conversation's CLI is started with only the reading tools, so Edit, Write,
    /// NotebookEdit and Bash do not exist for it, even when the request names no BuiltInTools of its own. Its first turn
    /// is told what it can and cannot do instead of the git and commit notes, and it is held at the broker while it runs.
    /// </summary>
    [Fact]
    public async Task A_graph_only_sessions_command_line_carries_the_restricted_tool_list_and_its_own_briefing()
    {
        var request = Request(prompt: "write these up") with { GraphOnly = true };
        var heldWhileRunning = false;
        var events = new List<AgentEvent>();

        await foreach (var e in Runner().RunAsync(request))
        {
            events.Add(e);
            if (e is SessionRegistered)
            {
                heldWhileRunning = _broker.IsGraphOnly(request.SessionId);
            }
        }

        var report = Report();
        report.After("--tools").Should().Be("Read,Grep,Glob,ToolSearch");
        report.After("--tools").Should().Be(string.Join(',', AgentGraphOnlyTools.BuiltIn));
        heldWhileRunning.Should().BeTrue("the broker holds it graph-only from registration on");
        _broker.IsGraphOnly(request.SessionId).Should().BeFalse("the hold goes with the session's release");
        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant).Select(m => m.Text)
            .Should().Equal("echo: " + AgentFirstTurnBriefing.GraphOnlyNote + "write these up");
    }

    [Fact]
    public void An_ordinary_sessions_command_line_still_names_no_tool_list()
    {
        ClaudeSessionRunner.BuildArguments(Request(), "/s.json").Should().NotContain("--tools");
    }

    /// <summary>Work item #352: a model change is a `with` copy of the request, so the restarted child is graph-only too.</summary>
    [Fact]
    public async Task A_model_change_restarts_a_graph_only_session_still_graph_only()
    {
        var runner = Runner(exitAfter: 2);
        var request = Request(prompt: "first") with { GraphOnly = true };
        var events = new List<AgentEvent>();
        var heldAfterRestart = false;

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 1)
            {
                runner.ChangeModel(request.SessionId, "claude-opus-4-5").Should().BeTrue();
            }

            if (e is SessionRegistered && events.OfType<SessionRegistered>().Count() == 2)
            {
                heldAfterRestart = _broker.IsGraphOnly(request.SessionId);
                (await runner.SendAsync(request.SessionId, "second")).Should().BeTrue();
            }

            // The restarted child serves its own two turns before it exits.
            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 2)
            {
                (await runner.SendAsync(request.SessionId, "third")).Should().BeTrue();
            }
        }

        // The report is written by the child that started last: the restarted one.
        var report = Report();
        report.After("--model").Should().Be("claude-opus-4-5");
        report.After("--resume").Should().Be("fake-cli-session-id");
        report.After("--tools").Should().Be(string.Join(',', AgentGraphOnlyTools.BuiltIn));
        heldAfterRestart.Should().BeTrue();
    }

    [Fact]
    public async Task The_first_prompt_is_delivered_and_the_reply_streams_back()
    {
        var request = Request(prompt: "write the readme");

        var events = await DrainAsync(Runner().RunAsync(request));

        events.OfType<SessionStarted>().Single().SessionId.Should().Be(request.SessionId);
        events.OfType<SessionRegistered>().Single().ProviderSessionId.Should().Be("fake-cli-session-id");
        events.OfType<MessageEmitted>().Single().Text.Should().Be(FirstTurnEcho("write the readme"));
        events.OfType<TurnCompleted>().Should().ContainSingle();
        events.OfType<SessionEnded>().Single().FinalState.Should().Be(AgentSessionState.Completed);
        events.Last().Should().BeOfType<SessionEnded>();
    }

    /// <summary>
    /// Work item #333: a brand new session's first turn carries the work gate's steps and the commit
    /// reviewer's failure classes, read from the same shared source
    /// (<see cref="CommitReviewFailureClasses"/>) the reviewer's own prompt reads (asserted directly against
    /// that source in <c>CommitReviewTests</c>, so the two can never silently drift apart). Sent as stdin
    /// content only: the CLI's argument list is unaffected, which is what proves the command line did not
    /// change (ADR 0023's pinned contract needs no re-drive for this).
    /// </summary>
    [Fact]
    public async Task A_new_sessions_first_turn_carries_the_work_gates_steps_and_the_reviewers_failure_classes()
    {
        var request = Request(prompt: "do the work");

        var events = await DrainAsync(Runner().RunAsync(request));

        var said = events.OfType<MessageEmitted>().Single().Text;
        said.Should().Contain(AgentWorkGate.FirstTurnSteps);
        foreach (var shortName in CommitReviewFailureClasses.ShortNames)
        {
            said.Should().Contain(shortName);
        }

        // The briefing is stdin content only: none of its words reach the argument list the pinned CLI
        // contract (ADR 0023) records, so this needs no re-drive of that contract.
        var args = string.Join(' ', Report().Args);
        args.Should().NotContain("session_work_link").And.NotContain("wiki_write").And.NotContain("APPLIED");
    }

    [Fact]
    public async Task A_follow_up_turn_reaches_the_process_that_is_already_running()
    {
        // Decision 6 rests on this: one long-lived process, more turns over stdin.
        var runner = Runner(exitAfter: 2);
        var request = Request(prompt: "first");
        var events = new List<AgentEvent>();

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 1)
            {
                (await runner.SendAsync(request.SessionId, "second")).Should().BeTrue();
            }
        }

        events.OfType<MessageEmitted>().Select(m => m.Text)
            .Should().ContainInOrder(FirstTurnEcho("first"), "echo: second");
        events.OfType<SessionRegistered>().Should().ContainSingle("it is the same session throughout");
        events.OfType<SessionEnded>().Single().FinalState.Should().Be(AgentSessionState.Completed);
    }

    [Fact]
    public async Task Pasted_images_reach_the_process_as_image_blocks_on_the_first_turn_and_a_follow_up()
    {
        // Backlog 26. The fake echoes an image block as its media type and base64 length, so the test sees
        // the block arrive intact and ahead of the words, over the same stdin every turn uses.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9, 9 };
        var image = AgentImageAttachment.TryCreate(png, out _)!;
        var runner = Runner(exitAfter: 2);
        var request = Request(prompt: string.Empty) with { PromptImages = [image] };
        var events = new List<AgentEvent>();

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 1)
            {
                (await runner.SendAsync(request.SessionId, "and this?", [image, image])).Should().BeTrue();
            }
        }

        events.OfType<MessageEmitted>().Select(m => m.Text).Should().ContainInOrder(
            FirstTurnEcho("[image/png 16]"),
            "echo: [image/png 16][image/png 16]and this?");
    }

    // ---- changing the model mid-conversation ---------------------------------------------

    [Fact]
    public async Task Changing_the_model_restarts_the_cli_on_the_same_conversation()
    {
        // The measured mechanism (pinned-lineage CLI 2.1.223 in the container, 2026-08-08): restarting
        // with --resume <id> --model <other> serves the next turn on the new model with the history
        // intact and the same session id. Proved here end to end against a real child: the second
        // child is handed --resume with the id the first announced, the new model, and the SAME hook
        // settings file, and follow-up turns reach it over stdin as if nothing had happened.
        var runner = Runner(exitAfter: 2);
        var request = Request(prompt: "first");
        var events = new List<AgentEvent>();

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 1)
            {
                runner.ChangeModel(request.SessionId, "claude-opus-4-5").Should().BeTrue();
            }

            if (e is SessionRegistered && events.OfType<SessionRegistered>().Count() == 2)
            {
                // The restarted child has announced itself, so the swap is complete: talk to it.
                (await runner.SendAsync(request.SessionId, "second")).Should().BeTrue();
            }

            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 2)
            {
                (await runner.SendAsync(request.SessionId, "third")).Should().BeTrue();
            }
        }

        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant).Select(m => m.Text)
            .Should().ContainInOrder(FirstTurnEcho("first"), "echo: second", "echo: third");
        events.OfType<SessionEnded>().Single().FinalState.Should().Be(
            AgentSessionState.Completed, "a model switch is not an interruption");

        // The report is written by whichever child started last, so it proves the RESTARTED command.
        var report = Report();
        report.After("--resume").Should().Be("fake-cli-session-id");
        report.After("--model").Should().Be("claude-opus-4-5");
        report.Args.Should().NotContain("--session-id", "the restarted child resumes, it does not start over");
        report.SettingsExisted.Should().BeTrue("the hook files carry over unchanged");
        report.Settings.Should().Contain("PreToolUse");
        Path.TrimEndingDirectorySeparator(report.Cwd)
            .Should().Be(Path.TrimEndingDirectorySeparator(_workspaceCopy), "same contained copy, same session");
    }

    [Fact]
    public async Task The_transcript_records_the_model_switch_where_it_happened()
    {
        // The studio notice line (the CLI-drift banner's style): the restarted child's init only echoes
        // the flag back, so the marker is the honest record of when and what the user switched to.
        var runner = Runner(exitAfter: 2);
        var request = Request(prompt: "first");
        var events = new List<AgentEvent>();

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 1)
            {
                runner.ChangeModel(request.SessionId, "claude-opus-4-5").Should().BeTrue();
            }

            if (e is SessionRegistered && events.OfType<SessionRegistered>().Count() == 2)
            {
                (await runner.SendAsync(request.SessionId, "second")).Should().BeTrue();
            }

            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 2)
            {
                (await runner.SendAsync(request.SessionId, "third")).Should().BeTrue();
            }
        }

        var notice = events.OfType<MessageEmitted>().Single(m => m.Role == TranscriptRole.System);
        notice.Text.Should().Contain("claude-opus-4-5");
        events.IndexOf(notice).Should().BeGreaterThan(
            events.FindIndex(e => e is TurnCompleted), "the switch happened after the first turn");
        events.IndexOf(notice).Should().BeLessThan(
            events.FindIndex(e => e is MessageEmitted { Text: "echo: second" }),
            "and before anything the new child said");
    }

    [Fact]
    public async Task Refreshing_credentials_restarts_the_child_with_the_current_broker_token_on_the_same_model()
    {
        // Backlog 235: measured against the pinned 2.1.280, a running child keeps the bearer it started with, so the
        // renewed token reaches it only through a new process. Proved end to end against a real child: the restarted
        // one resumes the same conversation on the same model with the credential store's CURRENT token in its
        // environment, and a turn written straight after the request waits for it instead of being lost.
        var secrets = new AgentRuntimes.FakeSecretStore();
        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "tok-first");
        CoverEveryModel(secrets);
        var credentials = new AgentRuntimeCredentialStore(
            secrets, new BrokerOptions { Enabled = true, BaseUrl = "https://broker.example" });
        var runner = Runner(exitAfter: 2, credentials: credentials);
        var request = Request(prompt: "first", model: "claude-haiku-4-5");
        var events = new List<AgentEvent>();

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 1)
            {
                Report().Env["ANTHROPIC_AUTH_TOKEN"].Should().Be("tok-first");
                secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "tok-renewed");
                runner.RefreshCredentials(request.SessionId).Should().BeTrue();

                // Written at once: it waits for the new child rather than landing in the dying one.
                (await runner.SendAsync(request.SessionId, "second")).Should().BeTrue();
            }

            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 2)
            {
                (await runner.SendAsync(request.SessionId, "third")).Should().BeTrue();
            }
        }

        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant).Select(m => m.Text)
            .Should().ContainInOrder(FirstTurnEcho("first"), "echo: second", "echo: third");
        events.OfType<MessageEmitted>().Should().Contain(m =>
            m.Role == TranscriptRole.System && m.Text == ClaudeSessionRunner.CredentialRefreshNotice);
        events.OfType<SessionEnded>().Single().FinalState.Should().Be(AgentSessionState.Completed);

        var report = Report();
        report.Env["ANTHROPIC_AUTH_TOKEN"].Should().Be("tok-renewed");
        report.After("--resume").Should().Be("fake-cli-session-id");
        report.After("--model").Should().Be("claude-haiku-4-5", "a credential restart keeps the model");
    }

    // ---- effort (requested 2026-10-22, model and effort per work item) ---------------------

    [Fact]
    public async Task The_effort_is_passed_only_when_one_was_picked()
    {
        await DrainAsync(Runner().RunAsync(Request()));
        Report().Args.Should().NotContain("--effort", "Default passes no flag, so the recorded command line holds");

        await DrainAsync(Runner().RunAsync(Request() with { Effort = AgentEffort.Medium }));
        Report().After("--effort").Should().Be("medium");
    }

    [Fact]
    public async Task Changing_the_effort_restarts_the_cli_on_the_same_conversation_and_keeps_the_model()
    {
        var runner = Runner(exitAfter: 2);
        var request = Request(prompt: "first", model: "claude-haiku-4-5");
        var events = new List<AgentEvent>();

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 1)
            {
                runner.ChangeEffort(request.SessionId, AgentEffort.High).Should().BeTrue();
            }

            if (e is SessionRegistered && events.OfType<SessionRegistered>().Count() == 2)
            {
                (await runner.SendAsync(request.SessionId, "second")).Should().BeTrue();
            }

            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 2)
            {
                (await runner.SendAsync(request.SessionId, "third")).Should().BeTrue();
            }
        }

        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant).Select(m => m.Text)
            .Should().ContainInOrder(FirstTurnEcho("first"), "echo: second", "echo: third");
        events.OfType<MessageEmitted>().Should().ContainSingle(m => m.Role == TranscriptRole.System)
            .Which.Text.Should().Be(ClaudeSessionRunner.EffortChangeNotice(AgentEffort.High));

        var report = Report();
        report.After("--resume").Should().Be("fake-cli-session-id");
        report.After("--effort").Should().Be("high");
        report.After("--model").Should().Be("claude-haiku-4-5", "an effort change keeps the model");
    }

    [Fact]
    public async Task A_model_change_keeps_the_effort_and_a_credential_refresh_keeps_both()
    {
        var secrets = new AgentRuntimes.FakeSecretStore();
        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "tok-first");
        CoverEveryModel(secrets);
        var credentials = new AgentRuntimeCredentialStore(
            secrets, new BrokerOptions { Enabled = true, BaseUrl = "https://broker.example" });
        var runner = Runner(exitAfter: 2, credentials: credentials);
        var request = Request(prompt: "first", model: "claude-haiku-4-5") with { Effort = AgentEffort.Low };
        var events = new List<AgentEvent>();

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 1)
            {
                runner.ChangeModel(request.SessionId, "claude-opus-4-5").Should().BeTrue();
            }

            if (e is SessionRegistered && events.OfType<SessionRegistered>().Count() == 2)
            {
                var afterModel = Report();
                afterModel.After("--model").Should().Be("claude-opus-4-5");
                afterModel.After("--effort").Should().Be("low", "a model change keeps the effort");
                (await runner.SendAsync(request.SessionId, "second")).Should().BeTrue();
            }

            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 2)
            {
                secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "tok-renewed");
                runner.RefreshCredentials(request.SessionId).Should().BeTrue();
                (await runner.SendAsync(request.SessionId, "third")).Should().BeTrue();
            }

            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 3)
            {
                (await runner.SendAsync(request.SessionId, "fourth")).Should().BeTrue();
            }
        }

        var report = Report();
        report.Env["ANTHROPIC_AUTH_TOKEN"].Should().Be("tok-renewed");
        report.After("--model").Should().Be("claude-opus-4-5", "a credential restart keeps the model");
        report.After("--effort").Should().Be("low", "and the effort");
    }

    [Fact]
    public async Task A_model_change_needs_a_live_child_that_has_announced_its_conversation()
    {
        // No such session at all.
        Runner().ChangeModel(AgentSessionId.New(), "claude-opus-4-5").Should().BeFalse();

        // A running child that has not spoken yet: there is no conversation id a restart could resume,
        // so the change is refused rather than killing the child and losing the session.
        var runner = Runner(mode: "hang");
        var request = Request();
        var events = new List<AgentEvent>();

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is SessionStarted)
            {
                await WaitUntilTheProcessIsAliveAsync();
                runner.ChangeModel(request.SessionId, "claude-opus-4-5").Should().BeFalse();
                runner.Stop(request.SessionId).Should().BeTrue();
            }
        }

        events.OfType<SessionEnded>().Single().FinalState.Should().Be(AgentSessionState.Cancelled);
    }

    [Fact]
    public async Task Usage_from_every_turn_is_reported()
    {
        var events = await DrainAsync(Runner().RunAsync(Request()));

        var stats = events.OfType<StatsUpdated>().Last().Stats;
        stats.OutputTokens.Should().Be(7);
        stats.CostUsd.Should().Be(0.01m);
    }

    // ---- the CLI installed at the first start ----------------------------------------------

    [Fact]
    public async Task A_conversation_started_while_the_first_install_runs_waits_for_it_and_says_so()
    {
        // The image does not carry the CLI (decided 2026-10-29): update-on-launch installs it at the first start. A
        // conversation started before it lands waits for the pass to end instead of failing at once.
        var report = new InMemoryAgentRuntimeStatusReport();
        report.BeginRun();
        var missing = Path.Combine(Path.GetTempPath(), "no-cli-" + Guid.NewGuid().ToString("N"), "claude");
        var runner = new ClaudeSessionRunner(
            _broker, Hooks(), launcher: new AgentCliLauncher(missing, []), runtimeReport: report);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var drain = DrainAsync(runner.RunAsync(Request()));
        await Task.Delay(700);
        drain.IsCompleted.Should().BeFalse("the conversation waits while the install is running");
        report.EndRun();
        var events = await drain;

        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(600));
        events.OfType<MessageEmitted>().Should().Contain(m =>
            m.Role == TranscriptRole.System && m.Text.StartsWith("The agent CLI was being installed", StringComparison.Ordinal));
        // This fake install put nothing there, so the start then fails as it always has, with its own reason.
        events.OfType<SessionEnded>().Single().FinalState.Should().Be(AgentSessionState.Failed);
    }

    [Fact]
    public async Task Nothing_waits_when_the_cli_is_already_there()
    {
        // The pass also runs at every later start; a conversation then starts on the CLI already installed.
        var report = new InMemoryAgentRuntimeStatusReport();
        report.BeginRun();
        var runner = new ClaudeSessionRunner(
            _broker,
            Hooks(),
            launcher: new AgentCliLauncher("node", [FakeCliScript()]),
            environment: new Dictionary<string, string>
            {
                ["FAKE_CLI_REPORT"] = _reportPath,
                ["FAKE_CLI_MODE"] = "reply",
                ["FAKE_CLI_EXIT_AFTER"] = "1",
            },
            runtimeReport: report);

        var events = await DrainAsync(runner.RunAsync(Request()));

        events.OfType<MessageEmitted>().Should().NotContain(m => m.Text.StartsWith("The agent CLI was being installed", StringComparison.Ordinal));
        events.OfType<SessionEnded>().Single().FinalState.Should().Be(AgentSessionState.Completed);
    }

    [Fact]
    public void The_cli_is_looked_for_on_path_or_at_its_rooted_name()
    {
        var dir = Directory.CreateTempSubdirectory("cli-path-").FullName;
        try
        {
            var name = OperatingSystem.IsWindows() ? "fakecli.cmd" : "fakecli";
            File.WriteAllText(Path.Combine(dir, name), "x");

            ClaudeSessionRunner.CliOnPath("fakecli", dir).Should().BeTrue();
            ClaudeSessionRunner.CliOnPath("fakecli", Path.GetTempPath()).Should().BeFalse();
            ClaudeSessionRunner.CliOnPath("fakecli", null).Should().BeFalse();
            ClaudeSessionRunner.CliOnPath(Path.Combine(dir, name), null).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- the pinned CLI -------------------------------------------------------------------

    /// <summary>
    /// Same wiring as <see cref="Runner"/>, plus a status report seeded with what the last update-on-launch
    /// pass observed. Deliberately NOT folded into the shared helper: the other tests count events.
    /// </summary>
    private ClaudeSessionRunner RunnerReporting(string? installedVersion) =>
        new(
            _broker,
            Hooks(),
            launcher: new AgentCliLauncher("node", [FakeCliScript()]),
            environment: new Dictionary<string, string>
            {
                ["FAKE_CLI_REPORT"] = _reportPath,
                ["FAKE_CLI_MODE"] = "reply",
                ["FAKE_CLI_EXIT_AFTER"] = "1",
            },
            runtimeReport: SeededReport(installedVersion));

    private static IAgentRuntimeStatusReport SeededReport(string? installedVersion)
    {
        var report = new InMemoryAgentRuntimeStatusReport();
        report.Record(new AgentRuntimeUpdateResult(
            AgentTool.Claude, AgentRuntimeUpdateOutcome.Succeeded, null, installedVersion, null, DateTimeOffset.UtcNow));
        return report;
    }

    [Fact]
    public async Task A_cli_that_is_not_the_pinned_version_warns_in_the_transcript_and_still_runs()
    {
        // Backlog 61: an unrecognised CLI degrades with an honest message, not a strange failure. The
        // warning has to arrive BEFORE the assistant speaks, or the user reads it after the damage.
        var claude = AgentRuntimeCatalog.For(AgentTool.Claude);

        var events = await DrainAsync(RunnerReporting("9.9.9").RunAsync(Request()));

        var warning = events.OfType<MessageEmitted>().First();
        warning.Role.Should().Be(TranscriptRole.System);
        warning.Text.Should().Contain("9.9.9").And.Contain(claude.ProtocolVerifiedVersion!);
        events.IndexOf(warning).Should().BeLessThan(
            events.FindIndex(e => e is MessageEmitted { Role: TranscriptRole.Assistant }));
        events.OfType<SessionEnded>().Single().FinalState.Should().Be(AgentSessionState.Completed);
    }

    [Fact]
    public void The_warning_names_the_version_the_protocol_was_driven_against_not_the_pin()
    {
        // ADR 0023's honesty defect, as a unit: the sentence used to interpolate the PIN behind the word
        // "verified", so bumping the pin silently promoted an unverified build to a verified one in the
        // user's transcript. A runtime the studio never drove over a protocol must not get that sentence
        // at all, however confidently it is pinned.
        var verified = AgentRuntimeCatalog.For(AgentTool.Claude) with
        {
            PinnedVersion = "3.0.0",
            ProtocolVerifiedVersion = "2.9.9",
        };
        var unverified = verified with { ProtocolVerifiedVersion = null };

        var claimed = ClaudeSessionRunner.PinWarning(verified, "1.0.0");
        claimed.Should().Contain("verified its protocol against 2.9.9")
            .And.NotContain("3.0.0", "the pin is not what anyone drove the contract tests against");

        var honest = ClaudeSessionRunner.PinWarning(unverified, "1.0.0");
        honest.Should().Contain("pins 3.0.0")
            .And.NotContain("verified", "nothing was, so the transcript must not say it was");
    }

    [Fact]
    public void A_cli_at_the_pin_gets_no_warning_however_the_pin_was_reached()
    {
        var claude = AgentRuntimeCatalog.For(AgentTool.Claude);

        ClaudeSessionRunner.PinWarning(claude, claude.PinnedVersion).Should().BeNull();
        ClaudeSessionRunner.PinWarning(claude, null).Should().BeNull();
    }

    [Fact]
    public async Task A_cli_at_the_pinned_version_says_nothing()
    {
        // The mirror case is what stops the warning becoming permanent noise.
        var pinned = AgentRuntimeCatalog.For(AgentTool.Claude).PinnedVersion;

        var events = await DrainAsync(RunnerReporting(pinned).RunAsync(Request()));

        events.OfType<MessageEmitted>().Should().OnlyContain(m => m.Role == TranscriptRole.Assistant);
    }

    [Fact]
    public async Task An_unknown_installed_version_is_not_treated_as_drift()
    {
        // A developer host and the window between startup and the first probe both look like this.
        var events = await DrainAsync(RunnerReporting(null).RunAsync(Request()));

        events.OfType<MessageEmitted>().Should().OnlyContain(m => m.Role == TranscriptRole.Assistant);
    }

    [Fact]
    public async Task Sending_to_a_session_that_is_not_running_is_refused()
    {
        (await Runner().SendAsync(AgentSessionId.New(), "anyone there?")).Should().BeFalse();
        Runner().Stop(AgentSessionId.New()).Should().BeFalse();
    }

    // ---- stopping -----------------------------------------------------------------------

    [Fact]
    public async Task Stopping_a_running_session_kills_the_process_and_ends_it_as_cancelled()
    {
        var runner = Runner(mode: "hang");
        var request = Request();
        var events = new List<AgentEvent>();

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is SessionStarted)
            {
                await WaitUntilTheProcessIsAliveAsync();
                runner.Stop(request.SessionId).Should().BeTrue();
            }
        }

        events.OfType<SessionEnded>().Single().FinalState.Should().Be(AgentSessionState.Cancelled);
        await AssertTheProcessIsGoneAsync();
    }

    [Fact]
    public async Task Stopping_a_typed_turn_keeps_the_prose_the_agent_had_already_written()
    {
        // Under a schema the parser holds assistant prose for the terminal result line, because the
        // document that line carries supersedes it. Stop kills the process TREE, so that line never
        // arrives, and the prose used to die with the process: the transcript kept the user's turn and
        // the tool cards and showed nothing the agent said. The user pressed Stop, not Delete.
        var runner = Runner(mode: "prose");
        var request = Request(kinds: AgentTurnDocumentSchema.DefaultKinds);
        var events = new List<AgentEvent>();
        var stopped = false;

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is TurnActivity && !stopped)
            {
                stopped = runner.Stop(request.SessionId);
                stopped.Should().BeTrue();
            }
        }

        var message = events.OfType<MessageEmitted>()
            .Where(m => m.Role == TranscriptRole.Assistant).Should().ContainSingle().Subject;
        message.Text.Should().Be("three paragraphs the user should keep");
        events.IndexOf(message).Should().BeLessThan(
            events.FindIndex(e => e is SessionEnded), "it was said before the session ended");
        events.OfType<SessionEnded>().Single().FinalState.Should().Be(AgentSessionState.Cancelled);
    }

    [Fact]
    public async Task Cancelling_the_stream_kills_the_agent_and_leaves_nothing_behind()
    {
        // This is the case that caught a real leak: cancelling while the opening turn was still being
        // written left the CLI running and the session secret on disk.
        var runner = Runner(mode: "hang");
        var request = Request();
        using var cancellation = new CancellationTokenSource();

        var act = async () =>
        {
            await foreach (var e in runner.RunAsync(request, cancellation.Token))
            {
                if (e is SessionStarted)
                {
                    await WaitUntilTheProcessIsAliveAsync();
                    await cancellation.CancelAsync();
                }
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        await AssertTheProcessIsGoneAsync();
        runner.IsRunning(request.SessionId).Should().BeFalse();
        Directory.Exists(Path.Combine(_scratch, "agent-sessions", request.SessionId.ToString()))
            .Should().BeFalse("the session secret must not outlive the process");
    }

    [Fact]
    public async Task A_caller_that_fails_on_the_first_event_still_kills_the_agent_and_takes_its_secret_away()
    {
        // Adversarial review 2026-09-15: the first events were handed out before the teardown owned the child,
        // so a caller throwing while recording SessionStarted left the CLI running with its hook script on
        // disk, and with ids reused by an in-place resume that orphan would authenticate as the next process.
        var runner = Runner(mode: "hang");
        var request = Request();
        string? secret = null;

        var act = async () =>
        {
            await foreach (var e in runner.RunAsync(request))
            {
                if (e is SessionStarted)
                {
                    await WaitUntilTheProcessIsAliveAsync();
                    secret = ReadSecretFromHookScript(request.SessionId);
                    throw new InvalidOperationException("the transcript could not be written");
                }
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
        await AssertTheProcessIsGoneAsync();
        runner.IsRunning(request.SessionId).Should().BeFalse();
        _broker.ResolveSecret(secret).Should().BeNull("the secret dies with the process");
        Directory.Exists(Path.Combine(_scratch, "agent-sessions", request.SessionId.ToString()))
            .Should().BeFalse("the hook script must not outlive the process");
    }

    /// <summary>Waits until the hanging stand-in has proved it is running, before a test kills it.</summary>
    private async Task WaitUntilTheProcessIsAliveAsync()
    {
        var heartbeat = _reportPath + ".alive";
        for (var attempt = 0; attempt < 100 && !File.Exists(heartbeat); attempt++)
        {
            await Task.Delay(50); // poll interval inside a wait helper, not an arm-window guess (backlog 107)
        }

        File.Exists(heartbeat).Should().BeTrue("the stand-in should be running by now");
    }

    /// <summary>
    /// The hanging stand-in stamps a heartbeat file every 50ms. If it stops advancing, the process is
    /// really dead: a bookkeeping flag would prove nothing. Called the moment RunAsync has returned, so
    /// the OS is asked first: the teardown must have WAITED for the child before it took the secret away
    /// (backlog 203). A kill that only sent the signal left the child running past the teardown, which is
    /// how the heartbeat was once seen truncated after the runner had returned.
    /// </summary>
    private async Task AssertTheProcessIsGoneAsync()
    {
        var pid = int.Parse(File.ReadAllText(_reportPath + ".pid"), CultureInfo.InvariantCulture);
        IsStillRunning(pid).Should().BeFalse("the teardown waits for the agent to be gone before it returns");

        var heartbeat = _reportPath + ".alive";
        var before = ReadHeartbeat(heartbeat);
        await Task.Delay(400);

        ReadHeartbeat(heartbeat).Should().Be(before, "nothing should still be writing this file");
    }

    /// <summary>
    /// Whether the OS still has a process by this id that has not exited. A child the runner killed but did
    /// not wait for is still there (alive, or not yet reaped) when the teardown returns.
    /// </summary>
    private static bool IsStillRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string ReadHeartbeat(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(25); // the stand-in may be mid-write
            }
        }
    }

    // ---- failing honestly ---------------------------------------------------------------

    [Fact]
    public async Task A_cli_that_will_not_start_ends_the_session_with_the_reason()
    {
        var events = await DrainAsync(Runner(missingExecutable: true).RunAsync(Request()));

        var ended = events.OfType<SessionEnded>().Single();
        ended.FinalState.Should().Be(AgentSessionState.Failed);
        ended.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    // ---- a turn that exhausted its validation attempts (backlog 25 increment 6) ---------------

    [Fact]
    public async Task A_turn_that_exhausts_its_attempts_ends_the_process_but_leaves_the_conversation_resumable()
    {
        // ADR 0022 outcome iii: the CLI ends the turn in error and EXITS 1. One process per conversation
        // used to make that the conversation failing; it is the turn failing, and the id --resume needs is
        // already known.
        var events = await DrainAsync(Runner(mode: "exhaust").RunAsync(Request(kinds: AgentTurnDocumentSchema.DefaultKinds)));

        events.OfType<TurnCompleted>().Single().TerminalReason.Should().Be(AgentTurnEnding.RetriesExhausted);
        events.OfType<SessionRegistered>().Single().ProviderSessionId.Should().Be("fake-cli-session-id");

        var ended = events.OfType<SessionEnded>().Single();
        ended.FinalState.Should().Be(AgentSessionState.Failed);
        ended.Resumable.Should().BeTrue();
        ended.FailureReason.Should().Be(ClaudeSessionRunner.ExhaustedEnding);
        events.Last().Should().BeOfType<SessionEnded>();
    }

    [Fact]
    public async Task Resuming_an_exhausted_conversation_starts_a_resume_process_that_waits_for_the_next_turn()
    {
        var first = await DrainAsync(Runner(mode: "exhaust").RunAsync(Request(kinds: AgentTurnDocumentSchema.DefaultKinds)));
        var provider = first.OfType<SessionRegistered>().Single().ProviderSessionId;
        File.Delete(_reportPath);

        // A Continue sends nothing: the resumed child must be started with --resume and then left to wait,
        // so the NEXT turn is the user's own, written through SendAsync like any follow-up.
        var runner = Runner(exitAfter: 1);
        var resumed = Request(resume: provider, prompt: string.Empty, kinds: AgentTurnDocumentSchema.DefaultKinds);
        var events = new List<AgentEvent>();
        var sent = false;
        await foreach (var e in runner.RunAsync(resumed))
        {
            events.Add(e);
            if (e is SessionRegistered && !sent)
            {
                sent = true;
                events.OfType<MessageEmitted>().Should().BeEmpty("a Continue must not put words in the user's mouth");
                (await runner.SendAsync(resumed.SessionId, "carry on")).Should().BeTrue();
            }
        }

        var report = Report();
        report.After("--resume").Should().Be("fake-cli-session-id");
        report.Args.Should().NotContain("--session-id");
        events.OfType<MessageEmitted>().Select(m => m.Text).Should().Equal("echo: carry on");
        events.OfType<SessionEnded>().Single().Resumable.Should().BeFalse("this one ended normally");
    }

    [Fact]
    public async Task An_ordinary_crash_is_not_offered_as_resumable()
    {
        var events = await DrainAsync(Runner(mode: "crash").RunAsync(Request(kinds: AgentTurnDocumentSchema.DefaultKinds)));

        events.OfType<SessionEnded>().Single().Resumable.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_sessions_first_event_records_whether_its_turns_were_asked_for_a_document(bool typed)
    {
        var events = await DrainAsync(Runner().RunAsync(Request(kinds: typed ? AgentTurnDocumentSchema.DefaultKinds : null)));

        events.OfType<SessionStarted>().Single().TypedTurns.Should().Be(typed);
    }

    [Fact]
    public async Task A_cli_that_exits_badly_reports_what_it_said_on_the_way_out()
    {
        var events = await DrainAsync(Runner(mode: "crash").RunAsync(Request()));

        var ended = events.OfType<SessionEnded>().Single();
        ended.FinalState.Should().Be(AgentSessionState.Failed);
        ended.FailureReason.Should().Contain("not logged in");
    }

    // ---- the approval channel's lifetime -------------------------------------------------

    [Fact]
    public async Task The_sessions_secret_and_hook_files_are_taken_away_when_it_ends()
    {
        var request = Request();

        await DrainAsync(Runner().RunAsync(request));

        var hookDirectory = Path.Combine(_scratch, "agent-sessions", request.SessionId.ToString());
        Directory.Exists(hookDirectory).Should().BeFalse("the session secret must not outlive the session");
        _broker.PendingFor(request.SessionId).Should().BeEmpty();
    }

    [Fact]
    public async Task A_tool_call_from_the_agent_can_be_approved_while_the_session_runs()
    {
        // The hook posts to the endpoint with the secret the runner registered; here the endpoint is
        // called directly with that secret, which is the same path the script takes over HTTP.
        var runner = Runner();
        var request = Request();
        string? secret = null;
        var events = new List<AgentEvent>();

        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is SessionStarted)
            {
                secret = ReadSecretFromHookScript(request.SessionId);
                _broker.ResolveSecret(secret).Should().Be(request.SessionId);
            }
        }

        secret.Should().NotBeNullOrWhiteSpace();
        _broker.ResolveSecret(secret).Should().BeNull("the secret dies with the session");
    }

    private string ReadSecretFromHookScript(AgentSessionId sessionId)
    {
        var script = File.ReadAllText(
            Path.Combine(_scratch, "agent-sessions", sessionId.ToString(), "approve.sh"));
        var marker = AgentApprovalEndpoint.SecretHeader + ": ";
        var start = script.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = script.IndexOf('\'', start);
        return script[start..end];
    }

    /// <summary>A broker token that covers every Claude model, for tests about the broker's credential and not its
    /// free tier: without it the runner would run each model on the free tier's one model.</summary>
    private static void CoverEveryModel(AgentRuntimes.FakeSecretStore secrets) =>
        secrets.Set(BrokerReferences.TokenMetadataSecretRef, new BrokerTokenMetadata(
            "oidc:subject", "tok", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1))
        {
            AllowedModels = ["claude-*"],
        }.Serialize());

    private sealed class StubCredentials(IReadOnlyDictionary<string, string> environment) : IAgentRuntimeCredentialStore
    {
        public void SaveApiKey(AgentTool tool, string apiKey) => throw new NotSupportedException();

        public void ClearApiKey(AgentTool tool) => throw new NotSupportedException();

        public bool HasApiKey(AgentTool tool) => environment.Count > 0;

        public IReadOnlyDictionary<string, string> EnvironmentFor(AgentTool tool) => environment;
    }
}
