using System.Globalization;
using System.Text;

namespace Fravashi.Application.Verification;

/// <summary>
/// Reads the text of a Playwright aria snapshot into <see cref="ObservedNode"/>s (backlog 222, plan @46), without a YAML
/// library: the generator is fixed and writes a small subset. Each line is a list item, <c>- key</c>, <c>- key:</c> with
/// its children indented under it, or <c>- key: value</c>. A key is <c>role "name" [attr] [attr=value]</c>, wrapped in
/// single quotes (a quote doubled) when it holds YAML-special characters; the name is a JSON string; a value is plain, a
/// JSON string or single-quoted. Property lines (<c>- /url: ...</c>) are dropped, because nothing matches on them.
/// Anything else is refused with its line number rather than guessed at.
/// </summary>
public static class AriaSnapshot
{
    /// <summary>The snapshot's top-level nodes.</summary>
    /// <exception cref="FormatException">A line is not one the generator writes.</exception>
    public static IReadOnlyList<ObservedNode> Parse(string text) => Parse(text, 1);

    /// <summary>As <see cref="Parse(string)"/>, numbering lines from <paramref name="firstLine"/> in errors.</summary>
    internal static IReadOnlyList<ObservedNode> Parse(string text, int firstLine)
    {
        ArgumentNullException.ThrowIfNull(text);

        var top = new List<Builder>();
        var frames = new Stack<Frame>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var number = firstLine + i;
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            if (line[indent] == '\t')
            {
                throw Error(number, "a tab in the indentation");
            }

            if (!line.AsSpan(indent).StartsWith("- ", StringComparison.Ordinal))
            {
                throw Error(number, "a line that is not a list item");
            }

            if (frames.Count == 0)
            {
                frames.Push(new Frame(indent, null, top));
            }

            while (frames.Count > 1 && indent < frames.Peek().Indent)
            {
                frames.Pop();
            }

            var frame = frames.Peek();
            if (indent < frame.Indent)
            {
                throw Error(number, "a line indented less than the first");
            }

            if (indent > frame.Indent)
            {
                var owner = frame.Items.Count > 0 ? frame.Items[^1] : null;
                if (owner is null || !owner.Opens)
                {
                    throw Error(number, "a line indented under an item that has no children");
                }

                frame = new Frame(indent, owner, owner.Children);
                frames.Push(frame);
            }

            var content = line[(indent + 2)..];
            if (content.StartsWith('/'))
            {
                // A property of the item above (its url, placeholder), not an element: kept out of the tree, and it
                // can still own nested lines, which go with it.
                frame.Items.Add(new Builder("/", null, [], content.TrimEnd().EndsWith(':')) { Property = true });
                continue;
            }

            frame.Items.Add(Item(content, number));
        }

        return top.Where(b => !b.Property).Select(b => b.Build()).ToArray();
    }

    private static Builder Item(string content, int number)
    {
        var reader = new Reader(content, number);
        Builder item;
        if (reader.Peek == '\'')
        {
            var key = reader.SingleQuoted();
            var inner = new Reader(key, number);
            item = Key(inner);
            if (!inner.AtEnd)
            {
                throw Error(number, "text after the attributes of a quoted key");
            }
        }
        else
        {
            item = Key(reader);
        }

        if (reader.AtEnd)
        {
            return item;
        }

        reader.Expect(':');
        if (reader.AtEnd)
        {
            item.Opens = true;
            return item;
        }

        reader.Expect(' ');
        item.Text = reader.Peek switch
        {
            '"' => reader.JsonString(),
            '\'' => reader.SingleQuoted(),
            _ => reader.Rest().TrimEnd(),
        };

        if (!reader.AtEnd)
        {
            throw Error(number, "text after a quoted value");
        }

        return item;
    }

    private static Builder Key(Reader reader)
    {
        var role = reader.Word();
        if (role.Length == 0)
        {
            throw reader.Error("an item with no role");
        }

        string? name = null;
        if (reader.Peek == ' ' && reader.PeekAt(1) == '"')
        {
            reader.Expect(' ');
            name = reader.JsonString();
        }

        var attributes = new List<KeyValuePair<string, string?>>();
        while (reader.Peek == ' ' && reader.PeekAt(1) == '[')
        {
            reader.Expect(' ');
            reader.Expect('[');
            var attribute = reader.Until("=]");
            string? value = null;
            if (reader.Peek == '=')
            {
                reader.Expect('=');
                value = reader.Until("]");
            }

            reader.Expect(']');
            attributes.Add(new(attribute, value));
        }

        return new Builder(role, name, attributes, false);
    }

    private static FormatException Error(int line, string what) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Aria snapshot line {line}: {what}."));

    private sealed record Frame(int Indent, Builder? Owner, List<Builder> Items);

    private sealed class Builder(string role, string? name, List<KeyValuePair<string, string?>> attributes, bool opens)
    {
        public bool Opens { get; set; } = opens;
        public bool Property { get; init; }
        public string? Text { get; set; }
        public List<Builder> Children { get; } = [];

        public ObservedNode Build()
        {
            var map = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (key, value) in attributes)
            {
                map[key] = value;
            }

            return new ObservedNode(
                role, name, Text, map, Children.Where(c => !c.Property).Select(c => c.Build()).ToArray());
        }
    }

    /// <summary>A cursor over one line's content.</summary>
    private sealed class Reader(string text, int line)
    {
        private int at;

        public bool AtEnd => at >= text.Length;

        public char Peek => PeekAt(0);

        public char PeekAt(int offset) => at + offset < text.Length ? text[at + offset] : '\0';

        public FormatException Error(string what) => AriaSnapshot.Error(line, what);

        public void Expect(char c)
        {
            if (Peek != c)
            {
                throw Error(string.Create(CultureInfo.InvariantCulture, $"expected '{c}' at column {at + 1}"));
            }

            at++;
        }

        public string Word()
        {
            var start = at;
            while (char.IsAsciiLetter(Peek))
            {
                at++;
            }

            return text[start..at];
        }

        public string Until(string stops)
        {
            var start = at;
            while (!AtEnd && !stops.Contains(Peek, StringComparison.Ordinal))
            {
                at++;
            }

            return text[start..at];
        }

        public string Rest()
        {
            var rest = text[at..];
            at = text.Length;
            return rest;
        }

        public string SingleQuoted()
        {
            Expect('\'');
            var value = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("an unterminated single-quoted string");
                }

                var c = text[at++];
                if (c == '\'')
                {
                    if (Peek != '\'')
                    {
                        return value.ToString();
                    }

                    at++;
                }

                value.Append(c);
            }
        }

        public string JsonString()
        {
            Expect('"');
            var value = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("an unterminated string");
                }

                var c = text[at++];
                if (c == '"')
                {
                    return value.ToString();
                }

                if (c != '\\')
                {
                    value.Append(c);
                    continue;
                }

                var escaped = AtEnd ? '\0' : text[at++];
                switch (escaped)
                {
                    case '"' or '\\' or '/':
                        value.Append(escaped);
                        break;
                    case 'b':
                        value.Append('\b');
                        break;
                    case 'f':
                        value.Append('\f');
                        break;
                    case 'n':
                        value.Append('\n');
                        break;
                    case 'r':
                        value.Append('\r');
                        break;
                    case 't':
                        value.Append('\t');
                        break;
                    case 'u' when at + 4 <= text.Length
                        && int.TryParse(text.AsSpan(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code):
                        value.Append((char)code);
                        at += 4;
                        break;
                    default:
                        throw Error("an escape JSON does not have");
                }
            }
        }
    }
}
