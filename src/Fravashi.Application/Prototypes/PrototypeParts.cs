using System.Globalization;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;

namespace Fravashi.Application.Prototypes;

/// <summary>Why an anchor names a part its node does not have: the anchor field at fault and the reason.</summary>
/// <param name="Field">The anchor's field the reason is about: <c>part</c>, or <c>column</c> for a column that is not there.</param>
/// <param name="Message">The reason, naming the parts (or columns) the node does have.</param>
public sealed record PrototypePartRefusal(string Field, string Message);

/// <summary>
/// Which parts a node of a design has (backlog 238, plan @52): the elements <see cref="PrototypeRenderer"/> draws for it
/// without an anchor of their own, exactly as it draws them. A SCREEN (a top-level node, drawn as a section with a heading,
/// whatever its type) has a title and nothing else; a panel of tabs (a direct child of a tabs node, drawn as a tab panel
/// whatever its type) has the tab it is chosen by and nothing else; otherwise a dialog has a title, a table a caption and
/// a header for each of its columns, a list and a tree a caption, and every other node none.
/// <para>
/// This is the design's fact, so it is asked against a tree, wherever an operation is NEW (<c>prototype_adjust</c>,
/// <c>prototype_check</c> on inline JSON) through <see cref="PrototypeReader.CheckParts"/>. A STORED operation whose node
/// has lost the part since is not refused, it goes unplaced, because a regenerate must never make a person's overlay
/// unreadable.
/// </para>
/// </summary>
public static class PrototypeParts
{
    /// <summary>The parts of a node, in the order the vocabulary lists them.</summary>
    /// <param name="node">The node.</param>
    /// <param name="screen">True when it is one of the document's screens.</param>
    /// <param name="tabPanel">True when it is a direct child of a tabs node that is not a screen.</param>
    public static IReadOnlyList<DesignPart> Of(MockupNode node, bool screen, bool tabPanel)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (screen)
        {
            return [DesignPart.Title];
        }

        if (tabPanel)
        {
            return [DesignPart.Tab];
        }

        return node.Type switch
        {
            MockupComponent.Dialog => [DesignPart.Title],
            MockupComponent.Table => [DesignPart.Caption, DesignPart.ColumnHeader],
            MockupComponent.List or MockupComponent.Tree => [DesignPart.Caption],
            _ => [],
        };
    }

    /// <summary>
    /// Why <paramref name="anchor"/>'s part is not one its node has, or null when it is (or names no part, or no node at
    /// all: an anchor that finds no node is the renderer's "unplaced", a different question).
    /// </summary>
    public static PrototypePartRefusal? Refusal(MockupDocument tree, PrototypeAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(anchor);

        if (anchor.Part == DesignPart.Node || Find(tree, anchor) is not { } place)
        {
            return null;
        }

        var parts = Of(place.Node, place.Screen, place.TabPanel);
        var what = place.Screen ? "screen" : MockupVocabulary.Name(place.Node.Type);
        var named = anchor.Id is { } id ? $"\"{id}\"" : $"at {anchor.Path}";
        if (!parts.Contains(anchor.Part))
        {
            var has = parts.Count == 0
                ? $"it has no parts, so leave \"part\" out to address the {what} itself"
                : $"its {(parts.Count == 1 ? "part is" : "parts are")} {Options(parts.Select(PrototypeVocabulary.Name))}";
            return new PrototypePartRefusal(
                "part", $"The {what} {named} has no \"{PrototypeVocabulary.Name(anchor.Part)}\" part; {has}.");
        }

        if (anchor.Part == DesignPart.ColumnHeader)
        {
            var columns = place.Node.Columns ?? [];
            if (!columns.Any(c => string.Equals(c.Key, anchor.Column, StringComparison.Ordinal)))
            {
                var keys = columns.Count == 0
                    ? "it has no columns"
                    : $"its columns are {Options(columns.Select(c => c.Key))}";
                return new PrototypePartRefusal(
                    "column", $"The table {named} has no column \"{anchor.Column}\"; {keys}.");
            }
        }

        return null;
    }

    private sealed record Place(MockupNode Node, bool Screen, bool TabPanel);

    /// <summary>The node an anchor names, by id or by tree path as the renderer spells it, the first in tree order.</summary>
    private static Place? Find(MockupDocument tree, PrototypeAnchor anchor)
    {
        for (var s = 0; s < tree.Screens.Count; s++)
        {
            if (Find(tree.Screens[s], $"/screens/{s.ToString(CultureInfo.InvariantCulture)}", true, false, anchor) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static Place? Find(MockupNode node, string path, bool screen, bool tabPanel, PrototypeAnchor anchor)
    {
        if (anchor.Id is { } id ? string.Equals(node.Id, id, StringComparison.Ordinal) : string.Equals(path, anchor.Path, StringComparison.Ordinal))
        {
            return new Place(node, screen, tabPanel);
        }

        var children = node.Children ?? [];
        var panels = !screen && node.Type == MockupComponent.Tabs;
        for (var i = 0; i < children.Count; i++)
        {
            if (Find(children[i], $"{path}/children/{i.ToString(CultureInfo.InvariantCulture)}", false, panels, anchor) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string Options(IEnumerable<string> names)
    {
        var list = names.Select(n => $"\"{n}\"").ToList();
        return list.Count == 1 ? list[0] : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }
}
