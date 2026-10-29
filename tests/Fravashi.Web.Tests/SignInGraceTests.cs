using System.Net;
using System.Text;
using Bunit;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Testing.Network;
using Fravashi.Web.Components.Auth;
using Fravashi.Web.Components.Shell;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 234 (plan @64): the sign-in gate admits a person for one hour past the session's expiry while its refresh
/// cannot reach the identity provider, and not a minute more; a refusal is not admitted; the strip and the gate page
/// say so with the instants carried for the browser to show in the person's own time.
/// </summary>
public sealed class SignInGraceTests
{
    private const string Issuer = "https://idp.example.test";
    private const string Access = "access-SECRET-1";
    private const string Refresh = "refresh-SECRET-2";

    private readonly FakeNetworkClock _clock = new();
    private readonly FakeConfigurationStore _settings = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly IdpSessionGrace _grace;
    private readonly DateTimeOffset _expires;
    private HttpStatusCode _tokenStatus = HttpStatusCode.OK;

    public SignInGraceTests()
    {
        _grace = new IdpSessionGrace(_clock);
        _settings.Set(AuthSettings.IdpIssuer, Issuer);
        _settings.Set(AuthSettings.IdpClientId, "fravashi-studio");
        _expires = _clock.GetUtcNow().AddSeconds(30);
        _secrets.Set(AuthSettings.IdpSessionSecretRef, CredentialEnvelope.Serialize(
            new StoredCredential(Access, Refresh, _expires, AuthMethods.AuthCode)));
    }

    private IdpSessionState State() =>
        new(_settings, _secrets, features: null, time: _clock, env: _ => null, grace: _grace);

    private (IdpAuthService Idp, ScriptedHttpHandler Handler) Idp(NetworkScript script)
    {
        var handler = new ScriptedHttpHandler(script, request =>
            request.RequestUri!.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, $$"""{"token_endpoint":"{{Issuer}}/connect/token"}""")
                : Json(_tokenStatus, _tokenStatus == HttpStatusCode.OK
                    ? """{"access_token":""" + "\"fresh-SECRET-3\",\"expires_in\":3600}"
                    : """{"error":"invalid_grant"}"""));
        return (new IdpAuthService(
            new HttpClient(handler), _settings, _secrets, new InMemoryAuthCodeStateStore(),
            new FakeConfigurationReloader(), _clock, _ => null, _grace), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<bool> Admits(IdpSessionState state, IdpAuthService idp) =>
        SignInGate.AdmitsAsync(() => state.Required, () => state.Usable, ct => idp.RefreshIfNeededAsync(ct), CancellationToken.None);

    [Fact]
    public async Task Inside_the_grace_a_network_failed_session_is_admitted_until_59_minutes_and_not_at_60()
    {
        var state = State();
        var (idp, handler) = Idp(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.NameFails)));
        Assert.Equal(IdpRefreshOutcome.Unreachable, (await idp.RefreshAsync(force: true)).Outcome);

        _clock.Advance(_expires + TimeSpan.FromMinutes(59) - _clock.GetUtcNow());
        var calls = handler.Journal.Count;
        Assert.True(await Admits(state, idp));
        // Inside the grace the gate admits without asking the identity provider.
        Assert.Equal(calls, handler.Journal.Count);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await Admits(state, idp));
    }

    [Fact]
    public async Task A_refused_session_is_not_admitted_past_its_expiry()
    {
        _tokenStatus = HttpStatusCode.BadRequest;
        var state = State();
        var (idp, _) = Idp(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)));
        Assert.Equal(IdpRefreshOutcome.Refused, (await idp.RefreshAsync(force: true)).Outcome);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await Admits(state, idp));
    }

    [Fact]
    public async Task Past_the_hour_the_gates_own_refresh_still_renews_once_the_network_is_back_as_before()
    {
        var state = State();
        var (idp, _) = Idp(new NetworkScript(_clock,
            new NetworkPhase(NetworkBehaviour.NoRoute, Calls: 1), new NetworkPhase(NetworkBehaviour.Healthy)));
        await idp.RefreshAsync(force: true);
        _clock.Advance(_expires + TimeSpan.FromMinutes(61) - _clock.GetUtcNow());
        Assert.False(state.Usable);

        Assert.True(await Admits(state, idp));
        // The gate's own refresh renewed it once the backoff had passed.
        Assert.True(state.SignedIn);
        Assert.Null(state.Grace);
    }

    private BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        ctx.Services.AddSingleton<IConfigurationStore>(_settings);
        ctx.Services.AddSingleton<ISecretStore>(_secrets);
        ctx.Services.AddSingleton<IConfigurationReloader>(new FakeConfigurationReloader());
        ctx.Services.AddSingleton(new Fravashi.Web.Services.CircuitConnection());
        ctx.Services.AddSingleton(_grace);
        ctx.Services.AddSingleton(State());
        ctx.Services.AddSingleton(Idp(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.NameFails))).Idp);
        return ctx;
    }

    [Fact]
    public void The_strip_says_since_when_why_and_when_it_locks_and_goes_when_a_refresh_succeeds()
    {
        var since = _clock.GetUtcNow();
        _grace.Unreachable("could not reach idp.example.test: name not resolved");
        _clock.Advance(TimeSpan.FromMinutes(3));
        using var ctx = Context();

        var cut = ctx.Render<SignInGraceStrip>();

        var words = cut.Find("[data-testid='signin-grace-words']").TextContent;
        Assert.StartsWith("Signed in; could not refresh since", words, StringComparison.Ordinal);
        Assert.Contains("(could not reach idp.example.test: name not resolved); locks at", words, StringComparison.Ordinal);
        Assert.Equal(LocalTime.IsoOf(since), cut.Find("[data-testid='signin-grace-since']").GetAttribute("datetime"));
        Assert.Equal(LocalTime.IsoOf(_expires.AddHours(1)), cut.Find("[data-testid='signin-grace-locks']").GetAttribute("datetime"));
        Assert.Equal("2026-10-07T12:00:00Z", cut.Find("[data-testid='signin-grace-since']").GetAttribute("datetime"));
        Assert.Equal("2026-10-07T13:00:30Z", cut.Find("[data-testid='signin-grace-locks']").GetAttribute("datetime"));
        Assert.DoesNotContain(Access, cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(Refresh, cut.Markup, StringComparison.Ordinal);

        cut.InvokeAsync(_grace.Renewed);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='signin-grace-strip']")));
    }

    [Fact]
    public void No_strip_without_an_outage()
    {
        using var ctx = Context();

        var cut = ctx.Render<SignInGraceStrip>();

        Assert.Empty(cut.FindAll("[data-testid='signin-grace-strip']"));
    }

    [Fact]
    public void The_gate_page_says_the_grace_and_offers_the_way_back_while_it_holds()
    {
        _grace.Unreachable("could not reach idp.example.test: no network route");
        _clock.Advance(TimeSpan.FromMinutes(10));
        using var ctx = Context();

        var cut = ctx.Render<SignIn>();

        Assert.Contains("Signed in; could not refresh since", cut.Find("[data-testid='signin-grace']").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid='signin-continue']"));
    }

    /// <summary>
    /// Backlog 282: the way on is a full page load, so the gates answer it in the middleware; an in-app route change
    /// from a lock page is exactly what went past them.
    /// </summary>
    [Fact]
    public void The_way_back_during_the_grace_is_a_full_page_load()
    {
        _grace.Unreachable("could not reach idp.example.test: no network route");
        _clock.Advance(TimeSpan.FromMinutes(10));
        using var ctx = Context();
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = ctx.Render<SignIn>();
        Assert.Empty(nav.History);

        cut.InvokeAsync(() => cut.Find("[data-testid='signin-continue']").Click());

        cut.WaitForAssertion(() =>
        {
            var entry = Assert.Single(nav.History);
            Assert.Equal("/", entry.Uri);
            Assert.True(entry.Options.ForceLoad);
        });
        Assert.DoesNotContain("<a href=\"/\"", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void After_the_hour_the_gate_page_says_why_it_closed()
    {
        _grace.Unreachable("could not reach idp.example.test: no network route");
        _clock.Advance(_expires + TimeSpan.FromHours(1) - _clock.GetUtcNow());
        using var ctx = Context();

        var cut = ctx.Render<SignIn>();

        var words = cut.Find("[data-testid='signin-grace']").TextContent;
        Assert.Contains("Signed out: could not refresh since", words, StringComparison.Ordinal);
        Assert.Contains("the hour's grace ended at", words, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='signin-continue']"));
        Assert.NotNull(cut.Find("[data-testid='signin-start']"));
    }

    [Fact]
    public void A_refusal_is_said_on_the_gate_page()
    {
        _grace.Refuse("The identity provider refused to renew the session (invalid_grant, HTTP 400 from its token endpoint). Sign in again.");
        _clock.Advance(TimeSpan.FromMinutes(1));
        using var ctx = Context();

        var cut = ctx.Render<SignIn>();

        Assert.Contains("invalid_grant", cut.Find("[data-testid='signin-refused']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='signin-grace']"));
    }
}
