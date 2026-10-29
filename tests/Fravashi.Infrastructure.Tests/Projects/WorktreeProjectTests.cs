using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Projects;

/// <summary>
/// Backlog 319: an autopilot worktree acts in its repository folder's project. The delivery proof
/// had a worktree row in the DEFAULT project
/// while its folder was in another, so a builder's work_item_read(2) returned the default project's #2 and its plans
/// landed in the default wiki. The project is RESOLVED from the repository folder (plan @136), so the worktree row here
/// is put in the default project the way the proof run's row was (item #373 made a fresh row "not set up", which no
/// longer reproduces the wrong row on its own), and every test reads through it.
/// </summary>
public sealed class WorktreeProjectTests : IDisposable
{
    private const string Version = "2025-11-25";
    private const string Repository = "repo";
    private const string Worktree = "wt-2";

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "fravashi-wt-project-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteTestContext _store = new();
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly AgentSessionId _session = AgentSessionId.New();
    private readonly SqliteProjectStore _projects;
    private readonly SqliteWorkspaceStore _workspaces;
    private readonly SqliteSessionStore _sessions;
    private readonly LocalTrackerResolver _trackers;
    private readonly SqliteWikiLibraries _wikis;
    private readonly ProjectId _proof;
    private readonly string _secret;

    private int _key;

    public WorktreeProjectTests()
    {
        _projects = new SqliteProjectStore(_store.Factory, TimeProvider.System);
        _workspaces = new SqliteWorkspaceStore(_store.Factory);
        _sessions = new SqliteSessionStore(_store.Factory);
        _trackers = new LocalTrackerResolver(_store.Factory, TimeProvider.System);
        _wikis = new SqliteWikiLibraries(_store.Factory, TimeProvider.System);

        _workspaces.Touch(Repository, "delivery-proof", @"C:\Repos\delivery-proof");
        _proof = _projects.Create("delivery-proof").Id;
        _projects.BindWorkspace(Repository, _proof).Should().BeTrue();
        _workspaces.Touch(Worktree, "2", @"C:\Repos\delivery-proof.fravashi-worktrees\2");
        _projects.BindWorkspace(Worktree, DefaultProject.Id).Should().BeTrue();

        _sessions.Save(new SessionRecord(
            _session, Worktree, AgentTool.Claude, "builder", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null));
        _secret = _broker.RegisterSession(_session, "/workspaces/" + Worktree);
    }

    public void Dispose()
    {
        _broker.Dispose();
        _store.Dispose();
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void The_worktree_row_is_in_the_default_project_as_the_proof_run_left_it()
    {
        // The precondition of every other test here: this IS the wrong row the proof run left.
        _workspaces.Get(Worktree)!.ProjectId.Should().Be(DefaultProject.Id.Value);
        _proof.Should().NotBe(DefaultProject.Id);
    }

    [Fact]
    public async Task A_work_item_read_by_number_from_a_worktree_reads_the_repository_folders_project()
    {
        await ItemAsync(DefaultProject.Id, "Probe 77 unattended creation");
        var mine = await ItemAsync(_proof, "Add CHANGELOG.md");
        mine.Number.Should().Be(1, "both projects number from one, which is what made the clash");

        var read = Payload(await CallAsync(Server(), WorkItemReadTool.ToolName, """{"number":1}"""));

        read.GetProperty("data").GetProperty("title").GetString().Should().Be("Add CHANGELOG.md");
    }

    [Fact]
    public async Task A_wiki_document_created_from_a_worktree_lands_in_the_repository_folders_project()
    {
        var written = Payload(await CallAsync(
            Server(),
            WikiWriteTool.ToolName,
            """{"title":"Plan: add CHANGELOG.md","type":"plan","body":"Where it lands is the test."}"""));
        var number = written.GetProperty("item").GetProperty("number").GetInt32();

        (await _wikis.For(_proof).GetByNumberAsync(number))!.Title.Should().Be("Plan: add CHANGELOG.md");
        (await _wikis.For(DefaultProject.Id).ListAsync(new Fravashi.Application.Wiki.WikiItemQuery())).Should().NotContain(d => d.Title == "Plan: add CHANGELOG.md");
    }

    [Fact]
    public void A_worktree_follows_its_folder_when_the_folder_moves_to_another_project()
    {
        var later = _projects.Create("Later").Id;
        _projects.BindWorkspace(Repository, later);

        WorkspaceProjects.For(_workspaces, Worktree).Should().Be(later);
        new SessionProjects(_sessions, _workspaces).For(_session).Should().Be(later);
    }

    [Fact]
    public async Task A_worktree_whose_repository_folder_has_no_record_is_placed_nowhere_and_its_calls_are_refused()
    {
        // Never the default: that fallback is the defect.
        _workspaces.Remove(Repository);
        await ItemAsync(DefaultProject.Id, "Probe 77 unattended creation");

        WorkspaceProjects.For(_workspaces, Worktree).Should().BeNull();
        var result = await CallAsync(Server(), WorkItemReadTool.ToolName, """{"number":1}""");

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("which project");
    }

    [Fact]
    public void An_ordinary_folder_keeps_its_own_project_and_an_unrecorded_one_is_in_none()
    {
        // Item #373: no shared bucket. An unrecorded folder is in no project, never quietly in the default one.
        WorkspaceProjects.For(_workspaces, Repository).Should().Be(_proof);
        WorkspaceProjects.For(_workspaces, "never-recorded").Should().BeNull();
        WorkspaceProjects.Place(_workspaces, "never-recorded").Should().Be(SessionPlacement.Unknown);
    }

    [Fact]
    public async Task A_session_in_a_folder_that_is_not_set_up_is_refused_in_words_and_reads_no_project()
    {
        // Item #373: no shared bucket. Before it, this folder was in the default project and its agent read the
        // default project's #1; now it acts on no project, and is told why in words.
        await ItemAsync(DefaultProject.Id, "Probe 77 unattended creation");
        var fresh = AgentSessionId.New();
        _workspaces.Touch("fresh", "Stomme", @"C:\Repos\Stomme");
        _sessions.Save(new SessionRecord(
            fresh, "fresh", AgentTool.Claude, "builder", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null));
        var secret = _broker.RegisterSession(fresh, "/workspaces/fresh");

        new SessionProjects(_sessions, _workspaces).Place(fresh).Should().Be(SessionPlacement.FolderNotSetUp);
        var read = await CallAsync(Server(), WorkItemReadTool.ToolName, """{"number":1}""", secret);
        var write = await CallAsync(
            Server(), WikiWriteTool.ToolName, """{"type":"plan","title":"Plan: anything","body":"b"}""", secret);

        read.IsError.Should().BeTrue();
        read.Text.Should().Be(StudioToolServer.FolderNotSetUpRefusal);
        read.Text.Should().NotContain("Probe 77");
        write.IsError.Should().BeTrue();
        (await _wikis.For(DefaultProject.Id).ListAsync(new Fravashi.Application.Wiki.WikiItemQuery())).Should().BeEmpty();
    }

    [Fact]
    public void Recent_folders_leave_the_worktree_out_and_a_projects_newest_folder_is_never_a_worktree()
    {
        ((IWorkspaceStore)_workspaces).RecentFolders().Select(w => w.Id).Should().Equal(Repository);
        _workspaces.Recent().Select(w => w.Id).Should().Contain(Worktree, "it is still recorded, only not offered");

        // The worktree was touched last and its row says default: before #319 the default project's newest folder
        // would have been this worktree of another project's folder.
        WorkspaceProjects.NewestFolderIn(_workspaces, DefaultProject.Id).Should().BeNull();
        WorkspaceProjects.NewestFolderIn(_workspaces, _proof).Should().Be(Repository);
    }

    private async Task<WorkItem> ItemAsync(ProjectId project, string title)
    {
        var created = await _trackers.For(project).CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = title, Description = "Why it exists." },
            MutationContext.User);
        created.Outcome.Should().Be(TrackerOutcome.Ok);
        return created.Value;
    }

    private StudioToolServer Server() =>
        new(
            _broker,
            [new WorkItemReadTool(_trackers, new WorkItemChanges()), new WikiWriteTool(_wikis, _trackers)],
            new FileToolCallLedger(_dataDirectory),
            "9.8.7",
            StudioToolServer.DefaultMaxResultBytes,
            grants: new DefaultStudioToolGrants(StudioToolGrant.Full),
            projects: new SessionProjects(_sessions, _workspaces));

    private async Task<(string Text, bool IsError)> CallAsync(
        StudioToolServer server, string tool, string arguments, string? secret = null)
    {
        var operationKey = "toolu_" + Interlocked.Increment(ref _key).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var body =
            $"{{\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{arguments},\"_meta\":{{\"claudecode/toolUseId\":\"{operationKey}\"}}}},\"jsonrpc\":\"2.0\",\"id\":2}}";

        var response = await server.HandleAsync(new StudioToolHttpRequest("POST", secret ?? _secret, Version, null, body));
        response.StatusCode.Should().Be(200);

        using var json = JsonDocument.Parse(response.Json!);
        if (json.RootElement.TryGetProperty("error", out var error))
        {
            return (error.GetProperty("message").GetString()!, true);
        }

        var result = json.RootElement.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    private static JsonElement Payload((string Text, bool IsError) result)
    {
        result.IsError.Should().BeFalse(result.Text);
        using var json = JsonDocument.Parse(result.Text);
        return json.RootElement.Clone();
    }
}
