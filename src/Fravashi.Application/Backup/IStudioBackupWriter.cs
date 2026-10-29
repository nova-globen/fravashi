namespace Fravashi.Application.Backup;

/// <summary>
/// What one written backup is, so the page can say where it went and what it holds.
/// </summary>
/// <param name="Path">The absolute path of the written zip - a host folder the owner can copy anywhere.</param>
/// <param name="SizeBytes">Its size on disk.</param>
/// <param name="FileCount">How many files it carries (the manifest itself is not one of them).</param>
/// <param name="SchemaVersion">The database schema version captured in it, which the restore validates.</param>
/// <param name="ConfiguredProviders">
/// A one-line description per configured provider, so the owner moving to another machine knows what to
/// check after the move rather than discovering it when something silently does not connect.
/// </param>
public sealed record StudioBackup(
    string Path,
    long SizeBytes,
    int FileCount,
    int SchemaVersion,
    IReadOnlyList<string> ConfiguredProviders);

/// <summary>
/// Takes the backup that is also the export (backlog 79). The STUDIO owns this half because it owns the
/// open database: the copy is taken through SQLite's online backup API while the app keeps running with
/// WAL active, so there is no "stop the product first" step and no half-copied file.
/// <para>
/// The zip carries the database, the Data Protection key ring and the agent-session artifacts, plus a
/// manifest with a SHA256 per file. The key ring is deliberately in it: without it a moved install cannot
/// decrypt a single stored credential, which is the difference between a backup and a pile of bytes. A
/// credential the PROVIDER bound to the old machine still has to be reconnected there, and the manifest
/// says so rather than leaving the owner to find out.
/// </para>
/// <para>
/// Restoring is the other half and it is NOT here: it needs the studio stopped, so it belongs to the
/// launcher (<c>fravashi restore &lt;zip&gt;</c>).
/// </para>
/// </summary>
public interface IStudioBackupWriter
{
    /// <summary>The folder backups land in, so the page can name it before one has ever been taken.</summary>
    string Folder { get; }

    /// <summary>Writes a backup and returns where it landed.</summary>
    StudioBackup Write();
}
