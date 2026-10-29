using System.Text;
using Fravashi.Bridge;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

public sealed class BridgeFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-bridge-tests", Guid.NewGuid().ToString("N"));
    private readonly WorkspaceRegistry _registry = new();
    private readonly BridgeFileService _files;
    private readonly string _id;

    public BridgeFileServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        File.WriteAllText(Path.Combine(_root, "alpha.txt"), "alpha");
        File.WriteAllText(Path.Combine(_root, "beta", "inner.txt"), "inner");
        _id = _registry.Register(_root).Id;
        _files = new BridgeFileService(_registry);
    }

    [Fact]
    public void ListDirectory_ReturnsDirectoriesFirst_WithRelativeSlashPaths()
    {
        var entries = _files.ListDirectory(_id, null);

        entries.Select(e => e.Name).Should().ContainInOrder("beta", "alpha.txt");
        entries.Single(e => e.Name == "beta").IsDirectory.Should().BeTrue();
        entries.Single(e => e.Name == "alpha.txt").Path.Should().Be("alpha.txt");

        var nested = _files.ListDirectory(_id, "beta");
        nested.Single().Path.Should().Be("beta/inner.txt");
    }

    [Fact]
    public void A_file_named_after_a_windows_device_does_not_take_the_listing_with_it()
    {
        // 2026-09-29: Git Bash's ssh, handed Windows' null device as a known-hosts file, wrote a real file named NUL
        // into a repository root, every listing of that root threw, and the agent's copy never started. On Windows
        // only the \\?\ form can create or remove such a file; on Linux NUL is an ordinary name and simply listed.
        var device = Path.Combine(_root, "NUL");
        var literal = OperatingSystem.IsWindows() ? @"\\?\" + device : device;
        File.WriteAllText(literal, "[localhost]:2918 ssh-ed25519 AAAA");
        try
        {
            var entries = _files.ListDirectory(_id, null);

            entries.Select(e => e.Name).Should().Contain(["beta", "alpha.txt"]);
        }
        finally
        {
            File.Delete(literal);
        }
    }

    [Fact]
    public async Task Write_CreatesParents_RoundTrips_AndLeavesNoTempFile()
    {
        var payload = Encoding.UTF8.GetBytes("written-by-test");
        using (var content = new MemoryStream(payload))
        {
            await _files.WriteAsync(_id, "deep/nested/new.txt", content, CancellationToken.None);
        }

        using var read = _files.OpenRead(_id, "deep/nested/new.txt");
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);
        buffer.ToArray().Should().Equal(payload);

        Directory.EnumerateFiles(Path.Combine(_root, "deep", "nested"))
            .Should().ContainSingle(f => !f.EndsWith(".fravashi-tmp"));
    }

    [Fact]
    public async Task Write_ReplacesAnExistingFile()
    {
        using (var content = new MemoryStream(Encoding.UTF8.GetBytes("v2")))
        {
            await _files.WriteAsync(_id, "alpha.txt", content, CancellationToken.None);
        }

        File.ReadAllText(Path.Combine(_root, "alpha.txt")).Should().Be("v2");
        Directory.EnumerateFiles(_root, "*.fravashi-tmp").Should().BeEmpty();
    }

    [Fact]
    public void Stat_ReportsSizeAndKind()
    {
        _files.Stat(_id, "alpha.txt").Size.Should().Be(5);
        _files.Stat(_id, "beta").IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void Delete_RemovesADirectoryRecursively()
    {
        _files.Delete(_id, "beta");
        Directory.Exists(Path.Combine(_root, "beta")).Should().BeFalse();
    }

    [Fact]
    public void Rename_MovesAFile_AndRefusesEscapes()
    {
        _files.Rename(_id, "alpha.txt", "beta/renamed.txt");
        File.Exists(Path.Combine(_root, "beta", "renamed.txt")).Should().BeTrue();

        var act = () => _files.Rename(_id, "beta/renamed.txt", "../outside.txt");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void UnknownWorkspace_Throws()
    {
        var act = () => _files.ListDirectory("deadbeefdeadbeef", null);
        act.Should().Throw<KeyNotFoundException>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
