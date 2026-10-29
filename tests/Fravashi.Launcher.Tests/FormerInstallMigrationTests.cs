using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// The bridge release's start-time migration of the names an install from before the internal rename carries
/// (<c>docs/plans/rename-internal-names.md</c>, phase 2): launcher.json's container, image and sidecar names, the former
/// container with its sidecar and network, and the database file. Each step is idempotent, verifies its result, keeps the
/// former state usable when it fails and is tried again at the next start. Every test runs against a temp folder and a
/// fake engine; the old names are read through <see cref="FormerInstall"/>, never written out here.
/// </summary>
public sealed class FormerInstallMigrationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fravashi-former-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly NamedEngine _engine = new();

    public FormerInstallMigrationTests() => Directory.CreateDirectory(Data);

    private string Data => Path.Combine(_root, "data");

    private LauncherConfigStore Store => new(Path.Combine(_root, "launcher.json"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>An install as v0.9.102 leaves it: launcher.json with the former names, the former container running, a WAL database.</summary>
    private void FormerInstallOnDisk(bool withVpn = false, int rows = 4)
    {
        Store.Save(new LauncherConfig
        {
            Port = 9090,
            DataDir = Data,
            ContainerName = FormerInstall.ContainerName,
            Image = FormerInstall.Image,
            PinnedImageDigest = "sha256:former",
            Vpn = withVpn ? new VpnConfig { Enabled = true, ContainerName = FormerInstall.VpnContainerName, PinnedImageDigest = "sha256:vpn" } : null,
        });
        _engine.Running.Add(FormerInstall.ContainerName);
        _engine.Containers.Add(FormerInstall.ContainerName);
        if (withVpn)
        {
            _engine.Containers.Add(FormerInstall.VpnContainerName);
            _engine.Running.Add(FormerInstall.VpnContainerName);
        }

        _engine.Networks.Add(FormerInstall.StudioNetwork);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Data, FormerInstall.DatabaseFileName)};Pooling=False");
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "CREATE TABLE items (id INTEGER PRIMARY KEY, title TEXT NOT NULL);");
        for (var i = 1; i <= rows; i++)
        {
            Execute(connection, $"INSERT INTO items (title) VALUES ('item {i}');");
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Rows(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items;";
        return (long)command.ExecuteScalar()!;
    }

    private FormerInstallMigration Migration(Action<FormerInstallStep>? failAt = null) =>
        new(Store, _ => _engine, _log.Add, () => Now, failAt);

    private static Action<FormerInstallStep> FailAt(FormerInstallStep failing) => step =>
    {
        if (step == failing)
        {
            throw new IOException($"simulated failure at {failing}");
        }
    };

    private string CurrentDb => Path.Combine(Data, ProductHome.DatabaseFileName);

    private string FormerDb => Path.Combine(Data, FormerInstall.DatabaseFileName);

    [Fact]
    public void A_former_install_gets_every_new_name_verified_and_its_data_kept()
    {
        FormerInstallOnDisk(withVpn: true);
        SqliteConnection.ClearAllPools();

        var result = Migration().Run();

        result.Complete.Should().BeTrue(string.Join("; ", result.Failures));
        var config = Store.Load()!;
        config.ContainerName.Should().Be(LauncherDefaults.ContainerName);
        config.Image.Should().Be(LauncherDefaults.Image);
        config.PinnedImageDigest.Should().BeNull("the pin named the former image; the new one is pinned at its first start");
        config.Vpn!.ContainerName.Should().Be(LauncherDefaults.VpnContainerName);
        config.Port.Should().Be(9090, "nothing but the names changes");

        _engine.Containers.Should().NotContain([FormerInstall.ContainerName, FormerInstall.VpnContainerName]);
        _engine.RemovedOrder.Should().Equal(FormerInstall.ContainerName, FormerInstall.VpnContainerName);
        _engine.Networks.Should().NotContain(FormerInstall.StudioNetwork);
        _engine.RemovedVolumes.Should().BeEmpty("the former workspaces volume is kept until the cleanup release");

        File.Exists(FormerDb).Should().BeFalse();
        Rows(CurrentDb).Should().Be(4);
        var backup = Path.Combine(Data, "backups", "pre-rename-20261027-120000", FormerInstall.DatabaseFileName);
        Rows(backup).Should().Be(4, "the database is backed up before it is renamed");
        _log.Should().Contain(l => l.Contains(FormerInstall.WorkspacesVolume) && l.Contains("kept"));
    }

    [Fact]
    public void A_second_start_does_nothing()
    {
        FormerInstallOnDisk();
        Migration().Run();
        _log.Clear();
        _engine.RemovedOrder.Clear();
        var json = File.ReadAllText(Path.Combine(_root, "launcher.json"));

        var again = Migration().Run();

        again.Complete.Should().BeTrue();
        _log.Should().BeEmpty();
        _engine.RemovedOrder.Should().BeEmpty();
        File.ReadAllText(Path.Combine(_root, "launcher.json")).Should().Be(json);
        Rows(CurrentDb).Should().Be(4);
    }

    [Fact]
    public void A_fresh_install_has_nothing_to_migrate()
    {
        var result = Migration().Run();

        result.Complete.Should().BeTrue();
        File.Exists(Path.Combine(_root, "launcher.json")).Should().BeFalse("nothing is written for an install that does not exist");
        _engine.RemovedOrder.Should().BeEmpty();
    }

    [Fact]
    public void An_install_already_on_the_new_names_is_left_alone()
    {
        Store.Save(new LauncherConfig { Port = 9090, DataDir = Data, PinnedImageDigest = "sha256:current" });
        _engine.Containers.Add(LauncherDefaults.ContainerName);
        _engine.Running.Add(LauncherDefaults.ContainerName);

        Migration().Run().Complete.Should().BeTrue();

        Store.Load()!.PinnedImageDigest.Should().Be("sha256:current");
        _engine.Running.Should().Contain(LauncherDefaults.ContainerName, "this install's own container is never stopped for nothing");
    }

    [Fact]
    public void An_engine_that_does_not_answer_keeps_the_former_container_and_database_and_is_tried_again()
    {
        FormerInstallOnDisk();
        SqliteConnection.ClearAllPools();
        _engine.Available = false;

        var first = Migration().Run();

        first.Complete.Should().BeFalse();
        first.Failures.Should().Contain(f => f.Contains("not answering"));
        File.Exists(FormerDb).Should().BeTrue("a database no container can be shown not to hold is never renamed");
        File.Exists(CurrentDb).Should().BeFalse();
        Store.Load()!.ContainerName.Should().Be(LauncherDefaults.ContainerName, "the names in launcher.json need no engine");

        _engine.Available = true;
        SqliteConnection.ClearAllPools();
        Migration().Run().Complete.Should().BeTrue();
        _engine.Containers.Should().NotContain(FormerInstall.ContainerName);
        Rows(CurrentDb).Should().Be(4);
    }

    [Theory]
    [InlineData(FormerInstallStep.Names)]
    [InlineData(FormerInstallStep.Containers)]
    [InlineData(FormerInstallStep.Database)]
    public void A_failure_at_any_step_keeps_the_former_state_usable_and_the_next_start_finishes(FormerInstallStep failing)
    {
        FormerInstallOnDisk();
        SqliteConnection.ClearAllPools();

        var first = Migration(FailAt(failing)).Run();

        first.Complete.Should().BeFalse();
        first.Failures.Should().ContainSingle(f => f.Contains($"simulated failure at {failing}"));
        _log.Should().Contain(l => l.Contains("tried again at the next start"));
        // Whatever stopped, the database is whole under exactly one of its names.
        (File.Exists(FormerDb) ^ File.Exists(CurrentDb)).Should().BeTrue();
        Rows(File.Exists(CurrentDb) ? CurrentDb : FormerDb).Should().Be(4);

        SqliteConnection.ClearAllPools();
        var second = Migration().Run();

        second.Complete.Should().BeTrue(string.Join("; ", second.Failures));
        Store.Load()!.ContainerName.Should().Be(LauncherDefaults.ContainerName);
        _engine.Containers.Should().NotContain(FormerInstall.ContainerName);
        Rows(CurrentDb).Should().Be(4);
    }

    [Fact]
    public void A_former_container_that_will_not_go_keeps_the_database_name()
    {
        FormerInstallOnDisk();
        _engine.RefusesRemoval = true;

        var result = Migration().Run();

        result.Complete.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("still there"));
        File.Exists(FormerDb).Should().BeTrue();
    }

    [Fact]
    public void A_studio_already_running_on_the_former_database_is_stopped_before_the_rename()
    {
        // An earlier start of this release could not rename (the engine was down), so the new studio opened the database
        // under its former name; this start stops it first, and the start that follows brings it back.
        FormerInstallOnDisk();
        _engine.Containers.Remove(FormerInstall.ContainerName);
        _engine.Running.Remove(FormerInstall.ContainerName);
        Store.Save(Store.Load()! with { ContainerName = LauncherDefaults.ContainerName, Image = LauncherDefaults.Image });
        _engine.Containers.Add(LauncherDefaults.ContainerName);
        _engine.Running.Add(LauncherDefaults.ContainerName);
        SqliteConnection.ClearAllPools();

        Migration().Run().Complete.Should().BeTrue();

        _engine.Stopped.Should().Contain(LauncherDefaults.ContainerName);
        Rows(CurrentDb).Should().Be(4);
    }

    [Fact]
    public void Both_database_names_are_never_merged_the_current_one_is_used()
    {
        FormerInstallOnDisk();
        File.WriteAllText(CurrentDb, string.Empty);
        var formerBytes = File.ReadAllBytes(FormerDb);

        Migration().Run().Complete.Should().BeTrue();

        File.ReadAllBytes(FormerDb).Should().Equal(formerBytes, "the former file is left exactly as it is");
        _log.Should().Contain(l => l.Contains("leaves the former file as it is"));
    }

    [Fact]
    public void A_custom_container_name_is_this_install_s_own_and_is_kept()
    {
        Store.Save(new LauncherConfig { Port = 9090, DataDir = Data, ContainerName = "my-studio", Image = "my-image:1" });

        Migration().Run().Complete.Should().BeTrue();

        Store.Load()!.ContainerName.Should().Be("my-studio");
        Store.Load()!.Image.Should().Be("my-image:1");
    }

    [Fact]
    public void The_former_image_tag_goes_only_once_nothing_uses_it()
    {
        _engine.Images.Add(FormerInstall.Image);
        _engine.Containers.Add(FormerInstall.ContainerName);

        FormerInstallMigration.RetireFormerImage(_engine, _log.Add).Should().BeFalse();
        _engine.Images.Should().Contain(FormerInstall.Image);

        _engine.Containers.Remove(FormerInstall.ContainerName);
        FormerInstallMigration.RetireFormerImage(_engine, _log.Add).Should().BeTrue();
        _engine.Images.Should().NotContain(FormerInstall.Image);
        _engine.RemovedImages.Should().Equal(FormerInstall.Image);

        FormerInstallMigration.RetireFormerImage(_engine, _log.Add).Should().BeTrue("nothing left to remove is done");
        _engine.RemovedImages.Should().ContainSingle();
    }

    /// <summary>An engine that knows containers, networks and images by name, which the shared fake does not.</summary>
    private sealed class NamedEngine : IContainerRuntime
    {
        public bool Available { get; set; } = true;
        public bool RefusesRemoval { get; set; }
        public HashSet<string> Containers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Running { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Networks { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Images { get; } = new(StringComparer.Ordinal);
        public List<string> RemovedOrder { get; } = [];
        public List<string> Stopped { get; } = [];
        public List<string> RemovedVolumes { get; } = [];
        public List<string> RemovedImages { get; } = [];

        public string Name => "fake";
        public bool IsAvailable() => Available;
        public bool ImageExists(string image) => Images.Contains(image);
        public IReadOnlyList<string> LoadImage(string tarPath) => [];
        public bool ContainerExists(string containerName) => Available && Containers.Contains(containerName);
        public bool ContainerRunning(string containerName) => Available && Running.Contains(containerName);
        public void StartContainer(string containerName) => Running.Add(containerName);

        public void StopContainer(string containerName)
        {
            Stopped.Add(containerName);
            Running.Remove(containerName);
        }

        public void RemoveContainer(string containerName)
        {
            if (RefusesRemoval)
            {
                return;
            }

            RemovedOrder.Add(containerName);
            Containers.Remove(containerName);
            Running.Remove(containerName);
        }

        public bool RemoveNetwork(string networkName) => Networks.Remove(networkName);

        public void RemoveImage(string imageRef)
        {
            RemovedImages.Add(imageRef);
            Images.Remove(imageRef);
        }

        public void EnsureVolume(string volumeName)
        {
        }

        public bool RemoveVolume(string volumeName)
        {
            RemovedVolumes.Add(volumeName);
            return true;
        }

        public void RunContainer(ContainerRunSpec spec) => throw new NotSupportedException();
        public string? ImageVersion(string image) => null;
        public string? ResolveImageDigest(string image) => Images.Contains(image) ? "sha256:" + image.Length : null;
        public string GetLogs(string containerName, int tail) => string.Empty;
        public string? ImageReleaseVersion(string image) => null;
    }
}
