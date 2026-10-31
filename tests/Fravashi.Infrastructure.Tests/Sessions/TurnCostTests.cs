using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 377: what a conversation costs. The v1.0.0 drill showed $0.41 and then $0.88 for two Haiku 5.5 turns that
/// cost about a cent together, for two reasons pinned here: the CLI's <c>total_cost_usd</c> is the PROCESS's spending
/// so far, which the studio added up again on every result line, and the pinned CLI predates Haiku 5.5 and prices it
/// as something far dearer.
/// </summary>
public sealed class TurnCostTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 18, 0, 0, TimeSpan.Zero);

    private static AgentStatsSnapshot Replay(string fixture, bool brokered = false)
    {
        var parser = new ClaudeStreamJsonParser(() => Now, () => brokered);
        var path = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", fixture);
        return File.ReadAllLines(path).SelectMany(parser.Parse).OfType<StatsUpdated>().Last().Stats;
    }

    private static AgentStatsSnapshot Parse(params string[] lines)
    {
        var parser = new ClaudeStreamJsonParser(() => Now);
        return lines.SelectMany(parser.Parse).OfType<StatsUpdated>().Last().Stats;
    }

    [Fact]
    public void A_conversation_costs_what_the_process_spent_not_the_sum_of_its_running_totals()
    {
        // The capture's result lines say $0.0151036 and then $0.0187992: the second is both turns together. Summing
        // them made $0.0339028, and a long conversation's error grows with every turn.
        var stats = Replay("claude-multi-turn.jsonl");

        stats.Turns.Should().Be(2);
        stats.CostUsd.Should().Be(0.0187992m);
    }

    [Fact]
    public void Cache_writes_are_recorded()
    {
        Replay("claude-multi-turn.jsonl").CacheWriteTokens.Should().Be(6530 + 214);
    }

    [Fact]
    public void A_haiku_5_5_turn_is_priced_by_the_studio_not_by_the_cli()
    {
        // The drill's first turn, in its shape: the CLI said $0.41. At Haiku 5.5's price it is
        // 10 x 0.10 + 300 x 0.50 + 54637 x 0.20 (a one-hour cache write is twice the input rate), per million.
        var stats = Parse(ResultLine("claude-haiku-5-5", input: 10, output: 300, cacheRead: 0, cacheWrite: 54637, cost: 0.41m));

        stats.CostUsd.Should().Be(0.0110784m);
        stats.CacheWriteTokens.Should().Be(54637);
    }

    [Fact]
    public void A_model_the_studio_has_no_price_for_keeps_the_clis_figure_for_its_share()
    {
        var stats = Parse(
            ResultLine("claude-future-9", input: 10, output: 10, cacheRead: 0, cacheWrite: 0, cost: 0.30m),
            ResultLine("claude-future-9", input: 20, output: 20, cacheRead: 0, cacheWrite: 0, cost: 0.50m));

        stats.CostUsd.Should().Be(0.50m, "the second line's $0.50 is the process so far, $0.20 of it the second turn");
    }

    [Fact]
    public void A_brokered_turn_costs_the_person_nothing_and_counts_the_brokers_units()
    {
        // Per turn, input 1, cache write 1.25, cache read 0.1, output 5, rounded up: 10207 then 3536.
        var stats = Replay("claude-multi-turn.jsonl", brokered: true);

        stats.CostUsd.Should().Be(0m, "the free tier is not the person's spending and never meets their limits");
        stats.FreeTierUnits.Should().Be(10207 + 3536);
        stats.InputTokens.Should().Be(20, "the tokens are still shown");
    }

    /// <summary>An assistant line reporting one request whose prompt is <paramref name="prompt"/> tokens.</summary>
    private static string Request(long prompt) =>
        "{\"type\":\"assistant\",\"message\":{\"id\":\"m" + prompt + "\",\"role\":\"assistant\",\"content\":[],\"usage\":"
        + "{\"input_tokens\":10,\"cache_read_input_tokens\":" + (prompt - 10)
        + ",\"cache_creation_input_tokens\":0,\"output_tokens\":5}}}";

    /// <summary>The pinned 2.1.280's result line: the turn's SUM, with an empty iterations list.</summary>
    private static string SummedResult(long input, long cacheRead, long output) =>
        "{\"type\":\"result\",\"is_error\":false,\"terminal_reason\":\"completed\",\"total_cost_usd\":1,\"usage\":"
        + "{\"input_tokens\":" + input + ",\"output_tokens\":" + output + ",\"cache_read_input_tokens\":" + cacheRead
        + ",\"cache_creation_input_tokens\":0,\"iterations\":[]},\"modelUsage\":{\"claude-haiku-5-5\":{\"inputTokens\":" + input
        + ",\"outputTokens\":" + output + ",\"cacheReadInputTokens\":" + cacheRead
        + ",\"cacheCreationInputTokens\":0,\"costUSD\":1}}}";

    [Fact]
    public void Requests_each_under_100k_are_not_counted_five_times_over_however_large_their_sum()
    {
        // The 1.0.3 proof: three requests of 56k summed past 100k, and the studio counted 377.4k units where the broker,
        // which judges each request alone, counted 75,481.
        var parser = new ClaudeStreamJsonParser(() => Now, () => true);
        var stats = new[] { Request(56_000), Request(56_000), Request(56_000), SummedResult(30, 167_970, 300) }
            .SelectMany(parser.Parse).OfType<StatsUpdated>().Last().Stats;

        stats.FreeTierUnits.Should().Be(FreeTierUnits.Of(new TokenUsage(30, 300, 167_970, 0, 0)));
        stats.FreeTierUnits.Should().Be(18_327, "30 + 16,797 + 1,500, not five times that");
    }

    [Fact]
    public void A_request_over_100k_puts_the_turn_in_the_long_context_tier()
    {
        var parser = new ClaudeStreamJsonParser(() => Now);
        var stats = new[] { Request(150_000), SummedResult(10, 149_990, 100) }
            .SelectMany(parser.Parse).OfType<StatsUpdated>().Last().Stats;

        // Haiku 5.5 past 100k: $0.50 input, $0.05 cache read, $2.50 output, per million.
        stats.CostUsd.Should().Be(((10 * 0.50m) + (149_990 * 0.05m) + (100 * 2.50m)) / 1_000_000m);
    }

    [Fact]
    public void A_restarted_process_counts_its_spending_from_zero_again()
    {
        var parser = new ClaudeStreamJsonParser(() => Now);
        var line = ResultLine("claude-future-9", input: 10, output: 10, cacheRead: 0, cacheWrite: 0, cost: 0.30m);
        parser.Parse(line);
        parser.ExpectRestart();

        parser.Parse(line).OfType<StatsUpdated>().Last().Stats.CostUsd.Should().Be(0.60m);
    }

    /// <summary>A result line as the CLI writes one, with its modelUsage cumulative for the process.</summary>
    private static string ResultLine(string model, long input, long output, long cacheRead, long cacheWrite, decimal cost) =>
        Line(model, input, output, cacheRead, cacheWrite, cost.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string Line(string model, long input, long output, long cacheRead, long cacheWrite, string cost) =>
        $$$"""{"type":"result","is_error":false,"terminal_reason":"completed","total_cost_usd":{{{cost}}},"usage":{"input_tokens":{{{input}}},"output_tokens":{{{output}}},"cache_read_input_tokens":{{{cacheRead}}},"cache_creation_input_tokens":{{{cacheWrite}}},"cache_creation":{"ephemeral_1h_input_tokens":{{{cacheWrite}}},"ephemeral_5m_input_tokens":0}},"modelUsage":{"{{{model}}}":{"inputTokens":{{{input}}},"outputTokens":{{{output}}},"cacheReadInputTokens":{{{cacheRead}}},"cacheCreationInputTokens":{{{cacheWrite}}},"costUSD":{{{cost}}}""" + "}}}";
}
