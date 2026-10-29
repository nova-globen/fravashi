using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Framework;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>The broker's free tier serves Claude Haiku 5.5 only, and a swapped choice is said.</summary>
public class BrokerModelPolicyTests
{
    // The mint answer a test broker gave on 2026-10-29.
    private static readonly IReadOnlyList<string> Advertised = ["claude-haiku-5-5*"];

    [Fact]
    public void A_covered_model_runs_as_asked_and_says_nothing()
    {
        var choice = BrokerModelPolicy.Choose(Advertised, "claude-haiku-5-5");

        choice.Model.Should().Be("claude-haiku-5-5");
        choice.Notice.Should().BeNull();
    }

    [Fact]
    public void A_dated_id_the_pattern_starts_is_covered()
    {
        BrokerModelPolicy.Covers(Advertised, "claude-haiku-5-5-20261001").Should().BeTrue();
        BrokerModelPolicy.Covers(Advertised, "CLAUDE-HAIKU-5-5").Should().BeTrue();
    }

    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-fable-5-1")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-haiku-4-5")]
    // An alias resolves to whatever is newest, which is not the model the pattern names.
    [InlineData("haiku")]
    [InlineData("opusplan")]
    public void A_choice_the_broker_does_not_cover_runs_on_the_served_model_and_says_so(string requested)
    {
        var choice = BrokerModelPolicy.Choose(Advertised, requested);

        choice.Model.Should().Be("claude-haiku-5-5");
        choice.Requested.Should().Be(requested);
        choice.Notice.Should().Contain("claude-haiku-5-5").And.Contain(requested)
            .And.Contain("free tier").And.Contain("own key or subscription");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    public void Nothing_asked_for_runs_on_the_served_model_quietly(string? requested)
    {
        // "default" lets the CLI pick, and what it picks the broker refuses, so the broker counts it as Haiku 5.5.
        var choice = BrokerModelPolicy.Choose(Advertised, requested);

        choice.Model.Should().Be("claude-haiku-5-5");
        choice.Notice.Should().BeNull();
    }

    public static TheoryData<string[]?> NoUsableList() => new() { null, Array.Empty<string>(), new[] { "", " ", "*" } };

    [Theory]
    [MemberData(nameof(NoUsableList))]
    public void A_broker_that_names_no_usable_list_is_taken_to_serve_the_free_tier_model(string[]? advertised)
    {
        BrokerModelPolicy.PatternsOrFallback(advertised).Should().Equal("claude-haiku-5-5*");
        BrokerModelPolicy.Choose(advertised, "claude-opus-5-5").Model.Should().Be("claude-haiku-5-5");
    }

    [Fact]
    public void An_exact_entry_covers_only_that_id_and_is_what_is_served()
    {
        IReadOnlyList<string> exact = ["claude-haiku-5-5-20261001"];

        BrokerModelPolicy.Covers(exact, "claude-haiku-5-5").Should().BeFalse();
        BrokerModelPolicy.ServedModel(exact).Should().Be("claude-haiku-5-5-20261001");
    }

    [Fact]
    public void Every_model_variable_of_the_cli_is_pointed_at_the_served_model()
    {
        var env = BrokerModelPolicy.CliModelEnvironment("claude-haiku-5-5");

        env.Keys.Should().BeEquivalentTo(
            "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL",
            "ANTHROPIC_DEFAULT_FABLE_MODEL", "ANTHROPIC_SMALL_FAST_MODEL", "CLAUDE_CODE_SUBAGENT_MODEL",
            "CLAUDE_CODE_BG_CLASSIFIER_MODEL", "CLAUDE_CODE_AUTO_MODE_MODEL");
        env.Values.Should().AllBe("claude-haiku-5-5");
    }

    [Fact]
    public void The_free_tier_model_is_in_the_catalog_the_picker_offers()
    {
        AgentModelCatalog.For(AgentTool.Claude).Select(m => m.Id).Should().Contain(BrokerModelPolicy.FreeTierModel);
    }
}
