using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog 4: the splash shows one segment per real launch step, so a launch that stalls stalls visibly on a
/// named step instead of animating forever. The decision lives here because <c>Fravashi.Desktop</c> has no tests.
/// </summary>
public sealed class LaunchProgressTests
{
    [Fact]
    public void There_is_a_segment_for_every_step_a_launch_can_stand_on_and_none_for_being_done()
    {
        LaunchProgress.Steps.Should().Equal(
            Enum.GetValues<LaunchPhase>().Where(phase => phase != LaunchPhase.Ready));
    }

    [Fact]
    public void The_first_step_is_current_and_everything_after_it_waits()
    {
        var states = LaunchProgress.For(LaunchProgress.Steps[0]);

        states[0].Should().Be(LaunchSegmentState.Current);
        states.Skip(1).Should().OnlyContain(state => state == LaunchSegmentState.Pending);
    }

    [Fact]
    public void A_step_in_the_middle_has_the_earlier_ones_done_and_the_later_ones_waiting()
    {
        var at = LaunchProgress.Steps.ToList().IndexOf(LaunchPhase.VerifyingIntegrity);

        var states = LaunchProgress.For(LaunchPhase.VerifyingIntegrity);

        states.Take(at).Should().OnlyContain(state => state == LaunchSegmentState.Done);
        states[at].Should().Be(LaunchSegmentState.Current);
        states.Skip(at + 1).Should().OnlyContain(state => state == LaunchSegmentState.Pending);
    }

    [Fact]
    public void A_step_this_launch_skipped_reads_as_done_once_a_later_one_is_reached()
    {
        // The engine was already up and there is no tunnel, so two steps never raise their phase. A gap left
        // in the bar would read as something that went wrong.
        var states = LaunchProgress.For(LaunchPhase.PreparingContainer);
        var steps = LaunchProgress.Steps.ToList();

        states[steps.IndexOf(LaunchPhase.StartingRuntime)].Should().Be(LaunchSegmentState.Done);
        states[steps.IndexOf(LaunchPhase.StartingVpn)].Should().Be(LaunchSegmentState.Done);
    }

    [Fact]
    public void Ready_has_every_segment_done_and_none_current()
    {
        LaunchProgress.For(LaunchPhase.Ready).Should().OnlyContain(state => state == LaunchSegmentState.Done);
    }

    [Fact]
    public void A_launch_that_starts_over_shows_the_start_again()
    {
        // The tray's Open after a Stop raises the first phase a second time in the same process.
        _ = LaunchProgress.For(LaunchPhase.WaitingForStudio);

        LaunchProgress.For(LaunchProgress.Steps[0])[0].Should().Be(LaunchSegmentState.Current);
    }

    [Fact]
    public void The_steps_are_in_the_order_the_launcher_raises_them()
    {
        // The bar only tells the truth while this order is the launch's own. If a phase is ever raised out of
        // this order, the fix belongs in whichever of the two is wrong, not in making this test agree.
        // SigningIn comes first because the host raises it before the launcher starts anything (backlog 94), and
        // a start that needs no sign-in skips it, which the bar shows as done like any other skipped step.
        LaunchProgress.Steps.Should().Equal(
            LaunchPhase.SigningIn,
            LaunchPhase.CheckingRuntime,
            LaunchPhase.StartingRuntime,
            LaunchPhase.PreparingImage,
            LaunchPhase.StartingVpn,
            LaunchPhase.VerifyingIntegrity,
            LaunchPhase.PreparingContainer,
            LaunchPhase.CheckingUpdates,
            LaunchPhase.WaitingForStudio);
    }
}
