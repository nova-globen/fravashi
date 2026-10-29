namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// A Bash command that runs git inside a session's contained copy, refused up front and never asked (work item
/// #323). Found in the 2026-10-15 Fravashi-vs-CLI benchmark:
/// asked to commit a change, both runs finished the code, tried <c>git</c> in Bash, found the copy has no
/// <c>.git</c>, and reached for <c>git init</c> there (one run was denied, the other was not even asked). Neither
/// agent found the studio's <c>git_commit</c> tool on its own. A <c>git init</c> inside the copy is worse than the
/// failure it replaces: a commit made there lands in a <c>.git</c> that never syncs to the host, so the work looks
/// committed and is lost.
/// <para>
/// It only ever REFUSES, so it is safe to be approximate the same way <see cref="AgentReadRedirect"/> is: a
/// command split into its top-level statements (<see cref="AgentReadRedirect.Segments"/>, shared with that class,
/// now also split on a newline so a multi-line script is read the same way), each checked by its own first word
/// after skipping a leading environment assignment (<c>FOO=bar git status</c>). This catches <c>git status</c>,
/// <c>git init</c>, and <c>cd workspace &amp;&amp; git commit</c> (two statements; the second's first word is
/// <c>git</c>). It does NOT catch git reached through a path (<c>/usr/bin/git</c>), an alias, a wrapper
/// (<c>env git</c>, <c>xargs git</c>), or a git invocation built entirely inside a variable or a command
/// substitution: those fall through and ask a person exactly as before, which costs a wasted approval round, never
/// an unsafe allow. The newline split also costs one false hit the other way: a heredoc body whose own line starts
/// with <c>git</c> (writing a shell script through Bash, say) is refused too, even though that line is data, not a
/// command this Bash call runs; refusing there is a wrong reason but never a wrong outcome's worse half, since the
/// call still only needs a person's turn either way, the same as any refusal this rule gets wrong.
/// </para>
/// </summary>
public static class AgentGitInBash
{
    /// <summary>What the model is told: the copy has no .git, and which tools read and commit instead.</summary>
    public const string Sentence =
        "This session's contained copy has no .git by design: read history and status with git_status, git_log, "
        + "git_show, git_diff and git_blame, and commit a change with git_commit (or attempt_commit on the "
        + "autopilot), which runs on the host's real repository. A git command run here through Bash cannot see "
        + "that repository, and a git init here would make a commit look real and then lose it, because that "
        + ".git never reaches the host. Use git_commit to commit.";

    /// <summary>What the person is told, beside the call.</summary>
    public const string ForThePerson =
        "a Bash command that runs git was refused up front: the copy has no .git, and git_commit is the way to commit";

    /// <summary>The same fact, for a new session's opening turn (work item #323, change 2).</summary>
    public const string OpeningNote =
        "A note from the studio: this session's contained copy has no .git by design, so read git history and "
        + "status with git_status, git_log, git_show, git_diff and git_blame, and commit a change with "
        + "git_commit (or attempt_commit on the autopilot), which runs on the host's real repository, never "
        + "with git in Bash.";

    /// <summary>The sentence for the model when <paramref name="toolName"/> is Bash and the command runs git, else null.</summary>
    public static string? Check(string? toolName, string? command) =>
        string.Equals(toolName, "Bash", StringComparison.Ordinal) && RunsGit(command) ? Sentence : null;

    /// <summary>Whether any top-level statement of the command runs git as its own command.</summary>
    public static bool RunsGit(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        foreach (var segment in AgentReadRedirect.Segments(command))
        {
            var words = AgentReadRedirect.Words(segment);
            var i = 0;
            while (i < words.Count && IsAssignment(words[i]))
            {
                i++;
            }

            if (i < words.Count && string.Equals(words[i], "git", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a word is a leading environment assignment (<c>NAME=value</c>), not the command itself.</summary>
    private static bool IsAssignment(string word)
    {
        var eq = word.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return false;
        }

        var name = word[..eq];
        return (char.IsLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
