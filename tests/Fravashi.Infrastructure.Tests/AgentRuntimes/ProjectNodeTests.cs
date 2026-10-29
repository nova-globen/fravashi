using Fravashi.Infrastructure.AgentRuntimes;
using Microsoft.Extensions.Configuration;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

/// <summary>Plan 2d decision 10: project Node resolution and the provisioned-path lookup.</summary>
public sealed class ProjectNodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "fravashi-node-tests", Guid.NewGuid().ToString("N"));

    public ProjectNodeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Nvmrc_wins_and_the_v_prefix_is_stripped()
    {
        File.WriteAllText(Path.Combine(_dir, ".nvmrc"), "v22.12.0\n");
        File.WriteAllText(Path.Combine(_dir, ".node-version"), "20.0.0\n");

        ProjectNodeResolver.Resolve(_dir).Should().Be("22.12.0");
    }

    [Fact]
    public void Node_version_file_is_second_and_engines_third()
    {
        File.WriteAllText(Path.Combine(_dir, ".node-version"), "20.11\n");
        ProjectNodeResolver.Resolve(_dir).Should().Be("20.11");

        File.Delete(Path.Combine(_dir, ".node-version"));
        File.WriteAllText(Path.Combine(_dir, "package.json"), """{"engines":{"node":">=18.19 <19"}}""");
        ProjectNodeResolver.Resolve(_dir).Should().Be("18.19");
    }

    [Fact]
    public void Nothing_requested_resolves_null()
    {
        ProjectNodeResolver.Resolve(_dir).Should().BeNull();
        ProjectNodeResolver.Resolve(Path.Combine(_dir, "missing")).Should().BeNull();
        ProjectNodeResolver.ResolveRequest(_dir).Should().BeNull();
    }

    [Fact]
    public void The_request_names_the_file_that_asked()
    {
        // Backlog 10: the banner says who asked, so a reader knows which file to change.
        File.WriteAllText(Path.Combine(_dir, "package.json"), """{"engines":{"node":"^20.11"}}""");
        ProjectNodeResolver.ResolveRequest(_dir).Should().Be(new ProjectNodeRequest("20.11", "package.json"));

        File.WriteAllText(Path.Combine(_dir, ".node-version"), "18.19.0\n");
        ProjectNodeResolver.ResolveRequest(_dir).Should().Be(new ProjectNodeRequest("18.19.0", ".node-version"));

        File.WriteAllText(Path.Combine(_dir, ".nvmrc"), "lts/iron\n");
        // A name rather than a number in .nvmrc is not a version; the next file answers.
        ProjectNodeResolver.ResolveRequest(_dir).Should().Be(new ProjectNodeRequest("18.19.0", ".node-version"));
    }

    [Fact]
    public void A_prefix_serves_the_numerically_newest_provisioned_version()
    {
        // "22.9.0" sorts above "22.10.0" as text; the lookup must not serve the older one for "22".
        var nvsHome = Path.Combine(_dir, "nvs");
        Directory.CreateDirectory(Path.Combine(nvsHome, "node", "22.10.0", "x64", "bin"));
        Directory.CreateDirectory(Path.Combine(nvsHome, "node", "22.9.0", "x64", "bin"));
        var provisioner = new NodePathProvisioner(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NVS_HOME"] = nvsHome }).Build());

        provisioner.ProvisionedVersionFor("22").Should().Be("22.10.0");
        provisioner.PathPrefixFor("22").Should().Be(Path.Combine(nvsHome, "node", "22.10.0", "x64", "bin"));
        provisioner.ProvisionedVersionFor("21").Should().BeNull();
        provisioner.InstallDirectoryFor("v22.12.0", "arm64").Should().Be(Path.Combine(nvsHome, "node", "22.12.0", "arm64"));
        provisioner.NvsHome.Should().Be(nvsHome);
    }

    [Fact]
    public void Provisioned_versions_resolve_their_bin_path_and_prefixes_match_the_newest()
    {
        var nvsHome = Path.Combine(_dir, "nvs");
        Directory.CreateDirectory(Path.Combine(nvsHome, "node", "22.12.0", "x64", "bin"));
        Directory.CreateDirectory(Path.Combine(nvsHome, "node", "22.9.0", "x64", "bin"));
        var provisioner = new NodePathProvisioner(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NVS_HOME"] = nvsHome }).Build());

        provisioner.PathPrefixFor("22.12.0").Should().Be(
            Path.Combine(nvsHome, "node", "22.12.0", "x64", "bin"));
        provisioner.PathPrefixFor("v22.12.0").Should().NotBeNull();
        provisioner.PathPrefixFor("22").Should().Contain("22.");
        provisioner.PathPrefixFor("18").Should().BeNull();
        provisioner.PathPrefixFor(null).Should().BeNull();
    }

    [Fact]
    public void No_nvs_home_resolves_nothing()
    {
        var provisioner = new NodePathProvisioner(new ConfigurationBuilder().Build());

        provisioner.PathPrefixFor("22.12.0").Should().BeNull();
    }
}
