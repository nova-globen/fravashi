using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public sealed class LauncherConfigStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public LauncherConfigStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "fravashi-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "launcher.json");
    }

    [Fact]
    public void Load_ReturnsNull_WhenFileMissing()
    {
        new LauncherConfigStore(_path).Load().Should().BeNull();
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsConfig()
    {
        var store = new LauncherConfigStore(_path);
        var config = new LauncherConfig { Port = 9191, DataDir = @"C:\data\fravashi" };

        store.Save(config);

        store.Load().Should().Be(config);
    }

    [Fact]
    public void Load_ReturnsNull_OnCorruptJson()
    {
        File.WriteAllText(_path, "{ not json");

        new LauncherConfigStore(_path).Load().Should().BeNull();
    }

    [Fact]
    public void Load_OldConfigWithoutWebsiteUrl_MigratesToDefault()
    {
        // The owner's launcher.json predates websiteUrl - the field must default, not break the load.
        File.WriteAllText(_path,
            """{ "port": 9090, "dataDir": "C:\\data\\fravashi", "containerName": "fravashi", "image": "fravashi:latest" }""");

        var config = new LauncherConfigStore(_path).Load();

        config.Should().NotBeNull();
        config!.Port.Should().Be(9090);
        config.ContainerName.Should().Be("fravashi");
        config.WebsiteUrl.Should().BeNull("an unset website is the build's own, resolved at each use");
        LauncherDefaults.ResolveWebsiteUrl(config.WebsiteUrl).Should().Be(LauncherDefaults.WebsiteUrl);
    }

    [Fact]
    public void The_product_website_is_on_the_builds_service_host()
    {
        // Backlog 112. A plain build is production's.
        LauncherDefaults.WebsiteUrl.Should().Be("https://nova-globen.se/fravashi");
    }

    [Fact]
    public void Saving_a_config_never_freezes_the_default_website_into_the_file()
    {
        // Backlog 112's cause: a default written back into launcher.json outlived the build that chose it, so a later
        // build for another host kept opening the old address. An unset website stays unset on disk.
        var store = new LauncherConfigStore(_path);
        store.Save(new LauncherConfig { Port = 9090, DataDir = @"C:\data\fravashi" });

        File.ReadAllText(_path).Should().NotContain(LauncherDefaults.WebsiteUrl);
        store.Load()!.WebsiteUrl.Should().BeNull();
    }

    [Fact]
    public void Load_ConfigWithADeliberateWebsite_KeepsIt()
    {
        // The other half of the rule: only the retired DEFAULT is overridden, never a real setting.
        File.WriteAllText(_path,
            """{ "port": 9090, "dataDir": "C:\\data\\fravashi", "websiteUrl": "https://intranet.example/fravashi" }""");

        new LauncherConfigStore(_path).Load()!.WebsiteUrl.Should().Be("https://intranet.example/fravashi");
    }

    [Fact]
    public void Load_ConfigWithUnknownFields_IgnoresThem()
    {
        // A newer field written by a future launcher must not break an older reader.
        File.WriteAllText(_path,
            """{ "port": 9090, "dataDir": "C:\\d", "futureField": "ignored" }""");

        new LauncherConfigStore(_path).Load().Should().NotBeNull();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
