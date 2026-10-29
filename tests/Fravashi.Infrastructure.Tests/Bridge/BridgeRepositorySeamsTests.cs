using System.Text;
using Fravashi.Application.Git;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Repository;
using AwesomeAssertions;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Bridge;

/// <summary>Reader, writer, and trash over a real hosted bridge (plan 2b-i Task 5).</summary>
public sealed class BridgeRepositorySeamsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-remote-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "fravashi-remote-tests", Guid.NewGuid().ToString("N") + "-data");
    private HostedBridge _bridge = null!;
    private BridgeRepositoryReader _reader = null!;
    private BridgeRepositoryWriter _writer = null!;
    private BridgeRepositoryTrash _trash = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "class App { }\n");
        File.WriteAllText(Path.Combine(_root, "readme.md"), "# hi\n");
        Directory.CreateDirectory(_dataDir);

        _bridge = await HostedBridge.StartAsync(_root);
        var client = _bridge.CreateClient();
        var gate = new RepositoryWriteGate();
        var git = Substitute.For<IGitService>();
        git.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("main");
        _reader = new BridgeRepositoryReader(client);
        _writer = new BridgeRepositoryWriter(client, gate);
        _trash = new BridgeRepositoryTrash(client, gate, git, new DataDirectory(_dataDir));
    }

    [Fact]
    public async Task Reader_TreeHidesDeniedDirs_AndOrdersDirsFirst()
    {
        var root = await _reader.GetTreeAsync();

        root.Children.Select(c => c.Name).Should().ContainInOrder("src", "readme.md");
        root.Children.Should().NotContain(c => c.Name == ".git" || c.Name == "bin");
        root.Name.Should().Be(Path.GetFileName(_root));
        _reader.RootPath.Should().Be(_root);
    }

    [Fact]
    public async Task Reader_ReadsFilesWithLanguageAndStamp_AndTruncatesOversize()
    {
        var file = await _reader.ReadFileAsync("src/app.cs");
        file!.Content.Should().Contain("class App");
        file.Language.Should().Be("csharp");
        file.LastWriteUtc.Should().NotBeNull();

        File.WriteAllBytes(Path.Combine(_root, "big.bin"), new byte[600 * 1024]);
        var big = await _reader.ReadFileAsync("big.bin");
        big!.IsTruncated.Should().BeTrue();
        big.Content.Should().Be("(file too large to preview)");

        (await _reader.ReadFileAsync("absent.cs")).Should().BeNull();
    }

    [Fact]
    public async Task Writer_CreateRenameSaveConflictAndCopySuffix()
    {
        (await _writer.CreateFileAsync("src", "new.cs")).Succeeded.Should().BeTrue();
        (await _writer.CreateFileAsync("src", "new.cs")).Error.Should().Contain("already exists");
        (await _writer.CreateFileAsync("src", "ba/d")).Error.Should().Contain("not valid");

        using (var body = new MemoryStream(Encoding.UTF8.GetBytes("v1")))
        {
            var save = await _writer.SaveFileAsync("src/new.cs", body, expectedLastWriteUtc: null);
            save.Succeeded.Should().BeTrue();
            save.LastWriteUtc.Should().NotBeNull();

            using var stale = new MemoryStream(Encoding.UTF8.GetBytes("v2"));
            var conflicted = await _writer.SaveFileAsync(
                "src/new.cs", stale, save.LastWriteUtc!.Value.AddMinutes(-30));
            conflicted.Conflict.Should().BeTrue();
        }

        (await _writer.RenameAsync("src/new.cs", "renamed.cs")).Succeeded.Should().BeTrue();
        File.Exists(Path.Combine(_root, "src", "renamed.cs")).Should().BeTrue();

        (await _writer.CopyAsync("src/renamed.cs", "src")).Succeeded.Should().BeTrue();
        File.Exists(Path.Combine(_root, "src", "renamed copy.cs")).Should().BeTrue();
    }

    [Fact]
    public async Task Trash_ZipsBeforeDeleting_AndRestoresAllOrNothing()
    {
        var deleted = await _trash.DeleteToTrashAsync(["src"]);
        deleted.Succeeded.Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "src")).Should().BeFalse();
        File.Exists(Path.Combine(_dataDir, "trash", deleted.ZipFileName!)).Should().BeTrue();

        var restored = await _trash.RestoreAsync(deleted.ZipFileName!);
        restored.Succeeded.Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "src", "app.cs")).Should().Contain("class App");

        // Restoring again collides and must refuse without touching anything.
        (await _trash.RestoreAsync(deleted.ZipFileName!)).Error.Should().Contain("already exists");
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        foreach (var dir in new[] { _root, _dataDir })
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
