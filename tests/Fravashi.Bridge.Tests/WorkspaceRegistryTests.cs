using Fravashi.Bridge;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

public sealed class WorkspaceRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-bridge-tests", Guid.NewGuid().ToString("N"));
    private readonly WorkspaceRegistry _registry = new();

    public WorkspaceRegistryTests() => Directory.CreateDirectory(Path.Combine(_root, "sub"));

    [Fact]
    public void Register_IsIdempotentPerCanonicalPath()
    {
        var first = _registry.Register(_root);
        var second = _registry.Register(_root + Path.DirectorySeparatorChar);

        second.Id.Should().Be(first.Id);
        _registry.List().Should().ContainSingle();
        first.Name.Should().Be(Path.GetFileName(_root));
    }

    [Fact]
    public void Register_MissingFolder_Throws()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var act = () => _registry.Register(missing);
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void ResolvePath_ReturnsPathsInsideTheWorkspace()
    {
        var ws = _registry.Register(_root);

        _registry.ResolvePath(ws.Id, null).Should().Be(ws.RootPath);
        _registry.ResolvePath(ws.Id, "sub").Should().Be(Path.Combine(ws.RootPath, "sub"));
        _registry.ResolvePath(ws.Id, "sub/deeper.txt").Should().Be(
            Path.Combine(ws.RootPath, "sub", "deeper.txt"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("/etc/passwd")]
    public void ResolvePath_RefusesEscapes(string relative)
    {
        // Escapes on EVERY platform: these four run on the Linux CI runner as well as the Windows host.
        var ws = _registry.Register(_root);
        var act = () => _registry.ResolvePath(ws.Id, relative);
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [WindowsPathTheory]
    [InlineData("C:/Windows/system32")]
    [InlineData("\\\\server\\share")]
    public void ResolvePath_RefusesWindowsRootedEscapes(string relative)
    {
        // Backlog 96, decision "gate the two cases": a drive-letter path and a UNC share are ROOTED
        // only under Windows path rules, which are the rules the bridge really runs under (fravashi.exe hosts
        // it on the Windows host). On Linux both spellings are ordinary relative names that land inside the
        // workspace, so nothing escapes there and asserting a refusal would test the wrong platform.
        var ws = _registry.Register(_root);
        var act = () => _registry.ResolvePath(ws.Id, relative);
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void ResolvePath_UnknownWorkspace_Throws()
    {
        var act = () => _registry.ResolvePath("deadbeefdeadbeef", "x");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Unregister_DropsTheWorkspaceFromListAndTryGet()
    {
        var ws = _registry.Register(_root);

        _registry.Unregister(ws.Id);

        _registry.List().Should().BeEmpty();
        _registry.TryGet(ws.Id).Should().BeNull();
    }

    [Fact]
    public void Unregister_UnknownId_IsANoOp()
    {
        var ws = _registry.Register(_root);

        _registry.Unregister("deadbeefdeadbeef");

        _registry.List().Should().ContainSingle().Which.Id.Should().Be(ws.Id);
    }

    [Fact]
    public void Unregister_PersistsTheDrop()
    {
        var storePath = Path.Combine(_root, "store", WorkspaceRegistryStore.FileName);
        var withStore = new WorkspaceRegistry(new WorkspaceRegistryStore(storePath));
        var ws = withStore.Register(_root);

        withStore.Unregister(ws.Id);

        var restarted = new WorkspaceRegistry(new WorkspaceRegistryStore(storePath));
        restarted.Rehydrate().Should().Be(0);
        restarted.List().Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// A theory whose cases are only escapes under Windows path rules (backlog 96). It skips elsewhere with the
/// reason, set in the constructor because <c>Assert.Skip</c> needs xunit v3.
/// </summary>
internal sealed class WindowsPathTheoryAttribute : TheoryAttribute
{
    public WindowsPathTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows path semantics: a drive-letter path and a UNC share are rooted only on Windows, "
                + "where the bridge runs. On this platform they are ordinary relative names inside the workspace.";
        }
    }
}
