using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Domain;
using Microsoft.Data.Sqlite;

namespace Fravashi.Launcher;

/// <summary>The steps of <see cref="DataFolderMove"/>, in order; the test seam fails any one of them.</summary>
public enum DataMoveStep
{
    Stop,
    Backup,
    Move,
    Verify,
    Rewrite,
    Pointer,

    /// <summary>The copy fallback, when the folder could not be renamed (the bridge release).</summary>
    Copy,

    /// <summary>Removing the old folder a copy left, at a LATER start.</summary>
    Retire,
}

/// <summary>What <see cref="DataFolderMove.Run"/> found and did.</summary>
public enum DataFolderMoveOutcome
{
    /// <summary>No old folder: a fresh install, nothing to move.</summary>
    Fresh,

    /// <summary>The old folder holds only the pointer: moved at an earlier start.</summary>
    AlreadyMoved,

    /// <summary>Moved now: backed up, moved, verified, launcher.json rewritten.</summary>
    Moved,

    /// <summary>A step failed; everything is still in the old folder, which stays in use.</summary>
    KeptOldFolder,

    /// <summary>Both folders hold an install; the new one is used and the two are never merged.</summary>
    BothPresent,

    /// <summary>
    /// The folder could not be renamed (Access denied: something outside the product holds it open), so it was COPIED,
    /// verified and switched to; the old folder is left whole with the pointer in it, for a later start to remove.
    /// </summary>
    Copied,

    /// <summary>A copy from an earlier start is in use and the old folder it left was removed now.</summary>
    RetiredOldCopy,

    /// <summary>A copy from an earlier start is in use; the old folder is still held (or the copy did not check), so it stays.</summary>
    CopiedOldKept,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Root">The folder this install uses from now on (<see cref="ProductHome.Use"/>).</param>
/// <param name="Notice">The sentence status and the studio show, or null when there is nothing to say.</param>
public sealed record DataFolderMoveResult(DataFolderMoveOutcome Outcome, string Root, string? Notice);

/// <summary>
/// Moves the former data folder (<see cref="ProductHome.FormerFolderName"/>) to <c>%LOCALAPPDATA%\Fravashi</c> on the
/// first start of the renamed launcher
/// (<c>docs/plans/rename-to-fravashi.md</c>; MOVE was chosen over keeping the old name forever). It runs in the
/// resident process after it holds the single-instance mutex (which an older fravashi.exe shares, so no older resident is
/// running) and before anything opens a file in either folder.
/// <para>
/// The order, each step a reason to stop with everything where it was: stop the studio container (it holds the
/// database open through the bind mount); back up the database with SQLite's backup API, the key ring and
/// launcher.json into <c>%LOCALAPPDATA%\Fravashi-migration-backup\&lt;UTC stamp&gt;</c>; move the folder (a rename on
/// one volume, otherwise a copy); verify it (every file there with its size, and the database's integrity_check);
/// rewrite the paths launcher.json holds into the old folder, recording where the existing container's mount still
/// points so the next start recreates it on the new path (<see cref="LauncherConfig.DataDirBaked"/>); leave
/// <see cref="ProductHome.PointerFileName"/> in the old place.
/// </para>
/// <para>
/// A failure before the folder moved changes nothing. A failure after it (verify, rewrite) moves it back. Either way
/// the old folder stays in use, the reason is logged and left in <c>data-move.txt</c> for <c>fravashi status</c> and
/// the studio's Overview, and the next start tries again. A failure to write the pointer is only logged: the data has
/// moved and is verified by then. Both folders holding an install is never merged: the new one is used and the old
/// one is left exactly as it is, and that is said.
/// </para>
/// <para>
/// The copy fallback (the bridge release, decided 2026-10-26): on the dev host the rename failed with "Access denied",
/// because something outside the product (another session's wslc share) held the folder open. When the rename fails,
/// the folder is COPIED instead, but only while the product's own container is confirmed stopped; the copy is verified
/// (the same file list and sizes, and the database's integrity_check) before anything switches; the old folder is left
/// whole, with the pointer in it; and it is removed only at a LATER start that finds the new folder healthy and the old
/// one no longer held (a rename of it succeeds), never in the run that copied. A move to another volume is a copy too
/// and follows the same rule.
/// </para>
/// </summary>
public sealed class DataFolderMove
{
    private readonly string _localAppData;
    private readonly Func<LauncherConfig?, string?> _stopContainer;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string> _log;
    private readonly Action<DataMoveStep>? _beforeStep;
    private readonly Action<string, string> _renameFolder;

    /// <param name="localAppData">Where both folders live (%LOCALAPPDATA%; a temp folder in tests).</param>
    /// <param name="stopContainer">
    /// Stops the studio container the old launcher.json names, if it runs; returns null once it is not running, or
    /// the reason it could not be stopped (the move then waits for the next start).
    /// </param>
    /// <param name="now">The clock, for the backup's stamp.</param>
    /// <param name="log">Where each step is said.</param>
    /// <param name="beforeStep">Test seam: called before each step, and throwing from it fails that step.</param>
    /// <param name="renameFolder">
    /// Test seam: how a folder is renamed (<see cref="Directory.Move"/>); a test throws from it to stand for a folder
    /// something else holds open, which is what sends the move to the copy fallback.
    /// </param>
    public DataFolderMove(
        string localAppData,
        Func<LauncherConfig?, string?> stopContainer,
        Func<DateTimeOffset> now,
        Action<string> log,
        Action<DataMoveStep>? beforeStep = null,
        Action<string, string>? renameFolder = null)
    {
        _localAppData = localAppData;
        _stopContainer = stopContainer;
        _now = now;
        _log = log;
        _beforeStep = beforeStep;
        _renameFolder = renameFolder ?? Directory.Move;
    }

    private string OldRoot => ProductHome.OldRoot(_localAppData);

    private string NewRoot => ProductHome.NewRoot(_localAppData);

    /// <summary>Decides, and moves when there is something to move. Never throws.</summary>
    public DataFolderMoveResult Run()
    {
        try
        {
            return Decide();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or JsonException or SqliteException or NotSupportedException)
        {
            // Every step catches its own; this is the net under the decision itself, and it changes nothing.
            return Keep($"the move could not be prepared ({ex.Message})");
        }
    }

    private DataFolderMoveResult Decide()
    {
        if (!ProductHome.HoldsData(OldRoot))
        {
            var moved = File.Exists(Path.Combine(OldRoot, ProductHome.PointerFileName));
            DataMoveFile.Clear(StatusDir(NewRoot));
            return new(moved ? DataFolderMoveOutcome.AlreadyMoved : DataFolderMoveOutcome.Fresh, NewRoot, null);
        }

        if (ProductHome.InUse(NewRoot) && File.Exists(Path.Combine(OldRoot, ProductHome.PointerFileName)))
        {
            // A copy from an earlier start (the pointer says the old folder was left on purpose): never "both present".
            return RetireCopiedOldFolder();
        }

        if (ProductHome.InUse(NewRoot))
        {
            var both = $"Both {OldRoot} and {NewRoot} hold an install. {ProductName.Short} uses {NewRoot} and leaves "
                + $"{OldRoot} exactly as it is: two installs are never merged. Copy anything you still need out of the old "
                + "folder yourself, then delete it, and this note goes away.";
            _log(both);
            DataMoveFile.Write(StatusDir(NewRoot), both);
            return new(DataFolderMoveOutcome.BothPresent, NewRoot, both);
        }

        return Move();
    }

    private DataFolderMoveResult Move()
    {
        var oldConfigPath = ProductHome.ConfigPath(OldRoot);
        var config = Load(oldConfigPath);
        _log($"Moving this install from {OldRoot} to {NewRoot} (the rename to {ProductName.Full}).");

        if (Step(DataMoveStep.Stop, () => _stopContainer(config)) is { } stopFailed)
        {
            return Keep($"the studio container could not be stopped first ({stopFailed})");
        }

        var stamp = _now().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = Path.Combine(_localAppData, ProductHome.BackupFolderName, stamp);
        for (var attempt = 2; Directory.Exists(backup); attempt++)
        {
            // An earlier attempt in the same second left its own backup; each attempt keeps its own.
            backup = Path.Combine(_localAppData, ProductHome.BackupFolderName, $"{stamp}-{attempt}");
        }

        if (Step(DataMoveStep.Backup, () => Backup(config, backup)) is { } backupFailed)
        {
            return Keep($"the backup before the move failed ({backupFailed})");
        }

        _log($"Backed up the database, the key ring and launcher.json to {backup}.");

        var before = Inventory(OldRoot);
        string? aside = null;
        string? renameRefused = null;
        var moveFailed = Step(DataMoveStep.Move, () =>
        {
            if (Directory.Exists(NewRoot))
            {
                // Not in use (checked above), but not empty either: an earlier half-run or a stray log. Set it aside
                // whole rather than merge into it or delete it.
                aside = $"{NewRoot}.pre-move-{stamp}";
                Directory.Move(NewRoot, aside);
                _log($"Set the unused {NewRoot} aside as {aside} (nothing in it is deleted).");
            }

            if (!SameVolume(OldRoot, NewRoot))
            {
                renameRefused = "the two folders are on different volumes";
                return null;
            }

            try
            {
                _renameFolder(OldRoot, NewRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something outside the product holds the folder (seen on the dev host: "Access denied"). The copy below is the
                // fallback; nothing has changed yet.
                renameRefused = ex.Message;
            }

            return null;
        });
        if (moveFailed is not null)
        {
            RestoreAside(aside);
            return Keep($"the folder could not be moved ({moveFailed})");
        }

        if (renameRefused is not null)
        {
            return CopyInstead(config, before, aside, backup, renameRefused);
        }

        if (Step(DataMoveStep.Verify, () => Verify(before)) is { } verifyFailed)
        {
            return Undo(copied: false, aside, $"the moved folder did not verify ({verifyFailed})");
        }

        var originalDataDir = config?.DataDir;
        if (Step(DataMoveStep.Rewrite, () => Rewrite(originalDataDir)) is { } rewriteFailed)
        {
            return Undo(copied: false, aside, $"launcher.json could not be rewritten for the new folder ({rewriteFailed})");
        }

        if (Step(DataMoveStep.Pointer, () => WritePointer(backup, copied: false)) is { } pointerFailed)
        {
            _log($"The move is done, but the note in {OldRoot} saying so could not be written ({pointerFailed}).");
        }

        DataMoveFile.Clear(StatusDir(NewRoot));
        _log($"Moved this install to {NewRoot}; {before.Count} file(s) verified, and the database passed its integrity check.");
        return new(DataFolderMoveOutcome.Moved, NewRoot, null);
    }

    /// <summary>
    /// The copy fallback: the folder could not be renamed, so it is copied, verified and switched to, and the old folder
    /// is left whole with the pointer in it. Only while the product's own container is confirmed stopped (asked again
    /// here, right before the copy), and the old folder is never deleted in this run.
    /// </summary>
    private DataFolderMoveResult CopyInstead(
        LauncherConfig? config, IReadOnlyDictionary<string, long> before, string? aside, string backup, string why)
    {
        _log($"The folder could not be renamed ({why}); copying it instead, and leaving the old folder as it is.");
        if (Step(DataMoveStep.Stop, () => _stopContainer(config)) is { } running)
        {
            RestoreAside(aside);
            return Keep($"the folder could not be renamed ({why}), and it is only copied while the studio container is "
                + $"stopped, which could not be made sure of ({running})");
        }

        if (Step(DataMoveStep.Copy, () => { CopyTree(OldRoot, NewRoot); return null; }) is { } copyFailed)
        {
            DeletePartialCopy();
            RestoreAside(aside);
            return Keep($"the folder could not be renamed ({why}) nor copied ({copyFailed})");
        }

        if (Step(DataMoveStep.Verify, () => Verify(before)) is { } verifyFailed)
        {
            return Undo(copied: true, aside, $"the copied folder did not verify ({verifyFailed})");
        }

        if (Step(DataMoveStep.Rewrite, () => Rewrite(config?.DataDir)) is { } rewriteFailed)
        {
            return Undo(copied: true, aside, $"launcher.json could not be rewritten for the new folder ({rewriteFailed})");
        }

        // The pointer is what tells a later start that the old folder was left ON PURPOSE (and may go once unlocked), so
        // without it the copy is not switched to: two unmarked installs would read as "both present" for good.
        if (Step(DataMoveStep.Pointer, () => WritePointer(backup, copied: true)) is { } pointerFailed)
        {
            return Undo(copied: true, aside, $"the note in the old folder saying it was copied could not be written ({pointerFailed})");
        }

        DataMoveFile.Clear(StatusDir(NewRoot));
        _log($"Copied this install to {NewRoot}; {before.Count} file(s) verified, and the database passed its integrity check. "
            + $"{OldRoot} is left as it was and is removed at a later start, once nothing holds it.");
        return new(DataFolderMoveOutcome.Copied, NewRoot, null);
    }

    /// <summary>
    /// At a start AFTER a copy: the old folder goes only when the new one is healthy (launcher.json there, and the
    /// database passes its integrity check) and the old one is no longer held, which renaming it proves (a rename fails
    /// while anything has a handle in the tree). Renamed first and deleted after, so a delete that stops part way never
    /// leaves a half-deleted folder under the old name. Anything else keeps it, for the next start.
    /// </summary>
    private DataFolderMoveResult RetireCopiedOldFolder()
    {
        DataMoveFile.Clear(StatusDir(NewRoot));
        var unhealthy = Step(DataMoveStep.Retire, () =>
        {
            if (!File.Exists(ProductHome.ConfigPath(NewRoot)))
            {
                return $"{NewRoot} has no launcher.json";
            }

            var database = ProductHome.DatabasePath(ProductHome.DataDir(NewRoot));
            return database is not null && IntegrityCheck(database) is { } bad
                ? $"the database in {NewRoot} failed its integrity check: {bad}"
                : null;
        });
        if (unhealthy is not null)
        {
            _log($"{OldRoot} (copied to {NewRoot} at an earlier start) is kept: {unhealthy}.");
            return new(DataFolderMoveOutcome.CopiedOldKept, NewRoot, null);
        }

        var stamp = _now().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var retiring = $"{OldRoot}.retiring-{stamp}";
        if (Step(DataMoveStep.Retire, () => { _renameFolder(OldRoot, retiring); return null; }) is { } held)
        {
            _log($"{OldRoot} (copied to {NewRoot} at an earlier start) is still held, so it stays until a later start ({held}).");
            return new(DataFolderMoveOutcome.CopiedOldKept, NewRoot, null);
        }

        if (Step(DataMoveStep.Retire, () => { Directory.Delete(retiring, recursive: true); return null; }) is { } deleteFailed)
        {
            _log($"{OldRoot} was copied to {NewRoot} and set aside as {retiring}, which could not be deleted completely "
                + $"({deleteFailed}). Nothing in it is used; delete it by hand.");
        }

        Step(DataMoveStep.Pointer, () => WritePointer(backup: null, copied: false));
        _log($"Removed {OldRoot}: its copy in {NewRoot} is in use and checks, and nothing held the old folder any more.");
        return new(DataFolderMoveOutcome.RetiredOldCopy, NewRoot, null);
    }

    private void DeletePartialCopy()
    {
        if (Directory.Exists(NewRoot) && ProductHome.HoldsData(OldRoot))
        {
            // A copy that stopped part way: the old folder was never touched, so the partial copy goes.
            Step(DataMoveStep.Copy, () => { Directory.Delete(NewRoot, recursive: true); return null; });
        }
    }

    /// <summary>Runs one step; returns null on success, else the reason (an exception's message).</summary>
    private string? Step(DataMoveStep step, Func<string?> body)
    {
        try
        {
            _beforeStep?.Invoke(step);
            return body();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or JsonException or SqliteException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    private DataFolderMoveResult Keep(string reason)
    {
        var notice = $"Data still at %LOCALAPPDATA%\\{ProductHome.FormerFolderName}: {reason}. {ProductName.Short} keeps "
            + "using it as it is and tries the move again at its next start.";
        _log(notice);
        DataMoveFile.Write(StatusDir(OldRoot), notice);
        return new(DataFolderMoveOutcome.KeptOldFolder, OldRoot, notice);
    }

    /// <summary>Puts everything back where it was after a failure past the move itself.</summary>
    private DataFolderMoveResult Undo(bool copied, string? aside, string reason)
    {
        try
        {
            if (copied)
            {
                // The old folder was never touched; the copy goes.
                if (Directory.Exists(OldRoot) && ProductHome.HoldsData(OldRoot))
                {
                    Directory.Delete(NewRoot, recursive: true);
                }
            }
            else
            {
                Directory.Move(NewRoot, OldRoot);
            }

            RestoreAside(aside);
            return Keep(reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not move it back: the data is whole in the new folder, so that is where it is used, and said.
            var notice = $"{reason}, and moving it back failed too ({ex.Message}). Everything is in {NewRoot}, which "
                + $"{ProductName.Short} uses now; the backup taken first is under %LOCALAPPDATA%\\{ProductHome.BackupFolderName}.";
            _log(notice);
            DataMoveFile.Write(StatusDir(NewRoot), notice);
            return new(DataFolderMoveOutcome.KeptOldFolder, NewRoot, notice);
        }
    }

    private void RestoreAside(string? aside)
    {
        if (aside is null || Directory.Exists(NewRoot) || !Directory.Exists(aside))
        {
            return;
        }

        try
        {
            Directory.Move(aside, NewRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"{aside} stays set aside ({ex.Message}).");
        }
    }

    /// <summary>The database (SQLite's backup API), the key ring and launcher.json, before anything moves.</summary>
    private string? Backup(LauncherConfig? config, string backup)
    {
        Directory.CreateDirectory(backup);
        var dataDir = string.IsNullOrWhiteSpace(config?.DataDir) ? ProductHome.DataDir(OldRoot) : config!.DataDir;
        // Either name: an install from before the internal rename still has its former database name (the bridge release).
        if (ProductHome.DatabasePath(dataDir) is { } database)
        {
            var target = Path.Combine(backup, ProductHome.DataFolderName, Path.GetFileName(database));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var source = Open(database))
            using (var copy = Open(target))
            {
                source.BackupDatabase(copy);
            }

            if (IntegrityCheck(target) is { } bad)
            {
                return $"the backup copy of the database failed its integrity check: {bad}";
            }
        }

        var keys = Path.Combine(dataDir, "keys");
        if (Directory.Exists(keys))
        {
            CopyTree(keys, Path.Combine(backup, ProductHome.DataFolderName, "keys"));
        }

        var launcherJson = ProductHome.ConfigPath(OldRoot);
        if (File.Exists(launcherJson))
        {
            File.Copy(launcherJson, ProductHome.ConfigPath(backup), overwrite: true);
        }

        return null;
    }

    /// <summary>Every file the old folder held is in the new one with its size, and the database checks.</summary>
    private string? Verify(IReadOnlyDictionary<string, long> before)
    {
        var after = Inventory(NewRoot);
        if (after.Count != before.Count)
        {
            return $"{before.Count} file(s) before the move and {after.Count} after";
        }

        foreach (var (path, size) in before)
        {
            if (!after.TryGetValue(path, out var moved))
            {
                return $"{path} is missing";
            }

            if (moved != size)
            {
                return $"{path} was {size} bytes and is {moved}";
            }
        }

        var database = ProductHome.DatabasePath(ProductHome.DataDir(NewRoot));
        return database is not null && IntegrityCheck(database) is { } bad
            ? $"the database failed its integrity check: {bad}"
            : null;
    }

    /// <summary>
    /// Rewrites every string in the moved launcher.json that names a path in the old folder, and records where the
    /// existing container's mount still points (<see cref="LauncherConfig.DataDirBaked"/>) when nothing recorded it,
    /// so the next start recreates the container on the new path.
    /// </summary>
    private string? Rewrite(string? originalDataDir)
    {
        var path = ProductHome.ConfigPath(NewRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException("it is not a JSON object");
        var rewritten = RewritePaths(root);
        if (!string.IsNullOrWhiteSpace(originalDataDir) && root["dataDirBaked"] is null)
        {
            root["dataDirBaked"] = originalDataDir;
        }

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _log($"Rewrote {rewritten} path(s) in launcher.json from {OldRoot} to {NewRoot}.");
        return null;
    }

    private int RewritePaths(JsonNode node)
    {
        var count = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && Moved(text) is { } moved)
                    {
                        obj[key] = moved;
                        count++;
                    }
                    else if (obj[key] is { } child)
                    {
                        count += RewritePaths(child);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && Moved(text) is { } moved)
                    {
                        array[i] = moved;
                        count++;
                    }
                    else if (array[i] is { } child)
                    {
                        count += RewritePaths(child);
                    }
                }

                break;
        }

        return count;
    }

    /// <summary>The same path under the new folder, or null when <paramref name="text"/> is not under the old one.</summary>
    internal string? Moved(string text)
    {
        var old = OldRoot.TrimEnd('\\', '/');
        if (!text.StartsWith(old, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = text[old.Length..];
        return rest.Length == 0 || rest[0] is '\\' or '/' ? NewRoot.TrimEnd('\\', '/') + rest : null;
    }

    /// <param name="backup">The backup taken before the move, or null when there is none to name (the retirement of a copy).</param>
    /// <param name="copied">True when the old folder was copied and left whole: it is then removed at a later start.</param>
    private string? WritePointer(string? backup, bool copied)
    {
        Directory.CreateDirectory(OldRoot);
        File.WriteAllText(
            Path.Combine(OldRoot, ProductHome.PointerFileName),
            $"{ProductName.Full} (formerly {ProductName.Former}) {(copied ? "copied" : "moved")} this folder to {NewRoot} on "
            + $"{_now().UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC.{Environment.NewLine}"
            + $"Nothing here is used any more: start {ProductName.Short} from the Start menu, or run `{ProductName.Command}`."
            + (copied
                ? $"{Environment.NewLine}{ProductName.Short} removes this folder at a later start, once nothing holds it open."
                : string.Empty)
            + (backup is null ? string.Empty : $"{Environment.NewLine}The backup taken before the move is in {backup}.")
            + Environment.NewLine);
        return null;
    }

    /// <summary>The data directory status and the studio read: launcher.json's, else the folder's own.</summary>
    private static string StatusDir(string root) =>
        Load(ProductHome.ConfigPath(root)) is { DataDir.Length: > 0 } config ? config.DataDir : ProductHome.DataDir(root);

    private static LauncherConfig? Load(string path) => File.Exists(path) ? new LauncherConfigStore(path).Load() : null;

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

    /// <summary>Null when the database answers <c>ok</c>, else what it answered.</summary>
    private static string? IntegrityCheck(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var answer = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return string.Equals(answer, "ok", StringComparison.OrdinalIgnoreCase) ? null : answer ?? "no answer";
    }

    /// <summary>Every file under <paramref name="root"/> by its relative path, with its size.</summary>
    private static Dictionary<string, long> Inventory(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: false);
        }
    }
}
