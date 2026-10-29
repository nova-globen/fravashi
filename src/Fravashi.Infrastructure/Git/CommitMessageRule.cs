namespace Fravashi.Infrastructure.Git;

/// <summary>
/// The one rule both agent commit tools (<c>attempt_commit</c> and <c>git_commit</c>) hold a message to, because
/// both hand it to <see cref="SessionGitWork.CommitAsync"/> and so to the same trailer (#318). An autopilot agent
/// once passed a copy of the trailer it had seen on an earlier commit as its whole message: the commit had no
/// subject and carried the trailer twice. So a message must open with a subject line of its own, and a trailer
/// it already carries is taken out before the session's own is appended, which keeps exactly one and makes it
/// name THIS session (a copied one can name another).
/// </summary>
public static class CommitMessageRule
{
    /// <summary>The line that opens the studio's work-item block.</summary>
    public const string WorkItemsHeading = "Work items:";

    /// <summary>The start of the studio's attribution line.</summary>
    public const string ProducedByPrefix = "Produced by a Fravashi agent session";

    /// <summary>
    /// Why <paramref name="message"/> cannot be committed, in words the agent can act on, or null when it can.
    /// </summary>
    public static string? Problem(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Nothing was committed: the message is empty. Write a subject line that says in one line what "
                + "this commit does; the studio adds the work items and its own trailer itself.";
        }

        var first = message.Split('\n')[0].Trim();
        if (first.Length == 0)
        {
            return "Nothing was committed: the message has no subject line (its first line is empty). Start it "
                + "with one line that says what this commit does, then a blank line and any body.";
        }

        if (IsTrailerLine(first))
        {
            return "Nothing was committed: the first line is the studio's own trailer ('" + first + "'), not a "
                + "subject. Start the message with one line that says what this commit does, and leave the "
                + "'" + WorkItemsHeading + "' block and the '" + ProducedByPrefix + "' line out: the studio adds them.";
        }

        return null;
    }

    /// <summary>
    /// <paramref name="message"/> without any paragraph that is the studio's trailer (the work-item block or the
    /// attribution line). A message carrying none is returned unchanged, byte for byte.
    /// </summary>
    public static string WithoutStudioTrailer(string message)
    {
        var paragraphs = Paragraphs(message);
        if (!paragraphs.Any(IsTrailerParagraph))
        {
            return message;
        }

        return string.Join("\n\n", paragraphs.Where(p => !IsTrailerParagraph(p)).Select(p => string.Join('\n', p)));
    }

    private static bool IsTrailerLine(string line) =>
        string.Equals(line, WorkItemsHeading, StringComparison.Ordinal)
        || line.StartsWith(ProducedByPrefix, StringComparison.Ordinal);

    private static bool IsTrailerParagraph(IReadOnlyList<string> lines) =>
        lines.All(l => l.TrimStart().StartsWith(ProducedByPrefix, StringComparison.Ordinal))
        || (string.Equals(lines[0].Trim(), WorkItemsHeading, StringComparison.Ordinal)
            && lines.Skip(1).All(l => l.TrimStart().StartsWith("- #", StringComparison.Ordinal)));

    /// <summary>The runs of non-blank lines, each line without a trailing carriage return.</summary>
    private static List<List<string>> Paragraphs(string message)
    {
        var paragraphs = new List<List<string>>();
        List<string>? current = null;
        foreach (var raw in message.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                current = null;
                continue;
            }

            if (current is null)
            {
                current = [];
                paragraphs.Add(current);
            }

            current.Add(line);
        }

        return paragraphs;
    }
}
