using Fravashi.Application.Notifications;
using Fravashi.Application.Repository;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Repository;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Web.Components.Sessions;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Fravashi.Web.Components.Code;

/// <summary>
/// Work item #366, "Follow the agent": while the agent panel is open and Follow is on, the editor shows what the
/// panel's conversation reads and changes. A file the agent reads, edits or writes inside this workspace's contained
/// copy opens as a preview tab; an Edit or Write waiting on approval shows as a read-only diff of the HOST file
/// against the proposed text, with Approve and Deny that decide that same approval; a change that lands is
/// highlighted once the host file has synced.
///
/// <para>
/// Nothing here decides anything about safety. The PreToolUse hook still fails closed and decides every call, the
/// diff's buttons go through <see cref="SessionApprovalResolution.Answer"/> exactly as the card's do, bound to the
/// request id the diff was built for, and every file shown is read through the page's root-bounded reader from the
/// host. The rules themselves (path mapping, the unsaved guard, the pause, the proposed text) are
/// <see cref="AgentFollowRules"/>, tested without rendering.
/// </para>
/// </summary>
public partial class CodePage
{
    /// <summary>The change waiting on approval that the diff shows, bound to its session and request id.</summary>
    private sealed record FollowDiff(
        LiveAgentSession Session, string RequestId, string Path, string Current, string? Proposed, string? Problem);

    /// <summary>What follow did not do and why; <see cref="Path"/> offers "Show it" when there is a file to show.</summary>
    private sealed record FollowNotice(string Text, string? Path);

    [Inject]
    private LiveSessionRegistry SessionRegistry { get; set; } = default!;

    private LiveAgentSession? _followSession;
    private int _followSeen;
    private (AgentFollowToolKind Kind, string Path)? _followCandidate;

    /// <summary>
    /// File tool calls that have started and not yet come back, by tool use id (work item #371). A call is acted on
    /// only once its result says it ran: a call the studio refused, or one that failed, never opens a tab.
    /// </summary>
    private readonly Dictionary<string, (AgentFollowToolKind Kind, string Path)> _followCalls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _followHandledRequests = new(StringComparer.Ordinal);
    private FollowDiff? _diff;
    private bool _diffNeedsPush;
    private bool _diffClosePending;
    private bool _deciding;
    private ElementReference _diffHost;
    private readonly AgentFollowPause _followPause = new();
    private CancellationTokenSource? _followResumeTimer;
    private FollowNotice? _followNotice;
    private string? _previewOpening;
    private readonly HashSet<string> _openWithoutFocus = new(StringComparer.Ordinal);
    private readonly List<string> _modelsToClose = [];
    private readonly Dictionary<string, (string Before, DateTimeOffset At)> _landingBefore = new(StringComparer.Ordinal);
    private (string Path, int First, int Last)? _pendingHighlight;
    private bool _followBusy;
    private bool _followAgain;

    private bool FollowOn => AgentPanel.IsOpen && AgentPanel.FollowAgent;

    private bool FollowPaused => FollowOn && _followPause.IsPaused(Clock.GetUtcNow());

    private bool FollowBandVisible => _followNotice is not null || FollowPaused;

    /// <summary>The conversation chosen for the panel, while it is still a live one of THIS workspace.</summary>
    private AgentSessionId? PanelSessionId =>
        AgentPanel.SessionId is { } id && SessionRegistry.Find(id) is { } session
        && string.Equals(session.WorkspaceId, SessionWorkspaceId, StringComparison.Ordinal)
            ? id
            : null;

    /// <summary>The conversation the panel shows: the chosen one, else the workspace's latest (as the pane resolves it).</summary>
    private LiveAgentSession? PanelSession()
    {
        if (string.IsNullOrWhiteSpace(SessionWorkspaceId))
        {
            return null;
        }

        return PanelSessionId is { } id ? SessionRegistry.Find(id) : SessionRegistry.LatestFor(SessionWorkspaceId);
    }

    private string OpenInSessionsHref => PanelSession() is { } session ? $"/sessions/{session.Id.Value:D}" : "/sessions";

    private void StartFollow()
    {
        SessionRegistry.SessionsChanged += OnFollowRegistryChanged;
        AttachFollowSession();
    }

    private void StopFollow()
    {
        SessionRegistry.SessionsChanged -= OnFollowRegistryChanged;
        if (_followSession is not null)
        {
            _followSession.Changed -= OnFollowSessionChanged;
            _followSession = null;
        }

        CancelFollowResume();
    }

    private void OnFollowRegistryChanged() => _ = InvokeAsync(AttachFollowAsync);

    private void OnFollowSessionChanged() => _ = InvokeAsync(ProcessFollowAsync);

    private async Task AttachFollowAsync()
    {
        AttachFollowSession();
        await ProcessFollowAsync();
    }

    /// <summary>
    /// Follows the panel's conversation while the panel is open. Only what happens from now on is followed: the
    /// transcript so far is history, not something to replay into the editor.
    /// </summary>
    private void AttachFollowSession()
    {
        var next = AgentPanel.IsOpen ? PanelSession() : null;
        if (ReferenceEquals(next, _followSession))
        {
            return;
        }

        if (_followSession is not null)
        {
            _followSession.Changed -= OnFollowSessionChanged;
        }

        _followSession = next;
        _followCandidate = null;
        _followCalls.Clear();
        _followHandledRequests.Clear();
        _followSeen = next?.Transcript.Count ?? 0;
        if (next is not null)
        {
            next.Changed += OnFollowSessionChanged;
        }
    }

    /// <summary>Runs the follow rules once per burst of session events; a burst during a run runs them again after it.</summary>
    private async Task ProcessFollowAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_followBusy)
        {
            _followAgain = true;
            return;
        }

        _followBusy = true;
        var changed = false;
        try
        {
            do
            {
                _followAgain = false;
                changed |= await FollowOnceAsync();
            }
            while (_followAgain && !_disposed);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is going away; there is no editor left to follow into.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Following the agent in the editor failed.");
        }
        finally
        {
            _followBusy = false;
        }

        if (changed && !_disposed)
        {
            StateHasChanged();
        }
    }

    private async Task<bool> FollowOnceAsync()
    {
        var changed = false;
        var session = _followSession;
        if (session is not null)
        {
            var transcript = session.Transcript;
            var changing = new List<string>();
            for (var i = Math.Min(_followSeen, transcript.Count); i < transcript.Count; i++)
            {
                var step = AgentFollowRules.Track(_followCalls, transcript[i], SessionWorkingDirectory);
                switch (step?.Outcome)
                {
                    case AgentFollowCallOutcome.Started:
                        if (step.Kind is AgentFollowToolKind.Edit or AgentFollowToolKind.Write)
                        {
                            changing.Add(step.Path);
                        }

                        break;

                    case AgentFollowCallOutcome.Ran:
                        _followCandidate = (step.Kind, step.Path);
                        break;

                    case AgentFollowCallOutcome.Refused:
                        // Work item #371: refused or failed, so nothing changed and there is nothing to show.
                        if (_followCandidate is { } same && same.Path == step.Path)
                        {
                            _followCandidate = null;
                        }

                        if (_diff?.Path != step.Path)
                        {
                            _landingBefore.Remove(step.Path);
                        }

                        break;
                }
            }

            _followSeen = transcript.Count;

            // The host file's text before a change runs, so the lines it touched can be highlighted once it lands.
            if (FollowOn)
            {
                foreach (var path in changing.Distinct(StringComparer.Ordinal))
                {
                    var before = await ReadForFollowAsync(path);
                    RememberLanding(path, before is { IsTruncated: false } text ? text.Content : string.Empty);
                }
            }
        }

        var pending = session?.Pending ?? [];

        // A diff goes the moment its approval stops waiting (decided on the card, timed out), or follow stops.
        if (_diff is { } shown
            && (!ReferenceEquals(shown.Session, session) || !FollowOn
                || pending.All(p => !string.Equals(p.RequestId, shown.RequestId, StringComparison.Ordinal))))
        {
            ClearDiff(ApprovedInTranscript(shown));
            changed = true;
        }

        if (session is null || !FollowOn)
        {
            return changed;
        }

        var now = Clock.GetUtcNow();
        if (_followPause.IsPaused(now))
        {
            // Kept for when the pause ends: the newest file tool and whatever is still waiting are acted on then.
            ScheduleFollowResume(now);
            return changed;
        }

        if (_diff is null)
        {
            foreach (var request in pending)
            {
                if (_followHandledRequests.Contains(request.RequestId)
                    || AgentFollowRules.TargetOf(request, SessionWorkingDirectory) is not { } target)
                {
                    continue;
                }

                _followHandledRequests.Add(request.RequestId);
                if (_followCandidate is { } same && same.Path == target)
                {
                    _followCandidate = null;
                }

                // The diff stands for this call, so its result does not open the file a second time.
                foreach (var id in _followCalls
                             .Where(c => string.Equals(c.Key, request.ToolUseId, StringComparison.Ordinal) || c.Value.Path == target)
                             .Select(c => c.Key).ToList())
                {
                    _followCalls.Remove(id);
                }

                var decision = AgentFollowRules.Decide(true, false, target, Workspace.ActivePath, IsUnsaved);
                if (decision == AgentFollowDecision.Follow)
                {
                    await ShowDiffAsync(session, request, target);
                }
                else
                {
                    Notice(decision, target);
                }

                return true;
            }
        }

        if (_diff is null && _followCandidate is { } candidate)
        {
            _followCandidate = null;
            var decision = AgentFollowRules.Decide(true, false, candidate.Path, Workspace.ActivePath, IsUnsaved);
            if (decision == AgentFollowDecision.Follow)
            {
                await FollowOpenAsync(candidate.Path, candidate.Kind);
            }
            else
            {
                Notice(decision, candidate.Path);
            }

            changed = true;
        }

        return changed;
    }

    private void Notice(AgentFollowDecision decision, string path)
    {
        _followNotice = decision switch
        {
            AgentFollowDecision.TargetUnsaved => new FollowNotice(
                $"The agent is working on {path}, which has unsaved changes here, so follow left it alone.", path),
            AgentFollowDecision.ActiveUnsaved => new FollowNotice(
                $"The agent is working on {path}. Follow did not switch away from your unsaved changes.", path),
            _ => _followNotice,
        };
    }

    /// <summary>
    /// Opens the file the agent read or changed as the preview tab, once the call's result says it ran (work item
    /// #371). For an edit or a write, the text remembered when the call started is compared with the host file's:
    /// a change that has already landed is highlighted now, one still syncing when it lands.
    /// </summary>
    private async Task FollowOpenAsync(string path, AgentFollowToolKind kind)
    {
        var file = await ReadForFollowAsync(path);
        var changes = kind is AgentFollowToolKind.Edit or AgentFollowToolKind.Write;
        if (file is null)
        {
            if (changes && !_landingBefore.ContainsKey(path))
            {
                RememberLanding(path, string.Empty);
            }

            return; // a file being created: it opens when it appears on the host (OpenLandedNewFileAsync)
        }

        await FollowOpenFileAsync(path);
        if (!changes)
        {
            return;
        }

        var now = file.IsTruncated ? string.Empty : file.Content;
        if (!_landingBefore.TryGetValue(path, out var landing))
        {
            RememberLanding(path, now);
        }
        else if (!file.IsTruncated
                 && !string.Equals(landing.Before, now, StringComparison.Ordinal)
                 && string.Equals(Workspace.ActivePath, path, StringComparison.Ordinal))
        {
            _landingBefore.Remove(path);
            if (AgentFollowRules.ChangedLines(landing.Before, now) is { } lines)
            {
                _pendingHighlight = (path, lines.First, lines.Last);
            }
        }
    }

    private async Task FollowOpenFileAsync(string path)
    {
        if (string.Equals(Workspace.ActivePath, path, StringComparison.Ordinal) && _view.Kind != ViewKind.None)
        {
            return;
        }

        _previewOpening = path;
        _openWithoutFocus.Add(path); // never take focus from the composer the person may be typing in
        try
        {
            await OpenFromPathAsync(path);
        }
        finally
        {
            _previewOpening = null;
        }

        if (!string.Equals(Workspace.ActivePath, path, StringComparison.Ordinal))
        {
            // Work item #371: the file was gone by the time the editor read it, so no tab was opened for it.
            _openWithoutFocus.Remove(path);
            return;
        }

        Nav.NavigateTo($"/code/{path}", replace: true);
    }

    /// <summary>
    /// Work item #371: a preview tab is follow's, so one whose file does not exist closes itself instead of showing
    /// the missing-file notice. True when <paramref name="path"/> was being opened by follow (no tab is opened for
    /// it) or is the preview tab (it is closed and the editor moves to a neighbour); false for an ordinary tab, or a
    /// preview holding unsaved work, which keep the notice.
    /// </summary>
    private async Task<bool> DropMissingPreviewAsync(string path, bool wasPreviewActive)
    {
        if (string.Equals(_previewOpening, path, StringComparison.Ordinal))
        {
            _previewOpening = null;
            _previewActive = wasPreviewActive;
            return true;
        }

        if (Workspace.Open.FirstOrDefault(f => f.Path == path) is not { IsPreview: true } || IsUnsaved(path))
        {
            return false;
        }

        var wasActive = string.Equals(Workspace.ActivePath, path, StringComparison.Ordinal);
        Workspace.Close(path);
        ForgetFileState(path);
        _landingBefore.Remove(path);
        _modelsToClose.Add(path);
        _pendingPersist = true;
        if (Workspace.ActivePath is not { } next)
        {
            _view = new ActiveView(ViewKind.None);
            Nav.NavigateTo("/code", replace: true);
        }
        else if (wasActive || _view.Kind == ViewKind.None)
        {
            await OpenFromPathAsync(next);
            Nav.NavigateTo($"/code/{next}", replace: true);
        }
        else
        {
            _previewActive = wasPreviewActive;
            Nav.NavigateTo($"/code/{next}", replace: true);
        }

        return true;
    }

    /// <summary>
    /// The tab for a file being opened: the preview tab when follow is opening it, an ordinary one otherwise. A
    /// preview with unsaved changes is promoted first, so the tab replaced is never one holding the person's work.
    /// </summary>
    private void OpenTab(string path, string name)
    {
        if (!string.Equals(_previewOpening, path, StringComparison.Ordinal))
        {
            Workspace.OpenAt(path, name);
            return;
        }

        _previewOpening = null;
        if (Workspace.PreviewPath is { } preview && preview != path && IsUnsaved(preview))
        {
            Workspace.Promote(preview);
        }

        if (Workspace.OpenAsPreview(path, name) is { } replaced)
        {
            ForgetFileState(replaced);
            _modelsToClose.Add(replaced);
        }
    }

    private async Task ShowDiffAsync(LiveAgentSession session, PendingToolApproval request, string path)
    {
        var file = await ReadForFollowAsync(path);
        string? problem = null;
        string? current = null;
        if (file is { IsTruncated: true } || file?.Content.Contains('\0', StringComparison.Ordinal) == true)
        {
            problem = "This file is binary or too large to show as a diff.";
        }
        else
        {
            current = file?.Content;
        }

        var change = problem is null ? AgentFollowRules.ProposeFor(request, current) : AgentProposedChange.Cannot(problem);
        _diff = new FollowDiff(session, request.RequestId, path, current ?? string.Empty, change.Proposed, change.Problem);
        _diffNeedsPush = change.Problem is null;
        _diffClosePending = false;
        if (file is not null)
        {
            // Beneath the diff, so the file is on screen when the decision is made and the change lands.
            await FollowOpenFileAsync(path);
        }
    }

    /// <summary>
    /// Takes the diff away. A change that was (or may have been) approved is remembered against the text the diff
    /// was built on, so the lines it touched are highlighted when the host file syncs.
    /// </summary>
    private void ClearDiff(bool? approved)
    {
        if (_diff is not { } diff)
        {
            return;
        }

        if (approved != false)
        {
            RememberLanding(diff.Path, diff.Current);
        }

        _diff = null;
        _diffNeedsPush = false;
        _diffClosePending = true;
    }

    /// <summary>What the transcript says the person decided about the diff's request, or null when it does not say yet.</summary>
    private static bool? ApprovedInTranscript(FollowDiff diff) =>
        diff.Session.Transcript.OfType<ToolApprovalResolved>()
            .LastOrDefault(r => string.Equals(r.RequestId, diff.RequestId, StringComparison.Ordinal))?.Approved;

    /// <summary>
    /// Approve or Deny in the diff header. The SAME resolution as the card (<see cref="SessionApprovalResolution"/>),
    /// for the request id this diff was built for on the session it came from; refused when that request is no
    /// longer pending, so the diff can never decide a different approval.
    /// </summary>
    private void DecideFromDiff(bool approved)
    {
        if (_diff is not { } diff || _deciding)
        {
            return;
        }

        _deciding = true;
        try
        {
            var answered = SessionApprovalResolution.Answer(SessionRegistry, diff.Session, diff.RequestId, approved, reason: null);
            if (!answered)
            {
                Notifications.Add(
                    "That change is no longer waiting for a decision: it was decided in the panel or timed out.",
                    NotificationSeverity.Info);
            }

            ClearDiff(answered ? approved : ApprovedInTranscript(diff));
        }
        finally
        {
            _deciding = false;
        }
    }

    private void RememberLanding(string path, string before) => _landingBefore[path] = (before, Clock.GetUtcNow());

    /// <summary>An open file's new host text arrived: highlight what a followed change touched.</summary>
    private void FollowedFileLanded(string path, string content)
    {
        if (!_landingBefore.Remove(path, out var landing)
            || Clock.GetUtcNow() - landing.At > AgentFollowRules.LandingWindow
            || !FollowOn)
        {
            return;
        }

        if (AgentFollowRules.ChangedLines(landing.Before, content) is { } lines)
        {
            _pendingHighlight = (path, lines.First, lines.Last);
        }
    }

    /// <summary>A file follow saw the agent create has appeared on the host: open it and highlight it.</summary>
    private async Task OpenLandedNewFileAsync(string path, RepositoryChangeKind kind)
    {
        if (kind == RepositoryChangeKind.Deleted
            || !_landingBefore.TryGetValue(path, out var landing)
            || landing.Before.Length != 0
            || Clock.GetUtcNow() - landing.At > AgentFollowRules.LandingWindow
            || !FollowOn
            || _followPause.IsPaused(Clock.GetUtcNow())
            || _diff is not null
            || AgentFollowRules.Decide(true, false, path, Workspace.ActivePath, IsUnsaved) != AgentFollowDecision.Follow)
        {
            return;
        }

        _landingBefore.Remove(path);
        var file = await ReadForFollowAsync(path);
        if (file is not { IsTruncated: false })
        {
            return;
        }

        await FollowOpenFileAsync(path);
        if (AgentFollowRules.ChangedLines(string.Empty, file.Content) is { } lines)
        {
            _pendingHighlight = (path, lines.First, lines.Last);
        }

        StateHasChanged();
    }

    private async Task<RepositoryFile?> ReadForFollowAsync(string path)
    {
        try
        {
            return await Reader.ReadFileAsync(path);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
        {
            Logger.LogDebug(ex, "Follow could not read {Path} from the host.", path);
            return null;
        }
    }

    // ---- the pause after the person acts in the editor ----

    /// <summary>Raised (throttled) by codeEditor.js when the person types, scrolls or clicks in the editor.</summary>
    [JSInvokable]
    public Task EditorPersonActivity() => InvokeAsync(() =>
    {
        var wasPaused = FollowPaused;
        NotePersonActivity();
        if (!wasPaused && FollowPaused)
        {
            StateHasChanged();
        }
    });

    private void NotePersonActivity()
    {
        var now = Clock.GetUtcNow();
        _followPause.PersonActed(now);
        if (FollowOn)
        {
            ScheduleFollowResume(now);
        }
    }

    /// <summary>One timer for the end of the pause: when it ends, whatever follow kept waiting is acted on.</summary>
    private void ScheduleFollowResume(DateTimeOffset now)
    {
        if (_followPause.Remaining(now) is not { } remaining)
        {
            return;
        }

        CancelFollowResume();
        var timer = new CancellationTokenSource();
        _followResumeTimer = timer;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(remaining + TimeSpan.FromMilliseconds(50), Clock, timer.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await InvokeAsync(async () =>
            {
                await ProcessFollowAsync();
                if (!_disposed)
                {
                    StateHasChanged();
                }
            });
        });
    }

    private void CancelFollowResume()
    {
        if (_followResumeTimer is { } timer)
        {
            _followResumeTimer = null;
            timer.Cancel();
            timer.Dispose();
        }
    }

    private async Task ResumeFollowAsync()
    {
        _followPause.Resume();
        CancelFollowResume();
        await ProcessFollowAsync();
    }

    private void ToggleFollow()
    {
        var on = !AgentPanel.FollowAgent;
        _followHandledRequests.Clear();
        _followNotice = null;
        _followCandidate = null;
        _followCalls.Clear();
        if (on)
        {
            _followPause.Resume();
        }

        AgentPanel.SetFollow(on);
    }

    private void ShowNoticedFile(string path)
    {
        _followNotice = null;
        NavigateToFile(path);
    }

    // ---- pushing to the editor (bounded; re-sent after a reconnection) ----

    /// <summary>Called from OnAfterRenderAsync: the diff, closed preview buffers and the landing highlight.</summary>
    private async Task PushFollowAsync()
    {
        if (_diffClosePending && await TryEditorCallAsync("fravashiCode.disposeDiff"))
        {
            _diffClosePending = false;
        }

        if (_diff is { Problem: null } diff && _diffNeedsPush)
        {
            _diffNeedsPush = false;
            var shown = await TryEditorCallAsync("fravashiCode.initDiff", _diffHost)
                && await TryEditorCallAsync("fravashiCode.openDiff", diff.Path, diff.Current, diff.Proposed);
            if (!shown && ReferenceEquals(_diff, diff))
            {
                _diffNeedsPush = true; // sent again on the next render, and on reconnection
            }
        }

        foreach (var closed in _modelsToClose.ToList())
        {
            if (Workspace.Open.Any(f => f.Path == closed) || await TryEditorCallAsync("fravashiCode.close", closed))
            {
                _modelsToClose.Remove(closed);
            }
        }

        if (_pendingHighlight is { } highlight && _editorInitialized
            && string.Equals(highlight.Path, Workspace.ActivePath, StringComparison.Ordinal)
            && await TryEditorCallAsync(
                "fravashiCode.highlightLines", highlight.Path, highlight.First, highlight.Last,
                (int)AgentFollowRules.HighlightFor.TotalMilliseconds))
        {
            _pendingHighlight = null;
        }
    }

    /// <summary>
    /// The window connected again: a diff pushed while it was away was never delivered (Blazor does not replay a
    /// JavaScript call to a reconnected window), so it is sent again; a highlight not yet confirmed is still pending.
    /// </summary>
    private void FollowReconnected() => _ = InvokeAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        if (_diff is { Problem: null })
        {
            _diffNeedsPush = true;
        }

        StateHasChanged();
    });

    /// <summary>A JavaScript call bounded by <see cref="ApplyTimeout"/>; false when it was not confirmed.</summary>
    private async Task<bool> TryEditorCallAsync(string identifier, params object?[] args)
    {
        try
        {
            await JS.InvokeVoidAsync(identifier, ApplyTimeout, args);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or JSException or JSDisconnectedException or InvalidOperationException)
        {
            return false;
        }
    }
}
