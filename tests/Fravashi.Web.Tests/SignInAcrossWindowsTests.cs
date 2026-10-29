using Bunit;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Application.Variants;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Web.Components.Auth;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// #372: with several studio windows open, the sign-in belongs to the whole studio. While one window's sign-in runs, every
/// other sign-in page says "Signing in in another window" instead of offering a second Sign in button, and when it
/// completes they all go on to the studio by themselves. The going-on is a call to the browser, which a window that is
/// disconnected at that moment never receives (CLAUDE.md, the reconnect rule), so it is sent again on reconnection.
/// </summary>
public sealed class SignInAcrossWindowsTests : IDisposable
{
    private readonly FakeConfigurationStore _settings = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly IdpSignInCoordinator _signIns = new();
    private readonly CircuitConnection _connection = new();
    private readonly RecordingNavigationManager _nav = new();
    private readonly BunitContext _ctx = new();

    public SignInAcrossWindowsTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddFluentUIComponents();
        _ctx.Services.AddSingleton<IConfigurationStore>(_settings);
        _ctx.Services.AddSingleton<ISecretStore>(_secrets);
        _ctx.Services.AddSingleton<IConfigurationReloader>(new FakeConfigurationReloader());
        _ctx.Services.AddSingleton(new IdpSessionState(_settings, _secrets, new AuthRequired(), env: _ => null));
        _ctx.Services.AddSingleton(new IdpAuthService(
            new HttpClient(), _settings, _secrets, new InMemoryAuthCodeStateStore(), new FakeConfigurationReloader(),
            env: _ => null, signIns: _signIns));
        _ctx.Services.AddSingleton(_connection);
        _ctx.Services.AddSingleton<NavigationManager>(_nav);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void While_another_windows_sign_in_runs_the_page_says_so_and_offers_no_second_sign_in_button()
    {
        var cut = _ctx.Render<SignIn>();
        Assert.NotEmpty(cut.FindAll("[data-testid='signin-start']"));

        cut.InvokeAsync(() => _signIns.Begin("state-of-the-other-window", origin: "the-other-window"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Signing in in another window", cut.Find("[data-testid='signin-elsewhere']").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid='signin-start']"));
            // The deliberate way past an attempt that was abandoned at the identity provider's page.
            Assert.NotEmpty(cut.FindAll("[data-testid='signin-here']"));
        });
    }

    [Fact]
    public void A_page_opened_while_another_windows_sign_in_runs_says_so_from_the_start()
    {
        _signIns.Begin("state-of-the-other-window", origin: "the-other-window");

        var cut = _ctx.Render<SignIn>();

        Assert.NotEmpty(cut.FindAll("[data-testid='signin-elsewhere']"));
        Assert.Empty(cut.FindAll("[data-testid='signin-start']"));
    }

    [Fact]
    public void An_attempt_that_ends_without_signing_in_gives_the_sign_in_button_back()
    {
        _signIns.Begin("state-of-the-other-window", origin: "the-other-window");
        var cut = _ctx.Render<SignIn>();

        cut.InvokeAsync(() => CompleteAsync("state-of-the-other-window", signsIn: false));

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid='signin-start']"));
            Assert.Empty(cut.FindAll("[data-testid='signin-elsewhere']"));
        });
        Assert.Empty(_nav.Sent);
    }

    [Fact]
    public void A_sign_in_completed_in_another_window_sends_this_one_on_to_the_studio()
    {
        _signIns.Begin("state-of-the-other-window", origin: "the-other-window");
        var cut = _ctx.Render<SignIn>();

        cut.InvokeAsync(() => CompleteAsync("state-of-the-other-window", signsIn: true));

        cut.WaitForAssertion(() => Assert.Contains(("/", true), _nav.Sent));
    }

    [Fact]
    public void A_window_disconnected_when_the_sign_in_completes_is_sent_on_again_once_it_reconnects()
    {
        _signIns.Begin("state-of-the-other-window", origin: "the-other-window");
        var cut = _ctx.Render<SignIn>();
        _connection.ConnectionDown();

        cut.InvokeAsync(() => CompleteAsync("state-of-the-other-window", signsIn: true));
        cut.WaitForAssertion(() => Assert.Single(_nav.Sent));

        _connection.ConnectionUp();

        cut.WaitForAssertion(() => Assert.Equal(2, _nav.Sent.Count(sent => sent == ("/", true))));
    }

    [Fact]
    public void A_reconnection_while_still_signed_out_sends_the_window_nowhere()
    {
        var cut = _ctx.Render<SignIn>();
        _connection.ConnectionDown();

        _connection.ConnectionUp();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='signin-start']")));
        Assert.Empty(_nav.Sent);
    }

    [Fact]
    public void A_page_that_is_gone_hears_nothing_more()
    {
        var cut = _ctx.Render<SignIn>();
        cut.Instance.Dispose();

        _secrets.Set(AuthSettings.IdpSessionSecretRef, Session());
        _connection.ConnectionUp();

        Assert.Empty(_nav.Sent);
    }

    /// <summary>The callback handled for <paramref name="state"/>, through the studio's one sign-in, as the endpoint drives it.</summary>
    private Task<AuthCodeExchange> CompleteAsync(string state, bool signsIn) =>
        _signIns.HandleOnceAsync(
            state,
            "code-1",
            "window",
            consume: issued => new AuthCodeStateEntry(
                issued, IdpAuthService.ProviderTag, "idp", "idp", AuthSettings.IdpSessionSecretRef, null,
                "http://localhost:9090/auth/callback/idp", "gate", DateTimeOffset.UtcNow),
            exchange: (_, _) =>
            {
                if (!signsIn)
                {
                    return Task.FromResult(AuthCodeExchange.Fail("The identity provider refused the sign-in: access_denied."));
                }

                _secrets.Set(AuthSettings.IdpSessionSecretRef, Session());
                return Task.FromResult(AuthCodeExchange.Ok(
                    new StoredCredential("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), AuthMethods.AuthCode), "Signed in."));
            });

    private static string Session() => CredentialEnvelope.Serialize(
        new StoredCredential("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), AuthMethods.AuthCode));

    private sealed class AuthRequired : IFeatureFlags
    {
        public string Variant => "full";

        public FeatureFlags Flags => new(GitHubLogin: true, VpnRequired: false, BrokerRequired: false, AuthRequired: true);
    }

    /// <summary>Records every navigation the page sends to the browser.</summary>
    private sealed class RecordingNavigationManager : NavigationManager
    {
        private readonly List<(string Uri, bool ForceLoad)> _sent = [];

        public RecordingNavigationManager() => Initialize("http://localhost:9090/", "http://localhost:9090/sign-in");

        public IReadOnlyList<(string Uri, bool ForceLoad)> Sent
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent];
                }
            }
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            lock (_sent)
            {
                _sent.Add((uri, forceLoad));
            }
        }
    }
}
