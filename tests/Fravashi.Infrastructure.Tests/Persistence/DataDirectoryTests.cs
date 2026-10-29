using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

public sealed class DataDirectoryTests
{
    [Fact]
    public void Constructor_CreatesDirectory_AndDerivesDbAndKeyPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));

        var dataDirectory = new DataDirectory(root);

        Directory.Exists(dataDirectory.Path).Should().BeTrue();
        dataDirectory.DatabasePath.Should().Be(Path.Combine(dataDirectory.Path, "fravashi.db"));
        dataDirectory.KeysPath.Should().Be(Path.Combine(dataDirectory.Path, "keys"));

        Directory.Delete(dataDirectory.Path, recursive: true);
    }

    [Fact]
    public void ResolvePath_PrefersConfiguredDataDir_OverDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = "/custom/data" })
            .Build();

        DataDirectory.ResolvePath(configuration).Should().Be("/custom/data");
    }

    [Fact]
    public void ResolvePath_WithNoOverrides_FallsBackToLocalAppData()
    {
        // No env var (assumed unset in test host), no config override.
        var resolved = DataDirectory.ResolvePath(configuration: null);

        resolved.Should().EndWith("Fravashi", "the product's folder since the rename, which the launcher moves the former one into");
    }
}
