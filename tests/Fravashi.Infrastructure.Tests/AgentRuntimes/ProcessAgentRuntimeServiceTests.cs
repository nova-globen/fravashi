using Fravashi.Application.AgentRuntimes;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.AgentRuntimes;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

public class ProcessAgentRuntimeServiceTests
{
    private static ProcessAgentRuntimeService Build(FakeAgentCommandRunner runner, FakeSecretStore secrets) =>
        new(runner, new AgentRuntimeCredentialStore(secrets));

    private static AgentCommandResult Ok(string stdout) => new(true, 0, stdout, string.Empty, false);

    [Fact]
    public async Task Not_installed_when_the_version_command_will_not_start()
    {
        var runner = new FakeAgentCommandRunner { Handler = _ => AgentCommandResult.NotStarted("not found") };
        var service = Build(runner, new FakeSecretStore());

        var status = await service.GetStatusAsync(AgentTool.Claude);

        status.Installed.Should().BeFalse();
        status.AuthState.Should().Be(AgentRuntimeAuthState.NotInstalled);
    }

    [Fact]
    public async Task A_stored_key_is_a_real_proof_of_authentication()
    {
        // A stored key is proof for any runtime: the CLI's own status probe here answers nothing that means
        // signed in or signed out, and the key alone carries the verdict.
        var secrets = new FakeSecretStore();
        var credentials = new AgentRuntimeCredentialStore(secrets);
        credentials.SaveApiKey(AgentTool.Claude, "sk-ant-key");
        var runner = new FakeAgentCommandRunner { Handler = _ => Ok("1.0.70 (Claude Code)") };

        var status = await Build(runner, secrets).GetStatusAsync(AgentTool.Claude);

        status.Installed.Should().BeTrue();
        status.Version.Should().Be("1.0.70");
        status.AuthState.Should().Be(AgentRuntimeAuthState.Authenticated);
    }

    [Fact]
    public async Task A_stored_subscription_token_is_a_real_proof_of_authentication()
    {
        // Backlog 183: injected into every child, so it authenticates them, whatever the CLI's own file says.
        var secrets = new FakeSecretStore();
        new AgentRuntimeCredentialStore(secrets).SaveSubscriptionToken(AgentTool.Claude, "made-up-subscription-token");
        var runner = new FakeAgentCommandRunner
        {
            Handler = command => command.Arguments.Contains("status") ? Ok("{ \"loggedIn\": false }") : Ok("2.1.280 (Claude Code)"),
        };

        var status = await Build(runner, secrets).GetStatusAsync(AgentTool.Claude);

        status.AuthState.Should().Be(AgentRuntimeAuthState.Authenticated);
        status.AuthDetail.Should().Contain("subscription");
    }

    [Fact]
    public async Task Under_the_broker_a_left_over_key_is_not_called_what_authenticates()
    {
        // Backlog 108, third clause. Seen in an install: the broker carried every turn while the status line
        // went on saying "Using a stored API key", about a key nothing read.
        var secrets = new FakeSecretStore();
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(AgentTool.Claude), "sk-ant-left-over");
        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "minted-token");
        var broker = new BrokerOptions { Enabled = true, BaseUrl = "https://broker.example" };
        var runner = new FakeAgentCommandRunner { Handler = _ => Ok("2.1.222 (Claude Code)") };
        var service = new ProcessAgentRuntimeService(runner, new AgentRuntimeCredentialStore(secrets, broker));

        var status = await service.GetStatusAsync(AgentTool.Claude);

        status.AuthState.Should().Be(AgentRuntimeAuthState.Authenticated);
        status.AuthDetail.Should().Contain("broker").And.NotContain("stored");
    }

    [Fact]
    public async Task Under_the_broker_with_no_token_the_runtime_is_not_called_authenticated()
    {
        var secrets = new FakeSecretStore();
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(AgentTool.Claude), "sk-ant-left-over");
        var broker = new BrokerOptions { Enabled = true, BaseUrl = "https://broker.example" };
        var runner = new FakeAgentCommandRunner { Handler = _ => Ok("2.1.222 (Claude Code)") };
        var service = new ProcessAgentRuntimeService(runner, new AgentRuntimeCredentialStore(secrets, broker));

        var status = await service.GetStatusAsync(AgentTool.Claude);

        status.AuthState.Should().Be(AgentRuntimeAuthState.SignedOut);
        status.AuthDetail.Should().Contain("broker");
    }

    [Fact]
    public async Task Auth_status_command_marker_means_authenticated()
    {
        var runner = new FakeAgentCommandRunner
        {
            Handler = cmd => cmd.Arguments.Contains("status")
                ? Ok("{ \"loggedIn\": true, \"authMethod\": \"claudeai\" }")
                : Ok("2.1.211 (Claude Code)"),
        };

        var status = await Build(runner, new FakeSecretStore()).GetStatusAsync(AgentTool.Claude);

        status.AuthState.Should().Be(AgentRuntimeAuthState.Authenticated);
        status.Version.Should().Be("2.1.211");
    }

    [Fact]
    public async Task Auth_status_command_without_the_marker_means_signed_out()
    {
        var runner = new FakeAgentCommandRunner
        {
            Handler = cmd => cmd.Arguments.Contains("status")
                ? Ok("""{"loggedIn": false}""")
                : Ok("2.1.222 (Claude Code)"),
        };

        var status = await Build(runner, new FakeSecretStore()).GetStatusAsync(AgentTool.Claude);

        status.AuthState.Should().Be(AgentRuntimeAuthState.SignedOut);
    }

#if V2_CODEX_COPILOT
    // Parked with the runtime it needs (docs/v2.md section 1). Every runtime this version offers HAS a
    // scriptable status command, so nothing in the catalog reaches the honest-Unknown branch. The branch is
    // dormant in ProcessAgentRuntimeService rather than removed, and this is the coverage that comes back
    // with the first runtime that cannot answer for itself.
    [Fact]
    public async Task No_status_command_and_no_key_is_honestly_unknown()
    {
        var runner = new FakeAgentCommandRunner { Handler = _ => Ok("GitHub Copilot CLI 1.0.70.") };

        var status = await Build(runner, new FakeSecretStore()).GetStatusAsync(AgentTool.Copilot);

        status.AuthState.Should().Be(AgentRuntimeAuthState.Unknown);
    }
#endif

    [Fact]
    public async Task Update_succeeds_and_reports_the_new_version()
    {
        // Two version reads happen: one before the npm update, one after.
        var versions = new Queue<string>(["1.0.0", "2.0.0"]);
        var runner = new FakeAgentCommandRunner
        {
            Handler = cmd => cmd.FileName == "npm"
                ? Ok("updated")
                : Ok(versions.Count > 0 ? versions.Dequeue() : "2.0.0"),
        };

        var result = await Build(runner, new FakeSecretStore()).InstallOrUpdateAsync(AgentTool.Claude);

        result.Outcome.Should().Be(AgentRuntimeUpdateOutcome.Succeeded);
        result.FromVersion.Should().Be("1.0.0");
        result.ToVersion.Should().Be("2.0.0");
    }

    [Theory]
    [InlineData(AgentTool.Claude, "@anthropic-ai/claude-code")]
    public async Task Install_runs_the_pinned_specifier_never_latest(AgentTool tool, string package)
    {
        // Backlog 61 at the process boundary: it is not enough that the descriptor property is right, the
        // actual AgentCommand handed to npm has to carry the exact version.
        var runner = new FakeAgentCommandRunner { Handler = cmd => cmd.FileName == "npm" ? Ok("installed") : Ok("1.0.0") };
        var pinned = AgentRuntimeCatalog.For(tool).PinnedVersion;

        await Build(runner, new FakeSecretStore()).InstallOrUpdateAsync(tool);

        var install = runner.Calls.Should().ContainSingle(c => c.FileName == "npm").Subject;
        install.Arguments.Should().Equal(["install", "-g", $"{package}@{pinned}"]);
        string.Join(" ", install.Arguments).Should().NotContain("@latest");
    }

    [Fact]
    public async Task Update_failure_is_reported_not_thrown()
    {
        var runner = new FakeAgentCommandRunner
        {
            Handler = cmd => cmd.FileName == "npm"
                ? new AgentCommandResult(true, 1, string.Empty, "network error", false)
                : Ok("1.0.0"),
        };

        var result = await Build(runner, new FakeSecretStore()).InstallOrUpdateAsync(AgentTool.Claude);

        result.Outcome.Should().Be(AgentRuntimeUpdateOutcome.Failed);
        result.Message.Should().Contain("network error");
    }

    [Fact]
    public async Task Update_self_heals_stale_npm_directories_and_retries()
    {
        // ENOTEMPTY family: the global npm prefix lives on the persistent /data volume, so an
        // interrupted update leaves directories npm cannot rename over and EVERY later install of
        // that package fails. The service must clear the package's dirs and retry once.
        var npmRoot = Path.Combine(Path.GetTempPath(), "fravashi-npmroot-" + Guid.NewGuid().ToString("N"));
        var stalePackage = Path.Combine(npmRoot, "@anthropic-ai", "claude-code");
        var renameLeftover = Path.Combine(npmRoot, "@anthropic-ai", ".claude-code-abc123");
        Directory.CreateDirectory(stalePackage);
        Directory.CreateDirectory(renameLeftover);
        try
        {
            var installAttempts = 0;
            var runner = new FakeAgentCommandRunner
            {
                Handler = cmd =>
                    cmd.FileName != "npm" ? Ok("1.0.0")
                    : cmd.Arguments.Contains("root") ? Ok(npmRoot)
                    : ++installAttempts == 1
                        ? new AgentCommandResult(true, 1, string.Empty, "npm error code ENOTEMPTY", false)
                        : Ok("installed"),
            };

            var result = await Build(runner, new FakeSecretStore()).InstallOrUpdateAsync(AgentTool.Claude);

            result.Outcome.Should().Be(AgentRuntimeUpdateOutcome.Succeeded);
            installAttempts.Should().Be(2);
            Directory.Exists(stalePackage).Should().BeFalse();
            Directory.Exists(renameLeftover).Should().BeFalse();
        }
        finally
        {
            try
            {
                Directory.Delete(npmRoot, recursive: true);
            }
            catch (IOException)
            {
                // Temp cleanup is best effort.
            }
        }
    }

    [Fact]
    public async Task Update_reports_the_original_error_when_the_failure_is_not_stale_directories()
    {
        // A non-ENOTEMPTY failure must not trigger deletion or a retry.
        var installAttempts = 0;
        var runner = new FakeAgentCommandRunner
        {
            Handler = cmd =>
                cmd.FileName != "npm" ? Ok("1.0.0")
                : cmd.Arguments.Contains("root") ? Ok("/tmp/never-used")
                : (++installAttempts, new AgentCommandResult(true, 1, string.Empty, "network error", false)).Item2,
        };

        var result = await Build(runner, new FakeSecretStore()).InstallOrUpdateAsync(AgentTool.Claude);

        result.Outcome.Should().Be(AgentRuntimeUpdateOutcome.Failed);
        installAttempts.Should().Be(1);
    }

    [Theory]
    [InlineData("2.1.211 (Claude Code)", "2.1.211")]
    [InlineData("codex-cli 0.144.4", "0.144.4")]
    [InlineData("GitHub Copilot CLI 1.0.70.", "1.0.70")]
    [InlineData("22.23.1-1nodesource1", "22.23.1-1nodesource1")]
    public void ParseVersion_extracts_the_semver(string output, string expected) =>
        ProcessAgentRuntimeService.ParseVersion(output).Should().Be(expected);
}
