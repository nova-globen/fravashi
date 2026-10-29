using System.Text.Json.Nodes;

namespace Fravashi.Application.Mockups;

/// <summary>
/// Which node an edit means: a path of indices (the screen, then each child below it), or the id a node
/// carries. <see cref="Screens"/> is the screen list, which is a parent but not a node.
/// </summary>
public sealed record MockupTarget
{
    private MockupTarget(IReadOnlyList<int>? path, string? id)
    {
        Path = path;
        Id = id;
    }

    /// <summary>The indices from the screen list down, when the target is a path.</summary>
    public IReadOnlyList<int>? Path { get; }

    /// <summary>The id, when the target is a node's id.</summary>
    public string? Id { get; }

    /// <summary>The screen list: where a screen is inserted, and a screen's parent in a move.</summary>
    public static MockupTarget Screens { get; } = new([], null);

    /// <summary>The node at a path: <c>At(0, 2)</c> is the third child of the first screen.</summary>
    public static MockupTarget At(params int[] path) => new(path.ToArray(), null);

    /// <summary>The node carrying an id.</summary>
    public static MockupTarget ById(string id) => new(null, id);

    /// <inheritdoc />
    public override string ToString() => Id ?? MockupTree.JsonPath(Path!);
}

/// <summary>Which map on a node <see cref="MockupEdit.SetEntry"/> changes one entry of.</summary>
public enum MockupEntryMap
{
    /// <summary><c>states</c>: a state name to the override shown in it.</summary>
    States,

    /// <summary><c>at</c>: a viewport name to the override shown at it.</summary>
    At,

    /// <summary><c>on</c>: an event to its action.</summary>
    On,

    /// <summary><c>a11y</c>: label, heading, live or decorative.</summary>
    A11y,
}

/// <summary>
/// One edit to a mockup tree (#195, ADR 0021: "every edit is a tree edit"). The set is closed: every editing
/// surface (the outline, the palette, the inspector, a later drag) is a way of choosing one of these, and an
/// <see cref="MockupEditSession"/> is the only thing that applies them.
/// <para>
/// Each edit is a pure function from a tree to a new tree plus the edit that undoes it. The undo is an exact
/// restore of what the edit changed (the list, map or value, at the position it held), never a re-reading of
/// text and never a second judgement, so an undo cannot be refused.
/// </para>
/// <para>
/// <b>Two kinds, one rule each.</b> A STRUCTURAL edit (insert, remove, move, rename, title, viewports, data
/// sets) is refused when the reader finds an error in the result that the tree before did not have, in the
/// reader's own words: a palette insert therefore has to be a valid node, and removing a dialog a button
/// opens is refused. A VALUE edit (a property, or one entry of a map) is refused only when the new error is at
/// or under what it wrote, because then the value itself is wrong (a property the component cannot carry, a
/// data set that does not exist, a name that is not a name). An error it causes elsewhere, a field left with
/// no label or an action naming a state just removed, is allowed: the editor has to pass through incomplete
/// states, and the error stays on <see cref="MockupEditSession.Errors"/>, shown and never hidden.
/// </para>
/// </summary>
public abstract record MockupEdit
{
    private protected MockupEdit()
    {
    }

    /// <summary>Inserts a node into a parent's children (or the screen list) at an index.</summary>
    public static MockupEdit Insert(MockupTarget parent, int index, JsonObject node) => new InsertNode(parent, index, node);

    /// <summary>Removes a node and everything under it.</summary>
    public static MockupEdit Remove(MockupTarget node) => new RemoveNode(node);

    /// <summary>
    /// Moves a node to an index in another parent's children, or its own: reorder, indent and outdent are all
    /// this. The index counts the destination list as it is once the node has been taken out of its place.
    /// </summary>
    public static MockupEdit Move(MockupTarget node, MockupTarget parent, int index) => new MoveNode(node, parent, index);

    /// <summary>Sets a plain property of a node, or clears it when the value is null.</summary>
    public static MockupEdit SetProperty(MockupTarget node, string name, JsonNode? value) => new SetNodeProperty(node, name, value);

    /// <summary>Sets one entry of a node's states, at, on or a11y, or clears it when the value is null.</summary>
    public static MockupEdit SetEntry(MockupTarget node, MockupEntryMap map, string key, JsonNode? value) =>
        new SetNodeEntry(node, map, key, value);

    /// <summary>
    /// Gives a node a new id, or none when null, and renames every go, open, close and set that named the old
    /// one, in the same edit (so one undo puts all of it back).
    /// </summary>
    public static MockupEdit RenameId(MockupTarget node, string? id) => new RenameNodeId(node, id);

    /// <summary>Sets the document's title, or clears it when null.</summary>
    public static MockupEdit SetTitle(string? title) => new SetDocumentTitle(title);

    /// <summary>Declares a viewport at an index of the viewport list.</summary>
    public static MockupEdit InsertViewport(int index, string name, int width) => new InsertDocumentViewport(index, name, width);

    /// <summary>Removes a declared viewport. Refused while a node still says how it looks there.</summary>
    public static MockupEdit RemoveViewport(string name) => new RemoveDocumentViewport(name);

    /// <summary>Adds or replaces a sample data set, or removes it when the records are null.</summary>
    public static MockupEdit SetDataSet(string name, JsonArray? records) => new SetDocumentDataSet(name, records);

    /// <summary>
    /// Renames a data set, keeping its place among the others (#207: a brand new key would otherwise land at its
    /// alphabetically sorted position, moving the set the moment it is renamed). Renaming onto an existing set's name
    /// overwrites that set's records in place instead, keeping ITS position.
    /// </summary>
    public static MockupEdit RenameDataSet(string name, string to) => new RenameDocumentDataSet(name, to);

    /// <summary>
    /// Several edits applied in order as ONE (#198: one act of a person can touch two properties, such as leaving the
    /// grid layout, which also drops <c>cols</c>), so one undo takes all of it back. It is judged as structural when
    /// any of them is, otherwise as a value edit wrong only where one of them wrote.
    /// </summary>
    public static MockupEdit Together(params MockupEdit[] edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Length == 0)
        {
            throw new ArgumentException("A group of edits holds at least one.", nameof(edits));
        }

        return edits.Length == 1 ? edits[0] : new EditGroup([.. edits]);
    }

    /// <summary>Which rule the session judges the result by.</summary>
    internal abstract MockupEditKind Kind { get; }

    /// <summary>
    /// Applies the edit to <paramref name="tree"/>, which the caller owns and has already copied, and returns
    /// the edit that undoes it and the JSON path of what it wrote. Throws <see cref="MockupEditRefusedException"/> when
    /// the edit cannot even be placed (no such node, no such position).
    /// </summary>
    internal abstract MockupApplied Apply(JsonObject tree);
}

/// <summary>Which rule a result is judged by.</summary>
internal enum MockupEditKind
{
    Structural,
    Value,

    /// <summary>An undo: it restores a tree that was already accepted, so it is not judged again.</summary>
    Restore,
}

/// <summary>What applying an edit produced: its undo, and the paths of what it wrote (one, except for a group).</summary>
internal readonly record struct MockupApplied(MockupEdit Inverse, IReadOnlyList<string> Locations)
{
    public MockupApplied(MockupEdit inverse, string location)
        : this(inverse, [location])
    {
    }
}

/// <summary>An edit that cannot be placed in the tree at all, with the sentence saying why.</summary>
internal sealed class MockupEditRefusedException(MockupError error) : Exception(error.ToString())
{
    public MockupError Error { get; } = error;
}

internal sealed record InsertNode(MockupTarget Parent, int Index, JsonObject Node) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Structural;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var parent = MockupTree.Resolve(tree, Parent, parentOnly: true);
        var owner = MockupTree.NodeAt(tree, parent);
        var inverse = MockupTree.Capture(parent, owner, MockupTree.ListName(parent));
        var list = MockupTree.List(tree, parent, create: true)!;
        if (Index < 0 || Index > list.Count)
        {
            throw MockupTree.Refuse(parent, $"There is no position {Index} here: it holds {list.Count} "
                + $"node{(list.Count == 1 ? "" : "s")}, so a new one goes at 0 to {list.Count}.");
        }

        list.Insert(Index, Node.DeepClone());
        return new MockupApplied(inverse, MockupTree.JsonPath([.. parent, Index]));
    }
}

internal sealed record RemoveNode(MockupTarget Node) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Structural;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var path = MockupTree.Resolve(tree, Node, parentOnly: false);
        var parent = path[..^1];
        var owner = MockupTree.NodeAt(tree, parent);
        var inverse = MockupTree.Capture(parent, owner, MockupTree.ListName(parent));
        MockupTree.List(tree, parent, create: false)!.RemoveAt(path[^1]);
        return new MockupApplied(inverse, MockupTree.JsonPath(path));
    }
}

internal sealed record MoveNode(MockupTarget Node, MockupTarget Parent, int Index) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Structural;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var from = MockupTree.Resolve(tree, Node, parentOnly: false);
        var to = MockupTree.Resolve(tree, Parent, parentOnly: true);
        if (to.Length >= from.Length && to.AsSpan(0, from.Length).SequenceEqual(from))
        {
            throw MockupTree.Refuse(from, "A node cannot move into itself or into anything inside it.");
        }

        // The deepest node holding both the source and the destination list. Its own path is untouched by
        // the move (it is an ancestor of the moved node), and its list holds everything the move changes, so
        // restoring that list is an exact undo.
        var common = 0;
        while (common < from.Length - 1 && common < to.Length && from[common] == to[common])
        {
            common++;
        }

        var ancestor = from[..common];
        var inverse = MockupTree.Capture(ancestor, MockupTree.NodeAt(tree, ancestor), MockupTree.ListName(ancestor));

        var source = MockupTree.List(tree, from[..^1], create: false)!;
        var moved = source[from[^1]]!;
        source.RemoveAt(from[^1]);

        // The destination's path as it is once the node has left: a later sibling of the moved node, or
        // anything under one, moved up by one.
        var depth = from.Length - 1;
        if (to.Length > depth && to.AsSpan(0, depth).SequenceEqual(from.AsSpan(0, depth)) && to[depth] > from[^1])
        {
            to[depth]--;
        }

        var destination = MockupTree.List(tree, to, create: true)!;
        if (Index < 0 || Index > destination.Count)
        {
            throw MockupTree.Refuse(to, $"There is no position {Index} here once the node has moved out of its "
                + $"place: it holds {destination.Count} node{(destination.Count == 1 ? "" : "s")}, so the node "
                + $"goes at 0 to {destination.Count}.");
        }

        destination.Insert(Index, moved);
        return new MockupApplied(inverse, MockupTree.JsonPath([.. to, Index]));
    }
}

internal sealed record SetNodeProperty(MockupTarget Node, string Name, JsonNode? Value) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Value;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var path = MockupTree.Resolve(tree, Node, parentOnly: false);
        var location = MockupReader.Member(MockupTree.JsonPath(path), Name);
        var elsewhere = Name switch
        {
            "type" => "A node's type is what it is, not a property of it: remove the node and insert another.",
            "id" => "An id is changed by renaming it, which also renames every go, open, close and set naming it.",
            "children" => "Children are inserted, removed and moved one node at a time.",
            "states" or "at" or "on" or "a11y" => $"\"{Name}\" is changed one entry at a time.",
            _ => null,
        };

        if (elsewhere is not null)
        {
            throw new MockupEditRefusedException(new MockupError(location, elsewhere));
        }

        var node = MockupTree.NodeAt(tree, path);
        var inverse = MockupTree.Capture(path, node, Name);
        MockupTree.Put(node, Name, Value?.DeepClone(), MockupTree.ByOrder(MockupVocabulary.NodeProperties));
        return new MockupApplied(inverse, location);
    }
}

internal sealed record SetNodeEntry(MockupTarget Node, MockupEntryMap Map, string Key, JsonNode? Value) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Value;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var path = MockupTree.Resolve(tree, Node, parentOnly: false);
        var mapName = MockupVocabulary.Name(Map);
        var mapPath = MockupReader.Member(MockupTree.JsonPath(path), mapName);

        // a11y's entries are judged together (decorative and labelled at once is an error on the object), so
        // what an a11y entry writes is the object; the other maps' entries stand alone.
        var location = Map == MockupEntryMap.A11y ? mapPath : MockupReader.Member(mapPath, Key);

        var node = MockupTree.NodeAt(tree, path);
        var inverse = MockupTree.Capture(path, node, mapName);
        JsonObject map;
        if (node[mapName] is JsonObject existing)
        {
            map = existing;
        }
        else if (node.ContainsKey(mapName))
        {
            throw new MockupEditRefusedException(new MockupError(mapPath, $"\"{mapName}\" here is not an object, so it has no "
                + "entries to change; clear it first."));
        }
        else
        {
            map = [];
            MockupTree.Put(node, mapName, map, MockupTree.ByOrder(MockupVocabulary.NodeProperties));
        }

        var order = Map switch
        {
            MockupEntryMap.States => MockupVocabulary.Names<MockupState>(),
            MockupEntryMap.On => MockupVocabulary.Names<MockupEvent>(),
            MockupEntryMap.A11y => MockupVocabulary.A11yProperties,
            _ => (tree["viewports"] as JsonArray ?? [])
                .Select(v => (v as JsonObject)?["name"] is JsonValue name && name.TryGetValue<string>(out var s) ? s : "")
                .ToList(),
        };

        MockupTree.Put(map, Key, Value?.DeepClone(), MockupTree.ByOrder(order));

        // An empty map says nothing, so clearing its last entry clears the map.
        if (map.Count == 0)
        {
            node.Remove(mapName);
        }

        return new MockupApplied(inverse, location);
    }
}

internal sealed record RenameNodeId(MockupTarget Node, string? Id) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Structural;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var path = MockupTree.Resolve(tree, Node, parentOnly: false);

        // References can be anywhere in the screens, so the undo restores all of them.
        var inverse = MockupTree.Capture([], tree, "screens");
        var node = MockupTree.NodeAt(tree, path);
        var old = node["id"] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

        MockupTree.Put(node, "id", Id is null ? null : JsonValue.Create(Id), MockupTree.ByOrder(MockupVocabulary.NodeProperties));

        if (old is not null && Id is not null)
        {
            foreach (var each in MockupTree.Nodes(tree))
            {
                foreach (var action in (each["on"] as JsonObject)?.Select(e => e.Value).OfType<JsonObject>() ?? [])
                {
                    foreach (var verb in new[] { "go", "open", "close" })
                    {
                        if (action[verb] is JsonValue target && target.TryGetValue<string>(out var named) && named == old)
                        {
                            action[verb] = Id;
                        }
                    }

                    if (action["set"] is JsonObject set && set.IndexOf(old) is var at and >= 0)
                    {
                        var state = set[old]?.DeepClone();
                        set.RemoveAt(at);
                        if (!set.ContainsKey(Id))
                        {
                            set.Insert(at, Id, state);
                        }
                    }
                }
            }
        }

        return new MockupApplied(inverse, MockupReader.Member(MockupTree.JsonPath(path), "id"));
    }
}

internal sealed record SetDocumentTitle(string? Title) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Structural;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var inverse = MockupTree.Capture([], tree, "title");
        MockupTree.Put(tree, "title", Title is null ? null : JsonValue.Create(Title), MockupTree.ByOrder(MockupVocabulary.RootProperties));
        return new MockupApplied(inverse, "$.title");
    }
}

internal sealed record InsertDocumentViewport(int Index, string Name, int Width) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Structural;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var inverse = MockupTree.Capture([], tree, "viewports");
        if (tree["viewports"] is not JsonArray list)
        {
            if (tree.ContainsKey("viewports"))
            {
                throw new MockupEditRefusedException(new MockupError("$.viewports", "\"viewports\" here is not a list; clear it first."));
            }

            list = [];
            MockupTree.Put(tree, "viewports", list, MockupTree.ByOrder(MockupVocabulary.RootProperties));
        }

        if (Index < 0 || Index > list.Count)
        {
            throw new MockupEditRefusedException(new MockupError("$.viewports", $"There is no position {Index} here: "
                + $"{list.Count} viewport{(list.Count == 1 ? " is" : "s are")} declared, so a new one goes at 0 to {list.Count}."));
        }

        list.Insert(Index, new JsonObject { ["name"] = Name, ["width"] = Width });
        return new MockupApplied(inverse, $"$.viewports[{Index}]");
    }
}

internal sealed record RemoveDocumentViewport(string Name) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Structural;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var inverse = MockupTree.Capture([], tree, "viewports");
        var list = tree["viewports"] as JsonArray ?? [];
        var index = -1;
        for (var i = 0; i < list.Count && index < 0; i++)
        {
            if ((list[i] as JsonObject)?["name"] is JsonValue n && n.TryGetValue<string>(out var s) && s == Name)
            {
                index = i;
            }
        }

        if (index < 0)
        {
            throw new MockupEditRefusedException(new MockupError("$.viewports", $"There is no viewport \"{Name}\"."));
        }

        list.RemoveAt(index);
        if (list.Count == 0)
        {
            tree.Remove("viewports");
        }

        return new MockupApplied(inverse, $"$.viewports[{index}]");
    }
}

internal sealed record SetDocumentDataSet(string Name, JsonArray? Records) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Structural;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var inverse = MockupTree.Capture([], tree, "data");
        if (tree["data"] is not JsonObject data)
        {
            if (tree.ContainsKey("data"))
            {
                throw new MockupEditRefusedException(new MockupError("$.data", "\"data\" here is not an object; clear it first."));
            }

            if (Records is null)
            {
                throw new MockupEditRefusedException(new MockupError("$.data", $"There is no data set \"{Name}\"."));
            }

            data = [];
            MockupTree.Put(tree, "data", data, MockupTree.ByOrder(MockupVocabulary.RootProperties));
        }

        if (Records is null && !data.ContainsKey(Name))
        {
            throw new MockupEditRefusedException(new MockupError("$.data", $"There is no data set \"{Name}\"."));
        }

        MockupTree.Put(data, Name, Records?.DeepClone(), StringComparer.Ordinal.Compare);
        if (data.Count == 0)
        {
            tree.Remove("data");
        }

        return new MockupApplied(inverse, MockupReader.Member("$.data", Name));
    }
}

internal sealed record RenameDocumentDataSet(string Name, string To) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Structural;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var inverse = MockupTree.Capture([], tree, "data");
        if (tree["data"] is not JsonObject data || data[Name] is not JsonArray records)
        {
            throw new MockupEditRefusedException(new MockupError("$.data", $"There is no data set \"{Name}\"."));
        }

        var at = data.IndexOf(Name);
        data.RemoveAt(at);
        if (data.ContainsKey(To))
        {
            data[To] = records;
        }
        else
        {
            data.Insert(Math.Min(at, data.Count), To, records);
        }

        return new MockupApplied(inverse, MockupReader.Member("$.data", To));
    }
}

internal sealed record EditGroup(IReadOnlyList<MockupEdit> Edits) : MockupEdit
{
    internal override MockupEditKind Kind =>
        Edits.Any(e => e.Kind == MockupEditKind.Structural) ? MockupEditKind.Structural
        : Edits.All(e => e.Kind == MockupEditKind.Restore) ? MockupEditKind.Restore
        : MockupEditKind.Value;

    internal override MockupApplied Apply(JsonObject tree)
    {
        // Each member's undo restores the tree as that member found it, so they are undone last first.
        var inverses = new List<MockupEdit>(Edits.Count);
        var locations = new List<string>();
        foreach (var edit in Edits)
        {
            var applied = edit.Apply(tree);
            inverses.Insert(0, applied.Inverse);
            locations.AddRange(applied.Locations);
        }

        return new MockupApplied(new EditGroup(inverses), locations);
    }
}

/// <summary>
/// The undo of every edit: puts one property of one node (or of the document, at the empty path) back to the
/// exact value it held, at the exact position it held, or takes it away when it was absent.
/// </summary>
internal sealed record RestoreProperty(int[] Owner, string Name, JsonNode? Value, int Index) : MockupEdit
{
    internal override MockupEditKind Kind => MockupEditKind.Restore;

    internal override MockupApplied Apply(JsonObject tree)
    {
        var owner = MockupTree.NodeAt(tree, Owner);
        var inverse = MockupTree.Capture(Owner, owner, Name);
        owner.Remove(Name);
        if (Index >= 0)
        {
            owner.Insert(Math.Min(Index, owner.Count), Name, Value?.DeepClone());
        }

        return new MockupApplied(inverse, Owner.Length == 0 ? "$" : MockupTree.JsonPath(Owner));
    }
}

/// <summary>Walking and addressing the JSON form of a tree, which is what an edit session holds.</summary>
internal static class MockupTree
{
    /// <summary>The JSON path of a node path, spelled as the reader spells it.</summary>
    public static string JsonPath(IReadOnlyList<int> path)
    {
        var text = "$.screens";
        for (var i = 0; i < path.Count; i++)
        {
            text += i == 0 ? $"[{path[i]}]" : $".children[{path[i]}]";
        }

        return text;
    }

    /// <summary>The list a parent's children are in: the screens at the root, its children anywhere else.</summary>
    public static string ListName(int[] parent) => parent.Length == 0 ? "screens" : "children";

    public static MockupEditRefusedException Refuse(int[] path, string message) =>
        new(new MockupError(path.Length == 0 ? "$.screens" : JsonPath(path), message));

    /// <summary>
    /// Turns a target into a path, refusing one that names nothing. <paramref name="parentOnly"/> says the
    /// screen list is acceptable (it is a parent); otherwise the target must be a node.
    /// </summary>
    public static int[] Resolve(JsonObject tree, MockupTarget target, bool parentOnly)
    {
        int[] path;
        if (target.Id is { } id)
        {
            path = Find(tree, id)
                ?? throw new MockupEditRefusedException(new MockupError("$", $"There is no node with the id \"{id}\"."));
        }
        else
        {
            path = [.. target.Path!];
            if (path.Length == 0 && !parentOnly)
            {
                throw new MockupEditRefusedException(new MockupError("$.screens", "The screen list is not a node; name a "
                    + "screen or a node inside one."));
            }

            if (TryNodeAt(tree, path) is null)
            {
                throw new MockupEditRefusedException(new MockupError(JsonPath(path), "There is no node here."));
            }
        }

        return path;
    }

    /// <summary>The node at a path, or the document at the empty path. The path is known to exist.</summary>
    public static JsonObject NodeAt(JsonObject tree, IReadOnlyList<int> path) =>
        TryNodeAt(tree, path) ?? throw new InvalidOperationException($"There is no node at {JsonPath(path)}.");

    private static JsonObject? TryNodeAt(JsonObject tree, IReadOnlyList<int> path)
    {
        var node = tree;
        for (var i = 0; i < path.Count; i++)
        {
            if (node[i == 0 ? "screens" : "children"] is not JsonArray list
                || path[i] < 0 || path[i] >= list.Count
                || list[path[i]] is not JsonObject next)
            {
                return null;
            }

            node = next;
        }

        return node;
    }

    /// <summary>The list holding a parent's children, made when <paramref name="create"/> and it has none.</summary>
    public static JsonArray? List(JsonObject tree, int[] parent, bool create)
    {
        var owner = NodeAt(tree, parent);
        var name = ListName(parent);
        if (owner[name] is JsonArray list)
        {
            return list;
        }

        if (owner.ContainsKey(name))
        {
            throw Refuse(parent, $"\"{name}\" here is not a list, so nothing can be placed in it.");
        }

        if (!create)
        {
            return null;
        }

        list = [];
        Put(owner, name, list, ByOrder(parent.Length == 0 ? MockupVocabulary.RootProperties : MockupVocabulary.NodeProperties));
        return list;
    }

    /// <summary>Every node of the screens (not overrides), in document order.</summary>
    public static IEnumerable<JsonObject> Nodes(JsonObject tree)
    {
        var pending = new Stack<JsonObject>();
        foreach (var screen in (tree["screens"] as JsonArray ?? []).OfType<JsonObject>().Reverse())
        {
            pending.Push(screen);
        }

        while (pending.TryPop(out var node))
        {
            yield return node;
            foreach (var child in (node["children"] as JsonArray ?? []).OfType<JsonObject>().Reverse())
            {
                pending.Push(child);
            }
        }
    }

    private static int[]? Find(JsonObject tree, string id)
    {
        var pending = new Stack<(JsonObject Node, int[] Path)>();
        var screens = (tree["screens"] as JsonArray ?? []).ToList();
        for (var i = screens.Count - 1; i >= 0; i--)
        {
            if (screens[i] is JsonObject screen)
            {
                pending.Push((screen, [i]));
            }
        }

        while (pending.TryPop(out var entry))
        {
            if (entry.Node["id"] is JsonValue value && value.TryGetValue<string>(out var s) && s == id)
            {
                return entry.Path;
            }

            var children = (entry.Node["children"] as JsonArray ?? []).ToList();
            for (var i = children.Count - 1; i >= 0; i--)
            {
                if (children[i] is JsonObject child)
                {
                    pending.Push((child, [.. entry.Path, i]));
                }
            }
        }

        return null;
    }

    /// <summary>The restore that puts <paramref name="name"/> on <paramref name="owner"/> back as it is now.</summary>
    public static RestoreProperty Capture(int[] path, JsonObject owner, string name)
    {
        var index = owner.IndexOf(name);
        return new RestoreProperty(path, name, index >= 0 ? owner[name]?.DeepClone() : null, index);
    }

    /// <summary>
    /// Sets a property in place when it is there, inserts it where the canonical order puts it when it is not,
    /// and removes it when the value is null.
    /// </summary>
    public static void Put(JsonObject owner, string name, JsonNode? value, Func<string, string, int> compare)
    {
        if (value is null)
        {
            owner.Remove(name);
            return;
        }

        var index = owner.IndexOf(name);
        if (index >= 0)
        {
            owner.SetAt(index, value);
            return;
        }

        var at = owner.Count;
        for (var i = 0; i < owner.Count; i++)
        {
            if (compare(owner.GetAt(i).Key, name) > 0)
            {
                at = i;
                break;
            }
        }

        owner.Insert(at, name, value);
    }

    /// <summary>Compares two names by their place in a fixed order; a name not in it comes after all that are.</summary>
    public static Func<string, string, int> ByOrder(IReadOnlyList<string> order) =>
        (a, b) => Rank(order, a).CompareTo(Rank(order, b));

    private static int Rank(IReadOnlyList<string> order, string name)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == name)
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
