using System.Text.Json;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Web.Services;

/// <summary>What a followed tool call does to a file (work item #366).</summary>
public enum AgentFollowToolKind
{
    /// <summary>Not a file tool follow cares about.</summary>
    None,

    /// <summary>The agent reads the file.</summary>
    Read,

    /// <summary>The agent edits part of an existing file (Edit, MultiEdit).</summary>
    Edit,

    /// <summary>The agent writes the whole file.</summary>
    Write,
}

/// <summary>What follow may do about one tool call, given the editor's state (work item #366).</summary>
public enum AgentFollowDecision
{
    /// <summary>Open the file (or show the proposed change) in the editor.</summary>
    Follow,

    /// <summary>Follow is switched off in the panel header.</summary>
    Off,

    /// <summary>The path is not inside this workspace's contained copy, so follow ignores it.</summary>
    Ignored,

    /// <summary>The person acted in the editor in the last <see cref="AgentFollowRules.PauseWindow"/>.</summary>
    Paused,

    /// <summary>The file the agent works on has unsaved changes here; it is never replaced.</summary>
    TargetUnsaved,

    /// <summary>The person has unsaved changes in the file on screen; follow does not take the editor away.</summary>
    ActiveUnsaved,
}

/// <summary>What one transcript event did to a file tool call follow is tracking (work item #371).</summary>
public enum AgentFollowCallOutcome
{
    /// <summary>The call started; nothing is shown yet, because it may still be refused or fail.</summary>
    Started,

    /// <summary>The call's result came back as a success: follow may open the file.</summary>
    Ran,

    /// <summary>The studio refused the call, or it failed: follow opens nothing for it.</summary>
    Refused,
}

/// <summary>One step of a tracked file tool call: what happened, to which kind of call, on which workspace path.</summary>
public sealed record AgentFollowCallStep(AgentFollowCallOutcome Outcome, AgentFollowToolKind Kind, string Path);

/// <summary>
/// The proposed text of a pending change, or why it cannot be shown. Never a guess: when the change cannot be
/// placed in the current file exactly as the agent's tool would place it, <see cref="Problem"/> says so and the
/// approval card's own view stands instead of a wrong diff.
/// </summary>
public sealed record AgentProposedChange(string? Proposed, string? Problem)
{
    public static AgentProposedChange Ok(string proposed) => new(proposed, null);

    public static AgentProposedChange Cannot(string problem) => new(null, problem);
}

/// <summary>
/// The rules of "Follow the agent" on the Code page (work item #366), kept out of the page so they are tested
/// without rendering. None of this decides anything about safety: the PreToolUse hook still decides every call,
/// and follow only chooses what the editor shows. What follow shows is read from the HOST file through the
/// page's root-bounded reader, never from the agent's text.
/// </summary>
public static class AgentFollowRules
{
    /// <summary>How long follow waits after the person types, scrolls or switches tabs in the editor.</summary>
    public static readonly TimeSpan PauseWindow = TimeSpan.FromSeconds(20);

    /// <summary>How long the lines a landed change touched stay highlighted.</summary>
    public static readonly TimeSpan HighlightFor = TimeSpan.FromSeconds(4);

    /// <summary>How long a change that was shown is waited for on the host before follow forgets it.</summary>
    public static readonly TimeSpan LandingWindow = TimeSpan.FromMinutes(2);

    /// <summary>The file tools follow knows, by the CLI's own names.</summary>
    public static AgentFollowToolKind KindOf(string? toolName) => toolName switch
    {
        "Read" => AgentFollowToolKind.Read,
        "Edit" or "MultiEdit" => AgentFollowToolKind.Edit,
        "Write" => AgentFollowToolKind.Write,
        _ => AgentFollowToolKind.None,
    };

    /// <summary>
    /// The workspace-relative path of <paramref name="path"/>, a path inside the contained copy at
    /// <paramref name="copyRoot"/> (<c>/workspaces/{id}</c>), or null. Only a plain absolute path under the copy
    /// maps: anything outside it, any <c>.</c> or <c>..</c> segment, an empty segment, a backslash, a NUL, or a
    /// path into <c>.git</c> (which never syncs) is ignored, never opened. Purely lexical on purpose: follow opens
    /// nothing the page's own root-bounded reader would not, and a path that needs resolving is not followed.
    /// </summary>
    public static string? MapToWorkspace(string? copyRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(copyRoot) || string.IsNullOrEmpty(path))
        {
            return null;
        }

        var root = copyRoot.TrimEnd('/');
        if (root.Length == 0 || !root.StartsWith('/'))
        {
            return null;
        }

        if (path.Contains('\0', StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal)
            || !path.StartsWith(root + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var relative = path[(root.Length + 1)..];
        var segments = relative.Split('/');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".."))
        {
            return null;
        }

        return string.Equals(segments[0], ".git", StringComparison.Ordinal) ? null : relative;
    }

    /// <summary>
    /// Tracks file tool calls through the transcript (work item #371), keeping the started ones in
    /// <paramref name="calls"/> by tool use id. A call is <see cref="AgentFollowCallOutcome.Ran"/> only when its
    /// result came back as a success. A result marked as an error (a denial arrives that way, "Refused by the
    /// studio" on the card) or a <see cref="ToolCallRefused"/> for the same tool and path (a refusal nobody was asked
    /// about) makes it <see cref="AgentFollowCallOutcome.Refused"/>. Null for an event that is not about a tracked
    /// file call.
    /// </summary>
    public static AgentFollowCallStep? Track(
        IDictionary<string, (AgentFollowToolKind Kind, string Path)> calls, AgentEvent agentEvent, string? copyRoot)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(agentEvent);

        switch (agentEvent)
        {
            case ToolCallStarted started
                when KindOf(started.ToolName) is var kind and not AgentFollowToolKind.None
                     && MapToWorkspace(copyRoot, started.Detail) is { } path:
                calls[started.ToolUseId] = (kind, path);
                return new AgentFollowCallStep(AgentFollowCallOutcome.Started, kind, path);

            case ToolCallCompleted completed when calls.TryGetValue(completed.ToolUseId, out var call):
                calls.Remove(completed.ToolUseId);
                return new AgentFollowCallStep(
                    completed.Success ? AgentFollowCallOutcome.Ran : AgentFollowCallOutcome.Refused, call.Kind, call.Path);

            case ToolCallRefused refused
                when KindOf(refused.ToolName) is var refusedKind and not AgentFollowToolKind.None
                     && MapToWorkspace(copyRoot, refused.Detail) is { } refusedPath:
                var ids = calls.Where(c => c.Value.Kind == refusedKind && c.Value.Path == refusedPath)
                    .Select(c => c.Key).ToList();
                if (ids.Count == 0)
                {
                    return null;
                }

                foreach (var id in ids)
                {
                    calls.Remove(id);
                }

                return new AgentFollowCallStep(AgentFollowCallOutcome.Refused, refusedKind, refusedPath);

            default:
                return null;
        }
    }

    /// <summary>
    /// Whether follow acts on a call for <paramref name="target"/> (a workspace-relative path, null when it did
    /// not map). Unsaved work wins over follow every time: the target's own buffer is never replaced, and the
    /// editor is not taken away from another file the person has unsaved changes in.
    /// </summary>
    public static AgentFollowDecision Decide(
        bool followOn, bool paused, string? target, string? activePath, Func<string, bool> isUnsaved)
    {
        ArgumentNullException.ThrowIfNull(isUnsaved);

        if (!followOn)
        {
            return AgentFollowDecision.Off;
        }

        if (target is null)
        {
            return AgentFollowDecision.Ignored;
        }

        if (paused)
        {
            return AgentFollowDecision.Paused;
        }

        if (isUnsaved(target))
        {
            return AgentFollowDecision.TargetUnsaved;
        }

        return activePath is not null && !string.Equals(activePath, target, StringComparison.Ordinal) && isUnsaved(activePath)
            ? AgentFollowDecision.ActiveUnsaved
            : AgentFollowDecision.Follow;
    }

    /// <summary>
    /// The text an Edit would leave: <paramref name="current"/> with <paramref name="oldString"/> replaced by
    /// <paramref name="newString"/>, once, or everywhere with <paramref name="replaceAll"/>. Like the tool itself,
    /// it refuses an old text that is not there or, without replace-all, is there more than once.
    /// </summary>
    public static AgentProposedChange ProposeEdit(string current, string oldString, string newString, bool replaceAll)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(oldString);
        ArgumentNullException.ThrowIfNull(newString);

        if (oldString.Length == 0)
        {
            return current.Length == 0
                ? AgentProposedChange.Ok(newString)
                : AgentProposedChange.Cannot(NotPlaced);
        }

        var count = Occurrences(current, oldString);
        if (count == 0)
        {
            return AgentProposedChange.Cannot(NotPlaced);
        }

        if (count > 1 && !replaceAll)
        {
            return AgentProposedChange.Cannot(
                $"The text this change replaces appears {count} times in the current file, so where it lands is not certain.");
        }

        if (replaceAll)
        {
            return AgentProposedChange.Ok(current.Replace(oldString, newString, StringComparison.Ordinal));
        }

        var at = current.IndexOf(oldString, StringComparison.Ordinal);
        return AgentProposedChange.Ok(string.Concat(current.AsSpan(0, at), newString, current.AsSpan(at + oldString.Length)));
    }

    /// <summary>
    /// The proposed text of a pending Edit, MultiEdit or Write against the host file's current text
    /// (<paramref name="current"/>, null when the file does not exist). Arguments are read from the request's raw
    /// copy where the display copy was cut (<see cref="PendingToolApproval.CutArguments"/>); an argument too long
    /// for the card to carry in full is not guessed at.
    /// </summary>
    public static AgentProposedChange ProposeFor(PendingToolApproval request, string? current)
    {
        ArgumentNullException.ThrowIfNull(request);

        switch (request.ToolName)
        {
            case "Write":
                return Argument(request, "content") is { } content
                    ? AgentProposedChange.Ok(content)
                    : AgentProposedChange.Cannot(TooLong);

            case "Edit":
                if (current is null)
                {
                    return AgentProposedChange.Cannot("The file this change edits is not on the host yet.");
                }

                if (Argument(request, "old_string") is not { } oldString || Argument(request, "new_string") is not { } newString)
                {
                    return AgentProposedChange.Cannot(TooLong);
                }

                return ProposeEdit(current, oldString, newString, IsTrue(Argument(request, "replace_all")));

            case "MultiEdit":
                if (current is null)
                {
                    return AgentProposedChange.Cannot("The file this change edits is not on the host yet.");
                }

                if (Argument(request, "edits") is not { } editsJson || ParseEdits(editsJson) is not { } edits)
                {
                    return AgentProposedChange.Cannot("The edits in this change could not be read.");
                }

                var text = current;
                foreach (var (oldText, newText, all) in edits)
                {
                    var step = ProposeEdit(text, oldText, newText, all);
                    if (step.Proposed is null)
                    {
                        return step;
                    }

                    text = step.Proposed;
                }

                return AgentProposedChange.Ok(text);

            default:
                return AgentProposedChange.Cannot("Follow shows only Edit, MultiEdit and Write as a diff.");
        }
    }

    /// <summary>
    /// The workspace-relative file a pending change targets, or null. The path must not be one the display copy
    /// cut (a path that long is not followed) and must map by <see cref="MapToWorkspace"/>.
    /// </summary>
    public static string? TargetOf(PendingToolApproval request, string? copyRoot)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (KindOf(request.ToolName) is not (AgentFollowToolKind.Edit or AgentFollowToolKind.Write)
            || (request.CutArguments ?? []).Any(c => c.Name == "file_path")
            || !request.Input.TryGetValue("file_path", out var path))
        {
            return null;
        }

        return MapToWorkspace(copyRoot, path);
    }

    /// <summary>
    /// The 1-based first and last line of <paramref name="after"/> that differ from <paramref name="before"/>,
    /// by common leading and trailing lines; null when the two are the same. A pure deletion names the line where
    /// the removed text was.
    /// </summary>
    public static (int First, int Last)? ChangedLines(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return null;
        }

        var old = Lines(before);
        var now = Lines(after);
        var head = 0;
        while (head < old.Length && head < now.Length && old[head] == now[head])
        {
            head++;
        }

        var tail = 0;
        while (tail < old.Length - head && tail < now.Length - head
               && old[old.Length - 1 - tail] == now[now.Length - 1 - tail])
        {
            tail++;
        }

        var first = head + 1;
        var last = Math.Max(first, now.Length - tail);
        var lineCount = Math.Max(1, now.Length);
        return (Math.Min(first, lineCount), Math.Min(last, lineCount));
    }

    private const string NotPlaced =
        "The text this change replaces is not in the current file, so the diff would be wrong.";

    private const string TooLong =
        "Part of this change is too long to show here.";

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(value, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += value.Length;
        }

        return count;
    }

    private static string? Argument(PendingToolApproval request, string name)
    {
        if ((request.CutArguments ?? []).FirstOrDefault(c => c.Name == name) is { } cut)
        {
            return cut.Full;
        }

        return request.Input.TryGetValue(name, out var value) ? value : null;
    }

    private static bool IsTrue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<(string Old, string New, bool All)>? ParseEdits(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var edits = new List<(string, string, bool)>();
            foreach (var edit in document.RootElement.EnumerateArray())
            {
                if (edit.ValueKind != JsonValueKind.Object
                    || !edit.TryGetProperty("old_string", out var oldText) || oldText.ValueKind != JsonValueKind.String
                    || !edit.TryGetProperty("new_string", out var newText) || newText.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var all = edit.TryGetProperty("replace_all", out var flag) && flag.ValueKind == JsonValueKind.True;
                edits.Add((oldText.GetString()!, newText.GetString()!, all));
            }

            return edits;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The pause after the person acts in the editor (work item #366): follow stays out of the way for
/// <see cref="AgentFollowRules.PauseWindow"/> after the last keystroke, scroll or tab switch, and "resume" ends
/// it at once.
/// </summary>
public sealed class AgentFollowPause
{
    private DateTimeOffset? _until;

    /// <summary>The person typed, scrolled or switched tabs at <paramref name="now"/>.</summary>
    public void PersonActed(DateTimeOffset now) => _until = now + AgentFollowRules.PauseWindow;

    public bool IsPaused(DateTimeOffset now) => _until is { } until && now < until;

    /// <summary>How long the pause still runs, or null when it does not.</summary>
    public TimeSpan? Remaining(DateTimeOffset now) => IsPaused(now) ? _until!.Value - now : null;

    public void Resume() => _until = null;
}
