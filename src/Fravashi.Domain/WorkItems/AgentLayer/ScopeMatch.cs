using System.Text.RegularExpressions;

namespace Fravashi.Domain.WorkItems.AgentLayer;

/// <summary>
/// Whether a repository-relative path is inside a scope pattern (backlog 85). The semantics are the ones
/// this product already documents for search globs, so an author does not learn two glob languages:
/// <c>**</c> crosses separators, <c>*</c> does not, <c>?</c> is exactly one character, a pattern ending in
/// <c>/</c> matches a directory anywhere in the path, and a pattern with no separator matches the file NAME.
/// <para>
/// Matching is done on a normalised copy with forward separators, because a pattern written on one machine
/// is read on another, and a scope that depended on which slash the author typed would be a scope that
/// silently opened when the file arrived from the other platform.
/// </para>
/// </summary>
public static class ScopeMatch
{
    /// <summary>True when the path is inside the pattern.</summary>
    public static bool Matches(string? pattern, string? path)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var glob = Normalise(pattern);
        var target = Normalise(path);

        // A trailing slash names a directory: everything under it, wherever that directory sits.
        if (glob.EndsWith('/'))
        {
            var folder = glob.TrimEnd('/');
            return Segments(target).Any(s => Is(folder, s))
                && target.Length > folder.Length;
        }

        // No separator at all: the pattern is about the file's name, not its place.
        if (!glob.Contains('/', StringComparison.Ordinal))
        {
            var name = target[(target.LastIndexOf('/') + 1)..];
            return Is(glob, name);
        }

        return Is(glob, target);
    }

    /// <summary>
    /// The verdict for a path against a whole set of patterns. Nothing declared means nothing forbidden: an
    /// item that has not said where the work happens has not ruled anywhere out either. A forbidden pattern
    /// wins over an allowed one, because the narrower statement is the deliberate one.
    /// </summary>
    public static bool IsAllowed(IEnumerable<ScopePattern> patterns, string? path)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var all = patterns.ToList();
        if (all.Count == 0)
        {
            return true;
        }

        if (all.Any(p => p.Mode == ScopeMode.Forbidden && Matches(p.Pattern, path)))
        {
            return false;
        }

        var allowed = all.Where(p => p.Mode == ScopeMode.Allowed).ToList();
        return allowed.Count == 0 || allowed.Any(p => Matches(p.Pattern, path));
    }

    private static string Normalise(string text) => text.Replace('\\', '/').Trim();

    private static IEnumerable<string> Segments(string path) => path.Split('/');

    // Compiled once per pattern. The set is bounded by the patterns a project declares, and the timeout is
    // a backstop: the translation below produces no nested quantifier, but a regex engine given a pattern
    // nobody anticipated should give up rather than hold a gate open while it thinks.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);

    /// <summary>
    /// Glob matching by translation to a regular expression, which is the version of this that is easy to
    /// read and therefore easy to be sure of. The first attempt was a hand-rolled backtracking matcher and
    /// it got <c>src/**/*.cs</c> against <c>src/a/b/c.cs</c> wrong, which is exactly the case an author
    /// would write first.
    /// </summary>
    private static bool Is(string glob, string text) =>
        Compiled.GetOrAdd(glob, Translate).IsMatch(text);

    private static Regex Translate(string glob)
    {
        var pattern = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;

                    // "**/" also matches zero directories, so "src/**/x" matches "src/x".
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        pattern.Append("(?:.*/)?");
                    }
                    else
                    {
                        pattern.Append(".*");
                    }
                }
                else
                {
                    pattern.Append("[^/]*");
                }

                continue;
            }

            pattern.Append(c == '?' ? "[^/]" : Regex.Escape(c.ToString()));
        }

        return new Regex(
            pattern.Append('$').ToString(),
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
    }
}
