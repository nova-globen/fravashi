using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

public sealed class SqliteOnboardingStateTests
{
    [Fact]
    public void FreshDatabase_ReportsIncomplete_FirstRun()
    {
        using var context = new SqliteTestContext();
        var state = new SqliteOnboardingState(context.Factory);

        state.IsComplete.Should().BeFalse();
        state.Get().CompletedUtc.Should().BeNull();
    }

    [Fact]
    public void MarkComplete_FlipsFlag_AndStampsCompletion()
    {
        using var context = new SqliteTestContext();
        var state = new SqliteOnboardingState(context.Factory);

        state.MarkComplete();

        state.IsComplete.Should().BeTrue();
        state.Get().CompletedUtc.Should().NotBeNull();
    }

    [Fact]
    public void SetCurrentStep_RecordsStep_AndStampsStartOnce()
    {
        using var context = new SqliteTestContext();
        var state = new SqliteOnboardingState(context.Factory);

        state.SetCurrentStep("git-access");
        var afterFirst = state.Get();
        state.SetCurrentStep("board-access");
        var afterSecond = state.Get();

        afterFirst.CurrentStep.Should().Be("git-access");
        afterSecond.CurrentStep.Should().Be("board-access");
        afterSecond.StartedUtc.Should().Be(afterFirst.StartedUtc, "started time is stamped only on first advance");
    }

    [Fact]
    public void Reset_ClearsCompletion()
    {
        using var context = new SqliteTestContext();
        var state = new SqliteOnboardingState(context.Factory);
        state.MarkComplete();

        state.Reset();

        state.IsComplete.Should().BeFalse();
    }
}
