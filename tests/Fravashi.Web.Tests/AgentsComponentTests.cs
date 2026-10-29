using Bunit;
using Fravashi.Application.Framework;
using Fravashi.Application.Persistence;
using Fravashi.Domain.Framework;
using Fravashi.Web.Components.Settings.Framework;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The Agents CRUD UI (T3, UI8 - UI10): the subagent form's required-field validation, the targets↔model coupling
/// (with <c>inherit</c> offered as the subagent default), list cards, and that a valid form persists the agent.
/// </summary>
public class AgentsComponentTests
{
    private static BunitContext Ctx(
        out FakeFrameworkArtifactStore store,
        out FakeConfigurationStore config,
        string? selectedRuntimes = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        store = new FakeFrameworkArtifactStore();
        config = new FakeConfigurationStore();
        if (selectedRuntimes is not null)
        {
            config.Set("AgentRuntimes:Selected", selectedRuntimes);
        }

        ctx.Services.AddSingleton<IFrameworkArtifactStore>(store);
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IModelListResolver>(new FakeModelListResolver());
        ctx.Services.AddSingleton(new FluentIconCatalog());
        return ctx;
    }

    private static void FillValid(IRenderedComponent<AgentForm> cut, string name)
    {
        cut.Find("[data-testid=agent-name]").Input(name);
        cut.Find("[data-testid=agent-description]").Input("Use to review changes before a commit.");
        cut.Find("[data-testid=agent-system-prompt]").Input("You are a meticulous reviewer.");
    }

    [Fact]
    public void Form_reports_missing_required_contract_fields()
    {
        using var ctx = Ctx(out var store, out _);

        var cut = ctx.Render<AgentForm>();
        cut.Find("[data-testid=agent-name]").Input("Bad Name");
        cut.Find("[data-testid=agent-save]").Click();

        Assert.NotEmpty(cut.FindAll("[data-testid=agent-error-name]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=agent-error-description]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=agent-error-system_prompt]"));
        Assert.Empty(store.Artifacts);
    }

    [Fact]
    public void With_no_target_the_model_field_offers_inherit()
    {
        using var ctx = Ctx(out _, out _, selectedRuntimes: "Claude");

        var cut = ctx.Render<AgentForm>();

        // No target selected yet: the model select is present and offers inherit.
        var select = cut.Find("[data-testid=agent-model]");
        Assert.Contains("inherit", select.InnerHtml);
    }

    [Fact]
    public void Selecting_a_target_populates_the_model_dropdown_from_its_catalog()
    {
        using var ctx = Ctx(out _, out _, selectedRuntimes: "Claude");

        var cut = ctx.Render<AgentForm>();
        cut.Find("[data-testid=agent-target-Claude]").Change(true);

        var select = cut.Find("[data-testid=agent-model]");
        Assert.Contains("opus", select.InnerHtml);   // Claude catalog options present
        Assert.Contains("inherit", select.InnerHtml); // inherit still offered
    }

    [Fact]
    public void A_valid_form_persists_the_agent()
    {
        using var ctx = Ctx(out var store, out _);

        var cut = ctx.Render<AgentForm>();
        FillValid(cut, "code-reviewer");
        cut.Find("[data-testid=agent-tools]").Input("Read, Grep");
        cut.Find("[data-testid=agent-save]").Click();

        var saved = Assert.Single(store.Artifacts.Values);
        Assert.Equal(FrameworkArtifactKind.Agent, saved.Kind);
        Assert.Equal("code-reviewer", saved.Name);
        var agent = FrameworkPayload<AgentDefinition>.From(saved);
        Assert.Equal(["Read", "Grep"], agent.Tools);
        Assert.Equal("You are a meticulous reviewer.", agent.SystemPrompt);
    }

    [Fact]
    public void Agent_list_renders_a_card_per_agent()
    {
        using var ctx = Ctx(out var store, out _);
        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Name = "code-reviewer",
            Description = "when to delegate",
            SystemPrompt = "body",
            DisplayName = "Reviewer",
            BrandColor = "#7C4DFF",
        };
        store.Add(new FrameworkArtifact("a1", FrameworkArtifactKind.Agent, "code-reviewer",
            FrameworkPayload<AgentDefinition>.Serialize(agent), now, now));

        var cut = ctx.Render<AgentList>();

        var card = cut.Find("[data-testid=agent-card-code-reviewer]");
        Assert.Contains("Reviewer", card.TextContent);
    }

    [Fact]
    public void The_view_renders_the_subagent_markdown_preview()
    {
        using var ctx = Ctx(out var store, out _);
        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Name = "code-reviewer",
            Description = "when to delegate",
            SystemPrompt = "You are a reviewer.",
            Tools = ["Read"],
        };
        store.Add(new FrameworkArtifact("a1", FrameworkArtifactKind.Agent, "code-reviewer",
            FrameworkPayload<AgentDefinition>.Serialize(agent), now, now));

        var cut = ctx.Render<AgentView>(p => p.Add(c => c.Id, "a1"));

        var preview = cut.Find("[data-testid=agent-view-markdown]").TextContent;
        Assert.Contains("name: \"code-reviewer\"", preview);   // quoted value, unquoted key
        Assert.Contains("tools: \"Read\"", preview);
    }
}
