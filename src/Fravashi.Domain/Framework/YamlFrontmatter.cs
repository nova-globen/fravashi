using System.Text;

namespace Fravashi.Domain.Framework;

/// <summary>
/// A YAML value in the frontmatter contract: either a scalar string or a list of scalar strings. Kept minimal -
/// the SKILL.md frontmatter only needs these two shapes.
/// </summary>
public abstract record YamlValue
{
    private YamlValue() { }

    /// <summary>A single string value.</summary>
    public sealed record Scalar(string Text) : YamlValue;

    /// <summary>A list of string values (rendered as a block sequence).</summary>
    public sealed record List(IReadOnlyList<string> Items) : YamlValue;
}

/// <summary>
/// Serializes YAML frontmatter under the locked contract (ADR 0005 / D03-6): <b>every string value is
/// double-quoted; every key is unquoted</b>. The output is byte-stable - quoting is unconditional, not
/// "only when needed" - so exported frontmatter is deterministic and safe for values containing colons,
/// <c>#</c>, quotes, leading spaces, or newlines. Double-quoted YAML scalars are used because they have a
/// well-defined escape set (<c>\\ \" \n \t \r</c>); we escape exactly those.
/// </summary>
public static class YamlFrontmatter
{
    /// <summary>
    /// Quotes a string as a YAML double-quoted scalar per the contract: wrap in <c>"</c>, escaping backslash,
    /// double-quote, and the CR/LF/TAB control characters. All other characters (colons, <c>#</c>, unicode)
    /// are safe inside double quotes and pass through unchanged.
    /// </summary>
    public static string QuoteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Renders an ordered set of key/value pairs as a YAML frontmatter block - WITHOUT the surrounding
    /// <c>---</c> fences (the caller composes the document). Keys are emitted unquoted; scalar values and every
    /// list item are double-quoted. A trailing newline follows each entry.
    /// </summary>
    public static string Serialize(IReadOnlyList<KeyValuePair<string, YamlValue>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sb = new StringBuilder();
        foreach (var (key, value) in entries)
        {
            switch (value)
            {
                case YamlValue.Scalar scalar:
                    sb.Append(key).Append(": ").Append(QuoteString(scalar.Text)).Append('\n');
                    break;
                case YamlValue.List list:
                    sb.Append(key).Append(":\n");
                    foreach (var item in list.Items)
                    {
                        sb.Append("  - ").Append(QuoteString(item)).Append('\n');
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
