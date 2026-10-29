using AwesomeAssertions;
using Fravashi.Domain.Releases;
using Xunit;

namespace Fravashi.Domain.Tests.Releases;

/// <summary>Work item #368: the one shape a release version (and so a release tag) may have.</summary>
public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.9.97", true, 0, 9, 97, null)]
    [InlineData("1.4.0", false, 1, 4, 0, null)]
    [InlineData("v1.4.0-rc.1", true, 1, 4, 0, "rc.1")]
    [InlineData("v2.0.0-beta-2", true, 2, 0, 0, "beta-2")]
    public void A_version_in_the_shape_is_read_into_its_parts(string text, bool prefixed, int major, int minor, int patch, string? label)
    {
        var version = ReleaseVersion.Parse(text);

        version.Should().NotBeNull();
        version!.Text.Should().Be(text);
        version.Prefixed.Should().Be(prefixed);
        (version.Major, version.Minor, version.Patch).Should().Be((major, minor, patch));
        version.Label.Should().Be(label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v01.2.3")]
    [InlineData("V1.2.3")]
    [InlineData(" v1.2.3")]
    [InlineData("v1.2.3 ")]
    [InlineData("v1.2.3-")]
    [InlineData("v1.2.3-rc_1")]
    [InlineData("v1.2.3-rc..1")]
    [InlineData("v1.2.3-rc.")]
    [InlineData("v1.2.3-x.lock")]
    [InlineData("v1.2.3:refs/heads/main")]
    [InlineData("v1.2.3^{}")]
    [InlineData("-v1.2.3")]
    [InlineData("v1.2.3/x")]
    [InlineData("v9999999999.0.0")]
    public void Anything_else_is_refused(string? text)
    {
        ReleaseVersion.Parse(text).Should().BeNull();
        ReleaseVersion.IsValid(text).Should().BeFalse();
    }

    [Fact]
    public void The_next_versions_keep_the_prefix_and_drop_the_label()
    {
        var version = ReleaseVersion.Parse("v0.9.97-rc.2")!;

        version.NextPatch().Text.Should().Be("v0.9.98");
        version.NextMinor().Text.Should().Be("v0.10.0");
        version.NextMajor().Text.Should().Be("v1.0.0");
        ReleaseVersion.Parse("1.2.3")!.NextPatch().Text.Should().Be("1.2.4");
    }

    [Fact]
    public void The_latest_orders_by_number_with_a_label_below_its_release_and_ignores_other_names()
    {
        ReleaseVersion.Latest(["v0.9.9", "v0.9.97", "v0.9.100-rc.1", "nightly", "v0.9.10", "broker-v1.0.5"])!.Text
            .Should().Be("v0.9.100-rc.1");
        ReleaseVersion.Latest(["v1.0.0-rc.1", "v1.0.0"])!.Text.Should().Be("v1.0.0");
        ReleaseVersion.Latest(["nightly"]).Should().BeNull();
    }
}
