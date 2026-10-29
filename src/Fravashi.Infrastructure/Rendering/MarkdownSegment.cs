namespace Fravashi.Infrastructure.Rendering;

/// <summary>
/// One piece of a rendered markdown message: either a run of prose (already HTML) or one code block
/// whose SOURCE is carried alongside it.
///
/// Backlog 110. The copy affordance on a code block has to hand over what the agent actually wrote, and
/// the only honest way to have that is to keep it: scraping it back out of the rendered DOM gives the
/// entity-decoded, highlight-wrapped, line-numbered version of the text rather than the text. So the
/// splitter keeps each code block's source and the display renders from it.
/// </summary>
/// <param name="IsCode">True for a code block, false for a run of prose.</param>
/// <param name="Html">The rendered HTML of a prose run. Empty on a code segment.</param>
/// <param name="Source">The raw source: the code block's content (no fence markers) on a code
/// segment, the markdown slice on a prose one.</param>
/// <param name="Language">The fenced block's info string when it carried one.</param>
public sealed record MarkdownSegment(bool IsCode, string Html, string Source, string? Language);
