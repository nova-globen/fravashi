using Fravashi.Launcher.Releases;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Releases;

/// <summary>Covers the launcher's SemVer comparison used by the update check (ADR 0006).</summary>
public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.0.0", 1, 0, 0, null)]
    [InlineData("2.1", 2, 1, 0, null)]
    [InlineData("3", 3, 0, 0, null)]
    [InlineData("1.0.0-rc.1", 1, 0, 0, "rc.1")]
    [InlineData("1.0.0+build.5", 1, 0, 0, null)] // build metadata ignored
    public void TryParse_ParsesTheSupportedForms(string text, int major, int minor, int patch, string? pre)
    {
        SemanticVersion.TryParse(text, out var v).Should().BeTrue();
        v.Major.Should().Be(major);
        v.Minor.Should().Be(minor);
        v.Patch.Should().Be(patch);
        v.PreRelease.Should().Be(pre);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("1.-2.3")]
    [InlineData("1.0.0-")]
    public void TryParse_RejectsMalformed(string text) =>
        SemanticVersion.TryParse(text, out _).Should().BeFalse();

    [Theory]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.1.0", "1.0.9", 1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.1", -1)]
    public void CompareTo_OrdersCoreVersions(string a, string b, int sign) =>
        Math.Sign(SemanticVersion.Parse(a).CompareTo(SemanticVersion.Parse(b))).Should().Be(sign);

    [Fact]
    public void CompareTo_PreReleaseSortsBelowItsReleaseCore()
    {
        SemanticVersion.Parse("1.0.0-rc.1").CompareTo(SemanticVersion.Parse("1.0.0"))
            .Should().BeNegative();
        SemanticVersion.Parse("1.0.0").CompareTo(SemanticVersion.Parse("1.0.0-rc.1"))
            .Should().BePositive();
        Math.Sign(SemanticVersion.Parse("1.0.0-rc.2").CompareTo(SemanticVersion.Parse("1.0.0-rc.1")))
            .Should().Be(1);
    }
}
