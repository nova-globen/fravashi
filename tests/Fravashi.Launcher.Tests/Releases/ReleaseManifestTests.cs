using Fravashi.Launcher.Releases;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Releases;

/// <summary>
/// Covers the release-manifest contract (ADR 0006) the install scripts and the T5 update path consume:
/// parsing (tolerant of comments/trailing commas/unknown fields, strict about JSON + version), variant and
/// per-arch image selection, and backward compatibility.
/// </summary>
public sealed class ReleaseManifestTests
{
    private const string SampleJson =
        """
        {
          "schemaVersion": 1,
          "product": "fravashi",
          "version": "1.0.0",
          "releasedAt": "2026-07-16T12:00:00Z",
          "releaseNotesUrl": "https://github.com/nova-globen/fravashi-releases/releases/tag/v1.0.0",
          "critical": false,
          "minSupportedVersion": "0.1.0",
          "variants": [
            {
              "name": "full",
              "displayName": "Fravashi (full)",
              "images": [
                { "os": "linux", "arch": "arm64", "asset": "fravashi-1.0.0-full-arm64.tar",
                  "assetUrl": "https://example/arm64.tar", "sizeBytes": 1680000000,
                  "sha256": "aaaa", "imageDigest": "sha256:deadbeef", "imageRef": "fravashi:1.0.0-full" },
                { "os": "linux", "arch": "amd64", "asset": "fravashi-1.0.0-full-amd64.tar",
                  "assetUrl": "https://example/amd64.tar", "sizeBytes": 1690000000,
                  "sha256": "bbbb", "imageDigest": "sha256:cafef00d", "imageRef": "fravashi:1.0.0-full" }
              ],
              "launchers": [
                { "rid": "win-x64", "asset": "fravashi-launcher-1.0.0-win-x64.zip",
                  "assetUrl": "https://example/win.zip", "sha256": "cccc" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsTheFullShape()
    {
        var manifest = ReleaseManifestParser.Parse(SampleJson);

        manifest.Product.Should().Be("fravashi");
        manifest.Version.Should().Be("1.0.0");
        manifest.Critical.Should().BeFalse();
        manifest.MinSupportedVersion.Should().Be("0.1.0");
        manifest.Variants.Should().ContainSingle();

        var full = manifest.FindVariant("FULL"); // case-insensitive
        full.Should().NotBeNull();
        full!.Images.Should().HaveCount(2);
        full.FindImage("linux", "arm64")!.ImageDigest.Should().Be("sha256:deadbeef");
        full.FindImage("linux", "amd64")!.Sha256.Should().Be("bbbb");
        full.FindLauncher("win-x64")!.Asset.Should().Be("fravashi-launcher-1.0.0-win-x64.zip");
    }

    [Fact]
    public void Parse_ReadsTheShapeTheReleaseWorkflowWrites()
    {
        // .forgejo/workflows/release.yml writes this with node after attaching the slices (backlog 157). The field
        // names are the contract between the two: a name drifting on either side reads as "no update" for ever.
        const string json =
            """
            {"schemaVersion":1,"product":"fravashi","version":"0.9.45","releasedAt":"2026-10-01T21:00:00.000Z",
             "releaseNotesUrl":"https://forge.example.test/nova-globen/fravashi/releases/tag/v0.9.45","critical":false,
             "variants":[{"name":"full","images":[{"os":"linux","arch":"amd64",
               "asset":"fravashi-v0.9.45-image-amd64.tar.gz","sizeBytes":701234567,"sha256":"abcd",
               "imageDigest":"sha256:feed",
               "partUrls":["https://forge.example.test/nova-globen/fravashi/releases/download/v0.9.45/fravashi-v0.9.45-image-amd64.tar.part00.gz",
                           "https://forge.example.test/nova-globen/fravashi/releases/download/v0.9.45/fravashi-v0.9.45-image-amd64.tar.part01.gz"]}],
               "launchers":[{"rid":"win-x64","asset":"fravashi-v0.9.45-win-x64.tar.gz",
                 "assetUrl":"https://forge.example.test/nova-globen/fravashi/releases/download/v0.9.45/fravashi-v0.9.45-win-x64.tar.gz",
                 "sha256":"ef01"}]}]}
            """;

        var image = ReleaseManifestParser.Parse(json).FindVariant("full")!.FindImage("linux", "amd64")!;

        image.AssetUrl.Should().BeNull();
        image.PartUrls.Should().HaveCount(2);
        image.PartUrls[1].Should().EndWith(".tar.part01.gz");
        image.Sha256.Should().Be("abcd");
        image.SizeBytes.Should().Be(701234567);
    }

    [Fact]
    public void FindVariant_UnknownName_ReturnsNull() =>
        ReleaseManifestParser.Parse(SampleJson).FindVariant("corp-vpn").Should().BeNull();

    [Fact]
    public void FindImage_ArchWithNoAsset_ReturnsNull() =>
        ReleaseManifestParser.Parse(SampleJson).FindVariant("full")!
            .FindImage("linux", "riscv64").Should().BeNull();

    [Fact]
    public void Parse_ToleratesCommentsTrailingCommasAndUnknownFields()
    {
        const string json =
            """
            {
              // a manifest a newer pipeline wrote
              "version": "2.0.0",
              "futureField": { "nested": true },
              "variants": [ { "name": "full", "images": [], "launchers": [] }, ],
            }
            """;

        var manifest = ReleaseManifestParser.Parse(json);
        manifest.Version.Should().Be("2.0.0");
        manifest.Variants.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("{ \"product\": \"fravashi\" }")] // no version
    public void Parse_RejectsMalformedOrVersionless(string json)
    {
        var act = () => ReleaseManifestParser.Parse(json);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void TryParse_ReturnsFalseOnGarbage()
    {
        ReleaseManifestParser.TryParse("nonsense", out _).Should().BeFalse();
        ReleaseManifestParser.TryParse(SampleJson, out var ok).Should().BeTrue();
        ok.Version.Should().Be("1.0.0");
    }
}
