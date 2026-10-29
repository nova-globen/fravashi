using System.Globalization;
using System.Text;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Releases;

/// <summary>The three groups a release note has (work item #368).</summary>
public enum ReleaseNoteGroup
{
    /// <summary>Something new a person can use.</summary>
    Feature,

    /// <summary>Something that was wrong and is now right.</summary>
    Fix,

    /// <summary>Everything else: chores, steps, research.</summary>
    Other,
}

/// <summary>
/// The release notes, drafted DETERMINISTICALLY from the items a release carries (work item #368): grouped into
/// features, fixes and other, one line per item from its title with its number, the items in each group by number. The
/// same items always give the same draft, which a person then edits before saving. The rules, first match wins:
/// <list type="number">
/// <item>a Bug is a fix;</item>
/// <item>a title that starts with the word "fix" (any case, as "fix", "fix:", "fix(...)", "fixes", "fixed") is a fix;</item>
/// <item>a title that starts with the word "feat" or "feature" is a feature;</item>
/// <item>a Spike is other (it answers a question rather than shipping one);</item>
/// <item>a Feature or an Increment is a feature (a capability, or a shippable slice of one);</item>
/// <item>anything else (a Task, a Tier) is other.</item>
/// </list>
/// </summary>
public static class ReleaseNotes
{
    /// <summary>The heading of each group, in the order the draft shows them.</summary>
    public static IReadOnlyList<(ReleaseNoteGroup Group, string Heading)> Groups { get; } =
    [
        (ReleaseNoteGroup.Feature, "Features"),
        (ReleaseNoteGroup.Fix, "Fixes"),
        (ReleaseNoteGroup.Other, "Other changes"),
    ];

    /// <summary>Which group an item belongs in, by the rules above.</summary>
    public static ReleaseNoteGroup Classify(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == WorkItemKind.Bug || StartsWithWord(item.Title, "fix", "fixes", "fixed"))
        {
            return ReleaseNoteGroup.Fix;
        }

        if (StartsWithWord(item.Title, "feat", "feature"))
        {
            return ReleaseNoteGroup.Feature;
        }

        if (item.Kind == WorkItemKind.Spike)
        {
            return ReleaseNoteGroup.Other;
        }

        return item.Level is WorkItemLevel.Feature or WorkItemLevel.Increment
            ? ReleaseNoteGroup.Feature
            : ReleaseNoteGroup.Other;
    }

    /// <summary>
    /// The draft for <paramref name="version"/> carrying <paramref name="items"/>: a heading, then each non-empty group
    /// with one line per item, <c>- Title (#N)</c>. A conventional-commit prefix on a title ("fix:", "feat(web):") is
    /// left out of its line, since the group already says it.
    /// </summary>
    public static string Draft(string version, IEnumerable<WorkItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.OrderBy(i => i.Number).ToList();
        var text = new StringBuilder();
        text.Append("# Release ").Append(version).Append('\n');
        if (list.Count == 0)
        {
            text.Append('\n').Append("No work items.").Append('\n');
            return text.ToString();
        }

        foreach (var (group, heading) in Groups)
        {
            var members = list.Where(i => Classify(i) == group).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            text.Append('\n').Append("## ").Append(heading).Append("\n\n");
            foreach (var item in members)
            {
                text.Append("- ").Append(LineTitle(item.Title))
                    .Append(" (#").Append(item.Number.ToString(CultureInfo.InvariantCulture)).Append(")\n");
            }
        }

        return text.ToString();
    }

    /// <summary>A title as a note line: one line, without a leading conventional-commit prefix.</summary>
    public static string LineTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var line = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon < 24 && colon + 1 < line.Length)
        {
            var prefix = line[..colon];
            var word = prefix.Split('(')[0];
            if (word is "fix" or "feat" or "chore" or "docs" or "refactor" or "perf" or "test" or "build" or "ci" or "style" or "revert"
                && (prefix.Length == word.Length || prefix.EndsWith(')')))
            {
                var rest = line[(colon + 1)..].Trim();
                if (rest.Length > 0)
                {
                    return char.ToUpperInvariant(rest[0]) + rest[1..];
                }
            }
        }

        return line;
    }

    private static bool StartsWithWord(string title, params string[] words)
    {
        var trimmed = title.TrimStart();
        foreach (var word in words)
        {
            if (trimmed.Length >= word.Length
                && trimmed.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == word.Length || !char.IsLetterOrDigit(trimmed[word.Length])))
            {
                return true;
            }
        }

        return false;
    }
}
