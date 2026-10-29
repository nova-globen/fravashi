using Fravashi.Application.Updates;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// The app-side version comparison behind the Type A tool-update check (ADR 0006): decide whether a registry's
/// latest is newer than the installed CLI version, failing closed to "no update" on anything unparseable.
/// </summary>
public class SemVerTests
{
    [Theory]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.0", "2.0.0", false)]
    public void IsNewer_compares_core_versions(string latest, string installed, bool expected) =>
        SemVer.IsNewer(latest, installed).Should().Be(expected);

    [Fact]
    public void IsNewer_treats_release_as_newer_than_its_prerelease() =>
        SemVer.IsNewer("1.0.0", "1.0.0-rc.1").Should().BeTrue();

    [Fact]
    public void IsNewer_treats_prerelease_as_older_than_release() =>
        SemVer.IsNewer("1.0.0-rc.1", "1.0.0").Should().BeFalse();

    [Theory]
    [InlineData("v1.2.0", "1.1.0")]
    [InlineData("1.2.0+build.5", "1.1.0")]
    public void IsNewer_tolerates_v_prefix_and_build_metadata(string latest, string installed) =>
        SemVer.IsNewer(latest, installed).Should().BeTrue();

    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("1.1.0", null)]
    [InlineData("not-a-version", "1.0.0")]
    [InlineData("1.1.0", "garbage")]
    [InlineData("", "")]
    public void IsNewer_fails_closed_on_unparseable_input(string? latest, string? installed) =>
        SemVer.IsNewer(latest, installed).Should().BeFalse();
}
