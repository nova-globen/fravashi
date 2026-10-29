using AwesomeAssertions;
using Xunit;

namespace Fravashi.WebSetup.Tests;

public sealed class SetupVerdictTests
{
    private static EngineProgress After(params string[] lines)
    {
        var progress = new EngineProgress();
        foreach (var line in lines)
        {
            progress.OnOutputLine(line);
        }

        return progress;
    }

    [Fact]
    public void Exit_zero_after_the_engines_installed_line_is_installed()
    {
        var verdict = SetupVerdict.From(new EngineExit(0, null, false),
            After("Release: 1.0.0 (manifest signature verified against the release key)", "Nova Globen Fravashi 1.0.0 is installed."));

        verdict.Code.Should().Be(SetupExitCode.Installed);
        ((int)verdict.Code).Should().Be(0);
        verdict.Headline.Should().Contain("1.0.0");
    }

    [Fact]
    public void Exit_zero_without_the_installed_line_is_not_called_an_install()
    {
        var verdict = SetupVerdict.From(new EngineExit(0, null, false), After("Looking up the release ..."));

        verdict.Code.Should().Be(SetupExitCode.EngineDidNotRun);
    }

    [Fact]
    public void Exit_one_is_a_failure_whose_reason_is_the_engines_last_line()
    {
        var verdict = SetupVerdict.From(new EngineExit(1, null, false),
            After("Downloading x ...", "",
                  "sha256 MISMATCH for x (manifest aa, got bb). Refusing to install a corrupt or tampered download."));

        verdict.Code.Should().Be(SetupExitCode.Failed);
        ((int)verdict.Code).Should().Be(1);
        verdict.Detail.Should().StartWith("sha256 MISMATCH");
    }

    [Fact]
    public void Nothing_published_on_the_update_channel_is_its_own_verdict()
    {
        var verdict = SetupVerdict.From(new EngineExit(1, null, false),
            After("Looking up the release ...", "Downloading release-manifest.json ...", "",
                  "No Fravashi release is published at https://updates.example.test/fravashi/9.9.9 yet (HTTP 404). Check the version."));

        verdict.Code.Should().Be(SetupExitCode.NotPublished);
        ((int)verdict.Code).Should().Be(6);
        verdict.Headline.Should().Contain("update channel");
        verdict.Detail.Should().Contain("Check the version").And.Contain("updates.example.test/fravashi/9.9.9");
    }

    [Fact]
    public void The_traps_FAILED_prefix_is_dropped_from_the_reason()
    {
        var verdict = SetupVerdict.From(new EngineExit(1, null, false), After("FAILED: the disk is full"));

        verdict.Detail.Should().Be("the disk is full");
    }

    [Fact]
    public void A_failure_stdout_never_explained_takes_its_reason_from_stderr()
    {
        var progress = new EngineProgress();
        progress.OnErrorFragment("A parameter cannot be found that matches parameter name 'Bogus'.");

        var verdict = SetupVerdict.From(new EngineExit(1, null, false), progress);

        verdict.Detail.Should().Contain("Bogus");
    }

    [Fact]
    public void A_transient_failure_that_ran_out_of_retries_is_a_failure()
    {
        // Backlog 276: a 5xx is retried by the engine and then reported as itself.
        var verdict = SetupVerdict.From(new EngineExit(1, null, false),
            After("Could not download x from u (curl exit 22, HTTP 502)."));

        verdict.Code.Should().Be(SetupExitCode.Failed);
    }

    [Fact]
    public void Exit_code_3_is_never_given_again()
    {
        // It meant "the forge refused without a token" until the forge retired, and a contract value is
        // never reused for another meaning.
        Enum.GetValues<SetupExitCode>().Select(c => (int)c).Should().NotContain(3);
    }

    [Theory]
    [InlineData(-196608)]
    [InlineData(2)]
    [InlineData(64)]
    public void Any_other_exit_code_did_not_come_from_the_engine(int code)
    {
        var verdict = SetupVerdict.From(new EngineExit(code, null, false), After());

        verdict.Code.Should().Be(SetupExitCode.EngineDidNotRun);
        verdict.Headline.Should().Contain(code.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_process_that_never_started_says_so()
    {
        var verdict = SetupVerdict.From(new EngineExit(null, "powershell.exe could not be started: not found", false), After());

        verdict.Code.Should().Be(SetupExitCode.EngineDidNotRun);
        ((int)verdict.Code).Should().Be(4);
        verdict.Detail.Should().Contain("not found");
    }

    [Fact]
    public void A_cancellation_before_the_handoff_says_nothing_was_installed()
    {
        var verdict = SetupVerdict.From(new EngineExit(null, null, true), After("Downloading x ..."));

        verdict.Code.Should().Be(SetupExitCode.Cancelled);
        verdict.Detail.Should().Contain("Nothing was installed");
    }

    [Fact]
    public void A_cancellation_after_the_handoff_does_not_claim_nothing_was_installed()
    {
        var verdict = SetupVerdict.From(new EngineExit(null, null, true),
            After("Handing off to the release's install-windows.ps1 ..."));

        verdict.Detail.Should().NotContain("Nothing was installed");
    }
}
