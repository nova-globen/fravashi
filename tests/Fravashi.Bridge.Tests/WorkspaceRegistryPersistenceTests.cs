using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

/// <summary>
/// Backlog 14: a fresh fravashi.exe process must still serve the workspace ids the studio's recents
/// hold. A SECOND registry over the same store file is exactly that fresh process.
/// </summary>
public sealed class WorkspaceRegistryPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-bridge-tests", Guid.NewGuid().ToString("N"));

    private readonly string _storePath;

    public WorkspaceRegistryPersistenceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha", "sub"));
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        _storePath = Path.Combine(_root, WorkspaceRegistryStore.FileName);
    }

    [Fact]
    public void Register_persists_the_folder_and_a_fresh_registry_rehydrates_the_same_id()
    {
        var first = new WorkspaceRegistry(new WorkspaceRegistryStore(_storePath));
        var alpha = first.Register(Path.Combine(_root, "alpha"));
        first.Register(Path.Combine(_root, "beta"));

        var restarted = new WorkspaceRegistry(new WorkspaceRegistryStore(_storePath));
        restarted.Rehydrate().Should().Be(2);

        restarted.TryGet(alpha.Id).Should().NotBeNull();
        restarted.ResolvePath(alpha.Id, "sub").Should().Be(Path.Combine(alpha.RootPath, "sub"));
    }

    [Fact]
    public void Rehydrate_prunes_a_folder_that_is_gone()
    {
        var first = new WorkspaceRegistry(new WorkspaceRegistryStore(_storePath));
        first.Register(Path.Combine(_root, "alpha"));
        var beta = first.Register(Path.Combine(_root, "beta"));
        Directory.Delete(Path.Combine(_root, "beta"), recursive: true);

        var restarted = new WorkspaceRegistry(new WorkspaceRegistryStore(_storePath));
        restarted.Rehydrate().Should().Be(1);

        restarted.List().Should().ContainSingle().Which.Name.Should().Be("alpha");
        restarted.TryGet(beta.Id).Should().BeNull();
        // The prune is written back, so the next start does not retry the deleted folder.
        new WorkspaceRegistryStore(_storePath).Load().Should().ContainSingle()
            .Which.Should().NotContain("beta");
    }

    // ---- A folder that is merely out of reach is NOT a folder that is gone ----

    /// <summary>
    /// Stands in for a volume that is not attached (Z:\ not mapped yet, the external disk not
    /// plugged in): no test can unmap a real drive, and Directory.Exists reports exactly the same
    /// false for that as for a deleted folder. WorkspacePathProbeTests pins the real classification.
    /// </summary>
    private static Func<string, WorkspacePathState> OfflineVolume(string folderPath)
        => path => string.Equals(path, folderPath, StringComparison.OrdinalIgnoreCase)
            ? WorkspacePathState.Unreachable
            : WorkspacePathProbe.Probe(path);

    [Fact]
    public void Rehydrate_keeps_a_grant_whose_volume_is_not_attached()
    {
        var beta = Path.Combine(_root, "beta");
        var first = new WorkspaceRegistry(new WorkspaceRegistryStore(_storePath));
        first.Register(Path.Combine(_root, "alpha"));
        first.Register(beta);

        // fravashi.exe starts before the drive is mapped.
        var restarted = new WorkspaceRegistry(new WorkspaceRegistryStore(_storePath), OfflineVolume(beta));
        restarted.Rehydrate().Should().Be(1, "only alpha could be granted this start");

        // The folder is fine, so the remembered list must still hold it.
        new WorkspaceRegistryStore(_storePath).Load().Should().Contain(beta);

        // And once the drive IS there, the same store rehydrates it with the same id as ever.
        var later = new WorkspaceRegistry(new WorkspaceRegistryStore(_storePath));
        later.Rehydrate().Should().Be(2);
        later.List().Select(w => w.Name).Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public void A_later_register_does_not_forget_the_folder_that_was_out_of_reach()
    {
        var beta = Path.Combine(_root, "beta");
        var gamma = Path.Combine(_root, "gamma");
        Directory.CreateDirectory(gamma);
        new WorkspaceRegistry(new WorkspaceRegistryStore(_storePath)).Register(beta);

        var restarted = new WorkspaceRegistry(new WorkspaceRegistryStore(_storePath), OfflineVolume(beta));
        restarted.Rehydrate();
        // Every Register rewrites the whole file, which is the second way the row could vanish.
        restarted.Register(gamma);

        new WorkspaceRegistryStore(_storePath).Load().Should().BeEquivalentTo([gamma, beta]);
    }

    [Fact]
    public void Registering_an_unreachable_folder_is_not_a_not_found()
    {
        var beta = Path.Combine(_root, "beta");
        var registry = new WorkspaceRegistry(new WorkspaceRegistryStore(_storePath), OfflineVolume(beta));

        var act = () => registry.Register(beta);

        // DirectoryNotFoundException is what the bridge turns into a 404, which the studio turns
        // into "not on this machine any more" over a destructive button. This must not be that,
        // and the type system has to carry the difference (both are IOExceptions).
        act.Should().Throw<WorkspaceUnreachableException>().Which.FolderPath.Should().Be(beta);
        typeof(WorkspaceUnreachableException).Should().NotBeAssignableTo<DirectoryNotFoundException>();
    }

    [Fact]
    public void Rehydrate_with_a_corrupt_file_or_no_store_is_a_quiet_no_op()
    {
        File.WriteAllText(_storePath, "not json");

        var registry = new WorkspaceRegistry(new WorkspaceRegistryStore(_storePath));
        registry.Rehydrate().Should().Be(0);
        registry.List().Should().BeEmpty();

        new WorkspaceRegistry().Rehydrate().Should().Be(0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
