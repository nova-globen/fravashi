using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Work item #352: a GRAPH-ONLY conversation ("Notes and items only") may run beside a folder's live editing one. It
/// reads the folder's copy and reads and writes the tracker and the wiki, and nothing else, held there at three layers:
/// the CLI's own tool list (<c>ClaudeSessionRunnerTests</c> proves the command line), the approval broker's up-front
/// refusal, and the registry's mark, which nothing lifts.
/// </summary>
public class GraphOnlyConversationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-graph-only-" + Guid.NewGuid().ToString("N"));

    private readonly string _copy;
    private readonly SqliteSessionStore _store;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly ScriptedRunner _runner = new();
    private readonly LiveSessionRegistry _registry;

    public GraphOnlyConversationTests()
    {
        _copy = Path.Combine(_directory, "workspaces", "w1");
        Directory.CreateDirectory(_copy);
        File.WriteAllText(Path.Combine(_copy, "seed.txt"), "one\n");
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _store = new SqliteSessionStore(factory);
        _registry = new LiveSessionRegistry(_runner, _store, _broker);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _registry.Dispose();
        _broker.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static SessionStartRequest Editing(string prompt = "fix the bug") => new("w1", "/workspaces/w1", prompt);

    private static SessionStartRequest GraphOnly(string prompt = "write these up") =>
        new("w1", "/workspaces/w1", prompt, GraphOnly: true);

    private static async Task Eventually(Func<bool> condition, string because = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10); // poll interval inside a wait helper (backlog 107)
        }

        condition().Should().BeTrue(because);
    }

    /// <summary>A runner the test drives by hand; each session gets its own event stream.</summary>
    private sealed class ScriptedRunner : IAgentSessionRunner
    {
        private readonly Dictionary<AgentSessionId, Channel<AgentEvent>> _streams = [];
        private readonly Lock _gate = new();

        public List<AgentSessionRequest> Requests { get; } = [];

        public List<string?> ModelChanges { get; } = [];

        private Channel<AgentEvent> StreamFor(AgentSessionId id)
        {
            lock (_gate)
            {
                if (!_streams.TryGetValue(id, out var stream))
                {
                    stream = Channel.CreateUnbounded<AgentEvent>();
                    _streams[id] = stream;
                }

                return stream;
            }
        }

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Requests.Add(request);
                _streams[request.SessionId] = Channel.CreateUnbounded<AgentEvent>();
            }

            return StreamFor(request.SessionId).Reader.ReadAllAsync(cancellationToken);
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId)
        {
            Finish(sessionId, AgentSessionState.Cancelled);
            return true;
        }

        public bool ChangeModel(AgentSessionId sessionId, string? model)
        {
            ModelChanges.Add(model);
            return true;
        }

        public void Emit(AgentSessionId id, AgentEvent e) => StreamFor(id).Writer.TryWrite(e);

        public void Finish(AgentSessionId id, AgentSessionState state = AgentSessionState.Completed)
        {
            Emit(id, new SessionEnded(Now, state, null));
            StreamFor(id).Writer.TryComplete();
        }
    }

    // ---- the registry: one graph-only slot beside the editing one ------------------------------------------------

    [Fact]
    public async Task With_an_editing_conversation_running_a_graph_only_one_starts_beside_it_and_the_slots_stay_single()
    {
        var editing = _registry.Start(Editing());
        var graphOnly = _registry.Start(GraphOnly());

        graphOnly.IsGraphOnly.Should().BeTrue();
        editing.IsGraphOnly.Should().BeFalse();
        _registry.RunningFor("w1").Should().Be(editing, "the folder's live session is still the editing one");
        _registry.RunningGraphOnlyFor("w1").Should().Be(graphOnly);
        _registry.AnyRunningFor("w1").Should().BeTrue();

        var secondGraphOnly = () => _registry.Start(GraphOnly("and more notes"));
        secondGraphOnly.Should().Throw<InvalidOperationException>().WithMessage(LiveSessionRegistry.GraphOnlyAlreadyRunning);

        var secondEditing = () => _registry.Start(Editing("and another fix"));
        secondEditing.Should().Throw<InvalidOperationException>().WithMessage("*already has a session running*");

        await Eventually(() => _runner.Requests.Count == 2);
        _runner.Requests.Single(r => r.SessionId == graphOnly.Id).GraphOnly.Should().BeTrue();
        _runner.Requests.Single(r => r.SessionId == editing.Id).GraphOnly.Should().BeFalse();
        _broker.IsGraphOnly(graphOnly.Id).Should().BeTrue();
        _broker.IsGraphOnly(editing.Id).Should().BeFalse();
    }

    [Fact]
    public void An_editing_conversation_may_start_while_only_a_graph_only_one_runs()
    {
        var graphOnly = _registry.Start(GraphOnly());

        var editing = _registry.Start(Editing());

        editing.IsGraphOnly.Should().BeFalse();
        _registry.RunningFor("w1").Should().Be(editing);
        _registry.RunningGraphOnlyFor("w1").Should().Be(graphOnly);
    }

    [Fact]
    public void The_graph_only_mark_is_in_the_transcript_and_the_store()
    {
        var graphOnly = _registry.Start(GraphOnly());

        graphOnly.Transcript.OfType<GraphOnlyChosen>().Should().ContainSingle();
        _store.Transcript(graphOnly.Id).OfType<GraphOnlyChosen>().Should().ContainSingle();
        _registry.IsGraphOnly(graphOnly.Id).Should().BeTrue();
    }

    [Fact]
    public async Task A_model_change_keeps_the_conversation_graph_only()
    {
        var graphOnly = _registry.Start(GraphOnly());
        await Eventually(() => _runner.Requests.Count == 1);
        _runner.Emit(graphOnly.Id, new SessionRegistered(Now, "provider-1", "claude-sonnet"));
        await Eventually(() => graphOnly.ProviderSessionId is not null);
        _runner.Emit(graphOnly.Id, new TurnCompleted(Now, "done", false, "completed"));
        await Eventually(() => !graphOnly.TurnInFlight, "a model change waits for the turn to end");

        _registry.ChangeModel(graphOnly.Id, "claude-opus-4-5").Should().BeTrue();

        _runner.ModelChanges.Should().Equal("claude-opus-4-5");
        graphOnly.IsGraphOnly.Should().BeTrue();
        _broker.IsGraphOnly(graphOnly.Id).Should().BeTrue("a model change restarts the child, never re-registers the hold away");
        _registry.RunningGraphOnlyFor("w1").Should().Be(graphOnly);
        _registry.RunningFor("w1").Should().BeNull();
    }

    [Fact]
    public async Task A_stored_graph_only_conversation_resumes_graph_only_beside_a_running_editing_one()
    {
        var graphOnly = _registry.Start(GraphOnly());
        await Eventually(() => _runner.Requests.Count == 1);
        _runner.Emit(graphOnly.Id, new SessionRegistered(Now, "provider-1", null));
        _runner.Finish(graphOnly.Id);
        await Eventually(() => !graphOnly.IsRunning);

        // A new studio process over the same store: the conversation is only a stored row now.
        var runner = new ScriptedRunner();
        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5));
        using var registry = new LiveSessionRegistry(runner, _store, broker);
        var editing = registry.Start(Editing());

        registry.IsGraphOnly(graphOnly.Id).Should().BeTrue();
        registry.CanResume(graphOnly.Id).Should().BeTrue("its slot is free even though an editing conversation runs");

        // The resume request does not say graph-only; the conversation's own mark decides.
        var resumed = registry.Resume(graphOnly.Id, "/workspaces/w1");

        resumed.IsGraphOnly.Should().BeTrue();
        await Eventually(() => runner.Requests.Count == 2);
        runner.Requests.Single(r => r.SessionId == graphOnly.Id).GraphOnly.Should().BeTrue();
        broker.IsGraphOnly(graphOnly.Id).Should().BeTrue();
        registry.RunningFor("w1").Should().Be(editing);
        _store.Transcript(graphOnly.Id).OfType<GraphOnlyChosen>().Should().ContainSingle("the mark is written once");
    }

    // ---- the broker: an explicit allowlist, refused up front in words ---------------------------------------------

    private AgentSessionId HeldGraphOnly(out List<PendingToolApproval> asked, out List<AutomaticToolRefusal> refused)
    {
        var id = AgentSessionId.New();
        _broker.RegisterSession(id, _copy, workspaceId: "w1");
        _broker.HoldToGraphOnly(id);
        var askedList = new List<PendingToolApproval>();
        var refusedList = new List<AutomaticToolRefusal>();
        _broker.Requested += request =>
        {
            if (request.SessionId == id)
            {
                askedList.Add(request);
                _broker.Answer(request.RequestId, false, string.Empty);
            }
        };
        _broker.Refused += refusal =>
        {
            if (refusal.SessionId == id)
            {
                refusedList.Add(refusal);
            }
        };
        asked = askedList;
        refused = refusedList;
        return id;
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("NotebookEdit")]
    [InlineData("Bash")]
    [InlineData("mcp__fravashi__git_commit")]
    [InlineData("mcp__fravashi__git_push")]
    [InlineData("mcp__fravashi__attempt_commit")]
    [InlineData("mcp__fravashi__browser_open")]
    [InlineData("mcp__fravashi__browser_fill")]
    [InlineData("mcp__fravashi__studio_upgrade")]
    [InlineData("mcp__fravashi__work_item_scenario_run")]
    [InlineData("Agent")]
    [InlineData("WebFetch")]
    [InlineData("SomeToolNobodyHasHeardOf")]
    [InlineData("read")]
    [InlineData("work_item_create")]
    [InlineData("mcp__other__work_item_create")]
    public async Task A_graph_only_session_is_refused_anything_off_its_list_up_front_in_words(string tool)
    {
        var id = HeldGraphOnly(out var asked, out var refused);

        var decision = await _broker.RequestAsync(id, tool, "t1", new Dictionary<string, string>
        {
            ["file_path"] = Path.Combine(_copy, "seed.txt"),
            ["old_string"] = "one",
            ["new_string"] = "changed",
            ["content"] = "changed",
            ["command"] = "echo hi",
            ["message"] = "commit",
        });

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Contain("notes and items only").And.Contain("refused without asking anyone").And.Contain(tool);
        decision.Reason!.Length.Should().BeLessThan(500, "a deny reason is cut at 500 characters on its way to the agent");
        asked.Should().BeEmpty("nothing off the list is put in front of a person");
        refused.Should().ContainSingle("a refusal is raised, so it counts as refused without asking");
        File.ReadAllText(Path.Combine(_copy, "seed.txt")).Should().Be("one\n");
    }

    [Fact]
    public async Task A_graph_only_session_reads_inside_the_copy_unattended()
    {
        var id = HeldGraphOnly(out var asked, out var refused);

        (await _broker.RequestAsync(id, "Read", "t1", new Dictionary<string, string> { ["file_path"] = Path.Combine(_copy, "seed.txt") }))
            .Approved.Should().BeTrue();
        (await _broker.RequestAsync(id, "Grep", "t2", new Dictionary<string, string> { ["pattern"] = "one" }))
            .Approved.Should().BeTrue();
        (await _broker.RequestAsync(id, "Glob", "t3", new Dictionary<string, string> { ["pattern"] = "*.txt" }))
            .Approved.Should().BeTrue();
        (await _broker.RequestAsync(id, "ToolSearch", "t4", new Dictionary<string, string> { ["query"] = "work_item_create" }))
            .Approved.Should().BeTrue();

        asked.Should().BeEmpty();
        refused.Should().BeEmpty();
    }

    [Fact]
    public async Task A_graph_only_session_writes_the_graph_exactly_as_an_editing_session_does()
    {
        var id = HeldGraphOnly(out var asked, out var refused);

        // A creation runs unattended, as it does for every session (backlog 77)...
        (await _broker.RequestAsync(id, "mcp__fravashi__work_item_create", "t1", new Dictionary<string, string>
        {
            ["title"] = "Write the notes up",
        })).Approved.Should().BeTrue();
        asked.Should().BeEmpty();

        // ...and a change to an existing wiki document still asks a person, as it always did.
        await _broker.RequestAsync(id, "mcp__fravashi__wiki_write", "t2", new Dictionary<string, string>
        {
            ["number"] = "12",
            ["title"] = "Notes",
            ["body"] = "Some notes.",
        });
        asked.Should().ContainSingle(request => request.ToolName == "mcp__fravashi__wiki_write");
        refused.Should().BeEmpty("nothing on the list is refused by the graph-only hold");
    }

    [Fact]
    public async Task Every_tool_on_the_list_passes_the_hold()
    {
        var id = HeldGraphOnly(out _, out var refused);

        foreach (var tool in AgentGraphOnlyTools.Studio)
        {
            await _broker.RequestAsync(id, tool, "t1", new Dictionary<string, string>());
        }

        refused.Where(r => r.Reason.Contains("notes and items only", StringComparison.Ordinal))
            .Should().BeEmpty("the hold refuses only what is off its list");
    }

    [Fact]
    public async Task A_registration_does_not_lift_the_hold_and_a_release_does()
    {
        var id = AgentSessionId.New();
        _broker.HoldToGraphOnly(id);
        _broker.RegisterSession(id, _copy, workspaceId: "w1");

        (await _broker.RequestAsync(id, "Write", "t1", new Dictionary<string, string> { ["file_path"] = Path.Combine(_copy, "x.txt") }))
            .Approved.Should().BeFalse();
        _broker.IsGraphOnly(id).Should().BeTrue();

        _broker.ReleaseSession(id);
        _broker.IsGraphOnly(id).Should().BeFalse();
    }

    [Fact]
    public void The_refusal_and_the_briefing_name_what_the_conversation_can_and_cannot_do()
    {
        AgentGraphOnlyTools.Refusal("Bash").Length.Should().BeLessThan(500);
        AgentFirstTurnBriefing.GraphOnlyNote.Should().Contain("work_item_*").And.Contain("wiki_*")
            .And.Contain("cannot edit a file, run a command or use git").And.Contain("needs no plan");
    }
}
