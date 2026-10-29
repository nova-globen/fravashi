using AwesomeAssertions;
using Xunit;

namespace Fravashi.WebSetup.Tests;

public sealed class EngineWorkspaceTests : IDisposable
{
    private readonly Scratch _scratch = new();

    [Fact]
    public void The_embedded_engine_is_the_committed_file_byte_for_byte()
    {
        EngineWorkspace.EngineBytes().Should().Equal(File.ReadAllBytes(Repository.EnginePath),
            "the EXE must run the engine that sits beside it in git, never a copy that drifted");
    }

    [Fact]
    public void A_workspace_writes_the_engine_into_a_fresh_folder_of_its_own()
    {
        using var workspace = EngineWorkspace.Create(_scratch.Folder);

        Path.GetDirectoryName(workspace.ScriptPath).Should().Be(workspace.Folder);
        Path.GetFileName(workspace.ScriptPath).Should().Be("webinstall.ps1");
        File.ReadAllBytes(workspace.ScriptPath).Should().Equal(File.ReadAllBytes(Repository.EnginePath));
        Directory.GetFiles(workspace.Folder).Should().ContainSingle("the engine is all a run needs");
    }

    [Fact]
    public void Disposing_removes_the_engine_folder()
    {
        var workspace = EngineWorkspace.Create(_scratch.Folder);
        var folder = workspace.Folder;

        workspace.Dispose();

        Directory.Exists(folder).Should().BeFalse("the engine does not outlive the run");
    }

    [Fact]
    public void A_clean_removal_reports_nothing_left()
    {
        var workspace = EngineWorkspace.Create(_scratch.Folder);

        workspace.Remove().Should().BeNull();
        Directory.Exists(workspace.Folder).Should().BeFalse();
    }

    [UnwritableFolderFact]
    public void A_folder_that_could_not_be_removed_is_named_rather_than_passed_over()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var workspace = EngineWorkspace.Create(_scratch.Folder);
        // A folder its owner may not write: nothing inside it can be deleted.
        File.SetUnixFileMode(workspace.Folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var leftover = workspace.Remove();

            leftover.Should().NotBeNull();
            leftover.Should().Contain("Left behind:").And.Contain(workspace.Folder);
        }
        finally
        {
            File.SetUnixFileMode(workspace.Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            workspace.Dispose();
        }
    }

    [Fact]
    public void Two_workspaces_never_share_a_folder()
    {
        using var first = EngineWorkspace.Create(_scratch.Folder);
        using var second = EngineWorkspace.Create(_scratch.Folder);

        first.Folder.Should().NotBe(second.Folder);
    }

    public void Dispose() => _scratch.Dispose();
}
