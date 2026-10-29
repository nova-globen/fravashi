using System.Diagnostics;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Infrastructure.AgentRuntimes;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

/// <summary>
/// Backlog 72. Both directions of the allowlist, because both can break something: withholding a
/// Fravashi credential is the point, and keeping what a CLI needs is what stops the fix breaking every
/// session.
/// </summary>
public sealed class AgentProcessEnvironmentTests
{
    [Theory]
    // The launcher injects every one of these into the studio container.
    [InlineData("FRAVASHI_BRIDGE_URL")]
    [InlineData("FRAVASHI_BRIDGE_TOKEN")]
    [InlineData("FRAVASHI_WINDOW_TOKEN")]
    [InlineData("FRAVASHI_INTEGRITY_KEY")]
    [InlineData("FRAVASHI_IMAGE_DIGEST")]
    [InlineData("FRAVASHI_DATA_DIR")]
    [InlineData("FRAVASHI_WORKSPACES_ROOT")]
    [InlineData("FRAVASHI_IN_CONTAINER")]
    [InlineData("FRAVASHI_APPRUNNER_URL")]
    [InlineData("FRAVASHI_APPRUNNER_TOKEN")]
    [InlineData("FRAVASHI_UPGRADE_URL")]
    [InlineData("FRAVASHI_UPGRADE_TOKEN")]
    [InlineData("Broker__BaseUrl")]
    [InlineData("Vpn__ControlServerUrl")]
    public void The_studios_own_credentials_are_not_on_the_list(string name) =>
        AgentProcessEnvironment.IsAllowed(name).Should().BeFalse();

    [Fact]
    public void A_name_nobody_has_thought_of_yet_is_denied() =>
        // The property that makes this survive the next secret someone adds to the container.
        AgentProcessEnvironment.IsAllowed("SOME_FUTURE_ORG_SECRET").Should().BeFalse();

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("NPM_CONFIG_PREFIX")]
    [InlineData("npm_config_registry")]
    [InlineData("TERM")]
    [InlineData("LANG")]
    [InlineData("LC_ALL")]
    [InlineData("TMPDIR")]
    [InlineData("SHELL")]
    [InlineData("USER")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("no_proxy")]
    [InlineData("SSL_CERT_FILE")]
    [InlineData("NODE_EXTRA_CA_CERTS")]
    [InlineData("ANTHROPIC_BASE_URL")]
    [InlineData("ANTHROPIC_MODEL")]
    [InlineData("CLAUDE_CONFIG_DIR")]
    [InlineData("DOTNET_CLI_TELEMETRY_OPTOUT")]
    [InlineData("DOTNET_NOLOGO")]
    [InlineData("DOTNET_SKIP_FIRST_TIME_EXPERIENCE")]
    public void What_the_cli_needs_survives(string name) =>
        AgentProcessEnvironment.IsAllowed(name).Should().BeTrue();

    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("anthropic_api_key")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("COPILOT_GITHUB_TOKEN")]
    public void A_provider_credential_is_never_inherited(string name) =>
        // Backlog 108, third clause. These names sit under prefixes that are allowed wholesale, so a key that
        // happened to be in the studio's own environment reached every agent child, broker or no broker. A
        // credential reaches a child only when the credential store puts it there on purpose, after Restrict.
        AgentProcessEnvironment.IsAllowed(name).Should().BeFalse();

    [Fact]
    public void Every_runtime_in_the_catalog_has_its_credential_names_withheld()
    {
        // Held by the catalog rather than by a list here, so a runtime added later cannot forget itself.
        foreach (var descriptor in AgentRuntimeCatalog.All)
        {
            AgentProcessEnvironment.IsAllowed(descriptor.ApiKeyEnvVar).Should().BeFalse(descriptor.ApiKeyEnvVar);
            if (descriptor.BrokerAuthTokenEnvVar is { } bearer)
            {
                AgentProcessEnvironment.IsAllowed(bearer).Should().BeFalse(bearer);
            }
        }
    }

    [Fact]
    public void An_inherited_key_is_taken_off_and_one_added_on_purpose_afterwards_stays()
    {
        var info = new ProcessStartInfo();
        info.Environment["ANTHROPIC_API_KEY"] = "inherited-from-the-studio";
        info.Environment["ANTHROPIC_BASE_URL"] = "https://broker.example";

        AgentProcessEnvironment.Restrict(info);
        info.Environment.Should().NotContainKey("ANTHROPIC_API_KEY");
        info.Environment["ANTHROPIC_BASE_URL"].Should().Be("https://broker.example");

        // What every runner does next: the store's own answer, which under the broker is a token and no key.
        info.Environment["ANTHROPIC_AUTH_TOKEN"] = "short-lived";
        info.Environment.Should().ContainKey("ANTHROPIC_AUTH_TOKEN");
    }

    [Theory]
    [InlineData("SystemRoot")]
    [InlineData("COMSPEC")]
    [InlineData("PATHEXT")]
    [InlineData("TEMP")]
    [InlineData("APPDATA")]
    [InlineData("LOCALAPPDATA")]
    [InlineData("USERPROFILE")]
    [InlineData("windir")]
    [InlineData("ProgramFiles(x86)")]
    public void A_windows_child_keeps_what_a_windows_child_needs(string name) =>
        // Guards a host `dotnet run` and this whole suite, which spawns real Windows children.
        AgentProcessEnvironment.IsAllowed(name).Should().BeTrue();

    [Fact]
    public void The_match_ignores_case_so_windows_spellings_survive()
    {
        AgentProcessEnvironment.IsAllowed("Path").Should().BeTrue();
        AgentProcessEnvironment.IsAllowed("Temp").Should().BeTrue();
        AgentProcessEnvironment.IsAllowed("SYSTEMROOT").Should().BeTrue();
    }

    [Fact]
    public void Restrict_takes_the_inherited_secrets_off_a_real_start_info()
    {
        var probe = "PROBE_" + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo();
        info.Environment["FRAVASHI_BRIDGE_TOKEN"] = "t";
        info.Environment["FRAVASHI_BRIDGE_URL"] = "http://172.23.160.1:9091";
        info.Environment[probe] = "x";
        info.Environment["PATH"] = "/usr/bin";

        AgentProcessEnvironment.Restrict(info);

        info.Environment.Should().NotContainKey("FRAVASHI_BRIDGE_TOKEN");
        info.Environment.Should().NotContainKey("FRAVASHI_BRIDGE_URL");
        info.Environment.Should().NotContainKey(probe);
        info.Environment["PATH"].Should().Be("/usr/bin");
    }

    /// <summary>
    /// Backlog 174: signed in with a person's subscription, the CLI loaded their claude.ai connectors (Gmail, Drive,
    /// Calendar) connected into every child. Every child is started with them off, whatever the studio's own
    /// environment said, and a project cannot hand its agents the switch to turn them back on.
    /// </summary>
    [Fact]
    public void Every_child_starts_with_the_accounts_claude_ai_connectors_off()
    {
        var info = new ProcessStartInfo();
        info.Environment[AgentProcessEnvironment.ClaudeAiConnectorsVariable] = "true";

        AgentProcessEnvironment.Restrict(info);

        info.Environment[AgentProcessEnvironment.ClaudeAiConnectorsVariable].Should().Be("false");
        Fravashi.Application.Projects.ProjectVariableNames.Refusal(AgentProcessEnvironment.ClaudeAiConnectorsVariable)
            .Should().NotBeNull("a project variable is applied after Restrict and could switch them back on");
    }

    /// <summary>
    /// Backlog 189: an agent's long build was moved to the background by the CLI and nothing in the studio can wait on
    /// a background task. Every child starts with background tasks off and a ten-minute Bash default, whatever the
    /// studio's own environment held (CLAUDE_ is inherited by prefix), and a project cannot set either back.
    /// </summary>
    [Fact]
    public void Every_child_keeps_a_long_command_in_the_foreground()
    {
        var info = new ProcessStartInfo();
        info.Environment[AgentProcessEnvironment.DisableBackgroundTasksVariable] = "0";
        info.Environment[AgentProcessEnvironment.BashDefaultTimeoutVariable] = "1000";

        AgentProcessEnvironment.Restrict(info);

        info.Environment[AgentProcessEnvironment.DisableBackgroundTasksVariable].Should().Be("1");
        info.Environment[AgentProcessEnvironment.BashDefaultTimeoutVariable].Should().Be("600000");
        foreach (var name in new[]
                 {
                     AgentProcessEnvironment.DisableBackgroundTasksVariable,
                     AgentProcessEnvironment.BashDefaultTimeoutVariable,
                     "BASH_MAX_TIMEOUT_MS",
                 })
        {
            Fravashi.Application.Projects.ProjectVariableNames.Refusal(name)
                .Should().NotBeNull($"a project variable is applied after Restrict and {name} would override the studio's");
        }
    }

    /// <summary>
    /// Backlog 293: a scenario run that builds the application came back to the agent as "The operation timed out" after
    /// the pinned CLI's 60 second default for an http MCP server, while it went on in the studio. This holds what the
    /// child is started with: the CLI's tool-call limit and its idle limit (which the first does not raise), both named as
    /// read from the pinned binary, set to a build, a start, one step and the design check, whatever the studio's own
    /// environment held, and a project cannot set either back. That the CLI then waits that long is the item's live proof,
    /// not this test's.
    /// </summary>
    [Fact]
    public void Every_child_waits_for_the_studios_longest_tool()
    {
        var info = new ProcessStartInfo();
        info.Environment[AgentProcessEnvironment.McpToolTimeoutVariable] = "1000";
        info.Environment[AgentProcessEnvironment.McpToolIdleTimeoutVariable] = "1000";

        AgentProcessEnvironment.Restrict(info);

        AgentProcessEnvironment.McpToolTimeoutVariable.Should().Be("MCP_TOOL_TIMEOUT", "the name read from the pinned binary");
        AgentProcessEnvironment.McpToolIdleTimeoutVariable.Should().Be("CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT");
        var expected = (long)(Fravashi.Infrastructure.AppRuns.AppRunnerClient.BuildWait
            + Fravashi.Infrastructure.AppRuns.AppRunnerClient.ReadyWait
            + Fravashi.Application.WorkItems.ScenarioRunner.StepTimeout
            + Fravashi.Application.Verification.DesignCheckRequest.DefaultBudget).TotalMilliseconds;
        foreach (var name in new[] { AgentProcessEnvironment.McpToolTimeoutVariable, AgentProcessEnvironment.McpToolIdleTimeoutVariable })
        {
            long.Parse(info.Environment[name]!, System.Globalization.CultureInfo.InvariantCulture)
                .Should().Be(expected, $"{name} covers a build, its start, a step and the design check");
            Fravashi.Application.Projects.ProjectVariableNames.Refusal(name)
                .Should().NotBeNull($"a project variable is applied after Restrict and {name} would override the studio's");
        }

        expected.Should().BeGreaterThan(60000, "the CLI's own default for an http server is a minute");
    }

    /// <summary>
    /// Backlog 192: the approval card describes Agent and Bash calls as the CLI treats them with background tasks
    /// OFF, naming the switch from the Domain. This holds that what the child is really started with is that same
    /// name set on, so the card and the environment cannot drift apart.
    /// </summary>
    [Fact]
    public void The_child_is_started_with_the_background_switch_the_approval_card_describes()
    {
        var info = new ProcessStartInfo();

        AgentProcessEnvironment.Restrict(info);

        AgentProcessEnvironment.DisableBackgroundTasksVariable
            .Should().Be(Fravashi.Domain.AgentSessions.AgentCliSwitches.DisableBackgroundTasksVariable);
        info.Environment.Should().ContainKey(Fravashi.Domain.AgentSessions.AgentCliSwitches.DisableBackgroundTasksVariable)
            .WhoseValue.Should().Be(Fravashi.Domain.AgentSessions.AgentCliSwitches.DisableBackgroundTasksValue)
            .And.Be("1", "1 is the value item 189 saw reach an agent's shell in v0.9.52");
        Fravashi.Domain.AgentSessions.AgentToolEffects
            .Of("Agent", new Dictionary<string, string>(StringComparer.Ordinal))
            .Should().ContainSingle()
            .Which.Notice.Should().Contain(Fravashi.Domain.AgentSessions.AgentCliSwitches.DisableBackgroundTasksVariable);
    }
}
