using Bunit;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Web.Components.Onboarding;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

public class OnboardingMiscStepTests
{
    private static BunitContext BaseContext(out FakeConfigurationStore config)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        config = new FakeConfigurationStore();
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IProviderConnectionStore>(new FakeProviderConnectionStore());
        ctx.Services.AddSingleton<ISecretStore>(new FakeSecretStore());
        ctx.Services.AddSingleton<IBoardStatesProbe>(new FakeBoardStatesProbe(BoardStatesResult.Failed("no states")));
        ctx.Services.AddSingleton<Application.AgentRuntimes.IAgentRuntimeService>(new FakeAgentRuntimeService());
        ctx.Services.AddSingleton<Application.AgentRuntimes.IBoardMappingSuggester>(
            new FakeBoardMappingSuggester(Application.AgentRuntimes.BoardMappingSuggestion.Failed("no runtime")));
        ctx.Services.AddScoped<OnboardingDraft>();
        ctx.Services.AddProviderSignInForTests();
        return ctx;
    }

    [Fact]
    public void Data_location_step_shows_the_resolved_path_and_the_never_lose_it_warning()
    {
        using var ctx = BaseContext(out _);
        var dir = Path.Combine(Path.GetTempPath(), "fravashi-test-" + Guid.NewGuid().ToString("N"));
        ctx.Services.AddSingleton(new DataDirectory(dir));

        var cut = ctx.Render<DataLocationStep>();

        Assert.Contains("must never be lost", cut.Markup);
        Assert.Contains(dir, cut.Find("[data-testid=data-directory]").TextContent);
    }

    [Fact]
    public void Board_mapping_step_lists_all_seven_canonical_statuses_and_persists_on_confirm()
    {
        using var ctx = BaseContext(out var config);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.AzureDevOps);

        var cut = ctx.Render<BoardMappingStep>();

        var rows = cut.FindAll("[data-testid=mapping-table] tbody tr");
        Assert.Equal(BoardStatusMapping.Catalog.Count, rows.Count);
        Assert.Contains("Triage", cut.Markup);
        Assert.Contains("Cancelled", cut.Markup);

        cut.Find("[data-testid=mapping-confirm]").Click();

        var saved = config.Get("Board:StatusMapping");
        Assert.NotNull(saved);
        Assert.Contains("Triage", saved!);
        Assert.Contains("In Review", saved);
    }

    [Fact]
    public void Review_step_summarises_stored_connections_and_warns_when_a_restart_is_due()
    {
        using var ctx = BaseContext(out var config);
        var connections = new FakeProviderConnectionStore();
        connections.Upsert(new ProviderConnection("board", ProviderKind.Board, "https://dev.azure.com/org", "board:token", "Verified", DateTimeOffset.UtcNow));
        ctx.Services.AddSingleton<IProviderConnectionStore>(connections);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.AzureDevOps);
        config.Set("Tracker:Provider", OnboardingProviders.AzureDevOps);

        var cut = ctx.Render<ReviewStep>(p => p.Add(c => c.InitialTrackerProvider, null));

        Assert.Contains("Verified", cut.Find("[data-testid=review-table]").TextContent);
        // Tracker:Provider changed from null → AzureDevOps, so the restart warning is shown.
        cut.Find("[data-testid=review-restart]");
    }

    [Fact]
    public void Review_step_shows_no_restart_when_the_tracker_provider_is_unchanged()
    {
        using var ctx = BaseContext(out var config);
        ctx.Services.AddSingleton<IProviderConnectionStore>(new FakeProviderConnectionStore());

        var cut = ctx.Render<ReviewStep>(p => p.Add(c => c.InitialTrackerProvider, null));

        Assert.Empty(cut.FindAll("[data-testid=review-restart]"));
    }
}
