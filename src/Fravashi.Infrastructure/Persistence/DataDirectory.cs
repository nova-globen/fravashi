using Fravashi.Domain;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// The single folder that holds all durable state: the SQLite database (<c>fravashi.db</c>) and the Data
/// Protection key ring (<c>keys/</c>). Under ADR 0003 this folder is the mounted volume in Docker and must
/// never be lost. The location is resolved from the <c>FRAVASHI_DATA_DIR</c> environment variable (set to
/// the mount point - e.g. <c>/data</c> - inside the container), falling back to a per-user local
/// application-data folder for host development runs. The directory is created if it does not exist.
/// </summary>
public sealed class DataDirectory
{
    /// <summary>Environment variable naming the data directory. The launcher and Docker set this.</summary>
    public const string EnvironmentVariable = "FRAVASHI_DATA_DIR";

    public const string DatabaseFileName = "fravashi.db";
    public const string KeysDirectoryName = "keys";

    public DataDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Data directory path must not be empty.", nameof(path));
        }

        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(Path);
    }

    /// <summary>The resolved, absolute data-directory path (guaranteed to exist).</summary>
    public string Path { get; }

    /// <summary>
    /// Absolute path to the SQLite database file inside the data directory. The bridge release: while the database still
    /// has its name from before the internal rename (the launcher renames it before the studio starts, and retries at the
    /// next start when it could not), that file is the database, never an empty new one created beside it.
    /// </summary>
    public string DatabasePath
    {
        get
        {
            var current = System.IO.Path.Combine(Path, DatabaseFileName);
            var former = System.IO.Path.Combine(Path, FormerNames.DatabaseFileName);
            return !File.Exists(current) && File.Exists(former) ? former : current;
        }
    }

    /// <summary>Absolute path to the Data Protection key-ring folder inside the data directory.</summary>
    public string KeysPath => System.IO.Path.Combine(Path, KeysDirectoryName);

    /// <summary>
    /// Resolves the data-directory path: <c>FRAVASHI_DATA_DIR</c> if set, else the optional
    /// <c>DataDir</c> configuration value, else <c>%LOCALAPPDATA%/Fravashi</c> (or the XDG/user-local
    /// equivalent on Linux/macOS).
    /// </summary>
    public static string ResolvePath(IConfiguration? configuration = null)
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var fromConfig = configuration?["DataDir"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return fromConfig;
        }

        // A host development run only (the launcher sets FRAVASHI_DATA_DIR for every real studio). It names the product's
        // folder since the rename to Fravashi (the launcher's ProductHome.FolderName), not the former one:
        // the launcher moves that folder away at its first start, and a dev studio recreating it would read as a second
        // install there.
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fravashi");
    }

    /// <summary>Resolves the path (see <see cref="ResolvePath"/>) and returns a ready-to-use instance.</summary>
    public static DataDirectory FromEnvironment(IConfiguration? configuration = null)
        => new(ResolvePath(configuration));
}
