using Bunit;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Web.Components.Onboarding;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The provider sign-in component's honesty rules (ADR 0004): the OAuth apps ship with the product, so a user
/// never registers one. When a provider's app is not registered in this build the sign-in button is visibly
/// disabled with an honest "not available in this build yet - use an access token" reason (never a dead button
/// and never a "register an app" instruction); when its client id is present the button is enabled and reads as
/// granting Fravashi access. Atlassian is gated behind an installation's own app registration. The access-token
/// (PAT) fallback is always reachable via the method toggle.
/// </summary>
public sealed class OnboardingSignInTests
{
    private static BunitContext NewContext(out FakeConfigurationStore config)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        config = new FakeConfigurationStore();
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IProviderConnectionStore>(new FakeProviderConnectionStore());
        ctx.Services.AddSingleton<ISecretStore>(new FakeSecretStore());
        ctx.Services.AddScoped<OnboardingDraft>();
        ctx.Services.AddProviderSignInForTests();
        return ctx;
    }

    private static IRenderedComponent<ProviderSignIn> RenderFor(BunitContext ctx, string provider, string area = "source") =>
        ctx.Render<ProviderSignIn>(p => p
            .Add(x => x.Area, area)
            .Add(x => x.Provider, provider)
            .Add(x => x.ConnectionId, area)
            .Add(x => x.CredentialRef, $"{area}:git")
            .Add(x => x.UseSso, true));

    [Fact]
    public void Without_a_registered_app_the_sign_in_button_is_disabled_with_the_honest_build_reason()
    {
        using var ctx = NewContext(out _);
        // Both device-code vendor apps now ship a baked-in client id (ADR 0004), so no shipped provider renders
        // the disabled state through the real store. The honest "not available in this build yet" state is
        // modelled with an unconfigured store double - a device-code config whose client id is blanked to null.
        ctx.Services.AddSingleton<IAuthConfigStore>(new FakeAuthConfigStore(
            new ProviderAuthConfig(OnboardingProviders.AzureDevOps, AuthStrategy.DeviceCode, ClientId: null, HasSecret: false)));
        var cut = RenderFor(ctx, OnboardingProviders.AzureDevOps);

        var button = cut.Find("[data-testid=source-signin-disabled]");
        Assert.True(button.HasAttribute("disabled"));
        var reason = cut.Find("[data-testid=source-signin-reason]").TextContent;
        Assert.Contains("isn't available in this build yet", reason);
        // Never tells the user to register an app - that is the vendor's job (ADR 0004).
        Assert.DoesNotContain("register", reason, System.StringComparison.OrdinalIgnoreCase);
        // The access-token fallback is always offered.
        Assert.NotNull(cut.Find("[data-testid=source-method-pat]"));
    }

    [Fact]
    public void The_shipped_github_default_enables_the_sign_in_button_with_the_grant_access_copy()
    {
        // No per-install override set: the button is enabled purely by the baked-in VendorAppDefaults client id,
        // resolved through the real AuthConfigStore. This proves the shipped GitHub registration reaches the UI.
        using var ctx = NewContext(out _);
        var cut = RenderFor(ctx, OnboardingProviders.GitHub);

        Assert.Empty(cut.FindAll("[data-testid=source-signin-disabled]"));
        Assert.Contains("Sign in with GitHub", cut.Find("[data-testid=source-signin-start]").TextContent);
        Assert.Contains("grant Fravashi access", cut.Find("[data-testid=source-signin-grant]").TextContent);
    }

    [Fact]
    public void The_shipped_azure_devops_default_enables_the_sign_in_button_with_the_grant_access_copy()
    {
        // No per-install override set: the Azure DevOps (Entra) button is enabled purely by the baked-in
        // VendorAppDefaults client id, resolved through the real AuthConfigStore. This proves the shipped Entra
        // registration reaches the UI (ADR 0004).
        using var ctx = NewContext(out _);
        var cut = RenderFor(ctx, OnboardingProviders.AzureDevOps);

        Assert.Empty(cut.FindAll("[data-testid=source-signin-disabled]"));
        Assert.Contains("Sign in with Azure DevOps", cut.Find("[data-testid=source-signin-start]").TextContent);
        Assert.Contains("grant Fravashi access", cut.Find("[data-testid=source-signin-grant]").TextContent);
    }

    [Fact]
    public void A_per_install_client_id_override_also_enables_the_sign_in_button()
    {
        using var ctx = NewContext(out var config);
        config.Set(AuthSettings.GitHubClientId, "gh-client");

        var cut = RenderFor(ctx, OnboardingProviders.GitHub);

        Assert.Empty(cut.FindAll("[data-testid=source-signin-disabled]"));
        Assert.Contains("Sign in with GitHub", cut.Find("[data-testid=source-signin-start]").TextContent);
        Assert.Contains("grant Fravashi access", cut.Find("[data-testid=source-signin-grant]").TextContent);
    }

    [Fact]
    public void Atlassian_sign_in_is_gated_behind_an_installations_own_app_registration()
    {
        using var ctx = NewContext(out _);
        var cut = RenderFor(ctx, OnboardingProviders.Jira, area: "board");

        var button = cut.Find("[data-testid=board-signin-disabled]");
        Assert.True(button.HasAttribute("disabled"));
        var reason = cut.Find("[data-testid=board-signin-reason]").TextContent;
        Assert.Contains("own app registration", reason);
        Assert.Contains("API token", reason);
        Assert.NotNull(cut.Find("[data-testid=board-method-pat]"));
    }

    [Fact]
    public void The_wizard_no_longer_carries_an_inline_app_registration_panel()
    {
        using var ctx = NewContext(out _);
        var cut = RenderFor(ctx, OnboardingProviders.GitHub);

        // App registration is not the user's job (ADR 0004): the inline "Configure sign-in" panel is gone.
        Assert.Empty(cut.FindAll("[data-testid=source-signin-config]"));
        Assert.Empty(cut.FindAll("[data-testid=source-configure-toggle]"));
        Assert.Empty(cut.FindAll("[data-testid=source-cfg-clientid]"));
    }
}
