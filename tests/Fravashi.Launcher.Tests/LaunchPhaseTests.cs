using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public sealed class LaunchPhaseTests
{
    [Fact]
    public void EveryPhase_HasANonEmptyDescription()
    {
        foreach (var phase in Enum.GetValues<LaunchPhase>())
        {
            phase.Describe().Should().NotBeNullOrWhiteSpace();
        }
    }
}
