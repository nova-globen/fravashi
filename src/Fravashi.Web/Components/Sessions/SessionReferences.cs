using System.Globalization;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Rendering;

namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// Every <c>#N</c> (work item), <c>@N</c> (wiki document) and <c>!N</c> (pull request) in a conversation as a link
/// (work item #351), found by the wiki's own parser (<see cref="WikiReferences"/>) and, in markdown, written by the
/// wiki's own renderer (<see cref="WikiMarkdownRenderer.Rewrite"/>), so a conversation and a wiki page can never
/// disagree about what a reference is. Nothing inside a code span or a fenced code block becomes a link: the parser
/// skips both.
/// <para>
/// Each link is a real link to the item's page, with the wiki's class on it. Inside a conversation a plain click on one
/// opens its preview beside the conversation instead (sessionScroll.js asks the transcript), and anywhere else, or with
/// a modifier held, it is simply the link.
/// </para>
/// </summary>
public static class SessionReferences
{
    /// <summary>The class the links carry: the wiki's, which is also what the click handler looks for.</summary>
    public const string LinkClass = WikiMarkdownRenderer.ResolvedClass;

    /// <summary>One piece of plain text: either words, or a reference.</summary>
    public readonly record struct Piece(string Text, WikiReference? Reference);

    /// <summary>
    /// The markdown with every reference rewritten as a link to its page. The conversation does not look each one up
    /// first (a message is rendered once per streamed token, and a lookup per reference would stall it), so every
    /// reference is a link and the preview says when nothing of that number exists.
    /// </summary>
    public static string LinkMarkdown(string? markdown)
    {
        var text = markdown ?? string.Empty;
        var found = WikiReferences.Find(text);
        if (found.Count == 0)
        {
            return text;
        }

        var map = new WikiReferenceMap();
        foreach (var occurrence in found)
        {
            map.Add(occurrence.Reference, Tooltip(occurrence.Reference));
        }

        return WikiMarkdownRenderer.Rewrite(text, map);
    }

    /// <summary>Plain text cut into words and references, for text the studio shows verbatim.</summary>
    public static IReadOnlyList<Piece> Split(string? text)
    {
        var value = text ?? string.Empty;
        var found = WikiReferences.Find(value);
        if (found.Count == 0)
        {
            return value.Length == 0 ? [] : [new Piece(value, null)];
        }

        var pieces = new List<Piece>(found.Count * 2 + 1);
        var at = 0;
        foreach (var occurrence in found)
        {
            if (occurrence.Start > at)
            {
                pieces.Add(new Piece(value[at..occurrence.Start], null));
            }

            pieces.Add(new Piece(value.Substring(occurrence.Start, occurrence.Length), occurrence.Reference));
            at = occurrence.Start + occurrence.Length;
        }

        if (at < value.Length)
        {
            pieces.Add(new Piece(value[at..], null));
        }

        return pieces;
    }

    /// <summary>Reads a reference as a link shows it ("#343"), or null when the text is not exactly one.</summary>
    public static WikiReference? Parse(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return WikiReferences.TryReadAt(value, 0, out var found) && found.Length == value.Length
            ? found.Reference
            : null;
    }

    /// <summary>The page a reference opens.</summary>
    public static string Route(WikiReference reference) => WikiReferenceMap.RouteFor(reference);

    /// <summary>What a reference's link says when pointed at.</summary>
    public static string Tooltip(WikiReference reference) =>
        "Preview " + KindWords(reference.Kind) + " " + reference.Text;

    /// <summary>The kind of thing a reference points at, in words.</summary>
    public static string KindWords(WikiReferenceKind kind) => kind switch
    {
        WikiReferenceKind.WorkItem => "work item",
        WikiReferenceKind.WikiItem => "wiki document",
        WikiReferenceKind.PullRequest => "pull request",
        _ => kind.ToString().ToLower(CultureInfo.InvariantCulture),
    };
}
