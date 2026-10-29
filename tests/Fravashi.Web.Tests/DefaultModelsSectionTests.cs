using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #357: Settings &gt; Agents &gt; Default models and effort. Three roles (a new conversation, the studio
/// assistant, the autopilot builder), each Default or a choice from the session menus' own closed sets, saved to the
/// settings table and read back; the commit reviewer shown read-only, because its model is fixed.
/// </summary>
public class DefaultModelsSectionTests
{
    private static BunitContext Context(out FakeConfigurationStore config)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        config = new FakeConfigurationStore();
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        return ctx;
    }

    [Fact]
    public void Nothing_set_reads_as_default_everywhere_and_save_starts_disabled()
    {
        using var ctx = Context(out _);

        var cut = ctx.Render<DefaultModelsSection>();

        foreach (var role in new[] { "new-conversation", "studio-assistant", "autopilot-builder" })
        {
            Assert.Equal(string.Empty, cut.Find($"[data-testid=default-models-{role}-model]").GetAttribute("value"));
            Assert.Equal(string.Empty, cut.Find($"[data-testid=default-models-{role}-effort]").GetAttribute("value"));
        }

        Assert.True(cut.Find("[data-testid=default-models-save]").HasAttribute("disabled"));
    }

    [Fact]
    public void The_choices_are_the_session_menus_own_closed_sets()
    {
        using var ctx = Context(out _);

        var cut = ctx.Render<DefaultModelsSection>();

        var models = cut.FindAll("[data-testid=default-models-new-conversation-model] option").Select(o => o.GetAttribute("value")).ToList();
        Assert.Equal(Fravashi.Web.Components.Sessions.SessionModelOptions.For(AgentTool.Claude).Select(o => o.Value), models);
        var efforts = cut.FindAll("[data-testid=default-models-new-conversation-effort] option").Select(o => o.GetAttribute("value"));
        Assert.Equal(["", "low", "medium", "high", "xhigh", "max"], efforts);
    }

    [Fact]
    public async Task A_saved_choice_is_stored_and_read_back_on_the_next_visit()
    {
        using var ctx = Context(out var config);
        var cut = ctx.Render<DefaultModelsSection>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=default-models-new-conversation-model]").Change("sonnet"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=default-models-new-conversation-effort]").Change("high"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=default-models-studio-assistant-model]").Change("haiku"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=default-models-autopilot-builder-effort]").Change("xhigh"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=default-models-save]").Click());

        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=default-models-save]").HasAttribute("disabled")));
        var stored = AgentDefaultsSetting.Read(config);
        Assert.Equal(new AgentDefault("sonnet", AgentEffort.High), stored.NewConversation);
        Assert.Equal(new AgentDefault("haiku", null), stored.StudioAssistant);
        Assert.Equal(new AgentDefault(null, AgentEffort.XHigh), stored.AutopilotBuilder);

        var again = ctx.Render<DefaultModelsSection>();
        Assert.Equal("sonnet", again.Find("[data-testid=default-models-new-conversation-model]").GetAttribute("value"));
        Assert.Equal("high", again.Find("[data-testid=default-models-new-conversation-effort]").GetAttribute("value"));
        Assert.Equal("haiku", again.Find("[data-testid=default-models-studio-assistant-model]").GetAttribute("value"));
        Assert.Equal("xhigh", again.Find("[data-testid=default-models-autopilot-builder-effort]").GetAttribute("value"));
    }

    [Fact]
    public async Task Use_the_default_everywhere_clears_every_row_on_save()
    {
        using var ctx = Context(out var config);
        AgentDefaultsSetting.Write(config, new AgentDefaults(
            new AgentDefault("opus", AgentEffort.Max), new AgentDefault("haiku", AgentEffort.Low), new AgentDefault("sonnet", null)));
        var cut = ctx.Render<DefaultModelsSection>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=default-models-reset]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=default-models-save]").Click());

        Assert.Equal(AgentDefaults.None, AgentDefaultsSetting.Read(config));
        Assert.DoesNotContain(config.Values.Keys, k => k.StartsWith(AgentDefaultsSetting.Prefix, StringComparison.Ordinal));
    }

    [Fact]
    public void The_commit_reviewer_is_shown_read_only_as_fixed()
    {
        using var ctx = Context(out var config);
        CommitReviewerSetting.Write(config, new CommitReviewerRole(true, AgentTool.Claude, "claude-opus-5-5"));

        var cut = ctx.Render<DefaultModelsSection>();

        var reviewer = cut.Find("[data-testid=default-models-reviewer]");
        Assert.Contains("claude-opus-5-5", cut.Find("[data-testid=default-models-reviewer-model]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Fixed", cut.Find("[data-testid=default-models-reviewer-note]").TextContent, StringComparison.Ordinal);
        Assert.Contains("2026-10-10", reviewer.TextContent, StringComparison.Ordinal);
        Assert.Empty(reviewer.QuerySelectorAll("select, input"));
    }

    [Fact]
    public async Task Saving_the_defaults_never_moves_the_commit_reviewer()
    {
        using var ctx = Context(out var config);
        CommitReviewerSetting.Write(config, new CommitReviewerRole(true, AgentTool.Claude, "claude-opus-5-5"));
        var cut = ctx.Render<DefaultModelsSection>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=default-models-autopilot-builder-model]").Change("haiku"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=default-models-save]").Click());

        Assert.Equal("claude-opus-5-5", CommitReviewerSetting.Read(config).Model);
    }
}
