using System.Text.RegularExpressions;

namespace Fravashi.Application.Migration;

/// <summary>One line of a markdown checklist.</summary>
/// <param name="Text">The line's text after its box, trimmed.</param>
/// <param name="Done">True for a ticked box (<c>- [x]</c>).</param>
/// <param name="Line">The 1-based line it was on, so an item can say where it came from.</param>
public sealed record ChecklistEntry(string Text, bool Done, int Line);

/// <summary>
/// Reads a markdown checklist (item #373, <c>docs/plans/first-open-setup.md</c> section 3.5): a <c>TODO.md</c>, a
/// <c>TASKS.md</c>, or any file that is mostly <c>- [ ]</c> lines. Every box becomes one work item, an open box an open
/// item and a ticked one a Done item, in the order they are written. Nesting is not kept: a nested box is an item of its
/// own, because the board has no "sub-line" and inventing a parent would be a guess.
/// </summary>
public static partial class ChecklistMarkdown
{
    /// <summary>The file names that are checklists by name, whatever share of their lines are boxes.</summary>
    private static readonly string[] ListNames = ["todo", "tasks", "checklist"];

    /// <summary>Every box in <paramref name="markdown"/>, in order. Lines inside a fenced code block are not boxes.</summary>
    public static IReadOnlyList<ChecklistEntry> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var entries = new List<ChecklistEntry>();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            var match = Box().Match(lines[i]);
            if (match.Success && match.Groups[2].Value.Trim() is { Length: > 0 } text)
            {
                entries.Add(new ChecklistEntry(text, match.Groups[1].Value is "x" or "X", i + 1));
            }
        }

        return entries;
    }

    /// <summary>
    /// True when the file is a checklist: it has at least one box and either its name says it is a list
    /// (<c>TODO.md</c>, <c>TASKS.md</c>, <c>CHECKLIST.md</c>) or boxes are more than half of its lines that carry text
    /// (headings and blank lines aside).
    /// </summary>
    public static bool IsChecklist(string path, string markdown)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(markdown);
        var boxes = Parse(markdown).Count;
        if (boxes == 0)
        {
            return false;
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/')).ToLowerInvariant();
        if (ListNames.Contains(name, StringComparer.Ordinal))
        {
            return true;
        }

        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Count(l => l.Trim() is { Length: > 0 } t && !t.StartsWith('#'));
        return boxes * 2 > text;
    }

    /// <summary>A list item with a box: <c>- [ ] text</c>, <c>* [x] text</c>, <c>1. [ ] text</c>, at any indent.</summary>
    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+\[([ xX])\]\s+(.*)$")]
    private static partial Regex Box();
}
