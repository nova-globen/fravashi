using System.Text;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Markdig;

namespace Fravashi.Infrastructure.Rendering;

/// <summary>
/// Renders a wiki body, turning every reference into a real link into the studio (backlog 39).
/// <para>
/// A reference is rewritten in the SOURCE, at the positions <see cref="WikiReferences"/> found, and the
/// result is then rendered by the same Markdig pipeline as every other preview. That is deliberate: the
/// syntax is decided in exactly one place, so what the store indexes as a link and what the reader sees as
/// one cannot disagree. A Markdig inline parser would have been the other way to do it, and it would have
/// been a second implementation of the same rules.
/// </para>
/// <para>
/// A reference to something that does not exist renders as code rather than as a link, which is the
/// acceptance's "visibly broken rather than silently plain text". It is not a link to nowhere, because a
/// link that answers nothing is worse than a mark that says there is nothing to answer.
/// </para>
/// </summary>
public sealed class WikiMarkdownRenderer
{
    /// <summary>The class a resolved reference carries, for styling.</summary>
    public const string ResolvedClass = "wiki-ref";

    /// <summary>The class a reference to something that does not exist carries.</summary>
    public const string BrokenClass = "wiki-ref-broken";

    // Raw HTML is disabled, as in the preview renderer: a wiki page is authored content and must not be
    // able to put markup into the studio's own page. Generic attributes come from the advanced set and are
    // what let a rewritten reference carry a class without any HTML in the source. Link and image targets
    // pass the same URL policy as every other surface (backlog 286); a reference's own route is relative.
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Use<SafeLinksExtension>()
        .Build();

    /// <summary>The body as HTML, with references resolved through the map.</summary>
    public string ToHtml(string? markdown, WikiReferenceMap? references) =>
        Markdig.Markdown.ToHtml(Rewrite(markdown, references ?? WikiReferenceMap.Empty), _pipeline);

    /// <summary>
    /// The body with each reference replaced by markdown that renders as a link (or as broken). Exposed so a
    /// test can read what was produced rather than inferring it from HTML.
    /// </summary>
    public static string Rewrite(string? markdown, WikiReferenceMap references)
    {
        ArgumentNullException.ThrowIfNull(references);

        var text = markdown ?? string.Empty;
        var found = WikiReferences.Find(text);
        if (found.Count == 0)
        {
            return text;
        }

        var rewritten = new StringBuilder(text.Length + (found.Count * 32));
        var at = 0;

        foreach (var occurrence in found)
        {
            rewritten.Append(text, at, occurrence.Start - at);

            var reference = occurrence.Reference;
            var shown = reference.Text;
            if (references.Resolve(reference) is { } target)
            {
                rewritten.Append('[').Append(shown).Append("](").Append(target.Url);
                if (target.Title is { Length: > 0 } title)
                {
                    // A markdown title, with its quotes and backslashes escaped so a document's own
                    // punctuation cannot end the attribute early.
                    rewritten.Append(" \"").Append(EscapeTitle(title)).Append('"');
                }

                rewritten.Append("){.").Append(ResolvedClass).Append('}');
            }
            else
            {
                rewritten.Append('`').Append(shown).Append("`{.").Append(BrokenClass).Append('}');
            }

            at = occurrence.Start + occurrence.Length;
        }

        rewritten.Append(text, at, text.Length - at);
        return rewritten.ToString();
    }

    private static string EscapeTitle(string title) =>
        title.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
