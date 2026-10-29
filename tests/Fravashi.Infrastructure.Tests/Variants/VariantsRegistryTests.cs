using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Variants;

/// <summary>
/// Guards the release variant registry (setup/release/variants.json) that the release workflow fans out into a
/// build matrix (variant × arch), one asset set with its own sha256 + image digest per variant (D03-3 / I1). B4
/// adds the <c>corp-broker</c> variant; this asserts it is wired with the broker feature build-arg and - the
/// load-bearing invariant - that no secret (the org key, the broker signing secret, a token, a base URL) is a
/// build argument, so nothing sensitive is ever baked into a published image layer.
/// </summary>
public sealed class VariantsRegistryTests
{
    private static JsonElement Registry()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test must be able to locate the repository root (Fravashi.sln)");
        var path = Path.Combine(dir!.FullName, "setup", "release", "variants.json");
        File.Exists(path).Should().BeTrue($"variants.json should exist at {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    private static JsonElement Variant(string name) =>
        Registry().GetProperty("variants").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);

    [Fact]
    public void The_registry_lists_full_corp_vpn_and_corp_broker()
    {
        var names = Registry().GetProperty("variants").EnumerateArray()
            .Select(v => v.GetProperty("name").GetString()).ToList();

        names.Should().Contain(["full", "corp-vpn", "corp-broker"]);
    }

    [Fact]
    public void Corp_broker_variant_ships_the_broker_feature_build_arg()
    {
        var buildArgs = Variant("corp-broker").GetProperty("buildArgs");

        buildArgs.GetProperty("VARIANT").GetString().Should().Be("corp-broker");
        buildArgs.GetProperty("BROKER_REQUIRED").GetString().Should().Be("true");
    }

    [Fact]
    public void No_variant_carries_a_secret_or_a_broker_endpoint_as_a_build_arg()
    {
        // The org key + the broker endpoint + any token are DEPLOY-time values, never build args - a build arg is
        // baked into the image. This keeps the corp-broker guarantee real: the image ships no org key and no
        // hard-coded broker host.
        foreach (var variant in Registry().GetProperty("variants").EnumerateArray())
        {
            foreach (var arg in variant.GetProperty("buildArgs").EnumerateObject())
            {
                var key = arg.Name.ToLowerInvariant();
                key.Should().NotContain("key");
                key.Should().NotContain("secret");
                key.Should().NotContain("token");
                key.Should().NotContain("baseurl");
                key.Should().NotContain("password");
            }
        }
    }
}
