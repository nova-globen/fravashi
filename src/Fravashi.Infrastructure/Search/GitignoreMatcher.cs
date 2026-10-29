using System.Text.RegularExpressions;

namespace Fravashi.Infrastructure.Search;

/// <summary>
/// A practical subset of gitignore matching for the managed search fallback and the include/exclude
/// glob fields (ADR 0015): <c>#</c> comments, <c>!</c> negation, trailing <c>/</c> directory-only
/// patterns, leading <c>/</c> root anchoring, <c>**</c>, <c>*</c>, <c>?</c>, and character classes. Last matching
/// pattern wins, like git. Paths are repository-relative, forward-slash normalized.
/// </summary>
public sealed class GitignoreMatcher
{
    private readonly List<(Regex Pattern, bool Negated, bool DirectoryOnly, string Text)> _rules = [];

    public GitignoreMatcher(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var negated = line.StartsWith('!');
            if (negated)
            {
                line = line[1..].Trim();
            }

            var directoryOnly = line.EndsWith('/');
            line = line.Trim('/');
            if (line.Length == 0)
            {
                continue;
            }

            // A pattern without a slash matches at any depth (gitignore semantics).
            var anchored = raw.TrimStart('!').TrimStart().StartsWith('/') || line.Contains('/', StringComparison.Ordinal);
            var regex = GlobToRegex(line, anchored);
            _rules.Add((regex, negated, directoryOnly, raw.Trim()));
        }
    }

    public static GitignoreMatcher FromRuleText(params string?[] ruleTexts) =>
        new(ruleTexts.Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => t!.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')));

    /// <summary>Match a VS-Code-style comma-separated glob list ("src/**/*.cs, *.md"); null when empty.</summary>
    public static GitignoreMatcher? FromGlobList(string? globs)
    {
        if (string.IsNullOrWhiteSpace(globs))
        {
            return null;
        }

        var parts = globs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : new GitignoreMatcher(parts);
    }

    /// <summary>True when the path is excluded by the rules (negations honored, last match wins).</summary>
    public bool IsIgnored(string relativePath, bool isDirectory) => IgnoringRule(relativePath, isDirectory) is not null;

    /// <summary>
    /// The rule that excludes the path, as it was written, or null when the path is not excluded (backlog 253: a path
    /// withheld from the agent's copy is logged with the rule that withheld it).
    /// </summary>
    public string? IgnoringRule(string relativePath, bool isDirectory)
    {
        string? ignoring = null;
        foreach (var (pattern, negated, directoryOnly, text) in _rules)
        {
            if (directoryOnly && !isDirectory)
            {
                // A "dir/" pattern also ignores everything under it; test the path's parents.
                if (!ParentMatches(pattern, relativePath))
                {
                    continue;
                }
            }
            else if (!pattern.IsMatch(relativePath))
            {
                continue;
            }

            ignoring = negated ? null : text;
        }

        return ignoring;
    }

    /// <summary>True when any rule (ignoring negations) matches: the include-list interpretation.</summary>
    public bool IsMatch(string relativePath)
    {
        foreach (var (pattern, negated, directoryOnly, _) in _rules)
        {
            _ = directoryOnly;
            if (!negated && (pattern.IsMatch(relativePath) || ParentMatches(pattern, relativePath)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ParentMatches(Regex pattern, string relativePath)
    {
        var index = -1;
        while ((index = relativePath.IndexOf('/', index + 1)) >= 0)
        {
            if (pattern.IsMatch(relativePath[..index]))
            {
                return true;
            }
        }

        return pattern.IsMatch(relativePath);
    }

    private static Regex GlobToRegex(string glob, bool anchored)
    {
        var sb = new System.Text.StringBuilder("^");
        if (!anchored)
        {
            sb.Append("(?:.*/)?");
        }

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    // "**" crosses directory boundaries ("**/x", "x/**", "a/**/b").
                    sb.Append(".*");
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else if (c == '[' && ClassEnd(glob, i) is var end and > 0)
            {
                sb.Append(CharacterClass(glob[(i + 1)..end]));
                i = end;
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Where the character class opened at <paramref name="start"/> closes, or -1 when it never does (then the
    /// <c>[</c> is an ordinary character, as git reads it). A <c>]</c> right after the opening, or after its
    /// negation, is a member rather than the end, which is how <c>[]]</c> names a closing bracket.
    /// </summary>
    private static int ClassEnd(string glob, int start)
    {
        var i = start + 1;
        if (i < glob.Length && glob[i] is '!' or '^')
        {
            i++;
        }

        if (i < glob.Length && glob[i] == ']')
        {
            i++;
        }

        return glob.IndexOf(']', i);
    }

    /// <summary>
    /// A glob class (<c>Dd</c>, <c>0-9</c>, <c>!0-9</c>) as a regular expression class. Added 2026-09-29: the Visual
    /// Studio template ignores build output with <c>[Dd]ebug/</c> and <c>[Rr]elease/</c>, and read as literal text
    /// those ignored nothing. A class never matches a slash, as in git, which matches path components separately.
    /// </summary>
    private static string CharacterClass(string members)
    {
        var negated = members.Length > 0 && members[0] is '!' or '^';
        if (negated)
        {
            members = members[1..];
        }

        // Inside a regular expression class only these four mean anything beyond themselves; a range's hyphen is
        // kept as a range on purpose.
        var body = members
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("^", "\\^", StringComparison.Ordinal);
        return negated ? "[^/" + body + "]" : "(?:(?!/)[" + body + "])";
    }
}
