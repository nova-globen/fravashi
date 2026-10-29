using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Fravashi.Infrastructure.Rendering;

/// <summary>
/// Gives a NESTED code block the same copy control a top-level one gets (backlog 110).
///
/// Why this exists rather than more splitting: <see cref="MarkdownPreviewRenderer.ToSegments"/> cuts the
/// message at its top-level code blocks, and a block inside a list item or a blockquote cannot be cut out
/// that way. Cutting there would hand the two halves of the list to the parser separately, so the
/// continuation would re-open as its own list and a prose line indented under a deep item would come back
/// as an indented CODE block. An agent putting a command inside a numbered list is an everyday shape, so
/// "no control at all there" was not an option either.
///
/// So the containing block is rendered whole, structure intact, and the control is written INTO the
/// rendered HTML with the block's source on it. The source still comes from what Markdig parsed
/// (<see cref="MarkdownPreviewRenderer.SourceOf"/>), never from the rendering: this is not DOM scraping
/// wearing a different hat, it is the same source the segment carries, put where a nested block can reach
/// it. sessionCopy.js answers the click, because markup inside a MarkupString has no Blazor handlers.
///
/// The markup matches what ChatBubble draws for a top-level block, class for class, so the two kinds of
/// block look identical on screen and a test can count them together.
/// </summary>
internal sealed class TranscriptCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
{
    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        var source = MarkdownPreviewRenderer.SourceOf(obj);
        var language = MarkdownPreviewRenderer.LanguageOf(obj) ?? "code";

        renderer.EnsureLine();
        renderer.Write("<div class=\"session-code\" data-testid=\"code-block\"><div class=\"session-code-bar\"><span class=\"session-code-lang\">");
        renderer.WriteEscape(language);
        renderer.Write("</span><button type=\"button\" class=\"session-copy session-copy-code\" data-testid=\"copy-code\" title=\"Copy this code block\" data-copy-source=\"");
        renderer.WriteEscape(source);
        renderer.Write("\">Copy</button></div><pre class=\"session-code-pre\"><code>");
        renderer.WriteEscape(source);
        renderer.WriteLine("</code></pre></div>");
    }
}

/// <summary>
/// Puts <see cref="TranscriptCodeBlockRenderer"/> in place of Markdig's own code-block renderer. Used
/// ONLY by the transcript's prose pipeline: the Code page's markdown preview is not a conversation and
/// keeps the plain rendering.
/// </summary>
internal sealed class TranscriptCodeBlockExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        // Nothing to add on the parsing side: this changes how a code block is WRITTEN, not how it is found.
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer html)
        {
            html.ObjectRenderers.Replace<CodeBlockRenderer>(new TranscriptCodeBlockRenderer());
        }
    }
}
