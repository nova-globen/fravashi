using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Setup;

/// <summary>
/// Pins what backlog 272 added to <c>setup/webinstall/webinstall.ps1</c>: a dropped transfer (curl
/// exit 18 and the others the item names) is retried a bounded number of times, resuming from the
/// second attempt onward; an HTTP refusal (22) is never one of those retries; and a resume the server
/// itself refused (33) clears the partial file before trying again. It also pins what backlog 276
/// narrowed that to: exit 22 covers both a real refusal (401/403/404) and a passing server or CDN
/// hiccup (5xx, 408, 429), told apart by the HTTP status curl's own <c>--write-out</c> reports
/// alongside the exit code, and the token prompt a refusal may lead to never blocks a non-interactive
/// session at <c>Read-Host</c>.
/// <para>
/// There was no automated test of this engine before backlog 272 (checked: no Pester anywhere in the
/// repository, nothing under <c>tests/*.Tests</c> exercised <c>webinstall.ps1</c>; it had only ever
/// been proven by hand against the real forge). This drives the real <c>Invoke-CurlDownload</c>,
/// <c>Invoke-PublicDownload</c> and <c>Die</c> functions rather than a copy of their decisions, by
/// dot-sourcing the real file with <c>FRAVASHI_WEBINSTALL_FUNCTIONS_ONLY=1</c> (which the script reads
/// before its startup banner, any network call or the rest of the install) and then pointing <c>$script:curlExe</c> at a PowerShell
/// function that stands in for curl (logging the arguments it was called with and returning an exit
/// code plus an HTTP status from a scripted sequence, one pair per call) and
/// <c>$script:ForceNonInteractiveForTest</c> at a fixed value standing in for
/// <c>[Console]::IsInputRedirected</c>.
/// </para>
/// </summary>
public class WebInstallCurlRetryTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        (dir is not null).Should().BeTrue("the test must be able to locate the repository root (Fravashi.sln)");
        return dir!.FullName;
    }

    private static string ScriptPath() => Path.Combine(RepositoryRoot(), "setup", "webinstall", "webinstall.ps1");

    // Placeholders are substituted with String.Replace (not C# string interpolation), so nothing here needs
    // escaping against PowerShell's own $ and {} syntax.
    private const string RetryLoopTemplate = """
        $env:FRAVASHI_WEBINSTALL_FUNCTIONS_ONLY = '1'
        . '__SCRIPT_PATH__'
        $script:MaxTransientCurlAttempts = __MAX_ATTEMPTS__
        $script:curlCallCount = 0
        $script:curlLog = New-Object System.Collections.Generic.List[string]
        $script:curlExitSequence = @(__EXIT_SEQUENCE__)
        $script:curlStatusSequence = @(__STATUS_SEQUENCE__)
        function FakeCurl {
            $script:curlCallCount++
            $outFile = $null
            for ($i = 0; $i -lt $args.Count; $i++) { if ($args[$i] -eq '--output') { $outFile = $args[$i + 1] } }
            $outExists = [bool]($outFile -and (Test-Path -LiteralPath $outFile))
            $script:curlLog.Add("call=$script:curlCallCount args=$($args -join ' ') outExists=$outExists")
            $code = $script:curlExitSequence[$script:curlCallCount - 1]
            $status = $script:curlStatusSequence[$script:curlCallCount - 1]
            if ($code -eq 0 -and $outFile) { Set-Content -LiteralPath $outFile -Value 'ok' }
            $global:LASTEXITCODE = $code
            Write-Output "$status"
        }
        $script:curlExe = 'FakeCurl'
        $outFile = Join-Path ([IO.Path]::GetTempPath()) ('webinstall-retry-test-' + [guid]::NewGuid().ToString('N'))
        __SEED_OUTFILE__
        $result = Invoke-CurlDownload '' $outFile 'http://example.invalid/x'
        Write-Output "RESULT=$result"
        Write-Output "CALLS=$script:curlCallCount"
        Write-Output "LASTSTATUS=$script:lastHttpStatus"
        foreach ($line in $script:curlLog) { Write-Output "LOG:$line" }
        Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
        """;

    private sealed record CurlDownloadRun(int? ExitCode, int Calls, List<string> Log, int LastHttpStatus);

    private static CurlDownloadRun RunInvokeCurlDownload(int[] exitSequence, int maxAttempts, bool seedOutFile = false, int[]? statusSequence = null)
    {
        var scriptPath = ScriptPath();
        File.Exists(scriptPath).Should().BeTrue($"{scriptPath} should exist");

        statusSequence ??= new int[exitSequence.Length];

        var command = RetryLoopTemplate
            .Replace("__SCRIPT_PATH__", scriptPath)
            .Replace("__MAX_ATTEMPTS__", maxAttempts.ToString())
            .Replace("__EXIT_SEQUENCE__", string.Join(",", exitSequence))
            .Replace("__STATUS_SEQUENCE__", string.Join(",", statusSequence))
            .Replace("__SEED_OUTFILE__", seedOutFile ? "Set-Content -LiteralPath $outFile -Value 'seed'" : "");

        var (stdout, stderr) = RealPowerShell.Run(command, TimeSpan.FromSeconds(60));
        stdout.Should().NotBeNull($"{RealPowerShell.ExeName} should run the dot-sourced retry loop to completion; stderr: {stderr}");

        int? exitCode = null;
        var calls = 0;
        var lastStatus = 0;
        var log = new List<string>();
        foreach (var line in stdout!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("RESULT=", StringComparison.Ordinal))
            {
                exitCode = int.Parse(line["RESULT=".Length..]);
            }
            else if (line.StartsWith("CALLS=", StringComparison.Ordinal))
            {
                calls = int.Parse(line["CALLS=".Length..]);
            }
            else if (line.StartsWith("LASTSTATUS=", StringComparison.Ordinal))
            {
                lastStatus = int.Parse(line["LASTSTATUS=".Length..]);
            }
            else if (line.StartsWith("LOG:", StringComparison.Ordinal))
            {
                log.Add(line["LOG:".Length..]);
            }
        }

        exitCode.Should().NotBeNull($"the script should have printed a RESULT= line; stderr: {stderr}");
        return new CurlDownloadRun(exitCode, calls, log, lastStatus);
    }

    [RealPowerShellFact]
    public void A_dropped_transfer_is_retried_and_resumes_from_the_second_attempt_on()
    {
        var run = RunInvokeCurlDownload([18, 18, 0], maxAttempts: 5);

        run.Calls.Should().Be(3, "it took three curl calls to land the success on the third try");
        run.ExitCode.Should().Be(0);
        run.Log[0].Should().NotContain("-C -", "the first attempt has nothing to resume from");
        run.Log[1].Should().Contain("-C -", "the second attempt should resume");
        run.Log[2].Should().Contain("-C -", "the third attempt should resume");
    }

    [RealPowerShellFact]
    public void A_refusal_is_never_retried_as_a_dropped_transfer()
    {
        var run = RunInvokeCurlDownload([22], maxAttempts: 5);

        run.Calls.Should().Be(1, "exit 22 is a refusal, not a transient transfer failure, so it never loops");
        run.ExitCode.Should().Be(22);
    }

    [RealPowerShellFact]
    public void A_dropped_transfer_that_never_recovers_stops_at_the_bound()
    {
        var run = RunInvokeCurlDownload([18, 18, 18], maxAttempts: 3);

        run.Calls.Should().Be(3, "the bound is 3, so a transfer that keeps dropping is retried exactly that many times, not forever");
        run.ExitCode.Should().Be(18);
    }

    [RealPowerShellFact]
    public void A_resume_the_server_refused_clears_the_partial_file_before_trying_again()
    {
        var run = RunInvokeCurlDownload([33, 0], maxAttempts: 5, seedOutFile: true);

        run.Calls.Should().Be(2);
        run.ExitCode.Should().Be(0);
        run.Log[0].Should().Contain("outExists=True", "the slice already had partial bytes on disk before the first attempt");
        run.Log[1].Should().Contain("outExists=False", "exit 33 (the resume itself did not work) should have cleared the partial file first");
    }

    // backlog 276: exit 22 (curl --fail's "an HTTP status of 400 or more") is no longer always a
    // Refusal. A 5xx/408/429 status looks exactly like a passing server or CDN hiccup, so it is
    // retried through the same loop and bound as a dropped transfer; the status rides alongside the
    // exit code via the fake curl's stdout, standing in for curl's own --write-out.
    [RealPowerShellFact]
    public void A_passing_server_error_status_on_exit_22_is_retried_like_a_dropped_transfer()
    {
        var run = RunInvokeCurlDownload([22, 22, 0], maxAttempts: 5, statusSequence: [503, 503, 0]);

        run.Calls.Should().Be(3, "two HTTP 503s (looked like a passing problem) before the third call succeeded");
        run.ExitCode.Should().Be(0);
        run.Log[0].Should().NotContain("-C -", "the first attempt has nothing to resume from");
        run.Log[1].Should().Contain("-C -", "exit 22 with a transient status resumes exactly like a dropped transfer");
        run.Log[2].Should().Contain("-C -");
    }

    [RealPowerShellFact]
    public void A_refusal_status_on_exit_22_is_never_retried()
    {
        var run = RunInvokeCurlDownload([22], maxAttempts: 5, statusSequence: [404]);

        run.Calls.Should().Be(1, "HTTP 404 on exit 22 is a real refusal, so it never loops");
        run.ExitCode.Should().Be(22);
        run.LastHttpStatus.Should().Be(404);
    }

    [RealPowerShellFact]
    public void A_persistent_server_error_status_on_exit_22_stops_at_the_bound()
    {
        var run = RunInvokeCurlDownload([22, 22, 22], maxAttempts: 3, statusSequence: [503, 503, 503]);

        run.Calls.Should().Be(3, "the bound is 3, so a status that keeps answering 503 is retried exactly that many times, not forever");
        run.ExitCode.Should().Be(22);
        run.LastHttpStatus.Should().Be(503);
    }

    // The loop-acting tests above are the primary evidence; these pin Get-CurlExitClass itself, directly,
    // against every exit code the item names plus a few unlisted ones, so the classification table is
    // visible on its own and not only through the one scenario each loop test happens to exercise.
    private static Dictionary<int, string> ClassifyExits(IReadOnlyList<int> exitCodes)
    {
        var scriptPath = ScriptPath();
        var codesLiteral = string.Join(",", exitCodes);
        var command =
            $"$env:FRAVASHI_WEBINSTALL_FUNCTIONS_ONLY = '1'; " +
            $". '{scriptPath}'; " +
            $"foreach ($c in @({codesLiteral})) {{ Write-Output \"$c=$(Get-CurlExitClass $c)\" }}";

        var (stdout, stderr) = RealPowerShell.Run(command, TimeSpan.FromSeconds(30));
        stdout.Should().NotBeNull($"{RealPowerShell.ExeName} should run the dot-sourced script to completion; stderr: {stderr}");

        var result = new Dictionary<int, string>();
        foreach (var line in stdout!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out var code))
            {
                result[code] = parts[1];
            }
        }

        result.Count.Should().Be(exitCodes.Count, $"every exit code should have produced a classification line; stderr: {stderr}");
        return result;
    }

    [RealPowerShellFact]
    public void Every_dropped_transfer_exit_the_item_names_classifies_as_transient()
    {
        var classes = ClassifyExits([18, 28, 35, 52, 55, 56]);

        classes.Should().BeEquivalentTo(new Dictionary<int, string>
        {
            [18] = "Transient",
            [28] = "Transient",
            [35] = "Transient",
            [52] = "Transient",
            [55] = "Transient",
            [56] = "Transient",
        });
    }

    [RealPowerShellFact]
    public void Exit_22_classifies_as_a_refusal_and_nothing_else_does()
    {
        var classes = ClassifyExits([22, 18, 33, 2, 6, 7, 99]);

        classes[22].Should().Be("Refusal");
        classes.Where(kv => kv.Key != 22).Select(kv => kv.Value).Should().NotContain("Refusal");
    }

    [RealPowerShellFact]
    public void An_exit_the_item_does_not_name_classifies_as_other()
    {
        var classes = ClassifyExits([0, 2, 6, 7, 99]);

        classes.Values.Should().OnlyContain(c => c == "Other");
    }

    [RealPowerShellFact]
    public void Exit_22_with_no_status_argument_still_classifies_as_a_refusal()
    {
        // Get-CurlExitClass's $HttpStatus parameter defaults to 0 ("unknown"), so every call site and
        // test from before backlog 276 (which never passed one) keeps its old meaning unchanged.
        var classes = ClassifyExits([22]);

        classes[22].Should().Be("Refusal");
    }

    // These pin Get-CurlExitClass's status argument directly, the same way the exit-code-only tests
    // above pin the exit code, so the (exit, status) classification table is visible on its own.
    private static Dictionary<(int Code, int Status), string> ClassifyExitsWithStatus(IReadOnlyList<(int Code, int Status)> pairs)
    {
        var scriptPath = ScriptPath();
        var pairsLiteral = string.Join(",", pairs.Select(p => $"@({p.Code},{p.Status})"));
        var command =
            $"$env:FRAVASHI_WEBINSTALL_FUNCTIONS_ONLY = '1'; " +
            $". '{scriptPath}'; " +
            $"foreach ($p in @({pairsLiteral})) {{ Write-Output \"$($p[0]):$($p[1])=$(Get-CurlExitClass $p[0] $p[1])\" }}";

        var (stdout, stderr) = RealPowerShell.Run(command, TimeSpan.FromSeconds(30));
        stdout.Should().NotBeNull($"{RealPowerShell.ExeName} should run the dot-sourced script to completion; stderr: {stderr}");

        var result = new Dictionary<(int Code, int Status), string>();
        foreach (var line in stdout!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.Split('=', 2);
            if (eq.Length != 2) { continue; }
            var parts = eq[0].Split(':', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out var code) && int.TryParse(parts[1], out var status))
            {
                result[(code, status)] = eq[1];
            }
        }

        result.Count.Should().Be(pairs.Count, $"every (code, status) pair should have produced a classification line; stderr: {stderr}");
        return result;
    }

    [RealPowerShellFact]
    public void Exit_22_with_a_5xx_408_or_429_status_classifies_as_transient()
    {
        var classes = ClassifyExitsWithStatus([(22, 500), (22, 502), (22, 503), (22, 504), (22, 408), (22, 429)]);

        classes.Values.Should().OnlyContain(c => c == "Transient");
    }

    [RealPowerShellFact]
    public void Exit_22_with_a_401_403_or_404_status_stays_a_refusal()
    {
        var classes = ClassifyExitsWithStatus([(22, 401), (22, 403), (22, 404), (22, 400), (22, 451)]);

        classes.Values.Should().OnlyContain(c => c == "Refusal");
    }

    [RealPowerShellFact]
    public void A_non_22_exit_ignores_the_http_status_and_keeps_its_own_classification()
    {
        var classes = ClassifyExitsWithStatus([(18, 503), (2, 404)]);

        classes[(18, 503)].Should().Be("Transient", "18 is a dropped transfer regardless of any status");
        classes[(2, 404)].Should().Be("Other", "2 is our own bad arguments regardless of any status");
    }

    // backlog 276: a non-interactive SSH session hung at a credential prompt for about ten minutes until the host killed
    // it. Since F1 every download is public and nothing asks for a credential at all (the forge path retired, infra
    // D47), so what is left to hold is that a refusal is reported as itself and a passing server error is retried, with
    // nothing waiting for an answer. These drive the real Invoke-PublicDownload/Invoke-CurlDownload/Die chain with
    // $script:ForceNonInteractiveForTest standing in for [Console]::IsInputRedirected and $script:curlExe for curl.
    private const string NonInteractiveRefusalTemplate = """
        $env:FRAVASHI_WEBINSTALL_FUNCTIONS_ONLY = '1'
        . '__SCRIPT_PATH__'
        $script:ForceNonInteractiveForTest = $true
        $script:MaxTransientCurlAttempts = 2
        $script:lastHttpStatus = 0
        $script:curlCallCount = 0
        function FakeCurl {
            $script:curlCallCount++
            $outFile = $null
            for ($i = 0; $i -lt $args.Count; $i++) { if ($args[$i] -eq '--output') { $outFile = $args[$i + 1] } }
            $global:LASTEXITCODE = 22
            Write-Output '__STATUS__'
        }
        $script:curlExe = 'FakeCurl'
        $outFile = Join-Path ([IO.Path]::GetTempPath()) ('webinstall-noninteractive-test-' + [guid]::NewGuid().ToString('N'))
        Write-Output 'BEFORE'
        Invoke-PublicDownload 'http://example.invalid/x' $outFile 'the thing'
        Write-Output 'AFTER'
        """;

    private static (string? Stdout, string Stderr) RunNonInteractiveRefusal(int httpStatus)
    {
        var scriptPath = ScriptPath();
        File.Exists(scriptPath).Should().BeTrue($"{scriptPath} should exist");

        var command = NonInteractiveRefusalTemplate
            .Replace("__SCRIPT_PATH__", scriptPath)
            .Replace("__STATUS__", httpStatus.ToString());

        return RealPowerShell.Run(command, TimeSpan.FromSeconds(30));
    }

    [RealPowerShellFact]
    public void A_refusal_in_a_non_interactive_session_is_reported_as_itself_with_nothing_to_wait_for()
    {
        var (stdout, stderr) = RunNonInteractiveRefusal(httpStatus: 404);

        stdout.Should().NotBeNull($"the run should finish well within its own timeout rather than hang at a prompt; stderr: {stderr}");
        stdout.Should().Contain("BEFORE");
        stdout.Should().Contain("HTTP 404", "the real refusal status reaches the failure message (backlog 255)");
        stdout.Should().Contain("Could not download the thing");
        stdout.Should().NotContain("retrying", "a refusal is never retried as a dropped transfer");
        stdout.Should().NotContain("AFTER", "Die exits the whole process; execution should never return to the caller");
        stdout.Should().NotContain("token", "every download is public, so nothing ever asks for a credential");
    }

    [RealPowerShellFact]
    public void A_passing_server_error_in_a_non_interactive_session_is_retried_and_then_reported_as_itself()
    {
        var (stdout, stderr) = RunNonInteractiveRefusal(httpStatus: 503);

        stdout.Should().NotBeNull($"stderr: {stderr}");
        stdout.Should().Contain("BEFORE");
        // With MaxTransientCurlAttempts=2 there are two curl calls and exactly one retry line between them, then the
        // failure once the bound is spent.
        var retryLineCount = stdout!.Split("looked like a dropped transfer; retrying", StringSplitOptions.None).Length - 1;
        retryLineCount.Should().Be(1, "a 503 on exit 22 is Transient (backlog 276), so it goes through the same retry loop as a dropped transfer, once, before the bound (2) is spent");
        stdout.Should().Contain("Could not download the thing", "after the bound is reached it fails as itself");
        stdout.Should().NotContain("AFTER", "Die exits the whole process; execution should never return to the caller");
    }

    // backlog 276: Die's "never blocks at a prompt nobody can answer" has to hold on every path that
    // reaches it, including -Pause (which Wait-BeforeClosing owns): a session with
    // redirected stdin has nobody to press Enter either.
    [RealPowerShellFact]
    public void Die_with_Pause_set_in_a_non_interactive_session_still_does_not_wait_at_a_prompt()
    {
        var scriptPath = ScriptPath();
        File.Exists(scriptPath).Should().BeTrue($"{scriptPath} should exist");

        var command = """
            $env:FRAVASHI_WEBINSTALL_FUNCTIONS_ONLY = '1'
            . '__SCRIPT_PATH__'
            $Pause = $true
            $script:ForceNonInteractiveForTest = $true
            Write-Output 'BEFORE'
            Die 'refused for a test'
            Write-Output 'AFTER'
            """.Replace("__SCRIPT_PATH__", scriptPath);

        var (stdout, stderr) = RealPowerShell.Run(command, TimeSpan.FromSeconds(15));

        // RealPowerShell runs the child with -NonInteractive (see RealPowerShell.cs), under which
        // Read-Host THROWS a terminating error immediately rather than blocking, so finishing inside
        // the timeout proves nothing by itself: the pre-fix Wait-BeforeClosing (an unconditional
        // Read-Host) also returns quickly here, just via an uncaught error instead of a deliberate
        // check. The one line that tells the two apart is the message the fixed path writes when it
        // decides not to prompt.
        stdout.Should().NotBeNull($"stderr: {stderr}");
        stdout.Should().Contain("BEFORE");
        stdout.Should().Contain("refused for a test");
        stdout.Should().Contain(
            "closing without waiting",
            "Wait-BeforeClosing should recognize the non-interactive session and skip Read-Host on purpose, rather than let it throw uncaught");
        stdout.Should().NotContain("AFTER", "Die calls exit 1 after Wait-BeforeClosing; execution should never return to the caller");
    }
}
