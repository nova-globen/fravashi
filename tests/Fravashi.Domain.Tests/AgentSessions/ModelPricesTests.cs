using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>Backlog 377: the studio prices a turn from its tokens wherever it knows the model.</summary>
public sealed class ModelPricesTests
{
    [Theory]
    [InlineData("claude-haiku-4-5", 1)]
    [InlineData("claude-haiku-4-5-20251001", 1)]
    [InlineData("claude-opus-5[1m]", 5)]
    [InlineData("Claude-Opus-5-5", 4)]
    [InlineData("claude-opus-5-5", 4)]
    [InlineData("claude-opus-5", 5)]
    [InlineData("claude-haiku-5-5", 0.10)]
    public void A_model_is_found_by_its_id_with_or_without_a_date_or_a_context_suffix(string model, double input)
    {
        ModelPrices.For(model)!.Input.Should().Be((decimal)input);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("claude-opus-5-9")]
    [InlineData("claude-opus-5-5-preview")]
    [InlineData("gpt-5")]
    public void A_model_the_table_does_not_list_has_no_price(string? model)
    {
        ModelPrices.For(model).Should().BeNull("the CLI's own figure is kept for it");
    }

    [Fact]
    public void A_haiku_4_5_turn_costs_exactly_what_the_cli_charges_for_it()
    {
        // The multi-turn capture's first result line: 10 in, 56 out, 17536 cache read, 6530 written to the one-hour
        // cache, and total_cost_usd 0.0151036. The CLI knows Haiku 4.5, so agreeing to the last digit is what proves
        // the table and the cache multipliers are read the way the API bills them.
        var usage = new TokenUsage(10, 56, 17536, 0, 6530);

        ModelPrices.For("claude-haiku-4-5")!.Cost(usage, 24076).Should().Be(0.0151036m);
    }

    [Fact]
    public void A_five_minute_cache_write_costs_less_than_a_one_hour_one()
    {
        var price = ModelPrices.For("claude-sonnet-5-5")!;

        price.Cost(new TokenUsage(0, 0, 0, 1_000_000, 0), 0).Should().Be(2.50m);
        price.Cost(new TokenUsage(0, 0, 0, 0, 1_000_000), 0).Should().Be(4m);
    }

    [Fact]
    public void Haiku_5_5_is_priced_at_its_long_context_tier_past_100k_prompt_tokens()
    {
        var price = ModelPrices.For("claude-haiku-5-5")!;
        var usage = new TokenUsage(1_000_000, 1_000_000, 0, 0, 0);

        price.Cost(usage, 100_000).Should().Be(0.60m);
        price.Cost(usage, 100_001).Should().Be(3m);
    }

    [Theory]
    // The pricing page, read 2026-10-31: Sonnet 5.5 and Opus 5.5 read their cache at 0.05 times input, Fable 5.1 at 0.025.
    [InlineData("claude-sonnet-5-5", 0.10)]
    [InlineData("claude-opus-5-5", 0.20)]
    [InlineData("claude-fable-5-1", 0.25)]
    [InlineData("claude-sonnet-5", 0.20)]
    [InlineData("claude-opus-4-1-20250805", 1.50)]
    [InlineData("claude-sonnet-4-20250514", 0.30)]
    public void Each_model_reads_its_cache_at_its_own_published_rate(string model, double cacheRead)
    {
        ModelPrices.For(model)!.CacheRead.Should().Be((decimal)cacheRead);
    }

    [Fact]
    public void Free_tier_units_use_the_brokers_weights()
    {
        // Input 1, cache write 1.25, cache read 0.1, output 5 (infra D54).
        FreeTierUnits.Of(new TokenUsage(100, 10, 1000, 40, 40)).Should().Be(100 + 50 + 100 + 100);
    }

    [Fact]
    public void Free_tier_units_are_five_times_over_past_a_100k_prompt_as_the_broker_counts_them()
    {
        var usage = new TokenUsage(100, 10, 1000, 40, 40);

        FreeTierUnits.Of(usage, 100_000).Should().Be(350);
        FreeTierUnits.Of(usage, 100_001).Should().Be(1750);
    }
}
