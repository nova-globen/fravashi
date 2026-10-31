using System.Globalization;
using Fravashi.Domain;
using Microsoft.Data.Sqlite;

namespace Fravashi.Launcher;

/// <summary>The steps of <see cref="FormerInstallMigration"/>, in order; the test seam fails any one of them.</summary>
public enum FormerInstallStep
{
    /// <summary>launcher.json's container, image and sidecar names.</summary>
    Names,

    /// <summary>The former container, its VPN sidecar and its studio network.</summary>
    Containers,

    /// <summary>The database file's name.</summary>
    Database,
}

/// <summary>What <see cref="FormerInstallMigration.Run"/> did.</summary>
/// <param name="Complete">True when nothing of the former names is left for a later start.</param>
/// <param name="Failures">Why each step that did not finish stopped; each is tried again at the next start.</param>
public sealed record FormerInstallResult(bool Complete, IReadOnlyList<string> Failures);

/// <summary>
/// The bridge release's start-time migration of the names an install from before the internal rename still carries
/// (<c>docs/plans/rename-internal-names.md</c>, phase 2). It runs in the resident fravashi.exe right after
/// <see cref="DataFolderMove"/> (which settles which folder holds the install) and before anything starts the studio,
/// so nothing reads a name before its migration:
/// <list type="number">
///   <item><b>Names</b>: launcher.json's container, image and VPN sidecar names, when they are still the former defaults,
///     become the new ones; the image pin goes with the image name (it pins the former image, and the new one is pinned
///     at its first start, ADR 0007's trust on first use, which is what an install does too).</item>
///   <item><b>Containers</b>: the former studio container and its VPN sidecar are stopped and removed (the data is a bind
///     mount and survives), and the former rootless-podman studio network goes. The former workspaces volume is NOT
///     copied: it holds only the agents' synced copies, which the studio re-syncs from the host folders into the new
///     volume; it is kept until the cleanup release, so nothing on it is lost either.</item>
///   <item><b>Database</b>: the database file takes its new name, only once no container can have it open, with the
///     folder move's discipline: a backup through SQLite's backup API first, the WAL checkpointed, the rename, and the
///     database's integrity_check after (a failure puts the old name back).</item>
/// </list>
/// Each step is idempotent and verifies its result; a step that fails leaves the former state usable (the studio opens
/// the database under either name, see DataDirectory) and is tried again at the next start. After the new studio has
/// answered healthy, <see cref="RetireFormerImage"/> removes the former image tag. The cleanup release (phase 5) deletes
/// this class.
/// </summary>
public sealed class FormerInstallMigration
{
    private readonly LauncherConfigStore _store;
    private readonly Func<LauncherConfig, IContainerRuntime> _runtimeFor;
    private readonly Action<string> _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<FormerInstallStep>? _beforeStep;

    /// <param name="store">This install's launcher.json (the folder <see cref="DataFolderMove"/> settled on).</param>
    /// <param name="runtimeFor">The container engine for a configuration (ContainerRuntimeDetector.Detect; a fake in tests).</param>
    /// <param name="log">Where each step is said.</param>
    /// <param name="now">The clock, for the database backup's stamp.</param>
    /// <param name="beforeStep">Test seam: called before each step, and throwing from it fails that step.</param>
    public FormerInstallMigration(
        LauncherConfigStore store,
        Func<LauncherConfig, IContainerRuntime> runtimeFor,
        Action<string> log,
        Func<DateTimeOffset> now,
        Action<FormerInstallStep>? beforeStep = null)
    {
        _store = store;
        _runtimeFor = runtimeFor;
        _log = log;
        _now = now;
        _beforeStep = beforeStep;
    }

    /// <summary>Runs every step that has something to do. Never throws.</summary>
    public FormerInstallResult Run()
    {
        var failures = new List<string>();
        LauncherConfig? config;
        try
        {
            config = _store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Failed(failures, $"launcher.json could not be read ({ex.Message})");
        }

        if (config is null)
        {
            // A fresh install: nothing was ever made under the former names here.
            return new FormerInstallResult(true, failures);
        }

        if (Step(FormerInstallStep.Names, () => RenameNames(ref config)) is { } namesFailed)
        {
            failures.Add($"launcher.json still names the former container or image ({namesFailed})");
        }

        // Backlog 384: an install a healthy start already found clean asks the engine nothing. The database's former name
        // is a file on this machine, so a restore that brings it back is still noticed, and then the full migration runs.
        if (config.FormerNamesRetired == true && failures.Count == 0 && !FormerDatabaseExists(config))
        {
            return new FormerInstallResult(true, failures);
        }

        IContainerRuntime? runtime = null;
        var containersGone = false;
        if (Step(FormerInstallStep.Containers, () =>
            {
                runtime = _runtimeFor(config);
                return RemoveFormerContainers(runtime, config);
            }) is { } containersFailed)
        {
            failures.Add($"the former container could not be removed ({containersFailed})");
        }
        else
        {
            containersGone = true;
        }

        if (Step(FormerInstallStep.Database, () => RenameDatabase(config, runtime, containersGone)) is { } databaseFailed)
        {
            failures.Add($"the database keeps its former name for now ({databaseFailed})");
        }

        foreach (var failure in failures)
        {
            _log($"Rename to {ProductName.Short}: {failure}; this is tried again at the next start.");
        }

        return new FormerInstallResult(failures.Count == 0, failures);
    }

    /// <summary>
    /// <see cref="RetireFormerImage(IContainerRuntime, Action{string})"/> for this install, after a healthy start: skipped
    /// without asking the engine anything once launcher.json is marked <see cref="LauncherConfig.FormerNamesRetired"/>,
    /// and the mark is written when the former image tag is gone and no former container or sidecar is left (backlog 384).
    /// </summary>
    public static bool RetireFormerImage(LauncherConfigStore store, Func<LauncherConfig, IContainerRuntime> runtimeFor, Action<string> log)
    {
        try
        {
            if (store.Load() is not { } config || config.FormerNamesRetired == true)
            {
                return true;
            }

            var runtime = runtimeFor(config);
            if (!RetireFormerImage(runtime, log)
                || runtime.ContainerExists(FormerNames.ContainerName)
                || runtime.ContainerExists(FormerNames.VpnContainerName)
                || FormerDatabaseExists(config))
            {
                return false;
            }

            store.Save(config with { FormerNamesRetired = true });
            log($"Nothing of {ProductName.Short}'s former names is left; later starts no longer look for them.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log($"Whether the former names are gone could not be recorded ({ex.Message}); this is tried again at the next start.");
            return false;
        }
    }

    /// <summary>
    /// Removes the former image tag once the new studio has answered healthy. Untags only: an image another name still
    /// carries keeps that name, and one that carried only the former tag is the superseded image, which goes with it (a
    /// targeted removal, never a prune). Returns true when the former tag no longer resolves.
    /// </summary>
    public static bool RetireFormerImage(IContainerRuntime runtime, Action<string> log)
    {
        try
        {
            if (runtime.ResolveImageDigest(FormerNames.Image) is null)
            {
                return true;
            }

            if (runtime.ContainerExists(FormerNames.ContainerName))
            {
                log($"Kept the image tag {FormerNames.Image}: the former container still exists.");
                return false;
            }

            runtime.RemoveImage(FormerNames.Image);
            var gone = runtime.ResolveImageDigest(FormerNames.Image) is null;
            log(gone
                ? $"Removed the former image tag {FormerNames.Image}; {ProductName.Short} runs {LauncherDefaults.Image}."
                : $"The former image tag {FormerNames.Image} could not be removed; it is tried again at the next start.");
            return gone;
        }
        catch (Exception ex)
        {
            log($"The former image tag {FormerNames.Image} could not be removed ({ex.Message}); it is tried again at the next start.");
            return false;
        }
    }

    private string? RenameNames(ref LauncherConfig config)
    {
        var updated = config;
        if (string.Equals(config.ContainerName, FormerNames.ContainerName, StringComparison.Ordinal))
        {
            updated = updated with { ContainerName = LauncherDefaults.ContainerName };
        }

        if (string.Equals(config.Image, FormerNames.Image, StringComparison.Ordinal))
        {
            updated = updated with { Image = LauncherDefaults.Image, PinnedImageDigest = null };
        }

        if (config.Vpn is { } vpn && string.Equals(vpn.ContainerName, FormerNames.VpnContainerName, StringComparison.Ordinal))
        {
            updated = updated with { Vpn = vpn with { ContainerName = LauncherDefaults.VpnContainerName, PinnedImageDigest = null } };
        }

        if (updated == config)
        {
            return null;
        }

        _store.Save(updated);
        var saved = _store.Load();
        if (saved is null
            || saved.ContainerName != updated.ContainerName
            || saved.Image != updated.Image
            || saved.Vpn?.ContainerName != updated.Vpn?.ContainerName)
        {
            return "launcher.json did not read back with the new names";
        }

        _log($"launcher.json now names the container '{saved.ContainerName}' and the image '{saved.Image}' "
            + $"(they were '{config.ContainerName}' and '{config.Image}').");
        config = saved;
        return null;
    }

    private string? RemoveFormerContainers(IContainerRuntime runtime, LauncherConfig config)
    {
        if (!runtime.IsAvailable())
        {
            return $"the '{runtime.Name}' container engine is not answering";
        }

        // The studio first: in VPN mode it lives in the sidecar's network namespace.
        foreach (var name in new[] { FormerNames.ContainerName, FormerNames.VpnContainerName })
        {
            if (string.Equals(name, config.ContainerName, StringComparison.Ordinal)
                || string.Equals(name, config.Vpn?.ContainerName, StringComparison.Ordinal))
            {
                // launcher.json still uses this name on purpose (a custom setup): it is this install's own container.
                continue;
            }

            if (!runtime.ContainerExists(name))
            {
                continue;
            }

            if (runtime.ContainerRunning(name))
            {
                runtime.StopAndConfirm(name, _log);
            }

            runtime.RemoveContainer(name);
            if (runtime.ContainerExists(name))
            {
                return $"'{name}' is still there after it was removed";
            }

            _log(name == FormerNames.ContainerName
                ? $"Removed the former container '{name}' (the data is on the host and stays). The former volume "
                    + $"'{FormerNames.WorkspacesVolume}' is kept as it is: it holds only the agents' synced copies, which "
                    + $"are synced again from your folders into '{LauncherDefaults.WorkspacesVolume}'."
                : $"Removed the former VPN sidecar '{name}'.");
        }

        if (runtime.RemoveNetwork(FormerNames.StudioNetwork))
        {
            _log($"Removed the former studio network '{FormerNames.StudioNetwork}'.");
        }

        return null;
    }

    private string? RenameDatabase(LauncherConfig config, IContainerRuntime? runtime, bool formerContainersGone)
    {
        var dataDir = config.DataDir;
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            return null;
        }

        var former = Path.Combine(dataDir, FormerNames.DatabaseFileName);
        var current = Path.Combine(dataDir, ProductHome.DatabaseFileName);
        if (!File.Exists(former))
        {
            return null;
        }

        if (File.Exists(current))
        {
            _log($"Both {former} and {current} exist: {ProductName.Short} uses {current} and leaves the former file as it is.");
            return null;
        }

        if (!formerContainersGone || runtime is null)
        {
            return "no container can be shown not to have it open";
        }

        // A studio from an earlier start of this release may run on the former name (DataDirectory opens either), so
        // the container launcher.json names is stopped too; the start that follows creates or starts it again.
        if (runtime.ContainerExists(config.ContainerName) && runtime.ContainerRunning(config.ContainerName))
        {
            runtime.StopAndConfirm(config.ContainerName, _log);
            if (runtime.ContainerRunning(config.ContainerName))
            {
                return $"'{config.ContainerName}' is still running";
            }
        }

        var stamp = _now().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backupDir = Path.Combine(dataDir, "backups", "pre-rename-" + stamp);
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, FormerNames.DatabaseFileName);
        using (var source = Open(former))
        using (var copy = Open(backup))
        {
            source.BackupDatabase(copy);
        }

        if (IntegrityCheck(backup) is { } badBackup)
        {
            return $"the backup copy failed its integrity check: {badBackup}";
        }

        using (var connection = Open(former))
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }

        File.Move(former, current);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(former + suffix) && !File.Exists(current + suffix))
            {
                File.Move(former + suffix, current + suffix);
            }
        }

        if (IntegrityCheck(current) is { } bad)
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                if (File.Exists(current + suffix) && !File.Exists(former + suffix))
                {
                    File.Move(current + suffix, former + suffix);
                }
            }

            return $"the renamed database failed its integrity check ({bad}), so it has its former name again";
        }

        _log($"Renamed the database {FormerNames.DatabaseFileName} to {ProductHome.DatabaseFileName}; it passed its "
            + $"integrity check, and the copy taken first is in {backupDir}.");
        return null;
    }

    private static bool FormerDatabaseExists(LauncherConfig config) =>
        !string.IsNullOrWhiteSpace(config.DataDir) && File.Exists(Path.Combine(config.DataDir, FormerNames.DatabaseFileName));

    private string? Step(FormerInstallStep step, Func<string?> body)
    {
        try
        {
            _beforeStep?.Invoke(step);
            return body();
        }
        catch (Exception ex)
        {
            // Everything a step can meet (an engine's complaint, a locked file, SQLite) ends the step, never the start.
            return ex.Message;
        }
    }

    private FormerInstallResult Failed(List<string> failures, string reason)
    {
        failures.Add(reason);
        _log($"Rename to {ProductName.Short}: {reason}; this is tried again at the next start.");
        return new FormerInstallResult(false, failures);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string? IntegrityCheck(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var answer = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return string.Equals(answer, "ok", StringComparison.OrdinalIgnoreCase) ? null : answer ?? "no answer";
    }
}
