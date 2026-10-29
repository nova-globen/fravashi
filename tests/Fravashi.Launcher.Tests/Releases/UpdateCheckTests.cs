using Fravashi.Launcher.Releases;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Releases;

/// <summary>
/// Covers the update-decision contract (ADR 0006 / D03-2 / D03-3) that T5's <c>fravashi</c> product-update path
/// is built on: variant-scoped, arch-matched, and critical classification (manifest flag or version floor).
/// </summary>
public sealed class UpdateCheckTests
{
    private static ReleaseManifest Manifest(
        string version = "1.1.0",
        bool critical = false,
        string? minSupported = null,
        params string[] variants)
    {
        var names = variants.Length == 0 ? ["full"] : variants;
        return new ReleaseManifest
        {
            Version = version,
            Critical = critical,
            MinSupportedVersion = minSupported,
            ReleaseNotesUrl = "https://example/notes",
            Variants = names.Select(n => new ReleaseVariant
            {
                Name = n,
                Images =
                [
                    new ReleaseImage { Os = "linux", Arch = "arm64", Asset = $"img-{n}-arm64.tar", Sha256 = "a", ImageDigest = "sha256:aa" },
                    new ReleaseImage { Os = "linux", Arch = "amd64", Asset = $"img-{n}-amd64.tar", Sha256 = "b", ImageDigest = "sha256:bb" },
                ],
            }).ToList(),
        };
    }

    [Fact]
    public void NewerVersion_SameVariant_MatchingArch_IsAvailable()
    {
        var result = UpdateCheck.Evaluate(Manifest(version: "1.1.0"), "1.0.0", "full", "arm64");

        result.Kind.Should().Be(UpdateKind.Available);
        result.TargetVersion.Should().Be("1.1.0");
        result.Image!.Asset.Should().Be("img-full-arm64.tar"); // native arch selected
        result.ReleaseNotesUrl.Should().Be("https://example/notes");
    }

    [Fact]
    public void SameOrOlderVersion_IsNone()
    {
        UpdateCheck.Evaluate(Manifest(version: "1.0.0"), "1.0.0", "full", "arm64").Kind.Should().Be(UpdateKind.None);
        UpdateCheck.Evaluate(Manifest(version: "0.9.0"), "1.0.0", "full", "arm64").Kind.Should().Be(UpdateKind.None);
    }

    [Fact]
    public void DifferentVariant_IsNeverOffered() // D03-3: never cross variants
    {
        var manifest = Manifest(version: "2.0.0", variants: "corp-vpn");
        UpdateCheck.Evaluate(manifest, "1.0.0", "full", "arm64").Should().Be(UpdateAvailability.None);
    }

    [Fact]
    public void NoImageForHostArch_IsNone()
    {
        var manifest = new ReleaseManifest
        {
            Version = "2.0.0",
            Variants = [new ReleaseVariant { Name = "full", Images = [new ReleaseImage { Os = "linux", Arch = "amd64" }] }],
        };
        UpdateCheck.Evaluate(manifest, "1.0.0", "full", "arm64").Kind.Should().Be(UpdateKind.None);
    }

    [Fact]
    public void ManifestCriticalFlag_MakesItCritical()
    {
        UpdateCheck.Evaluate(Manifest(version: "1.1.0", critical: true), "1.0.0", "full", "amd64")
            .Kind.Should().Be(UpdateKind.Critical);
    }

    [Fact]
    public void BelowMinSupportedFloor_IsCritical_EvenWhenNotFlagged()
    {
        UpdateCheck.Evaluate(Manifest(version: "1.1.0", minSupported: "1.0.5"), "1.0.0", "full", "amd64")
            .Kind.Should().Be(UpdateKind.Critical);
        // At or above the floor and not flagged => ordinary.
        UpdateCheck.Evaluate(Manifest(version: "1.1.0", minSupported: "1.0.0"), "1.0.0", "full", "amd64")
            .Kind.Should().Be(UpdateKind.Available);
    }

    [Theory]
    [InlineData("not-a-version", "1.0.0")]
    [InlineData("1.1.0", "not-a-version")]
    public void UnparseableVersion_FailsClosedToNone(string manifestVersion, string installed) =>
        UpdateCheck.Evaluate(Manifest(version: manifestVersion), installed, "full", "arm64")
            .Kind.Should().Be(UpdateKind.None);
}
