using Bunit;
using Fravashi.Application.Framework;
using Fravashi.Application.Persistence;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Framework;
using Fravashi.Web.Components.Settings.Framework;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The Skills CRUD UI (T2): the form's validation (kebab name, 25-64 short description, hex colour,
/// $skill-name reference), the icon picker (real FluentUI icon search + render), icon_large defaulting, the
/// targets↔model coupling and the empty-target explanation, and the list cards (brand colour + icon).
/// </summary>
public class SkillsComponentTests
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

    private static SkillDefinition ValidSkill() => new()
    {
        Name = "weekly-status",
        DisplayName = "Weekly status",
        ShortDescription = "Drafts a concise weekly status update",
        Description = "Use when the user asks for a weekly status summary.",
        BrandColor = "#7C4DFF",
        DefaultPrompt = "Use $skill-name to draft the update.",
        Content = "# body",
        IconSmall = new SkillIcon("Home", null),
    };

    // ── The icon catalog is enumerated from the real installed package ───────────────────────────────────────

    [Fact]
    public void The_icon_catalog_enumerates_thousands_of_real_icons()
    {
        var catalog = new FluentIconCatalog();

        Assert.True(catalog.Count > 1000, $"expected the full Regular·Size20 set, got {catalog.Count}");
        Assert.True(catalog.Contains("Home"));
        Assert.True(catalog.TryGetSvg("Home", out var svg));
        Assert.Contains("<svg", svg);
        Assert.Contains("<path", svg);
    }

    // ── UI4 - icon picker: search filters and renders the actual icons ───────────────────────────────────────

    [Fact]
    public void Icon_picker_search_filters_the_set_and_renders_real_icon_svgs()
    {
        using var ctx = Ctx(out _, out _);
        var picked = SkillIcon.None;

        var cut = ctx.Render<IconPicker>(p => p
            .Add(c => c.TestId, "icon")
            .Add(c => c.Value, SkillIcon.None)
            .Add(c => c.ValueChanged, (SkillIcon i) => picked = i));

        var unfiltered = cut.FindAll("[data-testid^=icon-item-]").Count;

        cut.Find("[data-testid=icon-search]").Input("home");
        var filtered = cut.FindAll("[data-testid^=icon-item-]");

        Assert.NotEmpty(filtered);
        Assert.True(filtered.Count <= unfiltered);
        Assert.All(filtered, el => Assert.Contains("home", el.GetAttribute("data-testid")!, StringComparison.OrdinalIgnoreCase));
        // The results render the genuine icon SVGs, not just names.
        Assert.Contains("<svg", cut.Find("[data-testid=icon-grid]").InnerHtml);

        // Picking one emits the icon name.
        cut.Find("[data-testid=icon-item-Home]").Click();
        Assert.Equal("Home", picked.FluentIconName);
    }

    // ── UI3 - list cards render the brand colour and icon ────────────────────────────────────────────────────

    [Fact]
    public void Skill_list_renders_a_card_per_skill_with_brand_colour_and_icon()
    {
        using var ctx = Ctx(out var store, out _);
        var now = DateTimeOffset.UtcNow;
        store.Add(new FrameworkArtifact("a1", FrameworkArtifactKind.Skill, "weekly-status",
            SkillPayload.Serialize(ValidSkill()), now, now));

        var cut = ctx.Render<SkillList>();

        var card = cut.Find("[data-testid=skill-card-weekly-status]");
        Assert.Contains("Weekly status", card.TextContent);
        Assert.Contains("7C4DFF", card.GetAttribute("style"), StringComparison.OrdinalIgnoreCase);
        // The small icon renders as an SVG inside the card.
        Assert.Contains("<svg", card.InnerHtml);
    }

    // ── UI5 - form validation fires on the stated rules ──────────────────────────────────────────────────────

    [Fact]
    public void Form_reports_kebab_short_description_hex_and_placeholder_errors()
    {
        using var ctx = Ctx(out var store, out _);

        var cut = ctx.Render<SkillForm>();
        cut.Find("[data-testid=skill-name]").Input("Bad Name");
        cut.Find("[data-testid=skill-display-name]").Input("Bad");
        cut.Find("[data-testid=skill-short-description]").Input("too short");
        cut.Find("[data-testid=skill-description]").Input("desc");
        cut.Find("[data-testid=skill-brand-color-hex]").Input("not-a-color");
        cut.Find("[data-testid=skill-default-prompt]").Input("no placeholder here");
        cut.Find("[data-testid=skill-content]").Input("# body");

        cut.Find("[data-testid=skill-save]").Click();

        Assert.NotEmpty(cut.FindAll("[data-testid=skill-error-name]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=skill-error-short_description]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=skill-error-brand_color]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=skill-error-default_prompt]"));
        Assert.Empty(store.Artifacts); // nothing saved while invalid
    }

    [Fact]
    public void Form_rejects_a_duplicate_name()
    {
        using var ctx = Ctx(out var store, out _);
        var now = DateTimeOffset.UtcNow;
        store.Add(new FrameworkArtifact("a1", FrameworkArtifactKind.Skill, "weekly-status",
            SkillPayload.Serialize(ValidSkill()), now, now));

        var cut = ctx.Render<SkillForm>();
        FillValid(cut, name: "weekly-status");
        cut.Find("[data-testid=skill-save]").Click();

        Assert.NotEmpty(cut.FindAll("[data-testid=skill-error-name]"));
        Assert.Single(store.Artifacts);
    }

    [Fact]
    public void A_valid_form_persists_the_skill()
    {
        using var ctx = Ctx(out var store, out _);

        var cut = ctx.Render<SkillForm>();
        FillValid(cut, name: "weekly-status");
        cut.Find("[data-testid=skill-save]").Click();

        var saved = Assert.Single(store.Artifacts.Values);
        Assert.Equal("weekly-status", saved.Name);
        var skill = SkillPayload.From(saved);
        Assert.Equal("Weekly status", skill.DisplayName);
        Assert.Contains("$skill-name", skill.DefaultPrompt);
    }

    // ── UI4 - icon_large defaults to icon_small when unset ───────────────────────────────────────────────────

    [Fact]
    public void Large_icon_defaults_to_the_small_icon_when_unset()
    {
        using var ctx = Ctx(out _, out _);

        var cut = ctx.Render<SkillForm>();
        // Pick a small icon (search to surface it); leave the large picker untouched.
        cut.Find("[data-testid=icon-small-search]").Input("Home");
        cut.Find("[data-testid=icon-small-item-Home]").Click();

        Assert.NotEmpty(cut.FindAll("[data-testid=icon-large-defaulted]"));
    }

    // ── UI12 - model dropdown is coupled to selected targets ─────────────────────────────────────────────────

    [Fact]
    public void With_no_targets_selected_the_model_field_explains_why_it_is_empty()
    {
        using var ctx = Ctx(out _, out _, selectedRuntimes: "Claude,Codex");

        var cut = ctx.Render<SkillForm>();

        // Targets are offered (configured) but none selected yet → the model field explains itself.
        Assert.NotEmpty(cut.FindAll("[data-testid=skill-targets]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=skill-model-empty]"));
        Assert.Empty(cut.FindAll("[data-testid=skill-model]"));
    }

    [Fact]
    public void Selecting_a_target_populates_the_model_dropdown_from_its_catalog()
    {
        using var ctx = Ctx(out _, out _, selectedRuntimes: "Claude");

        var cut = ctx.Render<SkillForm>();
        cut.Find("[data-testid=skill-target-Claude]").Change(true);

        var select = cut.Find("[data-testid=skill-model]");
        // The Claude catalog options are present (e.g. the "opus" alias).
        Assert.Contains("opus", select.InnerHtml);
        Assert.Empty(cut.FindAll("[data-testid=skill-model-empty]"));
    }

    [Fact]
    public void With_no_runtimes_configured_the_targets_section_is_honestly_empty()
    {
        using var ctx = Ctx(out _, out _); // no AgentRuntimes:Selected

        var cut = ctx.Render<SkillForm>();

        Assert.NotEmpty(cut.FindAll("[data-testid=skill-targets-empty]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=skill-model-empty]"));
    }

    private static void FillValid(IRenderedComponent<SkillForm> cut, string name)
    {
        cut.Find("[data-testid=skill-name]").Input(name);
        cut.Find("[data-testid=skill-display-name]").Input("Weekly status");
        cut.Find("[data-testid=skill-short-description]").Input("Drafts a concise weekly status update");
        cut.Find("[data-testid=skill-description]").Input("Use when the user asks for a weekly status summary.");
        cut.Find("[data-testid=skill-brand-color-hex]").Input("#7C4DFF");
        cut.Find("[data-testid=skill-default-prompt]").Input("Use $skill-name to draft the update.");
        cut.Find("[data-testid=skill-content]").Input("# body");
    }
}
