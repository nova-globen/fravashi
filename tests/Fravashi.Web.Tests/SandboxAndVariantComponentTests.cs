using Bunit;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sandbox;
using Fravashi.Application.Variants;
using Fravashi.Application.Vpn;
using Fravashi.Web.Components.Onboarding;
using Fravashi.Web.Components.Settings;
using Fravashi.Web.Components.Vpn;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// F4 / I1 surfaces: the honest sandbox-posture view (active vs unavailable per runtime, never a false green),
/// the VPN-required startup gate (path policy + waiting page), and the GITHUB_LOGIN variant switch honestly
/// hiding the GitHub SSO button.
/// </summary>
public sealed class SandboxAndVariantComponentTests
{
    // ---- sandbox posture view (F4) ----

    private static BunitContext PostureContext(SandboxPosture posture)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        ctx.Services.AddSingleton<ISandboxPosture>(new FakeSandboxPosture(posture));
        return ctx;
    }

    private static SandboxControl Active(string id, string title) =>
        new(id, title, "desc", SandboxProtectionStatus.Active, "applied");

    private static SandboxControl Unavailable(string id, string title) =>
        new(id, title, "desc", SandboxProtectionStatus.Unavailable, "Not supported by the wslc runtime - use Docker or Podman.");

    [Fact]
    public void Posture_view_shows_active_and_unavailable_protections_honestly_for_wslc()
    {
        var posture = new SandboxPosture(
            "wslc",
            [Active("LoopbackPublish", "Loopback-only port"), Unavailable("ReadOnlyRootfs", "Read-only root filesystem")],
            StrongerRuntimeRecommended: true,
            Recommendation: "For the strongest isolation, run Fravashi under Docker or Podman.",
            CheckedUtc: DateTimeOffset.UtcNow);

        var cut = PostureContext(posture).Render<SandboxPostureCard>();

        Assert.Equal("wslc", cut.Find("[data-testid=sandbox-posture-runtime]").TextContent);
        Assert.Equal("active", cut.Find("[data-testid=sandbox-control-LoopbackPublish]").GetAttribute("data-status"));
        Assert.Equal("unavailable", cut.Find("[data-testid=sandbox-control-ReadOnlyRootfs]").GetAttribute("data-status"));
        // The honest recommendation to use a stronger runtime is surfaced.
        Assert.Contains("Docker or Podman", cut.Find("[data-testid=sandbox-posture-recommend]").TextContent);
    }

    [Fact]
    public void Posture_view_for_docker_shows_no_stronger_runtime_recommendation()
    {
        var posture = new SandboxPosture(
            "docker",
            [Active("ReadOnlyRootfs", "Read-only root filesystem"), Active("DropAllCapabilities", "Dropped capabilities")],
            StrongerRuntimeRecommended: false,
            Recommendation: null,
            CheckedUtc: DateTimeOffset.UtcNow);

        var cut = PostureContext(posture).Render<SandboxPostureCard>();

        Assert.Equal("docker", cut.Find("[data-testid=sandbox-posture-runtime]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=sandbox-posture-recommend]"));
    }

    [Fact]
    public void Each_control_shows_its_detail_as_its_own_element_apart_from_its_description()
    {
        // Backlog 188: in the installed v0.9.50 window the detail ran straight into the description
        // ("...can reach it.127.0.0.1"). Both controls are checked, one Active and one Unavailable.
        var posture = new SandboxPosture(
            "wslc",
            [
                new SandboxControl("LoopbackPublish", "Loopback-only port",
                    "so nothing on the local network can reach it.", SandboxProtectionStatus.Active, "127.0.0.1"),
                new SandboxControl("ReadOnlyRootfs", "Read-only root filesystem",
                    "The image cannot be changed while it runs.", SandboxProtectionStatus.Unavailable,
                    "use Docker or Podman for this protection."),
            ],
            StrongerRuntimeRecommended: false,
            Recommendation: null,
            CheckedUtc: DateTimeOffset.UtcNow);

        var cut = PostureContext(posture).Render<SandboxPostureCard>();

        foreach (var (id, description, detail) in new[]
                 {
                     ("LoopbackPublish", "so nothing on the local network can reach it.", "127.0.0.1"),
                     ("ReadOnlyRootfs", "The image cannot be changed while it runs.", "use Docker or Podman for this protection."),
                 })
        {
            var desc = cut.Find($"[data-testid=sandbox-control-{id}-desc]");
            var det = cut.Find($"[data-testid=sandbox-control-{id}-detail]");

            Assert.NotSame(desc, det);
            Assert.Equal(description, desc.TextContent.Trim());
            Assert.Equal(detail, det.TextContent.Trim());
            Assert.False(desc.Contains(det), "the detail must not sit inside the description");
            Assert.Contains("settings-posture-detail", det.ClassList);
        }
    }

    [Theory]
    [InlineData(".settings-posture-desc")]
    [InlineData(".settings-posture-detail")]
    [InlineData(".settings-posture-title")]
    public void The_posture_body_parts_each_take_their_own_line_in_the_stylesheet(string selector)
    {
        // The parts are spans, so it is the stylesheet that keeps them apart, and bunit has no layout.
        // Backlog 188's defect was these rules being absent altogether. Guards the rule, not the pixels.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "the test must be able to locate the repository root (Fravashi.sln)");
        var css = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(dir!.FullName, "src", "Fravashi.Web", "wwwroot", "studio.css")),
            @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

        var blockRule = System.Text.RegularExpressions.Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .Any(m => m.Groups["selector"].Value.Split(',').Any(s => s.Trim() == selector)
                      && System.Text.RegularExpressions.Regex.IsMatch(m.Groups["body"].Value, @"display\s*:\s*block"));

        Assert.True(blockRule, $"studio.css must give {selector} its own line (display: block)");
    }

    [Fact]
    public void Posture_view_running_outside_a_container_is_honest_about_it()
    {
        var cut = PostureContext(SandboxPosture.OutsideContainer).Render<SandboxPostureCard>();
        Assert.NotNull(cut.Find("[data-testid=sandbox-posture-outside]"));
    }

    // ---- VPN-required gate (F5 / I1) ----

    [Fact]
    public void VpnGate_redirects_pages_but_exempts_liveness_and_the_gate_itself()
    {
        Assert.True(VpnGate.IsExempt("/secure-connection"));
        Assert.True(VpnGate.IsExempt("/healthz"));
        Assert.True(VpnGate.IsExempt("/_blazor"));
        // Backlog 283: no path is exempt for looking like a file; StudioGates lets a real static asset through.
        Assert.False(VpnGate.IsExempt("/app.css"));
        Assert.False(VpnGate.IsExempt("/code/README.md"));
        Assert.True(VpnGate.IsExempt("/Error"));
        Assert.True(VpnGate.IsExempt("/Error/"));
        Assert.False(VpnGate.IsExempt("/"));
        Assert.False(VpnGate.IsExempt("/board"));
    }

    [Fact]
    public void Secure_connection_page_shows_the_variant_and_the_waiting_status()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IFeatureFlags>(new FakeFeatureFlags("corp-vpn", new FeatureFlags(GitHubLogin: false, VpnRequired: true)));
        ctx.Services.AddSingleton<IVpnConnectivity>(new UnconfiguredVpnConnectivity());

        var cut = ctx.Render<SecureConnection>();

        Assert.Equal("corp-vpn", cut.Find("[data-testid=vpn-gate-variant]").TextContent);
        Assert.Equal("Yes", cut.Find("[data-testid=vpn-gate-required]").TextContent);
        Assert.Equal("Not connected", cut.Find("[data-testid=vpn-gate-status]").TextContent);
    }

    [Fact]
    public void Secure_connection_page_repolls_while_the_tunnel_is_down()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IFeatureFlags>(new FakeFeatureFlags("corp-vpn", new FeatureFlags(GitHubLogin: false, VpnRequired: true)));
        ctx.Services.AddSingleton<IVpnConnectivity>(new FakeVpnConnectivity(false, "Waiting for the tunnel…"));

        var cut = ctx.Render<SecureConnection>();

        // While disconnected the gate re-polls so it clears automatically once the tunnel comes up (VPN6). A meta
        // refresh since backlog 287, because the Content-Security-Policy runs no inline script.
        var refresh = cut.Find("[data-testid=vpn-gate-refresh]");
        Assert.Equal("refresh", refresh.GetAttribute("http-equiv"));
        Assert.Equal("5", refresh.GetAttribute("content"));
        Assert.DoesNotContain("<script", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Not connected", cut.Find("[data-testid=vpn-gate-status]").TextContent);
    }

    [Fact]
    public void Secure_connection_page_redirects_to_the_studio_once_connected()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IFeatureFlags>(new FakeFeatureFlags("corp-vpn", new FeatureFlags(GitHubLogin: false, VpnRequired: true)));
        ctx.Services.AddSingleton<IVpnConnectivity>(new FakeVpnConnectivity(true, "Connected - egress via 203.0.113.7."));

        ctx.Render<SecureConnection>();

        // Once the sidecar tunnel is confirmed the gate sends the studio onward (the gate middleware then lets "/" through).
        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Assert.EndsWith("/", nav.Uri);
    }

    private sealed class FakeVpnConnectivity(bool connected, string detail) : IVpnConnectivity
    {
        public VpnConnectivityState Current { get; } = new(connected, detail);
    }

    // ---- GITHUB_LOGIN variant switch (I1) ----

    [Fact]
    public void GitHub_login_off_hides_the_sso_button_and_offers_token_only()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        ctx.Services.AddSingleton<IConfigurationStore>(new FakeConfigurationStore());
        ctx.Services.AddSingleton<IProviderConnectionStore>(new FakeProviderConnectionStore());
        ctx.Services.AddSingleton<ISecretStore>(new FakeSecretStore());
        ctx.Services.AddScoped<OnboardingDraft>();
        // Register the locked-down flags BEFORE the test-services helper (its TryAdd will not overwrite).
        ctx.Services.AddSingleton<IFeatureFlags>(new FakeFeatureFlags("corp-vpn", new FeatureFlags(GitHubLogin: false, VpnRequired: true)));
        ctx.Services.AddProviderSignInForTests();

        var cut = ctx.Render<ProviderSignIn>(p => p
            .Add(x => x.Area, "source")
            .Add(x => x.Provider, OnboardingProviders.GitHub)
            .Add(x => x.ConnectionId, "source")
            .Add(x => x.CredentialRef, "source:git")
            .Add(x => x.UseSso, true));

        // The honest "SSO off in this build" note is shown, and the SSO start button is gone.
        Assert.NotNull(cut.Find("[data-testid=source-signin-variant-off]"));
        Assert.Empty(cut.FindAll("[data-testid=source-signin-start]"));
        Assert.Empty(cut.FindAll("[data-testid=source-method-sso]"));
    }

    [Fact]
    public void GitHub_login_on_still_shows_the_sso_button()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        ctx.Services.AddSingleton<IConfigurationStore>(new FakeConfigurationStore());
        ctx.Services.AddSingleton<IProviderConnectionStore>(new FakeProviderConnectionStore());
        ctx.Services.AddSingleton<ISecretStore>(new FakeSecretStore());
        ctx.Services.AddScoped<OnboardingDraft>();
        ctx.Services.AddProviderSignInForTests(); // default: full variant, GitHub on

        var cut = ctx.Render<ProviderSignIn>(p => p
            .Add(x => x.Area, "source")
            .Add(x => x.Provider, OnboardingProviders.GitHub)
            .Add(x => x.ConnectionId, "source")
            .Add(x => x.CredentialRef, "source:git")
            .Add(x => x.UseSso, true));

        Assert.Empty(cut.FindAll("[data-testid=source-signin-variant-off]"));
        Assert.NotNull(cut.Find("[data-testid=source-signin-start]"));
    }

    private sealed record FakeSandboxPosture(SandboxPosture Value) : ISandboxPosture
    {
        public SandboxPosture Current => Value;
    }
}
