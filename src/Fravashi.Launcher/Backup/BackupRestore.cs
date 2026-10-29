using System.IO.Compression;

namespace Fravashi.Launcher.Backup;

/// <summary>
/// What the swap did. <see cref="PreviousStateMoved"/> is the field that decides what the user is told
/// after a failure: nothing moved means the install is exactly as it was, and something moved means the
/// previous state is sitting in <see cref="PreRestoreFolder"/> and the message has to say so.
/// </summary>
/// <param name="Ok">True when the archive's contents are in place.</param>
/// <param name="PreRestoreFolder">
/// Where the previous database, key ring and sessions were moved to. It only EXISTS when
/// <see cref="PreviousStateMoved"/> is true: on a clean machine there was nothing to move, and a folder
/// created anyway is a folder the failure message would send the owner to for nothing.
/// </param>
/// <param name="Problem">Why the swap stopped; null on success.</param>
/// <param name="PreviousStateMoved">True once anything has been moved aside.</param>
/// <param name="Restored">The data-directory-relative paths written out of the archive.</param>
public sealed record RestoreSwap(
    bool Ok,
    string PreRestoreFolder,
    string? Problem,
    bool PreviousStateMoved,
    IReadOnlyList<string> Restored);

/// <summary>
/// The file half of <c>fravashi restore</c> (backlog 79): move the current state aside, then unpack the
/// archive over the data directory.
/// <para>
/// NOTHING IS EVER DELETED. The current database (with its <c>-wal</c> and <c>-shm</c>), the Data
/// Protection key ring and the agent-session folder are MOVED into
/// <c>&lt;data dir&gt;/backups/pre-restore-&lt;stamp&gt;/</c>. A restore that goes wrong therefore always has
/// whatever it replaced to name, and a restore that goes right leaves it anyway, because "it restored and
/// now the wrong data is there" is a thing an owner discovers minutes later. That folder is created only
/// when something is actually moved into it: on a clean machine there is nothing to replace, and an empty
/// folder there would make every message that names it false.
/// </para>
/// <para>
/// The archive is verified in full by <see cref="BackupArchive.Inspect"/> BEFORE this runs, so by the time
/// anything moves, every hash has already matched and every path has already been shown to stay inside the
/// data directory.
/// </para>
/// </summary>
public static class BackupRestore
{
    /// <summary>The folder under the data directory both backups and pre-restore folders live in.</summary>
    public const string BackupsFolderName = "backups";

    /// <summary>Prefix of the folder holding the state a restore replaced.</summary>
    public const string PreRestorePrefix = "pre-restore-";

    /// <summary>The Data Protection key-ring folder name inside the data directory.</summary>
    public const string KeysFolderName = "keys";

    /// <summary>The agent-session artifact folder name inside the data directory.</summary>
    public const string SessionsFolderName = "agent-sessions";

    /// <summary>The work item attachments folder name inside the data directory (work item #359).</summary>
    public const string AttachmentsFolderName = "work-item-attachments";

    /// <summary>The database file name inside the data directory, with its WAL companions.</summary>
    public const string DatabaseFileName = "fravashi.db";

    /// <summary>
    /// The database file's name in an archive taken before the internal rename (the bridge release). Such an archive is
    /// restored with the database under <see cref="DatabaseFileName"/>, which is the name the studio opens.
    /// </summary>
    public const string FormerDatabaseFileName = FormerInstall.DatabaseFileName;

    /// <summary>Where an archive's entry lands in the data directory: the former database name becomes the current one.</summary>
    public static string RestoredName(string relative) =>
        string.Equals(relative, FormerDatabaseFileName, StringComparison.Ordinal) ? DatabaseFileName : relative;

    /// <summary>Moves the current state aside and unpacks <paramref name="archivePath"/> over it.</summary>
    public static RestoreSwap Apply(string archivePath, BackupManifest manifest, string dataDir, string stamp)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var preRestore = Path.Combine(dataDir, BackupsFolderName, PreRestorePrefix + stamp);
        var moved = false;

        // Created on the first thing actually moved, never up front. A restore onto a clean machine moves
        // nothing, and an empty pre-restore folder there is exactly the shape of the lie this record is
        // supposed to prevent: a message naming a folder that holds none of the owner's data.
        void EnsurePreRestore() => Directory.CreateDirectory(preRestore);

        try
        {
            // The database under its former name too (an install the bridge release has not renamed it in yet): whatever
            // the restore replaces is moved aside, never left beside the restored one to be opened instead.
            foreach (var name in new[]
                     {
                         DatabaseFileName,
                         DatabaseFileName + "-wal",
                         DatabaseFileName + "-shm",
                         FormerDatabaseFileName,
                         FormerDatabaseFileName + "-wal",
                         FormerDatabaseFileName + "-shm",
                     })
            {
                var source = Path.Combine(dataDir, name);
                if (File.Exists(source))
                {
                    EnsurePreRestore();
                    File.Move(source, Path.Combine(preRestore, name));
                    moved = true;
                }
            }

            foreach (var name in new[] { KeysFolderName, SessionsFolderName, AttachmentsFolderName })
            {
                var source = Path.Combine(dataDir, name);
                if (Directory.Exists(source))
                {
                    EnsurePreRestore();
                    Directory.Move(source, Path.Combine(preRestore, name));
                    moved = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RestoreSwap(
                false,
                preRestore,
                $"the current data could not be moved aside ({ex.Message})",
                moved,
                []);
        }

        var restored = new List<string>();
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var file in manifest.Files)
            {
                var relative = file.Path!;
                var entry = archive.GetEntry(relative)
                    ?? throw new InvalidDataException($"'{relative}' vanished from the archive.");

                var destination = Path.Combine(dataDir, RestoredName(relative).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                restored.Add(relative);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // `moved`, never a hardcoded true: the unpack can stop on a machine where there was nothing to
            // move aside, and claiming otherwise points the owner at an empty folder in the one message
            // whose whole job is to say where their data is.
            return new RestoreSwap(
                false,
                preRestore,
                $"the archive could not be unpacked ({ex.Message})",
                moved,
                restored);
        }

        return new RestoreSwap(true, preRestore, null, moved, restored);
    }
}
