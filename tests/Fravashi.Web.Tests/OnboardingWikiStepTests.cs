using Bunit;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Web.Components.Onboarding;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

public class OnboardingWikiStepTests
{
    private static BunitContext NewContext(
        IWikiAccessProbe probe,
        out FakeProviderConnectionStore connections,
        out FakeConfigurationStore config)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        connections = new FakeProviderConnectionStore();
        config = new FakeConfigurationStore();
        ctx.Services.AddSingleton(probe);
        ctx.Services.AddSingleton<IProviderConnectionStore>(connections);
        ctx.Services.AddSingleton<ISecretStore>(new FakeSecretStore());
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddScoped<OnboardingDraft>();
        ctx.Services.AddProviderSignInForTests();
        return ctx;
    }

    [Fact]
    public void External_wiki_shows_read_verified_and_write_recorded_not_verified()
    {
        var result = new AccessProbeResult(
        [
            new AccessCheck("Read", AccessProbeOutcome.Passed, "Confluence GET /wiki/api/v2/spaces", "ok"),
            new AccessCheck("Write", AccessProbeOutcome.NotVerifiable, "no non-destructive Confluence write probe", "deferred"),
        ]);
        using var ctx = NewContext(new FakeWikiAccessProbe(result), out _, out _);
        var cut = ctx.Render<WikiStep>();

        cut.Find("[data-testid=wiki-test]").Click();

        Assert.Contains("✓ Verified", cut.Markup);
        Assert.Contains("Recorded, not verified", cut.Markup);
        // The O9 sync policy is always stated.
        Assert.Contains("authoritative", cut.Markup);
    }

    [Fact]
    public void Confluence_shows_an_optional_space_key_field_and_passes_it_to_the_probe()
    {
        var probe = new FakeWikiAccessProbe(new AccessProbeResult(
        [
            new AccessCheck("Read", AccessProbeOutcome.Passed, "Confluence GET /wiki/api/v2/spaces", "ok"),
            new AccessCheck("Write", AccessProbeOutcome.Passed, "Confluence POST + PUT /wiki/api/v2/pages", "round-trip"),
            new AccessCheck("Delete", AccessProbeOutcome.Passed, "Confluence DELETE /wiki/api/v2/pages/{id}", "moved to trash"),
        ]));
        using var ctx = NewContext(probe, out _, out _);
        var cut = ctx.Render<WikiStep>();

        // The helper text makes the empty-key behaviour explicit.
        var help = cut.Find("[data-testid=wiki-spacekey-help]").TextContent;
        Assert.Contains("cannot be tested", help, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not verified", help, System.StringComparison.OrdinalIgnoreCase);

        cut.Find("[data-testid=wiki-spacekey]").Change("DOCS");
        cut.Find("[data-testid=wiki-test]").Click();

        Assert.Equal("DOCS", probe.LastRequest!.SpaceKey);
    }

    [Fact]
    public void Confluence_round_trip_renders_write_and_delete_verified()
    {
        var probe = new FakeWikiAccessProbe(new AccessProbeResult(
        [
            new AccessCheck("Read", AccessProbeOutcome.Passed, "Confluence GET /wiki/api/v2/spaces", "ok"),
            new AccessCheck("Write", AccessProbeOutcome.Passed, "Confluence POST + PUT /wiki/api/v2/pages", "Created and updated a probe page (version bumped to 2)."),
            new AccessCheck("Delete", AccessProbeOutcome.Passed, "Confluence DELETE /wiki/api/v2/pages/{id}", "Deleted the probe page (moved to trash)."),
        ]));
        using var ctx = NewContext(probe, out _, out _);
        var cut = ctx.Render<WikiStep>();

        cut.Find("[data-testid=wiki-test]").Click();

        // Three real verifications render, and the trash wording is surfaced honestly.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(cut.Markup, "✓ Verified").Count);
        Assert.Contains("moved to trash", cut.Markup);
    }

    [Fact]
    public void Confluence_leftover_probe_page_is_reported_when_delete_fails()
    {
        var probe = new FakeWikiAccessProbe(new AccessProbeResult(
        [
            new AccessCheck("Read", AccessProbeOutcome.Passed, "Confluence GET /wiki/api/v2/spaces", "ok"),
            new AccessCheck("Write", AccessProbeOutcome.Passed, "Confluence POST + PUT /wiki/api/v2/pages", "round-trip"),
            new AccessCheck("Delete", AccessProbeOutcome.Failed, "Confluence DELETE /wiki/api/v2/pages/{id}",
                "The delete request returned 403. A probe page was left behind - \"Fravashi access probe - safe to delete\" (https://acme.atlassian.net/wiki/spaces/DOCS/pages/9001/probe). Please delete it manually."),
        ]));
        using var ctx = NewContext(probe, out _, out _);
        var cut = ctx.Render<WikiStep>();

        cut.Find("[data-testid=wiki-test]").Click();

        Assert.Contains("✗ Failed", cut.Markup);
        Assert.Contains("left behind", cut.Markup);
        Assert.Contains("Please delete it manually", cut.Markup);
    }

    /// <summary>
    /// Saving IS the moment the connection has to reach the adapters (backlog 12, met live on 2026-09-21). The
    /// only reload used to sit in the wizard's final Done, and a step re-opened from Settings is saved and left
    /// without ever getting there, so a verified Confluence connection read "not fully wired yet" until the
    /// container restarted.
    /// </summary>
    [Fact]
    public void Saving_the_connection_reloads_the_configuration_so_the_adapters_see_it_without_a_restart()
    {
        using var ctx = NewContext(new FakeWikiAccessProbe(ProbeResults.Verified), out var connections, out _);
        var reloader = (FakeConfigurationReloader)ctx.Services.GetRequiredService<IConfigurationReloader>();
        var cut = ctx.Render<WikiStep>();

        cut.Find("[data-testid=wiki-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(connections.Get("wiki")));
        Assert.Equal(1, reloader.ReloadCount);
    }

    [Fact]
    public void Local_wiki_is_recorded_and_labelled_as_not_yet_a_working_store()
    {
        using var ctx = NewContext(new FakeWikiAccessProbe(ProbeResults.Verified), out var connections, out var config);
        var cut = ctx.Render<WikiStep>();

        cut.Find("[data-testid=wiki-provider]").Change(OnboardingProviders.Local);
        Assert.Contains("not yet a working store", cut.Find("[data-testid=wiki-local]").TextContent);

        cut.Find("[data-testid=wiki-save]").Click();

        var connection = connections.Get("wiki");
        Assert.NotNull(connection);
        Assert.Equal(ProviderKind.Wiki, connection!.Kind);
        Assert.Equal("Local", connection.Status);
        Assert.Equal("SiteAuthoritative;AskOnConflict", config.Get("Wiki:SyncPolicy"));
    }
}
