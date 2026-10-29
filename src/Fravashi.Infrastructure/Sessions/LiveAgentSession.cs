using System.Text;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// One session as the UI sees it: its transcript so far, the message currently being typed, the run
/// counters, and anything waiting on the user. It lives in the studio process, not in a circuit, so
/// closing the window and coming back finds the same object with the whole conversation in it
/// (decision 6).
/// </summary>
public sealed class LiveAgentSession
{
    private readonly Lock _gate = new();
    private readonly List<AgentEvent> _transcript = [];
    private readonly List<PendingToolApproval> _pending = [];
    private readonly StringBuilder _partial = new();

    // Backlog 117: how many times each call (tool name + subject) has been refused automatically in this
    // session. Ordinal on both halves, because a case variant of a command is a different command.
    private readonly Dictionary<(string Tool, string Detail), int> _automaticRefusals = [];

    internal LiveAgentSession(
        AgentSessionId id, string workspaceId, string title, DateTimeOffset startedUtc, string? customTitle = null)
    {
        Id = id;
        WorkspaceId = workspaceId;
        DerivedTitle = title;
        CustomTitle = string.IsNullOrWhiteSpace(customTitle) ? null : customTitle.Trim();
        StartedUtc = startedUtc;
        ActivatedUtc = startedUtc;
        State = AgentSessionState.Running;
    }

    /// <summary>Raised after every change, for any surface rendering this session.</summary>
    public event Action? Changed;

    public AgentSessionId Id { get; }

    public string WorkspaceId { get; }

    /// <summary>The title derived from the first prompt: the fallback, which a rename never overwrites.</summary>
    public string DerivedTitle { get; }

    /// <summary>A title the owner chose (backlog 109), or null until they rename the conversation.</summary>
    public string? CustomTitle { get; private set; }

    /// <summary>What every surface shows: the owner's chosen title, else the derived one.</summary>
    public string Title => CustomTitle ?? DerivedTitle;

    public DateTimeOffset StartedUtc { get; }

    /// <summary>
    /// When this conversation last came to life in this studio: its start, or the moment it was resumed.
    /// A resumed conversation keeps its original <see cref="StartedUtc"/> (it is the same conversation, in the
    /// same place in the list), so "the workspace's latest conversation" is measured by this instead, or a
    /// conversation resumed from further down the list would never be the one a pane shows.
    /// </summary>
    public DateTimeOffset ActivatedUtc { get; private set; }

    /// <summary>
    /// The conversation's usage as it stood when it was resumed. The CLI counts from zero in every new
    /// process, so each reading a resumed process reports is added to this before anyone sees it; otherwise
    /// the session's one summary row would be overwritten with the second half of its own spending.
    /// </summary>
    private AgentStatsSnapshot _usageCarried = AgentStatsSnapshot.Empty;

    public DateTimeOffset? EndedUtc { get; private set; }

    public AgentSessionState State { get; private set; }

    /// <summary>The CLI's own session id, once it has announced itself. What <c>--resume</c> needs.</summary>
    public string? ProviderSessionId { get; private set; }

    /// <summary>The model the CLI reported for the run, which may differ from the one asked for.</summary>
    public string? Model { get; private set; }

    /// <summary>
    /// The effort the studio last started or moved the process at (requested 2026-10-22), null for the CLI's
    /// default. Known from the studio's own <see cref="EffortChosen"/> rows, because the CLI reports no effort.
    /// </summary>
    public AgentEffort? Effort { get; private set; }

    /// <summary>
    /// True for a GRAPH-ONLY conversation, "Notes and items only" (work item #352): it reads the folder's copy and
    /// writes only the tracker and the wiki. Known from the studio's own <see cref="GraphOnlyChosen"/> row, and it
    /// only ever goes from false to true: nothing here can set it back, so a graph-only conversation never becomes an
    /// editing one.
    /// </summary>
    public bool IsGraphOnly { get; private set; }

    /// <summary>
    /// True for the STUDIO ASSISTANT (work item #359), Home's general assistant: known from the studio's own
    /// <see cref="AssistantChosen"/> row, and like <see cref="IsGraphOnly"/> it only ever goes from false to true.
    /// </summary>
    public bool IsAssistant { get; private set; }

    public AgentStatsSnapshot Stats { get; private set; } = AgentStatsSnapshot.Empty;

    /// <summary>
    /// What the agent is doing right now, or null when nothing is in flight. Under a schema a turn can
    /// carry no streamed text at all (ADR 0022), so this is what a working indicator has to be built on.
    /// </summary>
    public AgentTurnPhase? Phase { get; private set; }

    /// <summary>
    /// True while the agent is answering a turn: from the moment one is submitted until its result line
    /// arrives. Deliberately distinct from <see cref="Phase"/>, which goes null mid-turn every time a
    /// message commits; this is what "between turns" means, and between turns is when the model picker
    /// is allowed to move a running conversation onto another model.
    /// </summary>
    public bool TurnInFlight { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>
    /// Set while the conversation waits for the network (backlog 229): its turn or its process failed on the network
    /// and the studio continues it by itself when the model's host answers again. Null otherwise.
    /// </summary>
    public NetworkWait? NetworkWait { get; private set; }

    /// <summary>The agent CLI's own retry of a failed model call, while it is retrying in this turn (backlog 229).</summary>
    public ApiRetrying? Retrying { get; private set; }

    /// <summary>Automatic continues after a network failure since a turn last succeeded (backlog 229).</summary>
    internal int NetworkContinues { get; private set; }

    /// <summary>
    /// The id of the broker token the agent process was started with (backlog 235), read from the credential store just
    /// before the process built its environment; null where the broker does not carry the runtime or no token was valid.
    /// The id, never the token: it is what tells a stale process from a fresh one.
    /// </summary>
    internal string? CredentialTokenId
    {
        get => Volatile.Read(ref _credentialTokenId);
        set => Volatile.Write(ref _credentialTokenId, value);
    }

    private string? _credentialTokenId;

    public bool IsRunning => !State.IsTerminal();

    /// <summary>
    /// True when the process ended but the conversation can be continued (backlog 25 increment 6): the CLI
    /// exited because one turn exhausted its validation attempts, and the provider session id
    /// <c>--resume</c> needs is known. The pane offers Continue exactly while this is true.
    /// </summary>
    public bool IsResumable => !IsRunning && _resumable && !string.IsNullOrWhiteSpace(ProviderSessionId);

    private bool _resumable;

    /// <summary>Everything that has happened, oldest first.</summary>
    public IReadOnlyList<AgentEvent> Transcript
    {
        get
        {
            lock (_gate)
            {
                return [.. _transcript];
            }
        }
    }

    /// <summary>The assistant message being written right now, or empty between messages.</summary>
    public string PartialText
    {
        get
        {
            lock (_gate)
            {
                return _partial.ToString();
            }
        }
    }

    /// <summary>Tool calls waiting on Approve or Deny, oldest first.</summary>
    public IReadOnlyList<PendingToolApproval> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending];
            }
        }
    }

    /// <summary>
    /// Pre-loads the conversation's stored transcript when a studio that no longer holds it resumes it. The
    /// usage the transcript last reported comes with it, so the strip shows the conversation's whole spend.
    /// </summary>
    internal void Seed(IEnumerable<AgentEvent> history)
    {
        lock (_gate)
        {
            _transcript.AddRange(history);
            if (_transcript.OfType<StatsUpdated>().LastOrDefault() is { } last)
            {
                Stats = last.Stats;
            }

            // Requested 2026-10-22: the effort the conversation was last at, which a resume naming none keeps.
            if (_transcript.OfType<EffortChosen>().LastOrDefault() is { } effort)
            {
                Effort = effort.Effort;
            }

            // Work item #352: once graph-only, always graph-only; a seed can only add the mark, never lift it.
            if (_transcript.OfType<GraphOnlyChosen>().Any())
            {
                IsGraphOnly = true;
            }

            // Work item #359: and once the assistant, always the assistant.
            if (_transcript.OfType<AssistantChosen>().Any())
            {
                IsAssistant = true;
            }
        }

        Raise();
    }

    /// <summary>
    /// Work item #359: a detached copy made only to SHOW a stored conversation (Home's box after a restart) is not
    /// running: it takes the stored row's terminal state, so no surface offers to stop or answer it.
    /// </summary>
    internal void SettleAsStored(AgentSessionState state, DateTimeOffset? endedUtc)
    {
        lock (_gate)
        {
            State = state.IsTerminal() ? state : AgentSessionState.Cancelled;
            EndedUtc = endedUtc;
        }

        Raise();
    }

    /// <summary>
    /// Brings an ended conversation back to life in place (owner report 2026-09-15): the same object, the
    /// same id and the same transcript, running again. Whatever surface is attached to it keeps rendering it,
    /// which is what makes a resume look exactly like the conversation it continues. The usage so far is
    /// carried forward because the resumed process will count from zero.
    /// </summary>
    internal void Reopen(DateTimeOffset now)
    {
        lock (_gate)
        {
            State = AgentSessionState.Running;
            EndedUtc = null;
            FailureReason = null;
            _resumable = false;
            ActivatedUtc = now;
            _usageCarried = Stats;
        }

        Raise();
    }

    /// <summary>
    /// A usage reading from the process as the CONVERSATION's reading: the carried flows added in, the
    /// levels (context occupancy and window) left as the process reports them, and the baseline kept from
    /// the conversation's first reading when there was one. Every other event passes through untouched.
    /// </summary>
    internal AgentEvent WithCarriedUsage(AgentEvent e)
    {
        AgentStatsSnapshot carried;
        lock (_gate)
        {
            carried = _usageCarried;
        }

        if (e is not StatsUpdated stats || carried == AgentStatsSnapshot.Empty)
        {
            return e;
        }

        var now = stats.Stats;
        return stats with
        {
            Stats = now with
            {
                InputTokens = carried.InputTokens + now.InputTokens,
                OutputTokens = carried.OutputTokens + now.OutputTokens,
                CacheReadTokens = carried.CacheReadTokens + now.CacheReadTokens,
                CostUsd = carried.CostUsd + now.CostUsd,
                ToolCalls = carried.ToolCalls + now.ToolCalls,
                RateLimitHits = carried.RateLimitHits + now.RateLimitHits,
                Turns = carried.Turns + now.Turns,
                BaselineTokens = carried.BaselineTokens > 0 ? carried.BaselineTokens : now.BaselineTokens,
            },
        };
    }

    /// <summary>
    /// A session that CONTINUES an earlier conversation knows that conversation's provider id before its
    /// resumed CLI says anything: a Continue sends no prompt, so no init line arrives until the user's next
    /// turn, and a session saved in the meantime would otherwise lose the id it can be resumed under.
    /// </summary>
    internal void AdoptProviderSession(string providerSessionId)
    {
        lock (_gate)
        {
            ProviderSessionId ??= providerSessionId;
        }
    }

    /// <summary>Folds one streamed event into the session's state.</summary>
    internal void Apply(AgentEvent e)
    {
        lock (_gate)
        {
            switch (e)
            {
                case PartialText partial:
                    // Transient: kept out of the transcript, replaced by the finished message.
                    _partial.Append(partial.Delta);
                    break;

                case TurnActivity activity:
                    // Transient like PartialText: it drives the working indicator and is never history.
                    Phase = activity.Phase;
                    TurnInFlight = true;
                    if (activity.Phase != AgentTurnPhase.Submitted)
                    {
                        // The model is answering, so whatever the CLI was retrying has gone through.
                        Retrying = null;
                    }

                    break;

                case ApiRetrying retrying:
                    // Transient too: true only until the call goes through or the turn ends.
                    Retrying = retrying;
                    break;

                case ApiErrorReported:
                    // Read by the registry to judge the turn; the same words are in the transcript as the message.
                    break;

                case MessageEmitted:
                    // Safe to clear because the parser commits a text block the moment that block
                    // completes: what the preview holds is exactly the block this message IS, so the
                    // bubble replaces it rather than replacing something still unwritten. When prose
                    // was held for the end of the turn this was the third live defect, because a
                    // message committed mid-turn wiped a preview of prose nothing had committed yet.
                    _partial.Clear();
                    Phase = null;
                    _transcript.Add(e);
                    break;

                case TurnCompleted completed:
                    Phase = null;
                    TurnInFlight = false;
                    Retrying = null;
                    if (!completed.IsError)
                    {
                        NetworkContinues = 0;
                    }

                    // The turn is over, so there is nothing still being typed, and there is nothing
                    // uncommitted to lose: the parser commits any streamed tail on the result line,
                    // immediately before this event. A turn whose document repeated a bubble already on
                    // screen emits no message of its own, so this is what takes that bubble's preview
                    // down rather than leaving it hanging under the finished turn.
                    _partial.Clear();
                    _transcript.Add(e);
                    break;

                case SessionRegistered registered:
                    ProviderSessionId = registered.ProviderSessionId;
                    Model = registered.Model;
                    _transcript.Add(e);
                    break;

                case EffortChosen chosen:
                    Effort = chosen.Effort;
                    _transcript.Add(e);
                    break;

                case GraphOnlyChosen:
                    IsGraphOnly = true;
                    _transcript.Add(e);
                    break;

                case AssistantChosen:
                    IsAssistant = true;
                    _transcript.Add(e);
                    break;

                case StatsUpdated stats:
                    Stats = stats.Stats;
                    _transcript.Add(e);
                    break;

                case ToolApprovalRequested requested:
                    _transcript.Add(requested);
                    break;

                case ToolApprovalResolved resolved:
                    _pending.RemoveAll(p => p.RequestId == resolved.RequestId);
                    _transcript.Add(resolved);
                    break;

                case SessionEnded ended:
                    State = ended.FinalState;
                    FailureReason = ended.FailureReason;
                    _resumable = ended.Resumable;
                    EndedUtc = ended.Timestamp;
                    Phase = null;
                    TurnInFlight = false;
                    Retrying = null;
                    _pending.Clear();
                    _partial.Clear();
                    _transcript.Add(e);
                    break;

                default:
                    _transcript.Add(e);
                    break;
            }
        }

        Raise();
    }

    /// <summary>
    /// Counts one more automatic refusal of this call and returns the running total, this one included
    /// (backlog 117). A call is its tool name and its subject, compared exactly.
    /// </summary>
    internal int CountAutomaticRefusal(string toolName, string detail)
    {
        lock (_gate)
        {
            var key = (toolName ?? string.Empty, detail ?? string.Empty);
            var count = _automaticRefusals.TryGetValue(key, out var seen) ? seen + 1 : 1;
            _automaticRefusals[key] = count;
            return count;
        }
    }

    /// <summary>Starts, updates or (with null) ends the wait for the network (backlog 229).</summary>
    internal void SetNetworkWait(NetworkWait? wait)
    {
        lock (_gate)
        {
            NetworkWait = wait;
        }

        Raise();
    }

    /// <summary>
    /// Changes the wait in place, if there still is one, under the session's lock: the probe and the process ending
    /// each change a different part of it from different threads, and neither may undo the other.
    /// </summary>
    internal NetworkWait? UpdateNetworkWait(Func<NetworkWait, NetworkWait> change)
    {
        NetworkWait? updated;
        lock (_gate)
        {
            updated = NetworkWait is { } current ? change(current) : null;
            NetworkWait = updated;
        }

        if (updated is not null)
        {
            Raise();
        }

        return updated;
    }

    /// <summary>Counts one automatic continue after a network failure and answers the new count.</summary>
    internal int CountNetworkContinue()
    {
        lock (_gate)
        {
            return ++NetworkContinues;
        }
    }

    internal void AddPending(PendingToolApproval approval)
    {
        lock (_gate)
        {
            _pending.Add(approval);
        }

        Raise();
    }

    /// <summary>
    /// Sets the owner's chosen title, leaving <see cref="DerivedTitle"/> in place as the fallback. Blank
    /// or unchanged leaves the name alone; true when the name actually changed.
    /// </summary>
    internal bool Rename(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var trimmed = title.Trim();
        if (string.Equals(trimmed, Title, StringComparison.Ordinal))
        {
            return false;
        }

        CustomTitle = trimmed;
        Raise();
        return true;
    }

    /// <summary>
    /// The record to persist for this session as it stands. The derived title and the custom one travel
    /// separately, so a lifecycle save (registered, ended) carries the rename rather than undoing it.
    /// </summary>
    internal SessionRecord ToRecord() =>
        new(Id, WorkspaceId, ConversationRuntime.Tool, DerivedTitle, ProviderSessionId, State, StartedUtc, EndedUtc, CustomTitle);

    private void Raise() => Changed?.Invoke();
}
