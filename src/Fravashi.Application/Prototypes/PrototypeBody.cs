using System.Globalization;
using Fravashi.Application.Wiki;

namespace Fravashi.Application.Prototypes;

/// <summary>One fenced block in a markdown body whose info string is <see cref="PrototypeBody.InfoString"/>.</summary>
/// <param name="Start">The index of the opening fence line's first character.</param>
/// <param name="Length">How many characters the block occupies, fences and the closing line end included.</param>
/// <param name="Line">The opening fence's line number, from 1.</param>
/// <param name="Json">The text between the fences, which is the document.</param>
/// <param name="Closed">False when no closing fence was found, so the block runs to the end of the body.</param>
public sealed record PrototypeBlock(int Start, int Length, int Line, string Json, bool Closed);

/// <summary>What a prototype's body holds: its blocks, the reading of the one block, and every error.</summary>
/// <param name="Blocks">Every prototype block found, in order.</param>
/// <param name="Result">The reader's result for the one block, or null when there was not exactly one.</param>
/// <param name="Errors">The body's own errors (path <see cref="PrototypeBody.BodyPath"/>), then the reader's, verbatim.</param>
public sealed record PrototypeBodyRead(
    IReadOnlyList<PrototypeBlock> Blocks, PrototypeReadResult? Result, IReadOnlyList<PrototypeError> Errors)
{
    /// <summary>The block, when the body has exactly one.</summary>
    public PrototypeBlock? Block => Blocks.Count == 1 ? Blocks[0] : null;

    /// <summary>The document, when the body holds exactly one closed block and the reader accepted it.</summary>
    public PrototypeDocument? Document => Result?.Document;

    /// <summary>True when the body holds exactly one closed block and the reader accepted its document.</summary>
    public bool IsValid => Errors.Count == 0 && Result is { IsValid: true };
}

/// <summary>
/// How a prototype is stored (ADR 0021 decision 6, backlog 211): a wiki document of type prototype whose
/// markdown body carries exactly ONE fenced block with the info string <c>prototype</c>, holding the
/// document. The prose around it is the prototype's own description.
/// <para>
/// Fences are found the way CommonMark finds them, exactly as <see cref="Mockups.DesignMockup"/> finds its
/// own: an opening line indented at most three spaces with three or more backticks or tildes, closed by a
/// line of the same character at least as long with nothing after it. Only blocks at the top level of the
/// body count.
/// </para>
/// <para>
/// Nothing here refuses a save. A generated or edited document may be broken; what this produces is shown
/// on the prototype's page and by the <c>prototype_check</c> tool, never hidden.
/// </para>
/// </summary>
public static class PrototypeBody
{
    /// <summary>The info string that marks a fenced block as the prototype's document.</summary>
    public const string InfoString = "prototype";

    /// <summary>The path an error about the body itself (rather than inside the document) carries.</summary>
    public const string BodyPath = "body";

    /// <summary>
    /// The body of a prototype generated for a design (backlog 215, plan @40): one line of prose citing the design,
    /// so the design's backlinks show the prototype, then the one block holding <paramref name="document"/> in
    /// <see cref="PrototypeWriter"/>'s canonical form.
    /// </summary>
    public static string NewBody(PrototypeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var design = document.Design;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"A prototype of @{design.Number}, generated from version {design.Version} of the design.\n\n```{InfoString}\n")
            + PrototypeWriter.Write(document) + "```\n";
    }

    /// <summary>Reads a prototype's body: finds its block and, when there is exactly one, reads the document in it.</summary>
    public static async Task<PrototypeBodyRead> ReadAsync(
        string? markdown, IWikiLibrary wiki, CancellationToken ct = default)
    {
        var blocks = FindBlocks(markdown);
        var errors = new List<PrototypeError>();

        if (blocks.Count == 0)
        {
            errors.Add(new PrototypeError(
                BodyPath,
                "This prototype has no prototype block. Put the document in ONE fenced block whose info "
                + "string is prototype: a line ```prototype, the JSON, then a closing line ```."));
            return new PrototypeBodyRead(blocks, null, errors);
        }

        if (blocks.Count > 1)
        {
            var lines = string.Join(", ", blocks.Select(b => b.Line.ToString(CultureInfo.InvariantCulture)));
            errors.Add(new PrototypeError(
                BodyPath,
                $"This prototype has {blocks.Count} prototype blocks (at lines {lines}); a prototype holds "
                + "exactly one document, so none of them was read. Merge them into one, or change the info "
                + "string of the ones that are not the prototype."));
            return new PrototypeBodyRead(blocks, null, errors);
        }

        var block = blocks[0];
        if (!block.Closed)
        {
            errors.Add(new PrototypeError(
                BodyPath,
                $"The prototype block at line {block.Line} is never closed, so everything after it is read "
                + "as part of the document. Close it with a fence line like the one that opens it."));
        }

        var document = await PrototypeReader.ReadAsync(block.Json, wiki, ct).ConfigureAwait(false);
        errors.AddRange(document.Errors);
        return new PrototypeBodyRead(blocks, document, errors);
    }

    /// <summary>Every top-level fenced block whose info string is <see cref="InfoString"/>, in order.</summary>
    public static IReadOnlyList<PrototypeBlock> FindBlocks(string? markdown)
    {
        var text = markdown ?? string.Empty;
        var found = new List<PrototypeBlock>();
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
                found.Add(new PrototypeBlock(
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
