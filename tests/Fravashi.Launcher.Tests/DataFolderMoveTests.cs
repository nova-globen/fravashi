using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// The rename to Fravashi moves the former data folder (<see cref="ProductHome.FormerFolderName"/>) to <c>%LOCALAPPDATA%\Fravashi</c> on the resident's first
/// start (<c>docs/plans/rename-to-fravashi.md</c>; MOVE was chosen). Every test runs against a temp folder standing
/// in for %LOCALAPPDATA%, never the real one. What must hold: a clean move lands everything, verified, with a backup
/// and a pointer; a failure at any step leaves everything where it was and says why; a second run does nothing; a
/// fresh install has nothing to move; two installs are never merged; and the next start recreates the container on
/// the new path. The bridge release adds the copy fallback: a folder that cannot be renamed is copied, verified and
/// switched to, the old one is left whole, and only a later start removes it, once nothing holds it.
/// </summary>
public sealed class DataFolderMoveTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 30, 0, TimeSpan.Zero);

    private readonly string _localAppData =
        Path.Combine(Path.GetTempPath(), "fravashi-move-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly List<LauncherConfig?> _stopped = [];

    public DataFolderMoveTests() => Directory.CreateDirectory(_localAppData);

    private string Old => Path.Combine(_localAppData, ProductHome.FormerFolderName);

    private string New => Path.Combine(_localAppData, "Fravashi");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_localAppData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- the install the move starts from ------------------------------------------------------------------------

    /// <summary>An install as the earlier executable from before the rename leaves it: launcher.json, a WAL database, keys, logs, a profile.</summary>
    private void OldInstall(int rows = 3)
    {
        var data = Path.Combine(Old, "data");
        Directory.CreateDirectory(Path.Combine(data, "keys"));
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        Directory.CreateDirectory(Path.Combine(Old, "webview2", "Default"));
        File.WriteAllText(Path.Combine(data, "keys", "key-1.xml"), "<key id=\"1\" />");
        File.WriteAllText(Path.Combine(data, "logs", "studio-2026-10-26.log"), "a line\n");
        File.WriteAllText(Path.Combine(Old, "webview2", "Default", "Preferences"), "{}");
        File.WriteAllText(Path.Combine(Old, "bridge.token"), "token");
        new LauncherConfigStore(Path.Combine(Old, "launcher.json")).Save(new LauncherConfig
        {
            Port = 9090,
            DataDir = data,
            ContainerName = FormerInstall.ContainerName,
            Vpn = null,
        });

        using var connection = new SqliteConnection($"Data Source={Path.Combine(data, ProductHome.FormerDatabaseFileName)};Pooling=False");
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

    private static Dictionary<string, long> Tree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), f => new FileInfo(f).Length);

    /// <summary>The database in a folder's data directory, by whichever name it has (the move keeps the former one).</summary>
    private static string Db(string root) => ProductHome.DatabasePath(Path.Combine(root, "data"))
        ?? Path.Combine(root, "data", ProductHome.DatabaseFileName);

    /// <summary>A rename that fails the way the dev host's did: something outside the product holds the folder.</summary>
    private static void Denied(string from, string to) =>
        throw new UnauthorizedAccessException($"Access to the path '{from}' is denied.");

    private DataFolderMove Mover(
        Func<LauncherConfig?, string?>? stop = null,
        Action<DataMoveStep>? failAt = null,
        Action<string, string>? rename = null) => new(
        _localAppData,
        stop ?? (config =>
        {
            _stopped.Add(config);
            return null;
        }),
        () => Now,
        _log.Add,
        failAt,
        rename);

    private static Action<DataMoveStep> FailAt(DataMoveStep failing) => step =>
    {
        if (step == failing)
        {
            throw new IOException($"simulated failure at {failing}");
        }
    };

    // ---- a clean move --------------------------------------------------------------------------------------------

    [Fact]
    public void A_clean_move_lands_every_file_verified_and_leaves_only_the_pointer_behind()
    {
        OldInstall();
        SqliteConnection.ClearAllPools();
        var before = Tree(Old);

        var result = Mover().Run();

        result.Outcome.Should().Be(DataFolderMoveOutcome.Moved);
        result.Root.Should().Be(New);
        result.Notice.Should().BeNull();
        Tree(New).Keys.Should().BeEquivalentTo(before.Keys.Where(k => !k.EndsWith("-wal") && !k.EndsWith("-shm")));
        Rows(Db(New)).Should().Be(3);
        Directory.EnumerateFileSystemEntries(Old).Select(Path.GetFileName).Should().Equal(ProductHome.PointerFileName);
        File.ReadAllText(Path.Combine(Old, ProductHome.PointerFileName)).Should().Contain(New);
        ProductHome.Resolve(_localAppData).Should().Be(New);
        _stopped.Should().ContainSingle().Which!.ContainerName.Should().Be(FormerInstall.ContainerName);
    }

    [Fact]
    public void The_backup_is_taken_first_with_the_database_the_keys_and_launcher_json()
    {
        OldInstall(rows: 5);

        Mover().Run();

        var backup = Path.Combine(_localAppData, ProductHome.BackupFolderName, "20261027-093000");
        Rows(Path.Combine(backup, "data", ProductHome.FormerDatabaseFileName)).Should().Be(5);
        File.Exists(Path.Combine(backup, "data", "keys", "key-1.xml")).Should().BeTrue();
        File.Exists(Path.Combine(backup, "launcher.json")).Should().BeTrue();
    }

    [Fact]
    public void Launcher_json_is_rewritten_to_the_new_folder_and_records_where_the_container_still_mounts()
    {
        OldInstall();

        Mover().Run();

        var config = new LauncherConfigStore(Path.Combine(New, "launcher.json")).Load()!;
        config.DataDir.Should().Be(Path.Combine(New, "data"));
        config.DataDirBaked.Should().Be(Path.Combine(Old, "data"),
            "the existing container was created mounting the old path, so the next start must see it as stale");
        config.Port.Should().Be(9090, "nothing but paths changes");
        // Every path but dataDirBaked (which names the old mount ON PURPOSE, asserted above) has left the old folder.
        // Compared in the file's own JSON spelling: a Windows path is written with escaped backslashes, so the plain
        // path never matched there and this line held nothing until the dev host's CI run of bd4472b6 caught it on Linux.
        var oldInJson = System.Text.Json.JsonSerializer.Serialize(Old + Path.DirectorySeparatorChar).Trim('"');
        var everyOtherLine = File.ReadAllLines(Path.Combine(New, "launcher.json"))
            .Where(line => !line.Contains("\"dataDirBaked\"", StringComparison.Ordinal));
        everyOtherLine.Should().NotContain(line => line.Contains(oldInJson, StringComparison.Ordinal));
    }

    [Fact]
    public void An_unused_new_folder_is_set_aside_whole_rather_than_merged_or_deleted()
    {
        OldInstall();
        Directory.CreateDirectory(Path.Combine(New, "data", "logs"));
        File.WriteAllText(Path.Combine(New, "data", "logs", "window-2026-10-27.log"), "stray");

        var result = Mover().Run();

        result.Outcome.Should().Be(DataFolderMoveOutcome.Moved);
        var aside = $"{New}.pre-move-20261027-093000";
        File.ReadAllText(Path.Combine(aside, "data", "logs", "window-2026-10-27.log")).Should().Be("stray");
        Rows(Db(New)).Should().Be(3);
    }

    // ---- a failure at each step leaves everything where it was ----------------------------------------------------

    [Theory]
    [InlineData(DataMoveStep.Backup)]
    [InlineData(DataMoveStep.Move)]
    [InlineData(DataMoveStep.Verify)]
    [InlineData(DataMoveStep.Rewrite)]
    public void A_failure_at_any_step_keeps_everything_in_the_old_folder_and_says_why(DataMoveStep failing)
    {
        OldInstall();
        SqliteConnection.ClearAllPools();
        var before = Tree(Old);

        var result = Mover(failAt: FailAt(failing)).Run();

        result.Outcome.Should().Be(DataFolderMoveOutcome.KeptOldFolder);
        result.Root.Should().Be(Old);
        result.Notice.Should().StartWith($@"Data still at %LOCALAPPDATA%\{ProductHome.FormerFolderName}: ").And.Contain($"simulated failure at {failing}");
        Directory.Exists(New).Should().BeFalse("nothing is left half-moved");
        Tree(Old).Where(f => f.Key != DataMoveFile.FileName && !f.Key.EndsWith(DataMoveFile.FileName))
            .Select(f => f.Key).Should().BeEquivalentTo(before.Keys.Where(k => !k.EndsWith("-wal") && !k.EndsWith("-shm")));
        Rows(Db(Old)).Should().Be(3);
        new LauncherConfigStore(Path.Combine(Old, "launcher.json")).Load()!.DataDir.Should().Be(Path.Combine(Old, "data"));
        DataMoveFile.Read(Path.Combine(Old, "data")).Should().Be(result.Notice, "status and the studio read it from there");
        ProductHome.Resolve(_localAppData).Should().Be(Old);
    }

    [Fact]
    public void A_container_that_cannot_be_stopped_keeps_the_old_folder_untouched()
    {
        OldInstall();
        SqliteConnection.ClearAllPools();
        var before = Tree(Old);

        var result = Mover(stop: _ => "the engine said no").Run();

        result.Outcome.Should().Be(DataFolderMoveOutcome.KeptOldFolder);
        result.Notice.Should().Contain("could not be stopped first (the engine said no)");
        Directory.Exists(New).Should().BeFalse();
        Directory.Exists(Path.Combine(_localAppData, ProductHome.BackupFolderName)).Should().BeFalse("nothing was started");
        Tree(Old).Keys.Where(k => !k.EndsWith(DataMoveFile.FileName)).Should().BeEquivalentTo(before.Keys);
    }

    [Fact]
    public void A_failed_move_is_tried_again_at_the_next_start_and_then_succeeds()
    {
        OldInstall();
        Mover(failAt: FailAt(DataMoveStep.Verify)).Run().Outcome.Should().Be(DataFolderMoveOutcome.KeptOldFolder);

        var second = Mover().Run();

        second.Outcome.Should().Be(DataFolderMoveOutcome.Moved);
        Rows(Db(New)).Should().Be(3);
        DataMoveFile.Read(Path.Combine(New, "data")).Should().BeNull("the notice goes once the move is done");
    }

    [Fact]
    public void A_pointer_that_cannot_be_written_is_only_logged_because_the_data_has_moved_and_verified()
    {
        OldInstall();

        var result = Mover(failAt: FailAt(DataMoveStep.Pointer)).Run();

        result.Outcome.Should().Be(DataFolderMoveOutcome.Moved);
        result.Root.Should().Be(New);
        Rows(Db(New)).Should().Be(3);
        _log.Should().Contain(l => l.Contains("could not be written") && l.Contains("simulated failure at Pointer"));
    }

    // ---- nothing to move -----------------------------------------------------------------------------------------

    [Fact]
    public void A_second_run_after_a_move_does_nothing()
    {
        OldInstall();
        Mover().Run();
        var moved = Tree(New);
        _stopped.Clear();

        var again = Mover().Run();

        again.Outcome.Should().Be(DataFolderMoveOutcome.AlreadyMoved);
        again.Root.Should().Be(New);
        Tree(New).Keys.Should().BeEquivalentTo(moved.Keys);
        _stopped.Should().BeEmpty("nothing is stopped when nothing moves");
    }

    [Fact]
    public void A_fresh_install_has_no_old_folder_and_uses_the_new_one()
    {
        var result = Mover().Run();

        result.Outcome.Should().Be(DataFolderMoveOutcome.Fresh);
        result.Root.Should().Be(New);
        Directory.Exists(Old).Should().BeFalse("the move creates nothing in the old place");
        ProductHome.Resolve(_localAppData).Should().Be(New);
    }

    [Fact]
    public void Two_installs_are_never_merged_the_new_one_is_used_and_that_is_said()
    {
        OldInstall();
        Directory.CreateDirectory(Path.Combine(New, "data"));
        new LauncherConfigStore(Path.Combine(New, "launcher.json")).Save(new LauncherConfig
        {
            Port = 9091,
            DataDir = Path.Combine(New, "data"),
        });
        SqliteConnection.ClearAllPools();
        var oldBefore = Tree(Old);

        var result = Mover().Run();

        result.Outcome.Should().Be(DataFolderMoveOutcome.BothPresent);
        result.Root.Should().Be(New);
        result.Notice.Should().Contain(Old).And.Contain(New).And.Contain("never merged");
        Tree(Old).Keys.Should().BeEquivalentTo(oldBefore.Keys, "the old install is left exactly as it is");
        DataMoveFile.Read(Path.Combine(New, "data")).Should().Be(result.Notice);
        _stopped.Should().BeEmpty();
        ProductHome.Resolve(_localAppData).Should().Be(New);
    }

    // ---- where the paths point -----------------------------------------------------------------------------------

    [Fact]
    public void Paths_under_the_old_folder_are_moved_and_others_are_left_alone()
    {
        var mover = Mover();

        mover.Moved(Path.Combine(Old, "data")).Should().Be(Path.Combine(New, "data"));
        mover.Moved(Old).Should().Be(New);
        mover.Moved(Path.Combine(Old.ToUpperInvariant(), "x")).Should().Be(Path.Combine(New, "x"));
        mover.Moved(Old + "-ci").Should().BeNull("a sibling that merely starts with the name is another folder");
        mover.Moved(@"D:\elsewhere\data").Should().BeNull();
    }

    [Fact]
    public void Until_the_move_the_old_folder_is_the_one_in_use()
    {
        OldInstall();

        ProductHome.Resolve(_localAppData).Should().Be(Old);
        ProductHome.InUse(Old).Should().BeTrue();
        ProductHome.InUse(New).Should().BeFalse();
    }

    // ---- the container follows the data ---------------------------------------------------------------------------

    [Fact]
    public void After_the_move_the_next_start_recreates_the_container_on_the_new_path()
    {
        OldInstall();
        Mover().Run();
        var store = new LauncherConfigStore(Path.Combine(New, "launcher.json"));
        var runtime = new FakeContainerRuntime { ContainerPresent = true, ContainerIsRunning = false };
        var app = new LauncherApp(
            runtime,
            store,
            launcherDirectory: _localAppData,
            log: _log.Add,
            openBrowser: _ => { },
            isPortFree: _ => true,
            defaultDataDir: () => Path.Combine(New, "data"));

        app.Run().Should().Be(ExitCodes.Success);

        runtime.Removed.Should().Contain(FormerInstall.ContainerName, "the old container mounts a folder that is gone");
        runtime.RunSpecs.Should().ContainSingle().Which.DataDir.Should().Be(Path.Combine(New, "data"));
        store.Load()!.DataDirBaked.Should().Be(Path.Combine(New, "data"), "the new container's mount is recorded");
        _log.Should().Contain(l => l.Contains("mounts the data directory's old place"));
    }

    [Fact]
    public void A_launcher_json_from_before_the_field_existed_is_not_taken_for_a_move()
    {
        var data = Path.Combine(_localAppData, "data");
        var store = new LauncherConfigStore(Path.Combine(_localAppData, "launcher.json"));
        store.Save(new LauncherConfig { Port = 9090, DataDir = data, ContainerName = "fravashi" });
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(_localAppData, "launcher.json")))!.AsObject();
        json.Remove("dataDirBaked");
        File.WriteAllText(Path.Combine(_localAppData, "launcher.json"), json.ToJsonString());
        var runtime = new FakeContainerRuntime { ContainerPresent = true, ContainerIsRunning = true };

        new LauncherApp(runtime, store, launcherDirectory: _localAppData, log: _log.Add, openBrowser: _ => { },
            isPortFree: _ => true, defaultDataDir: () => data).Run().Should().Be(ExitCodes.Success);

        _log.Should().NotContain(l => l.Contains("mounts the data directory's old place"));
    }

    // ---- the copy fallback (the bridge release, decided 2026-10-26) ------------------------------------------

    [Fact]
    public void A_folder_that_cannot_be_renamed_is_copied_verified_and_switched_to_and_the_old_one_is_left_whole()
    {
        OldInstall();
        SqliteConnection.ClearAllPools();
        var before = Tree(Old);

        var result = Mover(rename: Denied).Run();

        result.Outcome.Should().Be(DataFolderMoveOutcome.Copied);
        result.Root.Should().Be(New);
        Tree(New).Keys.Should().BeEquivalentTo(before.Keys.Where(k => !k.EndsWith("-wal") && !k.EndsWith("-shm")));
        Rows(Db(New)).Should().Be(3);
        Tree(Old).Keys.Where(k => k != ProductHome.PointerFileName)
            .Should().BeEquivalentTo(before.Keys, "the old folder is never deleted in the run that copied");
        File.ReadAllText(Path.Combine(Old, ProductHome.PointerFileName)).Should().Contain("copied").And.Contain(New);
        new LauncherConfigStore(Path.Combine(New, "launcher.json")).Load()!.DataDir.Should().Be(Path.Combine(New, "data"));
        ProductHome.Resolve(_localAppData).Should().Be(New);
        _stopped.Should().HaveCount(2, "the container is confirmed stopped again right before the copy");
    }

    [Fact]
    public void The_copy_is_made_only_while_the_container_is_confirmed_stopped()
    {
        OldInstall();
        var asked = 0;

        var result = Mover(stop: _ => ++asked == 1 ? null : "it is running again", rename: Denied).Run();

        result.Outcome.Should().Be(DataFolderMoveOutcome.KeptOldFolder);
        result.Notice.Should().Contain("only copied while the studio container is stopped").And.Contain("it is running again");
        Directory.Exists(New).Should().BeFalse("nothing was copied");
        ProductHome.Resolve(_localAppData).Should().Be(Old);
    }

    [Theory]
    [InlineData(DataMoveStep.Copy)]
    [InlineData(DataMoveStep.Verify)]
    [InlineData(DataMoveStep.Rewrite)]
    [InlineData(DataMoveStep.Pointer)]
    public void A_copy_that_fails_at_any_step_is_removed_and_the_old_folder_stays_in_use(DataMoveStep failing)
    {
        OldInstall();
        SqliteConnection.ClearAllPools();
        var before = Tree(Old);

        var result = Mover(failAt: FailAt(failing), rename: Denied).Run();

        result.Outcome.Should().Be(DataFolderMoveOutcome.KeptOldFolder);
        result.Root.Should().Be(Old);
        Directory.Exists(New).Should().BeFalse("a copy that did not finish is not left beside the data");
        Tree(Old).Where(f => !f.Key.EndsWith(DataMoveFile.FileName)).Select(f => f.Key)
            .Should().BeEquivalentTo(before.Keys.Where(k => !k.EndsWith("-wal") && !k.EndsWith("-shm")));
        Rows(Db(Old)).Should().Be(3);
        ProductHome.Resolve(_localAppData).Should().Be(Old);
    }

    [Fact]
    public void A_later_start_removes_the_copied_old_folder_once_nothing_holds_it()
    {
        OldInstall();
        Mover(rename: Denied).Run().Outcome.Should().Be(DataFolderMoveOutcome.Copied);
        SqliteConnection.ClearAllPools();
        _stopped.Clear();

        var later = Mover().Run();

        later.Outcome.Should().Be(DataFolderMoveOutcome.RetiredOldCopy);
        later.Root.Should().Be(New);
        Directory.EnumerateFileSystemEntries(Old).Select(Path.GetFileName).Should().Equal(ProductHome.PointerFileName);
        Directory.EnumerateDirectories(_localAppData, ProductHome.FormerFolderName + ".retiring-*").Should().BeEmpty();
        Rows(Db(New)).Should().Be(3);
        _stopped.Should().BeEmpty("the copy in use is never stopped for this");
        Mover().Run().Outcome.Should().Be(DataFolderMoveOutcome.AlreadyMoved, "and after that there is nothing left to do");
    }

    [Fact]
    public void A_later_start_keeps_an_old_folder_that_is_still_held()
    {
        OldInstall();
        Mover(rename: Denied).Run();
        var before = Tree(Old);

        var later = Mover(rename: Denied).Run();

        later.Outcome.Should().Be(DataFolderMoveOutcome.CopiedOldKept);
        later.Root.Should().Be(New);
        Tree(Old).Keys.Should().BeEquivalentTo(before.Keys);
        _log.Should().Contain(l => l.Contains("still held"));
    }

    [Fact]
    public void A_later_start_keeps_the_old_folder_while_the_copy_is_not_healthy()
    {
        OldInstall();
        Mover(rename: Denied).Run();
        File.Delete(Path.Combine(New, "launcher.json"));
        var before = Tree(Old);

        var later = Mover().Run();

        later.Outcome.Should().Be(DataFolderMoveOutcome.CopiedOldKept);
        Tree(Old).Keys.Should().BeEquivalentTo(before.Keys, "an unhealthy copy never costs the original");
        _log.Should().Contain(l => l.Contains("has no launcher.json"));
    }

    [Fact]
    public void A_database_under_either_name_counts_as_an_install()
    {
        var data = Path.Combine(New, "data");
        Directory.CreateDirectory(data);
        ProductHome.InUse(New).Should().BeFalse();

        File.WriteAllText(Path.Combine(data, ProductHome.FormerDatabaseFileName), string.Empty);
        ProductHome.InUse(New).Should().BeTrue();
        ProductHome.DatabasePath(data).Should().EndWith(ProductHome.FormerDatabaseFileName);

        File.WriteAllText(Path.Combine(data, ProductHome.DatabaseFileName), string.Empty);
        ProductHome.DatabasePath(data).Should().EndWith(ProductHome.DatabaseFileName, "the current name wins when both are there");
    }
}
