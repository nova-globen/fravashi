using System.Text.RegularExpressions;

namespace Fravashi.Application.Migration;

/// <summary>One item of a markdown backlog, as written.</summary>
/// <param name="Number">The item's number from its heading.</param>
/// <param name="Title">The heading's title.</param>
/// <param name="Type">The Type field (Feature, Increment, Bug, Spike, ...), or null.</param>
/// <param name="Status">The Status field (Triage, Backlog, Ready, In Progress, In Review, Done, Cancelled), or null.</param>
/// <param name="Body">Everything under the heading, as written, metadata line included.</param>
/// <param name="Acceptance">The text of the <c>**Acceptance.**</c> paragraph, or null.</param>
/// <param name="BlockedBy">The item numbers in the Blocked by field.</param>
/// <param name="PersonBlockers">The non-numeric entries of the Blocked by field, each with its parenthetical reason.</param>
public sealed record BacklogEntry(
    int Number,
    string Title,
    string? Type,
    string? Status,
    string Body,
    string? Acceptance,
    IReadOnlyList<int> BlockedBy,
    IReadOnlyList<(string Person, string? Reason)> PersonBlockers);

/// <summary>
/// Reads a backlog written the way this repository writes <c>docs/backlog.md</c> (backlog 49, the dogfood migration):
/// each item is a <c>### N. Title</c> heading, a metadata line of bold field names and values separated by <c>|</c>,
/// and its prose, up to the next item heading, a top-level heading or a horizontal rule. Nothing is interpreted beyond
/// the fields the tracker has a place for; the body is kept word for word, so the import can be checked against the
/// file.
/// </summary>
public static partial class BacklogMarkdown
{
    public static IReadOnlyList<BacklogEntry> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var entries = new List<BacklogEntry>();
        for (var i = 0; i < lines.Length; i++)
        {
            var heading = ItemHeading().Match(lines[i]);
            if (!heading.Success)
            {
                continue;
            }

            var body = new List<string>();
            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                if (ItemHeading().IsMatch(lines[j]) || lines[j].StartsWith("# ", StringComparison.Ordinal) || lines[j].Trim() == "---")
                {
                    break;
                }

                body.Add(lines[j]);
            }

            entries.Add(Read(int.Parse(heading.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                heading.Groups[2].Value.Trim(), string.Join('\n', body).Trim('\n')));
            i = j - 1;
        }

        return entries;
    }

    private static BacklogEntry Read(int number, string title, string body)
    {
        var type = Field(body, "Type");
        var status = Field(body, "Status");
        var acceptance = Paragraph(body, "**Acceptance.**");
        var blockedBy = new List<int>();
        var people = new List<(string, string?)>();
        if (Field(body, "Blocked by") is { } field && field != "-")
        {
            foreach (var part in SplitTopLevel(field))
            {
                var entry = part.Trim();
                if (entry.Length == 0 || entry == "-")
                {
                    continue;
                }

                if (int.TryParse(entry, out var blocker))
                {
                    blockedBy.Add(blocker);
                    continue;
                }

                var open = entry.IndexOf('(', StringComparison.Ordinal);
                var person = (open < 0 ? entry : entry[..open]).Trim();
                var reason = open < 0 ? null : entry[(open + 1)..].TrimEnd(')').Trim();
                if (person.Length > 0)
                {
                    people.Add((person, reason));
                }
            }
        }

        return new BacklogEntry(number, title, type, status, body, acceptance, blockedBy, people);
    }

    /// <summary>The fields a tracker item carries as data, so an imported description must not repeat them as text.</summary>
    private static readonly string[] TrackedFields = ["Status", "Blocked by", "Blocks"];

    /// <summary>
    /// The body without the fields the tracker carries as data (backlog 175): Status on the metadata line, Blocked by and
    /// Blocks on the links line. They move in the tracker after an import and the text does not, so imported items read
    /// "Status In Review" beside a Done chip. What has no field of its own (Type, Size, Phase, ADRs, Design) stays word
    /// for word, and so does every other line; a field line left empty goes with the blank line after it.
    /// </summary>
    public static string WithoutTrackedFields(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var lines = body.Split('\n');
        var kept = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("**Type** ", StringComparison.Ordinal) && !line.StartsWith("**Blocked by** ", StringComparison.Ordinal))
            {
                kept.Add(line);
                continue;
            }

            var fields = FieldsOf(line)
                .Where(f => !TrackedFields.Any(name => f.StartsWith($"**{name}** ", StringComparison.Ordinal)))
                .ToList();
            if (fields.Count > 0)
            {
                kept.Add(string.Join(" | ", fields));
            }
            else if (i + 1 < lines.Length && lines[i + 1].Length == 0 && (kept.Count == 0 || kept[^1].Length == 0))
            {
                i++;
            }
        }

        return string.Join('\n', kept);
    }

    /// <summary>A metadata line's fields, each as <c>**Name** value</c>, split where a pipe starts the next bold field.</summary>
    private static IEnumerable<string> FieldsOf(string line)
    {
        var start = 0;
        foreach (Match separator in FieldSeparator().Matches(line))
        {
            yield return line[start..separator.Index].TrimEnd();
            start = separator.Index + separator.Length - 2; // the next field begins at its "**"
        }

        yield return line[start..].TrimEnd();
    }

    /// <summary>The value of <c>**Name** value</c> up to the next <c>|</c> or the end of its line.</summary>
    internal static string? Field(string body, string name)
    {
        var marker = $"**{name}** ";
        var at = body.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = body[(at + marker.Length)..];
        var end = rest.IndexOfAny(['\n']);
        var line = end < 0 ? rest : rest[..end];
        var bar = FieldSeparator().Match(line);
        return (bar.Success ? line[..bar.Index] : line).Trim();
    }

    /// <summary>The paragraph that starts with <paramref name="lead"/>, without the lead, up to a blank line.</summary>
    private static string? Paragraph(string body, string lead)
    {
        var at = body.IndexOf(lead, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = body[(at + lead.Length)..];
        var end = rest.IndexOf("\n\n", StringComparison.Ordinal);
        var text = (end < 0 ? rest : rest[..end]).Replace('\n', ' ').Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Splits on commas that are not inside parentheses.</summary>
    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return text[start..];
    }

    [GeneratedRegex(@"^### (\d+)\. (.+)$")]
    private static partial Regex ItemHeading();

    /// <summary>A field separator: a pipe that starts the NEXT bold field, not one inside a value.</summary>
    [GeneratedRegex(@"\s\|\s\*\*")]
    private static partial Regex FieldSeparator();
}
