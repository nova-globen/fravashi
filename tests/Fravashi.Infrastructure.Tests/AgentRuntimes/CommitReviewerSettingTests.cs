using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Framework;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

/// <summary>
/// Backlog 243: the commit reviewer is a ROLE held in settings, generic over the runtime catalog, with one shipped
/// default (on, the runtime this version ships, on its most capable model).
/// </summary>
public sealed class CommitReviewerSettingTests
{
    [Fact]
    public void The_default_is_on_the_shipped_runtime_on_its_most_capable_model()
    {
        var role = CommitReviewerSetting.Read(new FakeConfigurationStore());

        role.Enabled.Should().BeTrue("review by a second agent is the default (#77 verdict 2)");
        role.Runtime.Should().Be(AgentRuntimeCatalog.Tools[0]).And.Be(AgentTool.Claude);
        role.Model.Should().Be("claude-fable-5-1");
        AgentModelCatalog.For(AgentTool.Claude).Single(m => m.MostCapable).Id.Should().Be(role.Model,
            "exactly one model is marked most capable, and that is the default");
        role.Filler.Should().Be("Claude Code/claude-fable-5-1");
    }

    [Fact]
    public void The_role_round_trips_through_the_settings_table()
    {
        var store = new FakeConfigurationStore();

        CommitReviewerSetting.Write(store, new CommitReviewerRole(false, AgentTool.Claude, "claude-sonnet-5"));

        CommitReviewerSetting.Read(store).Should().Be(new CommitReviewerRole(false, AgentTool.Claude, "claude-sonnet-5"));
        store.Values[CommitReviewerSetting.EnabledKey].Should().Be("false");
        store.Values[CommitReviewerSetting.RuntimeKey].Should().Be("Claude");
        store.Values[CommitReviewerSetting.ModelKey].Should().Be("claude-sonnet-5");
    }

    [Fact]
    public void A_runtime_the_catalog_does_not_offer_falls_back_to_the_default_and_drops_its_model()
    {
        // Codex is parked in docs/v2.md section 1: a stored choice of it must not throw, and its model must not be
        // handed to the runtime that replaces it.
        var store = new FakeConfigurationStore();
        store.Set(CommitReviewerSetting.RuntimeKey, nameof(AgentTool.Codex));
        store.Set(CommitReviewerSetting.ModelKey, "gpt-5.5");

        CommitReviewerSetting.Read(store).Should().Be(CommitReviewerSetting.Default);
    }

    [Fact]
    public void The_choice_is_every_runtime_the_catalog_offers()
    {
        CommitReviewerSetting.Runtimes.Select(r => r.Tool).Should().Equal(AgentRuntimeCatalog.Tools);
    }

    [Fact]
    public void An_unreadable_switch_leaves_the_review_on()
    {
        var store = new FakeConfigurationStore();
        store.Set(CommitReviewerSetting.EnabledKey, "maybe");

        CommitReviewerSetting.Read(store).Enabled.Should().BeTrue("only an explicit false turns the review off");
    }
}
