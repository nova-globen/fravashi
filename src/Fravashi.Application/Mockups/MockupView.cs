using System.Globalization;
using System.Text.Json;

namespace Fravashi.Application.Mockups;

/// <summary>One row a list or tree shows: its words and, in a tree, the rows under it.</summary>
public sealed record MockupRow(string Text, IReadOnlyList<MockupRow> Children);

/// <summary>A node found by <see cref="MockupView.Walk"/>, with the JSON path the reader would name it by.</summary>
public sealed record MockupPlace(string Path, MockupNode Node);

/// <summary>
/// How a valid tree LOOKS for a chosen viewport and state (backlog 194): the read half of the editor, used by the
/// preview the studio draws. Pure, so what a viewport or a state changes is decided here and tested here, and the
/// component only draws the answer.
/// <para>
/// A node is its own properties, then its <see cref="MockupNode.At"/> override for the viewport, then its
/// <see cref="MockupNode.States"/> override for the state. State goes on top because a state is a moment and a
/// viewport is the frame it happens in: a button that says "Saving..." while loading says it at every width. Each
/// property an override carries replaces the node's; one it does not carry is left alone, which is how an empty
/// override means "the same, in that state" (@14).
/// </para>
/// </summary>
public static class MockupView
{
    /// <summary>What a node looks like at <paramref name="viewport"/> in <paramref name="state"/> (null: at rest).</summary>
    public static MockupNode Resolve(MockupNode node, string? viewport, MockupState? state)
    {
        var resolved = node;
        if (viewport is not null && node.At is { } at && at.TryGetValue(viewport, out var wide))
        {
            resolved = Apply(resolved, wide);
        }

        if (state is { } named && node.States is { } states && states.TryGetValue(named, out var look))
        {
            resolved = Apply(resolved, look);
        }

        return resolved;
    }

    /// <summary>A node with every property the override carries put over its own.</summary>
    public static MockupNode Apply(MockupNode node, MockupNode over) => node with
    {
        Text = over.Text ?? node.Text,
        Href = over.Href ?? node.Href,
        Layout = over.Layout ?? node.Layout,
        Cols = over.Cols ?? node.Cols,
        Input = over.Input ?? node.Input,
        Value = over.Value ?? node.Value,
        Placeholder = over.Placeholder ?? node.Placeholder,
        Required = over.Required ?? node.Required,
        Options = over.Options ?? node.Options,
        Data = over.Data ?? node.Data,
        Show = over.Show ?? node.Show,
        Columns = over.Columns ?? node.Columns,
        Items = over.Items ?? node.Items,
        A11y = over.A11y ?? node.A11y,
        Children = over.Children ?? node.Children,
    };

    /// <summary>
    /// The rows a list or tree shows: its inline items, or the <see cref="MockupNode.Show"/> key of each record in
    /// its bound data set. A tree's record nests the records of its first property that holds a list of records,
    /// because the schema lets a record carry such a list and names no key for it.
    /// </summary>
    public static IReadOnlyList<MockupRow> Rows(MockupNode node, MockupDocument document)
    {
        var nests = node.Type == MockupComponent.Tree;
        if (node.Items is { } items)
        {
            return items.Select(i => Item(i, nests)).ToArray();
        }

        if (node.Data is { } name && node.Show is { } show && Set(document, name) is { } records)
        {
            return records.Select(r => Record(r, show, nests)).ToArray();
        }

        return [];
    }

    /// <summary>The records a table shows: its inline items, or its bound data set.</summary>
    public static IReadOnlyList<JsonElement> Records(MockupNode node, MockupDocument document) =>
        node.Items ?? (node.Data is { } name ? Set(document, name) : null) ?? [];

    /// <summary>A record's value under <paramref name="key"/>, as the words a cell shows.</summary>
    public static string Cell(JsonElement record, string key) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(key, out var value) ? Words(value) : string.Empty;

    /// <summary>
    /// Every node under <paramref name="root"/>, the root included, in document order, with its path. Only the tree
    /// as written is walked: children an override brings in are drawn, but are not places a control can name.
    /// </summary>
    public static IEnumerable<MockupPlace> Walk(MockupNode root, string path)
    {
        var stack = new Stack<MockupPlace>();
        stack.Push(new MockupPlace(path, root));
        while (stack.Count > 0)
        {
            var place = stack.Pop();
            yield return place;
            if (place.Node.Children is { } children)
            {
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(new MockupPlace(ChildPath(place.Path, i), children[i]));
                }
            }
        }
    }

    /// <summary>The path of a screen, spelled as the reader spells it.</summary>
    public static string ScreenPath(int index) => $"$.screens[{index.ToString(CultureInfo.InvariantCulture)}]";

    /// <summary>The path of a child, spelled as the reader spells it.</summary>
    public static string ChildPath(string parent, int index) =>
        $"{parent}.children[{index.ToString(CultureInfo.InvariantCulture)}]";

    /// <summary>The words a node is known by in a control: its id, else its type and text.</summary>
    public static string Name(MockupNode node) =>
        node.Id ?? (node.Text is { } text ? $"{MockupVocabulary.Name(node.Type)} \"{text}\"" : MockupVocabulary.Name(node.Type));

    private static IReadOnlyList<JsonElement>? Set(MockupDocument document, string name) =>
        document.Data.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))?.Records;

    private static MockupRow Item(JsonElement item, bool nests)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return new MockupRow(Words(item), []);
        }

        var text = item.TryGetProperty("text", out var words) ? Words(words) : string.Empty;
        var children = nests && item.TryGetProperty("items", out var nested) && nested.ValueKind == JsonValueKind.Array
            ? nested.EnumerateArray().Select(i => Item(i, nests)).ToArray()
            : [];
        return new MockupRow(text, children);
    }

    private static MockupRow Record(JsonElement record, string show, bool nests)
    {
        var children = new List<MockupRow>();
        if (nests && record.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in record.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array
                    && property.Value.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object))
                {
                    children.AddRange(property.Value.EnumerateArray().Select(r => Record(r, show, nests)));
                    break;
                }
            }
        }

        return new MockupRow(Cell(record, show), children);
    }

    private static string Words(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => $"{value.GetArrayLength().ToString(CultureInfo.InvariantCulture)} items",
        _ => string.Empty,
    };
}
