using Bunit;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Web.Components.Onboarding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Covers the Settings "Connections" panel readiness signal (S17 + coordinate projection): "ready to use" is
/// shown ONLY when the token AND every coordinate the adapter needs are present - otherwise it names what is
/// still missing, so the card never claims a partially-configured connection is live.
/// </summary>
public class OnboardingConnectionsPanelTests
{
    private static BunitContext PanelContext(out FakeConfigurationStore config, out FakeProviderConnectionStore connections)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        config = new FakeConfigurationStore();
        connections = new FakeProviderConnectionStore();
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IProviderConnectionStore>(connections);
        ctx.Services.AddSingleton<IAgentRuntimeService>(new FakeAgentRuntimeService());
        ctx.Services.AddSingleton<IAgentRuntimeStatusReport>(new InMemoryAgentRuntimeStatusReport());
        return ctx;
    }

    [Fact]
    public void FullyConfiguredJiraBoard_ShowsReadyToUse()
    {
        using var ctx = PanelContext(out var config, out var connections);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        config.Set("Onboarding:Board:Email", "dev@example.com");
        config.Set("Onboarding:Board:Project", "ENG");
        connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var cut = ctx.Render<OnboardingConnectionsPanel>();

        var chip = cut.Find("[data-testid=settings-conn-ready-board]");
        Assert.Contains("ready to use", chip.TextContent);
        Assert.Contains("ok", chip.GetAttribute("class"));
    }

    [Fact]
    public void JiraBoardMissingProject_ShowsNeedsProjectKey_NotReady()
    {
        using var ctx = PanelContext(out var config, out var connections);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        config.Set("Onboarding:Board:Email", "dev@example.com");
        // No project key committed.
        connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var cut = ctx.Render<OnboardingConnectionsPanel>();

        var chip = cut.Find("[data-testid=settings-conn-ready-board]");
        Assert.Contains("needs a project key", chip.TextContent);
        Assert.Contains("warn", chip.GetAttribute("class"));
    }

    [Fact]
    public void JiraBoardWithoutToken_ShowsNeedsToken_NotReady()
    {
        using var ctx = PanelContext(out var config, out var connections);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        config.Set("Onboarding:Board:Email", "dev@example.com");
        config.Set("Onboarding:Board:Project", "ENG");
        // A verified-looking record but no stored credential (CredentialRef null).
        connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", CredentialRef: null, "Recorded", DateTimeOffset.UtcNow));

        var cut = ctx.Render<OnboardingConnectionsPanel>();

        var chip = cut.Find("[data-testid=settings-conn-ready-board]");
        Assert.Contains("needs a token", chip.TextContent);
    }

    [Fact]
    public void ThreeLeggedJiraBoard_WithCloudId_ShowsReadyToUse_WithoutEmail()
    {
        using var ctx = PanelContext(out var config, out var connections);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        config.Set("Onboarding:Board:Project", "ENG");
        // A 3LO sign-in: auth method authcode, cloudId captured, and NO account email (the Bearer token carries it).
        config.Set(AuthSettings.AuthMethodKey("board"), AuthMethods.AuthCode);
        config.Set("Onboarding:board:CloudId", "cloud-abc-123");
        connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Signed in", DateTimeOffset.UtcNow));

        var cut = ctx.Render<OnboardingConnectionsPanel>();

        var chip = cut.Find("[data-testid=settings-conn-ready-board]");
        Assert.Contains("ready to use", chip.TextContent);
        Assert.Contains("ok", chip.GetAttribute("class"));
    }

    [Fact]
    public void ThreeLeggedJiraBoard_MissingCloudId_ShowsReSignIn_NotReady()
    {
        using var ctx = PanelContext(out var config, out var connections);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        config.Set("Onboarding:Board:Project", "ENG");
        // An older 3LO sign-in that predates cloudId capture: authcode, token present, but no cloudId.
        config.Set(AuthSettings.AuthMethodKey("board"), AuthMethods.AuthCode);
        connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Signed in", DateTimeOffset.UtcNow));

        var cut = ctx.Render<OnboardingConnectionsPanel>();

        var chip = cut.Find("[data-testid=settings-conn-ready-board]");
        Assert.Contains("re-sign-in to finish", chip.TextContent);
        Assert.Contains("warn", chip.GetAttribute("class"));
    }

    [Fact]
    public void ThreeLeggedConfluenceWiki_WithCloudId_ShowsReadyToUse()
    {
        using var ctx = PanelContext(out var config, out var connections);
        config.Set("Onboarding:Wiki:Provider", OnboardingProviders.Confluence);
        config.Set(AuthSettings.AuthMethodKey("wiki"), AuthMethods.AuthCode);
        config.Set("Onboarding:wiki:CloudId", "cloud-wiki-9");
        connections.Upsert(new ProviderConnection(
            "wiki", ProviderKind.Wiki, "https://docs.atlassian.net", "wiki:token", "Signed in", DateTimeOffset.UtcNow));

        var cut = ctx.Render<OnboardingConnectionsPanel>();

        var chip = cut.Find("[data-testid=settings-conn-ready-wiki]");
        Assert.Contains("ready to use", chip.TextContent);
    }

    [Fact]
    public void LocalBoard_ShowsNoReadinessChip()
    {
        using var ctx = PanelContext(out var config, out var connections);
        config.Set("Onboarding:Board:Provider", OnboardingProviders.Local);
        connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, null, null, "Local", DateTimeOffset.UtcNow));

        var cut = ctx.Render<OnboardingConnectionsPanel>();

        // A local board has no external adapter to be "ready" - no readiness chip is rendered.
        Assert.Empty(cut.FindAll("[data-testid=settings-conn-ready-board]"));
    }
}
