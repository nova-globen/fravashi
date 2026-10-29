using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.Backup;
using Fravashi.Application.Persistence;
using Fravashi.Application.Updates;
using Fravashi.Infrastructure.Backup;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Launcher.Backup;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fravashi.Launcher.Tests.Backup;

/// <summary>
/// Backlog 79, the launcher's half: <c>fravashi restore</c>, driven through <see cref="LauncherApp"/> itself
/// with a fake container runtime, over REAL archives written by the studio's own writer. A hand-built
/// archive would only prove the restore can read what this test file invented; a real one proves the two
/// halves of the product agree on a format neither can see the other's code for.
/// <para>
/// What is held here is the thing an owner actually needs: a restore either happens completely or leaves
/// the install exactly as it was, and whatever it replaced is still on disk afterwards. So a tampered
/// hash, a backup from a newer Fravashi and a path climbing out of the data directory are all refusals
/// with nothing touched, and a successful restore is asserted by opening the restored database and
/// reading the rows out of it.
/// </para>
/// </summary>
public sealed class BackupRestoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fravashi-restore-tests", Guid.NewGuid().ToString("N"));

    private readonly LauncherConfigStore _configStore;
    private readonly List<string> _log = [];
    private readonly string _dataDir;

    public BackupRestoreTests()
    {
        Directory.CreateDirectory(_root);
        _dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataDir);
        _configStore = new LauncherConfigStore(Path.Combine(_root, "launcher.json"));
        _configStore.Save(new LauncherConfig { Port = 9090, DataDir = _dataDir, ContainerName = "fravashi" });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- the format contract ---------------------------------------------------------------------

    [Fact]
    public void The_launcher_knows_the_same_schema_version_the_studio_writes()
    {
        // The launcher cannot see the schema code, so this is the only place the "refuse a backup from a
        // newer Fravashi" rule can be checked against what the studio actually stamps. If a migration is
        // added and this constant is not moved with it, every backup taken by the new build would be
        // refused by its own launcher.
        BackupManifest.KnownSchemaVersion.Should().Be(SqliteSchema.CurrentVersion);
    }

    [Fact]
    public void The_launcher_reads_a_manifest_the_studio_wrote()
    {
        var archive = WriteBackup(("k", "v"));

        var inspection = BackupArchive.Inspect(archive);

        inspection.Ok.Should().BeTrue(inspection.Problem);
        var manifest = inspection.Manifest!;
        manifest.Kind.Should().Be(BackupManifest.ExpectedKind);
        manifest.Format.Should().Be(BackupManifest.SupportedFormat);
        manifest.SchemaVersion.Should().Be(SqliteSchema.CurrentVersion);
        manifest.DatabaseEntry.Should().Be(DataDirectory.DatabaseFileName);
        manifest.ProductVersion.Should().Be("9.9.9");
        manifest.Files.Should().NotBeEmpty();
        manifest.Notes.Should().NotBeEmpty();
    }

    // ---- refusals, with nothing touched ----------------------------------------------------------

    [Fact]
    public void A_tampered_file_is_refused_and_nothing_is_moved()
    {
        var archive = WriteBackup(("k", "v"));
        SeedCurrentInstall("the-original");
        TamperInPlace(archive, DataDirectory.DatabaseFileName);

        Build().Restore(archive).Should().Be(ExitCodes.BackupRefused);

        Text().Should().Contain("recorded SHA256");
        Text().Should().Contain("Nothing has been changed.");
        File.ReadAllText(Path.Combine(_dataDir, DataDirectory.DatabaseFileName)).Should().Be("the-original");
        PreRestoreFolders().Should().BeEmpty();
    }

    [Fact]
    public void A_backup_from_a_newer_Fravashi_is_refused_before_anything_moves()
    {
        var archive = WriteBackup(("k", "v"));
        SeedCurrentInstall("the-original");
        RewriteManifest(archive, manifest =>
            manifest["schemaVersion"] = BackupManifest.KnownSchemaVersion + 1);

        Build().Restore(archive).Should().Be(ExitCodes.BackupRefused);

        // The studio migrates forward and has no way back, so unpacking this would hand the product a
        // database it can only fail on, after the working one had already been moved aside.
        Text().Should().Contain("NEWER Fravashi");
        File.ReadAllText(Path.Combine(_dataDir, DataDirectory.DatabaseFileName)).Should().Be("the-original");
        PreRestoreFolders().Should().BeEmpty();
    }

    [Fact]
    public void A_manifest_path_that_climbs_out_of_the_data_directory_is_refused()
    {
        var archive = WriteBackup(("k", "v"));
        SeedCurrentInstall("the-original");
        RewriteManifest(archive, manifest =>
            manifest["files"]!.AsArray()[0]!["path"] = "../escaped.db");

        Build().Restore(archive).Should().Be(ExitCodes.BackupRefused);

        Text().Should().Contain("does not stay inside the data directory");
        File.Exists(Path.Combine(_root, "escaped.db")).Should().BeFalse();
        PreRestoreFolders().Should().BeEmpty();
    }

    [Fact]
    public void An_entry_the_manifest_does_not_account_for_is_refused()
    {
        var archive = WriteBackup(("k", "v"));
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var smuggled = zip.CreateEntry("keys/smuggled.xml");
            using var stream = smuggled.Open();
            stream.Write(Encoding.UTF8.GetBytes("not in the manifest"));
        }

        Build().Restore(archive).Should().Be(ExitCodes.BackupRefused);

        // An unlisted entry would be unpacked unverified, which is the hole hashing every listed file is
        // there to close.
        Text().Should().Contain("not in the manifest");
    }

    [Fact]
    public void A_file_that_is_not_a_backup_is_refused_by_name()
    {
        var notABackup = Path.Combine(_root, "holiday-photos.zip");
        using (var zip = ZipFile.Open(notABackup, ZipArchiveMode.Create))
        {
            zip.CreateEntry("photo.jpg");
        }

        Build().Restore(notABackup).Should().Be(ExitCodes.BackupRefused);

        Text().Should().Contain("not a Fravashi backup");
    }

    [Fact]
    public void Declining_the_confirmation_changes_nothing()
    {
        var archive = WriteBackup(("k", "v"));
        SeedCurrentInstall("the-original");

        Build(confirm: false).Restore(archive).Should().Be(ExitCodes.Success);

        Text().Should().Contain("Restore cancelled");
        File.ReadAllText(Path.Combine(_dataDir, DataDirectory.DatabaseFileName)).Should().Be("the-original");
        PreRestoreFolders().Should().BeEmpty();
    }

    // ---- a machine that has never run Fravashi ----------------------------------------------------
    //
    // Found on a real clean machine on 2026-09-23 (backlog 79), and invisible to every test above: the
    // constructor writes a launcher.json, so the "moved to another machine" case these tests model had a
    // configuration a real new machine never has. On the real one, `fravashi restore` refused with "Fravashi has
    // never been started on this machine" and sent the owner through a full first launch, which creates an
    // empty install only for the restore to move it aside. The rest of Restore was already written for this
    // case (the swap has a branch worded for "nothing to move aside"); one guard at the top made it
    // unreachable.

    [Fact]
    public void A_machine_that_never_ran_Fravashi_is_restored_onto_without_a_first_launch()
    {
        var archive = WriteBackup(("project", "the-work"));
        var (app, runtime, configPath) = NeverRanMachine(confirm: true);

        app.Restore(archive).Should().Be(ExitCodes.Success);

        ReadRestored("project").Should().Be("the-work");
        runtime.RunSpecs.Should().NotBeEmpty("the product is started on the restored database");
        File.Exists(configPath).Should().BeTrue("the restore sets the machine up the way a first launch would");
        Text().Should().NotContain("never been started");
        Text().Should().Contain("nothing was kept aside", "there was nothing of the owner's here to move");
    }

    /// <summary>
    /// The reason the configuration is created AFTER the archive is checked and the owner has said yes, rather
    /// than first: a refused backup says "Nothing has been changed", and on a machine that had nothing, writing
    /// a launcher.json and reserving a port before refusing would make that sentence false.
    /// </summary>
    [Fact]
    public void A_bad_backup_on_a_machine_that_never_ran_Fravashi_writes_nothing_at_all()
    {
        var notABackup = Path.Combine(_root, "not-a-backup.zip");
        File.WriteAllText(notABackup, "this is not a zip");
        var (app, runtime, configPath) = NeverRanMachine(confirm: true);

        app.Restore(notABackup).Should().Be(ExitCodes.BackupRefused);

        File.Exists(configPath).Should().BeFalse("a refused restore claims it changed nothing");
        Directory.Exists(_dataDir).Should().BeFalse();
        runtime.RunSpecs.Should().BeEmpty();
        Text().Should().Contain("Nothing has been changed");
    }

    [Fact]
    public void Declining_on_a_machine_that_never_ran_Fravashi_writes_nothing_at_all()
    {
        var archive = WriteBackup(("k", "v"));
        var (app, _, configPath) = NeverRanMachine(confirm: false);

        app.Restore(archive).Should().Be(ExitCodes.Success);

        Text().Should().Contain("Restore cancelled");
        File.Exists(configPath).Should().BeFalse("saying no must leave the machine as it was");
        Directory.Exists(_dataDir).Should().BeFalse();
    }

    /// <summary>
    /// A machine as the installer leaves it: the image is loaded, and there is no launcher.json and no data
    /// directory, because nothing has ever been started.
    /// </summary>
    private (LauncherApp App, FakeContainerRuntime Runtime, string ConfigPath) NeverRanMachine(bool confirm)
    {
        Directory.Delete(_dataDir, recursive: true);
        var configPath = Path.Combine(_root, "never-ran", "launcher.json");
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true };
        // Built like Build() above in every respect except the missing configuration. The first draft of this
        // left out studioHealth and openBrowser, so a restore that SUCCEEDED then polled a real address for a
        // studio that does not exist, and a one-second class took minutes.
        var app = new LauncherApp(
            runtime,
            new LauncherConfigStore(configPath),
            launcherDirectory: _root,
            log: _log.Add,
            openBrowser: _ => { },
            isPortFree: _ => true,
            tray: new FakeTrayControl(),
            confirm: _ => confirm,
            defaultDataDir: () => _dataDir,
            studioHealth: _ => new StudioHealth(true, SqliteSchema.CurrentVersion));
        return (app, runtime, configPath);
    }

    [Fact]
    public void The_command_line_dispatches_fravashi_restore_to_the_restore_flow()
    {
        // Without this, a missing case in the dispatch switch would answer "unknown command" and no unit
        // test of Restore itself would notice.
        var archive = WriteBackup(("k", "v"));
        var output = new List<string>();

        var exit = DesktopCommandLine.Execute(
            DesktopCommandLine.Parse(["restore", archive]), Build(), output.Add);

        exit.Should().Be(ExitCodes.Success);
        output.Should().NotContain(line => line.Contains("unknown command", StringComparison.Ordinal));
        Text().Should().Contain("back up on the restored database");
    }

    // ---- the restore itself ----------------------------------------------------------------------

    [Fact]
    public void Restoring_into_a_fresh_install_brings_the_rows_back_and_reports_the_schema_version()
    {
        var archive = WriteBackup(("project", "the-work"), ("second", "row"));
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true };

        // A fresh data directory is the "moved to another machine" case: nothing to move aside, and the
        // only proof that matters is that the product comes back up on the restored database.
        Build(runtime).Restore(archive).Should().Be(ExitCodes.Success);

        ReadRestored("project").Should().Be("the-work");
        ReadRestored("second").Should().Be("row");
        File.ReadAllText(Path.Combine(_dataDir, DataDirectory.KeysDirectoryName, "key-1.xml"))
            .Should().Be("key-ring-material");
        Text().Should().Contain($"reports schema version {SqliteSchema.CurrentVersion}");
        runtime.RunSpecs.Should().NotBeEmpty("the product is started again");

        // And it does not claim to have kept a previous state: there was none, so there is no folder, and
        // a message naming one would send the owner looking for their data in an empty directory.
        PreRestoreFolders().Should().BeEmpty();
        Text().Should().NotContain(BackupRestore.PreRestorePrefix);
        Text().Should().Contain("nothing was kept aside");
    }

    [Fact]
    public void The_state_a_restore_replaced_is_kept_and_never_deleted()
    {
        var archive = WriteBackup(("project", "the-backed-up-work"));
        SeedCurrentInstall("the-state-being-replaced");

        Build().Restore(archive).Should().Be(ExitCodes.Success);

        var kept = PreRestoreFolders().Should().ContainSingle().Subject;
        File.ReadAllText(Path.Combine(kept, DataDirectory.DatabaseFileName))
            .Should().Be("the-state-being-replaced");
        File.ReadAllText(Path.Combine(kept, DataDirectory.DatabaseFileName + "-wal")).Should().Be("stale-wal");
        File.ReadAllText(Path.Combine(kept, DataDirectory.KeysDirectoryName, "old-key.xml")).Should().Be("old");
        Directory.Exists(Path.Combine(kept, BackupRestore.SessionsFolderName)).Should().BeTrue();

        // And the restored database really is the archive's, not the one that was moved aside.
        ReadRestored("project").Should().Be("the-backed-up-work");
        Text().Should().Contain(kept);
    }

    [Fact]
    public void A_stale_write_ahead_log_is_never_left_beside_the_restored_database()
    {
        // The nastiest failure available here: leave the previous -wal in place and SQLite reads it as
        // belonging to the restored file. It is moved aside with the database it belonged to.
        var archive = WriteBackup(("project", "the-backed-up-work"));
        SeedCurrentInstall("the-state-being-replaced");

        Build().Restore(archive).Should().Be(ExitCodes.Success);

        File.Exists(Path.Combine(_dataDir, DataDirectory.DatabaseFileName + "-wal")).Should().BeFalse();
        File.Exists(Path.Combine(_dataDir, DataDirectory.DatabaseFileName + "-shm")).Should().BeFalse();
    }

    [Fact]
    public void A_backup_living_in_this_install_own_backups_folder_restores_over_the_install_it_came_from()
    {
        // The ordinary case, and the one where a careless implementation eats itself: the archive sits in
        // <data dir>/backups, which is INSIDE the directory being replaced. It must survive the swap and
        // still be readable while it is being unpacked.
        var archive = WriteBackupFrom(_dataDir, ("project", "before-the-mistake"));
        WriteRow(_dataDir, "project", "after-the-mistake");

        Build().Restore(archive).Should().Be(ExitCodes.Success);

        ReadRestored("project").Should().Be("before-the-mistake");
        File.Exists(archive).Should().BeTrue("a restore never consumes the backup it restored from");
        // And the state it replaced is still openable, not merely still present as bytes.
        ReadRow(
            Path.Combine(PreRestoreFolders().Single(), DataDirectory.DatabaseFileName),
            "project").Should().Be("after-the-mistake");
    }

    [Fact]
    public void The_container_is_stopped_before_the_database_is_replaced()
    {
        var archive = WriteBackup(("k", "v"));
        var runtime = new FakeContainerRuntime
        {
            Available = true,
            HasImage = true,
            ContainerPresent = true,
            ContainerIsRunning = true,
        };

        Build(runtime).Restore(archive).Should().Be(ExitCodes.Success);

        runtime.Stopped.Should().Contain("fravashi");
    }

    [Fact]
    public void A_studio_that_does_not_come_back_healthy_is_reported_as_unverified()
    {
        var archive = WriteBackup(("k", "v"));
        SeedCurrentInstall("the-state-being-replaced");

        var app = Build(health: _ => null);
        app.Restore(archive).Should().Be(ExitCodes.RestoreIncomplete);

        // The files are in place, so the honest report is "not verified", and it still has to say where
        // the previous state is.
        Text().Should().Contain("NOT verified");
        Text().Should().Contain(BackupRestore.PreRestorePrefix);
    }

    [Fact]
    public void An_older_studio_that_cannot_report_its_schema_version_still_restores()
    {
        var archive = WriteBackup(("k", "v"));

        Build(health: _ => new StudioHealth(true, null)).Restore(archive).Should().Be(ExitCodes.Success);

        Text().Should().Contain("does not report its schema version");
    }

    // ---- the swap that stopped half way ----------------------------------------------------------
    //
    // The only state where the install is neither what it was nor what was asked for, and the only one
    // where the report is all the owner has. Every case below is provoked with an ordinary filesystem
    // obstruction (a file sitting where a folder has to be created) rather than a seam, so what is
    // exercised is the real File/Directory calls on whichever platform runs the suite.

    [Fact]
    public void A_swap_that_cannot_move_the_current_state_aside_leaves_the_install_exactly_as_it_was()
    {
        var archive = WriteBackup(("project", "the-backed-up-work"));
        SeedCurrentInstall("the-original");
        // The backups folder cannot be created because a FILE of that name is in the way, so the very
        // first move fails and nothing of the owner's has been touched.
        File.WriteAllText(Path.Combine(_dataDir, BackupRestore.BackupsFolderName), "not a folder");

        Build().Restore(archive).Should().Be(ExitCodes.RestoreIncomplete);

        Text().Should().Contain("could not be moved aside");
        Text().Should().Contain("exactly as it was");
        Text().Should().NotContain("YOUR PREVIOUS DATA IS NOT LOST");
        File.ReadAllText(Path.Combine(_dataDir, DataDirectory.DatabaseFileName)).Should().Be("the-original");
        File.ReadAllText(Path.Combine(_dataDir, DataDirectory.KeysDirectoryName, "old-key.xml"))
            .Should().Be("old");
    }

    [Fact]
    public void An_unpack_that_stops_after_the_swap_names_the_folder_the_previous_state_is_in()
    {
        var archive = WriteBackup(("project", "the-backed-up-work"));
        SeedCurrentInstall("the-state-being-replaced");
        BlockTheKeysFolder();

        Build().Restore(archive).Should().Be(ExitCodes.RestoreIncomplete);

        Text().Should().Contain("could not be unpacked");
        Text().Should().Contain("YOUR PREVIOUS DATA IS NOT LOST");
        // The sentence has to be TRUE, so the named folder really holds the database that was moved.
        var kept = PreRestoreFolders().Should().ContainSingle().Subject;
        Text().Should().Contain(kept);
        File.ReadAllText(Path.Combine(kept, DataDirectory.DatabaseFileName))
            .Should().Be("the-state-being-replaced");
        // And it admits the half of the archive that did land, because moving the old files back means
        // writing over those.
        Text().Should().Contain("already been written");
    }

    [Fact]
    public void An_unpack_that_stops_with_nothing_moved_aside_does_not_point_at_an_empty_folder()
    {
        // The clean-machine restore, which is the export case: there was nothing to move, so the
        // "your data is in the pre-restore folder" sentence would name a folder holding nothing at all.
        var archive = WriteBackup(("project", "the-backed-up-work"));
        BlockTheKeysFolder();

        Build().Restore(archive).Should().Be(ExitCodes.RestoreIncomplete);

        Text().Should().Contain("could not be unpacked");
        Text().Should().NotContain("YOUR PREVIOUS DATA IS NOT LOST");
        Text().Should().NotContain(BackupRestore.PreRestorePrefix);
        PreRestoreFolders().Should().BeEmpty("nothing was moved, so no pre-restore folder was made");
        // What it says instead is the truth: nothing of the owner's was replaced, part of the backup is
        // on disk, and the same command can simply be run again.
        Text().Should().Contain("PART RESTORED");
        Text().Should().Contain("run the same `fravashi restore` again");
    }

    [Fact]
    public void A_missing_or_unnamed_archive_is_answered_rather_than_thrown()
    {
        Build().Restore(Path.Combine(_root, "nothing-here.zip")).Should().Be(ExitCodes.BackupRefused);
        Build().Restore(null).Should().Be(ExitCodes.UsageError);
        Text().Should().Contain("fravashi restore <backup.zip>");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private LauncherApp Build(
        FakeContainerRuntime? runtime = null,
        bool confirm = true,
        Func<string, StudioHealth?>? health = null)
        => new(
            runtime ?? new FakeContainerRuntime { Available = true, HasImage = true },
            _configStore,
            launcherDirectory: _root,
            log: _log.Add,
            openBrowser: _ => { },
            isPortFree: _ => true,
            tray: new FakeTrayControl(),
            confirm: _ => confirm,
            defaultDataDir: () => _dataDir,
            studioHealth: health ?? (_ => new StudioHealth(true, SqliteSchema.CurrentVersion)));

    /// <summary>
    /// Writes a REAL backup with the studio's own writer, from a separate data directory standing in for
    /// the machine the backup was taken on.
    /// </summary>
    private string WriteBackup(params (string Key, string Value)[] rows)
        => WriteBackupFrom(Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N")[..8]), rows);

    private static string WriteBackupFrom(string source, params (string Key, string Value)[] rows)
    {
        Directory.CreateDirectory(source);
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(
            Path.Combine(source, DataDirectory.DatabaseFileName), Pooling: false));
        SqliteSchema.EnsureCreated(factory);

        using (var connection = factory.Open())
        {
            foreach (var (key, value) in rows)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) " +
                    "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                command.Parameters.AddWithValue("$k", key);
                command.Parameters.AddWithValue("$v", value);
                command.ExecuteNonQuery();
            }
        }

        Directory.CreateDirectory(Path.Combine(source, DataDirectory.KeysDirectoryName));
        File.WriteAllText(Path.Combine(source, DataDirectory.KeysDirectoryName, "key-1.xml"), "key-ring-material");
        Directory.CreateDirectory(Path.Combine(source, BackupRestore.SessionsFolderName, "s1"));
        File.WriteAllText(
            Path.Combine(source, BackupRestore.SessionsFolderName, "s1", "settings.json"), "{}");

        IStudioBackupWriter writer = new StudioBackupWriter(
            new DataDirectory(source),
            factory,
            new FixedProductInfo(),
            new NoProviders());
        return writer.Write().Path;
    }

    /// <summary>Puts recognisable current state in the data directory, so the swap can be seen.</summary>
    private void SeedCurrentInstall(string databaseContent)
    {
        File.WriteAllText(Path.Combine(_dataDir, DataDirectory.DatabaseFileName), databaseContent);
        File.WriteAllText(Path.Combine(_dataDir, DataDirectory.DatabaseFileName + "-wal"), "stale-wal");
        File.WriteAllText(Path.Combine(_dataDir, DataDirectory.DatabaseFileName + "-shm"), "stale-shm");
        Directory.CreateDirectory(Path.Combine(_dataDir, DataDirectory.KeysDirectoryName));
        File.WriteAllText(Path.Combine(_dataDir, DataDirectory.KeysDirectoryName, "old-key.xml"), "old");
        Directory.CreateDirectory(Path.Combine(_dataDir, BackupRestore.SessionsFolderName, "old-session"));
        File.WriteAllText(
            Path.Combine(_dataDir, BackupRestore.SessionsFolderName, "old-session", "settings.json"), "{}");
    }

    /// <summary>
    /// Puts a FILE where the key-ring folder has to be created, so the unpack fails on the archive's
    /// second entry, after the database has been written. A file of that name is not a directory, so the
    /// swap does not move it aside first, and creating a directory over it fails on every platform.
    /// </summary>
    private void BlockTheKeysFolder()
    {
        var keys = Path.Combine(_dataDir, DataDirectory.KeysDirectoryName);
        if (Directory.Exists(keys))
        {
            Directory.Delete(keys, recursive: true);
        }

        File.WriteAllText(keys, "not a folder");
    }

    private string? ReadRestored(string key)
        => ReadRow(Path.Combine(_dataDir, DataDirectory.DatabaseFileName), key);

    private static string? ReadRow(string databasePath, string key)
    {
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(databasePath, Pooling: false));
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteRow(string dataDir, string key, string value)
    {
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(
            Path.Combine(dataDir, DataDirectory.DatabaseFileName), Pooling: false));
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    private IEnumerable<string> PreRestoreFolders()
    {
        var backups = Path.Combine(_dataDir, BackupRestore.BackupsFolderName);
        return Directory.Exists(backups)
            ? Directory.EnumerateDirectories(backups, BackupRestore.PreRestorePrefix + "*")
            : [];
    }

    private string Text() => string.Join('\n', _log);

    /// <summary>Replaces an entry's bytes with the SAME number of different ones: only the hash can fire.</summary>
    private static void TamperInPlace(string archivePath, string entryName)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = zip.GetEntry(entryName)!;
        var length = (int)entry.Length;
        byte[] replacement;
        using (var read = entry.Open())
        {
            replacement = new byte[length];
            read.ReadExactly(replacement);
        }

        replacement[^1] ^= 0xFF;
        using var write = entry.Open();
        write.SetLength(0);
        write.Write(replacement);
    }

    private static void RewriteManifest(string archivePath, Action<JsonObject> mutate)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = zip.GetEntry(BackupArchive.ManifestEntryName)!;
        JsonObject manifest;
        using (var read = entry.Open())
        {
            manifest = JsonNode.Parse(read)!.AsObject();
        }

        mutate(manifest);
        var bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions()));
        using var write = entry.Open();
        write.SetLength(0);
        write.Write(bytes);
    }

    private sealed class FixedProductInfo : IProductInfo
    {
        public string Version => "9.9.9";

        public string Variant => "full";
    }

    private sealed class NoProviders : IProviderConnectionStore
    {
        public IReadOnlyList<ProviderConnection> GetAll() => [];

        public IReadOnlyList<ProviderConnection> GetByKind(ProviderKind kind) => [];

        public ProviderConnection? Get(string id) => null;

        public void Upsert(ProviderConnection connection)
        {
        }

        public void Remove(string id)
        {
        }
    }
}
