using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public sealed class DesktopCommandLineTests
{
    [Theory]
    [InlineData(new string[0], DesktopVerb.Default)]
    [InlineData(new[] { "run" }, DesktopVerb.Default)]
    [InlineData(new[] { "onboarding" }, DesktopVerb.Onboarding)]
    [InlineData(new[] { "settings" }, DesktopVerb.Onboarding)]
    [InlineData(new[] { "stop" }, DesktopVerb.Stop)]
    [InlineData(new[] { "exit" }, DesktopVerb.Exit)]
    [InlineData(new[] { "status" }, DesktopVerb.Status)]
    [InlineData(new[] { "update" }, DesktopVerb.Update)]
    [InlineData(new[] { "repin" }, DesktopVerb.Repin)]
    [InlineData(new[] { "website" }, DesktopVerb.Website)]
    [InlineData(new[] { "logs" }, DesktopVerb.Logs)]
    [InlineData(new[] { "broker" }, DesktopVerb.Broker)]
    [InlineData(new[] { "help" }, DesktopVerb.Help)]
    [InlineData(new[] { "--help" }, DesktopVerb.Help)]
    [InlineData(new[] { "-h" }, DesktopVerb.Help)]
    [InlineData(new[] { "version" }, DesktopVerb.Version)]
    [InlineData(new[] { "--version" }, DesktopVerb.Version)]
    [InlineData(new[] { "STATUS" }, DesktopVerb.Status)] // case-insensitive
    public void Parse_ResolvesVerb(string[] args, DesktopVerb expected)
        => DesktopCommandLine.Parse(args).Verb.Should().Be(expected);

    [Theory]
    [InlineData(DesktopVerb.Default, true)]
    [InlineData(DesktopVerb.Onboarding, true)]
    [InlineData(DesktopVerb.Stop, false)]
    [InlineData(DesktopVerb.Exit, false)]
    [InlineData(DesktopVerb.Status, false)]
    [InlineData(DesktopVerb.Website, false)]
    [InlineData(DesktopVerb.Logs, false)]
    [InlineData(DesktopVerb.Broker, false)]
    [InlineData(DesktopVerb.Help, false)]
    [InlineData(DesktopVerb.Version, false)]
    [InlineData(DesktopVerb.Update, false)]
    [InlineData(DesktopVerb.Repin, false)]
    [InlineData(DesktopVerb.Unknown, false)]
    public void IsWindowVerb_SplitsGuiVerbsFromConsoleVerbs(DesktopVerb verb, bool expected)
        => DesktopCommandLine.IsWindowVerb(verb).Should().Be(expected);

    [Fact]
    public void Parse_OpenOption_CarriesTheFolderOnWindowVerbs()
    {
        DesktopCommandLine.Parse(["--open", @"C:\repos\widgets"]).Should().BeEquivalentTo(
            new ParsedCommand(DesktopVerb.Default, OpenPath: @"C:\repos\widgets"));

        var onboarding = DesktopCommandLine.Parse(["onboarding", "--open", @"C:\repos\widgets"]);
        onboarding.Verb.Should().Be(DesktopVerb.Onboarding);
        onboarding.OpenPath.Should().Be(@"C:\repos\widgets");
    }

    [Fact]
    public void Parse_OpenOption_WithoutAValue_IsUnknown()
    {
        var parsed = DesktopCommandLine.Parse(["--open"]);

        parsed.Verb.Should().Be(DesktopVerb.Unknown);
        parsed.UnknownToken.Should().Be("--open");
    }

    [Fact]
    public void Parse_UnknownVerb_CapturesToken()
    {
        var parsed = DesktopCommandLine.Parse(new[] { "frobnicate" });

        parsed.Verb.Should().Be(DesktopVerb.Unknown);
        parsed.UnknownToken.Should().Be("frobnicate");
    }

    [Fact]
    public void Parse_ReadsGlobalOptions()
    {
        var parsed = DesktopCommandLine.Parse(new[] { "stop", "--config", @"C:\tmp\l.json", "--name", "studio-test" });

        parsed.Verb.Should().Be(DesktopVerb.Stop);
        parsed.ConfigPath.Should().Be(@"C:\tmp\l.json");
        parsed.ContainerName.Should().Be("studio-test");
    }

    [Fact]
    public void Parse_OptionsBeforeVerb_StillResolvesVerb()
    {
        var parsed = DesktopCommandLine.Parse(new[] { "--config", "x.json", "status" });

        parsed.Verb.Should().Be(DesktopVerb.Status);
        parsed.ConfigPath.Should().Be("x.json");
    }

    [Fact]
    public void Parse_Broker_CapturesUrlArgument()
    {
        var parsed = DesktopCommandLine.Parse(new[] { "broker", "http://broker.example:8080" });

        parsed.Verb.Should().Be(DesktopVerb.Broker);
        parsed.BrokerUrl.Should().Be("http://broker.example:8080");
    }

    [Fact]
    public void Parse_Broker_WithNoArgument_LeavesUrlNull()
    {
        var parsed = DesktopCommandLine.Parse(new[] { "broker" });

        parsed.Verb.Should().Be(DesktopVerb.Broker);
        parsed.BrokerUrl.Should().BeNull();
    }

    [Fact]
    public void Parse_Broker_UrlArgSurvivesInterleavedOptions()
    {
        var parsed = DesktopCommandLine.Parse(new[] { "broker", "--config", "x.json", "http://b:8080" });

        parsed.Verb.Should().Be(DesktopVerb.Broker);
        parsed.BrokerUrl.Should().Be("http://b:8080");
        parsed.ConfigPath.Should().Be("x.json");
    }

    [Fact]
    public void Parse_Restore_CapturesTheArchivePath()
    {
        var parsed = DesktopCommandLine.Parse(new[] { "restore", @"C:\backups\fravashi-backup-20260913-070509.zip" });

        parsed.Verb.Should().Be(DesktopVerb.Restore);
        parsed.ArchivePath.Should().Be(@"C:\backups\fravashi-backup-20260913-070509.zip");
    }

    [Fact]
    public void Parse_Restore_WithNoArgument_LeavesThePathNull()
    {
        var parsed = DesktopCommandLine.Parse(new[] { "restore" });

        parsed.Verb.Should().Be(DesktopVerb.Restore);
        parsed.ArchivePath.Should().BeNull();
    }

    [Fact]
    public void Restore_IsAConsoleVerb_NotAWindowOne()
        // It runs with the studio stopped and reports to a console; opening a window would be opening the
        // thing the restore is replacing.
        => DesktopCommandLine.IsWindowVerb(DesktopVerb.Restore).Should().BeFalse();

    [Fact]
    public void Help_MentionsBroker()
        => DesktopCommandLine.HelpText.Should().Contain("broker");

    [Fact]
    public void Help_MentionsRestore()
        => DesktopCommandLine.HelpText.Should().Contain("restore ZIP");

    [Fact]
    public void Help_MentionsRepin()
        => DesktopCommandLine.HelpText.Should().Contain("repin");

    [Fact]
    public void Parse_Tail_DefaultsTo200_AndReadsOverride()
    {
        DesktopCommandLine.Parse(new[] { "logs" }).Tail.Should().Be(200);
        DesktopCommandLine.Parse(new[] { "logs", "--tail", "42" }).Tail.Should().Be(42);
    }

    [Fact]
    public void Execute_Help_PrintsReference()
    {
        var output = new List<string>();
        var app = BuildApp();

        DesktopCommandLine.Execute(new ParsedCommand(DesktopVerb.Help), app, output.Add).Should().Be(ExitCodes.Success);

        string.Join('\n', output).Should().Contain("Usage: fravashi");
        string.Join('\n', output).Should().Contain("onboarding");
    }

    [Fact]
    public void Execute_Version_PrintsVersion()
    {
        var output = new List<string>();

        DesktopCommandLine.Execute(new ParsedCommand(DesktopVerb.Version), BuildApp(), output.Add).Should().Be(ExitCodes.Success);

        output.Should().ContainSingle().Which.Should().StartWith("fravashi ");
    }

    [Fact]
    public void Execute_Unknown_ReturnsUsageError()
    {
        var output = new List<string>();

        DesktopCommandLine.Execute(new ParsedCommand(DesktopVerb.Unknown, UnknownToken: "frob"), BuildApp(), output.Add)
            .Should().Be(ExitCodes.UsageError);

        string.Join('\n', output).Should().Contain("unknown command 'frob'");
    }

    private static LauncherApp BuildApp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fravashi-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new LauncherApp(
            new FakeContainerRuntime(),
            new LauncherConfigStore(Path.Combine(dir, "launcher.json")),
            dir,
            log: _ => { },
            openBrowser: _ => { },
            isPortFree: _ => true,
            tray: new FakeTrayControl());
    }
}
