using System.Globalization;
using System.Text.Json.Nodes;

namespace Fravashi.Application.Mockups;

/// <summary>Where the palette puts a new node, relative to the node that is selected.</summary>
public enum MockupPalettePlace
{
    /// <summary>Inside the selection as its last child when it may hold the new node, else just after it.</summary>
    Auto,

    /// <summary>Just after the selection, among its siblings.</summary>
    After,

    /// <summary>Just before the selection, among its siblings.</summary>
    Before,

    /// <summary>Inside the selection, as its last child.</summary>
    Inside,
}

/// <summary>
/// What choosing one palette type at one place would do: the ONE insert it is and where the node lands, or the
/// sentence saying why that place is refused.
/// </summary>
public sealed record MockupPaletteChoice(
    MockupComponent Component, MockupPalettePlace Place, MockupEdit? Edit, IReadOnlyList<int>? Lands, string? Refusal)
{
    /// <summary>Whether the place is offered: an insert the reader accepts.</summary>
    public bool Offered => Edit is not null;
}

/// <summary>
/// The component palette (#197, slice 5 of #51; ADR 0021 decision 5 puts it in stage one, as choose-and-insert rather
/// than drag). Each of the fifteen types inserts a node with defaults that make it VALID on its own (a button
/// labelled "Button", a table with one column and one sample row, an image marked decorative until somebody labels
/// it), with a fresh id where its type needs one, as one <see cref="MockupEdit.Insert"/>. Only the places the reader
/// accepts are offered: every place is dry-run through <see cref="MockupEditSession.Check"/>, so a refusal the reader
/// makes comes back in its words, and the palette's own rules about WHERE a type belongs come with a sentence too.
/// <para>
/// A window is only ever a new screen. A dialog goes only directly in a window, because a screen's dialogs are drawn
/// as layers over that screen (#194); the reader would take one deeper, the palette does not put one there. Tabs
/// hold only panels, each labelled as a tab, and inserting a panel while one of a tabs' own panels is selected
/// (Auto place) puts it AFTER that panel, as a new tab, rather than nesting it inside (#207); Ctrl+click still
/// reaches nesting it.
/// </para>
/// </summary>
public static class MockupPalette
{
    /// <summary>The fifteen types, in the palette's order (ADR 0021 decision 3, and backlog 290's link).</summary>
    public static IReadOnlyList<MockupComponent> Components { get; } = Enum.GetValues<MockupComponent>();

    /// <summary>
    /// What inserting <paramref name="component"/> at <paramref name="place"/> relative to <paramref name="selected"/>
    /// (a node path, or null when nothing is selected) would do in the tree as it is now.
    /// </summary>
    public static MockupPaletteChoice Plan(
        MockupEditSession session, IReadOnlyList<int>? selected, MockupComponent component, MockupPalettePlace place)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (selected is not null && !MockupOutline.Exists(session, selected))
        {
            selected = null;
        }

        var typeName = MockupVocabulary.Name(component);
        MockupPaletteChoice Refused(string why) => new(component, place, null, null, why);

        int[] parent;
        int index;
        if (component == MockupComponent.Window)
        {
            if (place == MockupPalettePlace.Inside)
            {
                return Refused("A window is a screen: it stands beside the other screens, never inside a node.");
            }

            if (selected is null)
            {
                if (place == MockupPalettePlace.Before)
                {
                    return Refused("Choose the screen it goes before: a node in the outline or the preview.");
                }

                (parent, index) = ([], Count(session.Tree["screens"]));
            }
            else
            {
                (parent, index) = ([], place == MockupPalettePlace.Before ? selected[0] : selected[0] + 1);
            }
        }
        else if (selected is null)
        {
            return Refused($"Choose where the {typeName} goes first: a node in the outline or the preview.");
        }
        else
        {
            var node = MockupTree.NodeAt(session.Tree, selected);
            var selectedType = Text(node, "type");
            var holds = MockupVocabulary.TryParse<MockupComponent>(selectedType, out var kind)
                && MockupVocabulary.Containers.Contains(kind);
            int[] beside = [.. selected.Take(selected.Count - 1)];

            if (place == MockupPalettePlace.Auto)
            {
                if (component == MockupComponent.Dialog)
                {
                    // A dialog's level is its screen's own children, wherever in the screen the selection is.
                    int[] screen = [selected[0]];
                    (parent, index) = (screen, Count(MockupTree.NodeAt(session.Tree, screen)["children"]));
                }
                else
                {
                    // A panel is almost always meant as a new tab when the selection's own PARENT is tabs (#207):
                    // nesting the new panel inside the selected tab would be the surprise, not the rule, there.
                    // Ctrl+click (place Inside, below) still reaches nesting it for the rare case that is wanted.
                    var newTabBesideSelection = component == MockupComponent.Panel
                        && Text(MockupTree.NodeAt(session.Tree, beside), "type") == MockupVocabulary.Name(MockupComponent.Tabs);
                    var inside = holds && (kind != MockupComponent.Tabs || component == MockupComponent.Panel) && !newTabBesideSelection;
                    (parent, index) = inside
                        ? ([.. selected], Count(node["children"]))
                        : (beside, selected[^1] + 1);
                }
            }
            else if (place == MockupPalettePlace.Inside)
            {
                if (!holds)
                {
                    return Refused($"A {selectedType ?? "node"} holds no children, so nothing goes inside it.");
                }

                (parent, index) = ([.. selected], Count(node["children"]));
            }
            else
            {
                (parent, index) = (beside, place == MockupPalettePlace.After ? selected[^1] + 1 : selected[^1]);
            }

            if (parent.Length == 0)
            {
                return Refused($"Only windows stand beside the screens. To put a {typeName} in this screen, insert it "
                    + "inside the screen, or choose a node in it.");
            }

            var parentType = Text(MockupTree.NodeAt(session.Tree, parent), "type");
            if (parentType == MockupVocabulary.Name(MockupComponent.Tabs) && component != MockupComponent.Panel)
            {
                return Refused($"Tabs hold panels, one per tab, so a {typeName} goes inside one of their panels, not "
                    + "in the tabs themselves.");
            }

            if (component == MockupComponent.Dialog && parent.Length != 1)
            {
                return Refused("A dialog opens over its whole screen, so it goes directly in a window, not inside a "
                    + $"{parentType ?? "node"}. Insert it with the screen or a node directly in it selected, or let "
                    + "the place choose itself.");
            }
        }

        var edit = MockupEdit.Insert(
            parent.Length == 0 ? MockupTarget.Screens : MockupTarget.At(parent), index, Node(session, component, parent));
        var outcome = session.Check(edit);
        return outcome.Applied
            ? new MockupPaletteChoice(component, place, edit, [.. parent, index], null)
            : Refused(string.Join(" ", outcome.Refusal.Select(e => e.Message)));
    }

    /// <summary>
    /// The new node of a type, as it would be inserted into <paramref name="parent"/> (a node path, or the screen list
    /// when empty): valid on its own, with a fresh id where its type needs one.
    /// </summary>
    public static JsonObject Node(MockupEditSession session, MockupComponent component, IReadOnlyList<int> parent)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(parent);

        JsonObject Laid(string? text)
        {
            var node = new JsonObject { ["type"] = MockupVocabulary.Name(component) };
            if (text is not null)
            {
                node["text"] = text;
            }

            node["children"] = new JsonArray();
            return node;
        }

        switch (component)
        {
            case MockupComponent.Window:
                var window = Laid("Screen");
                window.Insert(1, "id", FreshId(session, "screen"));
                return window;

            case MockupComponent.Dialog:
                var dialog = Laid("Dialog");
                dialog.Insert(1, "id", FreshId(session, "dialog"));
                return dialog;

            case MockupComponent.Panel:
                var inTabs = parent.Count > 0
                    && Text(MockupTree.NodeAt(session.Tree, parent), "type") == MockupVocabulary.Name(MockupComponent.Tabs);
                return Laid(inTabs
                    ? "Tab " + (Count(MockupTree.NodeAt(session.Tree, parent)["children"]) + 1).ToString(CultureInfo.InvariantCulture)
                    : "Panel");

            case MockupComponent.Toolbar or MockupComponent.Form or MockupComponent.Container:
                return Laid(null);

            case MockupComponent.Tabs:
                return new JsonObject
                {
                    ["type"] = "tabs",
                    ["children"] = new JsonArray(new JsonObject { ["type"] = "panel", ["text"] = "Tab 1", ["children"] = new JsonArray() }),
                };

            case MockupComponent.List:
                return new JsonObject { ["type"] = "list", ["items"] = new JsonArray("Item 1", "Item 2", "Item 3") };

            case MockupComponent.Tree:
                return new JsonObject
                {
                    ["type"] = "tree",
                    ["items"] = new JsonArray(new JsonObject { ["text"] = "Item 1", ["items"] = new JsonArray("Item 1.1") }, "Item 2"),
                };

            case MockupComponent.Table:
                return new JsonObject
                {
                    ["type"] = "table",
                    ["columns"] = new JsonArray(new JsonObject { ["key"] = "name", ["text"] = "Name" }),
                    ["items"] = new JsonArray(new JsonObject { ["name"] = "Row 1" }),
                };

            case MockupComponent.Field:
                return new JsonObject { ["type"] = "field", ["text"] = "Field" };

            case MockupComponent.Button:
                return new JsonObject { ["type"] = "button", ["text"] = "Button" };

            case MockupComponent.Link:
                return new JsonObject { ["type"] = "link", ["text"] = "Link" };

            case MockupComponent.Text:
                return new JsonObject { ["type"] = "text", ["text"] = "Text" };

            case MockupComponent.Image:
                // Decorative until somebody says what it shows: a label invented here would be a false one.
                return new JsonObject { ["type"] = "image", ["text"] = "Image", ["a11y"] = new JsonObject { ["decorative"] = true } };

            default:
                throw new ArgumentOutOfRangeException(nameof(component), component, "Not a palette type.");
        }
    }

    /// <summary>The first of <c>stem</c>, <c>stem-2</c>, <c>stem-3</c>, ... that no node in the tree carries.</summary>
    private static string FreshId(MockupEditSession session, string stem)
    {
        var used = MockupTree.Nodes(session.Tree).Select(n => Text(n, "id")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(stem))
        {
            return stem;
        }

        for (var n = 2; ; n++)
        {
            var candidate = stem + "-" + n.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static int Count(JsonNode? list) => list is JsonArray array ? array.Count : 0;

    private static string? Text(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
