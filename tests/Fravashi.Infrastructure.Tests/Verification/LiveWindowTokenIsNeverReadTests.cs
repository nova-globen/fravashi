using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// Backlog 226 (plan @69): the verifier and the app runner never read, pass or log the LIVE studio's window token. A
/// verification run's token is minted by the app runner for that run; the live one lives in fravashi.exe's
/// <c>window.token</c> and the live container's <c>FRAVASHI_WINDOW_TOKEN</c>, and nothing on the verification path may
/// name either as something to read. This reads the source, so a future edit that reaches for them fails here.
/// </summary>
public sealed class LiveWindowTokenIsNeverReadTests
{
    /// <summary>Everything on the path from "run this scenario" to "the browser presents a marker".</summary>
    private static readonly string[] VerificationPath =
    [
        "src/Fravashi.AppRunner",
        "src/Fravashi.Infrastructure/AppRuns",
        "src/Fravashi.Infrastructure/Verification",
        "src/Fravashi.Application/Verification",
        "src/Fravashi.Application/WorkItems/ScenarioRunner.cs",
        "src/Fravashi.Application/WorkItems/IApplicationRuns.cs",
    ];

    /// <summary>The one file that may spell the window token's NAME: it is the name a verification run's token is SET under.</summary>
    private const string TheFieldsDefinition = "VerificationRunField.cs";

    [Fact]
    public void Nothing_on_the_verification_path_reads_the_live_studios_window_token()
    {
        var root = RepositoryRoot();
        var files = VerificationPath
            .Select(p => Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar)))
            .SelectMany(p => File.Exists(p) ? [p] : Directory.GetFiles(p, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        files.Should().HaveCountGreaterThan(10, "the path was found");
        files.Select(Path.GetFileName).Should().Contain([TheFieldsDefinition, "AppRunnerClient.cs", "PlaywrightDesignCheck.cs", "WindowMarker.cs"]);

        var reaches = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            foreach (var forbidden in new[] { "window.token", "WindowAccessToken", "TokenConfigKey", "GetEnvironmentVariable(VerificationRunField" })
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                {
                    reaches.Add($"{name}: {forbidden}");
                }
            }

            if (name != TheFieldsDefinition && text.Contains("FRAVASHI_WINDOW_TOKEN", StringComparison.Ordinal))
            {
                reaches.Add($"{name}: FRAVASHI_WINDOW_TOKEN");
            }

            if (name != TheFieldsDefinition && text.Contains("WindowTokenName", StringComparison.Ordinal))
            {
                reaches.Add($"{name}: WindowTokenName");
            }
        }

        reaches.Should().BeEmpty();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Fravashi.sln) was not found.");
    }
}
