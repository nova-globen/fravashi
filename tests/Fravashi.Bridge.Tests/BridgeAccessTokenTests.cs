using Fravashi.Bridge;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

public sealed class BridgeAccessTokenTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "fravashi-bridge-tests", Guid.NewGuid().ToString("N"));

    public BridgeAccessTokenTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void GetOrCreate_MintsOnce_AndPersists()
    {
        var first = BridgeAccessToken.GetOrCreate(_directory);
        var second = BridgeAccessToken.GetOrCreate(_directory);

        first.Should().NotBeNullOrWhiteSpace();
        second.Should().Be(first);
        BridgeAccessToken.TryRead(_directory).Should().Be(first);
        File.Exists(Path.Combine(_directory, BridgeAccessToken.FileName)).Should().BeTrue();
    }

    [Fact]
    public void TryRead_WithoutAFile_ReturnsNull()
        => BridgeAccessToken.TryRead(_directory).Should().BeNull();

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("wrong", false)]
    [InlineData("expected-token-value", true)]
    public void Verify_ComparesConstantTime(string? presented, bool expected)
        => BridgeAccessToken.Verify(presented, "expected-token-value").Should().Be(expected);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
