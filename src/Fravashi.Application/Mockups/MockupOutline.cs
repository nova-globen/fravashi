using System.Globalization;
using System.Text.Json.Nodes;

namespace Fravashi.Application.Mockups;

/// <summary>One row of a mockup's outline: a node of the tree under edit, where it is and what it says.</summary>
/// <param name="Path">The indices from the screen list down (the screen, then each child).</param>
/// <param name="JsonPath">The same place spelled as the reader spells it, which is also the preview's address for it.</param>
/// <param name="Type">The node's type as written (an invalid tree may carry one the palette does not have).</param>
/// <param name="Text">Its <c>text</c>, when it has one.</param>
/// <param name="Id">Its <c>id</c>, when it has one.</param>
/// <param name="Label">Its <c>a11y.label</c>, when it has one: what names an icon button or an image.</param>
/// <param name="Siblings">How many nodes its parent's list holds, itself included.</param>
public sealed record MockupOutlineRow(
    IReadOnlyList<int> Path, string JsonPath, string Type, string? Text, string? Id, string? Label, int Siblings)
{
    /// <summary>How deep it is: a screen is 1.</summary>
    public int Depth => Path.Count;

    /// <summary>Its place in its parent's list.</summary>
    public int Index => Path[^1];
}

/// <summary>What a button or key in the outline asks for.</summary>
public enum MockupOutlineAction
{
    /// <summary>One place earlier among its siblings.</summary>
    MoveUp,

    /// <summary>One place later among its siblings.</summary>
    MoveDown,

    /// <summary>Into the sibling before it, as that sibling's last child.</summary>
    Indent,

    /// <summary>Out of its parent, to the place just after it.</summary>
    Outdent,

    /// <summary>Removed, with everything under it.</summary>
    Remove,
}

/// <summary>The one edit an outline action is, and where the node is once it has been applied (null when removed).</summary>
public sealed record MockupOutlineStep(MockupEdit Edit, IReadOnlyList<int>? Lands);

/// <summary>
/// The outline of a mockup open for editing (#196, ADR 0021 decision 5: a sortable outline in stage one, not a
/// canvas). It is read from the session's tree rather than from a <see cref="MockupDocument"/>, so a tree the reader
/// refuses still has an outline to repair it in. Every action is ONE <see cref="MockupEdit"/> (#195), so it is
/// judged, undone and redone exactly as any other edit is.
/// </summary>
public static class MockupOutline
{
    /// <summary>Every node of the screens in document order (a parent before its children), as rows.</summary>
    public static IReadOnlyList<MockupOutlineRow> Rows(MockupEditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var rows = new List<MockupOutlineRow>();
        void Walk(JsonArray list, int[] parent)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is not JsonObject node)
                {
                    continue;
                }

                int[] path = [.. parent, i];
                rows.Add(new MockupOutlineRow(
                    path, MockupTree.JsonPath(path), Text(node, "type") ?? "node", Text(node, "text"), Text(node, "id"),
                    Text(node["a11y"] as JsonObject, "label"), list.Count));
                if (node["children"] is JsonArray children)
                {
                    Walk(children, path);
                }
            }
        }

        if (session.Tree["screens"] is JsonArray screens)
        {
            Walk(screens, []);
        }

        return rows;
    }

    /// <summary>
    /// The edit an action is for the node at <paramref name="path"/>, or null when the action has no meaning there
    /// (the first node cannot move up, a screen cannot be outdented). Whether the result is a tree the reader accepts
    /// is the session's to judge when the edit is applied.
    /// </summary>
    public static MockupOutlineStep? Plan(MockupEditSession session, IReadOnlyList<int> path, MockupOutlineAction action)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(path);

        if (path.Count == 0 || !Exists(session, path))
        {
            return null;
        }

        int[] parent = [.. path.Take(path.Count - 1)];
        var index = path[^1];
        var siblings = Siblings(session, parent);
        var node = MockupTarget.At([.. path]);
        var parentTarget = parent.Length == 0 ? MockupTarget.Screens : MockupTarget.At(parent);

        switch (action)
        {
            case MockupOutlineAction.MoveUp when index > 0:
                return new MockupOutlineStep(MockupEdit.Move(node, parentTarget, index - 1), [.. parent, index - 1]);

            case MockupOutlineAction.MoveDown when index < siblings - 1:
                // The index counts the list once the node has left it, so one place later is index + 1.
                return new MockupOutlineStep(MockupEdit.Move(node, parentTarget, index + 1), [.. parent, index + 1]);

            case MockupOutlineAction.Indent when index > 0:
                int[] into = [.. parent, index - 1];
                var last = Children(session, into);
                return new MockupOutlineStep(MockupEdit.Move(node, MockupTarget.At(into), last), [.. into, last]);

            case MockupOutlineAction.Outdent when path.Count >= 2:
                int[] grandparent = [.. path.Take(path.Count - 2)];
                var after = path[^2] + 1;
                var outTo = grandparent.Length == 0 ? MockupTarget.Screens : MockupTarget.At(grandparent);
                return new MockupOutlineStep(MockupEdit.Move(node, outTo, after), [.. grandparent, after]);

            case MockupOutlineAction.Remove:
                return new MockupOutlineStep(MockupEdit.Remove(node), null);

            default:
                return null;
        }
    }

    /// <summary>Whether a node is at this path in the tree as it is now.</summary>
    public static bool Exists(MockupEditSession session, IReadOnlyList<int> path)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(path);

        JsonObject node = session.Tree;
        for (var i = 0; i < path.Count; i++)
        {
            if (node[i == 0 ? "screens" : "children"] is not JsonArray list
                || path[i] < 0 || path[i] >= list.Count
                || list[path[i]] is not JsonObject next)
            {
                return false;
            }

            node = next;
        }

        return path.Count > 0;
    }

    /// <summary>The JSON path of a node path, spelled as the reader and the preview spell it.</summary>
    public static string JsonPath(IReadOnlyList<int> path) => MockupTree.JsonPath(path);

    /// <summary>
    /// The node a JSON path is in: the longest <c>$.screens[i].children[j]...</c> prefix of it, so an error about a
    /// node's property, override or item belongs to that node. Null when the path is not inside any screen.
    /// </summary>
    public static IReadOnlyList<int>? NodeOf(string jsonPath)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);

        const string Root = "$.screens[";
        if (!jsonPath.StartsWith(Root, StringComparison.Ordinal))
        {
            return null;
        }

        var path = new List<int>();
        var at = Root.Length;
        while (true)
        {
            var close = jsonPath.IndexOf(']', at);
            if (close < 0 || !int.TryParse(jsonPath.AsSpan(at, close - at), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                break;
            }

            path.Add(index);
            at = close + 1;
            const string Child = ".children[";
            if (string.CompareOrdinal(jsonPath, at, Child, 0, Child.Length) != 0)
            {
                break;
            }

            at += Child.Length;
        }

        return path.Count == 0 ? null : path;
    }

    private static int Siblings(MockupEditSession session, int[] parent) =>
        (parent.Length == 0 ? session.Tree["screens"] : NodeAt(session, parent)?["children"]) is JsonArray list ? list.Count : 0;

    private static int Children(MockupEditSession session, int[] node) =>
        NodeAt(session, node)?["children"] is JsonArray list ? list.Count : 0;

    private static JsonObject? NodeAt(MockupEditSession session, int[] path) =>
        Exists(session, path) ? MockupTree.NodeAt(session.Tree, path) : null;

    private static string? Text(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
