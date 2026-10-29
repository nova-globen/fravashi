using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fravashi.Application.Backup;
using Fravashi.Application.Persistence;
using Fravashi.Application.Updates;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Backup;

/// <summary>
/// Writes the backup that is also the export (backlog 79), into
/// <c>&lt;data dir&gt;/backups/fravashi-backup-&lt;stamp&gt;.zip</c>.
/// <para>
/// The database copy is taken with SQLite's ONLINE BACKUP API
/// (<see cref="SqliteConnection.BackupDatabase(SqliteConnection)"/>), not by copying the file. That is the
/// whole reason this half lives in the studio: the studio holds the database open with WAL journaling, and a
/// file copy taken underneath that is a torn database plus a write-ahead log whose relationship to it is
/// whatever the scheduler decided. The online backup copies a consistent snapshot of the COMMITTED content,
/// so the <c>-wal</c> and <c>-shm</c> files are not in the zip and do not need to be: what they held that
/// mattered is already inside the copied database.
/// </para>
/// <para>
/// Beside it ride the Data Protection key ring, the agent-session artifacts and (work item #359) the files work
/// items carry. The key ring is in the zip
/// deliberately: every stored credential is encrypted with it, so a backup without it restores a database
/// whose secrets are unreadable. That also makes the file worth protecting like a credential itself, which
/// the page says in as many words.
/// </para>
/// <para>
/// What is NOT in it is <c>&lt;data dir&gt;/home</c>, the container's HOME (Dockerfile: "git/CLI config +
/// credentials; persistent"), which holds the agent CLIs' own sign-ins and their caches. It is left out on
/// purpose: measured on the owner's running container it is 3.1 GB of npm, dotnet and transcript caches
/// around a few small credential files, so putting it in the archive would turn a backup an owner copies
/// around into something they stop taking. The cost is that an agent CLI has to be signed in again after a
/// move, and that cost is only acceptable if it is SAID, so it is a manifest note (which
/// <c>fravashi restore</c> prints before it asks) and a paragraph on the page. Everything else in the data
/// directory is rebuilt by the next start (logs, the agent-CLI installs under <c>.npm-global</c>, the
/// integrity, sandbox-posture and update-state records), which is why none of it is here either.
/// </para>
/// <para>
/// The manifest carries a SHA256 per file, and the LAUNCHER verifies every one of them before it moves
/// anything (<c>fravashi restore</c>). The two sides share a file format and nothing else: the launcher is a
/// dependency-free library by design, so it parses this manifest with its own reader, and
/// <c>BackupRoundTripTests</c> holds the two together by driving a real archive from this writer through
/// that reader.
/// </para>
/// </summary>
public sealed class StudioBackupWriter : IStudioBackupWriter
{
    /// <summary>The folder under the data directory backups are written into.</summary>
    public const string FolderName = "backups";

    /// <summary>The manifest entry name at the root of the zip.</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>
    /// The manifest format number. It exists so a future change to the layout is REFUSED by an older
    /// launcher with a sentence, instead of being half-understood and unpacked anyway.
    /// </summary>
    public const int ManifestFormat = 1;

    /// <summary>
    /// The sentence the manifest and the page both carry. A provider can bind a credential to the machine
    /// that created it (a device-bound OAuth grant, a PAT scoped to a host), and no amount of key ring makes
    /// that credential work somewhere else: it has to be reconnected on the new machine.
    /// </summary>
    public const string MachineBoundCredentialNote =
        "The key ring is in this archive, so stored credentials decrypt on the machine this is restored " +
        "onto. A credential the PROVIDER bound to the old machine (a device-bound sign-in, a token issued " +
        "for that host) still has to be reconnected there after the move.";

    /// <summary>
    /// The second sentence both the manifest and the page carry: what the archive deliberately leaves out.
    /// An owner who is told "this is your export" and is not told that the agent CLIs' sign-ins stayed
    /// behind finds out on the new machine, in the middle of trying to work.
    /// </summary>
    public const string ExcludedStateNote =
        "The agent CLI's own sign-in is NOT in this archive. It keeps its credentials, and the " +
        "container-side git config, under the data directory's 'home' folder, which " +
        "is left out because it is gigabytes of caches: sign in to the agent CLIs again after restoring on " +
        "another machine. Nothing else is missing, logs and the agent-CLI installs are rebuilt on the " +
        "next start.";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly DataDirectory _dataDirectory;
    private readonly ISqliteConnectionFactory _connections;
    private readonly IProductInfo _product;
    private readonly IProviderConnectionStore _providers;
    private readonly Func<DateTimeOffset> _clock;

    public StudioBackupWriter(
        DataDirectory dataDirectory,
        ISqliteConnectionFactory connections,
        IProductInfo product,
        IProviderConnectionStore providers,
        Func<DateTimeOffset>? clock = null)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _product = product ?? throw new ArgumentNullException(nameof(product));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public string Folder => Path.Combine(_dataDirectory.Path, FolderName);

    /// <inheritdoc />
    public StudioBackup Write()
    {
        var stamp = _clock().ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var folder = Folder;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"fravashi-backup-{stamp}.zip");

        // The online backup needs a destination FILE, so it is staged beside the archive and removed
        // afterwards. Staged under the backups folder (not the system temp dir) so it shares the volume
        // the archive is written to: a data directory large enough to back up is large enough to stage.
        var staging = Path.Combine(folder, $".staging-{stamp}");
        Directory.CreateDirectory(staging);

        try
        {
            var stagedDatabase = Path.Combine(staging, DataDirectory.DatabaseFileName);
            var schemaVersion = CopyDatabase(stagedDatabase);
            var providers = ReadProviders();

            var files = new List<BackupFileEntry>();
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                files.Add(AddFile(archive, stagedDatabase, DataDirectory.DatabaseFileName));
                files.AddRange(AddFolder(archive, _dataDirectory.KeysPath, DataDirectory.KeysDirectoryName));
                files.AddRange(AddFolder(
                    archive,
                    Path.Combine(_dataDirectory.Path, AgentSessionsDirectoryName),
                    AgentSessionsDirectoryName));

                // Work item #359: the files work items carry live beside the database, not in it, so they ride here.
                files.AddRange(AddFolder(
                    archive,
                    Path.Combine(_dataDirectory.Path, WorkItemAttachmentsDirectoryName),
                    WorkItemAttachmentsDirectoryName));

                var manifest = new BackupManifestDocument(
                    ManifestFormat,
                    ArchiveKind,
                    _clock().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    _product.Version,
                    _product.Variant,
                    schemaVersion,
                    DataDirectory.DatabaseFileName,
                    providers,
                    files,
                    [MachineBoundCredentialNote, ExcludedStateNote]);

                var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var writer = new StreamWriter(
                    entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(JsonSerializer.Serialize(manifest, ManifestJson));
            }

            return new StudioBackup(
                path,
                new FileInfo(path).Length,
                files.Count,
                schemaVersion,
                [.. providers.Select(p => p.Describe())]);
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort: a staged copy left behind is dead weight beside the archive, never a
                // reason to fail a backup that was written.
            }
        }
    }

    /// <summary>The folder holding the per-session agent artifacts, mirrored into the archive by name.</summary>
    internal const string AgentSessionsDirectoryName = "agent-sessions";

    /// <summary>The folder holding work item attachments (work item #359), mirrored into the archive by name.</summary>
    internal const string WorkItemAttachmentsDirectoryName = WorkItems.FileWorkItemAttachments.FolderName;

    /// <summary>The manifest's <c>kind</c> marker: what stops a restore reading some other zip as a backup.</summary>
    internal const string ArchiveKind = "fravashi-backup";

    /// <summary>
    /// Takes the online backup and returns the schema version of what was copied. The version is read on
    /// the SAME connection the backup is taken from, so it describes the copy and not some later state.
    /// </summary>
    private int CopyDatabase(string destinationPath)
    {
        using var source = _connections.Open();
        var schemaVersion = SchemaMigrator.StoredVersion(source);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Never pooled: the file is closed and zipped immediately afterwards, and a pooled handle
            // would still be holding it.
            Pooling = false,
        }.ToString();

        using var destination = new SqliteConnection(connectionString);
        destination.Open();
        source.BackupDatabase(destination);
        return schemaVersion;
    }

    private IReadOnlyList<BackupProviderEntry> ReadProviders()
    {
        try
        {
            return
            [
                .. _providers.GetAll()
                    .OrderBy(p => p.Kind.ToString(), StringComparer.Ordinal)
                    .ThenBy(p => p.Id, StringComparer.Ordinal)
                    .Select(p => new BackupProviderEntry(
                        p.Id,
                        p.Kind.ToString(),
                        p.Status,
                        p.BaseUrl,
                        !string.IsNullOrWhiteSpace(p.CredentialRef))),
            ];
        }
        catch (Exception)
        {
            // The provider list is the "what to check after a move" note, not the payload. A store that
            // cannot be read must not cost the owner their backup.
            return [];
        }
    }

    private static IEnumerable<BackupFileEntry> AddFolder(ZipArchive archive, string folder, string prefix)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(
                     f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            yield return AddFile(archive, file, $"{prefix}/{relative}");
        }
    }

    /// <summary>
    /// Writes one file into the archive and hashes it ON THE WAY IN, so the recorded hash is of the bytes
    /// that were actually stored rather than of a second read that could have caught a different moment.
    /// </summary>
    private static BackupFileEntry AddFile(ZipArchive archive, string sourcePath, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        long size;
        byte[] hash;

        using (var entryStream = entry.Open())
        using (var hasher = SHA256.Create())
        using (var crypto = new CryptoStream(entryStream, hasher, CryptoStreamMode.Write))
        using (var source = new FileStream(
                   sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            source.CopyTo(crypto);
            crypto.FlushFinalBlock();
            size = source.Length;
            hash = hasher.Hash ?? [];
        }

        return new BackupFileEntry(entryName, size, Convert.ToHexString(hash).ToLowerInvariant());
    }
}

/// <summary>
/// The manifest document as it is written. The LAUNCHER has its own reader for this shape (it takes no
/// project references by design); the round-trip test is what keeps the two honest.
/// </summary>
internal sealed record BackupManifestDocument(
    int Format,
    string Kind,
    string CreatedUtc,
    string ProductVersion,
    string ProductVariant,
    int SchemaVersion,
    string DatabaseEntry,
    IReadOnlyList<BackupProviderEntry> Providers,
    IReadOnlyList<BackupFileEntry> Files,
    IReadOnlyList<string> Notes);

/// <summary>One configured provider, so a restore on another machine knows what to check.</summary>
internal sealed record BackupProviderEntry(
    string Id,
    string Kind,
    string Status,
    string? BaseUrl,
    bool HasCredential)
{
    public string Describe() =>
        $"{Kind} '{Id}' ({Status}{(HasCredential ? ", credential stored" : ", no credential stored")})";
}

/// <summary>One file in the archive with the hash the restore verifies before it moves anything.</summary>
internal sealed record BackupFileEntry(string Path, long SizeBytes, string Sha256);
