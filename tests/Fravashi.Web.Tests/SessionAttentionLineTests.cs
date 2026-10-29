using Bunit;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Web.Components.Sessions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 244: the one line the session pane and a work item's linked sessions show is the counts' own line, word for
/// word, so the two surfaces and the session_attention tool cannot disagree; and a count rebuilt from an old transcript
/// says so.
/// </summary>
public class SessionAttentionLineTests
{
    private static readonly AgentSessionId Session = AgentSessionId.New();

    [Fact]
    public void The_line_is_the_counts_own_line_and_the_tooltip_breaks_the_asked_approvals_down_by_kind()
    {
        using var ctx = new BunitContext();
        var counted = SessionAttention.From(
        [
            AttentionEntry.Decision(Session, AttentionToolKind.Shell, AttentionOutcome.Approved, TimeSpan.FromSeconds(30), DateTimeOffset.UnixEpoch),
            AttentionEntry.Decision(Session, AttentionToolKind.Edit, AttentionOutcome.DeniedWithReason, TimeSpan.FromSeconds(30), DateTimeOffset.UnixEpoch),
        ]);

        var cut = ctx.Render<SessionAttentionLine>(p => p.Add(c => c.Attention, counted));

        Assert.Equal(counted.Line, cut.Find("[data-testid=session-attention-line]").TextContent);
        Assert.Equal("Approvals asked, by kind: edit 1, shell 1", cut.Find("[data-testid=session-attention]").GetAttribute("title"));
        Assert.Empty(cut.FindAll("[data-testid=session-attention-reconstructed]"));
    }

    [Fact]
    public void A_count_rebuilt_from_an_old_transcript_says_what_it_could_not_see()
    {
        using var ctx = new BunitContext();
        var counted = SessionAttention.From(
            [AttentionEntry.Decision(Session, AttentionToolKind.Write, AttentionOutcome.Approved, TimeSpan.Zero, DateTimeOffset.UnixEpoch, reconstructed: true)]);

        var cut = ctx.Render<SessionAttentionLine>(p => p
            .Add(c => c.Attention, counted)
            .Add(c => c.TestId, "work-item-session-attention"));

        Assert.Contains(
            "kept no calls the studio allowed without asking",
            cut.Find("[data-testid=work-item-session-attention-reconstructed]").TextContent);
    }
}
