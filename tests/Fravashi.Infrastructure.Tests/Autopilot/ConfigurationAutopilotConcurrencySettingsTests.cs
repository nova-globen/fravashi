using AwesomeAssertions;
using Fravashi.Domain.Autopilot;
using Fravashi.Infrastructure.Autopilot;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Autopilot;

/// <summary>
/// ADR 0031 decision 16, #307: the concurrency cap's Settings value, shaped after
/// <c>AgentSpendLimitTests</c>' own reading tests for <c>ConfigurationAgentSpendLimits</c>.
/// </summary>
public sealed class ConfigurationAutopilotConcurrencySettingsTests
{
    [Theory]
    [InlineData("3", 3)]
    [InlineData(" 5 ", 5)]
    public void A_stored_whole_number_becomes_the_cap(string stored, int expected) =>
        ConfigurationAutopilotConcurrencySettings.Read(stored).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2.5")]
    public void Anything_that_is_not_a_whole_number_above_zero_reads_as_the_shipped_default(string? stored) =>
        ConfigurationAutopilotConcurrencySettings.Read(stored).Should().Be(AutopilotConcurrency.DefaultCap);

    [Fact]
    public void The_cap_is_read_fresh_from_configuration_on_every_ask()
    {
        // A cap saved in Settings has to bite the next start, not the next restart, so the source may not
        // capture what it read the first time.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ConfigurationAutopilotConcurrencySettings.CapKey] = "3",
            })
            .Build();
        var settings = new ConfigurationAutopilotConcurrencySettings(configuration);

        settings.Cap().Should().Be(3);

        configuration[ConfigurationAutopilotConcurrencySettings.CapKey] = "1";

        settings.Cap().Should().Be(1);
    }

    [Fact]
    public void Write_is_the_inverse_of_read_for_a_positive_cap()
    {
        ConfigurationAutopilotConcurrencySettings.Write(4).Should().Be("4");
        ConfigurationAutopilotConcurrencySettings.Read(ConfigurationAutopilotConcurrencySettings.Write(4)).Should().Be(4);
    }
}
