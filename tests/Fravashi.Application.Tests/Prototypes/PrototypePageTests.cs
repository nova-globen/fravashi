using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// Backlog 213's acceptance, at the level of the page (plan @38): the page ACTS on the tree. Its one script is a
/// fixed interpreter of the data the renderer writes, so what go, open, close and set do is decided by that data,
/// and these tests read it back against the tree: every node's actions, the state each starts in, the screen
/// shown first, which dialogs start closed, tabs, the state CSS. And the boundary the viewer relies on: the
/// content security policy first in the head, admitting that one script by its hash and nothing else. How the
/// script itself behaves in a real engine is the window check's (#217), after a release.
/// </summary>
public class PrototypePageTests
{
    private static MockupDocument Valid(string json)
    {
        var read = MockupReader.Read(json);
        read.IsValid.Should().BeTrue(string.Join("\n", read.Errors));
        return read.Document!;
    }

    private static MockupDocument EveryFeature() =>
        Valid(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "every-feature.json")));

    /// <summary>The opening tag of the element anchored by <paramref name="key"/>.</summary>
    private static string Tag(string html, string key)
    {
        // In the body: the stylesheet names anchors too.
        var body = html.IndexOf("<body>", StringComparison.Ordinal);
        var at = html.IndexOf($" data-mk-anchor=\"{WebUtility.HtmlEncode(key)}\"", body, StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, $"the page should carry an element anchored by {key}");
        var start = html.LastIndexOf('<', at);
        return html[start..(html.IndexOf('>', at) + 1)];
    }

    /// <summary>An attribute's decoded value in an opening tag, or null.</summary>
    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"\\s{Regex.Escape(name)}=\"([^\"]*)\"");
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static string[] Classes(string tag) => (Attribute(tag, "class") ?? string.Empty).Split(' ');

    private static string ScriptOf(string html)
    {
        var scripts = Regex.Matches(html, "<script>(.*?)</script>", RegexOptions.Singleline);
        scripts.Should().ContainSingle();
        return scripts[0].Groups[1].Value;
    }

    private static string Sha256(string text) =>
        "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ---- what the tree says the page does --------------------------------------------------------------

    [Fact]
    public void Every_node_with_actions_carries_them_exactly_as_the_tree_says()
    {
        var document = EveryFeature();
        var html = PrototypeRenderer.Render(document, []).Html;

        var withActions = 0;
        for (var s = 0; s < document.Screens.Count; s++)
        {
            foreach (var place in MockupView.Walk(document.Screens[s], MockupView.ScreenPath(s)))
            {
                if (place.Node.On is not { } on || place.Node.Id is not { } id)
                {
                    continue;
                }

                withActions++;
                var written = JsonDocument.Parse(Attribute(Tag(html, id), "data-mk-on")!).RootElement;
                written.EnumerateObject().Select(p => p.Name).Should()
                    .BeEquivalentTo(on.Keys.Select(MockupVocabulary.Name), $"{id}'s events");

                foreach (var (@event, action) in on)
                {
                    var actual = written.GetProperty(MockupVocabulary.Name(@event));
                    Text(actual, "go").Should().Be(action.Go, $"{id} {@event} go");
                    Text(actual, "open").Should().Be(action.Open, $"{id} {@event} open");
                    Text(actual, "close").Should().Be(action.Close, $"{id} {@event} close");
                    var set = actual.TryGetProperty("set", out var s2)
                        ? s2.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
                        : new Dictionary<string, string>();
                    set.Should().BeEquivalentTo(action.Set ?? new Dictionary<string, string>(), $"{id} {@event} set");
                }
            }
        }

        // every-feature.json's save, help, next, edit, name, names, people-table, pages, close-about and back.
        withActions.Should().Be(10);

        static string? Text(JsonElement action, string name) =>
            action.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    [Fact]
    public void A_node_with_no_actions_carries_none()
    {
        var html = PrototypeRenderer.Render(EveryFeature(), []).Html;

        Attribute(Tag(html, "pass"), "data-mk-on").Should().BeNull();
        Attribute(Tag(html, "headline"), "data-mk-on").Should().BeNull();
    }

    [Fact]
    public void Each_node_starts_in_its_state_as_a_class()
    {
        var html = PrototypeRenderer.Render(EveryFeature(), []).Html;

        Classes(Tag(html, "save")).Should().Contain("mk-state-disabled");
        Classes(Tag(html, "status")).Should().Contain("mk-state-hidden");
        Classes(Tag(html, "names")).Should().NotContain(c => c.StartsWith("mk-state-", StringComparison.Ordinal));
    }

    [Fact]
    public void The_first_screen_is_shown_and_every_other_is_hidden()
    {
        var html = PrototypeRenderer.Render(EveryFeature(), []).Html;

        Attribute(Tag(html, "home"), "data-mk-screen").Should().NotBeNull();
        Attribute(Tag(html, "home"), "hidden").Should().BeNull();
        Attribute(Tag(html, "details"), "data-mk-screen").Should().NotBeNull();
        Attribute(Tag(html, "details"), "hidden").Should().Be("hidden");
    }

    [Theory]
    [InlineData(1, "details", "home")]
    [InlineData(7, "home", "details")]
    [InlineData(-1, "home", "details")]
    public void A_start_screen_is_shown_first_and_one_out_of_range_falls_back_to_the_first(int start, string shown, string hidden)
    {
        var html = PrototypeRenderer.Render(EveryFeature(), [], start).Html;

        Attribute(Tag(html, shown), "hidden").Should().BeNull();
        Attribute(Tag(html, hidden), "hidden").Should().Be("hidden");
    }

    [Fact]
    public void Tabs_show_their_first_panel_and_carry_what_the_script_switches_them_by()
    {
        var html = PrototypeRenderer.Render(EveryFeature(), []).Html;

        var tabs = Tag(html, "pages");
        Attribute(tabs, "data-mk-tabs").Should().NotBeNull();
        Attribute(tabs, "data-mk-select").Should().NotBeNull();
        // Backlog 238: each tab is its panel's part, so it names the panel it is the tab of.
        html.Should().Contain("<button data-mk-part-of=\"one\" data-mk-part=\"tab\" type=\"button\" role=\"tab\" data-mk-tab=\"0\" aria-selected=\"true\">One</button>");
        html.Should().Contain("<button data-mk-part-of=\"two\" data-mk-part=\"tab\" type=\"button\" role=\"tab\" data-mk-tab=\"1\" aria-selected=\"false\">Two</button>");
        Attribute(Tag(html, "one"), "hidden").Should().BeNull();
        Attribute(Tag(html, "two"), "hidden").Should().Be("hidden");
    }

    [Theory]
    [InlineData("names")]
    [InlineData("files")]
    [InlineData("people-table")]
    public void A_list_tree_and_table_are_marked_as_raising_select_from_their_rows(string key)
    {
        var html = PrototypeRenderer.Render(EveryFeature(), []).Html;

        Attribute(Tag(html, key), "data-mk-select").Should().Be("rows");
    }

    // ---- states: a class per state plus its override ------------------------------------------------------

    [Fact]
    public void A_states_text_is_a_variant_shown_while_the_node_is_in_that_state()
    {
        var html = PrototypeRenderer.Render(EveryFeature(), []).Html;

        html.Should().Contain("<span class=\"mk-text-variant mk-text-mk-state-loading\" data-mk-text-of=\"save\">Saving...</span>");
        html.Should().Contain(
            "[data-mk-anchor=\"save\"].mk-state-loading [data-mk-text-of=\"save\"] { display: none !important; } "
            + "[data-mk-anchor=\"save\"].mk-state-loading [data-mk-text-of=\"save\"].mk-text-mk-state-loading { display: inline !important; }");

        // A list with no caption of its own still has somewhere to say what its empty state says.
        html.Should().Contain("data-mk-text-of=\"names\">Nobody yet</span>");
    }

    [Fact]
    public void A_viewport_text_rule_names_its_own_node_and_never_reaches_one_inside_it()
    {
        var html = PrototypeRenderer.Render(EveryFeature(), []).Html;

        html.Should().Contain("[data-mk-text-of=\"side\"].mk-text-base { display: none; }");
        html.Should().NotContain("[data-mk-anchor=\"side\"] .mk-text-base");
    }

    [Fact]
    public void A_states_layout_and_columns_are_rules_under_its_class()
    {
        var document = Valid("""
            {"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [
                {"type": "panel", "id": "p", "layout": "column", "states": {"selected": {"layout": "row"}}},
                {"type": "table", "id": "t", "columns": [{"key": "name"}, {"key": "age"}], "items": [{"name": "Ada", "age": 36}],
                 "states": {"loading": {"columns": ["name"]}}}
            ]}]}
            """);

        var result = PrototypeRenderer.Render(document, []);

        result.Html.Should().Contain(
            "[data-mk-anchor=\"p\"].mk-state-selected { display: flex !important; flex-direction: row !important; gap: 0.75rem !important; }");
        result.Html.Should().Contain("[data-mk-anchor=\"t\"].mk-state-loading [data-mk-col=\"age\"] { display: none !important; }");
        result.Html.Should().NotContain("[data-mk-col=\"name\"] { display: none");
        result.UnexpressedOverrides.Should().BeEmpty();
    }

    [Fact]
    public void A_state_override_the_page_cannot_express_is_reported_with_its_state()
    {
        var document = Valid("""
            {"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [
                {"type": "field", "id": "f", "text": "Name", "states": {"error": {"placeholder": "Needed", "required": true}}}
            ]}]}
            """);

        var result = PrototypeRenderer.Render(document, []);

        result.UnexpressedOverrides.Should().Equal(
            new PrototypeUnexpressedOverride("f", null, "placeholder", "error"),
            new PrototypeUnexpressedOverride("f", null, "required", "error"));
    }

    [Fact]
    public void The_hidden_and_disabled_states_and_a_closed_dialog_cannot_be_shown_by_a_layout_rule()
    {
        var html = PrototypeRenderer.Render(EveryFeature(), []).Html;

        // "home" is laid out as a grid by a plain rule; [hidden] and a closed dialog have to win over that.
        html.Should().Contain("[hidden], dialog:not([open]), .mk-state-hidden { display: none !important; }");
        html.Should().Contain(".mk-state-disabled { opacity: 0.5; pointer-events: none; }");
    }

    // ---- the script and the policy ------------------------------------------------------------------------

    [Fact]
    public void The_page_carries_the_one_script_last_in_its_body()
    {
        var html = PrototypeRenderer.Render(EveryFeature(), []).Html;

        ScriptOf(html).Should().Be(PrototypeRenderer.Script);
        html.Should().EndWith($"<script>{PrototypeRenderer.Script}</script>\n</body>\n</html>\n");
        PrototypeRenderer.Script.Should().NotContain("</", "the script must never be able to close its own element");
    }

    [Fact]
    public void The_script_is_the_same_whatever_the_tree_so_its_hash_is_too()
    {
        var one = PrototypeRenderer.Render(EveryFeature(), []).Html;
        var other = PrototypeRenderer.Render(
            Valid("""{"mockup": 1, "screens": [{"type": "window", "id": "x", "children": []}]}"""), []).Html;

        ScriptOf(one).Should().Be(ScriptOf(other));
        PrototypeRenderer.ScriptHash.Should().Be(Sha256(PrototypeRenderer.Script));
    }

    [Fact]
    public void The_policy_is_the_first_element_of_the_head_and_admits_the_pages_script_by_its_hash()
    {
        var html = PrototypeRenderer.Render(EveryFeature(), []).Html;

        var head = "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta http-equiv=\"Content-Security-Policy\" content=\"";
        html.Should().StartWith(head);
        var policy = WebUtility.HtmlDecode(html[head.Length..html.IndexOf('"', head.Length)]);
        policy.Should().Be(PrototypeRenderer.ContentSecurityPolicy);
        policy.Should().Contain($"script-src '{Sha256(ScriptOf(html))}'");
    }

    [Fact]
    public void The_policy_allows_no_network_and_no_script_but_the_one()
    {
        var directives = PrototypeRenderer.ContentSecurityPolicy.Split(';', StringSplitOptions.TrimEntries)
            .ToDictionary(d => d.Split(' ')[0], d => d[(d.IndexOf(' ', StringComparison.Ordinal) + 1)..]);

        directives["default-src"].Should().Be("'none'");
        directives["script-src"].Should().Be($"'{PrototypeRenderer.ScriptHash}'");
        directives["style-src"].Should().Be("'unsafe-inline'");
        directives["img-src"].Should().Be("data:");
        directives["base-uri"].Should().Be("'none'");
        directives["form-action"].Should().Be("'none'");
        directives.Keys.Should().BeEquivalentTo(["default-src", "script-src", "style-src", "img-src", "base-uri", "form-action"]);
        PrototypeRenderer.ContentSecurityPolicy.Should().NotContain("unsafe-hashes").And.NotContain("unsafe-eval")
            .And.NotContain("http").And.NotContain("*");
    }

    [Fact]
    public void Rendering_stays_byte_identical_with_a_start_screen()
    {
        var document = EveryFeature();

        PrototypeRenderer.Render(document, [], 1).Html.Should().Be(PrototypeRenderer.Render(document, [], 1).Html);
    }

    // ---- what an overlay can and cannot reach --------------------------------------------------------------

    [Fact]
    public void A_fragment_the_guard_refuses_built_directly_is_left_unplaced_not_written()
    {
        var operation = new PrototypeInsertHtmlOperation(
            new PrototypeAnchor("save", null), PrototypePlacement.After, "<img src=x/onerror=alert(1)>");

        var result = PrototypeRenderer.Render(EveryFeature(), [operation]);

        result.Unplaced.Should().ContainSingle().Which.Should().BeSameAs(operation);
        result.Html.Should().NotContain("onerror");
    }

    [Fact]
    public void A_fragment_the_guard_misses_lands_and_the_policy_is_what_stands_against_it()
    {
        // An entity-encoded scheme is one of the ways past a pattern list. It lands raw, as every fragment
        // does; the browser decodes it into a javascript: URL, and the page's policy refuses that (no
        // 'unsafe-inline' for scripts), as the frame's own policy refuses navigating to anywhere else.
        var fragment = "<a href=\"&#106;avascript:alert(1)\">Help</a>";
        PrototypeHtmlGuard.FragmentRefusal(fragment).Should().BeNull();

        var result = PrototypeRenderer.Render(
            EveryFeature(), [new PrototypeInsertHtmlOperation(new PrototypeAnchor("save", null), PrototypePlacement.After, fragment)]);

        result.Unplaced.Should().BeEmpty();
        result.Html.Should().Contain(fragment);
        PrototypeRenderer.ContentSecurityPolicy.Should().Contain($"script-src '{PrototypeRenderer.ScriptHash}';");
        PrototypeRenderer.ContentSecurityPolicy.Split(';').Single(d => d.Trim().StartsWith("script-src", StringComparison.Ordinal))
            .Should().NotContain("unsafe-inline");
    }

    [Fact]
    public void An_overlay_cannot_rewire_the_page()
    {
        var operation = new PrototypeSetAttributeOperation(
            new PrototypeAnchor("names", null), "data-mk-on", """{"click":{"go":"details"}}""");

        var result = PrototypeRenderer.Render(EveryFeature(), [operation]);

        result.Unplaced.Should().ContainSingle().Which.Should().BeSameAs(operation);
        Attribute(Tag(result.Html, "names"), "data-mk-on").Should().Be("""{"select":{"go":"details"}}""");
    }
}
