using Bunit;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Web.Components.Onboarding;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

public class OnboardingBoardStepTests
{
    private static BunitContext NewContext(
        IBoardAccessProbe probe,
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
    public void Read_and_query_verify_while_write_is_recorded_not_verified()
    {
        using var ctx = NewContext(new FakeBoardAccessProbe(ProbeResults.NotVerifiableOnly), out _, out _);
        var cut = ctx.Render<BoardStep>();

        cut.Find("[data-testid=board-test]").Click();

        // Honesty: read/query are real passes; write is explicitly recorded-not-verified, never faked green.
        var rows = cut.FindAll(".ob-probe-row");
        Assert.Contains(rows, r => r.GetAttribute("data-capability") == "Read" && r.TextContent.Contains("✓ Verified"));
        Assert.Contains(rows, r => r.GetAttribute("data-capability") == "Query" && r.TextContent.Contains("✓ Verified"));
        Assert.Contains(rows, r => r.GetAttribute("data-capability") == "Write" && r.TextContent.Contains("Recorded, not verified"));
    }

    [Fact]
    public void Jira_shows_an_optional_project_key_field_and_passes_it_to_the_probe()
    {
        var probe = new FakeBoardAccessProbe(ProbeResults.NotVerifiableOnly);
        using var ctx = NewContext(probe, out _, out _);
        var cut = ctx.Render<BoardStep>();

        cut.Find("[data-testid=board-provider]").Change(OnboardingProviders.Jira);

        // The helper text makes the empty-key behaviour explicit.
        var help = cut.Find("[data-testid=board-project-help]").TextContent;
        Assert.Contains("Leave it", help, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not verified", help, System.StringComparison.OrdinalIgnoreCase);

        cut.Find("[data-testid=board-project]").Change("ENG");
        cut.Find("[data-testid=board-test]").Click();

        Assert.Equal("ENG", probe.LastRequest!.Project);
    }

    [Fact]
    public void Jira_round_trip_renders_write_modify_and_status_change_verified()
    {
        var probe = new FakeBoardAccessProbe(new AccessProbeResult(
        [
            new AccessCheck("Read", AccessProbeOutcome.Passed, "Jira GET /rest/api/3/myself", "ok"),
            new AccessCheck("Query", AccessProbeOutcome.Passed, "Jira POST /rest/api/3/search/jql", "ran"),
            new AccessCheck("Write", AccessProbeOutcome.Passed, "Jira POST /rest/api/3/issue", "Created a probe issue ENG-42."),
            new AccessCheck("Modify", AccessProbeOutcome.Passed, "Jira PUT /rest/api/3/issue/{key}", "Edited the probe issue."),
            new AccessCheck("Status change", AccessProbeOutcome.Passed, "Jira GET + POST /rest/api/3/issue/{key}/transitions", "Transitioned to 'In Progress'."),
        ]));
        using var ctx = NewContext(probe, out _, out _);
        var cut = ctx.Render<BoardStep>();

        cut.Find("[data-testid=board-provider]").Change(OnboardingProviders.Jira);
        cut.Find("[data-testid=board-test]").Click();

        // Five real verifications render (Read, Query, Write, Modify, Status change).
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(cut.Markup, "✓ Verified").Count);
    }

    [Fact]
    public void Jira_leftover_probe_issue_is_reported_when_delete_fails()
    {
        var probe = new FakeBoardAccessProbe(new AccessProbeResult(
        [
            new AccessCheck("Read", AccessProbeOutcome.Passed, "Jira GET /rest/api/3/myself", "ok"),
            new AccessCheck("Query", AccessProbeOutcome.Passed, "Jira POST /rest/api/3/search/jql", "ran"),
            new AccessCheck("Write", AccessProbeOutcome.Passed, "Jira POST /rest/api/3/issue",
                "Created a probe issue ENG-42. The probe issue could not be deleted - the token lacks the \"Delete issues\" permission (403). It was left behind - ENG-42 (https://acme.atlassian.net/browse/ENG-42). Please delete it manually."),
            new AccessCheck("Modify", AccessProbeOutcome.Passed, "Jira PUT /rest/api/3/issue/{key}", "Edited the probe issue."),
            new AccessCheck("Status change", AccessProbeOutcome.Passed, "Jira GET + POST /rest/api/3/issue/{key}/transitions", "Transitioned."),
        ]));
        using var ctx = NewContext(probe, out _, out _);
        var cut = ctx.Render<BoardStep>();

        cut.Find("[data-testid=board-provider]").Change(OnboardingProviders.Jira);
        cut.Find("[data-testid=board-test]").Click();

        Assert.Contains("left behind", cut.Markup);
        Assert.Contains("ENG-42", cut.Markup);
        Assert.Contains("Please delete it manually", cut.Markup);
    }

    [Fact]
    public void Saving_an_external_board_records_the_connection_and_flips_the_tracker_provider()
    {
        using var ctx = NewContext(new FakeBoardAccessProbe(ProbeResults.NotVerifiableOnly), out var connections, out var config);
        var cut = ctx.Render<BoardStep>();

        cut.Find("[data-testid=board-save]").Click();

        var connection = connections.Get("board");
        Assert.NotNull(connection);
        Assert.Equal(ProviderKind.Board, connection!.Kind);
        // Default provider is Azure DevOps → Tracker:Provider changes (drives the completion restart).
        Assert.Equal("AzureDevOps", config.Get("Tracker:Provider"));
    }

    /// <summary>
    /// The board half of the same fact as the wiki step's: a board wins the shared Atlassian coordinates, so a
    /// Jira connection saved from Settings and never reloaded left the wiki unwired as well (backlog 12).
    /// </summary>
    [Fact]
    public void Saving_the_connection_reloads_the_configuration_so_the_adapters_see_it_without_a_restart()
    {
        using var ctx = NewContext(new FakeBoardAccessProbe(ProbeResults.NotVerifiableOnly), out var connections, out _);
        var reloader = (FakeConfigurationReloader)ctx.Services.GetRequiredService<IConfigurationReloader>();
        var cut = ctx.Render<BoardStep>();

        cut.Find("[data-testid=board-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(connections.Get("board")));
        Assert.Equal(1, reloader.ReloadCount);
    }

    [Fact]
    public void Choosing_the_local_board_records_it_without_flipping_the_tracker_provider()
    {
        using var ctx = NewContext(new FakeBoardAccessProbe(ProbeResults.NotVerifiableOnly), out var connections, out var config);
        var cut = ctx.Render<BoardStep>();

        cut.Find("[data-testid=board-provider]").Change(OnboardingProviders.Local);
        cut.Find("[data-testid=board-local]"); // the local note is shown
        cut.Find("[data-testid=board-save]").Click();

        var connection = connections.Get("board");
        Assert.NotNull(connection);
        Assert.Equal("Local", connection!.Status);
        Assert.Null(config.Get("Tracker:Provider"));
    }
}
