using AwesomeAssertions;
using Fravashi.Application.Autopilot;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Application.Tests.Autopilot;

/// <summary>
/// ADR 0031 decision 16, #307: the one link between a dispatched run's own concurrency cap override and the
/// gate the builder consults. Without this, a lower override saved on a run (AutopilotDispatcherTests) would
/// never actually reach anything that enforces it (AgentAutopilotBuilderTests' own effect test of the gate).
/// </summary>
public sealed class AutopilotSeamsTests
{
    [Fact]
    public void AutopilotBuilderContext_From_carries_the_runs_own_concurrency_cap_override()
    {
        var run = AutopilotRun.Start(
            Guid.NewGuid(), new ProjectId("proj-1"),
            AutopilotDispatchSettings.Default with { ConcurrencyCapOverride = 1 },
            [new WorkItemId("item-1")], DateTimeOffset.UtcNow);

        AutopilotBuilderContext.From(run).ConcurrencyCapOverride.Should().Be(1);
    }

    [Fact]
    public void AutopilotBuilderContext_From_carries_null_when_the_run_has_no_override()
    {
        var run = AutopilotRun.Start(
            Guid.NewGuid(), new ProjectId("proj-1"), AutopilotDispatchSettings.Default,
            [new WorkItemId("item-1")], DateTimeOffset.UtcNow);

        AutopilotBuilderContext.From(run).ConcurrencyCapOverride.Should().BeNull();
    }
}
