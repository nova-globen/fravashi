using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Git;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Domain.Git;
using Fravashi.Infrastructure.Git;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// What an agent reaches git with (backlog 150). The contained copy has no <c>.git</c> on purpose, so an agent
/// working in it has no history, no blame and no way to commit; a person did all of that from the Git page.
/// These tools run the same parsed services the page runs, through the bridge, in the HOST folder of the
/// session's own workspace (<see cref="SessionGit"/>), so the agent sees the repository the person sees and
/// never touches <c>.git</c>'s files itself.
/// <para>
/// Two rules every tool here keeps. <b>Every argument vector is fixed</b>: the bridge runs whatever git
/// arguments it is handed, so a tool that passed an agent's text through as an option would hand it
/// <c>--output=</c> on the host. A reference and a path are validated (<see cref="GitToolArguments"/>) and a
/// path always follows <c>--</c>. And <b>a read is labelled as other-authored content</b>: commit messages
/// and blame lines were written by people, which is exactly what the threat model's trust label is for.
/// </para>
/// </summary>
public abstract class GitToolBase(SessionGit git) : IStudioTool
{
    /// <summary>The most characters of diff, log or file content one answer carries; the rest is said to be cut.</summary>
    protected const int TextCap = 40_000;

    /// <summary>The cap, for a test to assert against without repeating the number.</summary>
    public const int TextCapForTests = TextCap;

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract string InputSchemaJson { get; }

    /// <inheritdoc />
    public abstract StudioToolAccess Access { get; }

    /// <summary>The session-to-git resolver, for the tools that also need the settle state.</summary>
    protected SessionGit Sessions { get; } = git ?? throw new ArgumentNullException(nameof(git));

    /// <inheritdoc />
    public Task<StudioToolResult> InvokeAsync(StudioToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var resolved = Sessions.For(call.SessionId);
        if (resolved is not { Git.IsAvailable: true } services)
        {
            // Backlog 266: a host with no git says so, rather than looking like a folder with no repository.
            return Task.FromResult(resolved?.Git.UnavailableReason is { Length: > 0 } reason
                ? Rejected("git_not_installed", "workspace", reason)
                : Rejected(
                    "no_repository",
                    "workspace",
                    "This session's workspace has no git repository the studio can reach, so there is no history to read and nothing to commit to."));
        }

        return RunAsync(call, services, cancellationToken);
    }

    /// <summary>The tool's own work, with the call checked and git known to be reachable.</summary>
    protected abstract Task<StudioToolResult> RunAsync(
        StudioToolCall call, WorkspaceGitServices git, CancellationToken cancellationToken);

    /// <summary>A retrieval answer, labelled as other-authored data (threat model requirement 2).</summary>
    protected static StudioToolResult Retrieved(JsonNode payload) =>
        StudioToolResult.Success(new JsonObject
        {
            ["trust"] = "other-authored-content",
            ["note"] = WorkItemToolBase.TrustNote,
            ["data"] = payload,
        }.ToJsonString());

    /// <summary>A mutation answer: what the studio itself just did, so it carries no trust label.</summary>
    protected static StudioToolResult Changed(string what, JsonNode payload) =>
        StudioToolResult.Success(new JsonObject { ["changed"] = what, ["result"] = payload }.ToJsonString());

    /// <summary>A refusal this tool made, in the same shape the other studio tools use.</summary>
    protected static StudioToolResult Rejected(string code, string field, string message) =>
        StudioToolResult.Failure(new JsonObject
        {
            ["refused"] = code,
            ["errors"] = new JsonArray(new JsonObject
            {
                ["code"] = code,
                ["field"] = field,
                ["message"] = message,
            }),
        }.ToJsonString());

    /// <summary>Git's own words for a failed command, trimmed to a line an agent can act on.</summary>
    protected static StudioToolResult GitSaid(string code, string field, GitCommandResult result) =>
        Rejected(code, field, Summarize(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr));

    /// <summary>A string argument, or null when absent, not a string, or blank.</summary>
    protected static string? Text(StudioToolCall call, string name) =>
        call.Arguments.ValueKind == JsonValueKind.Object
        && call.Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    /// <summary>An integer argument, or null.</summary>
    protected static int? Number(StudioToolCall call, string name) =>
        call.Arguments.ValueKind == JsonValueKind.Object
        && call.Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>A boolean argument, or null.</summary>
    protected static bool? Flag(StudioToolCall call, string name) =>
        call.Arguments.ValueKind == JsonValueKind.Object
        && call.Arguments.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <summary>An array-of-strings argument as its non-blank entries, or null when absent or not an array.</summary>
    protected static IReadOnlyList<string>? Strings(StudioToolCall call, string name)
    {
        if (call.Arguments.ValueKind != JsonValueKind.Object
            || !call.Arguments.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!.Trim())
            .Where(e => e.Length > 0)
            .ToList();
    }

    /// <summary>
    /// A reference argument that is safe to hand git as a positional word, or a refusal. Absent is allowed
    /// and answers null with no refusal; the caller decides the default.
    /// </summary>
    protected static string? Reference(StudioToolCall call, string name, out StudioToolResult? refusal)
    {
        refusal = null;
        var text = Text(call, name);
        if (text is null)
        {
            return null;
        }

        if (!GitToolArguments.IsReference(text))
        {
            refusal = Rejected("bad_reference", name, GitToolArguments.ReferenceRule);
            return null;
        }

        return text;
    }

    /// <summary>
    /// A tag-name argument that is safe to hand git as the word after <c>refs/tags/</c>, or a refusal. Absent is
    /// allowed and answers null with no refusal; the caller decides whether that is a missing-argument error.
    /// </summary>
    protected static string? TagName(StudioToolCall call, string name, out StudioToolResult? refusal)
    {
        refusal = null;
        var text = Text(call, name);
        if (text is null)
        {
            return null;
        }

        if (!GitToolArguments.IsTagName(text))
        {
            refusal = Rejected("bad_tag_name", name, GitToolArguments.TagNameRule);
            return null;
        }

        return text;
    }

    /// <summary>A repository-relative path argument, normalised, or a refusal. Absent answers null.</summary>
    protected static string? RelativePath(StudioToolCall call, string name, out StudioToolResult? refusal)
    {
        refusal = null;
        var text = Text(call, name);
        if (text is null)
        {
            return null;
        }

        if (GitToolArguments.NormalizePath(text) is not { } path)
        {
            refusal = Rejected("bad_path", name, GitToolArguments.PathRule);
            return null;
        }

        return path;
    }

    /// <summary>Text cut at the cap, with whether it was cut, so an answer never silently ends mid-hunk.</summary>
    protected static (string Text, bool Truncated) Cap(string text) =>
        text.Length <= TextCap ? (text, false) : (text[..TextCap], true);

    /// <summary>One commit as an object, the same shape everywhere it appears.</summary>
    protected static JsonObject Describe(GitCommit commit) => new()
    {
        ["sha"] = commit.Sha,
        ["short_sha"] = commit.ShortSha,
        ["subject"] = commit.Subject,
        ["author"] = commit.Author,
        ["when"] = commit.When.ToString("O", CultureInfo.InvariantCulture),
    };

    /// <summary>One changed file as an object.</summary>
    protected static JsonObject Describe(GitFileChange change) => new()
    {
        ["path"] = change.Path,
        ["kind"] = change.Kind.ToString().ToLowerInvariant(),
        ["old_path"] = change.OldPath,
    };

    private static string Summarize(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("hint:", StringComparison.Ordinal)) ?? "git did not say why.";
        return line.Length <= 300 ? line : line[..300];
    }
}

/// <summary>
/// The three shapes of agent text these tools hand git, and what each may contain: a reference, a path, and a
/// tag name. All three refuse a leading hyphen outright, because that is the one character that turns a word
/// into an option on the host.
/// </summary>
public static class GitToolArguments
{
    /// <summary>The sentence a refused reference gets.</summary>
    public const string ReferenceRule =
        "A reference is a branch, tag, commit or revision expression such as main, v1.2, HEAD~2 or origin/main: "
        + "no leading hyphen, no whitespace, no control characters.";

    /// <summary>The sentence a refused path gets.</summary>
    public const string PathRule =
        "A path is relative to the repository root, with forward slashes, no leading hyphen and no '..' segment.";

    /// <summary>Whether this text may be handed to git as a revision word.</summary>
    public static bool IsReference(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text[0] == '-' || text.Length > 256)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The text as a repository-relative path with forward slashes, or null when it is not one: rooted, a
    /// drive, a leading hyphen, a <c>..</c> segment, or a control character all answer null.
    /// </summary>
    public static string? NormalizePath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var path = text.Trim().Replace('\\', '/');
        if (path[0] is '-' or '/' || path.Length > 1 && path[1] == ':' || path.Any(char.IsControl))
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".")
            .ToArray();
        if (segments.Length == 0 || segments.Any(s => s == ".."))
        {
            return null;
        }

        return string.Join('/', segments);
    }

    /// <summary>The sentence a refused tag name gets.</summary>
    public const string TagNameRule =
        "A tag name follows git's own ref-name rules: no ':', '^', '~', '?', '*', '[', '\\', whitespace or control "
        + "character, no '..', no '@{', not '@' alone, no leading '-', '+' or '/', no trailing '/', '.' or '.lock', "
        + "no '//', and no slash-separated part starting with '.' or ending with '.lock'.";

    /// <summary>
    /// Whether this text is safe to hand git as the word after <c>refs/tags/</c> or as <c>git tag</c>'s name: git's
    /// own ref-name rules (backlog 285), so a colon, caret, tilde or the other characters that give a git command
    /// line extra meaning can never ride inside what the studio always treats as "just a tag".
    /// </summary>
    public static bool IsTagName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 || text == "@")
        {
            return false;
        }

        if (text[0] is '-' or '+' or '/' || text[^1] is '/' or '.')
        {
            return false;
        }

        if (text.EndsWith(".lock", StringComparison.Ordinal)
            || text.Contains("..", StringComparison.Ordinal)
            || text.Contains("@{", StringComparison.Ordinal)
            || text.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is ':' or '^' or '~' or '?' or '*' or '[' or '\\')
            {
                return false;
            }
        }

        foreach (var segment in text.Split('/'))
        {
            if (segment.StartsWith('.') || segment.EndsWith(".lock", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The working tree at a glance, plus what the sync engine still owes the host.</summary>
public sealed class GitStatusTool(SessionGit git) : GitToolBase(git)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "git_status";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "The workspace repository's status on the host: current branch, upstream, ahead/behind counts, staged and "
        + "unstaged changes, and how many of this session's edits are still on their way from the contained copy "
        + "to the host folder (git sees an edit only once it has arrived).";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """{"type":"object","properties":{},"additionalProperties":false}""";

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, WorkspaceGitServices git, CancellationToken cancellationToken)
    {
        var status = await git.Tree.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var pending = Sessions.PendingWriteBacks(call.SessionId);

        return Retrieved(new JsonObject
        {
            ["branch"] = status.Branch,
            ["upstream"] = status.Upstream,
            ["ahead"] = status.Ahead,
            ["behind"] = status.Behind,
            ["staged"] = new JsonArray([.. status.StagedEntries.Select(Entry)]),
            ["unstaged"] = new JsonArray([.. status.UnstagedEntries.Select(Entry)]),
            ["pending_write_backs"] = pending,
            ["note"] = pending == 0
                ? "Every edit from this session has reached the host folder."
                : $"{pending} edit(s) from this session are still on their way to the host folder; git will see them shortly.",
        });
    }

    private static JsonNode Entry(GitWorkingEntry entry) => new JsonObject
    {
        ["path"] = entry.Path,
        ["state"] = entry.State.ToString().ToLowerInvariant(),
        ["old_path"] = entry.OldPath,
    };
}

/// <summary>Commits reachable from a reference, newest first, optionally only those touching a path.</summary>
public sealed class GitLogTool(SessionGit git) : GitToolBase(git)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "git_log";

    /// <summary>How many commits an answer carries when the caller does not say.</summary>
    public const int DefaultMax = 20;

    /// <summary>The most commits one answer carries.</summary>
    public const int MaxMax = 200;

    // Unit separator between fields: never in a subject, unlike anything printable.
    private const char Sep = '\u001f';

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Commits in the workspace repository, newest first: from `reference` (default HEAD), at most `max` "
        + "(default 20, up to 200), and only those touching `path` when one is given.";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """
        {"type":"object","properties":{
          "reference":{"type":"string","description":"A branch, tag or commit to start from. Default HEAD."},
          "max":{"type":"integer","description":"How many commits at most. Default 20, up to 200."},
          "path":{"type":"string","description":"Only commits touching this repository-relative path."}
        },"additionalProperties":false}
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, WorkspaceGitServices git, CancellationToken cancellationToken)
    {
        var reference = Reference(call, "reference", out var refused) ?? "HEAD";
        if (refused is not null)
        {
            return refused;
        }

        var path = RelativePath(call, "path", out refused);
        if (refused is not null)
        {
            return refused;
        }

        var max = Math.Clamp(Number(call, "max") ?? DefaultMax, 1, MaxMax);
        var args = new List<string>
        {
            "log", reference, "-n" + max.ToString(CultureInfo.InvariantCulture), "--no-color",
            $"--pretty=format:%H{Sep}%h{Sep}%an{Sep}%aI{Sep}%s",
        };
        if (path is not null)
        {
            args.Add("--");
            args.Add(path);
        }

        var result = await git.Runner.RunAsync(args, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return GitSaid("unknown_reference", "reference", result);
        }

        var commits = new JsonArray();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split(Sep);
            if (fields.Length < 5)
            {
                continue;
            }

            commits.Add(Describe(new GitCommit(
                fields[0], fields[1], fields[4], fields[2],
                DateTimeOffset.TryParse(fields[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
                    ? when
                    : DateTimeOffset.UnixEpoch)));
        }

        return Retrieved(new JsonObject
        {
            ["reference"] = reference,
            ["path"] = path,
            ["count"] = commits.Count,
            ["commits"] = commits,
        });
    }
}

/// <summary>One commit in full: its message, its files, and its diff, or one file as that commit left it.</summary>
public sealed class GitShowTool(SessionGit git) : GitToolBase(git)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "git_show";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "One commit of the workspace repository: message, author, changed files and the unified diff, "
        + "limited to `path` when one is given. With `file` set, the content of that repository-relative "
        + "file as it was at the commit instead of the diff.";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """
        {"type":"object","properties":{
          "sha":{"type":"string","description":"The commit: a sha, a tag, a branch or a revision such as HEAD~1."},
          "path":{"type":"string","description":"Limit the diff to this repository-relative path."},
          "file":{"type":"string","description":"Instead of the diff, this file's content at the commit."}
        },"required":["sha"],"additionalProperties":false}
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, WorkspaceGitServices git, CancellationToken cancellationToken)
    {
        var sha = Reference(call, "sha", out var refused);
        if (refused is not null)
        {
            return refused;
        }

        if (sha is null)
        {
            return Rejected("missing_sha", "sha", "Say which commit to show.");
        }

        var path = RelativePath(call, "path", out refused);
        if (refused is not null)
        {
            return refused;
        }

        var file = RelativePath(call, "file", out refused);
        if (refused is not null)
        {
            return refused;
        }

        var detail = await git.Git.GetCommitAsync(sha, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return Rejected("unknown_commit", "sha", $"No commit answers to '{sha}' in this repository.");
        }

        var answer = new JsonObject
        {
            ["commit"] = Describe(detail.Commit),
            ["body"] = detail.Body,
            ["files"] = new JsonArray([.. detail.Files.Select(Describe)]),
        };

        if (file is not null)
        {
            var read = await git.Git.ReadFileAsync(detail.Commit.Sha, file, cancellationToken).ConfigureAwait(false);
            if (read is null)
            {
                return Rejected("no_such_file", "file", $"'{file}' does not exist at {detail.Commit.ShortSha}.");
            }

            var (content, cut) = Cap(read.Content ?? string.Empty);
            answer["file"] = file;
            answer["content"] = content;
            answer["truncated"] = cut;
            return Retrieved(answer);
        }

        var (diff, truncated) = Cap(await git.Git.GetDiffAsync(detail.Commit.Sha, path, cancellationToken).ConfigureAwait(false));
        answer["path"] = path;
        answer["diff"] = diff;
        answer["truncated"] = truncated;
        return Retrieved(answer);
    }
}

/// <summary>What differs: in the working tree against HEAD, or between two references.</summary>
public sealed class GitDiffTool(SessionGit git) : GitToolBase(git)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "git_diff";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "A unified diff of the workspace repository. With no `base`: the working tree against HEAD (unstaged "
        + "changes, or the staged ones with `staged` true). With `base`: the changes on `head` (default HEAD) "
        + "since it diverged from `base`, which is what a pull request shows; `merge_base` false compares the two "
        + "trees directly instead. `path` limits either to one file or folder.";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """
        {"type":"object","properties":{
          "base":{"type":"string","description":"The reference to compare from, such as main or v1.2."},
          "head":{"type":"string","description":"The reference to compare to. Default HEAD."},
          "path":{"type":"string","description":"Limit to this repository-relative path."},
          "staged":{"type":"boolean","description":"Working-tree mode only: show what is staged rather than what is not."},
          "merge_base":{"type":"boolean","description":"Reference mode only: compare since the merge base (default true) or the two trees directly (false)."}
        },"additionalProperties":false}
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, WorkspaceGitServices git, CancellationToken cancellationToken)
    {
        var baseRef = Reference(call, "base", out var refused);
        if (refused is not null)
        {
            return refused;
        }

        var headRef = Reference(call, "head", out refused);
        if (refused is not null)
        {
            return refused;
        }

        var path = RelativePath(call, "path", out refused);
        if (refused is not null)
        {
            return refused;
        }

        var common = new List<string> { "diff", "--no-color", "--no-ext-diff" };
        string mode;
        if (baseRef is null)
        {
            if (Flag(call, "staged") == true)
            {
                common.Add("--cached");
                mode = "staged";
            }
            else
            {
                mode = "working-tree";
            }
        }
        else
        {
            var head = headRef ?? "HEAD";
            var dots = Flag(call, "merge_base") == false ? ".." : "...";
            common.Add(baseRef + dots + head);
            mode = baseRef + dots + head;
        }

        var tail = path is null ? Array.Empty<string>() : ["--", path];

        var names = await git.Runner.RunAsync([.. common, "--name-status", "--find-renames", .. tail], null, cancellationToken)
            .ConfigureAwait(false);
        if (names.ExitCode != 0)
        {
            return GitSaid("diff_failed", baseRef is null ? "path" : "base", names);
        }

        var patch = await git.Runner.RunAsync([.. common, .. tail], null, cancellationToken).ConfigureAwait(false);
        if (patch.ExitCode != 0)
        {
            return GitSaid("diff_failed", baseRef is null ? "path" : "base", patch);
        }

        var (diff, truncated) = Cap(patch.StdOut);
        return Retrieved(new JsonObject
        {
            ["mode"] = mode,
            ["path"] = path,
            ["files"] = new JsonArray([.. Changes(names.StdOut).Select(Describe)]),
            ["diff"] = diff,
            ["truncated"] = truncated,
        });
    }

    private static IEnumerable<GitFileChange> Changes(string nameStatus)
    {
        foreach (var raw in nameStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var parts = line.Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0)
            {
                continue;
            }

            var kind = parts[0][0] switch
            {
                'A' => GitChangeKind.Added,
                'D' => GitChangeKind.Deleted,
                'R' => GitChangeKind.Renamed,
                'C' => GitChangeKind.Added,
                _ => GitChangeKind.Modified,
            };
            yield return kind == GitChangeKind.Renamed && parts.Length >= 3
                ? new GitFileChange(parts[2], kind, parts[1])
                : new GitFileChange(parts[^1], kind);
        }
    }
}

/// <summary>Who last changed each line of a range of a file, with the commit that did it.</summary>
public sealed class GitBlameTool(SessionGit git) : GitToolBase(git)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "git_blame";

    /// <summary>The most lines one answer covers.</summary>
    public const int MaxLines = 300;

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "For each line of `path` between `from` and `to` (1-based, at most 300 lines, default the first 300): "
        + "the commit that last changed it, its author, date and subject, and the line's text as committed.";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """
        {"type":"object","properties":{
          "path":{"type":"string","description":"The repository-relative file."},
          "from":{"type":"integer","description":"First line, 1-based. Default 1."},
          "to":{"type":"integer","description":"Last line, inclusive. Default from + 299."}
        },"required":["path"],"additionalProperties":false}
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, WorkspaceGitServices git, CancellationToken cancellationToken)
    {
        var path = RelativePath(call, "path", out var refused);
        if (refused is not null)
        {
            return refused;
        }

        if (path is null)
        {
            return Rejected("missing_path", "path", "Say which file to blame.");
        }

        var from = Math.Max(1, Number(call, "from") ?? 1);
        var to = Math.Clamp(Number(call, "to") ?? from + MaxLines - 1, from, from + MaxLines - 1);

        var result = await git.Runner.RunAsync(
                ["blame", "--porcelain", "-L", $"{from},{to}", "--", path], null, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return GitSaid("blame_failed", "path", result);
        }

        return Retrieved(new JsonObject
        {
            ["path"] = path,
            ["from"] = from,
            ["to"] = to,
            ["lines"] = Parse(result.StdOut),
        });
    }

    /// <summary>
    /// The porcelain format: a header <c>sha orig final [group]</c>, then <c>key value</c> lines (author,
    /// author-time, summary and friends appear the FIRST time a sha is seen), then one tab-prefixed content
    /// line. Later lines of the same sha carry the header and the content only, so the metadata is kept
    /// per sha.
    /// </summary>
    private static JsonArray Parse(string porcelain)
    {
        var lines = new JsonArray();
        var authors = new Dictionary<string, (string Author, string When, string Subject)>(StringComparer.Ordinal);
        string? sha = null;
        var final = 0;
        string? author = null, when = null, subject = null;

        foreach (var raw in porcelain.Split('\n'))
        {
            var row = raw.TrimEnd('\r');
            if (row.Length == 0)
            {
                continue;
            }

            if (row[0] == '\t')
            {
                if (sha is null)
                {
                    continue;
                }

                if (author is not null)
                {
                    authors[sha] = (author, when ?? string.Empty, subject ?? string.Empty);
                }

                var known = authors.TryGetValue(sha, out var meta) ? meta : (string.Empty, string.Empty, string.Empty);
                lines.Add(new JsonObject
                {
                    ["line"] = final,
                    ["sha"] = sha,
                    ["short_sha"] = sha.Length >= 7 ? sha[..7] : sha,
                    ["author"] = known.Item1,
                    ["when"] = known.Item2,
                    ["subject"] = known.Item3,
                    ["text"] = row[1..],
                });
                sha = null;
                author = when = subject = null;
                continue;
            }

            if (sha is null)
            {
                var head = row.Split(' ');
                if (head.Length >= 3 && head[0].Length >= 40 && int.TryParse(head[2], NumberStyles.None, CultureInfo.InvariantCulture, out final))
                {
                    sha = head[0];
                }

                continue;
            }

            var space = row.IndexOf(' ', StringComparison.Ordinal);
            var key = space < 0 ? row : row[..space];
            var value = space < 0 ? string.Empty : row[(space + 1)..];
            switch (key)
            {
                case "author":
                    author = value;
                    break;
                case "author-time":
                    when = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("O", CultureInfo.InvariantCulture)
                        : value;
                    break;
                case "summary":
                    subject = value;
                    break;
            }
        }

        return lines;
    }
}

/// <summary>
/// Commits this session's work on the host, through the bridge, with the host identity. It asks a person
/// first (it is not on <c>AgentGraphTools</c>), waits for the session's edits to reach the host, and
/// commits the named paths, or <see cref="SessionGitWork.DefaultPathsAsync"/>'s own default when none are
/// named: ordinarily every path the session was approved to change, or, for an autopilot builder running in
/// its item's own git worktree (backlog 332), every path uncommitted in that worktree too.
/// </summary>
public sealed class GitCommitTool(SessionGit git, SessionGitWork work) : GitToolBase(git)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "git_commit";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Commits to the workspace repository on the host with `message` (write it as the repository's conventions "
        + "ask, for example type(scope): subject). `paths` names the files to commit; when omitted, every file "
        + "this session was approved to change is committed, or, if this is an autopilot builder running in its "
        + "item's own git worktree, every path uncommitted in that worktree too. It waits for this session's edits "
        + "to reach the host first and refuses, rather than committing a stale tree, if they have not. The author "
        + "is the host's git identity; the message records that an agent session produced the change. If a "
        + "previous call was refused by the commit reviewer and one of its findings is wrong, `answers` disputes "
        + "it once with a reason quoting the work item or plan it relies on; the next review is told the finding "
        + "and the answer and must withdraw or keep it.";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """
        {"type":"object","properties":{
          "message":{"type":"string","description":"The commit message: subject line, blank line, body."},
          "paths":{"type":"array","items":{"type":"string"},"description":"Repository-relative files to commit. Default: what this session was approved to change, or, for an autopilot builder in its item's own worktree, every path uncommitted in it."},
          "answers":{"type":"array","items":{"type":"object","required":["finding","reason"],"properties":{
            "finding":{"type":"integer","description":"The finding's number, exactly as printed in the refusal of the commit this review answers."},
            "reason":{"type":"string","description":"Why that finding is wrong, in at most 500 characters; quote the work item or plan text it relies on."}
          }},"description":"Disputes one or more blocking findings from the LATEST failed review of this session's own commit. Each finding named here can only be answered once."}
        },"required":["message"],"additionalProperties":false}
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, WorkspaceGitServices git, CancellationToken cancellationToken)
    {
        var message = Text(call, "message");
        if (message is null)
        {
            return Rejected("missing_message", "message", "A commit needs a message.");
        }

        var named = Strings(call, "paths");
        List<string>? paths = null;
        if (named is { Count: > 0 })
        {
            paths = [];
            foreach (var entry in named)
            {
                if (GitToolArguments.NormalizePath(entry) is not { } path)
                {
                    return Rejected("bad_path", "paths", $"'{entry}': {GitToolArguments.PathRule}");
                }

                paths.Add(path);
            }
        }

        var outcome = await work.CommitAsync(call.SessionId, message, paths, cancellationToken).ConfigureAwait(false);
        if (outcome.Refused)
        {
            // #318: the same rule attempt_commit holds a message to, because both build the same trailer.
            return Rejected("subject_required", "message", outcome.Error ?? "Write a subject line first.");
        }

        if (outcome.NotSettled)
        {
            return Rejected("not_settled", "paths", outcome.Error ?? SessionGitWork.NotSettledMessage);
        }

        if (outcome.Nothing)
        {
            return Rejected(
                "nothing_to_commit",
                "paths",
                paths is null
                    ? "This session has not been approved to change any file yet, so there is nothing of its own to commit. Name `paths` to commit specific files."
                    : "None of the named files differs from the last commit.");
        }

        if (outcome.Sha is null)
        {
            return Rejected("commit_failed", "message", outcome.Error ?? "The commit failed.");
        }

        var branch = await git.Git.GetCurrentBranchAsync(cancellationToken).ConfigureAwait(false);
        return Changed("committed", new JsonObject
        {
            ["sha"] = outcome.Sha,
            ["files"] = outcome.Committed,
            ["branch"] = branch,
        });
    }
}

/// <summary>Pushes the current branch to origin with the host's credentials, setting the upstream when there is none.</summary>
public sealed class GitPushTool(SessionGit git) : GitToolBase(git)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "git_push";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Pushes the workspace repository's current branch to origin, using the host's own git credentials, and sets "
        + "its upstream when it has none. With `tag`, pushes that tag instead (push the branch first: a tag on a commit "
        + "origin has not seen is a release nobody can build); `tag` is checked against git's own ref-name rules and "
        + "must already exist locally, or it is refused before anything runs. Nothing is force-pushed. A failure says "
        + "whether it was the network (retry_safe true: nothing changed) or a refusal; a push whose answer was lost is "
        + "compared with origin, and when origin cannot be asked it is push_outcome_unknown: then set `check` to ask "
        + "again without pushing.";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """
        {"type":"object","properties":{
          "tag":{"type":"string","description":"A tag to push instead of the current branch."},
          "check":{"type":"boolean","description":"Push nothing: only ask origin whether the current branch (or the tag) already has the local commit, after a push_outcome_unknown."}
        },"additionalProperties":false}
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, WorkspaceGitServices git, CancellationToken cancellationToken)
    {
        var tag = TagName(call, "tag", out var refused);
        if (refused is not null)
        {
            return refused;
        }

        if (Flag(call, "check") == true)
        {
            return await CheckAsync(git, tag, cancellationToken).ConfigureAwait(false);
        }

        if (tag is not null)
        {
            var exists = await git.Runner.RunAsync(["rev-parse", "--verify", "refs/tags/" + tag], null, cancellationToken)
                .ConfigureAwait(false);
            if (exists.ExitCode != 0)
            {
                return Rejected("no_such_tag", "tag", $"No tag named '{tag}' exists in this repository, so there is nothing to push.");
            }

            var pushed = await git.Remote.PushTagAsync(tag, cancellationToken).ConfigureAwait(false);
            return pushed.Succeeded
                ? Changed("pushed", new JsonObject { ["tag"] = tag, ["detail"] = pushed.Detail })
                : RemoteFailed(pushed, "tag");
        }

        var branch = await git.Git.GetCurrentBranchAsync(cancellationToken).ConfigureAwait(false);
        var outcome = await git.Remote.PushAsync(cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            return RemoteFailed(outcome, "branch");
        }

        var status = await git.Tree.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return Changed("pushed", new JsonObject
        {
            ["branch"] = branch,
            ["upstream"] = status.Upstream,
            ["detail"] = outcome.Detail,
        });
    }

    /// <summary>Asks origin whether the local commit (or tag) is already there, pushing nothing (backlog 231).</summary>
    private static async Task<StudioToolResult> CheckAsync(WorkspaceGitServices git, string? tag, CancellationToken cancellationToken)
    {
        string remoteRef;
        string? upstreamBranch = null;
        GitCommandResult sha;
        if (tag is not null)
        {
            remoteRef = "refs/tags/" + tag;
            sha = await git.Runner.RunAsync(["rev-parse", "--verify", remoteRef], null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var branch = await git.Git.GetCurrentBranchAsync(cancellationToken).ConfigureAwait(false);
            remoteRef = "refs/heads/" + branch;
            var upstream = await git.Runner.RunAsync(["rev-parse", "--abbrev-ref", "@{u}"], null, cancellationToken).ConfigureAwait(false);
            upstreamBranch = upstream.ExitCode == 0 ? null : branch;
            sha = await git.Runner.RunAsync(["rev-parse", "HEAD"], null, cancellationToken).ConfigureAwait(false);
        }

        if (sha.ExitCode != 0)
        {
            return GitSaid("unknown_reference", tag is null ? "branch" : "tag", sha);
        }

        var outcome = await git.Remote.ComparePushAsync(
            new GitPendingPush(remoteRef, sha.StdOut.Trim(), upstreamBranch), cancellationToken).ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            return Changed("pushed", new JsonObject { ["ref"] = remoteRef, ["detail"] = outcome.Detail });
        }

        return RemoteFailed(outcome, tag is null ? "branch" : "tag");
    }

    /// <summary>
    /// A failed push in the Git page's own words (backlog 231), with a code an agent can branch on and whether doing
    /// it again is safe. A push of unknown outcome is never called failed and is never safe to repeat blindly.
    /// </summary>
    public static StudioToolResult RemoteFailed(GitSyncOutcome outcome, string field)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var code = outcome.Failure switch
        {
            GitSyncFailure.Network => "network_unreachable",
            GitSyncFailure.OutcomeUnknown => "push_outcome_unknown",
            GitSyncFailure.Refused => outcome.Refusal switch
            {
                GitRefusal.Rejected or GitRefusal.HookDeclined => "push_rejected",
                GitRefusal.Authentication => "authentication_failed",
                GitRefusal.Permission => "permission_denied",
                GitRefusal.NotFound => "repository_not_found",
                GitRefusal.Certificate => "certificate_not_trusted",
                _ => "origin_server_error",
            },
            _ => "push_failed",
        };
        var message = outcome.Error ?? "The push did not complete.";
        if (outcome.Failure == GitSyncFailure.OutcomeUnknown)
        {
            message += " Do not push again yet: call git_push with check set to true (and the same tag, if it was one), which only asks origin whether it arrived.";
        }

        return StudioToolResult.Failure(new JsonObject
        {
            ["refused"] = code,
            ["retry_safe"] = outcome.RetrySafe,
            ["errors"] = new JsonArray(new JsonObject
            {
                ["code"] = code,
                ["field"] = field,
                ["message"] = message,
            }),
        }.ToJsonString());
    }
}
