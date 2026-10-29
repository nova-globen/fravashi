using Bunit;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Web.Components.Onboarding;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Covers the "Agent runtimes" onboarding step (S10 - S16), the interactive login proxy panel (S13/S14), the
/// step-insertion migration (S15), the AI board-mapping suggestion (S16), and the Settings surface (S17).
/// </summary>
public class AgentRuntimesStepTests
{
    // ── S10/S11/S12 - the runtimes step ──────────────────────────────────────────────────────────────────

    private static BunitContext RuntimesContext(out FakeConfigurationStore config, out FakeAgentRuntimeService runtimes)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        config = new FakeConfigurationStore();
        runtimes = new FakeAgentRuntimeService();
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IAgentRuntimeService>(runtimes);
        ctx.Services.AddSingleton<IAgentRuntimeStatusReport>(new InMemoryAgentRuntimeStatusReport());
        ctx.Services.AddSingleton<IAgentRuntimeCredentialStore>(new FakeAgentRuntimeCredentialStore());
        ctx.Services.AddSingleton<IAgentLoginProxy>(new FakeAgentLoginProxy());
        ctx.Services.AddSingleton<IConfigurationReloader>(new FakeConfigurationReloader());
        return ctx;
    }

    [Fact]
    public void Renders_a_card_for_every_runtime_with_live_status()
    {
        using var ctx = RuntimesContext(out _, out var runtimes);
        runtimes.Statuses[AgentTool.Claude] = new(AgentTool.Claude, true, "2.1.211", AgentRuntimeAuthState.Authenticated, "stored key");

        var cut = ctx.Render<RuntimesStep>();

        // A card for every runtime the CATALOG offers, and none for anything else: a runtime retired to v2
        // (docs/v2.md section 1) must not leave a card behind that has nothing to sign in to.
        foreach (var tool in AgentRuntimeCatalog.Tools)
        {
            Assert.NotEmpty(cut.FindAll($"[data-testid=runtime-card-{tool}]"));
        }

        Assert.Empty(cut.FindAll("[data-testid=runtime-card-Codex]"));
        Assert.Empty(cut.FindAll("[data-testid=runtime-card-Copilot]"));
        cut.WaitForAssertion(() =>
            Assert.Contains("2.1.211", cut.Find("[data-testid=runtime-version-Claude]").TextContent));
        cut.WaitForAssertion(() =>
            Assert.Contains("Authenticated", cut.Find("[data-testid=runtime-auth-Claude]").TextContent));
    }

    [Fact]
    public void Owns_the_page_title_only_when_routed_directly_not_embedded_in_settings()
    {
        // Backlog 277: the Settings page hosts this same step for Settings > Agents > Runtimes and sign-in,
        // where SettingsPage already titles the window "Fravashi · Settings" like every sibling section.
        // A later PageTitle in the render tree wins, so the embedded step must not render its own.
        using var ctx = RuntimesContext(out _, out _);

        var standalone = ctx.Render<RuntimesStep>();
        Assert.Single(standalone.FindComponents<PageTitle>());

        var embedded = ctx.Render<RuntimesStep>(p => p.Add(c => c.EmbeddedInSettings, true));
        Assert.Empty(embedded.FindComponents<PageTitle>());
    }

    [Fact]
    public void The_card_shows_the_pinned_npm_specifier()
    {
        // Backlog 61: the pinned version has to be visible somewhere a human actually looks, and this card
        // is reachable both during onboarding and from Settings > Agents > Runtimes and sign-in.
        using var ctx = RuntimesContext(out _, out _);

        var cut = ctx.Render<RuntimesStep>();

        Assert.Contains(AgentRuntimeCatalog.For(AgentTool.Claude).PinnedSpecifier, cut.Markup);
        Assert.Contains("pins", cut.Markup);
    }

    [Fact]
    public void A_runtime_that_drifted_off_the_pin_is_badged()
    {
        using var ctx = RuntimesContext(out _, out var runtimes);
        var pinned = AgentRuntimeCatalog.For(AgentTool.Claude).PinnedVersion;
        runtimes.Statuses[AgentTool.Claude] = new(AgentTool.Claude, true, "9.9.9", AgentRuntimeAuthState.Authenticated, "stored key");

        var cut = ctx.Render<RuntimesStep>();

        cut.WaitForAssertion(() =>
            Assert.Contains(pinned, cut.Find("[data-testid=runtime-pin-Claude]").TextContent));
    }

    [Fact]
    public void A_runtime_at_the_pinned_version_is_not_badged()
    {
        using var ctx = RuntimesContext(out _, out var runtimes);
        var pinned = AgentRuntimeCatalog.For(AgentTool.Claude).PinnedVersion;
        runtimes.Statuses[AgentTool.Claude] = new(AgentTool.Claude, true, pinned, AgentRuntimeAuthState.Authenticated, "stored key");

        var cut = ctx.Render<RuntimesStep>();

        // Wait for the status to have arrived at all, then assert the badge is absent rather than early.
        cut.WaitForAssertion(() => Assert.Contains(pinned, cut.Find("[data-testid=runtime-version-Claude]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=runtime-pin-Claude]"));
    }

    [Fact]
    public void Selecting_a_runtime_persists_the_choice()
    {
        using var ctx = RuntimesContext(out var config, out _);
        var cut = ctx.Render<RuntimesStep>();

        cut.Find("[data-testid=runtime-select-Claude]").Change(true);

        Assert.Contains(AgentTool.Claude, AgentRuntimeSelection.Read(config));
        Assert.Equal("Claude", config.Get(AgentRuntimeSelection.SettingKey));
    }

    [Fact]
    public void A_missing_runtime_offers_install_now_and_calls_the_service()
    {
        using var ctx = RuntimesContext(out _, out var runtimes);
        runtimes.Statuses[AgentTool.Claude] = new(AgentTool.Claude, false, null, AgentRuntimeAuthState.NotInstalled, "missing");

        var cut = ctx.Render<RuntimesStep>();
        cut.WaitForElement("[data-testid=runtime-install-Claude]").Click();

        cut.WaitForAssertion(() => Assert.Contains(AgentTool.Claude, runtimes.InstallCalls));
    }

    [Fact]
    public void A_selected_installed_runtime_shows_its_login_panel()
    {
        using var ctx = RuntimesContext(out var config, out var runtimes);
        runtimes.Statuses[AgentTool.Claude] = new(AgentTool.Claude, true, "2.1.211", AgentRuntimeAuthState.SignedOut, "signed out");
        AgentRuntimeSelection.Write(config, [AgentTool.Claude]);

        var cut = ctx.Render<RuntimesStep>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=runtime-login-Claude]")));
    }

    // ── S12/S13/S14 - the login panel ────────────────────────────────────────────────────────────────────

    private static BunitContext LoginContext(
        out FakeAgentRuntimeCredentialStore credentials, out FakeAgentLoginProxy proxy, out FakeConfigurationReloader reloader)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        credentials = new FakeAgentRuntimeCredentialStore();
        proxy = new FakeAgentLoginProxy();
        reloader = new FakeConfigurationReloader();
        ctx.Services.AddSingleton<IAgentRuntimeCredentialStore>(credentials);
        ctx.Services.AddSingleton<IAgentLoginProxy>(proxy);
        ctx.Services.AddSingleton<IConfigurationReloader>(reloader);
        return ctx;
    }

    [Fact]
    public void Saving_an_api_key_stores_it_and_reloads()
    {
        using var ctx = LoginContext(out var credentials, out _, out var reloader);
        var cut = ctx.Render<AgentLoginPanel>(p => p.Add(x => x.Tool, AgentTool.Claude));

        cut.Find("[data-testid=login-apikey-Claude]").Input("sk-ant-test");
        cut.Find("[data-testid=login-save-Claude]").Click();

        Assert.True(credentials.HasApiKey(AgentTool.Claude));
        Assert.Equal("sk-ant-test", credentials.Keys[AgentTool.Claude]);
        Assert.True(reloader.ReloadCount > 0);
    }

    [Fact]
    public void A_runtime_the_broker_carries_offers_no_key_field_and_says_why()
    {
        // Backlog 108, third clause: no provider key is stored here when the broker carries the runtime.
        using var ctx = LoginContext(out var credentials, out _, out _);
        credentials.CarriedElsewhere.Add(AgentTool.Claude);

        var cut = ctx.Render<AgentLoginPanel>(p => p.Add(x => x.Tool, AgentTool.Claude));

        Assert.Empty(cut.FindAll("[data-testid=login-apikey-Claude]"));
        Assert.Empty(cut.FindAll("[data-testid=login-save-Claude]"));
        Assert.Contains("credential broker", cut.Find("[data-testid=login-brokered-Claude]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=login-key-leftover-Claude]"));
    }

    [Fact]
    public void A_key_left_over_from_before_the_broker_is_named_as_unused_and_can_be_removed()
    {
        using var ctx = LoginContext(out var credentials, out _, out var reloader);
        credentials.Keys[AgentTool.Claude] = "sk-ant-left-over";
        credentials.CarriedElsewhere.Add(AgentTool.Claude);

        var cut = ctx.Render<AgentLoginPanel>(p => p.Add(x => x.Tool, AgentTool.Claude));

        // It must not go on claiming the key authenticates anything.
        Assert.Empty(cut.FindAll("[data-testid=login-key-saved-Claude]"));
        Assert.Contains("nothing uses it", cut.Find("[data-testid=login-key-leftover-Claude]").TextContent);

        cut.Find("[data-testid=login-clear-Claude]").Click();

        Assert.False(credentials.HasApiKey(AgentTool.Claude));
        Assert.Empty(cut.FindAll("[data-testid=login-key-leftover-Claude]"));
        Assert.True(reloader.ReloadCount > 0);
    }

    [Fact]
    public void A_subscription_token_left_over_from_before_the_broker_is_named_as_unused_and_can_be_removed()
    {
        // Backlog 267 leftover: HasSubscriptionToken true under the broker must not read as "no sign-in is kept".
        using var ctx = LoginContext(out var credentials, out _, out var reloader);
        credentials.CarriedElsewhere.Add(AgentTool.Claude);
        credentials.SubscriptionTokens.Add(AgentTool.Claude);

        var cut = ctx.Render<AgentLoginPanel>(p => p.Add(x => x.Tool, AgentTool.Claude));

        Assert.DoesNotContain("no sign-in is kept", cut.Find("[data-testid=login-brokered-Claude]").TextContent);
        Assert.Contains("unused", cut.Find("[data-testid=login-subscription-leftover-Claude]").TextContent);

        cut.Find("[data-testid=login-clear-subscription-Claude]").Click();

        Assert.False(credentials.HasSubscriptionToken(AgentTool.Claude));
        Assert.Empty(cut.FindAll("[data-testid=login-subscription-leftover-Claude]"));
        Assert.True(reloader.ReloadCount > 0);
    }

    [Fact]
    public void No_subscription_token_kept_under_the_broker_shows_no_leftover_line()
    {
        using var ctx = LoginContext(out var credentials, out _, out _);
        credentials.CarriedElsewhere.Add(AgentTool.Claude);

        var cut = ctx.Render<AgentLoginPanel>(p => p.Add(x => x.Tool, AgentTool.Claude));

        Assert.Empty(cut.FindAll("[data-testid=login-subscription-leftover-Claude]"));
        Assert.Contains("no sign-in is kept", cut.Find("[data-testid=login-brokered-Claude]").TextContent);
    }

    [Fact]
    public void A_runtime_signed_in_through_the_broker_stays_open_on_a_leftover_subscription_token_until_removed()
    {
        // The state the leftover was seen in live: the broker reports Authenticated, so the panel would
        // otherwise fold to "Claude Code is signed in." Folded must stay false while the leftover is there.
        using var ctx = SignInContext(AgentRuntimeAuthState.Authenticated, out _, out _);
        var credentials = new FakeAgentRuntimeCredentialStore { CarriedElsewhere = { AgentTool.Claude } };
        credentials.SubscriptionTokens.Add(AgentTool.Claude);
        ctx.Services.AddSingleton<IAgentRuntimeCredentialStore>(credentials);

        var cut = ctx.Render<RuntimesStep>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=login-subscription-leftover-Claude]")));
        Assert.Empty(cut.FindAll("[data-testid=login-signed-in-Claude]"));

        cut.Find("[data-testid=login-clear-subscription-Claude]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=login-signed-in-Claude]")));
        Assert.Empty(cut.FindAll("[data-testid=login-subscription-leftover-Claude]"));
        Assert.False(credentials.HasSubscriptionToken(AgentTool.Claude));
    }

    [Fact]
    public void Interactive_login_streams_output_highlights_url_and_code_and_forwards_input()
    {
        using var ctx = LoginContext(out _, out var proxy, out _);
        var cut = ctx.Render<AgentLoginPanel>(p => p.Add(x => x.Tool, AgentTool.Claude));

        cut.Find("[data-testid=login-start-Claude]").Click();
        var session = proxy.Started!;
        session.Emit("Open https://github.com/login/device and enter code", url: "https://github.com/login/device", deviceCode: "WDJB-MJHT");

        // The streamed line renders as a clickable URL and a copyable device code (a generous timeout so the
        // async channel read is not raced by a busy parallel run).
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("https://github.com/login/device", cut.Find("[data-testid=login-url]").GetAttribute("href"));
            Assert.Contains("WDJB-MJHT", cut.Find("[data-testid=login-code]").TextContent);
        }, TimeSpan.FromSeconds(10));

        // Each is drawn ONCE, in place: the installed window showed the address twice back to back (item 130).
        var line = cut.Find("[data-testid=login-chunk]").TextContent;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(line, @"github\.com/login/device"));
        Assert.Matches(@"Open\s+https://github\.com/login/device\s+and enter code", line);

        // Typed input is forwarded to the process stdin.
        cut.Find("[data-testid=login-input-Claude]").Input("my answer");
        cut.Find("[data-testid=login-send-Claude]").Click();
        cut.WaitForAssertion(() => Assert.Contains("my answer", session.SentInput), TimeSpan.FromSeconds(10));

        session.Finish(0);
    }

    // ── Owner report 2026-09-30: a sign-in that worked left the panel exactly as it was ──────────────────

    /// <summary>The runtimes step with a runtime selected and a proxy the test drives.</summary>
    private static BunitContext SignInContext(
        AgentRuntimeAuthState state, out FakeAgentRuntimeService runtimes, out FakeAgentLoginProxy proxy)
    {
        var ctx = RuntimesContext(out var config, out runtimes);
        proxy = new FakeAgentLoginProxy();
        ctx.Services.AddSingleton<IAgentLoginProxy>(proxy);
        runtimes.Statuses[AgentTool.Claude] = new(AgentTool.Claude, true, "2.1.280", state, "claude auth status reports it.");
        AgentRuntimeSelection.Write(config, [AgentTool.Claude]);
        return ctx;
    }

    [Fact]
    public void A_signed_in_runtime_says_so_and_folds_its_ways_to_sign_in_away()
    {
        using var ctx = SignInContext(AgentRuntimeAuthState.Authenticated, out _, out _);
        var cut = ctx.Render<RuntimesStep>();

        cut.WaitForAssertion(() =>
            Assert.Contains("Claude Code is signed in.", cut.Find("[data-testid=login-signed-in-Claude]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=login-apikey-Claude]"));
        Assert.Empty(cut.FindAll("[data-testid=login-start-Claude]"));

        // One button opens both ways again, and another folds them back without changing anything.
        cut.Find("[data-testid=login-change-Claude]").Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=login-apikey-Claude]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=login-start-Claude]"));

        cut.Find("[data-testid=login-keep-Claude]").Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=login-signed-in-Claude]"));
    }

    [Fact]
    public void An_interactive_sign_in_that_works_ends_on_a_signed_in_panel_not_on_the_form_it_started_from()
    {
        using var ctx = SignInContext(AgentRuntimeAuthState.SignedOut, out var runtimes, out var proxy);
        var cut = ctx.Render<RuntimesStep>();

        cut.WaitForElement("[data-testid=login-start-Claude]").Click();
        var session = proxy.Started!;
        session.Emit("Paste code here if prompted > Login successful.");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=login-output-Claude]")), TimeSpan.FromSeconds(10));

        // The CLI wrote its credentials, so the runtime's own check now answers signed in.
        runtimes.Statuses[AgentTool.Claude] = new(AgentTool.Claude, true, "2.1.280", AgentRuntimeAuthState.Authenticated, "claude auth status reports signed in.");
        session.Finish(0);

        cut.WaitForAssertion(() =>
            Assert.Contains("Signed in. Claude Code is ready to use.", cut.Find("[data-testid=login-signed-in-Claude]").TextContent),
            TimeSpan.FromSeconds(10));
        Assert.Empty(cut.FindAll("[data-testid=login-output-Claude]"));
        Assert.Empty(cut.FindAll("[data-testid=login-apikey-Claude]"));
        Assert.Contains("Authenticated", cut.Find("[data-testid=runtime-auth-Claude]").TextContent);
    }

    [Fact]
    public void A_sign_in_that_finishes_while_the_runtime_still_reports_signed_out_says_so_and_keeps_its_output()
    {
        using var ctx = SignInContext(AgentRuntimeAuthState.SignedOut, out _, out var proxy);
        var cut = ctx.Render<RuntimesStep>();

        cut.WaitForElement("[data-testid=login-start-Claude]").Click();
        var session = proxy.Started!;
        session.Emit("Something went sideways.");
        session.Finish(0);

        cut.WaitForAssertion(() =>
            Assert.Contains("still reports that it is not signed in", cut.Find("[data-testid=login-outcome-Claude]").TextContent),
            TimeSpan.FromSeconds(10));
        Assert.Contains("Something went sideways.", cut.Find("[data-testid=login-output-Claude]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=login-signed-in-Claude]"));
    }

    // ── Backlog 267: a corp-broker build offered a subscription sign-in the store refuses, then said Signed in ──

    [Fact]
    public void A_runtime_the_broker_carries_offers_no_interactive_sign_in_and_says_where_to_look()
    {
        using var ctx = LoginContext(out var credentials, out var proxy, out _);
        credentials.CarriedElsewhere.Add(AgentTool.Claude);

        var cut = ctx.Render<AgentLoginPanel>(p => p.Add(x => x.Tool, AgentTool.Claude));

        Assert.Empty(cut.FindAll("[data-testid=login-start-Claude]"));
        Assert.Empty(cut.FindAll("[data-testid=login-token-hint-Claude]"));
        Assert.Empty(cut.FindAll("[data-testid=login-no-interactive-Claude]"));
        Assert.Empty(cut.FindAll("[data-testid=login-apikey-Claude]"));
        Assert.DoesNotContain("sign in interactively", cut.Markup);
        var brokered = cut.Find("[data-testid=login-brokered-Claude]");
        Assert.Contains("credential broker", brokered.TextContent);
        Assert.Equal("/settings/broker", cut.Find("[data-testid=login-broker-settings-Claude]").GetAttribute("href"));
        Assert.Null(proxy.Started);
    }

    [Fact]
    public void Changing_how_a_brokered_runtime_signs_in_still_offers_no_interactive_sign_in()
    {
        // The owner's path on the test VM: signed in through the broker, "Change how it signs in", then the offer.
        using var ctx = SignInContext(AgentRuntimeAuthState.Authenticated, out _, out _);
        ctx.Services.AddSingleton<IAgentRuntimeCredentialStore>(new FakeAgentRuntimeCredentialStore { CarriedElsewhere = { AgentTool.Claude } });
        var cut = ctx.Render<RuntimesStep>();

        cut.WaitForElement("[data-testid=login-change-Claude]").Click();

        Assert.Empty(cut.FindAll("[data-testid=login-start-Claude]"));
        Assert.Empty(cut.FindAll("[data-testid=login-token-hint-Claude]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=login-brokered-Claude]"));
    }

    [Fact]
    public void A_sign_in_whose_token_was_refused_says_so_and_never_says_signed_in()
    {
        using var ctx = SignInContext(AgentRuntimeAuthState.SignedOut, out var runtimes, out var proxy);
        var cut = ctx.Render<RuntimesStep>();

        cut.WaitForElement("[data-testid=login-start-Claude]").Click();
        var session = proxy.Started!;
        session.Emit("Your OAuth token (valid for 1 year): [token hidden]");

        // Something else authenticates the runtime (the broker's bearer did on the VM), so its check reads signed in.
        runtimes.Statuses[AgentTool.Claude] = new(AgentTool.Claude, true, "2.1.280", AgentRuntimeAuthState.Authenticated, "Authenticated through the credential broker.");
        session.TokenRefusal = "Claude Code is authenticated through the credential broker here, so a subscription token is not stored in this container.";
        session.Finish(0);

        cut.WaitForAssertion(() =>
            Assert.Contains(
                "its token was not kept: Claude Code is authenticated through the credential broker here",
                cut.Find("[data-testid=login-outcome-Claude]").TextContent),
            TimeSpan.FromSeconds(10));
        Assert.Empty(cut.FindAll("[data-testid=login-signed-in-Claude]"));
        Assert.DoesNotContain("Signed in.", cut.Markup);
        Assert.Contains("[token hidden]", cut.Find("[data-testid=login-output-Claude]").TextContent);
    }

    [Fact]
    public async Task A_sign_in_whose_output_ends_before_its_exit_code_is_known_still_shows_its_outcome()
    {
        // #316: the release runner saw the stream end before the exit code existed. The panel read no code, dropped the
        // token refusal, folded to "signed in" and never rendered again. Here that order is forced: the stream ends, the
        // panel is seen to have read its end, and only then does the code arrive.
        using var ctx = SignInContext(AgentRuntimeAuthState.SignedOut, out var runtimes, out var proxy);
        var cut = ctx.Render<RuntimesStep>();

        cut.WaitForElement("[data-testid=login-start-Claude]").Click();
        var session = proxy.Started!;
        session.Emit("Your OAuth token (valid for 1 year): [token hidden]");
        runtimes.Statuses[AgentTool.Claude] = new(AgentTool.Claude, true, "2.1.280", AgentRuntimeAuthState.Authenticated, "Authenticated through the credential broker.");
        session.TokenRefusal = "Claude Code is authenticated through the credential broker here, so a subscription token is not stored in this container.";

        session.EndOutput();
        await session.OutputEndSeen.WaitAsync(TimeSpan.FromSeconds(10));
        // Gives a panel that does not wait for the code the time to settle without it; one that waits passes with any delay.
        await Task.Delay(250);
        session.SetExitCode(0);

        cut.WaitForAssertion(() =>
            Assert.Contains(
                "its token was not kept: Claude Code is authenticated through the credential broker here",
                cut.Find("[data-testid=login-outcome-Claude]").TextContent),
            TimeSpan.FromSeconds(10));
        Assert.Empty(cut.FindAll("[data-testid=login-signed-in-Claude]"));
    }

    [Fact]
    public void A_sign_in_that_fails_names_its_exit_code()
    {
        using var ctx = SignInContext(AgentRuntimeAuthState.SignedOut, out _, out var proxy);
        var cut = ctx.Render<RuntimesStep>();

        cut.WaitForElement("[data-testid=login-start-Claude]").Click();
        proxy.Started!.Finish(1);

        cut.WaitForAssertion(() =>
            Assert.Contains("did not complete (exit code 1)", cut.Find("[data-testid=login-outcome-Claude]").TextContent),
            TimeSpan.FromSeconds(10));
        Assert.NotEmpty(cut.FindAll("[data-testid=login-start-Claude]"));
    }

    // ── S15 - the step-insertion migration ───────────────────────────────────────────────────────────────

    private static BunitContext LayoutContext(IOnboardingState state, out FakeConfigurationStore config)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        config = new FakeConfigurationStore();
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IProviderConnectionStore>(new FakeProviderConnectionStore());
        ctx.Services.AddScoped<OnboardingDraft>();
        ctx.Services.AddScoped<ThemeState>();
        ctx.Services.AddScoped<AgentPanelState>();
        ctx.Services.AddScoped<ScratchpadState>();
        ctx.Services.AddScoped<StartDialogState>();
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton<IConfigurationReloader>(new FakeConfigurationReloader());
        ctx.Services.AddSingleton<IApplicationRestarter>(new FakeApplicationRestarter());
        // The window title bar (rendered by the layout) asks for these; git stays unavailable here
        // and no workspace is bound, so the title stays the bare product name.
        ctx.Services.AddSingleton<Fravashi.Application.Git.IGitService>(new UnavailableGitService());
        ctx.Services.AddSingleton<Fravashi.Application.Notifications.INotificationHistory>(
            new Fravashi.Infrastructure.Notifications.InMemoryNotificationHistory());
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Bridge.BridgeWorkspaceContext(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
        ctx.Services.AddSingleton<Fravashi.Application.Persistence.IWorkspaceStore>(new EmptyWorkspaceStore());
        // Backlog 74: the wizard records that onboarding finished. Off by default in production; here it
        // is the no-op recorder, so nothing about telemetry changes what these tests observe.
        ctx.Services.AddSingleton(Fravashi.Application.Telemetry.IProductEventRecorder.Off);
        return ctx;
    }

    [Fact]
    public void Mid_wizard_furthest_index_is_shifted_once_for_the_new_first_step()
    {
        var state = new FakeOnboardingState(complete: false, currentStep: "3"); // old "board"
        using var ctx = LayoutContext(state, out var config);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/onboarding/board");

        ctx.Render<OnboardingLayout>(ps => ps.Add(l => l.Body, (RenderFragment)(_ => { })));

        // Old index 3 (board, pre-runtimes ordering) survives BOTH reorderings pointing at the same
        // step: V1 shifts +1 (runtimes inserted at the front), V2 shifts -1 (the source step retired,
        // plan 2c). Board sits at index 3 of the current array.
        Assert.Equal("3", state.CurrentStep);
        Assert.Equal("true", config.Get("AgentRuntimes:StepIndexMigratedV1"));
        Assert.Equal("true", config.Get("Onboarding:StepIndexMigratedV2"));
    }

    [Fact]
    public void The_migration_runs_only_once()
    {
        var state = new FakeOnboardingState(complete: false, currentStep: "2");
        using var ctx = LayoutContext(state, out var config);
        config.Set("AgentRuntimes:StepIndexMigratedV1", "true"); // already migrated

        ctx.Render<OnboardingLayout>(ps => ps.Add(l => l.Body, (RenderFragment)(_ => { })));

        Assert.Equal("2", state.CurrentStep); // untouched
    }

    [Fact]
    public void The_bare_route_lands_on_the_new_first_step()
    {
        using var ctx = LayoutContext(new FakeOnboardingState(complete: false), out _);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/onboarding");

        ctx.Render<OnboardingLayout>(ps => ps.Add(l => l.Body, (RenderFragment)(_ => { })));

        Assert.EndsWith("/onboarding/runtimes", nav.Uri, StringComparison.OrdinalIgnoreCase);
    }

    // ── S16 - AI board-mapping suggestion ────────────────────────────────────────────────────────────────

    private static BunitContext MappingSuggestContext(
        BoardMappingSuggestion suggestion, bool runtimeAuthenticated, out FakeConfigurationStore config)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        config = new FakeConfigurationStore();
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);

        var connections = new FakeProviderConnectionStore();
        var secrets = new FakeSecretStore();
        secrets.Set("board:token", "pat");
        connections.Upsert(new ProviderConnection("board", ProviderKind.Board,
            "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var runtimes = new FakeAgentRuntimeService();
        if (runtimeAuthenticated)
        {
            AgentRuntimeSelection.Write(config, [AgentTool.Claude]);
            runtimes.Statuses[AgentTool.Claude] = new(AgentTool.Claude, true, "2.1.211", AgentRuntimeAuthState.Authenticated, "key");
        }

        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IProviderConnectionStore>(connections);
        ctx.Services.AddSingleton<ISecretStore>(secrets);
        ctx.Services.AddSingleton<IBoardStatesProbe>(new FakeBoardStatesProbe(
            BoardStatesResult.Ok(["Selected for Dev", "Doing", "Closed"])));
        ctx.Services.AddSingleton<IAgentRuntimeService>(runtimes);
        ctx.Services.AddSingleton<IBoardMappingSuggester>(new FakeBoardMappingSuggester(suggestion));
        ctx.Services.AddScoped<OnboardingDraft>();
        return ctx;
    }

    [Fact]
    public void Suggest_button_is_disabled_with_a_reason_when_no_runtime_is_authenticated()
    {
        using var ctx = MappingSuggestContext(BoardMappingSuggestion.Failed("x"), runtimeAuthenticated: false, out _);
        var cut = ctx.Render<BoardMappingStep>();
        cut.Find("[data-testid=mapping-load]").Click();

        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find("[data-testid=mapping-suggest]").GetAttribute("disabled")));
        Assert.NotEmpty(cut.FindAll("[data-testid=mapping-suggest-disabled]"));
    }

    [Fact]
    public void Suggest_applies_the_ai_assignment_and_marks_the_source()
    {
        var suggestion = new BoardMappingSuggestion(
            true,
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["Ready"] = ["Selected for Dev"],
                ["In Progress"] = ["Doing"],
                ["Done"] = ["Closed"],
            },
            null,
            "{}");
        using var ctx = MappingSuggestContext(suggestion, runtimeAuthenticated: true, out _);
        var cut = ctx.Render<BoardMappingStep>();
        cut.Find("[data-testid=mapping-load]").Click();

        cut.WaitForAssertion(() =>
            Assert.Null(cut.Find("[data-testid=mapping-suggest]").GetAttribute("disabled")));
        cut.Find("[data-testid=mapping-suggest]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("AI-suggested", cut.Find("[data-testid=mapping-source]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=mapping-suggest-error]"));
    }

    [Fact]
    public void A_suggestion_that_puts_one_state_under_two_statuses_is_refused_on_confirm()
    {
        // Backlog 181. The picker keeps one status per state when a person toggles, but a suggestion is applied
        // as the runtime returned it, so the picker can hold a duplicate too and confirming has to catch it.
        var suggestion = new BoardMappingSuggestion(
            true,
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["Ready"] = ["Selected for Dev"],
                ["Done"] = ["Closed"],
                ["Cancelled"] = ["Closed"],
            },
            null,
            "{}");
        using var ctx = MappingSuggestContext(suggestion, runtimeAuthenticated: true, out var config);
        var cut = ctx.Render<BoardMappingStep>();
        cut.Find("[data-testid=mapping-load]").Click();
        cut.WaitForAssertion(() =>
            Assert.Null(cut.Find("[data-testid=mapping-suggest]").GetAttribute("disabled")));
        cut.Find("[data-testid=mapping-suggest]").Click();
        cut.WaitForAssertion(() =>
            Assert.Contains("AI-suggested", cut.Find("[data-testid=mapping-source]").TextContent));

        cut.Find("[data-testid=mapping-confirm]").Click();

        Assert.Contains("'Closed' is listed under Done and Cancelled.", cut.Find("[data-testid=mapping-duplicate]").TextContent);
        Assert.Null(config.Get("Board:StatusMapping"));
    }

    [Fact]
    public void A_malformed_suggestion_shows_an_error_and_keeps_the_name_match()
    {
        using var ctx = MappingSuggestContext(
            BoardMappingSuggestion.Failed("The runtime did not return a JSON object."), runtimeAuthenticated: true, out _);
        var cut = ctx.Render<BoardMappingStep>();
        cut.Find("[data-testid=mapping-load]").Click();
        cut.WaitForAssertion(() =>
            Assert.Null(cut.Find("[data-testid=mapping-suggest]").GetAttribute("disabled")));

        cut.Find("[data-testid=mapping-suggest]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=mapping-suggest-error]")));
        Assert.Contains("name match", cut.Find("[data-testid=mapping-source]").TextContent);
    }

    // ── S17 - Settings surface ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Settings_panel_shows_connection_and_runtime_status_with_entry_links()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        var config = new FakeConfigurationStore();
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        var connections = new FakeProviderConnectionStore();
        connections.Upsert(new ProviderConnection("board", ProviderKind.Board,
            "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));
        var runtimes = new FakeAgentRuntimeService();
        runtimes.Statuses[AgentTool.Claude] = new(AgentTool.Claude, true, "2.1.211", AgentRuntimeAuthState.Authenticated, "key");

        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IProviderConnectionStore>(connections);
        ctx.Services.AddSingleton<IAgentRuntimeService>(runtimes);
        ctx.Services.AddSingleton<IAgentRuntimeStatusReport>(new InMemoryAgentRuntimeStatusReport());

        var cut = ctx.Render<OnboardingConnectionsPanel>();

        Assert.Contains("Verified", cut.Find("[data-testid=settings-conn-status-board]").TextContent);
        Assert.Equal("/onboarding/board?mode=settings",
            cut.Find("[data-testid=settings-conn-link-board]").GetAttribute("href"));
        Assert.Equal("/onboarding/runtimes?mode=settings",
            cut.Find("[data-testid=settings-runtimes-link]").GetAttribute("href"));
        cut.WaitForAssertion(() =>
            Assert.Contains("authenticated", cut.Find("[data-testid=settings-runtime-auth-Claude]").TextContent));
        ctx.Dispose();
    }
}
