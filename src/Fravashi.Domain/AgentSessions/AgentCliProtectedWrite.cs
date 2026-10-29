namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// A file write the pinned agent CLI refuses on its own, whatever the studio answers, refused by the studio first so
/// the agent is told why and what to do instead (backlog 271). Found on 2026-10-10 in #225: an agent asked to write
/// <c>.devcontainer/devcontainer.json</c> for the app runner was asked about it, a person approved, and the CLI then
/// answered "Claude requested permissions to edit ... which is a sensitive file." Approving again gave the same
/// answer, so the agent looped on it.
/// <para>
/// Read from the pinned 2.1.280 binary, not run (no live turn could start inside a session; the plan on #271 has the
/// excerpts): its file-write check returns "ask" for a path with a component on its protected folder list, or whose
/// last component is on its protected file list (both copied below), BEFORE it consults <c>acceptEdits</c> or any
/// allow rule from a settings file; only a rule from the SESSION source comes earlier. Traced: <c>--allowedTools</c>
/// rules land in the <c>cliArg</c> source, not there, and at start the session source is seeded only from
/// <c>CLAUDE_BG_SESSION_PERMISSION_RULES</c> when <c>CLAUDE_CODE_SESSION_KIND</c> is <c>bg</c>, an internal
/// background-session switch whose other effects were not examined. That is the one lever found and it is NOT used: it
/// could not be driven live here, and an unmeasured vendor switch on the approval path is worse than this refusal (the
/// plan on #271 names it as the follow-up). A PreToolUse hook's allow does not lift the ask either: the CLI logs "Hook
/// returned 'allow' ..., but ask rule/safety check requires full permission pipeline" and hands the call to that
/// pipeline, which in headless mode, with no permission prompt tool, refuses. So an allow rule or acceptEdits does not
/// lift it, and asking a person about it only buys the same refusal. Not examined: bypassPermissions (excluded anyway,
/// since nothing may become unattended). Not tried: a permission prompt tool answering that pipeline.
/// </para>
/// <para>
/// It only ever REFUSES, so it widens nothing. #271 left the binary's <c>.claude</c> branch out on the theory it
/// might allow some paths under it (<c>skills</c>, <c>agents</c>, <c>commands</c>, <c>scheduled_tasks.json</c> and
/// <c>worktrees</c>). #279 measured a <c>.claude/skills</c> write, twice, approved, against the pinned 2.1.280
/// headless: nothing landed either time, with the CLI answering "... but you haven't granted it yet", a DIFFERENT
/// message from the sensitive-file one #225 saw for <c>.devcontainer</c> (the mechanism behind it was not traced).
/// <c>agents</c>, <c>commands</c>, <c>worktrees</c> and <c>scheduled_tasks.json</c> were not themselves measured;
/// they are refused now by policy, not by measurement, because the carve-out's theory was never resolved and the
/// one name tried under it still failed after approval. Every <c>.claude</c> path is refused uniformly now, the
/// same as the rest of the list. The binary's multi-component list (a variable bound to <c>[".config/git"]</c> in
/// one place and <c>[]</c> in another, which one applies was not resolved) and its other checks before these lists
/// are not copied either, so a path only they catch still reaches a person as before. The plan on #271 has the
/// excerpts. Re-check it when the pin moves (ADR 0023): a CLI that stops protecting a name makes this refusal the
/// only thing in the way.
/// </para>
/// </summary>
public static class AgentCliProtectedWrite
{
    /// <summary>
    /// What the model is told for any protected name. Both refusals contain it, which is how the person's sentence is
    /// found for either (<see cref="AgentWorkGate.ForThePerson"/>).
    /// </summary>
    public const string Sentence =
        "The agent CLI this studio runs protects this location (folders such as .git, .vscode, .claude and "
        + ".devcontainer, and files such as .bashrc, .npmrc and .mcp.json) and refuses an approved write there anyway "
        + "(as a sensitive file, or as a permission it says was not granted), so the studio refuses every file-tool "
        + "write there up front instead of asking. Do not retry it, and do not try to write it through Bash instead.";

    /// <summary>The rest of the sentence for every protected name but the devcontainer ones.</summary>
    public const string OtherwiseAskAPerson = " If the change is needed, say so and a person will make it.";

    /// <summary>The rest of the sentence for <c>.devcontainer</c> and <c>.devcontainer.json</c>: the app runner's alternative.</summary>
    public const string DevcontainerAlternative =
        " For the app runner, write a Dockerfile at the repository root instead, which it accepts too; if the project "
        + "needs the devcontainer file itself, say so and a person will add it.";

    /// <summary>What the person is told, beside the call.</summary>
    public const string ForThePerson =
        "a write to a location the agent CLI protects was refused up front, because the CLI refuses an approved Write there";

    // The pinned 2.1.280 binary's lists, verbatim and in its order. A path is protected when any component is a folder
    // here, or its last component is a file here.
    private static readonly string[] Folders =
        [".git", ".vscode", ".idea", ".claude", ".husky", ".cargo", ".devcontainer", ".yarn", ".mvn"];

    private static readonly string[] Files =
    [
        ".gitconfig", ".gitmodules", ".bashrc", ".bash_profile", ".zshrc", ".zprofile", ".profile", ".zshenv",
        ".zlogin", ".zlogout", ".bash_login", ".bash_aliases", ".bash_logout", ".envrc", ".ripgreprc", ".mcp.json",
        ".claude.json", ".npmrc", ".yarnrc", ".yarnrc.yml", ".pnp.cjs", ".pnp.loader.mjs", ".pnpmfile.cjs",
        "bunfig.toml", ".bunfig.toml", ".bazelrc", ".bazelversion", ".bazeliskrc", ".pre-commit-config.yaml",
        "lefthook.yml", ".lefthook.yml", "lefthook.yaml", ".lefthook.yaml", "gradle-wrapper.properties",
        "maven-wrapper.properties", ".devcontainer.json", "pyrightconfig.json",
    ];

    // The file tools and the argument each names its target in. Write is the tool #225 saw refused, and the binary's
    // file-write check is the one read; that Edit, MultiEdit and NotebookEdit reach the same check was not traced, so
    // covering them is the studio's deliberate over-refusal (a write there is the same file in the same place). Bash is
    // not here: what a command writes is not knowable from its text. Read from the same binary: its Bash check passes
    // an output redirection's target to the same safety check, but whether every redirection reaches that call, and
    // what it does with cp, tee or mkdir there, was not traced; the sentence's "not through Bash" is advice, not a
    // claim about the CLI.
    private static readonly Dictionary<string, string> Writers = new(StringComparer.Ordinal)
    {
        ["Write"] = "file_path",
        ["Edit"] = "file_path",
        ["MultiEdit"] = "file_path",
        ["NotebookEdit"] = "notebook_path",
    };

    /// <summary>The argument <paramref name="toolName"/> names its target in, or null when it is not a file tool.</summary>
    public static string? PathField(string? toolName) =>
        toolName is not null && Writers.TryGetValue(toolName, out var field) ? field : null;

    /// <summary>
    /// What the model is told when <paramref name="toolName"/> is a file tool and any of <paramref name="paths"/> (the
    /// path as the agent wrote it, and where it resolves) is one the CLI protects, else null. A devcontainer name gets the
    /// app runner's alternative; any other gets "ask a person".
    /// </summary>
    public static string? Check(string? toolName, params string?[] paths)
    {
        if (PathField(toolName) is null)
        {
            return null;
        }

        var found = paths.Select(ProtectedName).Where(n => n is not null).ToList();
        if (found.Count == 0)
        {
            return null;
        }

        var devcontainer = found.Any(n =>
            string.Equals(n, ".devcontainer", StringComparison.Ordinal)
            || string.Equals(n, ".devcontainer.json", StringComparison.Ordinal));
        return Sentence + (devcontainer ? DevcontainerAlternative : OtherwiseAskAPerson);
    }

    /// <summary>Whether the CLI treats <paramref name="path"/> as protected (see <see cref="ProtectedName"/>).</summary>
    public static bool IsProtected(string? path) => ProtectedName(path) is not null;

    /// <summary>
    /// The protected name <paramref name="path"/> carries, as the list spells it, or null. The binary lowercases the list
    /// entry, and lowercases the path's component and then also strips a <c>:</c> suffix and a trailing run of dots and
    /// spaces (two functions; the plan on #271 has both excerpts), so <c>.DevContainer</c>, <c>.devcontainer.</c> and
    /// <c>.devcontainer:x</c> all count. This copies that; one more replacement the binary makes first (a regex whose
    /// definition was not resolved) is not copied. Split on both separators, because a refusal that is slightly too
    /// wide costs one sentence and one that is too narrow costs the loop this exists to stop.
    /// </summary>
    public static string? ProtectedName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // An array, not two chars: Split('/', '\\', options) binds to the (char, int count, options) overload.
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Select(Normalised).ToArray();
        for (var i = 0; i < parts.Length; i++)
        {
            if (Folders.FirstOrDefault(f => string.Equals(f, parts[i], StringComparison.OrdinalIgnoreCase)) is { } folder)
            {
                return folder;
            }
        }

        return parts.Length > 0
            ? Files.FirstOrDefault(f => string.Equals(f, parts[^1], StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>A component as the CLI compares it: a <c>:</c> suffix and a trailing run of dots and spaces removed.</summary>
    private static string Normalised(string part)
    {
        var colon = part.IndexOf(':', StringComparison.Ordinal);
        var cut = (colon >= 0 ? part[..colon] : part).TrimEnd('.', ' ');

        // The binary keeps the component as it was when stripping leaves nothing ("||t").
        return cut.Length > 0 ? cut : part;
    }
}
