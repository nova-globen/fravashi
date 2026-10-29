using System.Text;
using System.Text.RegularExpressions;

namespace Fravashi.Domain.Wiki;

/// <summary>Something a conversion could not represent, named so it can be reported rather than dropped.</summary>
/// <param name="What">What was met, in the source's own vocabulary.</param>
/// <param name="Detail">Enough of it to find in the document.</param>
public sealed record ConversionLoss(string What, string Detail);

/// <summary>The result of converting a document, and everything the conversion could not carry.</summary>
/// <param name="Text">The converted document.</param>
/// <param name="Losses">What could not be represented. Empty means the conversion carried everything.</param>
public sealed record ConversionResult(string Text, IReadOnlyList<ConversionLoss> Losses)
{
    /// <summary>True when nothing was lost, which is the only case a caller may treat as a clean round trip.</summary>
    public bool Lossless => Losses.Count == 0;
}

/// <summary>
/// Converts between the markdown the wiki stores (backlog 39) and the storage format Confluence keeps
/// pages in (backlog 83's "second, quieter problem").
/// <para>
/// <b>What this is and is not.</b> It converts the constructs a document is actually made of: headings,
/// paragraphs, emphasis, code, links, lists. It does NOT pretend to convert a provider's macros, layouts,
/// attachments or embedded panels, because a converter that quietly turned a macro into the text inside it
/// would produce a document that looked right and had lost the thing that made it work. Everything it
/// cannot carry is REPORTED, which is exactly what this item's acceptance asks for: anything the
/// conversion cannot represent is reported rather than silently dropped.
/// </para>
/// <para>
/// A caller decides what to do about a loss. Refusing to push is a reasonable answer and so is pushing
/// with the losses shown to the user; what is not reasonable is not knowing.
/// </para>
/// </summary>
public static class WikiFormatConversion
{
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(250);

    private static readonly Regex Macro = new(
        @"<ac:structured-macro[^>]*ac:name=""(?<name>[^""]+)""", RegexOptions.IgnoreCase, Patience);

    private static readonly Regex Attachment = new(
        @"<ri:attachment[^>]*ri:filename=""(?<name>[^""]+)""", RegexOptions.IgnoreCase, Patience);

    private static readonly Regex Layout = new(@"<ac:layout\b", RegexOptions.IgnoreCase, Patience);

    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.None, Patience);

    /// <summary>
    /// The attributes an ordinary tag may carry. A page saved by the site's own editor writes its
    /// bookkeeping on every element (<c>&lt;h2 local-id="..."&gt;</c>), and until 2026-09-21 every pattern
    /// here knew only the bare tag, so a page a person had touched came back flattened into one run of
    /// text with each of its elements reported as having no markdown equal (backlog 83, seen live). The
    /// leading whitespace is what keeps <c>&lt;param&gt;</c> from being read as a paragraph.
    /// </summary>
    private const string Attributes = @"(?:\s[^>]*?)?";

    /// <summary>The rest of an OPENING tag: its attributes and a close that is not a self-close.</summary>
    private const string Open = Attributes + @"(?<!/)>";

    /// <summary>
    /// A Confluence storage-format page as markdown, with everything it could not carry reported.
    /// </summary>
    public static ConversionResult ToMarkdown(string? storage)
    {
        var text = storage ?? string.Empty;
        var losses = new List<ConversionLoss>();

        foreach (Match match in Macro.Matches(text))
        {
            losses.Add(new ConversionLoss(
                "macro",
                $"The '{match.Groups["name"].Value}' macro has no markdown equivalent and its behaviour is "
                + "not carried."));
        }

        foreach (Match match in Attachment.Matches(text))
        {
            losses.Add(new ConversionLoss(
                "attachment",
                $"'{match.Groups["name"].Value}' is a page attachment; the file itself does not travel with "
                + "the text."));
        }

        if (Layout.IsMatch(text))
        {
            losses.Add(new ConversionLoss(
                "layout", "The page is laid out in columns, and markdown has no way to say so."));
        }

        var markdown = text;

        // Code comes out FIRST and is held aside. Its contents are not markup and must not be treated as
        // any: "1 < 2 && 3 > 2" inside a code block looks exactly like a tag to the sweep at the end of
        // this method, and a converter that ate it would have destroyed the one part of a page nobody can
        // reconstruct by reading it. Caught by a test that put that expression in a fence.
        var blocks = new List<string>();
        markdown = Replace(markdown, @"<ac:structured-macro[^>]*ac:name=""code""[^>]*>(?<b>.*?)</ac:structured-macro>",
            m =>
            {
                blocks.Add(CodeBody(m.Groups["b"].Value).Trim('\n'));
                return Placeholder(blocks.Count - 1);
            });

        // An element the site's editor saved EMPTY and self-closed ("<p local-id=... />") carries nothing,
        // and left alone it would be read as an opening tag hunting for a closer further down the page.
        markdown = Replace(markdown, @"<(p|li|h[1-6])" + Attributes + @"\s*/>", _ => string.Empty);

        markdown = Replace(markdown, @"<h([1-6])" + Open + @"(?<t>.*?)</h\1>", m =>
            new string('#', int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            + " " + Strip(m.Groups["t"].Value) + "\n\n");
        markdown = Replace(markdown, @"<(strong|b)" + Open + @"(?<t>.*?)</\1>", m => "**" + Strip(m.Groups["t"].Value) + "**");
        markdown = Replace(markdown, @"<(em|i)" + Open + @"(?<t>.*?)</\1>", m => "*" + Strip(m.Groups["t"].Value) + "*");
        markdown = Replace(markdown, @"<code" + Open + @"(?<t>.*?)</code>", m => "`" + Strip(m.Groups["t"].Value) + "`");
        markdown = Replace(markdown, @"<a[^>]*href=""(?<h>[^""]*)""[^>]*>(?<t>.*?)</a>", m =>
        {
            var href = m.Groups["h"].Value;
            var label = Strip(m.Groups["t"].Value);
            if (IsSafeLink(href))
            {
                return "[" + label + "](" + href + ")";
            }

            // The markdown this produces is rendered in the studio's own page, so a scheme that runs code
            // would be an injection from a page somebody else wrote. The text stays and the loss says why.
            losses.Add(new ConversionLoss(
                "unsafe link",
                $"A link to '{Unescape(href)}' was read as plain text: only http, https, mailto and "
                + "relative links are carried out of a page."));

            return label + " (" + Unescape(href) + ")";
        });
        // Tables, numbered lists, quotes and rules are what ToStorage writes since 2026-09-23, so a page published
        // from here comes back with them rather than flattened and reported.
        markdown = Replace(markdown, @"<table" + Open + @"(?<t>.*?)</table>", m => TableToMarkdown(m.Groups["t"].Value));
        markdown = Replace(markdown, @"<ol" + Open + @"(?<t>.*?)</ol>", m =>
            Replace(m.Groups["t"].Value, @"<li" + Open + @"(?<t>.*?)</li>", li => "1. " + Strip(li.Groups["t"].Value) + "\n") + "\n");
        markdown = Replace(markdown, @"<blockquote" + Open + @"(?<t>.*?)</blockquote>", m =>
        {
            var inner = Replace(m.Groups["t"].Value, @"<p" + Open + @"(?<t>.*?)</p>", p => Strip(p.Groups["t"].Value) + "\n\n");
            return string.Join('\n', Strip(inner).Trim().Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line)) + "\n\n";
        });
        markdown = Replace(markdown, @"<hr" + Attributes + @"\s*/?>", _ => "---\n\n");
        markdown = Replace(markdown, @"<br" + Attributes + @"\s*/?>", _ => "  \n");
        markdown = Replace(markdown, @"<li" + Open + @"(?<t>.*?)</li>", m => "- " + Strip(m.Groups["t"].Value) + "\n");
        markdown = Replace(markdown, @"<p" + Open + @"(?<t>.*?)</p>", m => Strip(m.Groups["t"].Value) + "\n\n");

        // A list WRAPPER carries nothing markdown lacks: the items already became lines. It is removed
        // without a word, because reporting it would teach a reader to ignore the report.
        markdown = Replace(markdown, @"</?(ul|ol)" + Attributes + ">", _ => "\n");

        // Whatever tags are left were not understood. They are removed so the text reads, and each kind is
        // reported once, because a document with forty table cells does not need forty lines about them.
        foreach (var name in Remaining(markdown))
        {
            losses.Add(new ConversionLoss("markup", $"A '<{name}>' element was removed; markdown has no equal."));
        }

        markdown = Tag.Replace(markdown, string.Empty);
        markdown = Unescape(markdown);

        // And the code goes back in, untouched by any of that.
        for (var i = 0; i < blocks.Count; i++)
        {
            markdown = markdown.Replace(
                Placeholder(i), "```\n" + blocks[i] + "\n```\n", StringComparison.Ordinal);
        }

        markdown = Regex.Replace(markdown, @"\n{3,}", "\n\n", RegexOptions.None, Patience);
        return new ConversionResult(markdown.Trim() + "\n", losses);
    }

    /// <summary>A storage-format table body as a pipe table. The first row is the header, as markdown requires.</summary>
    private static string TableToMarkdown(string body)
    {
        var rows = new List<List<string>>();
        foreach (Match row in Regex.Matches(body, @"<tr" + Open + @"(?<r>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase, Patience))
        {
            rows.Add([.. Regex.Matches(row.Groups["r"].Value, @"<(th|td)" + Attributes + @"(?:/>|>(?<c>.*?)</\1>)",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase, Patience)
                .Select(cell => Regex.Replace(Strip(cell.Groups["c"].Value), @"\s+", " ", RegexOptions.None, Patience).Trim()
                    .Replace("|", "\\|", StringComparison.Ordinal))]);
        }

        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var width = rows.Max(r => r.Count);
        var text = new StringBuilder("\n");
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].Concat(Enumerable.Repeat(string.Empty, width - rows[i].Count));
            text.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (i == 0)
            {
                text.Append('|').Append(string.Concat(Enumerable.Repeat("---|", width))).Append('\n');
            }
        }

        return text.Append('\n').ToString();
    }

    /// <summary>
    /// Markdown as Confluence storage format. The losses here are the other direction's: markdown says
    /// things about a document that the storage format has no single element for.
    /// <para>
    /// It reads BLOCKS, not lines. Until 2026-09-23 it read lines, and a paragraph wrapped at 120 columns in its
    /// source became one Confluence paragraph per source line, and every table became a run of paragraphs made
    /// of pipes (seen on the first page of this repository's own imported documents). A paragraph is every line
    /// up to a blank one or the start of another block, joined; a table, a list (ordered, bulleted, nested), a
    /// quote and a rule each become the element Confluence has for them.
    /// </para>
    /// </summary>
    public static ConversionResult ToStorage(string? markdown)
    {
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var losses = new List<ConversionLoss>();
        var storage = new StringBuilder();
        Blocks(lines, storage, losses);
        return new ConversionResult(storage.ToString(), Distinct(losses));
    }

    private static void Blocks(string[] lines, StringBuilder storage, List<ConversionLoss> losses)
    {
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd();
            if (line.Trim().Length == 0)
            {
                i++;
            }
            else if (Fence(line) is { } fence)
            {
                i = CodeBlock(lines, i, fence, storage, losses);
            }
            else if (Heading(line) is { } heading)
            {
                storage.Append("<h").Append(heading.Level).Append('>')
                    .Append(Inline(heading.Text, losses)).Append("</h").Append(heading.Level).Append('>');
                i++;
            }
            else if (IsRule(line))
            {
                storage.Append("<hr />");
                i++;
            }
            else if (IsTableStart(lines, i))
            {
                i = Table(lines, i, storage, losses);
            }
            else if (ListItem(line) is not null)
            {
                i = List(lines, i, storage, losses);
            }
            else if (line.TrimStart().StartsWith('>'))
            {
                i = Quote(lines, i, storage, losses);
            }
            else
            {
                i = Paragraph(lines, i, storage, losses);
            }
        }
    }

    /// <summary>The fence a line opens (its character and length), or null.</summary>
    private static (char Mark, int Length, string Info)? Fence(string line)
    {
        var trimmed = line.TrimStart();
        if (line.Length - trimmed.Length > 3 || trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~'))
        {
            return null;
        }

        var length = 0;
        while (length < trimmed.Length && trimmed[length] == trimmed[0])
        {
            length++;
        }

        return length >= 3 ? (trimmed[0], length, trimmed[length..].Trim()) : null;
    }

    private static int CodeBlock(
        string[] lines, int start, (char Mark, int Length, string Info) fence, StringBuilder storage, List<ConversionLoss> losses)
    {
        if (fence.Info.Length > 0)
        {
            // The language is carried as a parameter by Confluence, and this converter does not write
            // parameters, so it is stated as a loss rather than quietly dropped.
            losses.Add(new ConversionLoss(
                "code language", $"The code block's language '{fence.Info}' is not carried into the page."));
        }

        var body = new StringBuilder();
        var i = start + 1;
        var closed = false;
        for (; i < lines.Length; i++)
        {
            if (Fence(lines[i].TrimEnd()) is { } close && close.Mark == fence.Mark && close.Length >= fence.Length
                && close.Info.Length == 0)
            {
                closed = true;
                i++;
                break;
            }

            body.Append(lines[i].TrimEnd()).Append('\n');
        }

        if (!closed)
        {
            losses.Add(new ConversionLoss(
                "code block", "A code block was never closed, so the page ends inside it."));
        }

        // A CDATA section cannot hold its own terminator, so one inside the code is split across two sections.
        storage.Append("<ac:structured-macro ac:name=\"code\"><ac:plain-text-body><![CDATA[")
            .Append(body.ToString().Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal))
            .Append("]]></ac:plain-text-body></ac:structured-macro>");
        return i;
    }

    private static bool IsRule(string line) =>
        Regex.IsMatch(line, @"^ {0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.None, Patience);

    private static readonly Regex TableSeparator = new(
        @"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.None, Patience);

    /// <summary>A table starts at a row with a pipe followed by a separator row, which is what makes it one.</summary>
    private static bool IsTableStart(string[] lines, int i) =>
        i + 1 < lines.Length && lines[i].Contains('|', StringComparison.Ordinal)
        && lines[i + 1].Contains('|', StringComparison.Ordinal) && TableSeparator.IsMatch(lines[i + 1]);

    private static int Table(string[] lines, int start, StringBuilder storage, List<ConversionLoss> losses)
    {
        if (lines[start + 1].Contains(':', StringComparison.Ordinal))
        {
            losses.Add(new ConversionLoss(
                "table alignment", "A table's column alignment is not carried; its cells are."));
        }

        storage.Append("<table><tbody><tr>");
        foreach (var cell in Cells(lines[start]))
        {
            storage.Append("<th>").Append(Inline(cell, losses)).Append("</th>");
        }

        storage.Append("</tr>");
        var i = start + 2;
        for (; i < lines.Length && lines[i].Trim().Length > 0 && lines[i].Contains('|', StringComparison.Ordinal); i++)
        {
            storage.Append("<tr>");
            foreach (var cell in Cells(lines[i]))
            {
                storage.Append("<td>").Append(Inline(cell, losses)).Append("</td>");
            }

            storage.Append("</tr>");
        }

        storage.Append("</tbody></table>");
        return i;
    }

    /// <summary>A row's cells: split on the pipes that are not escaped and not inside a code span.</summary>
    private static List<string> Cells(string row)
    {
        var text = row.Trim();
        if (text.StartsWith('|'))
        {
            text = text[1..];
        }

        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal))
        {
            text = text[..^1];
        }

        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (c == '`')
            {
                inCode = !inCode;
                cell.Append(c);
            }
            else if (c == '|' && !inCode)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }

    /// <summary>A list item's indent, whether it is numbered, and its text; null when the line is not one.</summary>
    private static (int Indent, bool Ordered, string Text)? ListItem(string line)
    {
        var match = Regex.Match(line, @"^(?<i>\s*)(?<m>[-*+]|\d{1,9}[.)])\s+(?<t>.*)$", RegexOptions.None, Patience);
        return match.Success
            ? (match.Groups["i"].Value.Replace("\t", "    ", StringComparison.Ordinal).Length,
                char.IsDigit(match.Groups["m"].Value[0]), match.Groups["t"].Value)
            : null;
    }

    private static int List(string[] lines, int start, StringBuilder storage, List<ConversionLoss> losses)
    {
        // One entry per open list: its indent, its tag, and whether an item in it is still open (a nested list
        // goes INSIDE that item, which is what Confluence and every markdown reader mean by nesting).
        var open = new Stack<(int Indent, string Tag, bool ItemOpen)>();
        var i = start;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd();
            if (line.Trim().Length == 0)
            {
                // A blank line ends the list unless the next text is another item or an indented continuation.
                var next = i + 1;
                while (next < lines.Length && lines[next].Trim().Length == 0)
                {
                    next++;
                }

                if (next < lines.Length && (ListItem(lines[next]) is not null || Indent(lines[next]) >= 2)
                    && !IsRule(lines[next]))
                {
                    i = next;
                    continue;
                }

                break;
            }

            if (ListItem(line) is { } item && !IsRule(line))
            {
                var tag = item.Ordered ? "ol" : "ul";
                while (open.Count > 0 && item.Indent < open.Peek().Indent)
                {
                    Close(open, storage);
                }

                if (open.Count > 0 && item.Indent == open.Peek().Indent)
                {
                    var same = open.Pop();
                    if (same.ItemOpen)
                    {
                        storage.Append("</li>");
                    }

                    if (same.Tag != tag)
                    {
                        storage.Append("</").Append(same.Tag).Append('>').Append('<').Append(tag).Append('>');
                    }

                    open.Push((same.Indent, tag, true));
                }
                else
                {
                    storage.Append('<').Append(tag).Append('>');
                    open.Push((item.Indent, tag, true));
                }

                storage.Append("<li>").Append(Inline(item.Text, losses));
                i++;
                continue;
            }

            if (Indent(line) >= 2 || !StartsBlock(lines, i))
            {
                // A continuation of the open item: its wrapped text, joined as the paragraph it is.
                storage.Append(' ').Append(Inline(line.Trim(), losses));
                i++;
                continue;
            }

            break;
        }

        while (open.Count > 0)
        {
            Close(open, storage);
        }

        return i;
    }

    private static void Close(Stack<(int Indent, string Tag, bool ItemOpen)> open, StringBuilder storage)
    {
        var closing = open.Pop();
        if (closing.ItemOpen)
        {
            storage.Append("</li>");
        }

        storage.Append("</").Append(closing.Tag).Append('>');
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static int Quote(string[] lines, int start, StringBuilder storage, List<ConversionLoss> losses)
    {
        var inner = new List<string>();
        var i = start;
        for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
        {
            var text = lines[i].TrimStart()[1..];
            inner.Add(text.StartsWith(' ') ? text[1..] : text);
        }

        storage.Append("<blockquote>");
        Blocks([.. inner], storage, losses);
        storage.Append("</blockquote>");
        return i;
    }

    /// <summary>Whether the line at <paramref name="i"/> starts a block, so it ends the paragraph before it.</summary>
    private static bool StartsBlock(string[] lines, int i)
    {
        var line = lines[i].TrimEnd();
        return Fence(line) is not null || Heading(line) is not null || IsRule(line) || IsTableStart(lines, i)
            || line.TrimStart().StartsWith('>')
            // A numbered item interrupts a paragraph only when it starts at 1, as in every markdown reader, so
            // prose that wraps onto "2026. The year" stays prose.
            || (ListItem(line) is { } item && (!item.Ordered || Regex.IsMatch(line, @"^\s*1[.)]\s", RegexOptions.None, Patience)));
    }

    private static int Paragraph(string[] lines, int start, StringBuilder storage, List<ConversionLoss> losses)
    {
        var text = new StringBuilder();
        var i = start;
        for (; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (raw.Trim().Length == 0 || (i > start && StartsBlock(lines, i)))
            {
                break;
            }

            // A setext underline makes the paragraph above it a heading.
            if (i > start && Regex.IsMatch(raw, @"^ {0,3}(=+|-+)\s*$", RegexOptions.None, Patience))
            {
                var level = raw.Trim()[0] == '=' ? 1 : 2;
                storage.Append("<h").Append(level).Append('>').Append(Inline(text.ToString(), losses))
                    .Append("</h").Append(level).Append('>');
                return i + 1;
            }

            // Two trailing spaces or a trailing backslash is a hard break; any other line end is a space.
            var hard = raw.EndsWith("  ", StringComparison.Ordinal) || raw.TrimEnd().EndsWith('\\');
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(raw.Trim().TrimEnd('\\'));
            if (hard)
            {
                text.Append('\u0003');
            }
        }

        storage.Append("<p>")
            .Append(Inline(text.ToString().TrimEnd('\u0003', ' '), losses).Replace("\u0003", "<br />", StringComparison.Ordinal))
            .Append("</p>");
        return i;
    }

    /// <summary>Each loss once: a document with forty tables does not need forty lines about their alignment.</summary>
    private static List<ConversionLoss> Distinct(List<ConversionLoss> losses) => [.. losses.Distinct()];

    private static (int Level, string Text)? Heading(string line)
    {
        var trimmed = line.TrimStart();
        if (line.Length - trimmed.Length > 3)
        {
            return null;
        }

        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
        {
            hashes++;
        }

        return hashes is > 0 and <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' '
            ? (hashes, trimmed[(hashes + 1)..].TrimEnd('#', ' '))
            : null;
    }

    /// <summary>Inline markdown as storage-format markup, and everything else escaped.</summary>
    private static string Inline(string text, List<ConversionLoss> losses)
    {
        // Code spans come out first, so what is inside one is never read as emphasis or a link.
        var spans = new List<string>();
        text = Regex.Replace(text, @"(?<f>`+)(?<t>.+?)\k<f>", m =>
        {
            spans.Add(m.Groups["t"].Value.Trim());
            return "\u0002" + (spans.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u0002";
        }, RegexOptions.None, Patience);

        var escaped = Escape(text);
        escaped = Replace(escaped, @"!\[(?<t>[^\]]*)\]\((?<h>[^)]*)\)", m =>
        {
            losses.Add(new ConversionLoss(
                "image", "An image is written as a link to it; the picture itself does not travel with the text."));
            return "[" + (m.Groups["t"].Value.Length > 0 ? m.Groups["t"].Value : m.Groups["h"].Value) + "](" + m.Groups["h"].Value + ")";
        });
        escaped = Replace(escaped, @"\*\*(?<t>[^*]+)\*\*", m => "<strong>" + m.Groups["t"].Value + "</strong>");
        escaped = Replace(escaped, @"(?<!\*)\*(?<t>[^*]+)\*(?!\*)", m => "<em>" + m.Groups["t"].Value + "</em>");
        escaped = Replace(escaped, @"`(?<t>[^`]+)`", m => "<code>" + m.Groups["t"].Value + "</code>");
        escaped = Replace(escaped, @"\[(?<t>[^\]]*)\]\((?<h>[^)]*)\)", m =>
        {
            var href = m.Groups["h"].Value;
            if (IsSafeLink(href))
            {
                return "<a href=\"" + href + "\">" + m.Groups["t"].Value + "</a>";
            }

            // Not dropped and not obeyed: the text stays so the reader can see what was written, and the
            // loss says why it is no longer a link. A document can be authored by anybody or synced from a
            // provider, so a link is untrusted text and a scheme that runs code is not carried into a page.
            losses.Add(new ConversionLoss(
                "unsafe link",
                $"A link to '{Unescape(href)}' was written as plain text: only http, https, mailto and "
                + "relative links are carried into a page."));

            return "[" + m.Groups["t"].Value + "](" + href + ")";
        });

        return Regex.Replace(escaped, "\u0002(?<n>\\d+)\u0002", m =>
            "<code>" + Escape(spans[int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)]) + "</code>",
            RegexOptions.None, Patience);
    }

    /// <summary>
    /// Whether a link destination may be carried as a LINK. An allowlist, not a list of bad schemes: the
    /// set of things a browser will execute is not knowable in advance, and a relative or absolute path is
    /// what a document almost always means anyway.
    /// </summary>
    private static bool IsSafeLink(string href)
    {
        var target = Unescape(href).Trim();
        if (target.Length == 0)
        {
            return false;
        }

        var colon = target.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            // No scheme at all: a relative path, a fragment or a query. Nothing to execute.
            return true;
        }

        // A colon that comes after a slash belongs to the path, not to a scheme ("a/b:c").
        var slash = target.IndexOfAny(['/', '?', '#']);
        if (slash >= 0 && slash < colon)
        {
            return true;
        }

        var scheme = target[..colon];
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Text as storage-format character data. The QUOTE is escaped along with the angle brackets and the
    /// ampersand, and that is not tidiness: every value this produces can end up inside a quoted attribute,
    /// and a link destination carrying one closed the attribute and opened an event handler on the page.
    /// </summary>
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string Unescape(string text) =>
        text.Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);

    private static string Strip(string text) => Tag.Replace(text, string.Empty);

    /// <summary>
    /// A stand-in for a code block while the rest of the document is converted. It uses characters that
    /// cannot appear in storage-format markup, so nothing else in the document can collide with it.
    /// </summary>
    private static string Placeholder(int index) =>
        "\u0001fravashi-code-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u0001";

    /// <summary>
    /// The code inside a macro body: EVERY CDATA section, joined. <see cref="ToStorage"/> splits a <c>]]&gt;</c> in the
    /// code across two sections, and until backlog 193 only the first was read back, so such code came home cut short.
    /// </summary>
    private static string CodeBody(string body)
    {
        var start = body.IndexOf("<![CDATA[", StringComparison.Ordinal);
        if (start < 0)
        {
            return body;
        }

        var code = new StringBuilder();
        while (start >= 0)
        {
            var end = body.IndexOf("]]>", start + 9, StringComparison.Ordinal);
            if (end < 0)
            {
                code.Append(body, start + 9, body.Length - start - 9);
                break;
            }

            code.Append(body, start + 9, end - start - 9);
            start = body.IndexOf("<![CDATA[", end + 3, StringComparison.Ordinal);
        }

        return code.ToString();
    }

    /// <summary>The distinct element names still in the text, so each kind is reported once.</summary>
    private static IReadOnlyList<string> Remaining(string text)
    {
        var names = new List<string>();
        foreach (Match match in Tag.Matches(text))
        {
            var name = match.Value.Trim('<', '>', '/').Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (name is { Length: > 0 } && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string Replace(string text, string pattern, MatchEvaluator evaluator) =>
        Regex.Replace(text, pattern, evaluator, RegexOptions.Singleline | RegexOptions.IgnoreCase, Patience);
}
