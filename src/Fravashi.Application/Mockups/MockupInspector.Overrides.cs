using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fravashi.Application.Mockups;

/// <summary>
/// One override of a node the inspector edits (#199): a <c>states</c> entry (how it looks in that state) or an <c>at</c>
/// entry (how it looks at that viewport).
/// </summary>
public sealed record MockupOverrideScope
{
    private MockupOverrideScope(MockupEntryMap map, string key)
    {
        Map = map;
        Key = key;
    }

    /// <summary><see cref="MockupEntryMap.States"/> or <see cref="MockupEntryMap.At"/>.</summary>
    public MockupEntryMap Map { get; }

    /// <summary>The state's or the viewport's name.</summary>
    public string Key { get; }

    /// <summary>How a node looks in a state.</summary>
    public static MockupOverrideScope InState(MockupState state) => new(MockupEntryMap.States, MockupVocabulary.Name(state));

    /// <summary>How a node looks at a declared viewport.</summary>
    public static MockupOverrideScope AtViewport(string viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        return new(MockupEntryMap.At, viewport);
    }

    /// <summary>The state, when this is a state's override.</summary>
    public MockupState? State => Map == MockupEntryMap.States && MockupVocabulary.TryParse<MockupState>(Key, out var s) ? s : null;

    /// <summary>The viewport, when this is a viewport's override.</summary>
    public string? Viewport => Map == MockupEntryMap.At ? Key : null;
}

/// <summary>
/// The overrides half of the inspector (#199): a state's or a viewport's override is edited with exactly the controls
/// and changes of slice 6, with no second implementation. The node is shown AS IT LOOKS there (the override laid over
/// it), each change is worked out by the same method against a scratch copy of that view, and what came out is written
/// back as ONE <see cref="MockupEdit.SetEntry"/> of the override.
/// <para>
/// An override can only add or replace (@14), so it holds what differs from the node at rest: a property set to what
/// the node already says, or cleared, goes back to "as at rest". The one exception is a property true at rest and
/// cleared in the override, which is written <c>false</c>, since that is the only way an override can say "not
/// required".
/// </para>
/// </summary>
public static partial class MockupInspector
{
    /// <summary>What the view of a node in an override leaves out: what it is, and its own behaviour and children.</summary>
    private static readonly HashSet<string> NotViewed = new(StringComparer.Ordinal) { "id", "state", "states", "at", "on", "children" };

    /// <summary>
    /// What the inspector shows for a node in an override: the node as it looks there, offering what the node offers
    /// except its id; <see cref="MockupInspectorNode.Overridden"/> says which of the values the override itself carries.
    /// Null when there is no node there.
    /// </summary>
    public static MockupInspectorNode? Read(MockupEditSession session, IReadOnlyList<int> path, MockupOverrideScope scope)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(scope);

        if (!MockupOutline.Exists(session, path))
        {
            return null;
        }

        var (rest, over) = Parts(session, path, scope);
        var scratch = Scratch(session, View(rest, over));
        var read = Read(scratch, [0])!;
        return read with
        {
            Path = [.. path],
            Offered = [.. read.Offered.Where(p => p != "id")],
            Overridden = (over ?? []).Select(p => p.Key).ToHashSet(StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// One slice 6 change made in an override, as one edit of that override: <paramref name="change"/> is any of this
    /// class's changes (or <see cref="MockupBehaviour"/>'s accessibility ones), run against the node as it looks there.
    /// Null when nothing would change.
    /// </summary>
    public static MockupEdit? InOverride(
        MockupEditSession session,
        IReadOnlyList<int> path,
        MockupOverrideScope scope,
        Func<MockupEditSession, IReadOnlyList<int>, MockupEdit?> change)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(change);

        var (rest, over) = Parts(session, path, scope);
        var scratch = Scratch(session, View(rest, over));
        if (change(scratch, [0]) is not { } edit)
        {
            return null;
        }

        var before = (JsonObject)MockupTree.NodeAt(scratch.Tree, [0]).DeepClone();
        var tree = (JsonObject)scratch.Tree.DeepClone();
        try
        {
            edit.Apply(tree);
        }
        catch (MockupEditRefusedException)
        {
            return null;
        }

        var after = MockupTree.NodeAt(tree, [0]);
        var written = (JsonObject?)over?.DeepClone() ?? [];
        var order = MockupTree.ByOrder(MockupVocabulary.NodeProperties);
        foreach (var name in before.Select(p => p.Key).Union(after.Select(p => p.Key)).Where(n => n != "type").ToList())
        {
            if (JsonNode.DeepEquals(before[name], after[name]) && before.ContainsKey(name) == after.ContainsKey(name))
            {
                continue;
            }

            JsonNode? value = after.ContainsKey(name)
                ? (JsonNode.DeepEquals(after[name], rest[name]) ? null : after[name]?.DeepClone())
                : rest[name] is JsonValue held && held.GetValueKind() == JsonValueKind.True ? JsonValue.Create(false) : null;
            MockupTree.Put(written, name, value, order);
        }

        return JsonNode.DeepEquals(written, over ?? []) && over is not null
            ? null
            : MockupEdit.SetEntry(Target(path), scope.Map, scope.Key, written);
    }

    /// <summary>The node at rest (without what a view leaves out) and the override, when it has one.</summary>
    private static (JsonObject AtRest, JsonObject? Override) Parts(MockupEditSession session, IReadOnlyList<int> path, MockupOverrideScope scope)
    {
        var node = Node(session, path);
        var rest = new JsonObject();
        foreach (var (name, value) in node)
        {
            if (!NotViewed.Contains(name))
            {
                rest[name] = value?.DeepClone();
            }
        }

        var over = (node[MockupVocabulary.Name(scope.Map)] as JsonObject)?[scope.Key] as JsonObject;
        return (rest, over);
    }

    /// <summary>The node as it looks in the override: each property the override carries put over the node's own.</summary>
    private static JsonObject View(JsonObject rest, JsonObject? over)
    {
        var view = (JsonObject)rest.DeepClone();
        var order = MockupTree.ByOrder(MockupVocabulary.NodeProperties);
        foreach (var (name, value) in over ?? [])
        {
            if (!NotViewed.Contains(name) && name != "type")
            {
                MockupTree.Put(view, name, value?.DeepClone(), order);
            }
        }

        return view;
    }

    /// <summary>
    /// A one-node tree holding the view, with the document's viewports and data sets so a change that reads them (a
    /// data set's keys) reads the real ones. It is never judged or kept: only what the change does to it is read.
    /// </summary>
    private static MockupEditSession Scratch(MockupEditSession session, JsonObject view)
    {
        var tree = new JsonObject { ["mockup"] = MockupVocabulary.Version };
        foreach (var name in new[] { "viewports", "data" })
        {
            if (session.Tree[name] is { } value)
            {
                tree[name] = value.DeepClone();
            }
        }

        tree["screens"] = new JsonArray(view);
        return MockupEditSession.TryOpen(tree.ToJsonString(), out var scratch, out var refusal)
            ? scratch
            : throw new InvalidOperationException($"The view of an override could not be opened: {string.Join(" ", refusal)}");
    }
}
