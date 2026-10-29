using Bunit;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Web.Components.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 34: the home surface's recent-conversations card. The ten newest conversations across EVERY
/// workspace, each with the folder it belongs to, its title, when it started and how it ended, and the
/// three actions on it: resume, open in the sessions area, and delete behind a confirmation.
/// </summary>
public class HomeRecentConversationsTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

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

        /// <summary>Item #373: puts a folder in a project.</summary>
        public void Bind(string id, string project)
        {
            var index = _records.FindIndex(r => r.Id == id);
            _records[index] = _records[index] with { ProjectId = project };
        }
    }

    private sealed class ScriptedBridgeClient(
        BridgeWorkspaceContext context, Func<string, BridgeRegistration>? onRegister = null)
        : BridgeClient(new HttpClient(), context)
    {
        /// <summary>Every host path the card asked the bridge to grant, in order.</summary>
        public List<string> Registered { get; } = [];

        public override Task<BridgeRegistration> RegisterWorkspaceAsync(string path, CancellationToken ct)
        {
            Registered.Add(path);
            return Task.FromResult(onRegister is null ? BridgeRegistration.Missing : onRegister(path));
        }
    }

    private sealed class Harness : IDisposable
    {
        public required BunitContext Ctx { get; init; }
        public required RecordingSessionStore Sessions { get; init; }
        public required FakeWorkspaceStore Workspaces { get; init; }
        public required ScriptedBridgeClient Bridge { get; init; }

        public LiveSessionRegistry Registry => Ctx.Services.GetRequiredService<LiveSessionRegistry>();

        public NavigationManager Nav => Ctx.Services.GetRequiredService<NavigationManager>();

        public void Dispose() => Ctx.Dispose();
    }

    private static Harness Context(
        string? activeWorkspaceId = null,
        bool bridgeConfigured = true,
        Func<string, BridgeRegistration>? onRegister = null)
    {
        var ctx = new BunitContext();
        var values = new Dictionary<string, string?>();
        if (bridgeConfigured)
        {
            values[BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9";
            values[BridgeWorkspaceContext.TokenKey] = "t";
        }

        if (activeWorkspaceId is not null)
        {
            values[BridgeWorkspaceContext.ActiveIdKey] = activeWorkspaceId;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var bridgeContext = new BridgeWorkspaceContext(configuration);
        var client = new ScriptedBridgeClient(bridgeContext, onRegister);
        var workspaces = new FakeWorkspaceStore();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(configuration);
        ctx.Services.AddSingleton(bridgeContext);
        ctx.Services.AddSingleton<BridgeClient>(client);
        ctx.Services.AddSingleton<IWorkspaceStore>(workspaces);
        ctx.Services.AddLogging();
        var sessions = ctx.Services.AddSessionSurface();

        return new Harness { Ctx = ctx, Sessions = sessions, Workspaces = workspaces, Bridge = client };
    }

    private static SessionRecord Conversation(
        string workspaceId,
        string title,
        DateTimeOffset started,
        AgentSessionState state = AgentSessionState.Completed,
        string? customTitle = null,
        string? providerSessionId = "cli-1") =>
        new(AgentSessionId.New(), workspaceId, AgentTool.Claude, title, providerSessionId, state, started, null,
            customTitle);

    // ---- what the card lists ----------------------------------------------------------------

    [Fact]
    public void The_most_recent_conversations_across_every_workspace_are_listed_newest_first()
    {
        using var harness = Context();
        harness.Workspaces.Touch("w1", "widgets", @"C:\repos\widgets");
        harness.Workspaces.Touch("w2", "gadgets", @"C:\repos\gadgets");
        // Twelve conversations, alternating workspaces, oldest first. The card used to cut at ten, because it
        // could neither scroll nor be searched. Since the Home rework (2026-09-21) it does both, so all twelve
        // are there, newest first, and the order is across both workspaces rather than per workspace.
        for (var i = 0; i < 12; i++)
        {
            harness.Sessions.Add(Conversation(
                i % 2 == 0 ? "w1" : "w2", $"conversation {i}", Started.AddHours(i)));
        }

        var cut = harness.Ctx.Render<RecentConversationsCard>();

        var rows = cut.FindAll("[data-testid='recent-conversation']");
        Assert.Equal(12, rows.Count);
        Assert.Contains("conversation 11", rows[0].TextContent);
        Assert.Contains("conversation 0", rows[11].TextContent);
        Assert.Contains("home-scroll", cut.Find(".recent-conversation-list").ClassList);
        // Both workspaces are represented, named by the folder name the bridge registered.
        Assert.Contains("widgets", cut.Markup);
        Assert.Contains("gadgets", cut.Markup);
    }

    [Fact]
    public async Task Typing_in_the_filter_narrows_the_conversations_by_title_folder_or_state()
    {
        using var harness = Context();
        harness.Workspaces.Touch("w1", "widgets", @"C:\repos\widgets");
        harness.Workspaces.Touch("w2", "gadgets", @"C:\repos\gadgets");
        harness.Sessions.Add(Conversation("w1", "Add the readme", Started));
        harness.Sessions.Add(Conversation("w2", "Fix the login page", Started.AddHours(1), AgentSessionState.Failed));
        var cut = harness.Ctx.Render<RecentConversationsCard>();
        Assert.Equal(2, cut.FindAll("[data-testid='recent-conversation']").Count);

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversations-filter']").Input("README"));
        Assert.Contains("Add the readme", Assert.Single(cut.FindAll("[data-testid='recent-conversation']")).TextContent);

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversations-filter']").Input("gadgets"));
        Assert.Contains("Fix the login page", Assert.Single(cut.FindAll("[data-testid='recent-conversation']")).TextContent);

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversations-filter']").Input("no such thing"));
        Assert.Empty(cut.FindAll("[data-testid='recent-conversation']"));
        Assert.Contains("No conversation matches", cut.Find("[data-testid='recent-conversations-nomatch']").TextContent);
    }

    [Fact]
    public async Task Every_row_names_its_project_and_the_filter_finds_it_by_its_project()
    {
        // Item #373: the card spans every project, so a row says whose it is; a folder in no project says so.
        using var harness = Context();
        harness.Ctx.Services.AddSingleton<Fravashi.Application.Projects.IProjectStore>(new NamedProjectStore(("shop", "Shop")));
        harness.Workspaces.Touch("w1", "widgets", @"C:\repos\widgets");
        harness.Workspaces.Touch("w2", "gadgets", @"C:\repos\gadgets");
        harness.Workspaces.Bind("w2", "shop");
        harness.Sessions.Add(Conversation("w1", "Add the readme", Started));
        harness.Sessions.Add(Conversation("w2", "Fix the login page", Started.AddHours(1)));

        var cut = harness.Ctx.Render<RecentConversationsCard>();

        var labels = cut.FindAll(".recent-conversation-workspace").Select(e => e.TextContent).ToArray();
        Assert.Equal(["Shop · gadgets", "widgets (not set up)"], labels);
        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversations-filter']").Input("shop"));
        Assert.Contains("Fix the login page", Assert.Single(cut.FindAll("[data-testid='recent-conversation']")).TextContent);
    }

    [Fact]
    public void A_row_shows_its_workspace_its_title_when_it_started_and_its_outcome()
    {
        using var harness = Context();
        harness.Workspaces.Touch("w1", "widgets", @"C:\repos\widgets");
        harness.Sessions.Add(Conversation("w1", "Add the readme", Started, AgentSessionState.Failed));

        var cut = harness.Ctx.Render<RecentConversationsCard>();

        var row = cut.Find("[data-testid='recent-conversation']");
        Assert.Contains("Add the readme", row.TextContent);
        Assert.Contains("widgets", row.TextContent);
        Assert.Contains("failed", row.TextContent);
        // Backlog 246: no person's zone is registered here, so the time is in UTC and says so.
        Assert.Contains(
            Started.UtcDateTime.ToString("MMM d, HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC",
            row.TextContent);
    }

    [Fact]
    public void A_renamed_conversation_shows_the_name_the_owner_chose()
    {
        // Backlog 109's DisplayTitle, not the derived title.
        using var harness = Context();
        harness.Sessions.Add(Conversation("w1", "Add the readme", Started, customTitle: "Readme rewrite"));

        var cut = harness.Ctx.Render<RecentConversationsCard>();

        Assert.Contains("Readme rewrite", cut.Markup);
        Assert.DoesNotContain("Add the readme", cut.Markup);
    }

    [Fact]
    public void A_conversation_whose_recents_row_is_gone_still_lists_under_its_workspace_id()
    {
        // A recents row is droppable (backlog 14); the conversation is not, so it degrades to the id
        // rather than disappearing from the card.
        using var harness = Context();
        harness.Sessions.Add(Conversation("w-unknown", "Add the readme", Started));

        var cut = harness.Ctx.Render<RecentConversationsCard>();

        Assert.Contains("w-unknown", cut.Find("[data-testid='recent-conversation']").TextContent);
    }

    [Fact]
    public void With_no_conversations_the_card_says_so_rather_than_rendering_a_blank_box()
    {
        using var harness = Context();

        var cut = harness.Ctx.Render<RecentConversationsCard>();

        Assert.Contains("No conversations yet", cut.Find("[data-testid='recent-conversations-empty']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='recent-conversation']"));
    }

    // ---- opening: this workspace and another one ---------------------------------------------

    [Fact]
    public async Task Clicking_a_conversation_in_this_workspace_opens_it_without_switching_workspace()
    {
        using var harness = Context(activeWorkspaceId: "w1");
        var conversation = Conversation("w1", "Add the readme", Started);
        harness.Sessions.Add(conversation);
        var cut = harness.Ctx.Render<RecentConversationsCard>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation']").Click());

        cut.WaitForAssertion(() =>
            Assert.EndsWith($"/sessions/{conversation.Id.Value:D}", harness.Nav.Uri, StringComparison.Ordinal));
        Assert.Empty(harness.Bridge.Registered);
    }

    [Fact]
    public async Task Resume_arms_the_conversation_rather_than_only_opening_it()
    {
        using var harness = Context(activeWorkspaceId: "w1");
        var conversation = Conversation("w1", "Add the readme", Started);
        harness.Sessions.Add(conversation);
        var cut = harness.Ctx.Render<RecentConversationsCard>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation-resume']").Click());

        cut.WaitForAssertion(() => Assert.EndsWith(
            $"/sessions/{conversation.Id.Value:D}?resume=true", harness.Nav.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_conversation_in_another_workspace_is_registered_with_the_host_before_the_window_switches()
    {
        // The adoption path the home page's "open another folder" uses, plus the target session: an
        // explicit ws on the URL wins over the window's own, so the switch CAN carry the conversation.
        using var harness = Context(
            activeWorkspaceId: "w1",
            onRegister: path => BridgeRegistration.Registered(new BridgeWorkspaceDto("w2", "gadgets", path)));
        harness.Workspaces.Touch("w2", "gadgets", @"C:\repos\gadgets");
        var conversation = Conversation("w2", "Fix the build", Started);
        harness.Sessions.Add(conversation);
        var cut = harness.Ctx.Render<RecentConversationsCard>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation-open']").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal([@"C:\repos\gadgets"], harness.Bridge.Registered);
            Assert.EndsWith(
                $"/sessions/{conversation.Id.Value:D}?ws=w2", harness.Nav.Uri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Resuming_a_conversation_in_another_workspace_carries_both_the_workspace_and_the_resume()
    {
        using var harness = Context(
            activeWorkspaceId: "w1",
            onRegister: path => BridgeRegistration.Registered(new BridgeWorkspaceDto("w2", "gadgets", path)));
        harness.Workspaces.Touch("w2", "gadgets", @"C:\repos\gadgets");
        var conversation = Conversation("w2", "Fix the build", Started);
        harness.Sessions.Add(conversation);
        var cut = harness.Ctx.Render<RecentConversationsCard>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation-resume']").Click());

        cut.WaitForAssertion(() => Assert.EndsWith(
            $"/sessions/{conversation.Id.Value:D}?ws=w2&resume=true", harness.Nav.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_folder_that_is_not_on_this_machine_says_so_and_nothing_is_opened()
    {
        using var harness = Context(activeWorkspaceId: "w1", onRegister: _ => BridgeRegistration.Missing);
        harness.Workspaces.Touch("w2", "gadgets", @"C:\repos\gadgets");
        harness.Sessions.Add(Conversation("w2", "Fix the build", Started));
        var cut = harness.Ctx.Render<RecentConversationsCard>();
        var before = harness.Nav.Uri;

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation']").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "was not found", cut.Find("[data-testid='recent-conversation-message']").TextContent));
        Assert.Equal(before, harness.Nav.Uri);
    }

    [Fact]
    public async Task An_unreachable_folder_does_not_claim_the_folder_is_gone()
    {
        using var harness = Context(activeWorkspaceId: "w1", onRegister: _ => BridgeRegistration.Unreachable);
        harness.Workspaces.Touch("w2", "gadgets", @"Z:\repos\gadgets");
        harness.Sessions.Add(Conversation("w2", "Fix the build", Started));
        var cut = harness.Ctx.Render<RecentConversationsCard>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation']").Click());

        cut.WaitForAssertion(() =>
        {
            var message = cut.Find("[data-testid='recent-conversation-message']").TextContent;
            Assert.Contains("could not be reached", message);
            Assert.DoesNotContain("not found", message);
        });
    }

    [Fact]
    public async Task A_folder_that_registers_under_a_different_id_offers_that_folders_sessions_instead()
    {
        // The one case the adoption path cannot carry a target session: the conversation belongs to an id
        // that no longer names the workspace, so landing on it would show a pane with the conversation
        // nowhere in it. The reader is told why, and taken to that folder's sessions area on request.
        using var harness = Context(
            activeWorkspaceId: "w1",
            onRegister: path => BridgeRegistration.Registered(new BridgeWorkspaceDto("w2-canonical", "gadgets", path)));
        harness.Workspaces.Touch("w2", "gadgets", @"C:\repos\gadgets");
        harness.Sessions.Add(Conversation("w2", "Fix the build", Started));
        var cut = harness.Ctx.Render<RecentConversationsCard>();
        var before = harness.Nav.Uri;

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation']").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "different id", cut.Find("[data-testid='recent-conversation-message']").TextContent));
        Assert.Equal(before, harness.Nav.Uri);

        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='recent-conversation-workspace-instead']").Click());

        cut.WaitForAssertion(() =>
            Assert.EndsWith("/sessions?ws=w2-canonical", harness.Nav.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_bridge_the_workspace_switch_is_the_ws_query_alone()
    {
        // Host `dotnet run` dev fallback: there is no bridge to register with.
        using var harness = Context(activeWorkspaceId: "w1", bridgeConfigured: false);
        var conversation = Conversation("w2", "Fix the build", Started);
        harness.Sessions.Add(conversation);
        var cut = harness.Ctx.Render<RecentConversationsCard>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation']").Click());

        cut.WaitForAssertion(() => Assert.EndsWith(
            $"/sessions/{conversation.Id.Value:D}?ws=w2", harness.Nav.Uri, StringComparison.Ordinal));
        Assert.Empty(harness.Bridge.Registered);
    }

    // ---- deleting -----------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_conversation_takes_a_confirmation_and_goes_through_the_store()
    {
        using var harness = Context(activeWorkspaceId: "w1");
        var conversation = Conversation("w1", "Add the readme", Started);
        harness.Sessions.Add(conversation);
        var cut = harness.Ctx.Render<RecentConversationsCard>();

        // Asking is not doing.
        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation-delete-ask']").Click());
        cut.WaitForAssertion(() => Assert.Contains(
            "forgets the conversation", cut.Find("[data-testid='recent-conversation-delete-hint']").TextContent));
        Assert.Empty(harness.Sessions.Deleted);

        // Backing out keeps it.
        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation-delete-cancel']").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='recent-conversation-delete']")));
        Assert.Empty(harness.Sessions.Deleted);

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation-delete-ask']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation-delete']").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal([conversation.Id], harness.Sessions.Deleted);
            Assert.Empty(cut.FindAll("[data-testid='recent-conversation']"));
        });
    }

    [Fact]
    public async Task A_running_conversation_is_not_deleted_out_from_under_its_own_session()
    {
        using var harness = Context(activeWorkspaceId: "w1");
        // A real live session: the silent runner never ends it, so it is still running.
        harness.Registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "write the readme"));
        var cut = harness.Ctx.Render<RecentConversationsCard>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation-delete-ask']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='recent-conversation-delete']").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "still running", cut.Find("[data-testid='recent-conversation-message']").TextContent));
        Assert.Empty(harness.Sessions.Deleted);
        Assert.Single(cut.FindAll("[data-testid='recent-conversation']"));
    }
}
