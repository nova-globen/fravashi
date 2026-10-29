using Bunit;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Application.Variants;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Web.Components.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Plan Phase 3 + the 2026-08-03 directives: the sign-in gate page. The IdP coordinates ship at
/// compile time (VendorAppDefaults), so a fresh install goes straight to the Sign in button and
/// no user ever types an issuer.
/// </summary>
public class SignInPageTests
{
    private sealed class FakeFeatureFlags(bool authRequired) : IFeatureFlags
    {
        public string Variant => "full";
        public FeatureFlags Flags => new(GitHubLogin: true, VpnRequired: false, BrokerRequired: false, AuthRequired: authRequired);
    }

    private static BunitContext Context(bool signedIn, bool authRequired = true, bool locked = false)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        var settings = new FakeConfigurationStore();
        var secrets = new FakeSecretStore();
        Func<string, string?> env = locked
            ? key => key switch
            {
                IdpCoordinates.IssuerEnv => "https://baked.example.test",
                IdpCoordinates.ClientIdEnv => "baked-client",
                _ => null,
            }
            : _ => null;

        if (signedIn)
        {
            secrets.Set(AuthSettings.IdpSessionSecretRef, CredentialEnvelope.Serialize(
                new StoredCredential("t", "r", DateTimeOffset.UtcNow.AddHours(1), AuthMethods.AuthCode)));
        }

        ctx.Services.AddSingleton<IConfigurationStore>(settings);
        ctx.Services.AddSingleton<ISecretStore>(secrets);
        ctx.Services.AddSingleton<IConfigurationReloader>(new FakeConfigurationReloader());
        ctx.Services.AddSingleton(new Fravashi.Web.Services.CircuitConnection());
        ctx.Services.AddSingleton(new IdpSessionState(settings, secrets, new FakeFeatureFlags(authRequired), env: env));
        ctx.Services.AddSingleton(new IdpAuthService(
            new HttpClient(), settings, secrets, new InMemoryAuthCodeStateStore(),
            new FakeConfigurationReloader(), env: env));
        return ctx;
    }

    [Fact]
    public void A_fresh_install_goes_straight_to_the_sign_in_button()
    {
        // Nothing configured per-install: the SHIPPED coordinates carry the flow, no setup form.
        using var ctx = Context(signedIn: false);

        var cut = ctx.Render<SignIn>();

        Assert.NotNull(cut.Find("[data-testid='signin-start']"));
        Assert.Empty(cut.FindAll("[data-testid='signin-unconfigured']"));
    }

    [Fact]
    public void Baked_byod_coordinates_also_go_straight_to_the_button()
    {
        using var ctx = Context(signedIn: false, locked: true);

        var cut = ctx.Render<SignIn>();

        Assert.NotNull(cut.Find("[data-testid='signin-start']"));
    }

    [Fact]
    public void A_valid_session_leaves_for_the_studio()
    {
        using var ctx = Context(signedIn: true);

        ctx.Render<SignIn>();

        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        Assert.DoesNotContain("sign-in", nav.Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_gate_off_publish_without_explicit_config_leaves_for_the_studio()
    {
        // AUTH_REQUIRED=false and no per-install opt-in: the shipped defaults alone never
        // force the gate, so the page sends itself onward.
        using var ctx = Context(signedIn: false, authRequired: false);

        ctx.Render<SignIn>();

        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        Assert.DoesNotContain("sign-in", nav.Uri, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Backlog item 7: the redirect URI is built from the SERVED base address, so it carries whatever host port
    /// the launcher chose. That is why the launcher may only pick from the ports the identity provider has
    /// registered (LauncherDefaults.StudioPorts, 9090 alone as measured on 2026-09-13): a port the provider
    /// does not know is refused with a redirect-URI mismatch the user cannot fix from inside the app.
    /// </summary>
    private sealed class BaseUriNavigationManager : NavigationManager
    {
        public string? LastUri { get; private set; }

        public BaseUriNavigationManager(string baseUri) => Initialize(baseUri, baseUri + "sign-in");

        protected override void NavigateToCore(string uri, bool forceLoad) => LastUri = uri;
    }

    private sealed class DiscoveryHandler(string issuer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = $$"""
                {"authorization_endpoint":"{{issuer}}/connect/authorize","token_endpoint":"{{issuer}}/connect/token"}
                """;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Theory]
    [InlineData(9090)]
    // A port the provider has no redirect URI for: the page still sends it, which is the whole reason the
    // launcher is not allowed to serve on one.
    [InlineData(41733)]
    public void The_redirect_uri_follows_the_served_port(int port)
    {
        const string issuer = "https://idp.example.test";
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        var settings = new FakeConfigurationStore();
        settings.Set(AuthSettings.IdpIssuer, issuer);
        settings.Set(AuthSettings.IdpClientId, "fravashi-studio");
        var secrets = new FakeSecretStore();
        ctx.Services.AddSingleton<IConfigurationStore>(settings);
        ctx.Services.AddSingleton<ISecretStore>(secrets);
        ctx.Services.AddSingleton<IConfigurationReloader>(new FakeConfigurationReloader());
        ctx.Services.AddSingleton(new Fravashi.Web.Services.CircuitConnection());
        ctx.Services.AddSingleton(new IdpSessionState(settings, secrets, new FakeFeatureFlags(true), env: _ => null));
        ctx.Services.AddSingleton(new IdpAuthService(
            new HttpClient(new DiscoveryHandler(issuer)), settings, secrets, new InMemoryAuthCodeStateStore(),
            new FakeConfigurationReloader(), env: _ => null));
        var nav = new BaseUriNavigationManager($"http://localhost:{port}/");
        ctx.Services.AddSingleton<NavigationManager>(nav);

        var cut = ctx.Render<SignIn>();
        cut.InvokeAsync(() => cut.Find("[data-testid='signin-start']").Click());

        cut.WaitForAssertion(() => Assert.NotNull(nav.LastUri));
        var expected = Uri.EscapeDataString($"http://localhost:{port}/auth/callback/idp");
        Assert.Contains($"redirect_uri={expected}", nav.LastUri);
    }

    [Fact]
    public void The_callbacks_error_text_is_shown()
    {
        using var ctx = Context(signedIn: false);
        ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/sign-in?error=" + Uri.EscapeDataString("The identity provider refused the sign-in: access_denied."));

        var cut = ctx.Render<SignIn>();

        Assert.Contains("access_denied", cut.Find("[data-testid='signin-error']").TextContent);
    }
}
