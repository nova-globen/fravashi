namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// A Bash command that only LOOKS, sent back to the tools that look without asking anyone. Found in both self-hosting
/// trials (2026-09-27 and 2026-09-29): an agent inside Fravashi read with <c>sed -n</c>, <c>grep</c> and <c>ls</c> through
/// Bash, and every one of those asks a person and waits for the work gate, while Read, Grep and Glob inside the copy
/// run unattended. The guidance said so and was not enough, so the studio answers such a command itself, with the
/// tool to use instead.
/// <para>
/// It only ever REFUSES, which is always safe, so it can afford to be simple: a command is sent back only when every
/// part of it is one of a short list of reading commands, with no redirection but a discard to exactly
/// <c>/dev/null</c> (<see cref="DevNullRedirects"/>, backlog 337: agents append one to a read constantly, and
/// without this exception a command as plain as <c>grep -rn x src 2&gt;/dev/null</c> disqualified itself and
/// reached a person instead), no command substitution and none of the flags that make a reading command write.
/// Anything it is unsure of goes to a person exactly as before. A read in a pipeline after something else
/// (<c>dotnet test ... | tail</c>) is not a read: the pipeline runs a build.
/// </para>
/// </summary>
public static class AgentReadRedirect
{
    /// <summary>What the model is told: which tool does each kind of looking, and what Bash is for.</summary>
    public const string Sentence =
        "This Bash command only reads, and reading has tools that run without asking anyone: Read for a file (with "
        + "offset and limit for a range of lines, instead of sed -n, head or tail), Grep for searching contents "
        + "(instead of grep or rg), and Glob for finding files (instead of ls or find). Use those to look; every Bash "
        + "call asks a person and waits, so keep Bash for building, testing and running things.";

    /// <summary>What the person is told, beside the call.</summary>
    public const string ForThePerson =
        "a Bash command that only reads was sent back to Read, Grep or Glob, which need nobody's approval";

    // The commands that only read or only print, with what an unattended tool does instead. cd, echo and pwd change
    // nothing and are allowed alongside them; on their own they are not a read worth redirecting.
    private static readonly HashSet<string> Reading = new(StringComparer.Ordinal)
    {
        "cat", "head", "tail", "grep", "egrep", "fgrep", "rg", "ls", "find", "sed", "wc", "sort", "uniq", "tree",
    };

    private static readonly HashSet<string> Neutral = new(StringComparer.Ordinal) { "cd", "echo", "pwd", "true" };

    /// <summary>The sentence for the model when <paramref name="toolName"/> is Bash and the command only reads, else null.</summary>
    public static string? Check(string? toolName, string? command) =>
        string.Equals(toolName, "Bash", StringComparison.Ordinal) && OnlyReads(command) ? Sentence : null;

    /// <summary>Whether every part of the command is a reading command, and at least one is.</summary>
    public static bool OnlyReads(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        // Backlog 337: a redirection whose target is exactly /dev/null does not disqualify a command, because
        // agents append one to a read constantly; every other redirection still does. DevNullRedirects is the
        // one rule for that shape, read here and by the autopilot judge's own command rewrite; a source-reading
        // guard (DevNullRuleIsDecidedInOnePlaceTests, Application.Tests) fails the build on a verbatim second
        // copy of this exact regex, identified by its quoted-target backreference, so that one way of the two
        // drifting apart cannot happen unnoticed. It does not guard against a differently-written second rule.
        var stripped = DevNullRedirects.Strip(command);
        if (stripped.Contains('>', StringComparison.Ordinal)
            || stripped.Contains('<', StringComparison.Ordinal)
            || stripped.Contains("$(", StringComparison.Ordinal)
            || stripped.Contains('`', StringComparison.Ordinal)
            || stripped.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }

        var sawRead = false;
        foreach (var segment in Segments(stripped))
        {
            var words = Words(segment);
            if (words.Count == 0)
            {
                return false;
            }

            var name = words[0];
            if (Neutral.Contains(name))
            {
                continue;
            }

            if (!Reading.Contains(name) || Writes(name, words))
            {
                return false;
            }

            sawRead = true;
        }

        return sawRead;
    }

    /// <summary>The flags that turn a reading command into one that changes something.</summary>
    private static bool Writes(string name, List<string> words) => name switch
    {
        // sed edits in place with -i; the reading form is -n with a print ("sed -n '60,95p' file"). Anything else is
        // left to a person, since this rule only ever refuses and gains nothing from guessing.
        "sed" => words.Any(w => w.StartsWith("-i", StringComparison.Ordinal) || w.StartsWith("--in-place", StringComparison.Ordinal))
            || !words.Contains("-n"),
        // find runs things and deletes things.
        "find" => words.Any(w => w is "-exec" or "-execdir" or "-ok" or "-okdir" or "-delete" or "-fprint" or "-fprintf" or "-fls"),
        // sort writes a file with -o.
        "sort" => words.Any(w => w == "-o" || w.StartsWith("--output", StringComparison.Ordinal)),
        _ => false,
    };

    /// <summary>
    /// The command split at <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c> and a newline, outside quotes. Newline is
    /// included for <see cref="AgentGitInBash"/>, which reads a multi-line script the same way; it changes nothing
    /// here, because <see cref="OnlyReads"/> bails out before ever calling this when a newline is present.
    /// </summary>
    internal static IEnumerable<string> Segments(string command)
    {
        var current = new System.Text.StringBuilder();
        char? quote = null;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }

                current.Append(c);
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                current.Append(c);
                continue;
            }

            if (c is ';' or '|' or '&' or '\n')
            {
                if (c == '&' && (i + 1 >= command.Length || command[i + 1] != '&'))
                {
                    // A lone & sends something to the background: not a plain read.
                    yield return "&";
                    current.Clear();
                    continue;
                }

                yield return current.ToString();
                current.Clear();
                if (c is '|' or '&' && i + 1 < command.Length && command[i + 1] == c)
                {
                    i++;
                }

                continue;
            }

            current.Append(c);
        }

        yield return current.ToString();
    }

    /// <summary>The words of one simple command, quotes removed, a leading environment assignment kept as a word.</summary>
    internal static List<string> Words(string segment)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var c in segment.Trim())
        {
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
