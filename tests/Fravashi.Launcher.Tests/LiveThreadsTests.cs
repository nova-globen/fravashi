using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public class LiveThreadsTests
{
    [Fact]
    public void Describe_names_a_live_thread_the_way_the_product_named_it()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return; // no OS-level thread names are read elsewhere; Describe still lists the ids
        }

        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Short enough for Linux's 15-character comm; the runtime applies the name before the body runs.
        var thread = new Thread(() => { running.Set(); release.Wait(); }) { Name = "host-probe", IsBackground = true };
        thread.Start();
        try
        {
            running.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

            var entries = LiveThreads.Describe();

            entries.Should().Contain(entry => entry.EndsWith("\"host-probe\""));
            entries.Should().OnlyContain(entry => entry.Contains(" \"") || entry.EndsWith(" (unnamed)"));
        }
        finally
        {
            release.Set();
            thread.Join();
        }
    }

    [Fact]
    public void Describe_lists_at_least_the_calling_thread()
    {
        LiveThreads.Describe().Should().NotBeEmpty();
    }
}
