using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Web.Components.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 304: everything waiting on a person, at the top of the home page. Ordering, what each entry
/// opens, and the cross-workspace switch (the same shape <c>HomeLiveSessionsTests</c> already drives for
/// the live-sessions card).
/// </summary>
public sealed class WaitingForYouPanelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private sealed class FakeList(IReadOnlyList<WaitingForPersonEntry> entries) : IWaitingForPersonList
    {
        public Task<IReadOnlyList<WaitingForPersonEntry>> CurrentAsync(CancellationToken ct = default) =>
            Task.FromResult(entries);
    }

    private sealed class FakeWorkspaceStore : IWorkspaceStore
    {
        private readonly List<WorkspaceRecord> _records = [];

        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => _records.Take(max).ToList();

        public WorkspaceRecord? Get(string id) => _records.FirstOrDefault(r => r.Id == id);

        public void Touch(string id, string name, string rootPath)
        {
            _records.RemoveAll(r => r.Id == id);
            _records.Insert(0, new WorkspaceRecord(id, name, rootPath, DateTimeOffset.UtcNow));
        }

        public void Remove(string id) => _records.RemoveAll(r => r.Id == id);

        /// <summary>A folder set up in a project.</summary>
        public void Bound(string id, string name, string projectId)
        {
            _records.RemoveAll(r => r.Id == id);
            _records.Insert(0, new WorkspaceRecord(id, name, @"C:\repos\" + name, DateTimeOffset.UtcNow, projectId));
        }
    }

    private sealed class ScriptedBridgeClient(BridgeWorkspaceContext context, Func<string, BridgeRegistration> onRegister)
        : BridgeClient(new HttpClient(), context)
    {
        public List<string> Registered { get; } = [];

        public override Task<BridgeRegistration> RegisterWorkspaceAsync(string path, CancellationToken ct)
        {
            Registered.Add(path);
            return Task.FromResult(onRegister(path));
        }
    }

    private sealed class Harness : IDisposable
    {
        public required BunitContext Ctx { get; init; }
        public required ScriptedBridgeClient Bridge { get; init; }

        public NavigationManager Nav => Ctx.Services.GetRequiredService<NavigationManager>();

        public void Dispose() => Ctx.Dispose();
    }

    private static Harness Context(
        IReadOnlyList<WaitingForPersonEntry> entries,
        string? activeWorkspaceId = "w1",
        Func<string, BridgeRegistration>? onRegister = null,
        Action<FakeWorkspaceStore>? folders = null,
        Fravashi.Application.Projects.IProjectStore? projects = null)
    {
        var ctx = new BunitContext();
        var values = new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9",
            [BridgeWorkspaceContext.TokenKey] = "t",
        };
        if (activeWorkspaceId is not null)
        {
            values[BridgeWorkspaceContext.ActiveIdKey] = activeWorkspaceId;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var bridgeContext = new BridgeWorkspaceContext(configuration);
        var bridge = new ScriptedBridgeClient(bridgeContext, onRegister ?? (_ => BridgeRegistration.Missing));
        var workspaces = new FakeWorkspaceStore();
        workspaces.Touch("w2", "gadgets", @"C:\repos\gadgets");
        folders?.Invoke(workspaces);
        if (projects is not null)
        {
            ctx.Services.AddSingleton(projects);
        }

        ctx.Services.AddSingleton(configuration);
        ctx.Services.AddSingleton(bridgeContext);
        ctx.Services.AddSingleton<BridgeClient>(bridge);
        ctx.Services.AddSingleton<IWorkspaceStore>(workspaces);
        ctx.Services.AddSingleton<IWaitingForPersonList>(new FakeList(entries));
        ctx.Services.AddLogging();

        return new Harness { Ctx = ctx, Bridge = bridge };
    }

    [Fact]
    public void An_empty_list_renders_nothing()
    {
        using var harness = Context([]);

        var cut = harness.Ctx.Render<WaitingForYouPanel>();

        Assert.Empty(cut.FindAll("[data-testid='waiting-for-you']"));
    }

    [Fact]
    public void Entries_render_oldest_first_with_their_since_and_a_button()
    {
        var now = DateTimeOffset.UtcNow;
        var newer = new WaitingForPersonEntry(WaitingForPersonKind.Approval, "Waiting on your approval: Bash", now, "w1", SessionId: new AgentSessionId(Guid.NewGuid()));
        var older = new WaitingForPersonEntry(WaitingForPersonKind.Question, "Waiting on your answer: Tabs or spaces?", now.AddHours(-1), "w1", SessionId: new AgentSessionId(Guid.NewGuid()));
        using var harness = Context([newer, older]);

        var cut = harness.Ctx.Render<WaitingForYouPanel>();

        var rows = cut.FindAll("[data-testid='waiting-for-you-row']");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Waiting on your answer: Tabs or spaces?", rows[0].TextContent);
        Assert.Contains("Waiting on your approval: Bash", rows[1].TextContent);
        Assert.Equal("2", cut.Find("[data-testid='waiting-for-you-count']").TextContent);
    }

    [Fact]
    public void Every_row_names_its_project_because_the_list_spans_every_project()
    {
        // Item #373, the v0.9.100 check: Home for an unbound Stomme listed Default's #23 and #24 with nothing saying
        // they were Default's. A row says whose it is, on every Home.
        var now = DateTimeOffset.UtcNow;
        var parked = new WaitingForPersonEntry(WaitingForPersonKind.Parked, "#23 Add a usage page under docs is parked: push it.",
            now.AddHours(-3), "w9", Project: new Fravashi.Domain.Projects.ProjectId("default"), ItemNumber: 23);
        var unbound = new WaitingForPersonEntry(WaitingForPersonKind.Question, "Waiting on your answer: Tabs or spaces?",
            now.AddHours(-2), "w2", SessionId: new AgentSessionId(Guid.NewGuid()));
        var bound = new WaitingForPersonEntry(WaitingForPersonKind.Approval, "Waiting on your approval: Bash",
            now.AddHours(-1), "w3", SessionId: new AgentSessionId(Guid.NewGuid()));
        var same = new WaitingForPersonEntry(WaitingForPersonKind.Approval, "Waiting on your approval: Edit",
            now, "w4", SessionId: new AgentSessionId(Guid.NewGuid()));
        using var harness = Context([parked, unbound, bound, same],
            folders: f =>
            {
                f.Bound("w3", "shop-web", "shop");
                f.Bound("w4", "Fravashi", "dd");
            },
            projects: new NamedProjectStore(("default", "Default"), ("shop", "Shop"), ("dd", "Fravashi")));

        var cut = harness.Ctx.Render<WaitingForYouPanel>();

        var owners = cut.FindAll("[data-testid='waiting-for-you-project']").Select(e => e.TextContent).ToArray();
        Assert.Equal(["Default", "gadgets (not set up)", "Shop · shop-web", "Fravashi"], owners);
        var first = cut.FindAll("[data-testid='waiting-for-you-row']")[0].QuerySelector(".waiting-for-you-what")!.TextContent;
        Assert.StartsWith("Default · #23 Add a usage page", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clicking_an_approval_row_in_this_workspace_navigates_without_switching()
    {
        var session = new AgentSessionId(Guid.NewGuid());
        var entry = new WaitingForPersonEntry(WaitingForPersonKind.Approval, "Waiting on your approval: Bash", DateTimeOffset.UtcNow, "w1", SessionId: session);
        using var harness = Context([entry], activeWorkspaceId: "w1");
        var cut = harness.Ctx.Render<WaitingForYouPanel>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='waiting-for-you-open']").Click());

        Assert.EndsWith($"/sessions/{session.Value:D}", harness.Nav.Uri);
        Assert.Empty(harness.Bridge.Registered);
    }

    [Fact]
    public async Task Clicking_a_row_in_another_workspace_switches_through_the_reopen_path()
    {
        var session = new AgentSessionId(Guid.NewGuid());
        var entry = new WaitingForPersonEntry(WaitingForPersonKind.Question, "Waiting on your answer: ...", DateTimeOffset.UtcNow, "w2", SessionId: session);
        using var harness = Context(
            [entry],
            activeWorkspaceId: "w1",
            onRegister: path => BridgeRegistration.Registered(new BridgeWorkspaceDto("w2", "gadgets", path)));
        var cut = harness.Ctx.Render<WaitingForYouPanel>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='waiting-for-you-open']").Click());

        cut.WaitForAssertion(() => Assert.EndsWith($"/sessions/{session.Value:D}?ws=w2", harness.Nav.Uri), Patience);
        Assert.Equal([@"C:\repos\gadgets"], harness.Bridge.Registered);
    }

    [Fact]
    public async Task A_session_whose_folder_re_registers_under_a_different_id_is_not_navigated_there()
    {
        var session = new AgentSessionId(Guid.NewGuid());
        var entry = new WaitingForPersonEntry(WaitingForPersonKind.Question, "Waiting on your answer: ...", DateTimeOffset.UtcNow, "w2", SessionId: session);
        using var harness = Context(
            [entry],
            activeWorkspaceId: "w1",
            // The folder now registers under w3, not the w2 this session actually belongs to.
            onRegister: path => BridgeRegistration.Registered(new BridgeWorkspaceDto("w3", "gadgets", path)));
        var cut = harness.Ctx.Render<WaitingForYouPanel>();
        var before = harness.Nav.Uri;

        await cut.InvokeAsync(() => cut.Find("[data-testid='waiting-for-you-open']").Click());

        Assert.Equal(before, harness.Nav.Uri);
        Assert.Contains("a different id", cut.Find("[data-testid='waiting-for-you-message']").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid='waiting-for-you-sessions-instead']"));
    }

    [Fact]
    public async Task A_parked_row_opens_its_explanation_and_from_there_the_autopilot_card()
    {
        // Work item #356: a parked row explains itself first; the Autopilot card (#358, the retired autopilot page before it) is
        // one of its actions.
        var entry = new WaitingForPersonEntry(WaitingForPersonKind.Parked, "#7 Fix the thing is parked: waiting", DateTimeOffset.UtcNow, "w1");
        using var harness = Context([entry], activeWorkspaceId: "w1");
        var cut = harness.Ctx.Render<WaitingForYouPanel>();
        var before = harness.Nav.Uri;

        await cut.InvokeAsync(() => cut.Find("[data-testid='waiting-for-you-open']").Click());

        Assert.Equal(before, harness.Nav.Uri);
        await cut.InvokeAsync(() => cut.Find("[data-testid='waiting-for-you-open-autopilot']").Click());
        Assert.EndsWith("/#autopilot", harness.Nav.Uri);
    }

    /// <summary>Backlog 307's approvals inbox: a narrower view of the SAME list, restricted to one kind.</summary>
    [Fact]
    public void KindFilter_restricts_the_rendered_entries_to_that_kind()
    {
        var now = DateTimeOffset.UtcNow;
        var approval = new WaitingForPersonEntry(WaitingForPersonKind.Approval, "Waiting on your approval: Bash", now, "w1", SessionId: new AgentSessionId(Guid.NewGuid()));
        var question = new WaitingForPersonEntry(WaitingForPersonKind.Question, "Waiting on your answer: Tabs or spaces?", now.AddHours(-1), "w1", SessionId: new AgentSessionId(Guid.NewGuid()));
        using var harness = Context([approval, question]);

        var cut = harness.Ctx.Render<WaitingForYouPanel>(p => p.Add(x => x.KindFilter, WaitingForPersonKind.Approval));

        var rows = cut.FindAll("[data-testid='waiting-for-you-row']");
        Assert.Single(rows);
        Assert.Contains("Waiting on your approval: Bash", rows[0].TextContent);
    }

    /// <summary>
    /// The inbox's own acceptance (#307): oldest first among approvals, each opening its own session, proven
    /// on the FILTERED view itself rather than only on the unfiltered Home panel.
    /// </summary>
    [Fact]
    public async Task The_inbox_view_orders_approvals_oldest_first_and_each_opens_its_own_session()
    {
        var now = DateTimeOffset.UtcNow;
        var olderSession = new AgentSessionId(Guid.NewGuid());
        var newerSession = new AgentSessionId(Guid.NewGuid());
        var olderApproval = new WaitingForPersonEntry(WaitingForPersonKind.Approval, "Waiting on your approval: git_commit", now.AddHours(-2), "w1", SessionId: olderSession);
        var newerApproval = new WaitingForPersonEntry(WaitingForPersonKind.Approval, "Waiting on your approval: Bash", now.AddHours(-1), "w1", SessionId: newerSession);
        var question = new WaitingForPersonEntry(WaitingForPersonKind.Question, "Waiting on your answer: ...", now, "w1", SessionId: new AgentSessionId(Guid.NewGuid()));
        using var harness = Context([newerApproval, question, olderApproval], activeWorkspaceId: "w1");

        var cut = harness.Ctx.Render<WaitingForYouPanel>(p => p
            .Add(x => x.KindFilter, WaitingForPersonKind.Approval)
            .Add(x => x.HeadingLabel, "Approvals inbox")
            .Add(x => x.PanelTestId, "approvals-inbox"));

        var rows = cut.FindAll("[data-testid='waiting-for-you-row']");
        Assert.Equal(2, rows.Count);
        Assert.Contains("git_commit", rows[0].TextContent);
        Assert.Contains("Bash", rows[1].TextContent);

        await cut.InvokeAsync(() => rows[0].QuerySelector("[data-testid='waiting-for-you-open']")!.Click());

        Assert.EndsWith($"/sessions/{olderSession.Value:D}", harness.Nav.Uri);
    }

    /// <summary>The approvals inbox renders nothing when there is no PENDING APPROVAL, even if something else waits.</summary>
    [Fact]
    public void KindFilter_renders_nothing_when_no_entry_matches_even_though_others_wait()
    {
        var question = new WaitingForPersonEntry(WaitingForPersonKind.Question, "Waiting on your answer: ...", DateTimeOffset.UtcNow, "w1", SessionId: new AgentSessionId(Guid.NewGuid()));
        using var harness = Context([question]);

        var cut = harness.Ctx.Render<WaitingForYouPanel>(p => p.Add(x => x.KindFilter, WaitingForPersonKind.Approval));

        Assert.Empty(cut.FindAll("[data-testid='waiting-for-you-row']"));
    }

    /// <summary>
    /// PanelTestId and HeadingLabel let a second instance of this panel (the approvals inbox, on the Sessions
    /// page) carry its own section and count test ids without colliding with the Home page's own "waiting-for-you".
    /// Row-level ids are untouched: the two panels never render on the same page at once.
    /// </summary>
    [Fact]
    public void PanelTestId_and_HeadingLabel_customise_the_sections_own_ids_and_heading()
    {
        var entry = new WaitingForPersonEntry(WaitingForPersonKind.Approval, "Waiting on your approval: Bash", DateTimeOffset.UtcNow, "w1", SessionId: new AgentSessionId(Guid.NewGuid()));
        using var harness = Context([entry]);

        var cut = harness.Ctx.Render<WaitingForYouPanel>(p => p
            .Add(x => x.KindFilter, WaitingForPersonKind.Approval)
            .Add(x => x.HeadingLabel, "Approvals inbox")
            .Add(x => x.PanelTestId, "approvals-inbox"));

        Assert.NotEmpty(cut.FindAll("[data-testid='approvals-inbox']"));
        Assert.Equal("1", cut.Find("[data-testid='approvals-inbox-count']").TextContent);
        Assert.Contains("Approvals inbox", cut.Find("[data-testid='approvals-inbox']").TextContent);
        // Row-level ids stay the same; the two panels never render on the same page at once.
        Assert.NotEmpty(cut.FindAll("[data-testid='waiting-for-you-row']"));
    }
}
