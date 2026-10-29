using AwesomeAssertions;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

public sealed class LocalRepositoryWriterTests : IDisposable
{
    private readonly string _root;
    private readonly LocalRepositoryWriter _writer;

    public LocalRepositoryWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fravashi-writer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "alpha");
        _writer = new LocalRepositoryWriter(
            Options.Create(new RepositoryReaderOptions { Root = _root }), new RepositoryWriteGate());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup only.
        }
    }

    [Fact]
    public async Task Creates_files_and_directories_and_rejects_collisions()
    {
        (await _writer.CreateFileAsync("src", "b.txt")).Succeeded.Should().BeTrue();
        File.Exists(Path.Combine(_root, "src", "b.txt")).Should().BeTrue();

        (await _writer.CreateDirectoryAsync("", "docs")).Succeeded.Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "docs")).Should().BeTrue();

        var collision = await _writer.CreateFileAsync("src", "b.txt");
        collision.Succeeded.Should().BeFalse();
        collision.Error.Should().Contain("already exists");
    }

    [Fact]
    public async Task Rejects_invalid_names_and_escaping_paths()
    {
        (await _writer.CreateFileAsync("src", "..")).Succeeded.Should().BeFalse();
        (await _writer.CreateFileAsync("src", "a/b.txt")).Succeeded.Should().BeFalse();
        (await _writer.CreateFileAsync("../outside", "x.txt")).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Copy_duplicates_and_move_relocates()
    {
        (await _writer.CreateDirectoryAsync("", "target")).Succeeded.Should().BeTrue();

        (await _writer.CopyAsync("src/a.txt", "target")).Succeeded.Should().BeTrue();
        File.Exists(Path.Combine(_root, "target", "a.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "src", "a.txt")).Should().BeTrue();

        (await _writer.MoveAsync("src/a.txt", "")).Succeeded.Should().BeTrue();
        File.Exists(Path.Combine(_root, "a.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "src", "a.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Same_folder_copy_gets_a_copy_suffix()
    {
        (await _writer.CopyAsync("src/a.txt", "src")).Succeeded.Should().BeTrue();
        File.Exists(Path.Combine(_root, "src", "a copy.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Copying_a_directory_is_recursive_and_self_nesting_is_rejected()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "inner"));
        File.WriteAllText(Path.Combine(_root, "src", "inner", "deep.txt"), "deep");
        (await _writer.CreateDirectoryAsync("", "target")).Succeeded.Should().BeTrue();

        (await _writer.CopyAsync("src", "target")).Succeeded.Should().BeTrue();
        File.Exists(Path.Combine(_root, "target", "src", "inner", "deep.txt")).Should().BeTrue();

        (await _writer.MoveAsync("src", "src/inner")).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Unconfigured_writer_refuses_everything()
    {
        var writer = new LocalRepositoryWriter(Options.Create(new RepositoryReaderOptions()), new RepositoryWriteGate());

        var result = await writer.CreateFileAsync("", "x.txt");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("No repository is configured");
    }

    // ---- SaveFileAsync (phase 2, ADR 0016) ----

    private static MemoryStream Bytes(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Save_overwrites_an_existing_file_and_reports_the_new_timestamp()
    {
        var result = await _writer.SaveFileAsync("src/a.txt", Bytes("updated"), expectedLastWriteUtc: null);

        result.Succeeded.Should().BeTrue();
        result.LastWriteUtc.Should().NotBeNull();
        File.ReadAllText(Path.Combine(_root, "src", "a.txt")).Should().Be("updated");
        result.LastWriteUtc.Should().Be(
            new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(_root, "src", "a.txt")), TimeSpan.Zero));
    }

    [Fact]
    public async Task Save_detects_a_conflicting_disk_change_and_writes_nothing()
    {
        var target = Path.Combine(_root, "src", "a.txt");
        var staleStamp = new DateTimeOffset(File.GetLastWriteTimeUtc(target), TimeSpan.Zero).AddMinutes(-5);

        var result = await _writer.SaveFileAsync("src/a.txt", Bytes("mine"), staleStamp);

        result.Succeeded.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        File.ReadAllText(target).Should().Be("alpha");
    }

    [Fact]
    public async Task Save_with_the_matching_timestamp_succeeds()
    {
        var target = Path.Combine(_root, "src", "a.txt");
        var current = new DateTimeOffset(File.GetLastWriteTimeUtc(target), TimeSpan.Zero);

        var result = await _writer.SaveFileAsync("src/a.txt", Bytes("mine"), current);

        result.Succeeded.Should().BeTrue();
        File.ReadAllText(target).Should().Be("mine");
    }

    [Fact]
    public async Task Save_refuses_a_missing_file_unless_forced_which_recreates_it()
    {
        var checkedSave = await _writer.SaveFileAsync("src/gone.txt", Bytes("x"), DateTimeOffset.UtcNow);
        checkedSave.Succeeded.Should().BeFalse();
        checkedSave.Conflict.Should().BeFalse();

        // Force (null expected) recreates: the keep-my-copy path after an external delete.
        var forced = await _writer.SaveFileAsync("src/gone.txt", Bytes("recreated"), expectedLastWriteUtc: null);
        forced.Succeeded.Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "src", "gone.txt")).Should().Be("recreated");
    }

    [Fact]
    public async Task Save_preserves_a_utf8_bom()
    {
        var target = Path.Combine(_root, "src", "bom.txt");
        File.WriteAllBytes(target, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("with bom")]);

        (await _writer.SaveFileAsync("src/bom.txt", Bytes("changed"), null)).Succeeded.Should().BeTrue();

        var bytes = File.ReadAllBytes(target);
        bytes.Take(3).Should().Equal([0xEF, 0xBB, 0xBF]);
        System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Should().Be("changed");
    }

    [Fact]
    public async Task Save_rejects_paths_outside_the_root()
    {
        var result = await _writer.SaveFileAsync("../outside.txt", Bytes("x"), null);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("outside");
    }

    // ---- RenameAsync ----

    [Fact]
    public async Task Rename_moves_a_file_in_place_and_rejects_collisions()
    {
        (await _writer.RenameAsync("src/a.txt", "b.txt")).Succeeded.Should().BeTrue();
        File.Exists(Path.Combine(_root, "src", "b.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "src", "a.txt")).Should().BeFalse();

        File.WriteAllText(Path.Combine(_root, "src", "c.txt"), "c");
        var collision = await _writer.RenameAsync("src/b.txt", "c.txt");
        collision.Succeeded.Should().BeFalse();
        collision.Error.Should().Contain("already exists");
    }

    [Fact]
    public async Task Rename_works_for_directories_and_validates_names()
    {
        Directory.CreateDirectory(Path.Combine(_root, "olddir"));

        (await _writer.RenameAsync("olddir", "newdir")).Succeeded.Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "newdir")).Should().BeTrue();

        (await _writer.RenameAsync("newdir", "bad/name")).Succeeded.Should().BeFalse();
        (await _writer.RenameAsync("../outside", "x")).Succeeded.Should().BeFalse();
    }
}
