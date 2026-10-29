using Bunit;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Web.Components.Onboarding;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Covers the wizard-shell behaviour added by the UX batch: draft state survival (F13), the routed step
/// container's guard rails (F4), and the redesigned board-mapping multi-select (F7).
/// </summary>
public class OnboardingWizardTests
{
    // ── F13 - wizard-scoped draft state ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Draft_persists_nonsecret_fields_across_instances_but_never_the_token()
    {
        var config = new FakeConfigurationStore();
        var connections = new FakeProviderConnectionStore();

        var first = new OnboardingDraft(config, connections);
        first.WikiBaseUrl = "https://acme.atlassian.net/wiki";
        first.WikiEmail = "me@acme.com";
        first.BoardProject = "ENG";
        first.WikiToken = "super-secret-pat";
        first.Save();

        // A fresh instance (a refresh / reconnect) rehydrates the non-secret fields from the settings table…
        var second = new OnboardingDraft(config, connections);
        Assert.Equal("https://acme.atlassian.net/wiki", second.WikiBaseUrl);
        Assert.Equal("me@acme.com", second.WikiEmail);
        Assert.Equal("ENG", second.BoardProject);

        // …but the token is gone, and its plaintext was never written to any settings key.
        Assert.Equal(string.Empty, second.WikiToken);
        Assert.DoesNotContain("super-secret-pat", config.Values.Values);
    }

    [Fact]
    public void Draft_seeds_from_the_saved_connection_and_provider()
    {
        var config = new FakeConfigurationStore();
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        var connections = new FakeProviderConnectionStore();
        connections.Upsert(new ProviderConnection("board", ProviderKind.Board,
            "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var draft = new OnboardingDraft(config, connections);

        Assert.Equal(OnboardingProviders.Jira, draft.BoardProvider);
        Assert.Equal("https://acme.atlassian.net", draft.BoardBaseUrl);
    }

    // ── F4 - routed step container guard rails ──────────────────────────────────────────────────────────────

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

    private static IRenderedComponent<OnboardingLayout> RenderLayoutAt(BunitContext ctx, string relativeUrl)
    {
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(relativeUrl);
        return ctx.Render<OnboardingLayout>(ps => ps.Add(l => l.Body, (RenderFragment)(_ => { })));
    }

    [Fact]
    public void Completed_wizard_redirects_a_step_route_to_the_studio()
    {
        using var ctx = LayoutContext(new FakeOnboardingState(complete: true), out _);

        var cut = RenderLayoutAt(ctx, "/onboarding/review");

        // Redirected away: the wizard card never renders, and we are no longer on an onboarding route.
        Assert.Empty(cut.FindAll("[data-testid=ob-card]"));
        Assert.DoesNotContain("onboarding", ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_reentry_unlocks_every_step()
    {
        using var ctx = LayoutContext(new FakeOnboardingState(complete: true), out _);

        var cut = RenderLayoutAt(ctx, "/onboarding/review?mode=settings");

        // The review step renders with Done, and earlier steps are reachable (not disabled) in re-entry.
        Assert.NotEmpty(cut.FindAll("[data-testid=wizard-done]"));
        Assert.Null(cut.Find("[data-testid=step-nav-data]").GetAttribute("disabled"));
    }

    [Fact]
    public void First_run_cannot_jump_past_the_furthest_reached_step()
    {
        using var ctx = LayoutContext(new FakeOnboardingState(complete: false, currentStep: "1"), out var config);
        // Both index migrations already ran for this install; the saved index is current.
        config.Set("AgentRuntimes:StepIndexMigratedV1", "true");
        config.Set("Onboarding:StepIndexMigratedV2", "true");

        RenderLayoutAt(ctx, "/onboarding/board"); // index 3, furthest reached is 1 ("data")

        Assert.EndsWith("/onboarding/data", ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Next_is_gated_on_the_data_location_confirmation()
    {
        using var ctx = LayoutContext(new FakeOnboardingState(complete: false, currentStep: "0"), out var config);

        var unconfirmed = RenderLayoutAt(ctx, "/onboarding/data");
        Assert.NotNull(unconfirmed.Find("[data-testid=wizard-next]").GetAttribute("disabled"));

        using var confirmedCtx = LayoutContext(new FakeOnboardingState(complete: false, currentStep: "0"), out var config2);
        config2.Set("Onboarding:DataLocationConfirmed", "true");
        var confirmed = RenderLayoutAt(confirmedCtx, "/onboarding/data");
        Assert.Null(confirmed.Find("[data-testid=wizard-next]").GetAttribute("disabled"));
    }

    // ── F7 - board-mapping multi-select ─────────────────────────────────────────────────────────────────────

    private static BunitContext MappingContext(IBoardStatesProbe states, bool withVerifiedToken, out FakeConfigurationStore config)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        config = new FakeConfigurationStore();
        var connections = new FakeProviderConnectionStore();
        var secrets = new FakeSecretStore();
        if (withVerifiedToken)
        {
            secrets.Set("board:token", "pat");
            connections.Upsert(new ProviderConnection("board", ProviderKind.Board,
                "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));
        }

        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IProviderConnectionStore>(connections);
        ctx.Services.AddSingleton<ISecretStore>(secrets);
        ctx.Services.AddSingleton(states);
        // The mapping step now injects the runtime service + AI suggester (S16); default fakes keep the
        // name-match tests unaffected (no runtime selected → the AI-suggest button stays disabled).
        ctx.Services.AddSingleton<Application.AgentRuntimes.IAgentRuntimeService>(new FakeAgentRuntimeService());
        ctx.Services.AddSingleton<Application.AgentRuntimes.IBoardMappingSuggester>(
            new FakeBoardMappingSuggester(Application.AgentRuntimes.BoardMappingSuggestion.Failed("no runtime")));
        ctx.Services.AddScoped<OnboardingDraft>();
        return ctx;
    }

    [Fact]
    public void Verified_board_loads_real_states_prefills_and_flags_unmapped()
    {
        var states = new FakeBoardStatesProbe(BoardStatesResult.Ok(
            ["To Do", "In Progress", "Code Review", "Done", "Icebox"]));
        using var ctx = MappingContext(states, withVerifiedToken: true, out var config);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);

        var cut = ctx.Render<BoardMappingStep>();
        cut.Find("[data-testid=mapping-load]").Click();

        // Each canonical row now offers the user's real states as options in a multi-select.
        Assert.Contains("In Progress", cut.Find("[data-testid=map-InProgress]").TextContent);
        Assert.Contains("Done", cut.Find("[data-testid=map-Done]").TextContent);

        // A state that matches nothing by name is flagged as unmapped.
        Assert.Contains("Icebox", cut.Find("[data-testid=mapping-unmapped]").TextContent);

        // Name-based prefill assigned "To Do" (a Backlog synonym); confirm persists it and omits the unmapped state.
        cut.Find("[data-testid=mapping-confirm]").Click();
        var saved = config.Get("Board:StatusMapping");
        Assert.NotNull(saved);
        Assert.Contains("To Do", saved!);
        Assert.DoesNotContain("Icebox", saved);
    }

    [Fact]
    public void Unverified_board_degrades_to_manual_mapping_with_a_note()
    {
        var states = new FakeBoardStatesProbe(BoardStatesResult.Failed("no token"));
        using var ctx = MappingContext(states, withVerifiedToken: false, out var config);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);

        var cut = ctx.Render<BoardMappingStep>();

        Assert.Contains("token", cut.Find("[data-testid=mapping-manual-note]").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("[data-testid=mapping-load]"));
        Assert.Equal(BoardStatusMapping.Catalog.Count, cut.FindAll("[data-testid=mapping-table] tbody tr").Count);
    }

    [Fact]
    public void Typing_one_state_under_two_statuses_is_refused_on_confirm_with_the_state_named()
    {
        // Backlog 181. The text-box mode took anything, and the reader then kept whichever row came last.
        var states = new FakeBoardStatesProbe(BoardStatesResult.Failed("no token"));
        using var ctx = MappingContext(states, withVerifiedToken: false, out var config);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);

        var cut = ctx.Render<BoardMappingStep>();
        cut.Find("[data-testid=map-Done]").Change("Closed");
        cut.Find("[data-testid=map-Cancelled]").Change("closed");
        cut.Find("[data-testid=mapping-confirm]").Click();

        var refusal = cut.Find("[data-testid=mapping-duplicate]").TextContent;
        Assert.Contains("'Closed' is listed under Done and Cancelled.", refusal);
        Assert.Null(config.Get("Board:StatusMapping"));
        Assert.Empty(cut.FindAll("[data-testid=mapping-saved]"));

        // Listing it under one status is accepted, and the refusal goes away.
        cut.Find("[data-testid=map-Cancelled]").Change("Removed");
        cut.Find("[data-testid=mapping-confirm]").Click();

        Assert.Empty(cut.FindAll("[data-testid=mapping-duplicate]"));
        Assert.NotNull(cut.Find("[data-testid=mapping-saved]"));
        Assert.Contains("Removed", config.Get("Board:StatusMapping")!);
    }

    [Fact]
    public void Local_board_shows_the_canonical_set_as_is()
    {
        var states = new FakeBoardStatesProbe(BoardStatesResult.Failed("n/a"));
        using var ctx = MappingContext(states, withVerifiedToken: false, out var config);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Local);

        var cut = ctx.Render<BoardMappingStep>();

        cut.Find("[data-testid=mapping-local]");
        Assert.Empty(cut.FindAll("[data-testid=mapping-load]"));
        // Identity mapping: the canonical name is shown for each row rather than an editable field.
        Assert.Contains("In Progress", cut.Find("[data-testid=map-InProgress]").TextContent);
    }

    // ── D1 - routed dispatch survives the batch-7 step-index migration ───────────────────────────────────────

    [Fact]
    public void Completed_install_with_pre_migration_index_lands_on_the_studio_from_the_bare_route()
    {
        // The owner's case: onboarding was completed on the 6-step (pre-runtimes) wizard, so the persisted
        // furthest index is "5" (the OLD "review"). After recreating the container on the new image the
        // migration has not run yet (no marker). Completion must win over the stale furthest index: the bare
        // /onboarding route dispatches to the studio, never to whatever step index 5 now points at (mapping).
        using var ctx = LayoutContext(new FakeOnboardingState(complete: true, currentStep: "5"), out _);

        RenderLayoutAt(ctx, "/onboarding");

        Assert.DoesNotContain("onboarding", ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Migrated_mid_wizard_install_resumes_at_the_correct_shifted_step()
    {
        // A mid-wizard install that had reached the OLD "mapping" (index 4, pre-runtimes ordering) must
        // resume at the SAME step after BOTH reorderings: the V1 shift (+1, runtimes inserted at the
        // front) and the V2 shift (-1, the source step retired in plan 2c). Net: mapping sits at index 4
        // of the current array and the bare route lands there.
        var state = new FakeOnboardingState(complete: false, currentStep: "4");
        using var ctx = LayoutContext(state, out _);

        RenderLayoutAt(ctx, "/onboarding");

        Assert.EndsWith("/onboarding/mapping", ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("4", state.CurrentStep);
    }

    // ── D2 - dropdown multi-select (chips, add/remove, exclusivity) ──────────────────────────────────────────

    private static IRenderedComponent<BoardMappingStep> RenderMultiSelect(string[] states, out FakeConfigurationStore config, out BunitContext ctx)
    {
        var probe = new FakeBoardStatesProbe(BoardStatesResult.Ok(states));
        ctx = MappingContext(probe, withVerifiedToken: true, out config);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.AzureDevOps);
        var cut = ctx.Render<BoardMappingStep>();
        cut.Find("[data-testid=mapping-load]").Click();
        return cut;
    }

    [Fact]
    public void Picker_renders_prefilled_states_as_chips_and_an_empty_state()
    {
        var cut = RenderMultiSelect(["To Do", "Done"], out _, out var ctx);
        using (ctx)
        {
            // Prefill mapped "To Do" → Backlog and "Done" → Done; those show as chips (selection is now obvious).
            Assert.NotNull(cut.Find("[data-testid=map-Backlog-chip-ToDo]"));
            Assert.NotNull(cut.Find("[data-testid=map-Done-chip-Done]"));

            // A canonical status with nothing mapped shows the explicit empty-state, not a chip.
            Assert.Equal("No states mapped", cut.Find("[data-testid=map-Triage-empty]").TextContent.Trim());
        }
    }

    [Fact]
    public void Removing_a_chip_returns_the_state_to_the_unmapped_strip()
    {
        var cut = RenderMultiSelect(["New", "Done"], out _, out var ctx);
        using (ctx)
        {
            // "New" prefilled onto Triage; removing its chip unmaps it.
            cut.Find("[data-testid=map-Triage-chip-remove-New]").Click();

            Assert.Empty(cut.FindAll("[data-testid=map-Triage-chip-New]"));
            Assert.NotNull(cut.Find("[data-testid=unmapped-New]"));
        }
    }

    [Fact]
    public void Checking_a_state_that_is_mapped_elsewhere_reassigns_it_exclusively()
    {
        var cut = RenderMultiSelect(["New", "Done"], out _, out var ctx);
        using (ctx)
        {
            // "New" starts on Triage. Open the Backlog picker: its "New" option is dimmed + labelled.
            cut.Find("[data-testid=map-Backlog-trigger]").Click();
            Assert.Contains("Triage", cut.Find("[data-testid=map-Backlog-opt-elsewhere-New]").TextContent);

            // Check it under Backlog → it moves off Triage (one canonical per native state).
            cut.Find("[data-testid=map-Backlog-opt-New] input").Change(true);

            Assert.NotNull(cut.Find("[data-testid=map-Backlog-chip-New]"));
            Assert.Empty(cut.FindAll("[data-testid=map-Triage-chip-New]"));
        }
    }

    // ── D3 - synonym prefill against the owner's real Azure DevOps states ────────────────────────────────────

    [Fact]
    public void Prefill_matches_the_owners_azure_devops_states_by_name_and_synonym()
    {
        // The exact 13 states from the owner's Azure DevOps board (2026-07-16 report).
        var owner = new[]
        {
            "New", "Active", "Resolved", "Closed", "Requested", "Accepted", "Removed",
            "Design", "Ready", "Inactive", "In Planning", "In Progress", "Completed",
        };
        var cut = RenderMultiSelect(owner, out var config, out var ctx);
        using (ctx)
        {
            // Concrete out-of-the-box assignments (canonical name OR common-native synonym, case-insensitive).
            Assert.NotNull(cut.Find("[data-testid=map-Triage-chip-New]"));
            Assert.NotNull(cut.Find("[data-testid=map-Backlog-chip-Accepted]"));   // Accepted is a Backlog synonym
            Assert.NotNull(cut.Find("[data-testid=map-Ready-chip-Ready]"));
            Assert.NotNull(cut.Find("[data-testid=map-InProgress-chip-Active]"));  // Active synonym
            Assert.NotNull(cut.Find("[data-testid=map-InProgress-chip-InProgress]")); // "In Progress" canonical name
            Assert.NotNull(cut.Find("[data-testid=map-InReview-chip-Resolved]"));
            Assert.NotNull(cut.Find("[data-testid=map-Done-chip-Closed]"));
            Assert.NotNull(cut.Find("[data-testid=map-Done-chip-Completed]"));
            Assert.NotNull(cut.Find("[data-testid=map-Cancelled-chip-Removed]"));

            // States with no synonym in the canonical set land in the visible Unmapped strip (all of them).
            Assert.NotNull(cut.Find("[data-testid=unmapped-Requested]"));
            Assert.NotNull(cut.Find("[data-testid=unmapped-Design]"));
            Assert.NotNull(cut.Find("[data-testid=unmapped-Inactive]"));
            Assert.NotNull(cut.Find("[data-testid=unmapped-InPlanning]"));

            // Confirm persists the prefilled mapping (a Done row carries both native names, joined).
            cut.Find("[data-testid=mapping-confirm]").Click();
            var saved = config.Get("Board:StatusMapping");
            Assert.NotNull(saved);
            Assert.Contains("Accepted", saved!);
            Assert.Contains("Completed", saved);
            Assert.DoesNotContain("Requested", saved); // unmapped states are not persisted
        }
    }
}
