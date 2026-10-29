using Bunit;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Web.Components.Code;
using Fravashi.Web.Components.Sessions;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #366: a conversation beside the code, and the editor following it. The rules themselves are held in
/// <see cref="AgentFollowRulesTests"/>; these hold what the page does with them, above all that the diff's Approve
/// is the SAME decision as the card's, bound to the same pending approval.
/// </summary>
public sealed partial class CodePageTests
{
    private static readonly TimeSpan FollowWait = TimeSpan.FromSeconds(15);

    private (BunitContext Ctx, LiveSessionRegistry Registry, AgentApprovalBroker Broker, AgentPanelState Panel) FollowContext()
    {
        var ctx = PageContext();
        ctx.Services.GetRequiredService<Fravashi.Infrastructure.Bridge.WorkspaceSelection>().Capture("w1");
        ctx.Services.GetRequiredService<FakeWorkspaceAgentSync>().CopyPath = "/workspaces/w1";
        ctx.Services.GetRequiredService<FakeReader>().Contents["README.md"] = "old readme\n";
        return (
            ctx,
            ctx.Services.GetRequiredService<LiveSessionRegistry>(),
            (AgentApprovalBroker)ctx.Services.GetRequiredService<IAgentApprovalBroker>(),
            ctx.Services.GetRequiredService<AgentPanelState>());
    }

    private static LiveAgentSession StartFollowedSession(LiveSessionRegistry registry, AgentApprovalBroker broker)
    {
        var session = registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "write the readme"));
        broker.RegisterSession(session.Id, "/workspaces/w1");
        return session;
    }

    private static Task<ToolApprovalDecision> RequestWrite(AgentApprovalBroker broker, LiveAgentSession session, string content = "new readme\n") =>
        broker.RequestAsync(session.Id, "Write", "toolu_1", new Dictionary<string, string>
        {
            ["file_path"] = "/workspaces/w1/README.md",
            ["content"] = content,
        });

    // ---- 1. discoverable ----

    [Fact]
    public void The_editor_header_has_a_labelled_agent_toggle_that_opens_the_panel()
    {
        using var ctx = PageContext();
        var panel = ctx.Services.GetRequiredService<AgentPanelState>();
        var cut = ctx.Render<CodePage>();

        var toggle = cut.Find("[data-testid=code-agent-toggle]");
        Assert.Equal("Agent", toggle.TextContent.Trim());
        Assert.Equal("false", toggle.GetAttribute("aria-pressed"));

        cut.InvokeAsync(() => cut.Find("[data-testid=code-agent-toggle]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-session-panel]")));
        Assert.True(panel.IsOpen);
        Assert.Equal("true", cut.Find("[data-testid=code-agent-toggle]").GetAttribute("aria-pressed"));
    }

    // ---- 2. a chosen conversation beside the code ----

    [Fact]
    public void A_conversation_shown_beside_the_code_is_named_in_the_panel_and_goes_back_to_sessions()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var session = StartFollowedSession(registry, broker);
        panel.ShowBesideCode(session.Id);

        var cut = ctx.Render<CodePage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-session-panel]")));
        Assert.Equal(
            ConversationTitles.Display(session.CustomTitle, session.DerivedTitle),
            cut.Find("[data-testid=code-session-panel] .session-pane-title").TextContent.Trim());
        var back = cut.Find("[data-testid=code-session-open-area]");
        Assert.Equal($"/sessions/{session.Id.Value:D}", back.GetAttribute("href"));

        cut.InvokeAsync(() => cut.Find("[data-testid=code-session-open-area]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=code-session-panel]")));
        Assert.False(panel.IsOpen);
        Assert.Null(panel.SessionId);
    }

    // ---- 3. the shared approval ----

    [Fact]
    public async Task The_diff_header_and_its_buttons_sit_above_the_editor_never_over_it()
    {
        // Work item #369: laid over the Monaco host, Approve sat under the editor's minimap at the right
        // edge (1424 px window, agent panel open). The header is now a row of the editor column before the
        // host's wrap, so nothing Monaco draws can cover it; only the diff body covers the editor.
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var session = StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);

        var blocked = RequestWrite(broker, session);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-follow-approve]")), FollowWait);
        var approve = cut.Find("[data-testid=code-follow-approve]");
        var deny = cut.Find("[data-testid=code-follow-deny]");
        Assert.Null(approve.Closest(".code-editor-host-wrap"));
        Assert.Null(deny.Closest(".code-editor-host-wrap"));
        Assert.Null(approve.Closest(".code-editor-host"));

        var head = cut.Find("[data-testid=code-follow-diff]");
        var wrap = cut.Find(".code-editor-host-wrap");
        Assert.Same(wrap.ParentElement, head.ParentElement);
        var column = head.ParentElement!.Children.Select(c => c.ClassName ?? string.Empty).ToList();
        var headAt = column.FindIndex(c => c.Split(' ').Contains("code-follow-diff"));
        var wrapAt = column.FindIndex(c => c.Split(' ').Contains("code-editor-host-wrap"));
        Assert.True(
            headAt >= 0 && headAt < wrapAt,
            $"the diff header must come before the editor host, as a row above it: {string.Join(" | ", column)}");

        // The diff body is what covers the editor, inside the wrap.
        Assert.NotNull(cut.Find("[data-testid=code-follow-diff-body]").Closest(".code-editor-host-wrap"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=code-follow-deny]").Click());
        Assert.False((await blocked).Approved);
    }

    [Fact]
    public async Task A_pending_write_shows_as_a_diff_whose_approve_resolves_the_same_approval_as_the_card()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var session = StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);

        var blocked = RequestWrite(broker, session);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-follow-diff]")), FollowWait);
        var requestId = Assert.Single(session.Pending).RequestId;
        Assert.Equal(requestId, cut.Find("[data-testid=code-follow-diff]").GetAttribute("data-request"));
        Assert.Single(cut.FindAll("[data-testid=approval-prompt]"));

        // The diff is the HOST file against the proposed text.
        cut.WaitForAssertion(() =>
        {
            var diff = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiCode.openDiff");
            Assert.Equal(["README.md", "old readme\n", "new readme\n"], diff.Arguments.Select(a => a as string));
        }, FollowWait);

        await cut.InvokeAsync(() => cut.Find("[data-testid=code-follow-approve]").Click());

        Assert.True((await blocked).Approved);
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=code-follow-diff]"));
            Assert.Empty(cut.FindAll("[data-testid=approval-prompt]"));
        }, FollowWait);
    }

    [Fact]
    public async Task A_change_that_lands_on_the_host_is_highlighted_from_the_host_files_text()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var session = StartFollowedSession(registry, broker);
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);
        var blocked = RequestWrite(broker, session, "old readme\nadded line\n");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-follow-approve]")), FollowWait);
        await cut.InvokeAsync(() => cut.Find("[data-testid=code-follow-approve]").Click());
        await blocked;
        Assert.DoesNotContain(
            await cut.InvokeAsync(() => ctx.JSInterop.Invocations.ToList()), i => i.Identifier == "fravashiCode.highlightLines");

        // Nothing is assumed: the highlight waits for the host file to change, and comes from its text.
        reader.Contents["README.md"] = "old readme\nadded line\n";
        reader.Stamps["README.md"] = DateTimeOffset.UtcNow;
        ctx.Services.GetRequiredService<FakeNotifier>().Raise(
            new Fravashi.Application.Repository.RepositoryChange(Fravashi.Application.Repository.RepositoryChangeKind.ChangedOrCreated, "README.md"));

        cut.WaitForAssertion(() =>
        {
            var highlight = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiCode.highlightLines");
            Assert.Equal("README.md", highlight.Arguments[0]);
            Assert.Equal(2, highlight.Arguments[1]);
            Assert.Equal(2, highlight.Arguments[2]);
        }, FollowWait);
    }

    [Fact]
    public async Task A_diff_pushed_while_the_window_was_away_is_sent_again_when_it_reconnects()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var session = StartFollowedSession(registry, broker);
        var connection = ctx.Services.GetRequiredService<CircuitConnection>();
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);
        var blocked = RequestWrite(broker, session);
        cut.WaitForAssertion(
            () => Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiCode.openDiff"), FollowWait);

        connection.ConnectionDown();
        await cut.InvokeAsync(connection.ConnectionUp);

        cut.WaitForAssertion(
            () => Assert.Equal(2, ctx.JSInterop.Invocations.Count(i => i.Identifier == "fravashiCode.openDiff")), FollowWait);
        await cut.InvokeAsync(() => cut.Find("[data-testid=code-follow-deny]").Click());
        Assert.False((await blocked).Approved);
    }

    [Fact]
    public async Task Approving_on_the_card_takes_the_diff_away()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var session = StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);
        var blocked = RequestWrite(broker, session);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-follow-diff]")), FollowWait);

        await cut.InvokeAsync(() => cut.Find("[data-testid=approve]").Click());

        Assert.True((await blocked).Approved);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=code-follow-diff]")), FollowWait);
    }

    [Fact]
    public async Task Denying_in_the_diff_denies_the_agent_exactly_as_the_card_would()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var session = StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);
        var blocked = RequestWrite(broker, session);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-follow-deny]")), FollowWait);

        await cut.InvokeAsync(() => cut.Find("[data-testid=code-follow-deny]").Click());

        var decision = await blocked;
        Assert.False(decision.Approved);
        Assert.Equal(ToolApprovalDecidedBy.User, decision.DecidedBy);
    }

    [Fact]
    public async Task The_shared_resolution_refuses_an_approval_that_is_not_pending_on_that_session()
    {
        var (ctx, registry, broker, _) = FollowContext();
        using var __ = ctx;
        var session = StartFollowedSession(registry, broker);
        var blocked = RequestWrite(broker, session);
        await PollUntilAsync(() => session.Pending.Count == 1);
        var requestId = session.Pending[0].RequestId;

        Assert.False(SessionApprovalResolution.Answer(registry, session, "some-other-request", true, null));
        Assert.False(blocked.IsCompleted, "a different id must decide nothing");

        Assert.True(SessionApprovalResolution.Answer(registry, session, requestId, true, null));
        Assert.True((await blocked).Approved);

        // Decided once: the same id a second time (the other surface clicking late) changes nothing.
        await PollUntilAsync(() => session.Pending.Count == 0);
        Assert.False(SessionApprovalResolution.Answer(registry, session, requestId, false, null));
    }

    [Fact]
    public async Task An_edit_whose_old_text_is_not_in_the_file_shows_a_note_and_leaves_the_decision_to_the_card()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var session = StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);

        var blocked = broker.RequestAsync(session.Id, "Edit", "toolu_1", new Dictionary<string, string>
        {
            ["file_path"] = "/workspaces/w1/README.md",
            ["old_string"] = "text that is not there",
            ["new_string"] = "anything",
        });

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-follow-diff-problem]")), FollowWait);
        Assert.Empty(cut.FindAll("[data-testid=code-follow-approve]"));
        Assert.DoesNotContain(
            await cut.InvokeAsync(() => ctx.JSInterop.Invocations.ToList()), i => i.Identifier == "fravashiCode.openDiff");
        Assert.Single(cut.FindAll("[data-testid=approval-prompt]"));
        Assert.False(blocked.IsCompleted);

        await cut.InvokeAsync(() => cut.Find("[data-testid=deny]").Click());
        Assert.False((await blocked).Approved);
    }

    // ---- 3. the unsaved guard and the pause ----

    [Fact]
    public async Task Follow_never_replaces_a_file_with_unsaved_changes_and_says_so()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var session = StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));
        await cut.InvokeAsync(() => cut.Instance.SetDirty("README.md", true));
        await cut.InvokeAsync(panel.Open);

        var blocked = RequestWrite(broker, session);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-follow-notice]")), FollowWait);
        Assert.Contains("unsaved changes", cut.Find("[data-testid=code-follow-notice]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=code-follow-diff]"));
        Assert.False(blocked.IsCompleted, "the card still decides");
        await cut.InvokeAsync(() => cut.Find("[data-testid=deny]").Click());
    }

    [Fact]
    public async Task Follow_pauses_after_the_person_types_and_resumes_when_asked()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var session = StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);

        await cut.InvokeAsync(() => cut.Instance.EditorPersonActivity());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-follow-paused]")));
        var blocked = RequestWrite(broker, session);
        await PollUntilAsync(() => session.Pending.Count == 1);
        await Task.Delay(300);
        Assert.Empty(cut.FindAll("[data-testid=code-follow-diff]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=code-follow-paused]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-follow-diff]")), FollowWait);
        Assert.Empty(cut.FindAll("[data-testid=code-follow-paused]"));
        Assert.False(blocked.IsCompleted);
        await cut.InvokeAsync(() => cut.Find("[data-testid=code-follow-deny]").Click());
    }

    [Fact]
    public async Task With_follow_off_the_editor_shows_nothing_and_the_card_still_decides()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var session = StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-follow-toggle]")));
        Assert.Equal("true", cut.Find("[data-testid=code-follow-toggle]").GetAttribute("aria-pressed"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=code-follow-toggle]").Click());

        Assert.False(panel.FollowAgent);
        var blocked = RequestWrite(broker, session);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=approval-prompt]")), FollowWait);
        Assert.Empty(cut.FindAll("[data-testid=code-follow-diff]"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=approve]").Click());
        Assert.True((await blocked).Approved);
    }

    // ---- 3. a file the agent reads opens as a preview tab ----

    [Fact]
    public async Task A_file_the_agent_reads_inside_the_copy_opens_as_a_preview_tab_and_one_outside_is_ignored()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var runner = ctx.Services.GetRequiredService<RecordingSessionRunner>();
        StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);

        EmitRead(runner, "toolu_out", "/data/keys/ring.json");
        EmitRead(runner, "toolu_up", "/workspaces/w1/../w2/a.cs");
        EmitRead(runner, "toolu_in", "/workspaces/w1/src/Program.cs");

        cut.WaitForAssertion(() =>
        {
            var tab = Assert.Single(cut.FindAll(".code-tab.preview"));
            Assert.Contains("Program.cs", tab.TextContent, StringComparison.Ordinal);
        }, FollowWait);
        Assert.Single(cut.FindAll(".code-tab"));

        // A follow-opened file never takes focus from the composer.
        cut.WaitForAssertion(() =>
        {
            var open = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiCode.open");
            Assert.Equal(true, open.Arguments[^1]);
        }, FollowWait);

        // The next read replaces the preview rather than adding a tab.
        EmitRead(runner, "toolu_2", "/workspaces/w1/README.md");
        cut.WaitForAssertion(() =>
        {
            var tab = Assert.Single(cut.FindAll(".code-tab"));
            Assert.Contains("README.md", tab.TextContent, StringComparison.Ordinal);
            Assert.Contains("preview", tab.ClassList);
        }, FollowWait);
    }

    // ---- work item #371: a refused or failed call opens nothing; a missing file's preview closes itself ----

    [Fact]
    public async Task A_read_opens_its_file_only_once_its_result_comes_back_as_a_success()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var runner = ctx.Services.GetRequiredService<RecordingSessionRunner>();
        StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);

        runner.Emit(new ToolCallStarted(DateTimeOffset.UtcNow, "toolu_r", "Read", "/workspaces/w1/src/Program.cs"));
        await Task.Delay(300);
        Assert.Empty(cut.FindAll(".code-tab"));

        runner.Emit(new ToolCallCompleted(DateTimeOffset.UtcNow, "toolu_r", "Read", true, "1\tclass Program {}"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".code-tab.preview")), FollowWait);
    }

    [Fact]
    public async Task A_write_the_studio_refused_before_any_approval_opens_no_tab()
    {
        // Work item #371: every work item the session named was closed, so the work gate refused the Write
        // without asking anybody. The editor opened follow-check.txt anyway and showed the missing-file notice.
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var runner = ctx.Services.GetRequiredService<RecordingSessionRunner>();
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        reader.Missing.Add("follow-check.txt");
        StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);

        runner.Emit(new ToolCallStarted(DateTimeOffset.UtcNow, "toolu_w", "Write", "/workspaces/w1/follow-check.txt"));
        runner.Emit(new ToolCallRefused(
            DateTimeOffset.UtcNow, "Write", "/workspaces/w1/follow-check.txt", "Every work item this session named is closed."));
        runner.Emit(new ToolCallCompleted(
            DateTimeOffset.UtcNow, "toolu_w", "Write", false, "Every work item this session named is closed."));
        // The same refusal on a file that exists: it is the refusal, not the missing file, that keeps the tab shut.
        runner.Emit(new ToolCallStarted(DateTimeOffset.UtcNow, "toolu_e", "Edit", "/workspaces/w1/README.md"));
        runner.Emit(new ToolCallCompleted(
            DateTimeOffset.UtcNow, "toolu_e", "Edit", false, "Every work item this session named is closed."));
        // A read that failed is not followed either.
        runner.Emit(new ToolCallStarted(DateTimeOffset.UtcNow, "toolu_f", "Read", "/workspaces/w1/src/Program.cs"));
        runner.Emit(new ToolCallCompleted(DateTimeOffset.UtcNow, "toolu_f", "Read", false, "File does not exist."));

        // A later successful read proves follow processed everything before it.
        EmitRead(runner, "toolu_ok", "/workspaces/w1/logo.png");
        cut.WaitForAssertion(() =>
        {
            var tab = Assert.Single(cut.FindAll(".code-tab"));
            Assert.Contains("logo.png", tab.TextContent, StringComparison.Ordinal);
        }, FollowWait);
        Assert.Empty(cut.FindAll("[data-testid=code-missing-notice]"));
        // Copied on the renderer's dispatcher: the tab can show while the page is still making its JS calls for
        // logo.png, and enumerating bunit's live invocation list beside them threw "Collection was modified" in the
        // the dev host CI run of 07cdc596.
        var invocations = await cut.InvokeAsync(() => ctx.JSInterop.Invocations.ToList());
        Assert.DoesNotContain(invocations, i => i.Identifier == "fravashiCode.open"
            && i.Arguments.FirstOrDefault() is "follow-check.txt" or "README.md" or "src/Program.cs");
    }

    [Fact]
    public async Task A_preview_tab_whose_file_is_deleted_closes_itself_instead_of_showing_the_missing_notice()
    {
        var (ctx, registry, broker, panel) = FollowContext();
        using var _ = ctx;
        var runner = ctx.Services.GetRequiredService<RecordingSessionRunner>();
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        StartFollowedSession(registry, broker);
        var cut = ctx.Render<CodePage>();
        await cut.InvokeAsync(panel.Open);
        EmitRead(runner, "toolu_r", "/workspaces/w1/src/Program.cs");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".code-tab.preview")), FollowWait);

        reader.Missing.Add("src/Program.cs");
        ctx.Services.GetRequiredService<FakeNotifier>().Raise(
            new Fravashi.Application.Repository.RepositoryChange(Fravashi.Application.Repository.RepositoryChangeKind.Deleted, "src/Program.cs"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".code-tab")), FollowWait);
        Assert.Empty(cut.FindAll("[data-testid=code-missing-notice]"));
        Assert.Empty(cut.FindAll("[data-testid=code-file-alert]"));
    }

    [Fact]
    public void An_ordinary_tab_whose_file_is_missing_still_shows_the_missing_notice()
    {
        using var ctx = PageContext();
        ctx.Services.GetRequiredService<FakeReader>().Missing.Add("gone.txt");

        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "gone.txt"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-missing-notice]")), FollowWait);
        Assert.Single(cut.FindAll(".code-tab"));
    }

    private static void EmitRead(RecordingSessionRunner runner, string toolUseId, string path)
    {
        runner.Emit(new ToolCallStarted(DateTimeOffset.UtcNow, toolUseId, "Read", path));
        runner.Emit(new ToolCallCompleted(DateTimeOffset.UtcNow, toolUseId, "Read", true, "contents"));
    }

    private static async Task PollUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + FollowWait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true in time.");
            }

            await Task.Delay(20);
        }
    }
}
