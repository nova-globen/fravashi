using System.Diagnostics;
using Fravashi.Infrastructure.AgentRuntimes;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Resolves the REAL agent CLI for the contract tests (backlog 61 / ADR 0023), or explains why there is none.
/// <para>
/// It resolves ONLY from <c>FRAVASHI_CLAUDE_CLI</c> or the in-container path
/// <c>/data/.npm-global/bin/claude</c>, and <b>deliberately never searches PATH</b>. A developer's own CLI on
/// PATH is not the product's pinned CLI: binding the standard suite to it would make the suite assert against a
/// binary Fravashi does not ship, and turn the owner updating his personal install into a red build. The cost of
/// that choice is that the contract tests skip unless someone points them at a CLI on purpose, which is the
/// correct trade: a test that silently passes against the wrong binary proves nothing.
/// </para>
/// </summary>
internal static class RealClaudeCli
{
    /// <summary>Points the contract tests at a specific CLI binary.</summary>
    public const string PathEnvVar = "FRAVASHI_CLAUDE_CLI";

    /// <summary>Opts into the tier that makes a real, billable model call.</summary>
    public const string LiveEnvVar = "FRAVASHI_CLI_CONTRACT_LIVE";

    /// <summary>The path the studio image installs the CLI to.</summary>
    public const string InContainerPath = "/data/.npm-global/bin/claude";

    /// <summary>
    /// Names the config home the live tier's CLI children run with (backlog 248). It is its OWN variable because this
    /// test assembly clears an inherited <c>CLAUDE_CONFIG_DIR</c> before any test runs (backlog 169,
    /// <c>TestProcessEnvironment</c>), so setting that one on the <c>dotnet test</c> command, as ADR 0023 said to, never
    /// reached a child and every live turn answered "Not logged in" (measured 2026-10-14). Point it at a throwaway
    /// folder a person signed a CLI into; unset, the children keep whatever the platform gives them (the container
    /// recipe's <c>HOME</c>).
    /// </summary>
    public const string ConfigHomeEnvVar = "FRAVASHI_CLI_CONTRACT_CONFIG_DIR";

    /// <summary>
    /// What a live child gets on top of the environment allowlist: <c>CLAUDE_CONFIG_DIR</c> from
    /// <see cref="ConfigHomeEnvVar"/> when it is set, otherwise nothing.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ChildEnvironment =>
        Environment.GetEnvironmentVariable(ConfigHomeEnvVar) is { Length: > 0 } home
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["CLAUDE_CONFIG_DIR"] = home.Trim() }
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Applies <see cref="ChildEnvironment"/> to a process about to start.</summary>
    public static void ApplyTo(ProcessStartInfo info)
    {
        foreach (var (key, value) in ChildEnvironment)
        {
            info.Environment[key] = value;
        }
    }

    private static readonly Lazy<string?> Resolved = new(Resolve);
    private static readonly Lazy<string?> Version = new(ReadVersion);

    /// <summary>The real CLI to drive, or null when none was pointed at.</summary>
    public static string? Path => Resolved.Value;

    /// <summary>What <c>&lt;cli&gt; --version</c> reports, parsed the same way the product parses it.</summary>
    public static string? ReportedVersion => Version.Value;

    /// <summary>Null when the offline contract tests can run; otherwise why they cannot.</summary>
    public static string? SkipReason =>
        Path is not null
            ? null
            : $"No real agent CLI to test against. Set {PathEnvVar} to a CLI binary, or run inside the studio "
              + $"container where it lives at {InContainerPath}. PATH is deliberately not searched: a personal "
              + "install is not the version the product pins.";

    /// <summary>Null when the live tier can run; otherwise why it cannot.</summary>
    public static string? LiveSkipReason =>
        SkipReason
        ?? (string.Equals(Environment.GetEnvironmentVariable(LiveEnvVar), "1", StringComparison.Ordinal)
            ? null
            : $"Set {LiveEnvVar}=1 to run the live contract tier. It starts the real CLI and takes a real turn, "
              + "so it needs working authentication, network access, and it costs money.");

    private static string? Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
        {
            return configured.Trim();
        }

        return File.Exists(InContainerPath) ? InContainerPath : null;
    }

    private static string? ReadVersion()
    {
        if (Path is not { } cli)
        {
            return null;
        }

        var output = Run(cli, ["--version"], TimeSpan.FromSeconds(30));
        return output is null ? null : ProcessAgentRuntimeService.ParseVersion(output);
    }

    /// <summary>Runs the CLI and returns stdout+stderr, or null when it could not be run to completion.</summary>
    public static string? Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
                {
                }

                return null;
            }

            return stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// A fact that runs only against a real agent CLI. The skip is set in the CONSTRUCTOR, at discovery time,
/// because <c>Assert.Skip</c> needs xunit v3 runtime infrastructure and this repository pins xunit 2.9.3, where
/// it would fail the test instead of skipping it.
/// </summary>
internal sealed class RealClaudeCliFactAttribute : FactAttribute
{
    public RealClaudeCliFactAttribute() => Skip = RealClaudeCli.SkipReason;
}

/// <summary>A fact that additionally takes a real, billable turn; doubly opt-in.</summary>
internal sealed class LiveClaudeCliFactAttribute : FactAttribute
{
    public LiveClaudeCliFactAttribute() => Skip = RealClaudeCli.LiveSkipReason;
}
