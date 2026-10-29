using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public class LauncherLogTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "fravashi-launcherlog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Line_reaches_the_console_and_the_dated_file_under_data_logs()
    {
        var console = new List<string>();
        var log = new LauncherLog(_dataDir, console.Add);

        log.Line("first run: hello");
        log.Line("second line");

        console.Should().Equal("first run: hello", "second line");
        var file = Directory.GetFiles(Path.Combine(_dataDir, "logs"), "launcher-*.log").Should().ContainSingle().Subject;
        var content = File.ReadAllText(file);
        content.Should().Contain("first run: hello").And.Contain("second line");
    }

    [Fact]
    public void The_logs_directory_property_matches_where_lines_land()
    {
        var log = new LauncherLog(_dataDir, _ => { });

        log.Directory.Should().Be(Path.Combine(_dataDir, "logs"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort (covers a missing directory too).
        }
    }
}
