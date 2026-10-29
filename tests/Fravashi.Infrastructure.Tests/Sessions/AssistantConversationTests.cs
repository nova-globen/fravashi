using System.Text.Json;
using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Fravashi.Infrastructure.WorkItems;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Work item #359: the STUDIO ASSISTANT, Home's general assistant. Built on the graph-only conversation (#352): the same
/// three layers hold it to reading the folder and writing the graph (the CLI's tool list, the broker's up-front
/// refusal, the registry's permanent mark), in a slot of its own, plus two tools of its own that ALWAYS ask a person:
/// starting a building conversation for an item and dispatching the autopilot. And the files a work item carries.
/// </summary>
public sealed class AssistantConversationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-assistant-" + Guid.NewGuid().ToString("N"));

    private readonly string _copy;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteSessionStore _store;
    private readonly AgentApprovalBroker _broker;
    private readonly IWorkspaceTrust _trust = Substitute.For<IWorkspaceTrust>();
    private readonly ScriptedRunner _runner = new();
    private readonly LiveSessionRegistry _registry;
    private readonly LocalTrackerResolver _trackers;
    private readonly WorkItemChanges _changes = new();
    private readonly SqliteSessionWorkItems _work;
    private readonly SqliteSessionPlans _plans;
    private readonly FileWorkItemAttachments _attachments;
    private readonly IWorkspaceAgentSync _sync = Substitute.For<IWorkspaceAgentSync>();

    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3];

    public AssistantConversationTests()
    {
        _copy = Path.Combine(_directory, "workspaces", "w1");
        Directory.CreateDirectory(_copy);
        File.WriteAllText(Path.Combine(_copy, "seed.txt"), "one\n");
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _store = new SqliteSessionStore(_factory);
        _trust.For(Arg.Any<string>()).Returns(WorkspaceTrust.Edits);
        _broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5), trust: _trust);
        _registry = new LiveSessionRegistry(_runner, _store, _broker);
        _trackers = new LocalTrackerResolver(_factory, TimeProvider.System);
        _work = new SqliteSessionWorkItems(_factory, TimeProvider.System);
        _plans = new SqliteSessionPlans(_factory, TimeProvider.System);
        _attachments = new FileWorkItemAttachments(_factory, new DataDirectory(_directory));
        _sync.CopyPathFor("w1").Returns("/workspaces/w1");
    }

    public void Dispose()
    {
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

    private static SessionStartRequest Editing(string prompt = "fix the bug") => new("w1", "/workspaces/w1", prompt);

    private static SessionStartRequest Assistant(string prompt = "the save button does nothing") =>
        new("w1", "/workspaces/w1", prompt, Assistant: true);

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

    // ---- the registry: a slot of its own -----------------------------------------------------------------------

    [Fact]
    public async Task A_report_while_an_editing_conversation_runs_starts_the_assistant_with_no_refusal()
    {
        var editing = _registry.Start(Editing());

        var assistant = _registry.Start(Assistant());

        assistant.IsAssistant.Should().BeTrue();
        assistant.IsGraphOnly.Should().BeFalse("the assistant is its own kind, not the graph-only slot");
        _registry.RunningFor("w1").Should().Be(editing, "the folder's editing conversation is untouched");
        _registry.RunningAssistantFor("w1").Should().Be(assistant);
        await Eventually(() => _runner.Requests.Count == 2);
        var request = _runner.Requests.Single(r => r.SessionId == assistant.Id);
        request.Assistant.Should().BeTrue();
        request.EffectiveBuiltInTools.Should().Equal(AgentAssistantTools.BuiltIn);
        _broker.IsAssistant(assistant.Id).Should().BeTrue();
        _broker.IsAssistant(editing.Id).Should().BeFalse();
    }

    [Fact]
    public void The_assistant_slot_is_independent_of_the_graph_only_slot_and_holds_one()
    {
        var editing = _registry.Start(Editing());
        var graphOnly = _registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "notes", GraphOnly: true));
        var assistant = _registry.Start(Assistant());

        _registry.RunningFor("w1").Should().Be(editing);
        _registry.RunningGraphOnlyFor("w1").Should().Be(graphOnly);
        _registry.RunningAssistantFor("w1").Should().Be(assistant);

        var second = () => _registry.Start(Assistant("and another report"));
        second.Should().Throw<InvalidOperationException>().WithMessage(LiveSessionRegistry.AssistantAlreadyRunning);
    }

    [Fact]
    public void An_editing_conversation_starts_while_only_the_assistant_runs_and_panes_never_land_on_the_assistant()
    {
        var assistant = _registry.Start(Assistant());

        _registry.LatestFor("w1").Should().BeNull("the assistant lives on Home; a pane showing the folder's conversation skips it");

        var editing = _registry.Start(Editing());
        _registry.RunningFor("w1").Should().Be(editing);
        _registry.LatestFor("w1").Should().Be(editing);
        _registry.Find(assistant.Id).Should().Be(assistant, "it is still listed and opens by id");
    }

    [Fact]
    public async Task The_assistant_mark_is_permanent_and_a_resume_stays_the_assistant()
    {
        var assistant = _registry.Start(Assistant());
        _store.Transcript(assistant.Id).OfType<AssistantChosen>().Should().ContainSingle();
        await Eventually(() => _runner.Requests.Count == 1);
        _runner.Emit(assistant.Id, new SessionRegistered(Now, "provider-1", null));
        _runner.Finish(assistant.Id);
        await Eventually(() => !assistant.IsRunning);

        var runner = new ScriptedRunner();
        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5));
        using var registry = new LiveSessionRegistry(runner, _store, broker);
        registry.Start(Editing());

        registry.IsAssistant(assistant.Id).Should().BeTrue();
        registry.ShowOf(assistant.Id)!.IsAssistant.Should().BeTrue("Home shows a stored topic after a restart");
        registry.ShowOf(assistant.Id)!.IsRunning.Should().BeFalse();
        registry.CanResume(assistant.Id).Should().BeTrue("its slot is free although an editing conversation runs");

        // The continuation does not say "assistant"; the conversation's own mark decides.
        var resumed = registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "one more thing", ResumeOf: assistant.Id));

        resumed.IsAssistant.Should().BeTrue();
        await Eventually(() => runner.Requests.Count == 2);
        runner.Requests.Single(r => r.SessionId == assistant.Id).Assistant.Should().BeTrue();
        broker.IsAssistant(assistant.Id).Should().BeTrue();
        _store.Transcript(assistant.Id).OfType<AssistantChosen>().Should().ContainSingle("the mark is written once");
    }

    // ---- the broker: its list, refused up front, and its two tools that always ask ---------------------------

    private AgentSessionId Held(bool assistant, out List<PendingToolApproval> asked, out List<AutomaticToolRefusal> refused)
    {
        var id = AgentSessionId.New();
        _broker.RegisterSession(id, _copy, workspaceId: "w1");
        if (assistant)
        {
            _broker.HoldToAssistant(id);
        }

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
    [InlineData("mcp__fravashi__git_tag")]
    [InlineData("mcp__fravashi__attempt_branch")]
    [InlineData("mcp__fravashi__attempt_commit")]
    [InlineData("mcp__fravashi__browser_open")]
    [InlineData("mcp__fravashi__studio_upgrade")]
    [InlineData("mcp__fravashi__work_item_scenario_run")]
    [InlineData("mcp__fravashi__design_approve")]
    [InlineData("mcp__fravashi__quick_change")]
    [InlineData("Agent")]
    [InlineData("SomeToolNobodyHasHeardOf")]
    [InlineData("session_start_building")]
    [InlineData("mcp__other__session_start_building")]
    public async Task The_assistant_is_refused_anything_off_its_list_up_front_in_words(string tool)
    {
        var id = Held(assistant: true, out var asked, out var refused);

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
        decision.Reason.Should().Contain("Fravashi assistant").And.Contain("refused without asking anyone").And.Contain(tool);
        decision.Reason!.Length.Should().BeLessThan(500, "a deny reason is cut at 500 characters on its way to the agent");
        asked.Should().BeEmpty("nothing off the list is put in front of a person, even with edits trusted in the folder");
        refused.Should().ContainSingle();
        File.ReadAllText(Path.Combine(_copy, "seed.txt")).Should().Be("one\n");
    }

    [Fact]
    public async Task The_assistant_creates_items_unattended_and_an_update_asks_as_for_every_session()
    {
        var id = Held(assistant: true, out var asked, out var refused);

        (await _broker.RequestAsync(id, "mcp__fravashi__work_item_create", "t1", new Dictionary<string, string>
        {
            ["title"] = "Save does nothing",
            ["description"] = "Clicking Save on the settings page changes nothing.",
            ["level"] = "task",
        })).Approved.Should().BeTrue();
        asked.Should().BeEmpty();

        await _broker.RequestAsync(id, "mcp__fravashi__work_item_update", "t2", new Dictionary<string, string>
        {
            ["number"] = "4",
            ["version"] = "1",
            ["description"] = "Clicking Save changes nothing.",
        });
        asked.Should().ContainSingle(request => request.ToolName == "mcp__fravashi__work_item_update");
        refused.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AgentAssistantTools.StartBuilding, "number", "4")]
    [InlineData(AgentAssistantTools.DispatchAutopilot, "numbers", "[4,5]")]
    public async Task The_assistants_own_tools_always_ask_even_with_edits_trusted_and_under_the_autopilot(
        string tool, string field, string value)
    {
        var id = Held(assistant: true, out var asked, out var refused);
        _broker.SetAutopilot(id, true);

        var decision = await _broker.RequestAsync(id, tool, "t1", new Dictionary<string, string> { [field] = value });

        decision.Approved.Should().BeFalse("the test's person denies every card");
        asked.Should().ContainSingle(request => request.ToolName == tool, "a person is always asked");
        refused.Should().BeEmpty();
        AgentGraphTools.RunsUnattended(tool, new Dictionary<string, string> { [field] = value })
            .Should().BeNull("neither is on the unattended list");
        WorkspaceTrustLevels.IsEdit(tool).Should().BeFalse("no trust level reaches them");
    }

    [Theory]
    [InlineData(AgentAssistantTools.StartBuilding)]
    [InlineData(AgentAssistantTools.DispatchAutopilot)]
    public async Task Any_conversation_that_is_not_the_assistant_is_refused_its_two_tools_up_front(string tool)
    {
        var editing = Held(assistant: false, out var asked, out var refused);

        var decision = await _broker.RequestAsync(editing, tool, "t1", new Dictionary<string, string> { ["number"] = "4" });

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Contain("belongs to the Fravashi assistant");
        asked.Should().BeEmpty();
        refused.Should().ContainSingle();

        var graphOnly = AgentSessionId.New();
        _broker.RegisterSession(graphOnly, _copy, workspaceId: "w1");
        _broker.HoldToGraphOnly(graphOnly);
        (await _broker.RequestAsync(graphOnly, tool, "t2", new Dictionary<string, string> { ["number"] = "4" }))
            .Approved.Should().BeFalse("graph-only conversations never have them either");
    }

    [Fact]
    public void The_lists_and_the_briefing_say_what_the_assistant_can_and_cannot_do()
    {
        AgentAssistantTools.Studio.Should().Contain(AgentGraphOnlyTools.Studio);
        AgentAssistantTools.Studio.Should().Contain(AgentAssistantTools.AlwaysAsk);
        AgentAssistantTools.Studio.Should().NotContain(t => t.Contains("git_", StringComparison.Ordinal)
            || t.Contains("attempt_", StringComparison.Ordinal) || t.EndsWith("design_approve", StringComparison.Ordinal));
        AgentAssistantTools.BuiltIn.Should().NotContain(["Write", "Edit", "Bash", "NotebookEdit"]);
        AgentAssistantTools.Refusal("Bash").Length.Should().BeLessThan(500);
        AgentFirstTurnBriefing.AssistantNote.Should().Contain("proper conversation first")
            .And.Contain("acceptanceCriteria").And.Contain("ASK whether to start")
            .And.Contain("session_start_building").And.Contain("autopilot_dispatch")
            .And.Contain("cannot edit a file, run a command or use git");
        foreach (var tool in new[] { SessionStartBuildingTool.ToolName, AutopilotDispatchTool.ToolName, WorkItemAttachImageTool.ToolName })
        {
            StudioToolDescriptions.Describes(tool).Should().BeTrue();
        }

        StudioToolDescriptions.ApprovalSentence(AgentAssistantTools.DispatchAutopilot, new Dictionary<string, string> { ["numbers"] = "[4,5]" })
            .Should().Contain("#4 and #5");
        StudioToolDescriptions.ApprovalSentence(AgentAssistantTools.StartBuilding, new Dictionary<string, string> { ["number"] = "4" })
            .Should().Contain("building conversation").And.Contain("#4");
    }

    // ---- start building ------------------------------------------------------------------------------------

    private async Task<WorkItem> ReadyItemAsync(string title, AgentModelAlias? model = null, AgentEffort? effort = null)
    {
        var created = await _trackers.For(ProjectId.Default).CreateAsync(
            new NewWorkItem
            {
                Level = WorkItemLevel.Task,
                Title = title,
                Description = "Why it exists and what done looks like.",
                Status = WorkItemStatus.Ready,
                AcceptanceCriteria = [new AcceptanceCriterion(string.Empty, "It works.")],
                SuggestedModel = model,
                SuggestedEffort = effort,
            },
            MutationContext.User);
        created.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", created.Errors.Select(e => e.Code + " " + e.Message)));
        return created.Value;
    }

    private static StudioToolCall Call(AgentSessionId session, string tool, string json) =>
        new(session, tool, JsonDocument.Parse(json).RootElement.Clone(), "op-" + Guid.NewGuid().ToString("N"), ProjectId.Default);

    private SessionStartBuildingTool StartTool() => new(
        _trackers, _changes, _broker, _store, () => _registry, _sync, _work, _plans,
        () => new AgentDefault("claude-sonnet-5", AgentEffort.Medium));

    [Fact]
    public async Task Starting_a_building_conversation_starts_an_editing_one_linked_to_the_item_on_its_suggestions()
    {
        var item = await ReadyItemAsync("Fix Save", AgentModelAlias.Opus, AgentEffort.High);
        var assistant = _registry.Start(Assistant());

        var result = await StartTool().InvokeAsync(Call(assistant.Id, SessionStartBuildingTool.ToolName, $$"""{"number":{{item.Number}}}"""));

        result.IsError.Should().BeFalse(result.Text);
        var building = _registry.RunningFor("w1");
        building.Should().NotBeNull();
        building!.IsAssistant.Should().BeFalse();
        building.IsGraphOnly.Should().BeFalse();
        _work.For(building.Id).Should().ContainSingle(w => w.Item == item.Id);
        await Eventually(() => _runner.Requests.Any(r => r.SessionId == building.Id));
        var request = _runner.Requests.Single(r => r.SessionId == building.Id);
        request.Model.Should().Be(AgentModelAlias.Opus.CliId());
        request.Effort.Should().Be(AgentEffort.High);
        request.Prompt.Should().Contain($"#{item.Number}");
        _registry.RunningAssistantFor("w1").Should().Be(assistant, "the assistant keeps running beside it");
    }

    [Fact]
    public async Task Without_suggestions_it_starts_on_the_new_conversation_default()
    {
        var item = await ReadyItemAsync("Tidy the header");
        var assistant = _registry.Start(Assistant());

        (await StartTool().InvokeAsync(Call(assistant.Id, SessionStartBuildingTool.ToolName, $$"""{"number":{{item.Number}}}""")))
            .IsError.Should().BeFalse();

        var building = _registry.RunningFor("w1")!;
        await Eventually(() => _runner.Requests.Any(r => r.SessionId == building.Id));
        var request = _runner.Requests.Single(r => r.SessionId == building.Id);
        request.Model.Should().Be("claude-sonnet-5");
        request.Effort.Should().Be(AgentEffort.Medium);
    }

    [Fact]
    public async Task Starting_is_refused_in_words_when_an_editing_conversation_already_runs_and_that_one_is_left_alone()
    {
        var item = await ReadyItemAsync("Fix Save");
        var editing = _registry.Start(Editing());
        var assistant = _registry.Start(Assistant());

        var result = await StartTool().InvokeAsync(Call(assistant.Id, SessionStartBuildingTool.ToolName, $$"""{"number":{{item.Number}}}"""));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("already running in this folder").And.Contain("nothing was started");
        _registry.RunningFor("w1").Should().Be(editing);
        editing.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task The_tool_itself_refuses_a_conversation_that_is_not_the_assistant()
    {
        var item = await ReadyItemAsync("Fix Save");
        var editing = _registry.Start(Editing());

        var result = await StartTool().InvokeAsync(Call(editing.Id, SessionStartBuildingTool.ToolName, $$"""{"number":{{item.Number}}}"""));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("belongs to the Fravashi assistant");
    }

    // ---- dispatch --------------------------------------------------------------------------------------------

    private sealed class Dispatching
    {
        public required IServiceScopeFactory Scopes { get; init; }

        public required IAutopilotRunStore Runs { get; init; }
    }

    private Dispatching DispatchServices(IReadOnlyList<AutopilotRun>? history = null)
    {
        var runs = Substitute.For<IAutopilotRunStore>();
        runs.OpenRunsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AutopilotRun>>([]));
        runs.ForProjectAsync(Arg.Any<ProjectId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(history ?? (IReadOnlyList<AutopilotRun>)[]));
        runs.SaveAsync(Arg.Any<AutopilotRun>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<AutopilotRun>()));
        var delivery = Substitute.For<IProjectDelivery>();
        delivery.For(Arg.Any<ProjectId>()).Returns(AutopilotDeliveryMode.CommitToMain);
        var workspace = Substitute.For<IAutopilotWorkspaceStatus>();
        workspace.CheckAsync(Arg.Any<ProjectId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AutopilotWorkspaceState("main", 0, true, "w1")));
        var readiness = Substitute.For<IAutopilotDeliveryReadiness>();

        var services = new ServiceCollection();
        services.AddSingleton<ITrackerResolver>(_trackers);
        services.AddSingleton(delivery);
        services.AddSingleton(workspace);
        services.AddSingleton(readiness);
        services.AddSingleton(runs);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AutopilotDispatcher>();
        return new Dispatching { Scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), Runs = runs };
    }

    [Fact]
    public async Task Dispatch_goes_through_the_dispatcher_with_exactly_the_named_items_marked()
    {
        var first = await ReadyItemAsync("One");
        var second = await ReadyItemAsync("Two");
        var untouched = await ReadyItemAsync("Not named");
        var assistant = _registry.Start(Assistant());
        var services = DispatchServices();
        var tool = new AutopilotDispatchTool(_trackers, _changes, _broker, services.Scopes);

        var result = await tool.InvokeAsync(Call(assistant.Id, AutopilotDispatchTool.ToolName,
            $$"""{"numbers":[{{first.Number}},{{second.Number}}]}"""));

        result.IsError.Should().BeFalse(result.Text);
        await services.Runs.Received(1).SaveAsync(
            Arg.Is<AutopilotRun>(run => run.Queue.Count == 2 && run.Settings.Builder == AutopilotDispatchSettings.Default.Builder),
            Arg.Any<CancellationToken>());
        var board = _trackers.For(ProjectId.Default);
        (await board.GetAsync(first.Id))!.MarkedForAutopilot.Should().BeTrue();
        (await board.GetAsync(second.Id))!.MarkedForAutopilot.Should().BeTrue();
        (await board.GetAsync(untouched.Id))!.MarkedForAutopilot.Should().BeFalse();
    }

    [Fact]
    public async Task Dispatch_reuses_the_last_runs_settings()
    {
        var item = await ReadyItemAsync("One");
        var assistant = _registry.Start(Assistant());
        var lastSettings = AutopilotDispatchSettings.Default with { PersonWaitTimeout = TimeSpan.FromMinutes(7) };
        var last = AutopilotRun.Start(Guid.NewGuid(), ProjectId.Default, lastSettings, [], Now);
        var services = DispatchServices([last]);
        var tool = new AutopilotDispatchTool(_trackers, _changes, _broker, services.Scopes);

        (await tool.InvokeAsync(Call(assistant.Id, AutopilotDispatchTool.ToolName, $$"""{"numbers":[{{item.Number}}]}""")))
            .IsError.Should().BeFalse();

        await services.Runs.Received(1).SaveAsync(
            Arg.Is<AutopilotRun>(run => run.Settings.PersonWaitTimeout == TimeSpan.FromMinutes(7)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatch_is_refused_when_another_item_is_marked_so_the_card_stays_the_whole_truth()
    {
        var named = await ReadyItemAsync("Named");
        var other = await ReadyItemAsync("Already marked");
        var board = _trackers.For(ProjectId.Default);
        (await board.UpdateAsync(other.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) }, other.Version, MutationContext.User))
            .Outcome.Should().Be(TrackerOutcome.Ok);
        var assistant = _registry.Start(Assistant());
        var services = DispatchServices();
        var tool = new AutopilotDispatchTool(_trackers, _changes, _broker, services.Scopes);

        var result = await tool.InvokeAsync(Call(assistant.Id, AutopilotDispatchTool.ToolName, $$"""{"numbers":[{{named.Number}}]}"""));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain($"#{other.Number}").And.Contain("would be");
        await services.Runs.DidNotReceive().SaveAsync(Arg.Any<AutopilotRun>(), Arg.Any<CancellationToken>());
        (await board.GetAsync(named.Id))!.MarkedForAutopilot.Should().BeFalse();
    }

    [Fact]
    public async Task Dispatch_refuses_a_conversation_that_is_not_the_assistant()
    {
        var item = await ReadyItemAsync("One");
        var editing = _registry.Start(Editing());
        var services = DispatchServices();
        var tool = new AutopilotDispatchTool(_trackers, _changes, _broker, services.Scopes);

        var result = await tool.InvokeAsync(Call(editing.Id, AutopilotDispatchTool.ToolName, $$"""{"numbers":[{{item.Number}}]}"""));

        result.IsError.Should().BeTrue();
        await services.Runs.DidNotReceive().SaveAsync(Arg.Any<AutopilotRun>(), Arg.Any<CancellationToken>());
    }

    // ---- attachments -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_reports_image_is_attached_to_the_filed_item_and_stored_in_the_data_directory()
    {
        var item = await ReadyItemAsync("Save does nothing");
        var assistant = _registry.Start(new SessionStartRequest(
            "w1", "/workspaces/w1", "this is what I see [Image 1]", Images: [AgentImageAttachment.TryCreate(Png, out _)!], Assistant: true));
        var tool = new WorkItemAttachImageTool(_trackers, _changes, _store, _attachments);

        var result = await tool.InvokeAsync(Call(assistant.Id, WorkItemAttachImageTool.ToolName,
            $$"""{"number":{{item.Number}},"image":1,"name":"Save button"}"""));

        result.IsError.Should().BeFalse(result.Text);
        var stored = _attachments.For(item.Id).Should().ContainSingle().Subject;
        stored.MediaType.Should().Be("image/png");
        stored.FileName.Should().Be("Save button");
        stored.AddedBy.Should().Be("session:" + assistant.Id.Value.ToString("N"));
        _attachments.Read(item.Id, stored.Id).Should().Equal(Png);
        File.Exists(Path.Combine(_directory, FileWorkItemAttachments.FolderName, item.Id.Value, stored.Id + ".png"))
            .Should().BeTrue("the bytes live in the data directory, never the repository");

        (await tool.InvokeAsync(Call(assistant.Id, WorkItemAttachImageTool.ToolName, $$"""{"number":{{item.Number}},"image":2}""")))
            .IsError.Should().BeTrue("the conversation has one image");
    }

    [Fact]
    public void Attachments_are_images_only_size_capped_and_checked_by_content()
    {
        var item = new WorkItemId("item-1");

        _attachments.AddImage(item, "notes.txt", "just text"u8.ToArray(), "user").Refusal
            .Should().Contain("PNG, JPEG, GIF and WebP");
        var tooLarge = new byte[AgentImageAttachment.MaxBytes + 1];
        Png.CopyTo(tooLarge, 0);
        _attachments.AddImage(item, "huge.png", tooLarge, "user").Refusal.Should().Contain("limit");
        _attachments.AddImage(item, "", [], "user").Refusal.Should().Contain("empty");
        _attachments.For(item).Should().BeEmpty();

        var stored = _attachments.AddImage(item, "../../etc/passwd.png", Png, "user").Attachment!;
        stored.FileName.Should().Be("passwd.png", "a name is only ever a display name");
        _attachments.For(item).Should().ContainSingle();
        _attachments.Read(item, "../" + stored.Id).Should().BeNull();
        _attachments.Read(new WorkItemId("other"), stored.Id).Should().BeNull("an attachment is read only through its own item");

        // A file swapped underneath for something that is not the image the row says is never served.
        File.WriteAllText(Path.Combine(_directory, FileWorkItemAttachments.FolderName, "item-1", stored.Id + ".png"), "<script>");
        _attachments.Read(item, stored.Id).Should().BeNull();
    }

    /// <summary>A runner the test drives by hand; each session gets its own event stream.</summary>
    private sealed class ScriptedRunner : IAgentSessionRunner
    {
        private readonly Dictionary<AgentSessionId, Channel<AgentEvent>> _streams = [];
        private readonly Lock _gate = new();

        public List<AgentSessionRequest> Requests { get; } = [];

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

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;

        public void Emit(AgentSessionId id, AgentEvent e) => StreamFor(id).Writer.TryWrite(e);

        public void Finish(AgentSessionId id, AgentSessionState state = AgentSessionState.Completed)
        {
            Emit(id, new SessionEnded(Now, state, null));
            StreamFor(id).Writer.TryComplete();
        }
    }
}
