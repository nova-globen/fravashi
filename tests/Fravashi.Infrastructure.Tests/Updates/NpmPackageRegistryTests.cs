using System.Text.Json;
using Fravashi.Infrastructure.Updates;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Updates;

/// <summary>The registry-response parsing that decides the "latest" version (Type A check).</summary>
public class NpmPackageRegistryTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Reads_version_from_the_latest_manifest_shape() =>
        NpmPackageRegistry.ExtractVersion(Root("""{ "name": "x", "version": "1.4.2" }""")).Should().Be("1.4.2");

    [Fact]
    public void Falls_back_to_dist_tags_latest() =>
        NpmPackageRegistry.ExtractVersion(Root("""{ "dist-tags": { "latest": "9.9.9" } }""")).Should().Be("9.9.9");

    [Fact]
    public void Returns_null_when_no_version_is_present() =>
        NpmPackageRegistry.ExtractVersion(Root("""{ "name": "x" }""")).Should().BeNull();

    [Fact]
    public void Returns_null_for_a_non_object() =>
        NpmPackageRegistry.ExtractVersion(Root("\"just a string\"")).Should().BeNull();
}
