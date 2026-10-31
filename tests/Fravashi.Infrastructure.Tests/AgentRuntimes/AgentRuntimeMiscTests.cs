using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.AgentRuntimes;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

public class AgentRuntimeSelectionTests
{
    [Fact]
    public void Round_trips_the_selection_in_catalog_order_ignoring_unknown_entries()
    {
        var config = new FakeConfigurationStore();
        AgentRuntimeSelection.Write(config, [AgentTool.Claude]);

        AgentRuntimeSelection.Read(config).Should().Equal(AgentTool.Claude);
        AgentRuntimeSelection.IsSelected(config, AgentTool.Claude).Should().BeTrue();

        // Entries the CATALOG does not carry are dropped on read, whether they are nonsense or the name of a
        // runtime that was retired (docs/v2.md section 1). That is what makes a stored selection self-heal
        // rather than throw: AgentTool keeps its retired values so an old settings row still PARSES, and this
        // filter is what stops one reaching AgentRuntimeCatalog.For, which refuses them by design.
        config.Set(AgentRuntimeSelection.SettingKey, "Claude, nonsense, Copilot, Codex");
        AgentRuntimeSelection.Read(config).Should().Equal(AgentTool.Claude);
        AgentRuntimeSelection.IsSelected(config, AgentTool.Copilot).Should().BeFalse();
        AgentRuntimeSelection.IsSelected(config, AgentTool.Codex).Should().BeFalse();
    }

    [Fact]
    public void An_empty_setting_reads_as_no_selection()
    {
        AgentRuntimeSelection.Read(new FakeConfigurationStore()).Should().BeEmpty();
    }
}

/// <summary>
/// The agent CLIs run INSIDE the container, and the launcher publishes one port, the studio's. A sign-in that
/// ends with the identity provider redirecting the user's browser to a loopback callback therefore redirects the
/// HOST's browser to a port only the CONTAINER is listening on: the owner completed both sign-ins on 2026-09-20
/// and both landed on a connection-refused page. A device-code flow has no callback at all (the CLI polls), so
/// it is the only browser sign-in that can work here, and it has to be ASKED for: the one CLI that picks it by
/// itself in a headless environment did not recognise this container as one. Both flags were read from the
/// pinned versions' own help inside the container, and both flows were run there and seen to print their code.
/// </summary>
public class AgentRuntimeInteractiveLoginTests
{
#if V2_CODEX_COPILOT
    // Both runtimes this covers were retired to v2 (docs/v2.md section 1). The two flags were MEASURED from
    // the pinned binaries' own help inside the container, and the reason they are needed at all is in the
    // class comment above, so the case is parked rather than deleted.
    [Theory]
    [InlineData(AgentTool.Codex, "--device-auth")]
    [InlineData(AgentTool.Copilot, "--device-code")]
    public void A_browser_sign_in_that_runs_in_the_container_asks_for_the_device_code_flow(AgentTool tool, string flag)
    {
        var descriptor = AgentRuntimeCatalog.For(tool);

        descriptor.SupportsInteractiveLogin.Should().BeTrue();
        descriptor.InteractiveLoginArguments.Should().Equal("login", flag);
    }
#endif
}

public class AgentRuntimeCredentialStoreTests
{
    [Theory]
    [InlineData(AgentTool.Claude, "ANTHROPIC_API_KEY")]
    public void Stored_key_surfaces_as_the_runtimes_env_var(AgentTool tool, string envVar)
    {
        var store = new AgentRuntimeCredentialStore(new FakeSecretStore());
        store.HasApiKey(tool).Should().BeFalse();

        store.SaveApiKey(tool, "the-key");

        store.HasApiKey(tool).Should().BeTrue();
        store.EnvironmentFor(tool).Should().Contain(new KeyValuePair<string, string>(envVar, "the-key"));

        store.ClearApiKey(tool);
        store.HasApiKey(tool).Should().BeFalse();
        store.EnvironmentFor(tool).Should().BeEmpty();
    }

    [Fact]
    public void A_subscription_token_is_handed_over_under_its_own_variable_and_a_stored_key_still_comes_first()
    {
        // Backlog 183: one value from one place for every child, so no conversation holds a stale copy.
        var store = new AgentRuntimeCredentialStore(new FakeSecretStore());

        store.SaveSubscriptionToken(AgentTool.Claude, "  made-up-subscription-token  ");

        store.HasSubscriptionToken(AgentTool.Claude).Should().BeTrue();
        store.EnvironmentFor(AgentTool.Claude).Should().Equal(
            new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "made-up-subscription-token" });

        store.SaveApiKey(AgentTool.Claude, "made-up-key");
        store.EnvironmentFor(AgentTool.Claude).Should().Equal(
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "made-up-key" }, "a key a person stored is what they chose to pay with");

        store.ClearApiKey(AgentTool.Claude);
        store.ClearSubscriptionToken(AgentTool.Claude);
        store.EnvironmentFor(AgentTool.Claude).Should().BeEmpty();
    }

    [Fact]
    public void Default_mode_injects_the_org_key_and_never_a_broker_base_url()
    {
        var store = new AgentRuntimeCredentialStore(new FakeSecretStore(), BrokerOptions.Disabled);
        store.SaveApiKey(AgentTool.Claude, "sk-ant-ORGKEY");

        var env = store.EnvironmentFor(AgentTool.Claude);

        env.Should().ContainKey("ANTHROPIC_API_KEY").WhoseValue.Should().Be("sk-ant-ORGKEY");
        env.Should().NotContainKey("ANTHROPIC_BASE_URL");
        env.Should().NotContainKey("ANTHROPIC_AUTH_TOKEN");
    }
}

/// <summary>
/// The remote-broker client pathway (ADR 0010 / plan 04 B1): when brokering is active, a brokerable runtime is
/// pointed at the gateway with a per-user bearer and the org key is NEVER emitted; the default posture and
/// non-brokered runtimes are unchanged.
/// </summary>
public class AgentRuntimeCredentialStoreBrokerTests
{
    private static BrokerOptions ActiveBroker(string? tokenEnvVar = "FRAVASHI_BROKER_USER_TOKEN") => new()
    {
        Enabled = true,
        BaseUrl = "https://broker.example.internal",
        UserTokenEnvVar = tokenEnvVar,
    };

    [Fact]
    public void Broker_mode_points_claude_at_the_gateway_with_a_bearer_and_omits_the_org_key()
    {
        var secrets = new FakeSecretStore();
        // An org key IS stored - broker mode must still refuse to emit it.
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(AgentTool.Claude), "sk-ant-ORGKEY-do-not-leak");
        var store = new AgentRuntimeCredentialStore(
            secrets, ActiveBroker(), env => env == "FRAVASHI_BROKER_USER_TOKEN" ? "user-bearer-abc" : null);

        var env = store.EnvironmentFor(AgentTool.Claude);

        env.Should().ContainKey("ANTHROPIC_BASE_URL").WhoseValue.Should().Be("https://broker.example.internal");
        env.Should().ContainKey("ANTHROPIC_AUTH_TOKEN").WhoseValue.Should().Be("user-bearer-abc");
        env.Should().NotContainKey("ANTHROPIC_API_KEY");
        env.Values.Should().NotContain("sk-ant-ORGKEY-do-not-leak");
    }

    [Fact]
    public void Broker_mode_points_every_model_the_cli_picks_itself_at_the_model_the_token_covers()
    {
        // Infra D46: the side model, the aliases and the classifiers would otherwise ask for models the broker refuses.
        var secrets = new FakeSecretStore();
        secrets.Set(BrokerReferences.TokenMetadataSecretRef, new BrokerTokenMetadata(
            "oidc:subject", "tok", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1))
        {
            AllowedModels = ["claude-haiku-5-5*"],
        }.Serialize());
        var store = new AgentRuntimeCredentialStore(secrets, ActiveBroker(), _ => "user-bearer");

        var env = store.EnvironmentFor(AgentTool.Claude);

        foreach (var name in BrokerModelPolicy.CliModelVariables)
        {
            env.Should().ContainKey(name).WhoseValue.Should().Be("claude-haiku-5-5");
        }
    }

    [Fact]
    public void Broker_mode_runs_a_choice_the_token_does_not_cover_on_the_covered_model_and_says_so()
    {
        var store = new AgentRuntimeCredentialStore(new FakeSecretStore(), ActiveBroker(), _ => "user-bearer");

        var choice = store.ModelFor(AgentTool.Claude, "claude-opus-5-5");

        // No stored token metadata: an older broker that named no list, so the free tier's model is assumed.
        choice.Model.Should().Be("claude-haiku-5-5");
        choice.Notice.Should().Contain("claude-opus-5-5");
    }

    [Fact]
    public void The_free_tier_carries_a_runtime_until_the_person_stores_a_credential_of_their_own()
    {
        // a fresh install runs on the broker's free tier; a key or a subscription token is used instead.
        var secrets = new FakeSecretStore();
        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "minted-bearer");
        var freeTier = BrokerOptions.Disabled.WithFreeTier("https://example.test/fravashi/api");
        var store = new AgentRuntimeCredentialStore(secrets, freeTier);

        store.BrokerCarries(AgentTool.Claude).Should().BeTrue();
        store.EnvironmentFor(AgentTool.Claude)["ANTHROPIC_BASE_URL"].Should().Be("https://example.test/fravashi/api");
        store.UsesStoredKey(AgentTool.Claude).Should().BeTrue("the free tier never refuses the person's own key");

        store.SaveApiKey(AgentTool.Claude, "sk-ant-own-key");

        store.BrokerCarries(AgentTool.Claude).Should().BeFalse();
        var env = store.EnvironmentFor(AgentTool.Claude);
        env["ANTHROPIC_API_KEY"].Should().Be("sk-ant-own-key");
        env.Should().NotContainKey("ANTHROPIC_BASE_URL").And.NotContainKey("ANTHROPIC_AUTH_TOKEN");
        store.ModelFor(AgentTool.Claude, "claude-opus-5-5").Model.Should().Be("claude-opus-5-5", "the own key is not limited");

        store.ClearApiKey(AgentTool.Claude);
        store.SaveSubscriptionToken(AgentTool.Claude, "sub-token");
        store.BrokerCarries(AgentTool.Claude).Should().BeFalse("a subscription is the person's own credential too");
    }

    [Fact]
    public void A_broker_set_on_purpose_and_the_corp_broker_variant_are_not_the_free_tier()
    {
        var configured = ActiveBroker().WithFreeTier("https://example.test/fravashi/api");
        configured.BaseUrl.Should().Be("https://broker.example.internal");
        configured.OwnCredentialFirst.Should().BeFalse("a broker an install was pointed at carries the runtime whatever is stored");

        var corp = BrokerOptions.Disabled.ForVariant(brokerRequired: true).WithFreeTier("https://example.test/fravashi/api");
        corp.IsActive.Should().BeTrue();
        corp.OwnCredentialFirst.Should().BeFalse("the corp-broker variant forbids a local key");

        var off = BrokerOptions.Disabled with { FreeTierDisabled = true };
        off.WithFreeTier("https://example.test/fravashi/api").IsActive.Should().BeFalse("a free tier switched off stays off");
    }

    [Fact]
    public void Without_the_broker_every_choice_runs_as_asked_and_the_cli_picks_its_own_models()
    {
        var store = new AgentRuntimeCredentialStore(new FakeSecretStore(), BrokerOptions.Disabled);
        store.SaveApiKey(AgentTool.Claude, "sk-ant-own-key");

        store.ModelFor(AgentTool.Claude, "claude-opus-5-5").Should().Be(AgentModelChoice.AsAsked("claude-opus-5-5"));
        store.ModelFor(AgentTool.Claude, null).Model.Should().BeNull();
        store.EnvironmentFor(AgentTool.Claude).Keys.Should().NotContain(BrokerModelPolicy.CliModelVariables);
    }

    [Fact]
    public void A_runtime_the_broker_carries_does_not_use_a_stored_key_and_will_not_take_a_new_one()
    {
        // Backlog 108, third clause: the target state is NO provider key stored here at all. A key saved while
        // the broker carries the runtime would authenticate nothing and would be the one thing the broker
        // exists to remove, so it is refused rather than stored and ignored.
        var secrets = new FakeSecretStore();
        var store = new AgentRuntimeCredentialStore(secrets, ActiveBroker(), _ => "user-bearer");

        store.UsesStoredKey(AgentTool.Claude).Should().BeFalse();
        var save = () => store.SaveApiKey(AgentTool.Claude, "sk-ant-ORGKEY");

        save.Should().Throw<InvalidOperationException>().WithMessage("*broker*");
        secrets.Get(AgentRuntimeCredentialStore.SecretRef(AgentTool.Claude)).Should().BeNull();

        // Backlog 183: a subscription token likewise, by the same rule.
        var keep = () => store.SaveSubscriptionToken(AgentTool.Claude, "made-up-subscription-token");
        keep.Should().Throw<InvalidOperationException>().WithMessage("*broker*");
        secrets.Get(AgentRuntimeCredentialStore.SubscriptionTokenRef(AgentTool.Claude)).Should().BeNull();
    }

    [Fact]
    public void A_key_stored_before_the_broker_was_switched_on_can_still_be_seen_and_removed()
    {
        // Nothing is deleted behind the owner's back: the leftover is reported, and removing it is their act.
        var secrets = new FakeSecretStore();
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(AgentTool.Claude), "sk-ant-ORGKEY");
        var store = new AgentRuntimeCredentialStore(secrets, ActiveBroker(), _ => "user-bearer");

        store.HasApiKey(AgentTool.Claude).Should().BeTrue();
        store.UsesStoredKey(AgentTool.Claude).Should().BeFalse();

        store.ClearApiKey(AgentTool.Claude);
        store.HasApiKey(AgentTool.Claude).Should().BeFalse();
    }

#if V2_CODEX_COPILOT
    // Parked with the runtimes it needs (docs/v2.md section 1). Every runtime this version offers CAN be
    // brokered, so there is nothing left in the catalog that exercises the not-brokered branch. The branch
    // itself is still in AgentRuntimeCredentialStore and is dormant, not removed, which is why the case
    // is kept here rather than deleted: it is the coverage v2 gets back with the first such runtime.
    [Fact]
    public void A_runtime_the_broker_cannot_carry_still_uses_its_stored_key()
    {
        var store = new AgentRuntimeCredentialStore(new FakeSecretStore(), ActiveBroker(), _ => "user-bearer");

        store.UsesStoredKey(AgentTool.Codex).Should().BeTrue();
        store.SaveApiKey(AgentTool.Codex, "sk-openai");
        store.HasApiKey(AgentTool.Codex).Should().BeTrue();
    }
#endif

    [Fact]
    public void Where_the_local_key_is_forbidden_no_runtime_uses_or_takes_one()
    {
        var forbidden = new BrokerOptions { Enabled = true, LocalKeyForbidden = true };
        var store = new AgentRuntimeCredentialStore(new FakeSecretStore(), forbidden, _ => null);

        foreach (var tool in AgentRuntimeCatalog.Tools)
        {
            store.UsesStoredKey(tool).Should().BeFalse();
            var save = () => store.SaveApiKey(tool, "a-key-that-must-not-land");
            save.Should().Throw<InvalidOperationException>();
        }
    }

    [Fact]
    public void Without_a_broker_the_stored_key_is_what_authenticates() =>
        new AgentRuntimeCredentialStore(new FakeSecretStore()).UsesStoredKey(AgentTool.Claude).Should().BeTrue();

    [Fact]
    public void Broker_mode_prefers_the_minted_token_in_the_secret_store_over_the_env_var()
    {
        var secrets = new FakeSecretStore();
        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "minted-token");
        var store = new AgentRuntimeCredentialStore(
            secrets, ActiveBroker(), _ => "env-token");

        store.EnvironmentFor(AgentTool.Claude)["ANTHROPIC_AUTH_TOKEN"].Should().Be("minted-token");
    }

    [Fact]
    public void Broker_mode_with_no_token_fails_closed_and_never_falls_back_to_the_org_key()
    {
        var secrets = new FakeSecretStore();
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(AgentTool.Claude), "sk-ant-ORGKEY");
        var store = new AgentRuntimeCredentialStore(secrets, ActiveBroker(), _ => null);

        var env = store.EnvironmentFor(AgentTool.Claude);

        env.Should().BeEmpty();
        env.Should().NotContainKey("ANTHROPIC_API_KEY");
    }

    [Fact]
    public void An_enabled_broker_with_no_base_url_is_inert_and_the_org_key_still_flows()
    {
        var secrets = new FakeSecretStore();
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(AgentTool.Claude), "sk-ant-ORGKEY");
        var halfConfigured = new BrokerOptions { Enabled = true, BaseUrl = null, UserTokenEnvVar = "X" };

        var store = new AgentRuntimeCredentialStore(secrets, halfConfigured, _ => "token");

        var env = store.EnvironmentFor(AgentTool.Claude);
        env.Should().ContainKey("ANTHROPIC_API_KEY").WhoseValue.Should().Be("sk-ant-ORGKEY");
        env.Should().NotContainKey("ANTHROPIC_BASE_URL");
    }

#if V2_CODEX_COPILOT
    // Parked with the runtimes it needs (docs/v2.md section 1): no runtime this version offers has a null
    // broker pair, so nothing in the catalog reaches the not-brokered branch. The branch is dormant in
    // AgentRuntimeCredentialStore rather than gone, and this is the coverage that returns with it.
    [Theory]
    [InlineData(AgentTool.Codex, "OPENAI_API_KEY")]
    [InlineData(AgentTool.Copilot, "COPILOT_GITHUB_TOKEN")]
    public void Broker_mode_does_not_touch_non_brokered_runtimes_they_keep_their_org_key(AgentTool tool, string envVar)
    {
        var secrets = new FakeSecretStore();
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(tool), "the-key");
        var store = new AgentRuntimeCredentialStore(secrets, ActiveBroker(), _ => "user-bearer");

        var env = store.EnvironmentFor(tool);

        env.Should().ContainKey(envVar).WhoseValue.Should().Be("the-key");
        env.Should().NotContainKey("ANTHROPIC_BASE_URL");
        env.Should().NotContainKey("ANTHROPIC_AUTH_TOKEN");
    }
#endif

    [Fact]
    public void BrokerOptions_disabled_is_not_active_even_with_a_base_url()
    {
        new BrokerOptions { Enabled = false, BaseUrl = "https://broker" }.IsActive.Should().BeFalse();
        new BrokerOptions { Enabled = true, BaseUrl = "https://broker" }.IsActive.Should().BeTrue();
        BrokerOptions.Disabled.IsActive.Should().BeFalse();
    }

    // ---- B4: the corp-broker variant guarantee - the local org-key path is OFF (LocalKeyForbidden) ----

    /// <summary>The corp-broker composed options: brokering on + local-key forbidden, with a broker endpoint.</summary>
    private static BrokerOptions CorpBroker() =>
        new BrokerOptions { BaseUrl = "https://broker.corp.internal", UserTokenEnvVar = "FRAVASHI_BROKER_USER_TOKEN" }
            .ForVariant(brokerRequired: true);

    [Fact]
    public void Corp_broker_brokers_claude_with_a_bearer_and_never_the_org_key()
    {
        var secrets = new FakeSecretStore();
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(AgentTool.Claude), "sk-ant-ORGKEY-do-not-leak");
        var store = new AgentRuntimeCredentialStore(
            secrets, CorpBroker(), env => env == "FRAVASHI_BROKER_USER_TOKEN" ? "user-bearer" : null);

        var env = store.EnvironmentFor(AgentTool.Claude);

        env.Should().ContainKey("ANTHROPIC_BASE_URL");
        env.Should().ContainKey("ANTHROPIC_AUTH_TOKEN").WhoseValue.Should().Be("user-bearer");
        env.Should().NotContainKey("ANTHROPIC_API_KEY");
        env.Values.Should().NotContain("sk-ant-ORGKEY-do-not-leak");
    }

    [Fact]
    public void Corp_broker_with_no_token_fails_closed_and_never_emits_the_org_key()
    {
        var secrets = new FakeSecretStore();
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(AgentTool.Claude), "sk-ant-ORGKEY");
        var store = new AgentRuntimeCredentialStore(secrets, CorpBroker(), _ => null);

        store.EnvironmentFor(AgentTool.Claude).Should().BeEmpty();
    }

    [Fact]
    public void Corp_broker_with_a_misconfigured_broker_still_refuses_the_org_key_fail_closed()
    {
        // corp-broker image, but the deploy never set BaseUrl → broker inert. The local-key path is compiled OFF,
        // so the org key is STILL not emitted - the guarantee holds even under misconfiguration (never a fallback).
        var secrets = new FakeSecretStore();
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(AgentTool.Claude), "sk-ant-ORGKEY");
        var inert = new BrokerOptions { BaseUrl = null }.ForVariant(brokerRequired: true);
        var store = new AgentRuntimeCredentialStore(secrets, inert, _ => "token-ignored");

        var env = store.EnvironmentFor(AgentTool.Claude);

        env.Should().BeEmpty();
        env.Should().NotContainKey("ANTHROPIC_API_KEY");
    }

#if V2_CODEX_COPILOT
    // Parked with the runtimes it needs (docs/v2.md section 1): no runtime this version offers has a null
    // broker pair, so nothing in the catalog reaches the not-brokered branch. The branch is dormant in
    // AgentRuntimeCredentialStore rather than gone, and this is the coverage that returns with it.
    [Theory]
    [InlineData(AgentTool.Codex, "OPENAI_API_KEY")]
    [InlineData(AgentTool.Copilot, "COPILOT_GITHUB_TOKEN")]
    public void Corp_broker_does_not_inject_any_org_key_for_non_brokered_runtimes(AgentTool tool, string envVar)
    {
        // Codex/Copilot are not brokered in v1 (D3). Under corp-broker the local-key path is OFF for them too, so
        // they get NOTHING - honest: the tier does not silently inject an org key for a runtime it cannot broker.
        var secrets = new FakeSecretStore();
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(tool), "the-key");
        var store = new AgentRuntimeCredentialStore(secrets, CorpBroker(), _ => "bearer");

        var env = store.EnvironmentFor(tool);

        env.Should().BeEmpty();
        env.Should().NotContainKey(envVar);
    }
#endif

    [Fact]
    public void The_full_variant_control_still_injects_the_org_key_so_the_guarantee_is_a_real_difference()
    {
        // The mechanism seen to fail the other way (N1): off-variant, the org key IS injected. This makes the
        // corp-broker absence a genuine, mechanism-level difference rather than an untested claim.
        var secrets = new FakeSecretStore();
        secrets.Set(AgentRuntimeCredentialStore.SecretRef(AgentTool.Claude), "sk-ant-ORGKEY");
        var full = new BrokerOptions { BaseUrl = "https://broker.corp.internal" }.ForVariant(brokerRequired: false);
        var store = new AgentRuntimeCredentialStore(secrets, full, _ => null);

        store.EnvironmentFor(AgentTool.Claude)["ANTHROPIC_API_KEY"].Should().Be("sk-ant-ORGKEY");
    }
}

public class AgentRuntimeUpdateServiceTests
{
    [Fact]
    public async Task RunUpdatesAsync_records_a_result_for_every_runtime_and_ends_the_run()
    {
        var versions = new Queue<string>();
        var runner = new FakeAgentCommandRunner
        {
            Handler = cmd => cmd.FileName == "npm"
                ? new AgentCommandResult(true, 0, "ok", string.Empty, false)
                : new AgentCommandResult(true, 0, "1.0.0", string.Empty, false),
        };
        var service = new ProcessAgentRuntimeService(runner, new AgentRuntimeCredentialStore(new FakeSecretStore()));
        var report = new InMemoryAgentRuntimeStatusReport();
        var sut = new AgentRuntimeUpdateService(service, report, NullLogger<AgentRuntimeUpdateService>.Instance);

        await sut.RunUpdatesAsync(CancellationToken.None);

        report.InProgress.Should().BeFalse();
        report.LastCompletedUtc.Should().NotBeNull();
        report.Snapshot().Should().HaveCount(AgentRuntimeCatalog.Tools.Count);
        report.Snapshot().Should().OnlyContain(r => r.Outcome == AgentRuntimeUpdateOutcome.Succeeded);
    }

    [Fact]
    public async Task RunUpdatesAsync_contains_failures_without_throwing()
    {
        var runner = new FakeAgentCommandRunner
        {
            Handler = cmd => cmd.FileName == "npm"
                ? new AgentCommandResult(true, 1, string.Empty, "boom", false)
                : new AgentCommandResult(true, 0, "1.0.0", string.Empty, false),
        };
        var service = new ProcessAgentRuntimeService(runner, new AgentRuntimeCredentialStore(new FakeSecretStore()));
        var report = new InMemoryAgentRuntimeStatusReport();
        var sut = new AgentRuntimeUpdateService(service, report, NullLogger<AgentRuntimeUpdateService>.Instance);

        await sut.RunUpdatesAsync(CancellationToken.None);

        report.Snapshot().Should().OnlyContain(r => r.Outcome == AgentRuntimeUpdateOutcome.Failed);
        report.InProgress.Should().BeFalse();
    }
}

/// <summary>
/// The first of ADR 0023's three drift surfaces: the warning in <c>studio-*.log</c>. The case that matters is
/// NOT a successful reinstall (that one lands on the pin by construction) but a FAILED one, because an install
/// that could not run leaves the drifted copy standing in the writable <c>/data/.npm-global</c> prefix and the
/// studio then serves turns against it. A log that names only npm's error in that case makes the advertised
/// surface silent exactly where a field diagnosis needs it.
/// </summary>
public class AgentRuntimeDriftLoggingTests
{
    /// <summary>A version no runtime in the catalog pins, standing in for a drifted install on the volume.</summary>
    private const string Drifted = "2.1.230";

    private static FakeAgentCommandRunner Runner(AgentCommandResult npm, Func<AgentCommand, AgentCommandResult> version) =>
        new() { Handler = cmd => cmd.FileName == "npm" ? npm : version(cmd) };

    private static (AgentRuntimeUpdateService Sut, CapturingLogger<AgentRuntimeUpdateService> Logger) Build(
        FakeAgentCommandRunner runner)
    {
        var service = new ProcessAgentRuntimeService(runner, new AgentRuntimeCredentialStore(new FakeSecretStore()));
        var logger = new CapturingLogger<AgentRuntimeUpdateService>();
        return (new AgentRuntimeUpdateService(service, new InMemoryAgentRuntimeStatusReport(), logger), logger);
    }

    /// <summary>
    /// npm cannot reach the registry: exit 1 with its "npm error code ..." first line, the shape observed from
    /// a real failing install in the studio container (which also left the installed CLI answering
    /// <c>--version</c>, which is why the version after a failure is a real reading and not a guess). Not a
    /// stale-directory error, so the self-heal retry stays out of the way.
    /// </summary>
    private static AgentCommandResult OfflineNpm() => new(
        true, 1, string.Empty,
        "npm error code ENOTFOUND\nnpm error network request to https://registry.npmjs.org failed", false);

    [Fact]
    public async Task A_failed_update_names_the_version_still_installed_and_the_pin()
    {
        var claude = AgentRuntimeCatalog.For(AgentTool.Claude);
        var (sut, logger) = Build(Runner(
            OfflineNpm(),
            _ => new AgentCommandResult(true, 0, $"{Drifted} (Claude Code)", string.Empty, false)));

        await sut.RunUpdatesAsync(CancellationToken.None);

        var warnings = logger.Warnings.ToList();
        warnings.Should().Contain(w => w.Contains("did not succeed", StringComparison.Ordinal),
            "the failure reason is still worth having");
        warnings.Should().Contain(
            w => w.Contains("Claude", StringComparison.Ordinal)
                 && w.Contains(Drifted, StringComparison.Ordinal)
                 && w.Contains(claude.PinnedVersion, StringComparison.Ordinal),
            "an operator grepping studio-*.log for a version mismatch has to find BOTH numbers");
    }

    [Fact]
    public async Task An_on_pin_install_is_left_alone_and_claims_no_drift()
    {
        // Once, npm ran here even when the prefix already held the pinned copy (and could fail, offline). Since the 1.0.2
        // proof (a reinstall at a container start left the CLI broken) an on-pin CLI is not reinstalled at all, so npm
        // never runs, nothing fails, and nothing is claimed about drift.
        var (sut, logger) = Build(Runner(
            OfflineNpm(),
            cmd => new AgentCommandResult(
                true, 0, AgentRuntimeCatalog.All.First(d => d.Command == cmd.FileName).PinnedVersion,
                string.Empty, false)));

        await sut.RunUpdatesAsync(CancellationToken.None);

        logger.Warnings.Should().NotContain(w => w.Contains("the product pins", StringComparison.Ordinal));
        logger.Warnings.Should().NotContain(w => w.Contains("did not succeed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_update_with_no_cli_at_all_claims_nothing_about_drift()
    {
        // No version was ever observed, so a drift warning would be an assertion with no mechanism behind it.
        var (sut, logger) = Build(Runner(OfflineNpm(), _ => AgentCommandResult.NotStarted("no such file")));

        await sut.RunUpdatesAsync(CancellationToken.None);

        logger.Warnings.Should().NotContain(w => w.Contains("the product pins", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_successful_update_that_lands_off_the_pin_still_warns()
    {
        // The surface the branch originally covered, kept covered: npm reports success but the CLI answering
        // on PATH is not the pinned one (a shadowing prefix, a half-applied install).
        var (sut, logger) = Build(Runner(
            new AgentCommandResult(true, 0, "ok", string.Empty, false),
            _ => new AgentCommandResult(true, 0, Drifted, string.Empty, false)));

        await sut.RunUpdatesAsync(CancellationToken.None);

        logger.Warnings.Should().Contain(
            w => w.Contains("Claude", StringComparison.Ordinal) && w.Contains(Drifted, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_drift_line_costs_no_extra_probe()
    {
        // "Do not make the app slower": the version is read from the observations InstallOrUpdateAsync already
        // makes (one before the install, one after), so the failed pass runs exactly the commands it did before.
        var runner = Runner(OfflineNpm(), _ => new AgentCommandResult(true, 0, Drifted, string.Empty, false));
        var (sut, _) = Build(runner);

        await sut.RunUpdatesAsync(CancellationToken.None);

        var perTool = runner.Calls.Count / AgentRuntimeCatalog.Tools.Count;
        perTool.Should().Be(3, "one version probe, one npm install, one version probe");
        runner.Calls.Count(c => c.FileName == "npm").Should().Be(AgentRuntimeCatalog.Tools.Count);
    }
}
