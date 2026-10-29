using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.WebSetup.Tests;

/// <summary>A fact that needs a POSIX shell to stand in for Windows PowerShell; it skips on Windows.</summary>
internal sealed class ShellFactAttribute : FactAttribute
{
    public ShellFactAttribute()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Skip = "Drives /bin/sh as a stand-in for the engine; there is none on Windows, where the host's VM install proves the real one.";
        }
    }
}

/// <summary>
/// A fact that needs a folder its owner cannot write, made with Unix file modes; it skips on Windows, and for
/// root, whom file modes do not stop.
/// </summary>
internal sealed class UnwritableFolderFactAttribute : FactAttribute
{
    public UnwritableFolderFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Makes a folder unwritable with Unix file modes, which Windows does not have.";
        }
        else if (Environment.UserName == "root")
        {
            Skip = "Runs as root, whom file modes do not stop, so the folder cannot be made unwritable.";
        }
    }
}

public sealed class EngineRunnerTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private static async Task<(EngineExit Exit, List<string> Lines, List<string> Fragments)> Run(string script, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        var fragments = new List<string>();
        var exit = await EngineRunner.RunAsync(
            new EngineCommand("/bin/sh", ["-c", script]), Utf8,
            line => { lock (lines) { lines.Add(line); } },
            fragment => { lock (fragments) { fragments.Add(fragment); } },
            cancellationToken);
        return (exit, lines, fragments);
    }

    [ShellFact]
    public async Task Stdout_arrives_as_lines_and_the_exit_code_is_the_processes()
    {
        var (exit, lines, _) = await Run("echo one; echo; echo two; exit 1");

        exit.Should().Be(new EngineExit(1, null, false));
        lines.Should().Equal("one", "", "two");
    }

    [ShellFact]
    public async Task Stdin_is_a_closed_pipe_so_a_prompt_sees_end_of_file_instead_of_waiting()
    {
        // The engine's Read-Host guard rests on stdin being redirected (backlog 276); a closed pipe also turns
        // any read that slipped past it into an immediate end of file rather than a ten-minute hang.
        var (exit, lines, _) = await Run("if [ -t 0 ]; then echo tty; else echo pipe; fi; if read answer; then echo got; else echo eof; fi")
            .WaitAsync(TimeSpan.FromSeconds(30));

        exit.ExitCode.Should().Be(0);
        lines.Should().Equal("pipe", "eof");
    }

    [ShellFact]
    public async Task Stderr_is_split_on_carriage_returns_as_curl_redraws_its_bar()
    {
        var (_, _, fragments) = await Run(@"printf '##   10.0%%\r####  20.0%%\r###### 100.0%%\ncurl: (6) Could not resolve host\n' >&2");

        fragments.Should().Equal("##   10.0%", "####  20.0%", "###### 100.0%", "curl: (6) Could not resolve host");
    }

    [ShellFact]
    public async Task The_last_lines_are_read_before_the_exit_is_reported()
    {
        var (exit, lines, _) = await Run("i=0; while [ $i -lt 500 ]; do echo line$i; i=$((i+1)); done; echo the reason; exit 1");

        exit.ExitCode.Should().Be(1);
        lines.Should().HaveCount(501);
        lines[^1].Should().Be("the reason", "the engine's reason is its last line, so it must never be lost to a race");
    }

    [Fact]
    public async Task A_program_that_does_not_exist_is_reported_as_not_started()
    {
        var exit = await EngineRunner.RunAsync(
            new EngineCommand(Path.Combine(Path.GetTempPath(), "no-such-powershell-" + Guid.NewGuid().ToString("N")), []),
            Utf8, _ => { }, _ => { }, CancellationToken.None);

        exit.ExitCode.Should().BeNull();
        exit.Cancelled.Should().BeFalse();
        exit.StartError.Should().Contain("could not be started");
    }

    [Fact]
    public void The_engine_starts_without_the_parents_PSModulePath_and_with_the_rest_of_its_environment()
    {
        // Started from a PowerShell 7 session the EXE carries PowerShell 7's module path, and Windows PowerShell
        // 5.1 then cannot load Get-FileHash (the host's first run of websetup-v2.0.0, backlog 296).
        var before = Environment.GetEnvironmentVariable("PSModulePath");
        Environment.SetEnvironmentVariable("PSModulePath", @"C:\Program Files\PowerShell\7\Modules");
        try
        {
            var info = EngineRunner.StartInfo(new EngineCommand("powershell.exe", ["-File", "engine.ps1"]), Utf8);

            info.Environment.Keys.Should().NotContain(k => string.Equals(k, "PSModulePath", StringComparison.OrdinalIgnoreCase));
            info.Environment.Should().ContainKey("PATH", "only the module path is withheld; the engine needs the rest");
            info.ArgumentList.Should().Equal("-File", "engine.ps1");
            info.RedirectStandardInput.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", before);
        }
    }

    [ShellFact]
    public async Task The_engine_process_sees_no_PSModulePath_in_any_spelling()
    {
        // Windows matches environment names without regard to case and Linux does not, so a lower-case spelling
        // in the parent stands for the same variable and must go too.
        var before = (Environment.GetEnvironmentVariable("PSModulePath"), Environment.GetEnvironmentVariable("psmodulepath"));
        Environment.SetEnvironmentVariable("PSModulePath", "/pwsh7/Modules");
        Environment.SetEnvironmentVariable("psmodulepath", "/pwsh7/modules");
        try
        {
            var (exit, lines, _) = await Run("env | grep -i '^psmodulepath=' || echo none");

            exit.ExitCode.Should().Be(0);
            lines.Should().Equal("none");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", before.Item1);
            Environment.SetEnvironmentVariable("psmodulepath", before.Item2);
        }
    }

    [ShellFact]
    public async Task Cancelling_kills_the_engine_and_says_so()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var (exit, lines, _) = await Run("echo started; sleep 60; echo never", cancel.Token).WaitAsync(TimeSpan.FromSeconds(30));

        exit.Should().Be(new EngineExit(null, null, true));
        lines.Should().NotContain("never");
    }
}
