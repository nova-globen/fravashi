using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fravashi.Application.Mockups;

/// <summary>A table column as the inspector edits it: the record key it shows, and its heading when it has one.</summary>
public sealed record MockupInspectorColumn(string Key, string? Heading);

/// <summary>
/// What the inspector shows for one node (#198): its content and layout properties as they are in the tree under
/// edit, and which of them its type offers. Read from the session's JSON, so a node of a tree the reader refuses can
/// still be inspected and repaired.
/// </summary>
public sealed record MockupInspectorNode
{
    /// <summary>The node's path, the screen first.</summary>
    public required IReadOnlyList<int> Path { get; init; }

    /// <summary>Its type as written; null when it names no component.</summary>
    public required MockupComponent? Component { get; init; }

    /// <summary>The properties offered for it, in canonical order (<see cref="MockupInspector.Offered"/>).</summary>
    public required IReadOnlyList<string> Offered { get; init; }

    public string? Id { get; init; }

    public string? Text { get; init; }

    /// <summary>A link's <c>href</c> (backlog 290).</summary>
    public string? Href { get; init; }

    public string? Layout { get; init; }

    /// <summary><c>cols</c> as text: a number, or whatever an invalid tree holds there.</summary>
    public string? Cols { get; init; }

    public string? Input { get; init; }

    /// <summary>A field's sample value as text; a checkbox's is <c>true</c> or <c>false</c>.</summary>
    public string? Value { get; init; }

    public string? Placeholder { get; init; }

    public bool Required { get; init; }

    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>The bound data set's name, when it has one.</summary>
    public string? Data { get; init; }

    public string? Show { get; init; }

    public IReadOnlyList<MockupInspectorColumn> Columns { get; init; } = [];

    /// <summary>A table's inline rows, each a record key to its value as text.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; init; } = [];

    /// <summary>A list's or tree's inline items as lines, a tree's nested by two spaces a level.</summary>
    public string ItemLines { get; init; } = "";

    /// <summary>Whether the node holds inline items (as opposed to none, or a data set).</summary>
    public bool HasItems { get; init; }

    /// <summary>The data sets the document declares, which a list, tree or table can be bound to.</summary>
    public IReadOnlyList<string> DataSets { get; init; } = [];

    /// <summary>The record keys of the bound data set, which <c>show</c> chooses from.</summary>
    public IReadOnlyList<string> DataKeys { get; init; } = [];

    /// <summary><c>a11y.label</c>, the accessible name (#199).</summary>
    public string? Label { get; init; }

    /// <summary><c>a11y.heading</c> as text: 1 to 6, or whatever an invalid tree holds there.</summary>
    public string? Heading { get; init; }

    /// <summary><c>a11y.live</c>: polite or assertive.</summary>
    public string? Live { get; init; }

    /// <summary><c>a11y.decorative</c> is true.</summary>
    public bool Decorative { get; init; }

    /// <summary>
    /// Read in an override (#199): the properties the override itself carries. Everything else shown is the node's own
    /// value at rest, which the override leaves as it is.
    /// </summary>
    public IReadOnlySet<string> Overridden { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Whether a property is offered for this node.</summary>
    public bool Offers(string property) => Offered.Contains(property, StringComparer.Ordinal);
}

/// <summary>
/// The property inspector's half of the editor (#198, slice 6 of #51): which content and layout properties a node
/// offers, by type as @14 lists them, and the ONE <see cref="MockupEdit"/> each change a person makes is. States,
/// viewports, interactions, accessibility and the data sets themselves are slice 7 (#199) and are not offered here.
/// <para>
/// A change that is one act for a person but touches two properties is one edit through
/// <see cref="MockupEdit.Together"/>: leaving the grid layout drops <c>cols</c>; switching a field's input away from
/// select or radio drops its options; renaming a table column renames that key in its inline rows. So undo takes back
/// exactly what the person did.
/// </para>
/// </summary>
public static partial class MockupInspector
{
    /// <summary>The properties this slice edits, in canonical order. Everything else a node carries is #199's or structural.</summary>
    public static IReadOnlyList<string> ContentProperties { get; } =
    [
        "id", "text", "href", "layout", "cols", "input", "value", "placeholder", "required", "options", "data", "show", "columns", "items",
    ];

    /// <summary>The two options a field switched to select or radio starts with, so the switch lands on a valid field.</summary>
    public static IReadOnlyList<string> SeedOptions { get; } = ["Option 1", "Option 2"];

    /// <summary>Reads what the inspector shows for the node at a path; null when there is no node there.</summary>
    public static MockupInspectorNode? Read(MockupEditSession session, IReadOnlyList<int> path)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(path);

        if (!MockupOutline.Exists(session, path))
        {
            return null;
        }

        var node = MockupTree.NodeAt(session.Tree, path);
        MockupComponent? component = MockupVocabulary.TryParse<MockupComponent>(Str(node, "type"), out var c) ? c : null;
        var data = Str(node, "data");
        var nests = component == MockupComponent.Tree;

        return new MockupInspectorNode
        {
            Path = [.. path],
            Component = component,
            Offered = Offered(node, component),
            Id = Str(node, "id"),
            Text = Str(node, "text"),
            Href = Str(node, "href"),
            Layout = Str(node, "layout"),
            Cols = node["cols"] is { } cols ? Display(cols) : null,
            Input = Str(node, "input"),
            Value = node["value"] is { } value ? Display(value) : null,
            Placeholder = Str(node, "placeholder"),
            Required = node["required"] is JsonValue r && r.GetValueKind() == JsonValueKind.True,
            Options = (node["options"] as JsonArray ?? []).Select(o => o is null ? "" : Display(o)).ToList(),
            Data = data,
            Show = Str(node, "show"),
            Columns = Columns(node),
            Rows = component == MockupComponent.Table
                ? (node["items"] as JsonArray ?? []).Select(row => (IReadOnlyDictionary<string, string>)(row as JsonObject ?? [])
                    .ToDictionary(p => p.Key, p => p.Value is null ? "" : Display(p.Value), StringComparer.Ordinal)).ToList()
                : [],
            ItemLines = component == MockupComponent.Table ? "" : Lines(node["items"] as JsonArray, nests),
            HasItems = node["items"] is JsonArray,
            DataSets = (session.Tree["data"] as JsonObject ?? []).Select(p => p.Key).ToList(),
            DataKeys = data is null ? [] : KeysOf(session, data),
            Label = Str(node["a11y"] as JsonObject, "label"),
            Heading = (node["a11y"] as JsonObject)?["heading"] is { } heading ? Display(heading) : null,
            Live = Str(node["a11y"] as JsonObject, "live"),
            Decorative = (node["a11y"] as JsonObject)?["decorative"] is JsonValue d && d.GetValueKind() == JsonValueKind.True,
        };
    }

    /// <summary>
    /// Which content properties a node offers. Only what its type may carry (@14), and of those: <c>cols</c> only with
    /// a grid layout, <c>options</c> only for select and radio, <c>show</c> only for a list or tree bound to data, and
    /// <c>items</c> only while it is not bound. A property the tree already holds is offered anyway where its type may
    /// carry it, so a broken tree can be repaired here. A node of no known type offers its id and text.
    /// </summary>
    private static IReadOnlyList<string> Offered(JsonObject node, MockupComponent? component)
    {
        if (component is not { } type)
        {
            return ["id", "text"];
        }

        var carried = MockupVocabulary.PropertiesOf(type);
        var input = Str(node, "input");
        var choosing = input is "select" or "radio";
        var bound = node.ContainsKey("data");

        return ContentProperties.Where(p => carried.Contains(p) && p switch
        {
            "cols" => Str(node, "layout") == "grid" || node.ContainsKey("cols"),
            "options" => choosing || node.ContainsKey("options"),
            "show" => bound || node.ContainsKey("show"),
            "items" => !bound || node.ContainsKey("items"),
            _ => true,
        }).ToList();
    }

    // ---- changes: each answers ONE edit, or null when the change changes nothing -----------------------------------

    /// <summary>
    /// A new id, or none when blank. It is a rename, so every go, open, close and set naming the old id follows it
    /// (#195's rule); clearing one that is needed is refused by the reader.
    /// </summary>
    public static MockupEdit? SetId(MockupEditSession session, IReadOnlyList<int> path, string? text)
    {
        var id = Blank(text) ? null : text!.Trim();
        return Str(Node(session, path), "id") == id ? null : MockupEdit.RenameId(Target(path), id);
    }

    /// <summary>A plain string property (<c>text</c>, <c>href</c> or <c>placeholder</c>), cleared when blank.</summary>
    public static MockupEdit? SetString(MockupEditSession session, IReadOnlyList<int> path, string property, string? text)
    {
        JsonNode? value = string.IsNullOrEmpty(text) ? null : JsonValue.Create(text);
        return Same(Node(session, path)[property], value) ? null : MockupEdit.SetProperty(Target(path), property, value);
    }

    /// <summary>The layout, or none. Leaving the grid drops <c>cols</c> in the same edit, since it means nothing without one.</summary>
    public static MockupEdit? SetLayout(MockupEditSession session, IReadOnlyList<int> path, string? layout)
    {
        var node = Node(session, path);
        layout = Blank(layout) ? null : layout;
        if (Str(node, "layout") == layout && node.ContainsKey("layout") == (layout is not null))
        {
            return null;
        }

        var set = MockupEdit.SetProperty(Target(path), "layout", layout is null ? null : JsonValue.Create(layout));
        return layout != "grid" && node.ContainsKey("cols")
            ? MockupEdit.Together(set, MockupEdit.SetProperty(Target(path), "cols", null))
            : set;
    }

    /// <summary><c>cols</c>, a whole number; blank clears it, and anything else is written as typed so the reader says why not.</summary>
    public static MockupEdit? SetCols(MockupEditSession session, IReadOnlyList<int> path, string? text)
    {
        JsonNode? value = Blank(text) ? null
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? JsonValue.Create(n)
            : JsonValue.Create(text!.Trim());
        return Same(Node(session, path)["cols"], value) ? null : MockupEdit.SetProperty(Target(path), "cols", value);
    }

    /// <summary>
    /// A field's input, or the default (text) when blank. Away from select and radio its options go, and so does a value
    /// that no longer fits (a choice, or a true or false that only a checkbox holds); onto select or radio with no
    /// options it starts with <see cref="SeedOptions"/>, keeping a value that is one of them.
    /// </summary>
    public static MockupEdit? SetInput(MockupEditSession session, IReadOnlyList<int> path, string? input)
    {
        var node = Node(session, path);
        input = Blank(input) ? null : input;
        if (Str(node, "input") == input && node.ContainsKey("input") == (input is not null))
        {
            return null;
        }

        var target = Target(path);
        var edits = new List<MockupEdit> { MockupEdit.SetProperty(target, "input", input is null ? null : JsonValue.Create(input)) };
        var choosing = input is "select" or "radio";
        var value = node["value"];

        if (!choosing && node.ContainsKey("options"))
        {
            edits.Add(MockupEdit.SetProperty(target, "options", null));
        }

        if (choosing && node["options"] is not JsonArray { Count: > 0 })
        {
            edits.Add(MockupEdit.SetProperty(target, "options", new JsonArray([.. SeedOptions.Select(o => (JsonNode)JsonValue.Create(o)!)])));
        }

        var options = choosing
            ? (node["options"] as JsonArray is { Count: > 0 } held ? held.Select(o => o is null ? "" : Display(o)).ToList() : SeedOptions)
            : null;
        var isBool = value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
        var fits = value is null
            || (input == "checkbox" ? isBool
                : choosing ? value is JsonValue s && s.GetValueKind() == JsonValueKind.String && options!.Contains(Display(s), StringComparer.Ordinal)
                : !isBool);
        if (!fits)
        {
            edits.Add(MockupEdit.SetProperty(target, "value", null));
        }

        return MockupEdit.Together([.. edits]);
    }

    /// <summary>
    /// A field's sample value, cleared when blank: true or false for a checkbox, a number for a number field when the
    /// text is one, and otherwise the text.
    /// </summary>
    public static MockupEdit? SetValue(MockupEditSession session, IReadOnlyList<int> path, string? text)
    {
        var node = Node(session, path);
        JsonNode? value = Blank(text) ? null : Str(node, "input") switch
        {
            "checkbox" => text!.Trim() switch { "true" => JsonValue.Create(true), "false" => JsonValue.Create(false), _ => JsonValue.Create(text) },
            "number" => Number(text!) ?? JsonValue.Create(text),
            _ => JsonValue.Create(text),
        };
        return Same(node["value"], value) ? null : MockupEdit.SetProperty(Target(path), "value", value);
    }

    /// <summary><c>required</c>: written when true, cleared when false (absent means not required).</summary>
    public static MockupEdit? SetRequired(MockupEditSession session, IReadOnlyList<int> path, bool required)
    {
        var node = Node(session, path);
        JsonNode? value = required ? JsonValue.Create(true) : null;
        return Same(node["required"], value) ? null : MockupEdit.SetProperty(Target(path), "required", value);
    }

    /// <summary>
    /// A select or radio field's options, in order, as the person left them; an empty list clears them (the reader then
    /// says the field needs some). The value follows: kept when it is still an option, renamed with its option when the
    /// person renamed that one, and cleared when its option is gone.
    /// </summary>
    public static MockupEdit? SetOptions(MockupEditSession session, IReadOnlyList<int> path, IReadOnlyList<string> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var node = Node(session, path);
        var target = Target(path);
        var before = (node["options"] as JsonArray ?? []).Select(o => o is null ? "" : Display(o)).ToList();
        JsonNode? list = options.Count == 0 ? null : new JsonArray([.. options.Select(o => (JsonNode)JsonValue.Create(o)!)]);
        if (Same(node["options"], list))
        {
            return null;
        }

        var edits = new List<MockupEdit> { MockupEdit.SetProperty(target, "options", list) };
        if (node["value"] is JsonValue v && v.GetValueKind() == JsonValueKind.String && Display(v) is var chosen
            && !options.Contains(chosen, StringComparer.Ordinal))
        {
            var at = before.IndexOf(chosen);
            var renamed = at >= 0 && before.Count == options.Count ? options[at] : null;
            edits.Add(MockupEdit.SetProperty(target, "value", renamed is null ? null : JsonValue.Create(renamed)));
        }

        return MockupEdit.Together([.. edits]);
    }

    /// <summary>
    /// Inline items of a list or tree from lines, one item a line, blank lines skipped; a tree nests a line under the one
    /// before it by two more spaces of indent. No lines clears them.
    /// </summary>
    public static MockupEdit? SetItemLines(MockupEditSession session, IReadOnlyList<int> path, string? lines)
    {
        var node = Node(session, path);
        var nests = Str(node, "type") == "tree";
        var items = ParseLines(lines ?? "", nests);
        JsonNode? value = items.Count == 0 ? null : items;
        if (Lines(node["items"] as JsonArray, nests) == Lines(items, nests) && node.ContainsKey("items") == (value is not null))
        {
            return null;
        }

        return MockupEdit.SetProperty(Target(path), "items", value);
    }

    /// <summary>
    /// Where a list, tree or table gets its rows: inline items when <paramref name="dataSet"/> is null, else the named
    /// data set. One edit either way. Going inline starts from sample items (a table's has one row with every column's
    /// key); binding a list or tree also chooses the set's first key to <c>show</c> unless the one it shows is a key of it.
    /// A table's columns are left as they are, and a column the set lacks is the reader's error to show.
    /// </summary>
    public static MockupEdit? SetSource(MockupEditSession session, IReadOnlyList<int> path, string? dataSet)
    {
        var node = Node(session, path);
        var target = Target(path);
        var isTable = Str(node, "type") == "table";
        dataSet = Blank(dataSet) ? null : dataSet;

        if (dataSet is null)
        {
            if (!node.ContainsKey("data") && !node.ContainsKey("show"))
            {
                return null;
            }

            var edits = new List<MockupEdit>
            {
                MockupEdit.SetProperty(target, "data", null),
                MockupEdit.SetProperty(target, "show", null),
            };
            if (!node.ContainsKey("items"))
            {
                JsonArray sample = isTable
                    ? [new JsonObject(Columns(node).Select((col, i) => KeyValuePair.Create(col.Key, (JsonNode?)JsonValue.Create(i == 0 ? "Row 1" : ""))))]
                    : ["Item 1", "Item 2", "Item 3"];
                edits.Add(MockupEdit.SetProperty(target, "items", sample));
            }

            return MockupEdit.Together([.. edits]);
        }

        if (Str(node, "data") == dataSet && !node.ContainsKey("items"))
        {
            return null;
        }

        var bind = new List<MockupEdit>
        {
            MockupEdit.SetProperty(target, "items", null),
            MockupEdit.SetProperty(target, "data", JsonValue.Create(dataSet)),
        };
        var keys = KeysOf(session, dataSet);
        if (!isTable && !keys.Contains(Str(node, "show") ?? "", StringComparer.Ordinal))
        {
            bind.Add(MockupEdit.SetProperty(target, "show", keys.Count == 0 ? null : JsonValue.Create(keys[0])));
        }

        return MockupEdit.Together([.. bind]);
    }

    /// <summary>The record key a list or tree bound to data shows.</summary>
    public static MockupEdit? SetShow(MockupEditSession session, IReadOnlyList<int> path, string? key) =>
        SetString(session, path, "show", Blank(key) ? null : key);

    // ---- a table's columns and rows ---------------------------------------------------------------------------------

    /// <summary>
    /// Renames column <paramref name="index"/>'s key. Its cells in the inline rows are renamed with it, in the same edit,
    /// because a column and its cells are one thing to the person editing the table.
    /// </summary>
    public static MockupEdit? RenameColumn(MockupEditSession session, IReadOnlyList<int> path, int index, string? key)
    {
        var node = Node(session, path);
        var columns = Columns(node).ToList();
        key = key?.Trim() ?? "";
        if (index < 0 || index >= columns.Count || columns[index].Key == key)
        {
            return null;
        }

        var old = columns[index].Key;
        columns[index] = columns[index] with { Key = key };
        var edits = new List<MockupEdit> { MockupEdit.SetProperty(Target(path), "columns", ColumnsJson(columns)) };
        if (node["items"] is JsonArray rows && key.Length > 0)
        {
            edits.Add(MockupEdit.SetProperty(Target(path), "items", new JsonArray([.. rows.Select(row => (JsonNode?)Renamed(row, old, key))])));
        }

        return MockupEdit.Together([.. edits]);
    }

    /// <summary>Column <paramref name="index"/>'s heading, or none when blank (the key is shown then).</summary>
    public static MockupEdit? SetColumnHeading(MockupEditSession session, IReadOnlyList<int> path, int index, string? heading)
    {
        var columns = Columns(Node(session, path)).ToList();
        heading = Blank(heading) ? null : heading;
        if (index < 0 || index >= columns.Count || columns[index].Heading == heading)
        {
            return null;
        }

        columns[index] = columns[index] with { Heading = heading };
        return MockupEdit.SetProperty(Target(path), "columns", ColumnsJson(columns));
    }

    /// <summary>
    /// A new last column. On a table bound to a data set: the first record key the table does not show yet, in the
    /// set's own key order, with that key as its heading; null when every key already has a column, so the caller
    /// says so instead of proposing one the reader would refuse (see <see cref="CanAddColumn"/>). On a table with
    /// inline rows: the first unused key of <c>column</c>, <c>column-2</c>, ...; inline rows get an empty cell for it.
    /// </summary>
    public static MockupEdit? AddColumn(MockupEditSession session, IReadOnlyList<int> path)
    {
        var node = Node(session, path);
        var columns = Columns(node).ToList();
        var dataSet = Str(node, "data");

        string key;
        string? heading;
        if (dataSet is not null)
        {
            var shown = columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            if (KeysOf(session, dataSet).FirstOrDefault(k => !shown.Contains(k)) is not { } next)
            {
                return null;
            }

            key = next;
            heading = next;
        }
        else
        {
            key = Fresh("column", columns.Select(c => c.Key));
            heading = "Column";
        }

        columns.Add(new MockupInspectorColumn(key, heading));
        var edits = new List<MockupEdit> { MockupEdit.SetProperty(Target(path), "columns", ColumnsJson(columns)) };
        if (node["items"] is JsonArray rows)
        {
            edits.Add(MockupEdit.SetProperty(Target(path), "items", new JsonArray([.. rows.Select(row =>
            {
                var copy = row?.DeepClone();
                if (copy is JsonObject record && !record.ContainsKey(key))
                {
                    record[key] = "";
                }

                return copy;
            })])));
        }

        return MockupEdit.Together([.. edits]);
    }

    /// <summary>
    /// Whether "Add a column" would add one: always true for a table with inline rows (it always has a fresh key to
    /// offer); for a table bound to a data set, only while that set still has a record key the table does not show.
    /// </summary>
    public static bool CanAddColumn(MockupInspectorNode node) =>
        node.Data is null || node.DataKeys.Any(k => !node.Columns.Any(c => c.Key == k));

    /// <summary>Removes column <paramref name="index"/>, and its cells from the inline rows. The last column cleared leaves the reader's "needs columns".</summary>
    public static MockupEdit? RemoveColumn(MockupEditSession session, IReadOnlyList<int> path, int index)
    {
        var node = Node(session, path);
        var columns = Columns(node).ToList();
        if (index < 0 || index >= columns.Count)
        {
            return null;
        }

        var key = columns[index].Key;
        columns.RemoveAt(index);
        var edits = new List<MockupEdit> { MockupEdit.SetProperty(Target(path), "columns", columns.Count == 0 ? null : ColumnsJson(columns)) };
        if (node["items"] is JsonArray rows && !columns.Any(c => c.Key == key))
        {
            edits.Add(MockupEdit.SetProperty(Target(path), "items", new JsonArray([.. rows.Select(row =>
            {
                var copy = row?.DeepClone();
                (copy as JsonObject)?.Remove(key);
                return copy;
            })])));
        }

        return MockupEdit.Together([.. edits]);
    }

    /// <summary>Moves column <paramref name="index"/> one place earlier (<paramref name="by"/> -1) or later (+1).</summary>
    public static MockupEdit? MoveColumn(MockupEditSession session, IReadOnlyList<int> path, int index, int by)
    {
        var columns = Columns(Node(session, path)).ToList();
        var to = index + by;
        if (index < 0 || index >= columns.Count || to < 0 || to >= columns.Count)
        {
            return null;
        }

        (columns[index], columns[to]) = (columns[to], columns[index]);
        return MockupEdit.SetProperty(Target(path), "columns", ColumnsJson(columns));
    }

    /// <summary>
    /// One cell of a table's inline row. A number stays a number and a true or false stays one when the new text is one,
    /// otherwise the cell holds the text.
    /// </summary>
    public static MockupEdit? SetCell(MockupEditSession session, IReadOnlyList<int> path, int row, string key, string? text)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Node(session, path)["items"] is not JsonArray rows || row < 0 || row >= rows.Count || rows[row] is not JsonObject record)
        {
            return null;
        }

        text ??= "";
        var old = record[key];
        JsonNode value = old is JsonValue v && v.GetValueKind() == JsonValueKind.Number && Number(text) is { } n ? n
            : old is JsonValue b && (b.GetValueKind() is JsonValueKind.True or JsonValueKind.False) && (text.Trim() is "true" or "false")
                ? JsonValue.Create(text.Trim() == "true")
            : JsonValue.Create(text)!;
        if (record.ContainsKey(key) && Same(old, value))
        {
            return null;
        }

        var copy = (JsonArray)rows.DeepClone();
        ((JsonObject)copy[row]!)[key] = value;
        return MockupEdit.SetProperty(Target(path), "items", copy);
    }

    /// <summary>A new last row with an empty cell for every column.</summary>
    public static MockupEdit AddRow(MockupEditSession session, IReadOnlyList<int> path)
    {
        var node = Node(session, path);
        var copy = node["items"] is JsonArray rows ? (JsonArray)rows.DeepClone() : [];
        copy.Add(new JsonObject(Columns(node).Select(c => KeyValuePair.Create(c.Key, (JsonNode?)JsonValue.Create("")))));
        return MockupEdit.SetProperty(Target(path), "items", copy);
    }

    /// <summary>Removes inline row <paramref name="row"/>; the last one gone leaves the table with no rows.</summary>
    public static MockupEdit? RemoveRow(MockupEditSession session, IReadOnlyList<int> path, int row)
    {
        if (Node(session, path)["items"] is not JsonArray rows || row < 0 || row >= rows.Count)
        {
            return null;
        }

        var copy = (JsonArray)rows.DeepClone();
        copy.RemoveAt(row);
        return MockupEdit.SetProperty(Target(path), "items", copy);
    }

    // ---- the document --------------------------------------------------------------------------------------------

    /// <summary>The document's title as the session holds it now.</summary>
    public static string? Title(MockupEditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Str(session.Tree, "title");
    }

    /// <summary>The document's title, or none when blank.</summary>
    public static MockupEdit? SetTitle(MockupEditSession session, string? title)
    {
        title = Blank(title) ? null : title;
        return Title(session) == title ? null : MockupEdit.SetTitle(title);
    }

    // ---- reading -------------------------------------------------------------------------------------------------

    /// <summary>Items as lines, a tree's nested by two spaces a level.</summary>
    private static string Lines(JsonArray? items, bool nests)
    {
        var text = new StringBuilder();
        void Walk(JsonArray list, int depth)
        {
            foreach (var item in list)
            {
                var words = item switch
                {
                    JsonValue value => Display(value),
                    JsonObject node => Str(node, "text") ?? "",
                    _ => "",
                };
                text.Append(' ', nests ? depth * 2 : 0).Append(words).Append('\n');
                if (nests && (item as JsonObject)?["items"] is JsonArray children)
                {
                    Walk(children, depth + 1);
                }
            }
        }

        if (items is not null)
        {
            Walk(items, 0);
        }

        return text.ToString().TrimEnd('\n');
    }

    private static JsonArray ParseLines(string lines, bool nests)
    {
        // Each line's depth is at most one deeper than the line before it, so a stray indent nests one level, not three.
        var entries = new List<(int Depth, string Words)>();
        foreach (var raw in lines.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var words = raw.Trim();
            if (words.Length > 0)
            {
                var indent = (raw.Length - raw.TrimStart(' ').Length) / 2;
                entries.Add((nests ? Math.Min(indent, entries.Count == 0 ? 0 : entries[^1].Depth + 1) : 0, words));
            }
        }

        var next = 0;
        JsonArray Build(int depth)
        {
            var list = new JsonArray();
            while (next < entries.Count && entries[next].Depth == depth)
            {
                var words = entries[next++].Words;
                list.Add(next < entries.Count && entries[next].Depth > depth
                    ? new JsonObject { ["text"] = words, ["items"] = Build(depth + 1) }
                    : JsonValue.Create(words));
            }

            return list;
        }

        return Build(0);
    }

    private static IReadOnlyList<MockupInspectorColumn> Columns(JsonObject node) =>
        (node["columns"] as JsonArray ?? []).Select(column => column switch
        {
            JsonValue key => new MockupInspectorColumn(Display(key), null),
            JsonObject full => new MockupInspectorColumn(Str(full, "key") ?? "", Str(full, "text")),
            _ => new MockupInspectorColumn("", null),
        }).ToList();

    private static JsonArray ColumnsJson(IEnumerable<MockupInspectorColumn> columns) =>
        [.. columns.Select(c => (JsonNode)(c.Heading is null
            ? new JsonObject { ["key"] = c.Key }
            : new JsonObject { ["key"] = c.Key, ["text"] = c.Heading }))];

    private static JsonNode? Renamed(JsonNode? row, string old, string key)
    {
        if (row is not JsonObject record || !record.ContainsKey(old) || record.ContainsKey(key))
        {
            return row?.DeepClone();
        }

        var copy = new JsonObject();
        foreach (var (name, value) in record)
        {
            copy[name == old ? key : name] = value?.DeepClone();
        }

        return copy;
    }

    private static List<string> KeysOf(MockupEditSession session, string dataSet) =>
        ((session.Tree["data"] as JsonObject)?[dataSet] as JsonArray ?? [])
            .OfType<JsonObject>().SelectMany(r => r.Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToList();

    internal static string Fresh(string stem, IEnumerable<string> used)
    {
        var taken = used.ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(stem))
        {
            return stem;
        }

        for (var n = 2; ; n++)
        {
            var candidate = stem + "-" + n.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    internal static JsonObject Node(MockupEditSession session, IReadOnlyList<int> path)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(path);
        return MockupOutline.Exists(session, path)
            ? MockupTree.NodeAt(session.Tree, path)
            : throw new ArgumentException($"There is no node at {MockupTree.JsonPath(path)}.", nameof(path));
    }

    internal static MockupTarget Target(IReadOnlyList<int> path) => MockupTarget.At([.. path]);

    internal static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);

    internal static JsonNode? Number(string text)
    {
        var trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(trimmed) is JsonValue n && n.GetValueKind() == JsonValueKind.Number ? n : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    /// <summary>A value as the inspector shows it: a string as itself, anything else as its JSON.</summary>
    internal static string Display(JsonNode node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : node.ToJsonString();

    internal static string? Str(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
