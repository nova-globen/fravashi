using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public sealed class StudioReadinessProbeTests
{
    [Fact]
    public async Task WaitUntilReady_WhenHealthzAnswersImmediately_ReturnsTrue_AndProbesTheHealthzPath()
    {
        var probed = new List<string>();
        var probe = new StudioReadinessProbe((url, _) =>
        {
            probed.Add(url);
            return Task.FromResult(true);
        });

        var ready = await probe.WaitUntilReadyAsync(
            "http://localhost:9090", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));

        ready.Should().BeTrue();
        probed.Should().ContainSingle().Which.Should().Be("http://localhost:9090/healthz");
    }

    [Fact]
    public async Task WaitUntilReady_KeepsPollingUntilTheStudioAnswers()
    {
        var attempts = 0;
        var probe = new StudioReadinessProbe((_, _) => Task.FromResult(++attempts >= 3));

        var ready = await probe.WaitUntilReadyAsync(
            "http://localhost:9090", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1));

        ready.Should().BeTrue();
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task WaitUntilReady_WhenTheStudioNeverAnswers_ReturnsFalseAfterTheTimeout()
    {
        var probe = new StudioReadinessProbe((_, _) => Task.FromResult(false));

        var ready = await probe.WaitUntilReadyAsync(
            "http://localhost:9090", TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(1));

        ready.Should().BeFalse();
    }

    [Fact]
    public async Task WaitUntilReady_TreatsAProbeException_AsNotReady()
    {
        var attempts = 0;
        var probe = new StudioReadinessProbe((_, _) =>
            ++attempts == 1
                ? Task.FromException<bool>(new HttpRequestException("refused"))
                : Task.FromResult(true));

        var ready = await probe.WaitUntilReadyAsync(
            "http://localhost:9090", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1));

        ready.Should().BeTrue();
        attempts.Should().Be(2);
    }
}
