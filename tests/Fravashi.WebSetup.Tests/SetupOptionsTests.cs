using AwesomeAssertions;
using Xunit;

namespace Fravashi.WebSetup.Tests;

public sealed class SetupOptionsTests : IDisposable
{
    private readonly Scratch _scratch = new();

    [Fact]
    public void No_arguments_is_the_wizard_on_the_latest_release_of_the_update_channel()
    {
        var options = SetupOptions.Parse([], out var error);

        error.Should().BeNull();
        options.Should().Be(new SetupOptions());
        options!.Quiet.Should().BeFalse();
        options.Version.Should().BeNull();
        options.Describe().Should().Be("latest from the update channel (https://nova-globen.se/fravashi/updates/latest/)");
        options.LogPath.Should().BeNull();
    }

    [Fact]
    public void The_silent_mode_takes_a_version_and_a_log()
    {
        var log = _scratch.File("logs/setup.log");

        var options = SetupOptions.Parse(["/quiet", "/version:1.0.0", $"/log:{log}"], out var error);

        error.Should().BeNull();
        options!.Quiet.Should().BeTrue();
        options.Version.Should().Be("1.0.0");
        options.LogPath.Should().Be(Path.GetFullPath(log));
    }

    [Theory]
    [InlineData("/quiet")]
    [InlineData("-quiet")]
    [InlineData("--quiet")]
    [InlineData("/QUIET")]
    [InlineData("/q")]
    [InlineData("/silent")]
    public void Quiet_is_understood_in_every_usual_spelling(string arg)
    {
        SetupOptions.Parse([arg], out _)!.Quiet.Should().BeTrue();
    }

    [Theory]
    [InlineData("/version=1.2.3")]
    [InlineData("--version:1.2.3")]
    [InlineData("-VERSION=1.2.3")]
    public void An_option_value_follows_a_colon_or_an_equals_sign(string arg)
    {
        SetupOptions.Parse([arg], out _)!.Version.Should().Be("1.2.3");
    }

    [Theory]
    [InlineData("/version:0.9.99", "0.9.99")]
    [InlineData("/version:v0.9.99", "0.9.99")]
    [InlineData("--version=0.9.100-rc.1", "0.9.100-rc.1")]
    [InlineData("/version:latest", null)]
    public void A_version_names_a_folder_of_the_update_channel(string arg, string? expected)
    {
        var options = SetupOptions.Parse([arg], out var error)!;

        error.Should().BeNull();
        options.Version.Should().Be(expected);
    }

    [Theory]
    [InlineData("/version:-Pause")]
    [InlineData("/version:0.9/../x")]
    [InlineData("/version:latest2")]
    [InlineData("/version:v")]
    public void A_version_the_engine_would_refuse_is_refused_here(string arg)
    {
        SetupOptions.Parse([arg], out var error).Should().BeNull();
        error.Should().Contain("not a version");
    }

    [Theory]
    [InlineData("/forge")]
    [InlineData("/tag:v0.9.80")]
    [InlineData("/tokenfile:C:\\t.txt")]
    [InlineData("/variant:corp-broker")]
    public void A_forge_option_from_an_older_command_line_is_refused_with_why(string arg)
    {
        // The forge retired, so these are told apart from a typo rather than ignored.
        SetupOptions.Parse([arg], out var error).Should().BeNull();
        error.Should().Contain("forge, which retired").And.Contain("FravashiSetup.exe");
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(" latest ", null)]
    [InlineData("v0.9.99", "0.9.99")]
    public void The_wizard_chooses_a_release(string typed, string? version)
    {
        var chosen = new SetupOptions().Choose(typed, out var problem)!;

        problem.Should().BeNull();
        chosen.Version.Should().Be(version);
    }

    [Fact]
    public void The_wizard_refuses_a_release_it_cannot_use()
    {
        new SetupOptions().Choose("-Pause x", out var problem).Should().BeNull();
        problem.Should().Contain("not a version");
    }

    [Theory]
    [InlineData("/frobnicate")]
    [InlineData("v0.9.80")]
    [InlineData("/quiet:yes")]
    [InlineData("/version")]
    [InlineData("/")]
    public void Anything_else_is_refused_with_the_usage(string arg)
    {
        SetupOptions.Parse([arg], out var error).Should().BeNull();
        error.Should().Contain("FravashiSetup.exe");
    }

    [Theory]
    [InlineData("/quiet")]
    [InlineData("-q")]
    [InlineData("--SILENT")]
    public void A_silent_run_is_recognised_even_when_another_argument_is_wrong(string quiet)
    {
        string[] args = [quiet, "/version:-Pause"];

        SetupOptions.Parse(args, out _).Should().BeNull();
        SetupOptions.AsksForQuiet(args).Should().BeTrue("a bad argument must still answer on the console with exit code 2, not in a message box");
    }

    [Theory]
    [InlineData("/version:quiet")]
    [InlineData("quiet")]
    [InlineData("/quiet:yes")]
    public void Only_the_switch_itself_asks_for_quiet(string arg)
    {
        SetupOptions.AsksForQuiet([arg]).Should().BeFalse();
    }

    [Fact]
    public void Help_is_its_own_request()
    {
        SetupOptions.Parse(["/?"], out _)!.ShowHelp.Should().BeTrue();
    }

    [Fact]
    public void The_usage_names_every_exit_code()
    {
        foreach (var code in Enum.GetValues<SetupExitCode>())
        {
            var number = ((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture);
            SetupOptions.Usage.Should().MatchRegex(@"(: |, )" + number + " ", "each entry follows a colon or a comma and a space");
        }
    }

    public void Dispose() => _scratch.Dispose();
}
