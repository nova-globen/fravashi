using System.Text;
using System.Text.RegularExpressions;

namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// The narrow rule list that answers routine calls itself for a session the autopilot runs, and nowhere
/// else (ADR 0031 decision 7). One class, mirroring <see cref="AgentGraphTools"/>'s shape: a fixed, named
/// set, each entry with its own test, so widening it is a deliberate act.
/// <para>
/// Two of the ADR's seven entries need no new code at all, because they are mechanisms that already run the
/// same way for every session, autopilot or not: edits inside the copy under the folder's Edits trust
/// (the <c>WorkspaceTrustLevels.IsEdit</c> branch in <see cref="AgentApprovalBroker"/>, configuration plane
/// excepted) and the graph creations ADR 0020 already lets run (<see cref="AgentGraphTools"/>). They are
/// not duplicated here.
/// </para>
/// <para>
/// <see cref="IsPlainBuildOrTest"/> reads only the raw call. <see cref="IsForwardStatusMove"/> also needs
/// the session's own bound items' CURRENT statuses (so an autopilot session can only move an item it is
/// actually doing, and only one genuine step forward, never backward) and <see cref="IsAttemptBranchPush"/>
/// also needs the session's recorded attempt branch and the workspace's actual current branch (so a push
/// cannot run unattended on the strength of an assumption about which branch is checked out).
/// <see cref="IsAttemptBranchStart"/> reads none of the raw call at all (<c>attempt_branch</c> takes no
/// argument): it needs the same recorded attempt branch and current branch <see cref="IsAttemptBranchPush"/>
/// does, plus the session's recorded run base. The broker fetches whatever each one needs before calling it,
/// because that is where the session's work links, the tracker and git live; this class stays a pure
/// function of what it is handed, which is what keeps it unit-testable without a database, a tracker or a
/// git process.
/// </para>
/// </summary>
public static partial class AutopilotApprovalRules
{
    /// <summary>The full name of <c>work_item_transition</c>.</summary>
    public const string TransitionTool = AgentWorkGate.StudioToolPrefix + "work_item_transition";

    /// <summary>The full name of <c>git_push</c>.</summary>
    public const string PushTool = AgentWorkGate.StudioToolPrefix + "git_push";

    /// <summary>The full name of <c>attempt_branch</c>.</summary>
    public const string AttemptBranchTool = AgentWorkGate.StudioToolPrefix + "attempt_branch";

    /// <summary>
    /// The project's plain build or test command, STARTING FROM <c>tools/window-probe/watch-trial.ps1</c>'s
    /// own pattern and narrower than it: <c>dotnet build</c> or <c>dotnet test</c>, an optional leading
    /// <c>timeout &lt;seconds&gt;</c>, no shell metacharacters (<c>;</c>, <c>&amp;</c>, <c>`</c>, <c>$</c>,
    /// <c>&lt;</c>, <c>&gt;</c>, <c>|</c>, a carriage return or a newline) beyond one optional trailing
    /// <c>| tail</c>/<c>| head</c>/<c>| grep -E "..."</c> read of its output (the grep pattern itself also
    /// excludes <c>$</c>, a backtick and a backslash, because bash expands <c>$(...)</c>, <c>${...}</c> and
    /// a backtick command substitution INSIDE a double-quoted string, so a quoted pattern is not inert the
    /// way plain text is), and an optional trailing <c>; echo "exit=${PIPESTATUS[0]}"</c> (the wrapper a
    /// long Bash call needs so it is not moved to the background, named in
    /// <c>tools/window-probe/watch-trial.ps1</c>, not CLAUDE.md). A newline is excluded explicitly rather
    /// than left to <c>$</c>: without <see cref="RegexOptions.Multiline"/> <c>$</c> still matches before a
    /// trailing newline, which would have let <c>dotnet build\nrm -rf ~</c> through as if it were one line.
    /// <c>build</c>/<c>test</c> is a WHOLE word (a lookahead for whitespace or the end), not merely a
    /// prefix: <c>dotnet &lt;file&gt;</c> runs that file as an application, so <c>dotnet build.dll</c>
    /// without the boundary matched with <c>.dll</c> read as a harmless-looking relative-path argument and
    /// ran whatever assembly an autopilot session had just as unattendedly produced (a csproj edit inside
    /// the copy under Edits trust, then the very <c>dotnet build</c> this rule already allows).
    /// <para>
    /// The ARGUMENTS between <c>build</c>/<c>test</c> and those trailing wrappers are an ALLOWLIST
    /// (<see cref="ArgumentsAreSafe"/>), not merely a forbidden-character class: MSBuild splits a
    /// <c>-p:</c> value on commas as well as semicolons, so a single token
    /// <c>-p:EnableWindowsTargeting=true,PostBuildEvent=./x.sh</c> carries a second property a
    /// per-character exclusion would never see, and a logger (<c>-logger:</c>, <c>-l:</c>, <c>-dl:</c>,
    /// <c>-flp:</c>, <c>-bl:</c>, <c>--logger</c>) or <c>--test-adapter-path</c> loads an assembly or
    /// writes to an arbitrary path with none of those characters at all. Only a named, narrow set of
    /// arguments with no execution or arbitrary-write surface is allowed; anything else refuses the whole
    /// command.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"^(timeout \d+ )?dotnet (build|test)(?=\s|$)(?<args>[^;&`$<>|\r\n]*)( 2>&1)?"
        + @"( \| (tail|head) (-n \d+|-\d+)| \| grep -E ""[^""\\$`\r\n]*"")?"
        + @"(; echo ""exit=\$\{PIPESTATUS\[0\]\}"")?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    /// <summary>
    /// The exact, case-sensitive MSBuild property switch CLAUDE.md's own container build commands use,
    /// because the container is Linux and <c>Fravashi.Desktop</c> targets Windows; no other property or value
    /// passes, whatever it is.
    /// </summary>
    private const string SafeProperty = "-p:EnableWindowsTargeting=true";

    /// <summary>A bare switch that changes nothing about where output goes or what runs.</summary>
    private static readonly HashSet<string> SafeBareSwitches = new(StringComparer.Ordinal)
    {
        "--no-build", "--no-restore",
    };

    /// <summary>A switch that takes one value naming a fixed, closed set, by its own exact word.</summary>
    private static readonly HashSet<string> SafeConfigurationValues =
        new(StringComparer.Ordinal) { "Debug", "Release" };

    private static readonly HashSet<string> SafeVerbosityValues = new(StringComparer.Ordinal)
    {
        "q", "quiet", "m", "minimal", "n", "normal", "d", "detailed", "diag", "diagnostic",
    };

    [GeneratedRegex(@"^-m:[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ParallelismSwitch();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativePathToken();

    /// <summary>The statuses one genuine forward step may move an item TO, keyed by the status it moves FROM.</summary>
    private static readonly Dictionary<string, string> ForwardTransitions = new(StringComparer.Ordinal)
    {
        ["ready"] = "in_progress",
        ["in_progress"] = "in_review",
    };

    /// <summary>Whether <paramref name="command"/> is exactly the plain build or test shape above.</summary>
    public static bool IsPlainBuildOrTest(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var match = Shape().Match(command);
        return match.Success && ArgumentsAreSafe(match.Groups["args"].Value);
    }

    /// <summary>
    /// Every argument token between <c>build</c>/<c>test</c> and the trailing wrappers is one of: the exact
    /// allowlisted MSBuild property, a bare safe switch, <c>-c</c>/<c>--configuration</c> with Debug or
    /// Release, <c>--verbosity</c> with one of its own named levels, <c>-m:N</c>, <c>--filter</c> with a
    /// following value that does not itself look like a switch (refused otherwise, rather than left for
    /// dotnet's own parser to decide what it meant), or a relative path with no <c>..</c> segment (a
    /// project, solution, or test path). Anything else, including an argument this allowlist has no case
    /// for, refuses the WHOLE command.
    /// </summary>
    private static bool ArgumentsAreSafe(string args)
    {
        var tokens = Tokenize(args);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (string.Equals(token, SafeProperty, StringComparison.Ordinal) || SafeBareSwitches.Contains(token))
            {
                continue;
            }

            if (token is "-c" or "--configuration")
            {
                if (i + 1 >= tokens.Count || !SafeConfigurationValues.Contains(tokens[i + 1]))
                {
                    return false;
                }

                i++;
                continue;
            }

            if (token == "--verbosity")
            {
                if (i + 1 >= tokens.Count || !SafeVerbosityValues.Contains(tokens[i + 1]))
                {
                    return false;
                }

                i++;
                continue;
            }

            if (token == "--filter")
            {
                // A value shaped like a switch is refused rather than passed through to dotnet's own
                // parser to decide what it meant: this allowlist shows what a token IS, not what dotnet
                // happens to do with it.
                if (i + 1 >= tokens.Count || tokens[i + 1] is { Length: > 0 } value && (value[0] == '-' || value[0] == '/'))
                {
                    return false;
                }

                i++;
                continue;
            }

            if (ParallelismSwitch().IsMatch(token))
            {
                continue;
            }

            if (IsSafeRelativePathToken(token))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// A relative path with no leading <c>-</c> or <c>/</c> (so no switch this allowlist does not already
    /// name, and no rooted Unix path, ever reaches here), no drive-letter colon, and no <c>..</c> segment.
    /// </summary>
    private static bool IsSafeRelativePathToken(string token) =>
        token.Length > 0
        && token[0] != '-'
        && token[0] != '/'
        && !token.Contains(':')
        && !token.Split('/').Any(segment => segment == "..")
        && RelativePathToken().IsMatch(token);

    /// <summary>
    /// Splits on whitespace, keeping a double-quoted section (a <c>--filter</c> value) together with its
    /// quotes stripped. The outer <see cref="Shape"/> match has already excluded every shell metacharacter
    /// from this text, so nothing here needs to guard against one.
    /// </summary>
    private static List<string> Tokenize(string args)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in args)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// Whether this is a <c>work_item_transition</c> moving ONE OF the session's own bound items exactly one
    /// genuine step forward along Ready, In Progress, In Review. <paramref name="boundItemStatuses"/> maps
    /// each bound item's board NUMBER to its CURRENT status (as the tool's own lower-snake names), read by
    /// the broker through the tracker; an item missing from it (not bound to this session, or the lookup
    /// failed) matches nothing, which is the fail-closed direction. A call naming a number whose current
    /// status is not a key <see cref="ForwardTransitions"/> has an entry for, or whose target <c>status</c>
    /// is not that entry's own value, refuses: that covers a different item, a move to <c>done</c> or
    /// <c>cancelled</c>, and a BACKWARD move (In Review back to In Progress) alike, because the rule is
    /// "this item's status moves along its own path" (ADR 0031 decision 7), a specific pair of steps, not
    /// "into one of two states".
    /// </summary>
    public static bool IsForwardStatusMove(
        string? toolName, AgentToolInput input, IReadOnlyDictionary<int, string> boundItemStatuses)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(boundItemStatuses);
        return string.Equals(toolName, TransitionTool, StringComparison.Ordinal)
            && input.Text("status") is { } status
            && input.TryGetValue("number", out var numberText)
            && int.TryParse(numberText, out var number)
            && boundItemStatuses.TryGetValue(number, out var currentStatus)
            && ForwardTransitions.TryGetValue(currentStatus, out var nextStatus)
            && string.Equals(status, nextStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether this is a <c>git_push</c> with no <c>tag</c>, where the session's own recorded attempt branch
    /// (<paramref name="attemptBranch"/>, from <c>ISessionBranches</c>) is non-empty and equals
    /// <paramref name="currentBranch"/> (what the workspace's git actually reports as checked out,
    /// <c>GetCurrentBranchAsync</c>). Both have to be supplied and equal: a session that never called
    /// <c>attempt_branch</c>, or whose host checkout a person switched back after, has no attempt branch or a
    /// mismatched one, and refuses rather than pushing whatever is current. A push naming a <c>tag</c>
    /// refuses outright.
    /// </summary>
    public static bool IsAttemptBranchPush(string? toolName, AgentToolInput input, string? attemptBranch, string? currentBranch)
    {
        ArgumentNullException.ThrowIfNull(input);
        return string.Equals(toolName, PushTool, StringComparison.Ordinal)
            && input.KindOf("tag") == AgentToolValueKind.Absent
            && !string.IsNullOrEmpty(attemptBranch)
            && string.Equals(attemptBranch, currentBranch, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether an <c>attempt_branch</c> call can only answer or create THIS session's own attempt branch
    /// (ADR 0031 decision 7, backlog 322). The autopilot engine gives every item exactly one of two facts
    /// before its building session is even started (<c>AgentAutopilotBuilder</c>), and no third shape is
    /// safe to treat as the item's own branch here: a WORKTREE item has its branch pre-recorded
    /// (<paramref name="alreadyStarted"/>, from <c>ISessionBranches</c>) before the session starts, so
    /// <c>SessionGitWork.StartAsync</c>'s own first line answers it straight back and touches no git at
    /// all; a plain, not-yet-started item instead carries the run's own base branch
    /// (<paramref name="knownBase"/>, from <c>ISessionAttemptBases</c>), which is what the call will branch
    /// FROM, named deterministically from the session's own bound work items and never from a call
    /// argument, because <c>attempt_branch</c>'s own input schema has no properties at all.
    /// <para>
    /// The worktree shape asks for one more thing, the same way the push rule already does: that the
    /// workspace's LIVE current branch (<paramref name="currentBranch"/>) still agrees with the recorded
    /// one, so a worktree folder a person moved off its own attempt branch after the engine recorded it is
    /// not silently handed back, even though <c>SessionGitWork</c> itself never reads git for this case.
    /// The cap-1 shape does NOT check the current branch at all: that is the whole point of the run's own
    /// base existing, branching from it rather than from whatever the shared folder happens to have checked
    /// out.
    /// </para>
    /// <para>
    /// A session with neither fact set names nothing this rule recognises as its own branch, and refuses,
    /// which also covers every tool name but this one.
    /// </para>
    /// </summary>
    public static bool IsAttemptBranchStart(
        string? toolName, string? alreadyStarted, string? currentBranch, string? knownBase) =>
        string.Equals(toolName, AttemptBranchTool, StringComparison.Ordinal)
        && (
            (!string.IsNullOrEmpty(alreadyStarted) && string.Equals(alreadyStarted, currentBranch, StringComparison.Ordinal))
            || (string.IsNullOrEmpty(alreadyStarted) && !string.IsNullOrEmpty(knownBase)));

    /// <summary>
    /// Whether a commit review's own verdict is what clears the commit, per the ADR's rule list rather than
    /// the judge: only an actual PASS, never a review the role was off for or one that gave no verdict. Takes
    /// the already-decided fact rather than the review itself, since <c>CommitReview</c> lives in the
    /// Application layer and this class has no dependency out; the caller passes
    /// <c>review?.Outcome == CommitReviewOutcome.Passed</c>.
    /// </summary>
    public static bool IsCommitAfterPassedReview(bool reviewPassed) => reviewPassed;
}
