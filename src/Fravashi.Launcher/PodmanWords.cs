using System.Globalization;
using System.Text.RegularExpressions;
using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// What the launcher says about the two Podman machine settings that stop Fravashi on a stock install (backlog 252),
/// in one place so the launcher log, fravashi.exe's boxes and the docs say the same. Both were met on the test VM on
/// 2026-10-08 and each fix below is the one measured there. The launcher only SAYS them: changing the machine,
/// <c>.wslconfig</c> or WSL is the person's act (and whether the installer should is the owner's open decision).
/// </summary>
public static partial class PodmanWords
{
    /// <summary>
    /// crun refusing a controller its cgroup was never given. Recorded: "crun: controller `pids` is not available under
    /// /sys/fs/cgroup/non-systemd/machine.slice/libpod-...scope/container/cgroup.controllers: OCI runtime error". Any
    /// controller name, because the empty <c>subtree_control</c> refuses whichever one crun asks for first.
    /// </summary>
    [GeneratedRegex(@"controller `\w+` is not available", RegexOptions.CultureInvariant)]
    private static partial Regex ControllerNotAvailable();

    /// <summary>
    /// The same failure with a memory limit set. Recorded: "open `memory.max` for writing: No such file or directory".
    /// Not the bare word <c>memory.max</c>, which other messages can carry without being this.
    /// </summary>
    private const string MemoryMaxNotWritable = "open `memory.max` for writing";

    /// <summary>
    /// The advice for a failed <c>run</c> or <c>start</c> whose engine text is WSL 3.0's per-distro cgroup isolation,
    /// or null when the text is not that failure or the engine is not Podman (the fix is about the Podman machine).
    /// </summary>
    public static string? CgroupIsolation(string cli, string engineSaid)
    {
        ArgumentNullException.ThrowIfNull(engineSaid);
        // The CLI by name, a path to it (either separator, whatever this platform calls one) or podman.exe: one engine.
        var name = cli[(cli.LastIndexOfAny(['/', '\\']) + 1)..];
        if (!string.Equals(Path.GetFileNameWithoutExtension(name), RuntimeNames.Podman, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!ControllerNotAvailable().IsMatch(engineSaid)
            && !engineSaid.Contains(MemoryMaxNotWritable, StringComparison.Ordinal))
        {
            return null;
        }

        return "The Podman machine cannot give a container any cgroup controllers, so no container can start there, "
            + "not even one without limits. This is WSL 3.0's per-distro cgroup isolation (the engine said a "
            + "controller \"is not available\" or could not open memory.max). To fix it, put these two lines in the "
            + "file .wslconfig in your user folder (%USERPROFILE%\\.wslconfig):   [wsl2]   IsolateDistroCgroup=false   "
            + $"then run   wsl --shutdown   and   podman machine start   and start {ProductName.Short} again. None of your "
            + $"{ProductName.Short} data is affected.";
    }

    /// <summary>
    /// What to say when the studio container is running on Podman but never answered on the studio port from Windows
    /// (backlog 252). <paramref name="rootless"/> is what Podman reported for the connection the containers run under:
    /// false is the known cause, true rules it out (null is returned, because guessing another cause is not honest),
    /// and null means it could not be asked, which is said as that.
    /// </summary>
    public static string? StudioUnanswered(int port, bool? rootless)
    {
        if (rootless == true)
        {
            return null;
        }

        var lead = rootless == false
            ? $"The {ProductName.Short} container is running, but the studio never answered on http://127.0.0.1:{Port(port)} from "
                + "Windows, and the Podman machine is ROOTFUL. A rootful machine publishes a port as a firewall rule with "
                + "nothing listening, so WSL has nothing to forward to Windows. Switch the machine to rootless (rootless "
                + "publishes through a real listener):"
            : $"The {ProductName.Short} container is running, but the studio never answered on http://127.0.0.1:{Port(port)} from "
                + "Windows, and Podman could not say whether its machine is rootful. A rootful machine is the known cause "
                + "of exactly this. Check with   podman machine inspect   (it shows \"Rootful\": true for a rootful "
                + "machine), and if it is, switch it to rootless:";

        return lead + RootlessSteps;
    }

    /// <summary>The measured switch to rootless, in its measured order (the test VM, 2026-10-08).</summary>
    private const string RootlessSteps =
        "   podman machine stop   then   podman machine set --rootful=false   then   podman machine start   then   "
        + $"podman load -i <the {ProductName.Short} image tarball>   (the rootless image store starts empty)   then   "
        + "wsl --shutdown   (the rootful network and its port rules survive a machine stop and keep the port dead "
        + $"until WSL itself restarts)   then   podman machine start   and start {ProductName.Short} again. None of your {ProductName.Short} "
        + "data is affected.";

    private static string Port(int port) => port.ToString(CultureInfo.InvariantCulture);
}
