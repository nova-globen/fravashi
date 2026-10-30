using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Install;

/// <summary>
/// Backlog 374: <c>setup/install/firewall-rule.ps1</c>, the installer's one elevated step, gives fravashi.exe its own
/// inbound firewall rule, because a first start on a locked console meets Windows' "Query User" prompt, which records
/// Block at once and never asks again (measured on the test VM, 2026-10-30). These drive the script's pure functions,
/// dot-sourced with <c>FRAVASHI_FIREWALL_FUNCTIONS_ONLY=1</c>: which rules go, the rule that comes, how the installer
/// runs it and what it says when it could not. Nothing here touches the firewall; the real rule is proven on the test
/// VM. They need pwsh, and skip with that reason where there is none (the studio's container, the Linux runner).
/// </summary>
public sealed class FirewallRuleScriptTests
{
    private const string Exe = @"C:\Users\ada\AppData\Local\Programs\Fravashi\fravashi.exe";

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("the repository root (Fravashi.sln) was not found");
    }

    private static string Script => Path.Combine(Root(), "setup", "install", "firewall-rule.ps1");

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Runs <paramref name="body"/> after dot-sourcing the script's functions and returns stdout.</summary>
    private static string Run(string body)
    {
        var command = "$ErrorActionPreference = 'Stop'\n$env:FRAVASHI_FIREWALL_FUNCTIONS_ONLY = '1'\n. " + Quote(Script) + "\n" + body;
        var (code, stdout, stderr) = Pwsh.Run(command, TimeSpan.FromSeconds(60));
        code.Should().Be(0, $"pwsh should run the dot-sourced functions; stderr: {stderr}");
        return stdout.Trim();
    }

    [PwshFact]
    public void The_prompts_leftovers_and_its_own_rule_go_and_nothing_else_does()
    {
        var lowered = Exe.ToLowerInvariant();
        var output = Run($$"""
            $env:FRAVASHI_TEST_HOME = 'C:\Users\ada\AppData\Local'
            $rules = @(
              [pscustomobject]@{ Name = 'TCP Query User{A}{{lowered}}'; Action = 'Block'; Program = '{{lowered}}' },
              [pscustomobject]@{ Name = 'UDP Query User{B}{{lowered}}'; Action = 'Allow'; Program = '{{lowered}}' },
              [pscustomobject]@{ Name = 'Fravashi-Inbound'; Action = 'Allow'; Program = '{{Exe}}' },
              [pscustomobject]@{ Name = 'An old block'; Action = 'Block'; Program = '%FRAVASHI_TEST_HOME%\Programs\Fravashi\fravashi.exe' },
              [pscustomobject]@{ Name = 'Somebody allowed it'; Action = 'Allow'; Program = '{{Exe}}' },
              [pscustomobject]@{ Name = 'TCP Query User{C}c:\other\app.exe'; Action = 'Block'; Program = 'c:\other\app.exe' },
              [pscustomobject]@{ Name = 'No program'; Action = 'Block'; Program = $null }
            )
            (Select-RulesToRemove -Rules $rules -Program '{{Exe}}' | ForEach-Object Name) -join '|'
            """);

        output.Split('|').Should().BeEquivalentTo(
            "TCP Query User{A}" + lowered, "UDP Query User{B}" + lowered, "Fravashi-Inbound", "An old block");
    }

    [PwshFact]
    public void The_rule_is_inbound_tcp_for_the_program_from_the_local_networks_and_pins_no_port()
    {
        var spec = JsonDocument.Parse(Run($"New-RuleSpec '{Exe}' | ConvertTo-Json -Compress")).RootElement;

        spec.GetProperty("Name").GetString().Should().Be("Fravashi-Inbound");
        spec.GetProperty("Direction").GetString().Should().Be("Inbound");
        spec.GetProperty("Action").GetString().Should().Be("Allow");
        spec.GetProperty("Protocol").GetString().Should().Be("TCP");
        spec.GetProperty("Program").GetString().Should().Be(Exe);
        spec.GetProperty("Profile").GetString().Should().Be("Any", "the Hyper-V Default Switch is a Public network");
        spec.GetProperty("RemoteAddress").GetString().Should().Be("LocalSubnet", "never the internet");
        spec.TryGetProperty("LocalPort", out _).Should().BeFalse("the listeners fall back to a random free port and keep it");
    }

    [PwshFact]
    public void An_elevated_install_sets_it_directly_a_quiet_one_never_asks_and_any_other_asks_once()
    {
        Run("""
            (Get-FirewallStepMode -Elevated $true -NoElevationPrompt $false),
            (Get-FirewallStepMode -Elevated $true -NoElevationPrompt $true),
            (Get-FirewallStepMode -Elevated $false -NoElevationPrompt $true),
            (Get-FirewallStepMode -Elevated $false -NoElevationPrompt $false) -join '|'
            """).Should().Be("direct|direct|skip|prompt");
    }

    [PwshFact]
    public void Without_the_rule_the_installer_says_why_what_may_happen_and_the_command_that_sets_it()
    {
        var advice = Run($"(Get-FirewallAdvice -Program '{Exe}' -Why 'the administrator prompt was declined') -join \"`n\"");

        advice.Should().Contain("(the administrator prompt was declined)");
        advice.Should().Contain("choose Allow");
        advice.Should().Contain("locked screen");
        advice.Should().Contain(@"-File ""C:\Users\ada\AppData\Local\Programs\Fravashi\firewall-rule.ps1"" -Program """ + Exe + @"""");
    }

    [Fact]
    public void The_installer_runs_it_and_the_bundle_carries_it()
    {
        var installer = File.ReadAllText(Path.Combine(Root(), "setup", "install", "install-windows.ps1"));
        installer.Should().Contain("[switch]$NoElevationPrompt");
        installer.Should().Contain("Copy-Item (Join-Path $bundleRoot 'firewall-rule.ps1') $firewallScript");
        installer.Should().Contain("-Verb RunAs", "a non-elevated install asks once");
        installer.Should().Contain("Wait-Job $job -Timeout", "a prompt nobody answers must not hang the install");

        File.ReadAllText(Path.Combine(Root(), "setup", "build-bundle.sh")).Should().Contain("install/firewall-rule.ps1");
        File.ReadAllText(Path.Combine(Root(), "setup", "webinstall", "webinstall.ps1"))
            .Should().Contain("$installArgs.NoElevationPrompt = $true");
    }

    /// <summary>pwsh on PATH, or why not. Kept here rather than shared because this file ships in the public tree.</summary>
    private static class Pwsh
    {
        private static readonly Lazy<string?> Missing = new(() =>
            Run("$PSVersionTable.PSVersion.ToString()", TimeSpan.FromSeconds(20)).Code == 0
                ? null
                : "pwsh (PowerShell 7) is not on PATH here (the studio's container and the Linux runner ship none); the installer it tests runs on Windows.");

        public static string? SkipReason => Missing.Value;

        public static (int Code, string Stdout, string Stderr) Run(string command, TimeSpan timeout)
        {
            var info = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            {
                info.ArgumentList.Add(argument);
            }

            try
            {
                using var process = Process.Start(info);
                if (process is null)
                {
                    return (-1, "", "pwsh did not start");
                }

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeout))
                {
                    process.Kill(entireProcessTree: true);
                    return (-1, "", $"timed out after {timeout}");
                }

                return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                return (-1, "", ex.Message);
            }
        }
    }

    private sealed class PwshFactAttribute : FactAttribute
    {
        public PwshFactAttribute() => Skip = Pwsh.SkipReason;
    }
}
