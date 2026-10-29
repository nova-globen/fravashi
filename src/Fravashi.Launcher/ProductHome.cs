using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// Where this install keeps everything it owns on the host: launcher.json, the data directory mounted at
/// <c>/data</c> (database, key ring, logs), the WebView2 profile, the bridge token and the workspace registry. One
/// folder under %LOCALAPPDATA%, named <see cref="FolderName"/> since the rename to Nova Globen Fravashi and
/// <see cref="FormerFolderName"/> before it (<c>docs/plans/rename-to-fravashi.md</c>: the decision was to MOVE the
/// folder). <see cref="DataFolderMove"/> moves an old one on the resident's first start; until it has,
/// <see cref="Resolve"/> keeps naming the old folder, so nothing ever starts an empty install beside real data.
/// </summary>
/// <remarks>
/// The folder names are NOT <c>ProductName.Short</c> on purpose: a display name may change again, and a folder that
/// follows it would move the person's data each time. These are paths, a contract with every install out there.
/// </remarks>
public static class ProductHome
{
    /// <summary>The folder's name since the rename.</summary>
    public const string FolderName = "Fravashi";

    /// <summary>The folder's name before the rename; an install from then still has it until the move.</summary>
    public const string FormerFolderName = FormerNames.DataFolder;

    /// <summary>The data directory's name inside the folder: the bind mount at <c>/data</c>.</summary>
    public const string DataFolderName = "data";

    /// <summary>The launcher's settings file inside the folder.</summary>
    public const string ConfigFileName = "launcher.json";

    /// <summary>The studio's database inside the data directory (the studio's DataDirectory.DatabaseFileName).</summary>
    public const string DatabaseFileName = "fravashi.db";

    /// <summary>
    /// The database file's name before the internal rename. The bridge release renames it before the studio starts
    /// (<see cref="FormerInstallMigration"/>); until it has, an install is still in use through it.
    /// </summary>
    public const string FormerDatabaseFileName = FormerNames.DatabaseFileName;

    /// <summary>The one file the move leaves in the old folder, saying where everything went.</summary>
    public const string PointerFileName = "MOVED-TO-FRAVASHI.txt";

    /// <summary>The folder, beside both, that holds the backup the move takes before it touches anything.</summary>
    public const string BackupFolderName = "Fravashi-migration-backup";

    private static string? _current;

    /// <summary>This user's local application-data folder (%LOCALAPPDATA% on Windows).</summary>
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The folder since the rename, under <paramref name="localAppData"/>.</summary>
    public static string NewRoot(string localAppData) => Path.Combine(localAppData, FolderName);

    /// <summary>The folder before the rename, under <paramref name="localAppData"/>.</summary>
    public static string OldRoot(string localAppData) => Path.Combine(localAppData, FormerFolderName);

    /// <summary>The data directory inside a folder.</summary>
    public static string DataDir(string root) => Path.Combine(root, DataFolderName);

    /// <summary>launcher.json inside a folder.</summary>
    public static string ConfigPath(string root) => Path.Combine(root, ConfigFileName);

    /// <summary>
    /// The folder this process uses. Resolved on first use by <see cref="Resolve"/>, unless the resident start set it
    /// first from what the move did (<see cref="Use"/>), which it does before anything reads a path.
    /// </summary>
    public static string Current => _current ??= Resolve(LocalAppData);

    /// <summary>Fixes <see cref="Current"/> for the rest of this process (the move's verdict).</summary>
    public static void Use(string root) => _current = root;

    /// <summary>
    /// Which folder holds this install: the new one when it is in use; else the old one while it still holds anything
    /// but the pointer (an install not moved yet, or one whose move failed and stays where it was); else the new one
    /// (a fresh install, or one already moved).
    /// </summary>
    public static string Resolve(string localAppData)
    {
        var newRoot = NewRoot(localAppData);
        var oldRoot = OldRoot(localAppData);
        if (InUse(newRoot))
        {
            return newRoot;
        }

        return HoldsData(oldRoot) ? oldRoot : newRoot;
    }

    /// <summary>A folder an install is using: it has a launcher.json, or a database (by either name) in its data directory.</summary>
    public static bool InUse(string root) =>
        File.Exists(ConfigPath(root)) || DatabasePath(DataDir(root)) is not null;

    /// <summary>
    /// The database in <paramref name="dataDir"/>: the file by its name since the rename, else by its former name (an
    /// install the bridge release has not renamed it in yet), else null.
    /// </summary>
    public static string? DatabasePath(string dataDir)
    {
        var current = Path.Combine(dataDir, DatabaseFileName);
        if (File.Exists(current))
        {
            return current;
        }

        var former = Path.Combine(dataDir, FormerDatabaseFileName);
        return File.Exists(former) ? former : null;
    }

    /// <summary>A folder that exists and holds anything but the move's pointer file.</summary>
    public static bool HoldsData(string root)
    {
        if (!Directory.Exists(root))
        {
            return false;
        }

        return Directory.EnumerateFileSystemEntries(root)
            .Any(entry => !string.Equals(Path.GetFileName(entry), PointerFileName, StringComparison.OrdinalIgnoreCase));
    }
}
