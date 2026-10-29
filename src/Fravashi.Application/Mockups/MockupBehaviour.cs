using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fravashi.Application.Mockups;

/// <summary>One node an action's <c>set</c> names, and the state it shows it in (one of its own, or <c>default</c>).</summary>
public sealed record MockupSetTarget(string Id, string State);

/// <summary>An action as the inspector edits it: each part optional, and nothing at all means no action.</summary>
public sealed record MockupInspectorAction(string? Go, string? Open, string? Close, IReadOnlyList<MockupSetTarget> Set)
{
    /// <summary>No action.</summary>
    public static MockupInspectorAction None { get; } = new(null, null, null, []);

    /// <summary>Whether it does nothing, in which case it is not written.</summary>
    public bool IsEmpty => Go is null && Open is null && Close is null && Set.Count == 0;
}

/// <summary>A node with an id, which an action's <c>set</c> may name, and the states it describes.</summary>
public sealed record MockupSetCandidate(string Id, string Type, IReadOnlyList<string> States);

/// <summary>A declared viewport.</summary>
public sealed record MockupInspectorViewport(string Name, string Width);

/// <summary>One value of a sample record as the inspector shows it.</summary>
public sealed record MockupDataCell(string Text, MockupDataCellKind Kind);

/// <summary>What a sample record holds under a key.</summary>
public enum MockupDataCellKind
{
    /// <summary>The record has no such key.</summary>
    Absent,
    String,
    Number,
    Boolean,
    Null,

    /// <summary>A list of records: shown, not edited here.</summary>
    Records,
}

/// <summary>
/// A sample data set as a small table: its keys, ordinally sorted (the order <see cref="MockupWriter"/> stores
/// them in, #207: so the inspector never reorders them once more on Save), and its records.
/// </summary>
public sealed record MockupInspectorDataSet(
    string Name,
    IReadOnlyList<string> Keys,
    IReadOnlyList<IReadOnlyDictionary<string, MockupDataCell>> Records,
    IReadOnlyList<string> BoundBy);

/// <summary>What the inspector shows of a node's behaviour (#199): its states, viewport overrides and interactions.</summary>
public sealed record MockupBehaviourNode
{
    public required IReadOnlyList<int> Path { get; init; }

    public required MockupComponent? Component { get; init; }

    /// <summary>The node's id, which a <c>set</c> names it by; a node with none cannot be set.</summary>
    public string? Id { get; init; }

    /// <summary>The states it describes, in the vocabulary's order.</summary>
    public IReadOnlyList<string> States { get; init; } = [];

    /// <summary>The state it starts in, when it names one.</summary>
    public string? InitialState { get; init; }

    /// <summary>The viewports the document declares.</summary>
    public IReadOnlyList<string> Viewports { get; init; } = [];

    /// <summary>The viewports it has an override for, as written (a stray one included).</summary>
    public IReadOnlyList<string> AtViewports { get; init; } = [];

    /// <summary>The events its type raises, and any other event it already carries.</summary>
    public IReadOnlyList<string> Events { get; init; } = [];

    /// <summary>Its action per event, for every event it carries.</summary>
    public IReadOnlyDictionary<string, MockupInspectorAction> Actions { get; init; } = new Dictionary<string, MockupInspectorAction>();

    /// <summary>The screens <c>go</c> may name.</summary>
    public IReadOnlyList<string> Screens { get; init; } = [];

    /// <summary>The dialogs <c>open</c> and <c>close</c> may name.</summary>
    public IReadOnlyList<string> Dialogs { get; init; } = [];

    /// <summary>The nodes <c>set</c> may name.</summary>
    public IReadOnlyList<MockupSetCandidate> Settable { get; init; } = [];

    /// <summary>The nodes whose actions set THIS node in a state, by state: what dropping that state also removes.</summary>
    public IReadOnlyDictionary<string, int> SetBy { get; init; } = new Dictionary<string, int>();
}

/// <summary>
/// The inspector's half for what #78 made the schema carry for the verifier (#199, slice 7 of #51): component states,
/// responsive variants, interactions, accessibility intent and sample data. Like <see cref="MockupInspector"/>, it reads
/// from the session's JSON (so a broken tree can be repaired) and answers ONE <see cref="MockupEdit"/> per change a
/// person makes, or null when nothing would change.
/// <para>
/// <b>What a removal takes with it</b> (decided here, recorded on #199): dropping a state also drops the initial
/// <c>state</c> naming it and every <c>set</c> showing this node in it; removing a viewport also removes every node's
/// override for it; both in the same edit, so one undo puts everything back. A state or viewport entry is a description,
/// and once it is gone what named it means nothing. Removing a SCREEN or a DIALOG something references stays refused by
/// the reader, naming each reference (#195's structural rule, as the outline's Delete already does), and so do removing a
/// bound data set and renaming or removing a key a binding uses: those take away content or data another node shows, and
/// what to put there instead is the person's choice.
/// </para>
/// </summary>
public static class MockupBehaviour
{
    /// <summary>The widths offered for a new viewport, by its name; any other is 1024.</summary>
    private static readonly (string Name, int Width)[] KnownViewports = [("wide", 1280), ("narrow", 390), ("medium", 768)];

    // ---- reading -----------------------------------------------------------------------------------------------

    /// <summary>What the inspector shows of a node's behaviour; null when there is no node there.</summary>
    public static MockupBehaviourNode? Read(MockupEditSession session, IReadOnlyList<int> path)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(path);

        if (!MockupOutline.Exists(session, path))
        {
            return null;
        }

        var tree = session.Tree;
        var node = MockupTree.NodeAt(tree, path);
        MockupComponent? component = MockupVocabulary.TryParse<MockupComponent>(MockupInspector.Str(node, "type"), out var c) ? c : null;
        var on = node["on"] as JsonObject ?? [];
        IEnumerable<string> raised = component is { } type
            ? MockupVocabulary.Names<MockupEvent>().Where(e => MockupVocabulary.TryParse<MockupEvent>(e, out var ev) && MockupVocabulary.Raises(type, ev))
            : [];
        var id = MockupInspector.Str(node, "id");

        var setBy = new Dictionary<string, int>(StringComparer.Ordinal);
        if (id is not null)
        {
            foreach (var (_, other) in Walk(tree))
            {
                foreach (var action in (other["on"] as JsonObject ?? []).Select(p => p.Value).OfType<JsonObject>())
                {
                    if ((action["set"] as JsonObject)?[id] is JsonValue s && s.TryGetValue<string>(out var state))
                    {
                        setBy[state] = setBy.GetValueOrDefault(state) + 1;
                    }
                }
            }
        }

        return new MockupBehaviourNode
        {
            Path = [.. path],
            Component = component,
            Id = id,
            States = StatesOf(node),
            InitialState = MockupInspector.Str(node, "state"),
            Viewports = ViewportNames(tree),
            AtViewports = (node["at"] as JsonObject ?? []).Select(p => p.Key).ToList(),
            Events = raised.Concat(on.Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToList(),
            Actions = on.ToDictionary(p => p.Key, p => ActionOf(p.Value as JsonObject), StringComparer.Ordinal),
            Screens = (tree["screens"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(s => MockupInspector.Str(s, "id")).OfType<string>().ToList(),
            Dialogs = Walk(tree).Where(n => MockupInspector.Str(n.Node, "type") == "dialog")
                .Select(n => MockupInspector.Str(n.Node, "id")).OfType<string>().ToList(),
            Settable = Walk(tree).Where(n => MockupInspector.Str(n.Node, "id") is not null)
                .Select(n => new MockupSetCandidate(MockupInspector.Str(n.Node, "id")!, MockupInspector.Str(n.Node, "type") ?? "", StatesOf(n.Node)))
                .ToList(),
            SetBy = setBy,
        };
    }

    // ---- states ------------------------------------------------------------------------------------------------

    /// <summary>Describes a state on a node, the same as at rest until its override says otherwise (<c>{}</c>).</summary>
    public static MockupEdit? DescribeState(MockupEditSession session, IReadOnlyList<int> path, MockupState state)
    {
        var name = MockupVocabulary.Name(state);
        return (MockupInspector.Node(session, path)["states"] as JsonObject)?.ContainsKey(name) == true
            ? null
            : MockupEdit.SetEntry(MockupInspector.Target(path), MockupEntryMap.States, name, new JsonObject());
    }

    /// <summary>
    /// Drops a state, and in the same edit the initial <c>state</c> when it names it and every <c>set</c> that shows this
    /// node in it (an action left with nothing to do goes with its event).
    /// </summary>
    public static MockupEdit? DropState(MockupEditSession session, IReadOnlyList<int> path, string state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var node = MockupInspector.Node(session, path);
        var states = node["states"] as JsonObject;
        if (states?.ContainsKey(state) != true && MockupInspector.Str(node, "state") != state)
        {
            return null;
        }

        var target = MockupInspector.Target(path);
        var edits = new List<MockupEdit>();
        if (states?.ContainsKey(state) == true)
        {
            edits.Add(MockupEdit.SetEntry(target, MockupEntryMap.States, state, null));
        }

        if (MockupInspector.Str(node, "state") == state)
        {
            edits.Add(MockupEdit.SetProperty(target, "state", null));
        }

        if (MockupInspector.Str(node, "id") is { } id)
        {
            foreach (var (otherPath, other) in Walk(session.Tree))
            {
                foreach (var (@event, value) in (other["on"] as JsonObject ?? []).ToList())
                {
                    if (value is JsonObject action && (action["set"] as JsonObject)?[id] is JsonValue s
                        && s.TryGetValue<string>(out var named) && named == state)
                    {
                        var current = ActionOf(action);
                        var kept = current with { Set = [.. current.Set.Where(t => t.Id != id)] };
                        edits.Add(MockupEdit.SetEntry(MockupInspector.Target(otherPath), MockupEntryMap.On, @event, Json(kept)));
                    }
                }
            }
        }

        return MockupEdit.Together([.. edits]);
    }

    /// <summary>The state a node starts in, or at rest when null. Only a described state is offered.</summary>
    public static MockupEdit? SetInitialState(MockupEditSession session, IReadOnlyList<int> path, string? state)
    {
        state = MockupInspector.Blank(state) ? null : state;
        return MockupInspector.Str(MockupInspector.Node(session, path), "state") == state
            ? null
            : MockupEdit.SetProperty(MockupInspector.Target(path), "state", state is null ? null : JsonValue.Create(state));
    }

    // ---- a node's viewport overrides ---------------------------------------------------------------------------

    /// <summary>Gives a node an override at a declared viewport, the same as everywhere else until it says otherwise.</summary>
    public static MockupEdit? AddViewportOverride(MockupEditSession session, IReadOnlyList<int> path, string viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        return (MockupInspector.Node(session, path)["at"] as JsonObject)?.ContainsKey(viewport) == true
            ? null
            : MockupEdit.SetEntry(MockupInspector.Target(path), MockupEntryMap.At, viewport, new JsonObject());
    }

    /// <summary>Removes a node's override at a viewport. Nothing else names it.</summary>
    public static MockupEdit? DropViewportOverride(MockupEditSession session, IReadOnlyList<int> path, string viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        return (MockupInspector.Node(session, path)["at"] as JsonObject)?.ContainsKey(viewport) == true
            ? MockupEdit.SetEntry(MockupInspector.Target(path), MockupEntryMap.At, viewport, null)
            : null;
    }

    // ---- interactions ------------------------------------------------------------------------------------------

    /// <summary>
    /// What a node does on an event, written whole: an action with nothing in it is not written, and its event goes.
    /// </summary>
    public static MockupEdit? SetAction(MockupEditSession session, IReadOnlyList<int> path, string @event, MockupInspectorAction action)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(action);

        var current = (MockupInspector.Node(session, path)["on"] as JsonObject)?[@event];
        var next = Json(action);
        return MockupInspector.Same(current, next)
            ? null
            : MockupEdit.SetEntry(MockupInspector.Target(path), MockupEntryMap.On, @event, next);
    }

    // ---- accessibility -----------------------------------------------------------------------------------------

    /// <summary>The accessible name, cleared when blank. Setting one clears <c>decorative</c> in the same edit.</summary>
    public static MockupEdit? SetLabel(MockupEditSession session, IReadOnlyList<int> path, string? label)
    {
        var a11y = MockupInspector.Node(session, path)["a11y"] as JsonObject;
        label = string.IsNullOrEmpty(label) ? null : label;
        if (MockupInspector.Str(a11y, "label") == label && a11y?.ContainsKey("label") == (label is not null))
        {
            return null;
        }

        var target = MockupInspector.Target(path);
        var set = MockupEdit.SetEntry(target, MockupEntryMap.A11y, "label", label is null ? null : JsonValue.Create(label));
        return label is not null && a11y?.ContainsKey("decorative") == true
            ? MockupEdit.Together(MockupEdit.SetEntry(target, MockupEntryMap.A11y, "decorative", null), set)
            : set;
    }

    /// <summary>Marks a node decorative (skipped by assistive technology), clearing its label in the same edit; or not.</summary>
    public static MockupEdit? SetDecorative(MockupEditSession session, IReadOnlyList<int> path, bool decorative)
    {
        var a11y = MockupInspector.Node(session, path)["a11y"] as JsonObject;
        var now = a11y?["decorative"] is JsonValue d && d.GetValueKind() == JsonValueKind.True;
        if (now == decorative && (decorative || a11y?.ContainsKey("decorative") != true))
        {
            return null;
        }

        var target = MockupInspector.Target(path);
        if (!decorative)
        {
            return MockupEdit.SetEntry(target, MockupEntryMap.A11y, "decorative", null);
        }

        var set = MockupEdit.SetEntry(target, MockupEntryMap.A11y, "decorative", JsonValue.Create(true));
        return a11y?.ContainsKey("label") == true
            ? MockupEdit.Together(MockupEdit.SetEntry(target, MockupEntryMap.A11y, "label", null), set)
            : set;
    }

    /// <summary>The heading level, 1 to 6, or none when blank; anything else is written as typed so the reader says why not.</summary>
    public static MockupEdit? SetHeading(MockupEditSession session, IReadOnlyList<int> path, string? level)
    {
        JsonNode? value = MockupInspector.Blank(level) ? null
            : int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? JsonValue.Create(n)
            : JsonValue.Create(level!.Trim());
        return A11yEntry(session, path, "heading", value);
    }

    /// <summary>How changes are announced: polite, assertive, or not at all when blank.</summary>
    public static MockupEdit? SetLive(MockupEditSession session, IReadOnlyList<int> path, string? live) =>
        A11yEntry(session, path, "live", MockupInspector.Blank(live) ? null : JsonValue.Create(live));

    private static MockupEdit? A11yEntry(MockupEditSession session, IReadOnlyList<int> path, string key, JsonNode? value)
    {
        var a11y = MockupInspector.Node(session, path)["a11y"] as JsonObject;
        return MockupInspector.Same(a11y?[key], value) && a11y?.ContainsKey(key) == (value is not null)
            ? null
            : MockupEdit.SetEntry(MockupInspector.Target(path), MockupEntryMap.A11y, key, value);
    }

    // ---- the document's viewports ------------------------------------------------------------------------------

    /// <summary>The viewports the document declares, in order.</summary>
    public static IReadOnlyList<MockupInspectorViewport> Viewports(MockupEditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return (session.Tree["viewports"] as JsonArray ?? []).Select(v => v as JsonObject).Select(v => new MockupInspectorViewport(
            MockupInspector.Str(v, "name") ?? "", v?["width"] is { } width ? MockupInspector.Display(width) : "")).ToList();
    }

    /// <summary>Declares a new last viewport: wide, narrow or medium when unused (with their widths), else viewport-N at 1024.</summary>
    public static MockupEdit AddViewport(MockupEditSession session)
    {
        var used = Viewports(session).Select(v => v.Name).ToList();
        var (name, width) = KnownViewports.FirstOrDefault(k => !used.Contains(k.Name, StringComparer.Ordinal));
        if (name is null)
        {
            (name, width) = (MockupInspector.Fresh("viewport", used), 1024);
        }

        return MockupEdit.InsertViewport(used.Count, name, width);
    }

    /// <summary>Renames a viewport, and every node's override for it with it, in one edit.</summary>
    public static MockupEdit? RenameViewport(MockupEditSession session, string name, string? to)
    {
        ArgumentNullException.ThrowIfNull(name);

        to = to?.Trim() ?? "";
        var viewports = Viewports(session).ToList();
        var index = viewports.FindIndex(v => v.Name == name);
        if (index < 0 || to == name)
        {
            return null;
        }

        var width = int.TryParse(viewports[index].Width, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 0;
        var edits = new List<MockupEdit> { MockupEdit.InsertViewport(index, to, width), MockupEdit.RemoveViewport(name) };
        foreach (var (path, node) in Walk(session.Tree))
        {
            if ((node["at"] as JsonObject)?[name] is { } over)
            {
                edits.Add(MockupEdit.SetEntry(MockupTarget.At([.. path]), MockupEntryMap.At, name, null));
                edits.Add(MockupEdit.SetEntry(MockupTarget.At([.. path]), MockupEntryMap.At, to, over.DeepClone()));
            }
        }

        return MockupEdit.Together([.. edits]);
    }

    /// <summary>A viewport's width in CSS pixels; a width out of range is refused in the reader's words.</summary>
    public static MockupEdit? ResizeViewport(MockupEditSession session, string name, string? width)
    {
        ArgumentNullException.ThrowIfNull(name);

        var viewports = Viewports(session).ToList();
        var index = viewports.FindIndex(v => v.Name == name);
        var parsed = int.TryParse(width?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 0;
        if (index < 0 || viewports[index].Width == parsed.ToString(CultureInfo.InvariantCulture))
        {
            return null;
        }

        return MockupEdit.Together(MockupEdit.RemoveViewport(name), MockupEdit.InsertViewport(index, name, parsed));
    }

    /// <summary>Removes a viewport, and every node's override for it, in one edit.</summary>
    public static MockupEdit? RemoveViewport(MockupEditSession session, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Viewports(session).All(v => v.Name != name))
        {
            return null;
        }

        var edits = new List<MockupEdit>();
        foreach (var (path, node) in Walk(session.Tree))
        {
            if ((node["at"] as JsonObject)?.ContainsKey(name) == true)
            {
                edits.Add(MockupEdit.SetEntry(MockupTarget.At([.. path]), MockupEntryMap.At, name, null));
            }
        }

        edits.Add(MockupEdit.RemoveViewport(name));
        return MockupEdit.Together([.. edits]);
    }

    // ---- sample data -------------------------------------------------------------------------------------------

    /// <summary>The document's sample data sets as small tables, each with the nodes bound to it.</summary>
    public static IReadOnlyList<MockupInspectorDataSet> DataSets(MockupEditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var bound = Walk(session.Tree)
            .Where(n => MockupInspector.Str(n.Node, "data") is not null)
            .Select(n => (Set: MockupInspector.Str(n.Node, "data")!, By: MockupInspector.Str(n.Node, "id") ?? MockupTree.JsonPath(n.Path)))
            .ToList();
        return (session.Tree["data"] as JsonObject ?? []).Select(set =>
        {
            var records = (set.Value as JsonArray ?? []).Select(r => r as JsonObject ?? []).ToList();
            var keys = records.SelectMany(r => r.Select(p => p.Key)).Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            return new MockupInspectorDataSet(
                set.Key,
                keys,
                [.. records.Select(r => (IReadOnlyDictionary<string, MockupDataCell>)keys.ToDictionary(k => k, k => Cell(r, k), StringComparer.Ordinal))],
                [.. bound.Where(b => b.Set == set.Key).Select(b => b.By)]);
        }).ToList();
    }

    /// <summary>A new data set, sample1 (or the first unused sampleN), holding one record.</summary>
    public static MockupEdit AddDataSet(MockupEditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var used = (session.Tree["data"] as JsonObject ?? []).Select(p => p.Key).ToList();
        var name = Enumerable.Range(1, int.MaxValue - 1).Select(n => "sample" + n.ToString(CultureInfo.InvariantCulture))
            .First(n => !used.Contains(n, StringComparer.Ordinal));
        return MockupEdit.SetDataSet(name, [new JsonObject { ["name"] = "Sample 1" }]);
    }

    /// <summary>Renames a data set, keeping its place among the others, and every node bound to it with it, in one edit.</summary>
    public static MockupEdit? RenameDataSet(MockupEditSession session, string name, string? to)
    {
        ArgumentNullException.ThrowIfNull(name);

        to = to?.Trim() ?? "";
        if (Records(session, name) is null || to == name)
        {
            return null;
        }

        var edits = new List<MockupEdit> { MockupEdit.RenameDataSet(name, to) };
        foreach (var (path, node) in Walk(session.Tree))
        {
            if (MockupInspector.Str(node, "data") == name)
            {
                edits.Add(MockupEdit.SetProperty(MockupTarget.At([.. path]), "data", JsonValue.Create(to)));
            }
        }

        return MockupEdit.Together([.. edits]);
    }

    /// <summary>Removes a data set. Refused by the reader, naming each binding, while a node is bound to it.</summary>
    public static MockupEdit? RemoveDataSet(MockupEditSession session, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Records(session, name) is null ? null : MockupEdit.SetDataSet(name, null);
    }

    /// <summary>A new last record with an empty text under every key.</summary>
    public static MockupEdit? AddRecord(MockupEditSession session, string name)
    {
        if (Records(session, name) is not { } records)
        {
            return null;
        }

        var copy = (JsonArray)records.DeepClone();
        copy.Add(new JsonObject(Keys(records).Select(k => KeyValuePair.Create(k, (JsonNode?)JsonValue.Create("")))));
        return MockupEdit.SetDataSet(name, copy);
    }

    /// <summary>Removes record <paramref name="index"/>.</summary>
    public static MockupEdit? RemoveRecord(MockupEditSession session, string name, int index)
    {
        if (Records(session, name) is not { } records || index < 0 || index >= records.Count)
        {
            return null;
        }

        var copy = (JsonArray)records.DeepClone();
        copy.RemoveAt(index);
        return MockupEdit.SetDataSet(name, copy);
    }

    /// <summary>A new key, key (or key-N), with an empty text in every record.</summary>
    public static MockupEdit? AddKey(MockupEditSession session, string name)
    {
        if (Records(session, name) is not { } records)
        {
            return null;
        }

        var key = MockupInspector.Fresh("key", Keys(records));
        var copy = (JsonArray)records.DeepClone();
        if (copy.Count == 0)
        {
            copy.Add(new JsonObject());
        }

        foreach (var record in copy.OfType<JsonObject>())
        {
            record[key] = "";
        }

        return MockupEdit.SetDataSet(name, copy);
    }

    /// <summary>
    /// Renames a key in every record, keeping its place. Refused by the reader when a binding uses the old key (a
    /// table's column or a list's <c>show</c>).
    /// </summary>
    public static MockupEdit? RenameKey(MockupEditSession session, string name, string key, string? to)
    {
        ArgumentNullException.ThrowIfNull(key);

        to = to?.Trim() ?? "";
        if (Records(session, name) is not { } records || to == key || to.Length == 0)
        {
            return null;
        }

        return MockupEdit.SetDataSet(name, [.. records.Select(r =>
        {
            if (r is not JsonObject record || !record.ContainsKey(key) || record.ContainsKey(to))
            {
                return r?.DeepClone();
            }

            var copy = new JsonObject();
            foreach (var (k, v) in record)
            {
                copy[k == key ? to : k] = v?.DeepClone();
            }

            return (JsonNode)copy;
        })]);
    }

    /// <summary>Removes a key from every record. Refused by the reader when a binding uses it.</summary>
    public static MockupEdit? RemoveKey(MockupEditSession session, string name, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Records(session, name) is not { } records || !Keys(records).Contains(key, StringComparer.Ordinal))
        {
            return null;
        }

        var copy = (JsonArray)records.DeepClone();
        foreach (var record in copy.OfType<JsonObject>())
        {
            record.Remove(key);
        }

        return MockupEdit.SetDataSet(name, copy);
    }

    /// <summary>
    /// One value of one record. It keeps its kind when the text is one of that kind (a number, true or false); a null or
    /// absent value becomes a number, true or false, or null when the text is one, else text. A list of records is not
    /// edited here.
    /// </summary>
    public static MockupEdit? SetDataCell(MockupEditSession session, string name, int index, string key, string? text)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Records(session, name) is not { } records || index < 0 || index >= records.Count || records[index] is not JsonObject record)
        {
            return null;
        }

        text ??= "";
        var cell = Cell(record, key);
        JsonNode? value = cell.Kind switch
        {
            MockupDataCellKind.Records => record[key],
            MockupDataCellKind.Number => MockupInspector.Number(text) ?? JsonValue.Create(text),
            MockupDataCellKind.Boolean when text.Trim() is "true" or "false" => JsonValue.Create(text.Trim() == "true"),
            MockupDataCellKind.Boolean => JsonValue.Create(text),
            MockupDataCellKind.String => JsonValue.Create(text),
            _ when text.Length == 0 && cell.Kind == MockupDataCellKind.Absent => null,
            _ when text.Trim() == "null" || text.Length == 0 => JsonValue.Create<string?>(null),
            _ when text.Trim() is "true" or "false" => JsonValue.Create(text.Trim() == "true"),
            _ => MockupInspector.Number(text) ?? JsonValue.Create(text),
        };

        if (cell.Kind == MockupDataCellKind.Records || (cell.Kind == MockupDataCellKind.Absent && value is null)
            || (record.ContainsKey(key) && MockupInspector.Same(record[key], value)))
        {
            return null;
        }

        var copy = (JsonArray)records.DeepClone();
        ((JsonObject)copy[index]!)[key] = value;
        return MockupEdit.SetDataSet(name, copy);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static MockupDataCell Cell(JsonObject record, string key)
    {
        if (!record.TryGetPropertyValue(key, out var value))
        {
            return new MockupDataCell("", MockupDataCellKind.Absent);
        }

        return value switch
        {
            null => new MockupDataCell("", MockupDataCellKind.Null),
            JsonArray list => new MockupDataCell($"{list.Count.ToString(CultureInfo.InvariantCulture)} nested record{(list.Count == 1 ? "" : "s")}", MockupDataCellKind.Records),
            JsonValue v when v.GetValueKind() == JsonValueKind.String => new MockupDataCell(v.GetValue<string>(), MockupDataCellKind.String),
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => new MockupDataCell(v.ToJsonString(), MockupDataCellKind.Number),
            JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => new MockupDataCell(v.ToJsonString(), MockupDataCellKind.Boolean),
            JsonValue v when v.GetValueKind() == JsonValueKind.Null => new MockupDataCell("", MockupDataCellKind.Null),
            _ => new MockupDataCell(value.ToJsonString(), MockupDataCellKind.Records),
        };
    }

    private static JsonArray? Records(MockupEditSession session, string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(name);
        return (session.Tree["data"] as JsonObject)?[name] as JsonArray;
    }

    private static List<string> Keys(JsonArray records) =>
        records.OfType<JsonObject>().SelectMany(r => r.Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToList();

    private static List<string> StatesOf(JsonObject node)
    {
        var states = node["states"] as JsonObject ?? [];
        var known = MockupVocabulary.Names<MockupState>();
        return known.Where(states.ContainsKey).Concat(states.Select(p => p.Key).Where(k => !known.Contains(k))).ToList();
    }

    private static IReadOnlyList<string> ViewportNames(JsonObject tree) =>
        (tree["viewports"] as JsonArray ?? []).Select(v => MockupInspector.Str(v as JsonObject, "name")).OfType<string>().ToList();

    private static MockupInspectorAction ActionOf(JsonObject? action) => action is null
        ? MockupInspectorAction.None
        : new MockupInspectorAction(
            MockupInspector.Str(action, "go"),
            MockupInspector.Str(action, "open"),
            MockupInspector.Str(action, "close"),
            [.. (action["set"] as JsonObject ?? []).Select(p => new MockupSetTarget(p.Key, p.Value is null ? "" : MockupInspector.Display(p.Value)))]);

    private static JsonObject? Json(MockupInspectorAction action)
    {
        if (action.IsEmpty)
        {
            return null;
        }

        var json = new JsonObject();
        if (action.Go is { } go)
        {
            json["go"] = go;
        }

        if (action.Open is { } open)
        {
            json["open"] = open;
        }

        if (action.Close is { } close)
        {
            json["close"] = close;
        }

        if (action.Set.Count > 0)
        {
            var set = new JsonObject();
            foreach (var target in action.Set)
            {
                set[target.Id] = target.State;
            }

            json["set"] = set;
        }

        return json;
    }

    /// <summary>Every node of the screens with its path, in document order.</summary>
    private static IEnumerable<(int[] Path, JsonObject Node)> Walk(JsonObject tree)
    {
        var pending = new Stack<(int[] Path, JsonObject Node)>();
        var screens = (tree["screens"] as JsonArray ?? []).ToList();
        for (var i = screens.Count - 1; i >= 0; i--)
        {
            if (screens[i] is JsonObject screen)
            {
                pending.Push(([i], screen));
            }
        }

        while (pending.TryPop(out var entry))
        {
            yield return entry;
            var children = (entry.Node["children"] as JsonArray ?? []).ToList();
            for (var i = children.Count - 1; i >= 0; i--)
            {
                if (children[i] is JsonObject child)
                {
                    pending.Push(([.. entry.Path, i], child));
                }
            }
        }
    }
}
