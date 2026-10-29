using System.Text;
using Markdig;
using Markdig.Syntax;

namespace Fravashi.Infrastructure.Rendering;

/// <summary>
/// Renders markdown to HTML for the Code page's preview pane. Raw HTML in the source is disabled
/// (rendered as text) so a previewed file cannot inject markup into the studio page, and every link and
/// image target passes <see cref="LinkUrlPolicy"/> (backlog 286); everything else uses Markdig's advanced
/// pipeline (tables, task lists, autolinks).
/// </summary>
public sealed class MarkdownPreviewRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Use<SafeLinksExtension>()
        .Build();

    /// <summary>
    /// The pipeline a transcript's PROSE runs through: the same as the preview's, plus the copy control
    /// for a code block nested inside a list item or a blockquote (see <see cref="ToSegments"/> and
    /// <see cref="TranscriptCodeBlockRenderer"/>). A top-level block never reaches it, because the split
    /// has already taken those out.
    /// </summary>
    private readonly MarkdownPipeline _transcriptPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Use<SafeLinksExtension>()
        .Use(new TranscriptCodeBlockExtension())
        .Build();

    public string ToHtml(string? markdown) => Markdig.Markdown.ToHtml(markdown ?? string.Empty, _pipeline);

    /// <summary>
    /// The same document, split at its TOP-LEVEL code blocks, so a display can put a control on each
    /// block and still copy the source rather than the rendering (backlog 110).
    ///
    /// Two deliberate limits, both recorded rather than hidden:
    ///
    /// 1. Only top-level blocks are split OUT. A code block nested in a list item or a blockquote stays
    ///    inside its prose run, because cutting the source there would leave the two halves of a list to
    ///    be parsed separately and the list would renumber itself on screen. It is not left without a
    ///    control, which is where this landed first: the prose pipeline writes the same control into the
    ///    rendered HTML with the same source on it (<see cref="TranscriptCodeBlockRenderer"/>), and
    ///    sessionCopy.js answers the click.
    /// 2. Each prose run is rendered on its own, so a link reference definition declared on one side of a
    ///    code block does not resolve on the other. Agent prose does not use them, and the alternative
    ///    (render once, then cut the HTML apart) is the DOM scraping this exists to avoid.
    ///
    /// An UNCLOSED fence is normal here rather than exceptional: the transcript renders the partial
    /// message while the agent is still writing it. Markdig ends such a block at the end of the input,
    /// so a half-written block is a code segment carrying what has arrived so far.
    /// </summary>
    public IReadOnlyList<MarkdownSegment> ToSegments(string? markdown)
    {
        var text = markdown ?? string.Empty;
        if (text.Length == 0)
        {
            return [];
        }

        var document = Markdig.Markdown.Parse(text, _pipeline);
        var segments = new List<MarkdownSegment>();
        var proseStart = 0;

        foreach (var block in document)
        {
            if (block is not CodeBlock code)
            {
                continue;
            }

            AddProse(segments, text, proseStart, code.Span.Start);
            segments.Add(new MarkdownSegment(true, string.Empty, SourceOf(code), LanguageOf(code)));

            proseStart = Math.Max(proseStart, EndOf(text, code));
        }

        AddProse(segments, text, proseStart, text.Length);

        // A document with no code block at all is one prose segment, which is exactly what the old
        // single ToHtml call produced.
        return segments;
    }

    /// <summary>
    /// Where a code block's source really ends, line break included.
    ///
    /// Span.End alone is not it, and the case that matters is the one EVERY streamed block passes through:
    /// while a fence is still OPEN, Markdig's span for the block stops at the opening backticks and never
    /// grows. Measured on "Here:\n\n```javascript\nconst a = 1;": the FencedCodeBlock's span is 7..10, so
    /// slicing the prose from Span.End + 1 handed the info string AND the code back as prose, and the
    /// transcript rendered "javascript" followed by the line the agent was typing, under the block that
    /// already showed it. It looks like a fence-marker bug and it is a span bug.
    ///
    /// So the end is taken from what the parser KEPT (the block's own lines, whose slices carry their
    /// positions in the source) as well as from the span, and then rounded out to the end of that line: a
    /// code block always ends at a line boundary, and on a closed block the closing fence is past the last
    /// line of content.
    /// </summary>
    private static int EndOf(string text, CodeBlock code)
    {
        var end = code.Span.End;
        var lines = code.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var slice = lines.Lines[i].Slice;
            if (slice.End > end)
            {
                end = slice.End;
            }
        }

        return EndOfLine(text, end);
    }

    /// <summary>The end of the line the index falls on, its line break included.</summary>
    private static int EndOfLine(string text, int lastIndex)
    {
        // A span that already ends ON its line break is complete; walking on from there would swallow the
        // whole next line.
        if (lastIndex >= 0 && lastIndex < text.Length && text[lastIndex] is '\n' or '\r')
        {
            return lastIndex + 1;
        }

        var index = Math.Clamp(lastIndex + 1, 0, text.Length);
        while (index < text.Length && text[index] is not ('\n' or '\r'))
        {
            index++;
        }

        if (index < text.Length && text[index] == '\r')
        {
            index++;
        }

        if (index < text.Length && text[index] == '\n')
        {
            index++;
        }

        return index;
    }

    private void AddProse(List<MarkdownSegment> segments, string text, int start, int end)
    {
        if (end <= start || start >= text.Length)
        {
            return;
        }

        var slice = text[start..Math.Min(end, text.Length)];
        if (slice.AsSpan().IsWhiteSpace())
        {
            return;
        }

        var html = Markdig.Markdown.ToHtml(slice, _transcriptPipeline);
        segments.Add(new MarkdownSegment(false, html, slice, Language: null));
    }

    /// <summary>
    /// The block's own lines, which is the content WITHOUT the fence markers and without the four-space
    /// indent of an indented block: Markdig strips both while parsing, so what it kept is the code.
    /// </summary>
    internal static string SourceOf(CodeBlock code)
    {
        var lines = code.Lines;
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(lines.Lines[i].Slice.ToString());
        }

        return builder.ToString();
    }

    internal static string? LanguageOf(CodeBlock code) =>
        code is FencedCodeBlock { Info: { Length: > 0 } info } ? info : null;
}
