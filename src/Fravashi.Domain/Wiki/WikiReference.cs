using System.Globalization;

namespace Fravashi.Domain.Wiki;

/// <summary>What a reference in wiki or work-item prose points at (backlog 39).</summary>
public enum WikiReferenceKind
{
    /// <summary>A work item, written <c>#432</c>.</summary>
    WorkItem,

    /// <summary>A pull request, written <c>!6243</c>.</summary>
    PullRequest,

    /// <summary>A wiki item, written <c>@7354</c>.</summary>
    WikiItem,
}

/// <summary>
/// One reference, modelled on Azure DevOps so a person and an agent navigate the graph the same way
/// (backlog 39): <c>#432</c> a work item, <c>!6243</c> a pull request, <c>@7354</c> a wiki item.
/// <para>
/// The syntax is deliberately three characters and a number rather than anything richer. It has to be
/// typeable mid-sentence by someone who is not thinking about markup, and it has to survive being pasted
/// between a work item, a wiki page and a commit message without carrying a link that rots.
/// </para>
/// </summary>
/// <param name="Kind">What it points at.</param>
/// <param name="Number">The target's number within its own surface.</param>
public readonly record struct WikiReference(WikiReferenceKind Kind, int Number)
{
    /// <summary>The character that opens a reference of this kind.</summary>
    public static char MarkerFor(WikiReferenceKind kind) => kind switch
    {
        WikiReferenceKind.WorkItem => '#',
        WikiReferenceKind.PullRequest => '!',
        WikiReferenceKind.WikiItem => '@',
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a reference kind."),
    };

    /// <summary>The kind a marker opens, or null when the character opens none.</summary>
    public static WikiReferenceKind? KindFor(char marker) => marker switch
    {
        '#' => WikiReferenceKind.WorkItem,
        '!' => WikiReferenceKind.PullRequest,
        '@' => WikiReferenceKind.WikiItem,
        _ => null,
    };

    /// <summary>The reference as it is written.</summary>
    public string Text => MarkerFor(Kind) + Number.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>One reference found in a body of text, with where it was found.</summary>
/// <param name="Reference">What it points at.</param>
/// <param name="Start">The index of the marker character.</param>
/// <param name="Length">How many characters the whole reference occupies.</param>
public readonly record struct FoundReference(WikiReference Reference, int Start, int Length);

/// <summary>
/// Finds references in markdown (backlog 39). The rules are small and all of them exist because the
/// alternative was measurably wrong somewhere:
/// <list type="bullet">
/// <item>A reference is a marker, then at least one digit, and it ends where the digits end.</item>
/// <item>The character BEFORE it must not be a letter, a digit or a marker, so <c>abc#4</c> and an email
/// address's <c>@</c> are not references, and neither is the second half of <c>##4</c>.</item>
/// <item>The character AFTER the digits must not be a letter or a digit, so <c>#4abc</c> is not one.</item>
/// <item>Nothing inside a fenced code block or an inline code span is a reference, because a document that
/// explains this syntax must be able to show it.</item>
/// <item>Nothing inside a link destination is one, because <c>](/x#4)</c> is a fragment, not a work item.</item>
/// </list>
/// <para>
/// This is the one place the syntax is decided. The renderer's Markdig extension asks THIS type where the
/// references are rather than re-implementing the scan, so what is stored as a link and what is drawn as one
/// cannot drift apart.
/// </para>
/// </summary>
public static class WikiReferences
{
    /// <summary>Every reference in the text, in the order they appear.</summary>
    public static IReadOnlyList<FoundReference> Find(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return [];
        }

        var found = new List<FoundReference>();
        var text = markdown;
        var i = 0;

        while (i < text.Length)
        {
            // A fenced block runs to its closing fence or to the end of the document, which is what an
            // unclosed fence means to every markdown parser and therefore what it must mean here.
            if (AtFence(text, i, out var fenceEnd))
            {
                i = fenceEnd;
                continue;
            }

            if (text[i] == '`')
            {
                i = SkipCodeSpan(text, i);
                continue;
            }

            // A link destination: "](" to the matching ")". Only the destination is skipped, so the link's
            // TEXT can still carry a reference, which is how "[see #4](/x)" behaves for a reader.
            if (text[i] == ']' && i + 1 < text.Length && text[i + 1] == '(')
            {
                i = SkipLinkDestination(text, i + 1);
                continue;
            }

            if (TryReadAt(text, i, out var reference))
            {
                found.Add(reference);
                i = reference.Start + reference.Length;
                continue;
            }

            i++;
        }

        return found;
    }

    /// <summary>
    /// Reads a reference that starts exactly at <paramref name="index"/>, boundary rules included. This is
    /// the primitive the renderer's inline parser calls, so the two agree by construction.
    /// </summary>
    public static bool TryReadAt(string text, int index, out FoundReference found)
    {
        found = default;
        if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length)
        {
            return false;
        }

        if (WikiReference.KindFor(text[index]) is not { } kind)
        {
            return false;
        }

        if (index > 0 && !IsLeftBoundary(text[index - 1]))
        {
            return false;
        }

        var end = index + 1;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        if (end == index + 1)
        {
            return false;
        }

        if (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            return false;
        }

        var digits = text.AsSpan(index + 1, end - index - 1);

        // A number nobody could have allocated is not a reference. Overflow is the honest reading of
        // "#99999999999999": it is a string of digits, not a pointer at anything.
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return false;
        }

        found = new FoundReference(new WikiReference(kind, number), index, end - index);
        return true;
    }

    /// <summary>A character a reference may follow: not a letter, a digit, an underscore or a marker.</summary>
    private static bool IsLeftBoundary(char c) =>
        !char.IsLetterOrDigit(c) && c != '_' && WikiReference.KindFor(c) is null;

    /// <summary>True when a fenced code block opens at this index; <paramref name="end"/> is past its close.</summary>
    private static bool AtFence(string text, int index, out int end)
    {
        end = index;
        if ((index != 0 && text[index - 1] != '\n') || index + 2 >= text.Length)
        {
            return false;
        }

        var marker = text[index];
        if (marker != '`' && marker != '~')
        {
            return false;
        }

        var run = 0;
        while (index + run < text.Length && text[index + run] == marker)
        {
            run++;
        }

        if (run < 3)
        {
            return false;
        }

        // Past the opening line, then look for a line whose first characters are a fence of the same
        // marker at least as long. An unclosed fence swallows the rest of the document.
        var line = text.IndexOf('\n', index);
        if (line < 0)
        {
            end = text.Length;
            return true;
        }

        var at = line + 1;
        while (at < text.Length)
        {
            var lineEnd = text.IndexOf('\n', at);
            var stop = lineEnd < 0 ? text.Length : lineEnd;
            var close = 0;
            while (at + close < stop && text[at + close] == marker)
            {
                close++;
            }

            if (close >= run)
            {
                end = lineEnd < 0 ? text.Length : lineEnd + 1;
                return true;
            }

            if (lineEnd < 0)
            {
                break;
            }

            at = lineEnd + 1;
        }

        end = text.Length;
        return true;
    }

    /// <summary>Past an inline code span, or past the lone backtick when nothing closes it.</summary>
    private static int SkipCodeSpan(string text, int index)
    {
        var run = 0;
        while (index + run < text.Length && text[index + run] == '`')
        {
            run++;
        }

        var at = index + run;
        while (at < text.Length)
        {
            if (text[at] != '`')
            {
                at++;
                continue;
            }

            var close = 0;
            while (at + close < text.Length && text[at + close] == '`')
            {
                close++;
            }

            if (close == run)
            {
                return at + close;
            }

            at += close;
        }

        // Nothing closes it, so the backticks are ordinary characters and the text after them is scanned.
        return index + run;
    }

    /// <summary>Past a link destination that opens at <paramref name="open"/>, which is the "(".</summary>
    private static int SkipLinkDestination(string text, int open)
    {
        var depth = 0;
        for (var at = open; at < text.Length; at++)
        {
            if (text[at] == '(')
            {
                depth++;
            }
            else if (text[at] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return at + 1;
                }
            }
            else if (text[at] == '\n')
            {
                // A destination does not span a blank line; treat an unclosed one as ordinary text.
                return open + 1;
            }
        }

        return open + 1;
    }
}
