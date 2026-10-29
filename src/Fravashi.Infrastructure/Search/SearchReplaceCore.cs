using System.Text.RegularExpressions;
using Fravashi.Application.Search;

namespace Fravashi.Infrastructure.Search;

/// <summary>
/// The pure heart of search-replace, shared by the local engine and the bridge adapter (plan 2b-i)
/// so both modes apply IDENTICAL semantics: the same compiled pattern, the same literal-vs-regex
/// substitution escaping, and the same replace-exactly-at-position rule for single-occurrence
/// replaces.
/// </summary>
internal static class SearchReplaceCore
{
    public static Regex BuildRegex(SearchQuery query)
    {
        var pattern = query.IsRegex ? query.Pattern : Regex.Escape(query.Pattern);
        if (query.WholeWord)
        {
            pattern = $@"\b(?:{pattern})\b";
        }

        var options = RegexOptions.CultureInvariant;
        if (!query.CaseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(pattern, options, TimeSpan.FromSeconds(5));
    }

    /// <summary>Literal replacement text must not be interpreted as regex substitutions.</summary>
    public static string Substitution(SearchQuery query, string replacement) =>
        query.IsRegex ? replacement : replacement.Replace("$", "$$", StringComparison.Ordinal);

    /// <summary>Applies the replacement (everywhere, or only at the given 1-based position).</summary>
    public static string Apply(
        string text, Regex regex, string substitution, (int Line, int Column)? onlyAt, out int replaced)
    {
        if (onlyAt is { } at)
        {
            return ReplaceSingle(text, regex, substitution, at.Line, at.Column, out replaced);
        }

        var count = 0;
        var updated = regex.Replace(text, m =>
        {
            count++;
            return m.Result(substitution);
        });
        replaced = count;
        return updated;
    }

    /// <summary>Replace only the occurrence that starts at the given 1-based line/column.</summary>
    private static string ReplaceSingle(
        string text, Regex regex, string substitution, int line, int column, out int replaced)
    {
        replaced = 0;
        var offset = OffsetOf(text, line, column);
        if (offset < 0)
        {
            return text;
        }

        var match = regex.Match(text, Math.Max(offset - 1, 0));
        // The match must start exactly at the requested position (tolerating the -1 probe start).
        if (!match.Success || match.Index != offset)
        {
            match = regex.Match(text, offset);
            if (!match.Success || match.Index != offset)
            {
                return text;
            }
        }

        replaced = 1;
        return text[..match.Index] + match.Result(substitution) + text[(match.Index + match.Length)..];
    }

    private static int OffsetOf(string text, int line, int column)
    {
        var currentLine = 1;
        var lineStart = 0;
        while (currentLine < line)
        {
            var next = text.IndexOf('\n', lineStart);
            if (next < 0)
            {
                return -1;
            }

            lineStart = next + 1;
            currentLine++;
        }

        var offset = lineStart + column - 1;
        return offset <= text.Length ? offset : -1;
    }
}
