using System.Text.RegularExpressions;
using Bunit;
using Fravashi.Domain.AgentSessions;
using Fravashi.Web.Components.Sessions;
using Fravashi.Web.Components.Shared;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 24: the house dropdown, and the two things the decision actually bought.
///
/// The studio had three dropdown idioms (Fluent at two onboarding sites, a hand-built picker at two
/// others, and a native select at nine). The decision was to standardise on the hand-built one, so what
/// has to be held here is the reason it was chosen over the other two: it needs NO JavaScript, and its
/// options can carry more than one word. A native <c>&lt;option&gt;</c> renders text and nothing else,
/// which is why the model catalog's notes were invisible in a title attribute before this.
/// </summary>
public class StudioDropdownTests
{
    private static readonly StudioDropdownOption[] Options =
    [
        new("", "CLI default", "Passes no model flag, so the agent CLI picks its own."),
        new("sonnet", "sonnet", "Latest Sonnet (the API default).", "S"),
        new("opusplan", "opusplan", "Opus while planning, Sonnet while executing.", "O"),
        new("mystery-9", "mystery-9"),
    ];

    private static IRenderedComponent<StudioDropdown> Render(
        BunitContext ctx,
        string? value = null,
        Action<string>? onChange = null,
        bool disabled = false) =>
        ctx.Render<StudioDropdown>(p => p
            .Add(c => c.TestId, "dd")
            .Add(c => c.Options, Options)
            .Add(c => c.Value, value)
            .Add(c => c.Disabled, disabled)
            .Add(c => c.ValueChanged, v => onChange?.Invoke(v)));

    [Fact]
    public async Task An_option_carries_its_subtitle_and_its_icon_where_one_exists()
    {
        // bunit's default JSInterop mode is strict: an unplanned invocation throws. So this test also
        // proves the claim the whole decision rests on, that the control needs no interop to open.
        using var ctx = new BunitContext();
        var cut = Render(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());

        Assert.Equal(
            "Latest Sonnet (the API default).",
            cut.Find("[data-testid=dd-subtitle-sonnet]").TextContent.Trim());
        Assert.Equal("S", cut.Find("[data-testid=dd-icon-sonnet]").TextContent.Trim());

        // "where one exists": an option with no icon gets an empty slot, not an invented symbol, and it
        // still has no subtitle element at all rather than an empty one.
        Assert.Equal(string.Empty, cut.Find("[data-testid=dd-icon-mystery-9]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=dd-subtitle-mystery-9]"));
    }

    [Fact]
    public async Task An_unavailable_option_is_shown_with_its_reason_and_cannot_be_chosen()
    {
        // Backlog 168: the model picker offered a model the pinned CLI is refused for.
        using var ctx = new BunitContext();
        string? chosen = null;
        var cut = ctx.Render<StudioDropdown>(p => p
            .Add(c => c.TestId, "dd")
            .Add(c => c.Options, [.. Options, new StudioDropdownOption("too-new", "too-new", "Needs a newer CLI.", Unavailable: true)])
            .Add(c => c.ValueChanged, v => chosen = v));

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());
        var row = cut.Find("[data-testid=dd-opt-too-new]");

        Assert.True(row.HasAttribute("disabled"));
        Assert.Equal("true", row.GetAttribute("aria-disabled"));
        Assert.Equal("Needs a newer CLI.", cut.Find("[data-testid=dd-subtitle-too-new]").TextContent.Trim());
        await cut.InvokeAsync(() => row.Click());
        Assert.Null(chosen);
    }

    [Fact]
    public void A_model_the_pinned_cli_is_too_old_for_is_offered_as_unavailable_with_the_reason()
    {
        // Measured under the 2.1.222 pin (the second self-hosting trial's first conversation died on it).
        var fable = new Fravashi.Application.Framework.ModelOption("claude-fable-5-1", "Fable.", MinimumCli: "2.1.251");
        var tooOld = SessionModelOptions.Option(fable, "2.1.222");

        Assert.True(tooOld.Unavailable);
        Assert.Contains("Needs Claude Code 2.1.251 or newer; this version pins 2.1.222.", tooOld.Subtitle);

        // The day the pin is promoted past it, the same model is simply on offer, which is where the pin is now.
        var promoted = SessionModelOptions.Option(fable, "2.1.280");
        Assert.False(promoted.Unavailable);
        Assert.Equal("Fable.", promoted.Subtitle);
        Assert.False(SessionModelOptions.For(AgentTool.Claude).Single(o => o.Value == "claude-fable-5-1").Unavailable);
    }

    [Fact]
    public void While_the_free_tier_carries_the_runtime_a_model_it_does_not_cover_is_shown_as_unavailable_with_why()
    {
        // shown rather than hidden, and a person learns how to use it (their own key).
        var secrets = new FakeSecretStore();
        secrets.Set(Fravashi.Application.AgentRuntimes.BrokerOptions.DefaultUserTokenSecretRef, "minted-bearer");
        var freeTier = new Fravashi.Infrastructure.AgentRuntimes.AgentRuntimeCredentialStore(
            secrets, Fravashi.Application.AgentRuntimes.BrokerOptions.Disabled.WithFreeTier("https://example.test/fravashi/api"));

        var options = SessionModelOptions.For(AgentTool.Claude, freeTier);

        Assert.False(options.Single(o => o.Value == "claude-haiku-5-5").Unavailable);
        var opus = options.Single(o => o.Value == "claude-opus-5-5");
        Assert.True(opus.Unavailable);
        Assert.Equal("not on the free tier", opus.UnavailableWhy);
        Assert.Contains("own API key or subscription", opus.Subtitle);
        Assert.Contains("claude-haiku-5-5", options[0].Subtitle);

        // With a key of the person's own, everything the pin allows is on offer again.
        freeTier.SaveApiKey(AgentTool.Claude, "sk-ant-own-key");
        Assert.False(SessionModelOptions.For(AgentTool.Claude, freeTier).Single(o => o.Value == "claude-opus-5-5").Unavailable);
    }

    [Fact]
    public async Task Choosing_an_option_reports_its_value_and_closes_the_list()
    {
        using var ctx = new BunitContext();
        string? chosen = null;
        var cut = Render(ctx, onChange: v => chosen = v);

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-opt-opusplan]").Click());

        Assert.Equal("opusplan", chosen);
        Assert.Empty(cut.FindAll("[data-testid=dd-panel]"));
    }

    [Fact]
    public async Task An_empty_valued_option_is_a_real_choice_and_reports_the_empty_string()
    {
        // The "leave it alone" option. Mapping the empty string back to null is the caller's policy, so
        // the control must report it rather than swallowing it as "nothing chosen".
        using var ctx = new BunitContext();
        var reported = new List<string>();
        var cut = Render(ctx, value: "sonnet", onChange: reported.Add);

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-opt-none]").Click());

        Assert.Equal([""], reported);
    }

    [Fact]
    public void The_closed_trigger_shows_the_chosen_option_and_a_null_value_selects_the_empty_one()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, value: "opusplan");
        Assert.Contains("opusplan", cut.Find("[data-testid=dd-trigger]").TextContent, StringComparison.Ordinal);
        Assert.Equal("O", cut.Find("[data-testid=dd-chosen-icon]").TextContent.Trim());

        // A compact trigger truncates with an ellipsis, so the full value has to stay recoverable.
        Assert.Equal("opusplan", cut.Find("[data-testid=dd-trigger]").GetAttribute("title"));

        // Null and the empty string mean the same thing, so a caller whose model uses null for "not set"
        // still gets its empty-valued option shown rather than the placeholder.
        cut.Render(p => p
            .Add(c => c.TestId, "dd")
            .Add(c => c.Options, Options)
            .Add(c => c.Value, (string?)null));
        Assert.Contains("CLI default", cut.Find("[data-testid=dd-trigger]").TextContent, StringComparison.Ordinal);

        // The empty icon slot is 21px of a 280px agent panel, which is the whole difference between
        // "CLI default" and "CLI d..." there, so the closed trigger drops it when there is no icon.
        Assert.Empty(cut.FindAll("[data-testid=dd-chosen-icon]"));
    }

    [Fact]
    public async Task Escape_closes_the_list_without_choosing_anything()
    {
        using var ctx = new BunitContext();
        var reported = new List<string>();
        var cut = Render(ctx, onChange: reported.Add);

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=dd]").KeyDown(new KeyboardEventArgs { Key = "Escape" }));

        Assert.Empty(cut.FindAll("[data-testid=dd-panel]"));
        Assert.Empty(reported);
    }

    [Fact]
    public void A_disabled_dropdown_cannot_be_opened()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, disabled: true);

        Assert.True(cut.Find("[data-testid=dd-trigger]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=dd-panel]"));
    }

    // ---- backlog 31: the two things a long list needs ------------------------------------------

    /// <summary>
    /// Loose interop, unlike every test above: the filter box asks the browser to focus it when the panel
    /// opens, and that one call is the control's only interop. Everything without Filterable stays under
    /// bunit's strict mode, which is what keeps the "no JavaScript" claim a measured one.
    /// </summary>
    private static IRenderedComponent<StudioDropdown> RenderFilterable(BunitContext ctx)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx.Render<StudioDropdown>(p => p
            .Add(c => c.TestId, "dd")
            .Add(c => c.Options, Options)
            .Add(c => c.Filterable, true));
    }

    [Fact]
    public async Task Opening_a_filterable_panel_puts_the_caret_in_the_filter_box()
    {
        // Searchable "by typing" has to mean typing. The plain autofocus attribute was tried first and
        // measured not to fire at all: a document processes autofocus once, and this panel is inserted
        // long after that, so the user would have had to click the box before every search.
        using var ctx = new BunitContext();
        var cut = RenderFilterable(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());

        ctx.JSInterop.VerifyFocusAsyncInvoke();
    }

    [Fact]
    public async Task Without_the_filter_turned_on_there_is_no_filter_box()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());

        Assert.Empty(cut.FindAll("[data-testid=dd-filter]"));
        Assert.Equal(Options.Length, cut.FindAll("[data-testid^=dd-opt-]").Count);
    }

    [Fact]
    public async Task Typing_narrows_the_list_and_matches_the_subtitle_as_well_as_the_label()
    {
        using var ctx = new BunitContext();
        var cut = RenderFilterable(ctx);
        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-filter]").Input("opus"));
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=dd-opt-opusplan]"));
            Assert.Empty(cut.FindAll("[data-testid=dd-opt-sonnet]"));
        });

        // The distinguishing word is often not in the label at all: "planning" is only in the subtitle.
        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-filter]").Input("PLANNING"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid^=dd-opt-]")));
        Assert.NotEmpty(cut.FindAll("[data-testid=dd-opt-opusplan]"));
    }

    [Fact]
    public async Task A_filter_matching_nothing_says_so_differently_from_an_empty_list()
    {
        using var ctx = new BunitContext();
        var cut = RenderFilterable(ctx);
        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-filter]").Input("no-such-model"));

        // "nothing here" and "nothing matching what you typed" send the user to two different moves.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=dd-nomatch]")));
        Assert.Empty(cut.FindAll("[data-testid=dd-empty]"));
    }

    [Fact]
    public async Task Reopening_the_panel_does_not_reapply_the_last_filter()
    {
        using var ctx = new BunitContext();
        var cut = RenderFilterable(ctx);
        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-filter]").Input("opus"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());

        // A stale filter hides most of the list with its reason sitting in a box nobody looks at.
        cut.WaitForAssertion(() => Assert.Equal(Options.Length, cut.FindAll("[data-testid^=dd-opt-]").Count));
    }

    [Fact]
    public async Task Opening_the_panel_tells_the_caller_so_it_can_load_a_list_that_costs_something()
    {
        using var ctx = new BunitContext();
        var opens = 0;
        // Strict interop on purpose: a non-filterable dropdown must still make no interop call at all.
        var cut = ctx.Render<StudioDropdown>(p => p
            .Add(c => c.TestId, "dd")
            .Add(c => c.Options, Options)
            .Add(c => c.Opened, () => opens++));

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());
        Assert.Equal(1, opens);

        // Closing is not opening.
        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task An_options_state_class_lands_on_its_row_so_the_callers_own_stylesheet_can_colour_it()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<StudioDropdown>(p => p
            .Add(c => c.TestId, "dd")
            .Add(c => c.Options, new StudioDropdownOption[]
            {
                new("a", "a", "synced", "S", "git-b-synced"),
                new("b", "b", "plain"),
            }));

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());

        Assert.Contains("git-b-synced", cut.Find("[data-testid=dd-opt-a]").GetAttribute("class")!);
        Assert.DoesNotContain("git-b-", cut.Find("[data-testid=dd-opt-b]").GetAttribute("class")!);
    }

    // ---- the model list the picker is built from -----------------------------------------------

    [Fact]
    public void Every_catalog_model_becomes_an_option_with_the_cli_default_first()
    {
        var options = SessionModelOptions.For(AgentTool.Claude);

        Assert.Equal(SessionModelOptions.CliDefaultValue, options[0].Value);

        // The catalog's own "default" id would be a second way to say the same thing, and passing it as
        // --model default is not what "no override" means to the runner.
        Assert.DoesNotContain(options.Skip(1), o => o.Value == "default");
        Assert.All(options.Skip(1), o => Assert.False(string.IsNullOrWhiteSpace(o.Subtitle)));
    }

    [Theory]
    [InlineData("opus", "O")]
    [InlineData("opusplan", "O")]
    [InlineData("claude-fable-5-1", "F")]
    [InlineData("claude-haiku-4-5", "H")]
    [InlineData("claude-sonnet-4.5", "S")]
    [InlineData("codex-mini-latest", "C")]
    // Earliest family named in the id wins, so a GPT model with a Codex variant is a GPT.
    [InlineData("gpt-5.1-codex", "G")]
    [InlineData("something-nobody-shipped", null)]
    public void The_family_badge_describes_the_id_and_is_absent_when_it_names_no_known_family(string id, string? icon) =>
        Assert.Equal(icon, SessionModelOptions.FamilyIcon(id));

    // ---- the 240px defect the item said rides along --------------------------------------------

    /// <summary>
    /// Every class the shipped stylesheet gives a wide minimum width. This is read from the real asset
    /// rather than hardcoded, so the guard follows the number if somebody moves it.
    /// </summary>
    internal static IReadOnlyList<string> ClassesWithAWideMinimum(int atLeastPx)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "the test must be able to locate the repository root (Fravashi.sln)");
        var path = Path.Combine(dir!.FullName, "src", "Fravashi.Web", "wwwroot", "studio.css");
        Assert.True(File.Exists(path), $"the studio should ship studio.css at {path}");
        var css = Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        // Selector + body pairs. A body never contains braces, so an @media wrapper drops out and the
        // rules inside it are matched on their own; a media-query override of the same class simply adds
        // it again, and this is a set.
        return Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .SelectMany(m => m.Groups["selector"].Value.Split(',').Select(s => (Selector: s.Trim(), Body: m.Groups["body"].Value)))
            .Where(r => Regex.IsMatch(r.Selector, @"^\.[a-z0-9-]+$"))
            .Where(r => Regex.Match(r.Body, @"min-width\s*:\s*(?<px>\d+)px") is { Success: true } px
                        && int.Parse(px.Groups["px"].Value) >= atLeastPx)
            .Select(r => r.Selector[1..])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void The_shared_form_field_minimum_is_still_the_thing_this_guard_is_about()
    {
        // If .studio-input ever stops declaring a wide minimum, the guard below would pass vacuously and
        // nobody would notice. So the guard's own premise is asserted rather than assumed.
        Assert.Contains("studio-input", ClassesWithAWideMinimum(200));
    }

    // ---- backlog 128 (decided 2026-09-21): an action pinned under the scrolling list ----

    [Fact]
    public async Task A_pinned_option_is_rendered_in_its_own_row_after_the_list_and_still_chooses_like_any_other()
    {
        // The workspace menu is capped and scrolls. With a long recents list, Close workspace, the one action
        // the menu exists for, fell below the fold with nothing but a thin scrollbar to say it was there. A
        // pinned option sits in a row that sticks to the bottom of the panel, as the filter sticks to the top.
        using var ctx = new BunitContext();
        string? chosen = null;
        StudioDropdownOption[] options =
        [
            new("a", "Alpha"),
            new("close", "Close workspace", Pinned: true),
            new("b", "Beta"),
        ];
        var cut = ctx.Render<StudioDropdown>(p => p
            .Add(c => c.TestId, "dd")
            .Add(c => c.Options, options)
            .Add(c => c.ValueChanged, v => chosen = v));

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());

        var pinnedRow = cut.Find("[data-testid=dd-pinned]");
        Assert.Single(pinnedRow.QuerySelectorAll("[role=option]"));
        Assert.Contains("Close workspace", pinnedRow.TextContent, StringComparison.Ordinal);

        // It is the LAST thing in the panel whatever its position in the list, and it is not in the list twice.
        var all = cut.FindAll("[role=option]").Select(o => o.TextContent.Trim()).ToList();
        Assert.Equal("Close workspace", all[^1]);
        Assert.Single(all, t => t == "Close workspace");

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-opt-close]").Click());
        Assert.Equal("close", chosen);
    }

    [Fact]
    public async Task With_nothing_pinned_there_is_no_pinned_row_at_all()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=dd-trigger]").Click());

        Assert.Empty(cut.FindAll("[data-testid=dd-pinned]"));
    }
}
