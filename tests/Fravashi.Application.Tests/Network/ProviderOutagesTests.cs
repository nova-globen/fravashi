using AwesomeAssertions;
using Fravashi.Application.Network;
using Xunit;

namespace Fravashi.Application.Tests.Network;

/// <summary>
/// Backlog 232 (plan @58): when an outage starts, what keeps it, and what ends it. It starts only at a failure that
/// provably never left, keeps the time it started across later failures, ignores a failure where the request may have
/// arrived (that is a question about one write, not about the provider), and ends at any answer.
/// </summary>
public sealed class ProviderOutagesTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 14, 2, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly NetworkFailure NameFails = new(NetworkFailureKind.NameNotResolved, "test");
    private static readonly NetworkFailure NoRoute = new(NetworkFailureKind.Unreachable, "test");
    private static readonly NetworkFailure ResetAfterWrite = new(NetworkFailureKind.LeftWithNoAnswer, "test");

    [Fact]
    public void An_outage_starts_at_a_call_that_never_left_and_keeps_its_start_across_later_failures()
    {
        var clock = new Clock();
        var outages = new ProviderOutages(clock);
        var changes = 0;
        outages.Changed += () => changes++;

        outages.Observe("Jira", NameFails);
        clock.Now = clock.Now.AddMinutes(3);
        outages.Observe("Jira", NoRoute);

        var outage = outages.For("Jira")!;
        outage.Words.Should().Be("Jira unreachable since 14:02 (no network route)", "the start is kept, the words are the latest failure's");
        outage.WithWaiting(3).Should().StartWith("Jira unreachable since 14:02 (no network route); 3 change(s) waiting; nothing lost.");
        changes.Should().Be(1, "only going down is a change, not every failure while down");
    }

    [Fact]
    public void A_failure_where_the_request_may_have_arrived_neither_starts_nor_ends_an_outage()
    {
        var outages = new ProviderOutages(new Clock());

        outages.Observe("Jira", ResetAfterWrite);
        outages.For("Jira").Should().BeNull("a lone reset after write is not an outage");

        outages.Observe("Jira", NameFails);
        outages.Observe("Jira", ResetAfterWrite);
        outages.For("Jira").Should().NotBeNull("and it does not prove the provider is back either");
    }

    [Fact]
    public void Any_answer_ends_it_and_providers_are_kept_apart()
    {
        var outages = new ProviderOutages(new Clock());
        var changes = 0;
        outages.Changed += () => changes++;
        outages.Observe("Jira", NameFails);
        outages.Observe("Confluence", NameFails);

        outages.Observe("Jira", null);

        outages.For("Jira").Should().BeNull();
        outages.For("Confluence").Should().NotBeNull();
        changes.Should().Be(3);
    }

    [Fact]
    public void A_failure_that_is_not_the_network_counts_as_an_answer()
    {
        var outages = new ProviderOutages(new Clock());
        outages.Observe("Jira", NameFails);

        outages.Observe("Jira", NetworkFailure.None);

        outages.For("Jira").Should().BeNull();
    }
}
