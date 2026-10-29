using AwesomeAssertions;
using Fravashi.Application.Demo;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// The demo switch (backlog 71). Small, and worth holding, because everything else about demo mode hangs off
/// reading one variable: get this wrong in the permissive direction and an ordinary install silently seeds
/// itself and stops its own clock.
/// </summary>
public sealed class DemoModeTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("yes")]
    [InlineData(" 1 ")]
    public void A_value_that_means_something_turns_it_on(string value) =>
        DemoMode.IsOn(value).Should().BeTrue();

    /// <summary>
    /// The half that matters. An absent variable is the normal case for every real install, and "0" and
    /// "false" are what somebody writes when they mean to turn it off: reading either as ON would seed a
    /// user's own install.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData(" false ")]
    public void Nothing_and_a_value_that_means_off_leaves_it_off(string? value) =>
        DemoMode.IsOn(value).Should().BeFalse();

    /// <summary>
    /// The demo's clock is STOPPED, not merely fixed at startup: two reads an hour apart give the same
    /// answer. That is what makes a rendered age ("3 hours ago") a constant rather than a number that grows
    /// while the demo sits open, which is the determinism promise on every page that shows one.
    /// </summary>
    [Fact]
    public void The_demo_clock_does_not_move()
    {
        var first = DemoMode.Clock.GetUtcNow();
        var second = DemoMode.Clock.GetUtcNow();

        first.Should().Be(DemoMode.Now);
        second.Should().Be(first);
    }

    /// <summary>
    /// The seeded data is stamped in the PAST relative to the demo's now, and by hours rather than seconds.
    /// If they were equal every age would render "just now", which hides the very formatting a screenshot of
    /// a dated surface is meant to show.
    /// </summary>
    [Fact]
    public void The_demo_now_is_a_few_hours_after_what_the_seed_is_stamped_with()
    {
        var gap = DemoMode.Now - DemoSeed.Epoch;

        gap.Should().BePositive("a demo whose data is in the future renders negative ages");
        gap.Should().BeGreaterThan(TimeSpan.FromHours(1), "everything would otherwise read as just now");
    }
}
