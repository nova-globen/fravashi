using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Sessions;

/// <summary>
/// The short title a conversation shows wherever it is listed (work item #351): the Sessions list, Home's lists and the
/// session header. Made locally and never by a model, from what the conversation is about:
/// <list type="number">
/// <item>a person's own rename, always, as they typed it;</item>
/// <item>the work item the conversation is linked to ("#343 Sidebar sections squeeze..."), when it is linked;</item>
/// <item>a commit review's own title ("Review of #335, round 2: pass"), told apart from the conversations it reviews;</item>
/// <item>else a short summary of the first prompt: greetings trimmed, the first meaningful clause, capped at a word.</item>
/// </list>
/// <para>
/// The list cut the old title at 60 characters mid-word ("mark the few tools nearly ev..."), and a list of reviews read
/// as many copies of one line. Every rule here exists to fix one of those two.
/// </para>
/// </summary>
public static partial class ConversationTitles
{
    /// <summary>
    /// The longest title this makes, ellipsis included. Sized to the Sessions list's width at its font, which fits about
    /// sixty characters; a narrower list still clips with an ellipsis of its own.
    /// </summary>
    public const int MaxLength = 56;

    /// <summary>What a conversation with nothing to name it by is called.</summary>
    public const string Untitled = "Untitled conversation";

    /// <summary>The prefix a commit review's conversation carried before work item #351. Read, never written.</summary>
    public const string LegacyReviewPrefix = "Commit review: ";

    /// <summary>The prefix every review title starts with now.</summary>
    public const string ReviewPrefix = "Review";

    private const string Ellipsis = "...";

    /// <summary>The shortest clause worth being a title on its own; shorter ones take the next sentence with them.</summary>
    private const int ShortestClause = 12;

    /// <summary>
    /// What a conversation shows. <paramref name="custom"/> (a person's rename) wins; then the linked work item; then
    /// the stored derived title, re-summarised so a title stored before #351 reads the same way a new one does.
    /// </summary>
    public static string Display(string? custom, string? derived, int? linkedNumber = null, string? linkedTitle = null)
    {
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom.Trim();
        }

        var stored = (derived ?? string.Empty).Trim();
        if (IsReview(stored))
        {
            return FromStoredReview(stored);
        }

        if (linkedNumber is { } number && number > 0)
        {
            return ForWorkItem(number, linkedTitle);
        }

        if (stored.Length == 0)
        {
            return Untitled;
        }

        if (string.Equals(stored, Untitled, StringComparison.Ordinal))
        {
            return stored;
        }

        var summary = FromPrompt(stored);
        return summary.Length == 0 ? Cap(stored, truncated: false) : summary;
    }

    /// <summary>"#343 Sidebar sections squeeze...", capped at a word.</summary>
    public static string ForWorkItem(int number, string? title)
    {
        var head = "#" + number.ToString(CultureInfo.InvariantCulture);
        var text = Flatten(title);
        return text.Length == 0 ? head : Cap(head + " " + text, truncated: false);
    }

    /// <summary>
    /// A commit review's title (work item #351): "Review of #335, round 2: pass" when the commit names a work item, else
    /// "Review: " and the commit subject without its conventional type. The verdict is added once it is known.
    /// </summary>
    public static string ForReview(int? itemNumber, int round, string? subject, string? verdict)
    {
        var builder = new StringBuilder();
        if (itemNumber is { } number && number > 0)
        {
            builder.Append("Review of #").Append(number.ToString(CultureInfo.InvariantCulture));
            if (round > 1)
            {
                builder.Append(", round ").Append(round.ToString(CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(verdict))
            {
                builder.Append(": ").Append(verdict.Trim());
            }

            return builder.ToString();
        }

        var tail = string.IsNullOrWhiteSpace(verdict) ? string.Empty : " (" + verdict.Trim() + ")";
        var described = WithoutCommitType(Flatten(subject));
        var room = MaxLength - tail.Length - "Review: ".Length;
        return "Review: " + (described.Length == 0 ? "a commit" : CapAt(described, room, truncated: false)) + tail;
    }

    /// <summary>The first work item number a commit message names, or null.</summary>
    public static int? ItemIn(string? commitMessage) =>
        WikiReferences.Find(commitMessage)
            .Where(found => found.Reference.Kind == WikiReferenceKind.WorkItem)
            .Select(found => (int?)found.Reference.Number)
            .FirstOrDefault();

    /// <summary>True when a stored derived title is a commit review's (either spelling).</summary>
    public static bool IsReview(string? title) =>
        title is not null
        && (title.StartsWith(LegacyReviewPrefix, StringComparison.Ordinal)
            || title.StartsWith("Review of #", StringComparison.Ordinal)
            || title.StartsWith(ReviewPrefix + ": ", StringComparison.Ordinal));

    /// <summary>
    /// A short title from a prompt, made locally: the greeting trimmed ("Hi.", "Hello,"), the first meaningful clause,
    /// and the length capped at a word boundary. Empty when nothing is left.
    /// </summary>
    public static string FromPrompt(string? prompt)
    {
        var text = prompt ?? string.Empty;

        // A title stored before #351 was cut at 60 characters with "..." on the end; that cut is remembered so the
        // summary still says it is not the whole sentence, and a word it cut in half is not shown as a whole one.
        var truncated = false;
        var trimmed = text.TrimEnd();
        if (trimmed.EndsWith(Ellipsis, StringComparison.Ordinal))
        {
            truncated = true;
            var beforeCut = trimmed[..^Ellipsis.Length];
            text = beforeCut.EndsWith(' ') ? beforeCut : DropLastWord(beforeCut);
        }

        // The first non-empty line that still says something once its greeting is gone.
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var lineIndex = 0;
        var body = string.Empty;
        for (; lineIndex < lines.Length; lineIndex++)
        {
            body = WithoutGreeting(Flatten(lines[lineIndex]));
            if (body.Length > 0)
            {
                break;
            }
        }

        if (body.Length == 0)
        {
            return string.Empty;
        }

        var lastLine = lineIndex >= lines.Length - 1
            || lines.Skip(lineIndex + 1).All(string.IsNullOrWhiteSpace);
        var clause = FirstClause(body, out var cutAtClause);
        var more = truncated && lastLine && !cutAtClause;

        clause = clause.TrimEnd(' ', '.', ',', ';', ':', '!', '-');
        if (clause.Length == 0)
        {
            return string.Empty;
        }

        // The person's own casing is kept: it is their sentence, and a title that differs from what they typed only
        // by a capital letter reads as the studio rewriting them.
        return Cap(clause, more);
    }

    /// <summary>The same title the reviewer would have made, for a review stored before #351.</summary>
    private static string FromStoredReview(string stored)
    {
        if (!stored.StartsWith(LegacyReviewPrefix, StringComparison.Ordinal))
        {
            return stored;
        }

        var subject = stored[LegacyReviewPrefix.Length..].Trim();
        var truncated = subject.EndsWith(Ellipsis, StringComparison.Ordinal);
        if (truncated)
        {
            var beforeCut = subject[..^Ellipsis.Length];
            subject = beforeCut.EndsWith(' ') ? beforeCut.TrimEnd() : DropLastWord(beforeCut);
        }

        if (ItemIn(subject) is { } number)
        {
            return ForReview(number, 1, subject, null);
        }

        var described = WithoutCommitType(subject);
        if (described.Length == 0)
        {
            return "Review: a commit";
        }

        return "Review: " + CapAt(described, MaxLength - "Review: ".Length, truncated);
    }

    /// <summary>The first sentence, or the first clause of an over-long one; never shorter than a few words.</summary>
    private static string FirstClause(string text, out bool cut)
    {
        cut = false;
        var sentenceEnd = SentenceEnd().Match(text);
        var first = text;
        if (sentenceEnd.Success)
        {
            var candidate = text[..sentenceEnd.Index];
            if (candidate.Trim().Length >= ShortestClause)
            {
                first = candidate;
                cut = true;
            }
        }

        if (first.Length <= MaxLength)
        {
            return first;
        }

        // Over long: a natural break inside the cap reads better than a cut word. Only one far enough in to say
        // something, so "Fix, then..." does not become "Fix".
        var breakAt = ClauseBreak().Matches(first)
            .Select(m => m.Index)
            .Where(index => index >= ShortestClause * 2 && index <= MaxLength)
            .DefaultIfEmpty(-1)
            .Max();
        if (breakAt < 0)
        {
            return first;
        }

        cut = true;
        return first[..breakAt];
    }

    /// <summary>Removes greetings and fillers at the start, repeatedly ("Hi. Hello, so ...").</summary>
    private static string WithoutGreeting(string text)
    {
        var current = text;
        while (true)
        {
            var match = Greeting().Match(current);
            if (!match.Success || match.Length == 0)
            {
                return current.Trim();
            }

            current = current[match.Length..].TrimStart();
        }
    }

    /// <summary>"feat(agent-tools): mark the few tools" becomes "mark the few tools".</summary>
    private static string WithoutCommitType(string subject)
    {
        var match = CommitType().Match(subject);
        return (match.Success ? subject[match.Length..] : subject).Trim();
    }

    private static string Flatten(string? text) =>
        Whitespace().Replace(text ?? string.Empty, " ").Trim();

    private static string DropLastWord(string text)
    {
        var at = text.TrimEnd().LastIndexOf(' ');
        return at <= 0 ? text.TrimEnd() : text[..at].TrimEnd();
    }

    private static string Cap(string text, bool truncated) => CapAt(text, MaxLength, truncated);

    /// <summary>
    /// The text within <paramref name="max"/> characters, cut at the last whole word with "..." when it does not fit,
    /// or with "..." added when the text was already a cut (<paramref name="truncated"/>).
    /// </summary>
    private static string CapAt(string text, int max, bool truncated)
    {
        var flat = Flatten(text);
        if (flat.Length <= max && !truncated)
        {
            return flat;
        }

        if (flat.Length + Ellipsis.Length <= max)
        {
            return flat.TrimEnd(' ', '.', ',', ';', ':') + Ellipsis;
        }

        var room = Math.Max(1, max - Ellipsis.Length);
        var head = flat[..Math.Min(room, flat.Length)];
        var space = head.LastIndexOf(' ');
        if (space > room / 2)
        {
            head = head[..space];
        }

        return head.TrimEnd(' ', '.', ',', ';', ':', '-', '(') + Ellipsis;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // A greeting or filler word that opens a prompt, with the punctuation after it. Whole words only, so "Hiding the
    // sidebar" keeps its first word.
    [GeneratedRegex(
        @"^(?:hi|hello|hey|hej|hiya|howdy|yo|greetings|good\s+(?:morning|afternoon|evening|day)|dear\s+\w+|ok(?:ay)?|so|well|alright|right|please|thanks|thank\s+you|claude)\b[\s,.!:;]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Greeting();

    // A sentence ends at ".", "!" or "?" followed by a space (so "v0.9.92" and "e.g." mid-word do not end one) or a ";".
    [GeneratedRegex(@"(?:[.!?]\s|;\s?)")]
    private static partial Regex SentenceEnd();

    // A break inside a sentence a title may stop at.
    [GeneratedRegex(@"(?:,\s|:\s|\s-\s|\s\(|\swhich\s|\sbecause\s|\sbut\s)")]
    private static partial Regex ClauseBreak();

    [GeneratedRegex(@"^[a-z]+(?:\([^)]*\))?!?:\s*", RegexOptions.CultureInvariant)]
    private static partial Regex CommitType();
}
