using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog 375: <c>fravashi uninstall</c> removes what an install left, under the current AND former names, keeps the
/// data unless asked, copies it out before removing it, and never stops at a failed step. The engine and the Windows
/// parts are fakes here; the real removal is proven on the test VM.
/// </summary>
public sealed class UninstallerTests
{
    private static readonly UninstallTarget Target = new(
        @"C:\Users\ada\AppData\Local\Programs\Fravashi", @"C:\Users\ada\AppData\Local\Fravashi",
        "fravashi", "fravashi:latest", @"C:\Users\ada\Documents");

    private static readonly DateTimeOffset At = new(2026, 10, 30, 18, 5, 0, TimeSpan.Zero);

    [Fact]
    public void It_removes_the_current_and_former_engine_objects_and_keeps_the_data_and_the_workspaces_volume()
    {
        var engine = new Engine();
        engine.Containers.UnionWith(["fravashi", FormerInstall.ContainerName]);
        engine.Running.Add("fravashi");
        engine.Images.UnionWith(["fravashi:latest", "localhost/fravashi:latest", FormerInstall.Image]);
        engine.Networks.UnionWith([ContainerHardening.StudioNetwork, FormerInstall.StudioNetwork]);
        engine.Volumes.UnionWith([LauncherDefaults.WorkspacesVolume]);
        var host = new Host();
        var said = new List<string>();

        var ok = new Uninstaller(engine, host, said.Add, () => At).Run(Target, removeData: false);

        ok.Should().BeTrue(string.Join("\n", said));
        engine.Stopped.Should().Equal("fravashi");
        engine.Containers.Should().BeEmpty();
        engine.Images.Should().BeEmpty();
        engine.Networks.Should().BeEmpty();
        engine.Volumes.Should().BeEquivalentTo([LauncherDefaults.WorkspacesVolume], "the agents' copies go only with the data");
        host.Calls.Should().Equal(
            "close", "context menu", "shortcuts", @"path C:\Users\ada\AppData\Local\Programs\Fravashi",
            // Joined the way the code joins it, so the expectation holds on the Linux CI as well as on Windows.
            "firewall " + Path.Combine(Target.ProgramFolder, "fravashi.exe"), "uninstall entry",
            @"program folder C:\Users\ada\AppData\Local\Programs\Fravashi");
        said.Should().Contain(l => l.StartsWith("data folder: kept", StringComparison.Ordinal) && l.Contains("--remove-data"));
    }

    [Fact]
    public void Remove_data_copies_it_out_first_then_removes_it_and_the_volumes()
    {
        var engine = new Engine();
        engine.Volumes.UnionWith([LauncherDefaults.WorkspacesVolume, FormerInstall.WorkspacesVolume]);
        var host = new Host();

        var ok = new Uninstaller(engine, host, _ => { }, () => At).Run(Target, removeData: true);

        ok.Should().BeTrue();
        engine.Volumes.Should().BeEmpty();
        var copy = host.Calls.IndexOf($"copy {Target.DataFolder} -> {Path.Combine(Target.BackupFolder, "fravashi-data-20261030-180500")}");
        var remove = host.Calls.IndexOf(@"remove data C:\Users\ada\AppData\Local\Fravashi");
        copy.Should().BeGreaterThanOrEqualTo(0);
        remove.Should().BeGreaterThan(copy, "the data is copied out before it is removed");
    }

    [Fact]
    public void A_failed_copy_keeps_the_data_and_says_so()
    {
        var host = new Host { CopyFails = true };
        var said = new List<string>();

        var ok = new Uninstaller(new Engine(), host, said.Add, () => At).Run(Target, removeData: true);

        ok.Should().BeFalse();
        host.Calls.Should().NotContain(c => c.StartsWith("remove data", StringComparison.Ordinal));
        said.Should().Contain(l => l.StartsWith("data copy: FAILED", StringComparison.Ordinal));
        said.Should().Contain(@"data folder: KEPT, because the copy did not complete (C:\Users\ada\AppData\Local\Fravashi).");
        host.Calls.Should().Contain(@"program folder C:\Users\ada\AppData\Local\Programs\Fravashi", "the rest still runs");
    }

    [Fact]
    public void A_failed_step_is_said_and_the_rest_still_runs()
    {
        var host = new Host { ShortcutsFail = true };
        var said = new List<string>();

        var ok = new Uninstaller(new Engine(), host, said.Add, () => At).Run(Target, removeData: false);

        ok.Should().BeFalse();
        said.Should().Contain("shortcuts: FAILED (access denied)");
        host.Calls.Should().Contain("uninstall entry").And.Contain(@"program folder C:\Users\ada\AppData\Local\Programs\Fravashi");
    }

    [Fact]
    public void Without_the_engine_the_engine_objects_are_left_and_said_and_everything_else_is_removed()
    {
        var said = new List<string>();
        var host = new Host();

        var ok = new Uninstaller(new Engine { Up = false }, host, said.Add, () => At).Run(Target, removeData: false);

        ok.Should().BeFalse();
        said.Should().Contain(l => l.StartsWith("container engine (fake): not available", StringComparison.Ordinal));
        host.Calls.Should().Contain("context menu").And.Contain("uninstall entry");
    }

    [Fact]
    public void The_lists_cover_both_names_and_never_repeat_one()
    {
        Uninstaller.Containers(Target).Should().Equal("fravashi", FormerInstall.ContainerName, FormerInstall.VpnContainerName);
        Uninstaller.Images(Target).Should().Equal("fravashi:latest", "localhost/fravashi:latest", FormerInstall.Image, "localhost/" + FormerInstall.Image);
        Uninstaller.Containers(Target with { ContainerName = FormerInstall.ContainerName })
            .Should().Equal(FormerInstall.ContainerName, FormerInstall.VpnContainerName);
    }

    private sealed class Host : IUninstallHost
    {
        public List<string> Calls { get; } = [];
        public bool CopyFails { get; init; }
        public bool ShortcutsFail { get; init; }

        public string CloseResident() { Calls.Add("close"); return "closed"; }
        public string RemoveContextMenu() { Calls.Add("context menu"); return "removed"; }

        public string RemoveShortcuts()
        {
            Calls.Add("shortcuts");
            return ShortcutsFail ? throw new UnauthorizedAccessException("access denied") : "removed";
        }

        public string RemoveFromUserPath(string programFolder) { Calls.Add("path " + programFolder); return "removed"; }
        public string RemoveFirewallRules(string program) { Calls.Add("firewall " + program); return "removed"; }

        public string CopyData(string dataFolder, string destination)
        {
            Calls.Add($"copy {dataFolder} -> {destination}");
            return CopyFails ? throw new IOException("disk full") : "copied";
        }

        public string RemoveDataFolder(string dataFolder) { Calls.Add("remove data " + dataFolder); return "removed"; }
        public string RemoveUninstallEntry() { Calls.Add("uninstall entry"); return "removed"; }
        public string ScheduleProgramFolderRemoval(string programFolder) { Calls.Add("program folder " + programFolder); return "scheduled"; }
    }

    /// <summary>An engine that holds named objects, enough to see what an uninstall removes.</summary>
    private sealed class Engine : IContainerRuntime
    {
        public bool Up { get; init; } = true;
        public HashSet<string> Containers { get; } = [];
        public HashSet<string> Running { get; } = [];
        public HashSet<string> Images { get; } = [];
        public HashSet<string> Networks { get; } = [];
        public HashSet<string> Volumes { get; } = [];
        public List<string> Stopped { get; } = [];

        public string Name => "fake";
        public bool IsAvailable() => Up;
        public bool ImageExists(string image) => Images.Contains(image);
        public IReadOnlyList<string> LoadImage(string tarPath) => [];
        public bool ContainerExists(string containerName) => Containers.Contains(containerName);
        public bool ContainerRunning(string containerName) => Running.Contains(containerName);
        public void StartContainer(string containerName) => Running.Add(containerName);

        public void StopContainer(string containerName)
        {
            Stopped.Add(containerName);
            Running.Remove(containerName);
        }

        public void RemoveContainer(string containerName) => Containers.Remove(containerName);
        public void RemoveImage(string imageRef) => Images.Remove(imageRef);
        public void EnsureVolume(string volumeName) => Volumes.Add(volumeName);
        public bool RemoveNetwork(string networkName) => Networks.Remove(networkName);
        public bool RemoveVolume(string volumeName) => Volumes.Remove(volumeName);
        public void RunContainer(ContainerRunSpec spec) { }
        public string? ImageVersion(string image) => null;
        public string? ResolveImageDigest(string image) => null;
        public string GetLogs(string containerName, int tail) => "";
        public string? ImageReleaseVersion(string image) => null;
    }
}
