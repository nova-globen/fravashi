using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Fravashi.Domain.Wiki;

namespace Fravashi.Web.Components.Wiki;

/// <summary>One kind of document in the wiki's list (work item #365): its type, and its documents newest first.</summary>
/// <param name="Type">The kind every document in the group has.</param>
/// <param name="Documents">The documents, the most recently changed first.</param>
public sealed record WikiDocumentGroup(WikiItemType Type, IReadOnlyList<WikiItem> Documents);

/// <summary>One heading of an open document, for its "On this page" list (work item #365).</summary>
/// <param name="Level">1 to 6, as the markdown wrote it.</param>
/// <param name="Id">The element id the renderer gave it, which is what scrolling goes to.</param>
/// <param name="Text">The heading's words, without markup.</param>
public sealed record WikiHeading(int Level, string Id, string Text);

/// <summary>
/// What the wiki page works out from its documents and from a rendered body to help a person find their way
/// (work item #365): the list grouped by kind, a document's headings, and file paths made into previewable links.
/// Kept apart from the page so each rule is tested on its own.
/// </summary>
public static partial class WikiReading
{
    /// <summary>The class a file path in a rendered document carries; the page's click handler looks for it.</summary>
    public const string FileRefClass = "wiki-file-ref";

    /// <summary>
    /// The documents grouped by kind, the groups in the type's declared order (Plan first) and only the kinds that
    /// have documents, each group's documents the most recently changed first (ties by number, newest first).
    /// </summary>
    public static IReadOnlyList<WikiDocumentGroup> Group(IEnumerable<WikiItem> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return
        [
            .. documents
                .GroupBy(d => d.Type)
                .OrderBy(g => (int)g.Key)
                .Select(g => new WikiDocumentGroup(
                    g.Key,
                    [.. g.OrderByDescending(d => d.UpdatedUtc).ThenByDescending(d => d.Number)])),
        ];
    }

    /// <summary>The most recently changed documents, for the reading pane before anything is open.</summary>
    public static IReadOnlyList<WikiItem> Recent(IEnumerable<WikiItem> documents, int count)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return [.. documents.OrderByDescending(d => d.UpdatedUtc).ThenByDescending(d => d.Number).Take(count)];
    }

    /// <summary>
    /// The headings of a rendered body, in order, as the renderer wrote them: only those it gave an id, because a
    /// heading with none has nowhere to scroll to.
    /// </summary>
    public static IReadOnlyList<WikiHeading> Headings(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }

        var headings = new List<WikiHeading>();
        foreach (Match match in HeadingPattern().Matches(html))
        {
            var text = WebUtility.HtmlDecode(TagPattern().Replace(match.Groups["inner"].Value, string.Empty)).Trim();
            if (text.Length > 0)
            {
                headings.Add(new WikiHeading(
                    match.Groups["level"].Value[0] - '0',
                    WebUtility.HtmlDecode(match.Groups["id"].Value),
                    text));
            }
        }

        return headings;
    }

    /// <summary>
    /// The rendered body with every inline code span that reads as a workspace file path (<c>src/Foo/Bar.cs</c>: at
    /// least one folder, a file name with an extension, no <c>.</c> or <c>..</c> segment, no scheme) wrapped in a link
    /// to the file in the editor carrying <see cref="FileRefClass"/>, which the page turns into a preview. A code block
    /// is left alone, and so is a code span already inside a link.
    /// </summary>
    public static string LinkFilePaths(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html ?? string.Empty;
        }

        var result = new StringBuilder(html.Length + 64);
        var at = 0;
        var insideLink = 0;
        foreach (Match match in FileTokenPattern().Matches(html))
        {
            if (match.Value.StartsWith("</a", StringComparison.OrdinalIgnoreCase))
            {
                insideLink = Math.Max(0, insideLink - 1);
                continue;
            }

            if (!match.Groups["path"].Success)
            {
                insideLink++;
                continue;
            }

            var path = WebUtility.HtmlDecode(match.Groups["path"].Value);
            if (insideLink > 0 || ReadPath(path) is not { } clean)
            {
                continue;
            }

            result.Append(html, at, match.Index - at);
            var encoded = WebUtility.HtmlEncode(clean);
            result.Append("<a class=\"").Append(FileRefClass).Append("\" href=\"")
                .Append(WebUtility.HtmlEncode(Sessions.ReferencePreviewPanel.FileRoute(clean)))
                .Append("\" data-path=\"").Append(encoded)
                .Append("\" title=\"Preview the file ").Append(encoded).Append("\">")
                .Append(match.Value)
                .Append("</a>");
            at = match.Index + match.Length;
        }

        result.Append(html, at, html.Length - at);
        return result.ToString();
    }

    /// <summary>The path as a workspace-relative file path, a leading <c>./</c> dropped, or null when it is not one.</summary>
    public static string? ReadPath(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        if (!PathPattern().IsMatch(value))
        {
            return null;
        }

        var segments = value.Split('/');
        return segments.Any(s => s is "." or "..") ? null : value;
    }

    [GeneratedRegex("""<h(?<level>[1-6])\b[^>]*?\bid="(?<id>[^"]+)"[^>]*>(?<inner>.*?)</h\k<level>>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    // An opening anchor, a closing one, or an attribute-less inline code element with no whitespace in it (a code
    // block's element always ends in a line break, and a fenced one carries a language class).
    [GeneratedRegex("""<a\b[^>]*>|</a\s*>|<code>(?<path>[^<>\s]+)</code>""", RegexOptions.IgnoreCase)]
    private static partial Regex FileTokenPattern();

    [GeneratedRegex("""^(?:[A-Za-z0-9_.\-]+/)+[A-Za-z0-9_\-][A-Za-z0-9_.\-]*\.[A-Za-z0-9]{1,10}$""")]
    private static partial Regex PathPattern();
}
