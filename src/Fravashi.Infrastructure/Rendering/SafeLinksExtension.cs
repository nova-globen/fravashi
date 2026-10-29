using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Fravashi.Infrastructure.Rendering;

/// <summary>
/// Applies <see cref="LinkUrlPolicy"/> to a parsed document before anything renders it (backlog 286). Every
/// studio markdown pipeline uses it: the Code page's preview, the transcript's prose and the wiki.
/// <para>
/// It rewrites the TREE rather than the HTML, so it sees each URL after Markdig has decoded it and resolved
/// any reference definition, which is the value the renderer will write. A refused link is replaced by its
/// own children, so its text stays and only the anchor goes; a refused image by its alt text; a refused
/// autolink by its text.
/// </para>
/// <para>
/// The same walk closes a second door the same audit found: the advanced pipeline's generic attributes let a
/// document write <c>{onclick="..."}</c> or <c>{href="javascript:..."}</c> onto any element, and
/// DisableHtml does nothing about those either. Classes and the id are kept (the wiki's references are
/// styled by a class); of the other attributes only <see cref="AllowedProperties"/> survive.
/// </para>
/// </summary>
public sealed class SafeLinksExtension : IMarkdownExtension
{
    /// <summary>
    /// The generic attributes a document may still set. Short on purpose: an event handler, a URL-bearing
    /// attribute or a style (which can lay a fake control over a real one) is never on it.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedProperties =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "title", "lang", "dir", "width", "height", "alt" };

    public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += Neutralise;

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        // Nothing on the rendering side: the tree is already safe by the time a renderer sees it.
    }

    /// <summary>Rewrites the document in place.</summary>
    public static void Neutralise(MarkdownDocument document)
    {
        // Materialised first: the loop below changes the tree it would otherwise be walking.
        foreach (var node in document.Descendants().ToList())
        {
            StripProperties(node);

            switch (node)
            {
                case LinkInline link when !LinkUrlPolicy.IsAllowed(link.GetDynamicUrl?.Invoke() ?? link.Url):
                    Unwrap(link);
                    break;
                case AutolinkInline auto when !auto.IsEmail && !LinkUrlPolicy.IsAllowed(auto.Url):
                    auto.ReplaceBy(new LiteralInline(auto.Url));
                    break;
            }
        }
    }

    private static void Unwrap(LinkInline link)
    {
        var child = link.FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            child.Remove();
            link.InsertBefore(child);
            child = next;
        }

        link.Remove();
    }

    private static void StripProperties(MarkdownObject node)
    {
        if (node.TryGetAttributes()?.Properties is { Count: > 0 } properties)
        {
            properties.RemoveAll(p => !AllowedProperties.Contains(p.Key));
        }
    }
}
