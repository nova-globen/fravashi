using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.WebSetup.Tests;

/// <summary>
/// One whole run through <see cref="SetupRun"/>, with a shell script standing in for powershell.exe. The script
/// records the argument list it was given and whether the engine it was pointed at is the embedded one, then narrates
/// like the engine does. What the real engine does with those arguments is held by its own PowerShell tests and, end to
/// end, by the VM install.
/// </summary>
public sealed class SetupRunTests : IDisposable
{
    private readonly Scratch _scratch = new();

    // Beside the test assembly rather than in the temp folder: the studio's container mounts /tmp noexec.
    private readonly string _fake = Path.Combine(AppContext.BaseDirectory, "fake-powershell-" + Guid.NewGuid().ToString("N"));

    private string FakePowerShell(string body)
    {
        var path = _fake;
        var script = "#!/bin/sh\n" +
                     $"out='{_scratch.Folder}'\n" +
                     "printf '%s\\n' \"$@\" > \"$out/argv.txt\"\n" +
                     // The engine path follows -File.
                     "engine=''; prev=''\n" +
                     "for a in \"$@\"; do\n" +
                     "  [ \"$prev\" = '-File' ] && engine=\"$a\"\n" +
                     "  prev=\"$a\"\n" +
                     "done\n" +
                     "cp \"$engine\" \"$out/engine-seen.ps1\"\n" +
                     "echo \"$engine\" > \"$out/engine-path.txt\"\n" +
                     body + "\n";
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private async Task<(SetupVerdict Verdict, List<string> Lines, string Log)> Run(string body, SetupOptions options)
    {
        var host = new SetupHost(FakePowerShell(body), _scratch.Folder, new UTF8Encoding(false));
        var lines = new List<string>();
        var logPath = _scratch.File("setup.log");
        SetupVerdict verdict;
        using (var log = SetupLog.Open(logPath))
        {
            verdict = await SetupRun.RunAsync(options, host, log, new EngineProgress(),
                line => { lock (lines) { lines.Add(line); } }, CancellationToken.None);
        }

        return (verdict, lines, File.ReadAllText(logPath));
    }

    [ShellFact]
    public async Task A_successful_install_runs_the_embedded_engine_against_the_update_channel_and_reports_installed()
    {
        var (verdict, lines, log) = await Run(
            "echo 'Looking up the release ...'; echo 'Release: 0.9.99 (manifest signature verified against the release key)'; " +
            "echo 'Nova Globen Fravashi 0.9.99 is installed.'",
            new SetupOptions { Quiet = true });

        verdict.Code.Should().Be(SetupExitCode.Installed, verdict.Headline + " " + verdict.Detail);
        verdict.Headline.Should().Contain("0.9.99");
        lines.Should().Contain("Nova Globen Fravashi 0.9.99 is installed.");
        File.ReadAllBytes(_scratch.File("engine-seen.ps1")).Should().Equal(File.ReadAllBytes(Repository.EnginePath));
        File.ReadAllLines(_scratch.File("argv.txt")).Should().Equal(
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            File.ReadAllText(_scratch.File("engine-path.txt")).Trim(),
            "-DownloadBase", "https://nova-globen.se/fravashi/updates");
        log.Should().Contain("from the update channel (https://nova-globen.se/fravashi/updates/latest/)");
        // The engine's own line uses the full name; the verdict, a sentence the person reads, uses the short one.
        log.Should().Contain("Running: ").And.Contain("Verdict: Fravashi 0.9.99 is installed.").And.Contain("exit code 0");
    }

    [ShellFact]
    public async Task A_version_reaches_the_engine_after_the_channel()
    {
        var (verdict, _, log) = await Run("echo 'Nova Globen Fravashi 1.0.0 is installed.'", new SetupOptions { Quiet = true, Version = "1.0.0" });

        verdict.Code.Should().Be(SetupExitCode.Installed, verdict.Headline + " " + verdict.Detail);
        File.ReadAllLines(_scratch.File("argv.txt")).Should().EndWith(["-Version", "1.0.0"]);
        log.Should().Contain("1.0.0 from the update channel");
    }

    [ShellFact]
    public async Task Nothing_published_on_the_update_channel_ends_in_exit_code_6()
    {
        var (verdict, _, log) = await Run(
            "echo 'Looking up the release ...'; echo 'Downloading release-manifest.json ...'; echo ''; " +
            "echo 'No Fravashi release is published at https://updates.example.test/fravashi/9.9.9 yet (HTTP 404). Check the version.'; exit 1",
            new SetupOptions { Quiet = true, Version = "9.9.9" });

        verdict.Code.Should().Be(SetupExitCode.NotPublished);
        ((int)verdict.Code).Should().Be(6);
        verdict.Detail.Should().Contain("Check the version");
        log.Should().Contain("exit code 6");
    }

    [ShellFact]
    public async Task The_private_engine_folder_is_gone_after_the_run_even_when_the_engine_failed()
    {
        await Run("exit 1", new SetupOptions());

        Directory.GetDirectories(_scratch.Folder, "fravashi-websetup-*").Should().BeEmpty();
    }

    [UnwritableFolderFact]
    public async Task What_the_cleanup_could_not_remove_is_named_in_the_log_and_the_verdict()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            return;
        }

        try
        {
            // The stand-in locks its own engine folder, so the EXE's cleanup cannot remove it afterwards.
            var (verdict, lines, log) = await Run(
                "chmod 500 \"$(dirname \"$engine\")\"; echo 'Nova Globen Fravashi 1.0.0 is installed.'", new SetupOptions());

            verdict.Code.Should().Be(SetupExitCode.Installed, "a leftover folder does not undo an install");
            verdict.Detail.Should().Contain("Left behind:").And.Contain("fravashi-websetup-");
            log.Should().Contain("Left behind:");
            lines.Should().Contain(l => l.StartsWith("Left behind:", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var folder in Directory.GetDirectories(_scratch.Folder, "fravashi-websetup-*"))
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public async Task A_run_that_fails_before_the_engine_starts_still_removes_its_folder()
    {
        // A version EngineCommand.For refuses: the run fails after the engine was written out and before it ran.
        var logPath = _scratch.File("setup.log");

        SetupVerdict verdict;
        using (var log = SetupLog.Open(logPath))
        {
            verdict = await SetupRun.RunAsync(new SetupOptions { Version = "-Pause" },
                new SetupHost("powershell.exe", _scratch.Folder, new UTF8Encoding(false)), log, new EngineProgress(),
                _ => { }, CancellationToken.None);
        }

        verdict.Code.Should().Be(SetupExitCode.EngineDidNotRun);
        Directory.GetDirectories(_scratch.Folder, "fravashi-websetup-*").Should().BeEmpty();
        File.ReadAllText(logPath).Should().Contain("exit code 4");
    }

    [Fact]
    public async Task A_PowerShell_that_cannot_start_ends_in_exit_code_4_with_its_folder_removed()
    {
        var logPath = _scratch.File("setup.log");

        SetupVerdict verdict;
        using (var log = SetupLog.Open(logPath))
        {
            verdict = await SetupRun.RunAsync(new SetupOptions(),
                new SetupHost(_scratch.File("no-such-powershell"), _scratch.Folder, new UTF8Encoding(false)), log,
                new EngineProgress(), _ => { }, CancellationToken.None);
        }

        verdict.Code.Should().Be(SetupExitCode.EngineDidNotRun);
        Directory.GetDirectories(_scratch.Folder, "fravashi-websetup-*").Should().BeEmpty();
    }

    [Fact]
    public async Task A_temp_folder_that_cannot_be_written_ends_in_exit_code_4_not_an_exception()
    {
        // A FILE where the temp folder should be: creating the engine's folder under it fails on every platform.
        var notAFolder = _scratch.File("not-a-folder");
        File.WriteAllText(notAFolder, "");
        var logPath = _scratch.File("setup.log");

        SetupVerdict verdict;
        using (var log = SetupLog.Open(logPath))
        {
            verdict = await SetupRun.RunAsync(new SetupOptions { Quiet = true },
                new SetupHost("powershell.exe", notAFolder, new UTF8Encoding(false)), log, new EngineProgress(),
                _ => { }, CancellationToken.None);
        }

        verdict.Code.Should().Be(SetupExitCode.EngineDidNotRun);
        ((int)verdict.Code).Should().Be(4);
        File.ReadAllText(logPath).Should().Contain("could not prepare its temporary folder").And.Contain("exit code 4");
    }

    [ShellFact]
    public async Task A_failure_is_reported_with_the_engines_reason()
    {
        var (verdict, _, _) = await Run(
            "echo 'Downloading x ...'; printf '## 50.0%%\\r' >&2; echo ''; echo 'Could not download x from u (curl exit 6).'; exit 1",
            new SetupOptions());

        verdict.Code.Should().Be(SetupExitCode.Failed);
        verdict.Detail.Should().Contain("curl exit 6");
    }

    public void Dispose()
    {
        File.Delete(_fake);
        _scratch.Dispose();
    }
}
