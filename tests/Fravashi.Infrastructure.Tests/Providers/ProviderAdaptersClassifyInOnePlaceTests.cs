using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Providers;

/// <summary>
/// Backlog 232 (plan @58): "never left" is decided in ONE place for every provider adapter, <c>ProviderCallFailure</c>
/// over <c>NetworkFailure.Classify</c>. Four copies of it once sat in the four adapters, and the outbox's rule 3 (a call
/// that left with no answer is looked up, never resent) is only as good as the copy that drifted. This reads the source,
/// so an adapter that starts classifying on its own again fails the build rather than a review.
/// </summary>
public sealed class ProviderAdaptersClassifyInOnePlaceTests
{
    private static readonly string[] Classified =
    [
        "src/Fravashi.Infrastructure/Tracker/Atlassian/AtlassianJiraGateway.cs",
        "src/Fravashi.Infrastructure/Tracker/AzureDevOps/AzureDevOpsBoardsGateway.cs",
        "src/Fravashi.Infrastructure/Wiki/AtlassianConfluencePageGateway.cs",
        "src/Fravashi.Infrastructure/Wiki/AzureDevOpsWikiPageGateway.cs",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test must be able to locate the repository root (Fravashi.sln)");
        return dir!.FullName;
    }

    [Fact]
    public void The_four_adapters_classify_through_the_shared_rule()
    {
        foreach (var file in Classified)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), file));
            text.Should().Contain("ProviderCallFailure.Result", $"{file} must classify a failed call through the shared rule");
            text.Should().NotContain("HttpRequestError", $"{file} must not decide on its own which phase a failure came from");
        }
    }

    [Fact]
    public void No_provider_gateway_classifies_a_failed_call_on_its_own()
    {
        var infrastructure = Path.Combine(RepoRoot(), "src", "Fravashi.Infrastructure");
        var gateways = Directory.EnumerateFiles(infrastructure, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(file => file.Text.Contains(": IProviderGateway", StringComparison.Ordinal)
                || file.Text.Contains(": IWikiPageGateway", StringComparison.Ordinal))
            .ToList();

        gateways.Select(g => Path.GetFileName(g.Path)).Should().Contain(Classified.Select(Path.GetFileName),
            "the scan must at least see the four adapters, or it proves nothing");
        foreach (var (path, text) in gateways)
        {
            text.Should().NotContain("HttpRequestError", $"{Path.GetFileName(path)} must leave never-left to ProviderCallFailure");
            text.Should().NotContain("catch (HttpRequestException", $"{Path.GetFileName(path)} must leave never-left to ProviderCallFailure");
        }
    }
}
