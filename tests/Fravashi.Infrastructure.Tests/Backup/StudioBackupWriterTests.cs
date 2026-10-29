using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Persistence;
using Fravashi.Application.Updates;
using Fravashi.Infrastructure.Backup;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Backup;

/// <summary>
/// Backlog 79, the studio's half. The property that matters is not "a zip appeared": it is that the
/// database inside it is a CONSISTENT copy of a database the studio is holding open with WAL active, so
/// the owner can take a backup at any moment instead of being told to stop the product first.
/// <para>
/// So the populated database here is held open by a live connection while the backup is taken, and a row
/// is committed into the write-ahead log and deliberately NOT checkpointed. A file copy would have missed
/// it; the online backup API copies committed content, and the assertion is that the row is in the zip's
/// database.
/// </para>
/// <para>
/// The other two assertions are what a restore depends on: every manifest hash describes the bytes that
/// were actually stored, and the Data Protection key ring comes across byte for byte. Without the key ring
/// the restored database's credentials are unreadable, which would make this an export in name only.
/// </para>
/// </summary>
public class StudioBackupWriterTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "fravashi-backup-tests", Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;

    public StudioBackupWriterTests()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = new SqliteConnectionFactory(new SqliteStoreOptions(
            Path.Combine(_dataRoot, DataDirectory.DatabaseFileName), Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private StudioBackupWriter Build(IProviderConnectionStore? providers = null) =>
        new(new DataDirectory(_dataRoot),
            _factory,
            new FixedProductInfo("9.9.9", "full"),
            providers ?? new EmptyProviderStore(),
            () => new DateTimeOffset(2026, 9, 13, 7, 5, 9, TimeSpan.Zero));

    private void Insert(string key, string value)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    private string Extract(string archivePath, string entryName)
    {
        var destination = Path.Combine(_dataRoot, "extracted", entryName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var archive = ZipFile.OpenRead(archivePath);
        archive.GetEntry(entryName)!.ExtractToFile(destination, overwrite: true);
        return destination;
    }

    [Fact]
    public void The_files_work_items_carry_ride_in_the_backup()
    {
        // Work item #359: attachments live in the data directory beside the database, so the backup carries them.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var attachments = new Fravashi.Infrastructure.WorkItems.FileWorkItemAttachments(_factory, new DataDirectory(_dataRoot));
        var stored = attachments.AddImage(new Fravashi.Domain.WorkItems.WorkItemId("item1"), "shot.png", png, "user").Attachment!;

        var backup = Build().Write();

        using var zip = ZipFile.OpenRead(backup.Path);
        var entry = $"work-item-attachments/item1/{stored.Id}.png";
        zip.Entries.Should().Contain(e => e.FullName == entry);
        using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        using var manifest = JsonDocument.Parse(reader.ReadToEnd());
        manifest.RootElement.GetProperty("files").EnumerateArray()
            .Should().Contain(f => f.GetProperty("path").GetString() == entry, "the launcher verifies and restores what the manifest lists");
    }

    [Fact]
    public void A_backup_taken_while_the_database_is_held_open_carries_its_rows_and_schema_version()
    {
        Insert("committed-before", "value-before");

        // The studio's own situation: a connection is open on the database as the backup runs, and the
        // newest row is still in the write-ahead log because nothing has checkpointed it.
        using var held = _factory.Open();
        Insert("committed-into-wal", "value-in-wal");

        var backup = Build().Write();

        File.Exists(backup.Path).Should().BeTrue();
        backup.SchemaVersion.Should().Be(SqliteSchema.CurrentVersion);

        var copy = Extract(backup.Path, DataDirectory.DatabaseFileName);
        var restored = new SqliteConnectionFactory(new SqliteStoreOptions(copy, Pooling: false));
        using var connection = restored.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
        Read(connection, "committed-before").Should().Be("value-before");
        Read(connection, "committed-into-wal").Should().Be("value-in-wal");
    }

    [Fact]
    public void Every_file_matches_the_sha256_the_manifest_recorded_for_it()
    {
        WriteKeyRing("key-ring-bytes");
        WriteSession("s1/settings.json", "{\"hooks\":{}}");

        var backup = Build().Write();

        using var archive = ZipFile.OpenRead(backup.Path);
        var manifest = ReadManifest(archive);
        manifest.GetProperty("files").GetArrayLength().Should().Be(backup.FileCount);

        foreach (var file in manifest.GetProperty("files").EnumerateArray())
        {
            var name = file.GetProperty("path").GetString()!;
            var entry = archive.GetEntry(name);
            entry.Should().NotBeNull($"the manifest lists {name}");
            entry!.Length.Should().Be(file.GetProperty("sizeBytes").GetInt64());

            using var stream = entry.Open();
            using var hasher = SHA256.Create();
            Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant()
                .Should().Be(file.GetProperty("sha256").GetString());
        }
    }

    [Fact]
    public void The_key_ring_comes_across_byte_for_byte()
    {
        // Not a hash comparison on purpose: the key ring is what makes a restored credential readable at
        // all, so the assertion is on the bytes themselves.
        var original = new byte[] { 0x00, 0x01, 0xFE, 0xFF, 0x41, 0x0A, 0x0D };
        Directory.CreateDirectory(Path.Combine(_dataRoot, DataDirectory.KeysDirectoryName));
        File.WriteAllBytes(Path.Combine(_dataRoot, DataDirectory.KeysDirectoryName, "key-abc.xml"), original);

        var backup = Build().Write();

        var extracted = Extract(backup.Path, $"{DataDirectory.KeysDirectoryName}/key-abc.xml");
        File.ReadAllBytes(extracted).Should().Equal(original);
    }

    [Fact]
    public void The_agent_session_artifacts_travel_with_it()
    {
        WriteSession("session-a/claude/settings.json", "{\"hooks\":{}}");
        WriteSession("session-a/approve.sh", "#!/bin/sh\n");

        var backup = Build().Write();

        using var archive = ZipFile.OpenRead(backup.Path);
        archive.GetEntry("agent-sessions/session-a/claude/settings.json").Should().NotBeNull();
        archive.GetEntry("agent-sessions/session-a/approve.sh").Should().NotBeNull();
    }

    [Fact]
    public void The_manifest_names_the_configured_providers_and_the_machine_bound_credential_caveat()
    {
        var providers = new StubProviderStore(
        [
            new ProviderConnection("wiki-main", ProviderKind.Wiki, "https://wiki.example", "wiki:token", "connected", DateTimeOffset.UtcNow),
            new ProviderConnection("board-main", ProviderKind.Board, "https://board.example", null, "configured", DateTimeOffset.UtcNow),
        ]);

        var backup = Build(providers).Write();

        // The owner moving machines needs to know what to reconnect, and that some credentials cannot
        // simply travel however good the key ring is.
        backup.ConfiguredProviders.Should().HaveCount(2);
        backup.ConfiguredProviders.Should().Contain(p => p.Contains("wiki-main", StringComparison.Ordinal)
            && p.Contains("credential stored", StringComparison.Ordinal));

        using var archive = ZipFile.OpenRead(backup.Path);
        var manifest = ReadManifest(archive);
        manifest.GetProperty("productVersion").GetString().Should().Be("9.9.9");
        manifest.GetProperty("schemaVersion").GetInt32().Should().Be(SqliteSchema.CurrentVersion);
        string.Join(' ', manifest.GetProperty("notes").EnumerateArray().Select(n => n.GetString()))
            .Should().Contain("reconnected");
    }

    [Fact]
    public void The_manifest_says_that_the_agent_CLI_sign_ins_stayed_behind()
    {
        // The container's HOME lives in the data directory and holds the agent CLIs' credentials, and it
        // is deliberately not in the archive (measured at 3.1 GB of caches on the owner's install). An
        // export that quietly leaves a sign-in behind is one the owner discovers on the new machine, in
        // the middle of trying to work, so the omission has to be IN the file that travels.
        Directory.CreateDirectory(Path.Combine(_dataRoot, "home", ".claude"));
        File.WriteAllText(Path.Combine(_dataRoot, "home", ".claude", ".credentials.json"), "{\"token\":\"x\"}");
        WriteKeyRing("key-ring-bytes");

        var backup = Build().Write();

        using var archive = ZipFile.OpenRead(backup.Path);
        archive.Entries.Should().NotContain(
            e => e.FullName.StartsWith("home/", StringComparison.Ordinal),
            "the caches around those credentials are gigabytes");
        var notes = string.Join(
            ' ', ReadManifest(archive).GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
        notes.Should().Contain("'home'");
        notes.Should().Contain("sign in to the agent CLIs again");
    }

    [Fact]
    public void Nothing_is_staged_beside_the_archive_once_it_is_written()
    {
        var backup = Build().Write();

        // The online backup needs a destination file; leaving it behind would double the space a backup
        // costs and put an unverified loose database in the folder the owner copies around.
        Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(backup.Path)!)
            .Should().ContainSingle().Which.Should().Be(backup.Path);
    }

    [Fact]
    public void A_backup_never_contains_a_previous_backup()
    {
        var first = Build().Write();

        var second = new StudioBackupWriter(
            new DataDirectory(_dataRoot),
            _factory,
            new FixedProductInfo("9.9.9", "full"),
            new EmptyProviderStore(),
            () => new DateTimeOffset(2026, 9, 13, 8, 0, 0, TimeSpan.Zero)).Write();

        using var archive = ZipFile.OpenRead(second.Path);
        archive.Entries.Should().NotContain(e => e.FullName.Contains("backups", StringComparison.Ordinal));
        File.Exists(first.Path).Should().BeTrue();
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static string? Read(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    private static JsonElement ReadManifest(ZipArchive archive)
    {
        using var stream = archive.GetEntry(StudioBackupWriter.ManifestEntryName)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return JsonDocument.Parse(reader.ReadToEnd()).RootElement.Clone();
    }

    private void WriteKeyRing(string content)
    {
        Directory.CreateDirectory(Path.Combine(_dataRoot, DataDirectory.KeysDirectoryName));
        File.WriteAllText(Path.Combine(_dataRoot, DataDirectory.KeysDirectoryName, "key-1.xml"), content);
    }

    private void WriteSession(string relativePath, string content)
    {
        var path = Path.Combine(_dataRoot, "agent-sessions", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class FixedProductInfo(string version, string variant) : IProductInfo
    {
        public string Version { get; } = version;

        public string Variant { get; } = variant;
    }

    private sealed class EmptyProviderStore : IProviderConnectionStore
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

    private sealed class StubProviderStore(IReadOnlyList<ProviderConnection> connections) : IProviderConnectionStore
    {
        public IReadOnlyList<ProviderConnection> GetAll() => connections;

        public IReadOnlyList<ProviderConnection> GetByKind(ProviderKind kind)
            => [.. connections.Where(c => c.Kind == kind)];

        public ProviderConnection? Get(string id) => connections.FirstOrDefault(c => c.Id == id);

        public void Upsert(ProviderConnection connection)
        {
        }

        public void Remove(string id)
        {
        }
    }
}
