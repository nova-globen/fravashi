using AwesomeAssertions;
using Fravashi.Infrastructure.AgentRuntimes;
using Xunit;
using Xunit.Abstractions;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

/// <summary>
/// Backlog 10: ONE real run of the download-and-unpack path against nodejs.org. Opt-in: it skips with its
/// reason unless <see cref="ScratchEnvVar"/> names a scratch folder, because it pulls tens of MB over the
/// network and a standard run must not. It asks for the 22 line and lets the release index pick the exact
/// version, which is the same path the banner's click takes for a workspace whose <c>.nvmrc</c> says "22".
/// The skip is set in the CONSTRUCTOR because <c>Assert.Skip</c> needs xunit v3 (see RealClaudeCli).
/// </summary>
public sealed class NodeProvisionerLiveTests(ITestOutputHelper output)
{
    public const string ScratchEnvVar = "FRAVASHI_NODE_LIVE_DIR";

    private sealed class NodeLiveFactAttribute : FactAttribute
    {
        public NodeLiveFactAttribute() => Skip = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ScratchEnvVar))
            ? $"Set {ScratchEnvVar} to a scratch folder to fetch a real Node 22 tarball from nodejs.org into it."
            : null;
    }

    [NodeLiveFact]
    public async Task A_real_node_22_lands_in_the_nvs_layout()
    {
        var scratch = Environment.GetEnvironmentVariable(ScratchEnvVar)!;
        Directory.CreateDirectory(scratch);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var provisioner = new NodeProvisioner(http, scratch, NodeDistributionPlatform.LinuxX64);
        var reports = new List<NodeProvisionProgress>();
        var progress = new Progress<NodeProvisionProgress>(r =>
        {
            lock (reports)
            {
                reports.Add(r);
            }
        });

        var result = await provisioner.InstallAsync("22", progress);

        output.WriteLine($"Succeeded={result.Succeeded} Version={result.Version} Bin={result.BinDirectory} SkippedLinks={result.SkippedLinks} Error={result.Error}");
        NodeProvisionProgress[] snapshot;
        lock (reports)
        {
            snapshot = [.. reports];
        }

        foreach (var phase in snapshot.GroupBy(r => r.Phase))
        {
            var last = phase.Last();
            output.WriteLine($"{phase.Key}: {phase.Count()} report(s), last {last.BytesReceived} of {last.TotalBytes} bytes");
        }

        result.Succeeded.Should().BeTrue(result.Error);
        result.Version.Should().StartWith("22.");
        var node = Path.Combine(scratch, "node", result.Version!, "x64", "bin", "node");
        output.WriteLine($"bin/node: {node} ({new FileInfo(node).Length} bytes)");
        File.Exists(node).Should().BeTrue();
    }
}
