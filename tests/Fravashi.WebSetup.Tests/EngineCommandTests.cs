using AwesomeAssertions;
using Xunit;

namespace Fravashi.WebSetup.Tests;

public sealed class EngineCommandTests
{
    private static readonly string Script = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ws", "webinstall.ps1"));

    [Fact]
    public void Windows_PowerShell_is_named_by_its_full_path_under_the_system_folder()
    {
        EngineCommand.WindowsPowerShell("SYSTEM32").Should().Be(Path.Combine("SYSTEM32", "WindowsPowerShell", "v1.0", "powershell.exe"));
    }

    [Fact]
    public void By_default_the_engine_runs_on_this_builds_update_channel()
    {
        var command = EngineCommand.For("powershell.exe", Script, new SetupOptions());

        command.FileName.Should().Be("powershell.exe");
        command.Arguments.Should().Equal(
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script,
            "-DownloadBase", "https://nova-globen.se/fravashi/updates");
    }

    [Fact]
    public void A_version_is_passed_as_the_engines_version()
    {
        EngineCommand.For("p", Script, new SetupOptions { Version = "0.9.99" }).Arguments
            .Should().EndWith(["-File", Script, "-DownloadBase", SetupOptions.DownloadsBase, "-Version", "0.9.99"]);
    }

    [Fact]
    public void A_version_that_got_past_parsing_some_other_way_is_still_refused()
    {
        ((Action)(() => EngineCommand.For("p", Script, new SetupOptions { Version = "-Pause" }))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Pause_is_never_passed_because_nobody_is_at_the_engines_console()
    {
        var command = EngineCommand.For("powershell.exe", Script, new SetupOptions { Quiet = true });

        command.Arguments.Should().NotContain("-Pause");
    }

    [Fact]
    public void A_path_with_spaces_stays_one_argument()
    {
        var spaced = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "with space", "webinstall.ps1"));

        var command = EngineCommand.For("p", spaced, new SetupOptions());

        command.Arguments.Should().Contain(spaced);
        command.ToString().Should().Contain("\"" + spaced + "\"");
    }
}
