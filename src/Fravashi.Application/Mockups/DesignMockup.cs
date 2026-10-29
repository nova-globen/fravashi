using System.Globalization;

namespace Fravashi.Application.Mockups;

/// <summary>
/// One fenced block in a markdown body whose info string is <see cref="DesignMockup.InfoString"/>.
/// </summary>
/// <param name="Start">The index of the opening fence line's first character.</param>
/// <param name="Length">How many characters the block occupies, fences and the closing line end included.</param>
/// <param name="Line">The opening fence's line number, from 1.</param>
/// <param name="Json">The text between the fences, which is the tree.</param>
/// <param name="Closed">False when no closing fence was found, so the block runs to the end of the body.</param>
public sealed record MockupBlock(int Start, int Length, int Line, string Json, bool Closed);

/// <summary>What a design's body holds: its mockup blocks, the tree when there is exactly one, and every error.</summary>
/// <param name="Blocks">Every mockup block found, in order.</param>
/// <param name="Tree">The reader's result for the one block, or null when there was not exactly one.</param>
/// <param name="Errors">The body's own errors (path <see cref="DesignMockup.BodyPath"/>), then the reader's, verbatim.</param>
public sealed record DesignMockupRead(
    IReadOnlyList<MockupBlock> Blocks, MockupReadResult? Tree, IReadOnlyList<MockupError> Errors)
{
    /// <summary>The block, when the body has exactly one.</summary>
    public MockupBlock? Block => Blocks.Count == 1 ? Blocks[0] : null;

    /// <summary>True when the body holds exactly one closed block and the reader accepted its tree.</summary>
    public bool IsValid => Errors.Count == 0 && Tree is { IsValid: true };
}

/// <summary>
/// How a mockup is stored (backlog 193, ADR 0021 decision 6): a wiki document of type design whose markdown body
/// carries exactly ONE fenced block with the info string <c>mockup</c>, holding the tree. The prose around it is
/// the design's own description, which is what an approver reads beside the tree.
/// <para>
/// Fences are found the way CommonMark finds them, because Markdig draws the page and the two must agree on where
/// a block is: an opening line indented at most three spaces with three or more backticks or tildes, closed by a
/// line of the same character at least as long with nothing after it. A fence INSIDE another fence is content, so
/// a document that shows how to write a mockup (in a longer fence) is not a second mockup. Only blocks at the top
/// level of the body count; one inside a list item or a quote is not found, and the page then says there is none.
/// </para>
/// <para>
/// Nothing here refuses a save. A draft may be broken; what this produces is shown on the design's page and by
/// the <c>mockup_check</c> tool, never hidden.
/// </para>
/// </summary>
public static class DesignMockup
{
    /// <summary>The info string that marks a fenced block as the design's tree.</summary>
    public const string InfoString = "mockup";

    /// <summary>The path an error about the body itself (rather than inside the tree) carries.</summary>
    public const string BodyPath = "body";

    /// <summary>The title "New design" on <c>/wiki</c> gives the document it creates (#197).</summary>
    public const string NewTitle = "New design";

    /// <summary>
    /// The body of a new design (#197): a line of prose saying what belongs there, and one block holding one empty
    /// screen, so the editor opens it at once and the palette has a window to grow.
    /// </summary>
    public static string NewBody()
    {
        var tree = MockupReader.Read("""{"mockup": 1, "screens": [{"type": "window", "id": "screen", "text": "Screen", "children": []}]}""");
        return "Say here what this screen is for, and anything the tree cannot say.\n\n```" + InfoString + "\n"
            + MockupWriter.Write(tree.Document!) + "```\n";
    }

    /// <summary>Reads a design's body: finds its block and, when there is exactly one, reads the tree in it.</summary>
    public static DesignMockupRead Read(string? markdown)
    {
        var blocks = FindBlocks(markdown);
        var errors = new List<MockupError>();

        if (blocks.Count == 0)
        {
            errors.Add(new MockupError(
                BodyPath,
                "This design has no mockup block. Put the tree in ONE fenced block whose info string is mockup: a line "
                + "```mockup, the JSON, then a closing line ```."));
            return new DesignMockupRead(blocks, null, errors);
        }

        if (blocks.Count > 1)
        {
            var lines = string.Join(", ", blocks.Select(b => b.Line.ToString(CultureInfo.InvariantCulture)));
            errors.Add(new MockupError(
                BodyPath,
                $"This design has {blocks.Count} mockup blocks (at lines {lines}); a design holds exactly one tree, so "
                + "none of them was read. Merge them into one, or change the info string of the ones that are not the "
                + "design."));
            return new DesignMockupRead(blocks, null, errors);
        }

        var block = blocks[0];
        if (!block.Closed)
        {
            errors.Add(new MockupError(
                BodyPath,
                $"The mockup block at line {block.Line} is never closed, so everything after it is read as part of "
                + "the tree. Close it with a fence line like the one that opens it."));
        }

        var tree = MockupReader.Read(block.Json);
        errors.AddRange(tree.Errors);
        return new DesignMockupRead(blocks, tree, errors);
    }

    /// <summary>Every top-level fenced block whose info string is <see cref="InfoString"/>, in order.</summary>
    public static IReadOnlyList<MockupBlock> FindBlocks(string? markdown)
    {
        var text = markdown ?? string.Empty;
        var found = new List<MockupBlock>();
        var lines = Lines(text);

        for (var i = 0; i < lines.Count; i++)
        {
            if (Opening(LineText(text, lines[i])) is not { } fence)
            {
                continue;
            }

            var close = -1;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (Closes(LineText(text, lines[j]), fence.Mark, fence.Length))
                {
                    close = j;
                    break;
                }
            }

            var last = close < 0 ? lines.Count - 1 : close;
            if (string.Equals(fence.Word, InfoString, StringComparison.Ordinal))
            {
                var contentStart = lines[i].End;
                var contentEnd = close < 0 ? text.Length : lines[close].Start;
                var json = contentStart >= contentEnd ? string.Empty : text[contentStart..contentEnd];
                found.Add(new MockupBlock(
                    lines[i].Start, lines[last].End - lines[i].Start, i + 1, json, close >= 0));
            }

            // Whatever the fence was, its content is not markdown, so nothing inside it is a block of its own.
            i = last;
        }

        return found;
    }

    /// <summary>One line: where it starts, and where the next one does (its line end included).</summary>
    private readonly record struct Span(int Start, int End);

    private static List<Span> Lines(string text)
    {
        var lines = new List<Span>();
        var at = 0;
        while (at < text.Length)
        {
            var newline = text.IndexOf('\n', at);
            var end = newline < 0 ? text.Length : newline + 1;
            lines.Add(new Span(at, end));
            at = end;
        }

        return lines;
    }

    private static string LineText(string text, Span line) => text[line.Start..line.End].TrimEnd('\n', '\r');

    /// <summary>The fence a line opens: its character, its length and the first word of its info string.</summary>
    private static (char Mark, int Length, string Word)? Opening(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        if (indent > 3 || indent >= line.Length || (line[indent] != '`' && line[indent] != '~'))
        {
            return null;
        }

        var mark = line[indent];
        var length = 0;
        while (indent + length < line.Length && line[indent + length] == mark)
        {
            length++;
        }

        if (length < 3)
        {
            return null;
        }

        var info = line[(indent + length)..].Trim();

        // CommonMark: a backtick fence's info string cannot hold a backtick, or the line is inline code instead.
        if (mark == '`' && info.Contains('`', StringComparison.Ordinal))
        {
            return null;
        }

        var space = info.IndexOfAny([' ', '\t']);
        return (mark, length, space < 0 ? info : info[..space]);
    }

    private static bool Closes(string line, char mark, int length)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        if (indent > 3)
        {
            return false;
        }

        var run = 0;
        while (indent + run < line.Length && line[indent + run] == mark)
        {
            run++;
        }

        return run >= length && line[(indent + run)..].Trim().Length == 0;
    }
}
