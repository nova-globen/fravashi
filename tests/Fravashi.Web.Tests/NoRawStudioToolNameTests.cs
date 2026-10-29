using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Web.Components.Dashboard;
using Fravashi.Web.Components.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #356: no mcp__ text anywhere a person reads, outside the raw view a person opens on purpose. The approval
/// card's header said "Needs your approval mcp__fravashi__work_item_update"; Home's lists and the inbox said "Waiting on
/// your approval: mcp__fravashi__work_item_update". The live-sessions card and the transcript notes are held in
/// HomeLiveSessionsTests and SessionsUiTests, the waiting reader in Infrastructure.Tests.
/// </summary>
public sealed class NoRawStudioToolNameTests
{
    private const string Raw = "mcp__fravashi__";

    /// <summary>The visible text with every collapsed raw view (a details block) taken out.</summary>
    private static string VisibleText(AngleSharp.Dom.IElement root)
    {
        var copy = (AngleSharp.Dom.IElement)root.Clone(deep: true);
        foreach (var raw in copy.QuerySelectorAll("details, .session-tool-raw").ToList())
        {
            raw.Remove();
        }

        return copy.TextContent;
    }

    [Fact]
    public void The_approval_cards_header_names_a_studio_tool_in_words_and_keeps_the_raw_name_in_the_raw_view()
    {
        using var ctx = new BunitContext();
        var request = new PendingToolApproval(
            "r1", AgentSessionId.New(), "mcp__fravashi__work_item_update", "#23", "t1",
            new Dictionary<string, string> { ["number"] = "23", ["description"] = "x" }, DateTimeOffset.UtcNow);

        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, request));

        Assert.Equal("work item update", cut.Find("[data-testid=approval-tool]").TextContent.Trim());
        Assert.DoesNotContain(Raw, VisibleText(cut.Find("[data-testid=approval-prompt]")));
        Assert.Contains("mcp__fravashi__work_item_update", cut.Find("[data-testid=approval-raw-name]").TextContent);
    }

    [Fact]
    public void A_built_in_tool_keeps_its_own_name_on_the_card()
    {
        using var ctx = new BunitContext();
        var request = new PendingToolApproval(
            "r1", AgentSessionId.New(), "Bash", "ls", "t1",
            new Dictionary<string, string> { ["command"] = "ls" }, DateTimeOffset.UtcNow);

        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, request));

        Assert.Equal("Bash", cut.Find("[data-testid=approval-tool]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=approval-raw-name]"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(WaitingForPersonKind.Approval)]
    public void Waiting_for_you_and_the_approvals_inbox_never_show_a_raw_name(WaitingForPersonKind? filter)
    {
        using var ctx = new BunitContext();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9",
            [BridgeWorkspaceContext.TokenKey] = "t",
            [BridgeWorkspaceContext.ActiveIdKey] = "w1",
        }).Build();
        var bridgeContext = new BridgeWorkspaceContext(configuration);
        ctx.Services.AddSingleton(configuration);
        ctx.Services.AddSingleton(bridgeContext);
        ctx.Services.AddSingleton(new BridgeClient(new HttpClient(), bridgeContext));
        ctx.Services.AddSingleton<IWorkspaceStore>(new NoWorkspaces());
        // An entry worded before #356 (or by any older writer) still carrying the raw name: the panel puts it into words.
        ctx.Services.AddSingleton<IWaitingForPersonList>(new OneEntry(new WaitingForPersonEntry(
            WaitingForPersonKind.Approval, "Waiting on your approval: mcp__fravashi__work_item_update",
            DateTimeOffset.UtcNow, "w1", SessionId: AgentSessionId.New())));
        ctx.Services.AddLogging();

        var cut = ctx.Render<WaitingForYouPanel>(p => p.Add(c => c.KindFilter, filter));

        var row = cut.Find("[data-testid=waiting-for-you-row]");
        Assert.DoesNotContain(Raw, row.TextContent);
        Assert.Contains("work item update", row.TextContent);
    }

    private sealed class OneEntry(WaitingForPersonEntry entry) : IWaitingForPersonList
    {
        public Task<IReadOnlyList<WaitingForPersonEntry>> CurrentAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WaitingForPersonEntry>>([entry]);
    }

    private sealed class NoWorkspaces : IWorkspaceStore
    {
        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => [];

        public WorkspaceRecord? Get(string id) => null;

        public void Touch(string id, string name, string rootPath)
        {
        }

        public void Remove(string id)
        {
        }
    }
}
