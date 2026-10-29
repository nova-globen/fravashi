using Fravashi.Infrastructure.AgentRuntimes;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// What the studio's own container is known to lack (backlog 295): not Windows, no PowerShell on PATH, no
/// container engine on PATH, and a live test tier's own environment variable, unless it reaches the test run.
/// One place, one fact per entry, each a check against THIS process, never against a test's source or a test
/// assembly, so it holds whether or not any particular test exists and changes the moment the image actually does.
/// <para>
/// The four facts answer for two different things on purpose, and neither is "the studio's own wider
/// environment": <see cref="EnvVarIsSet"/> answers whether a variable is INHERITED by an agent CLI child
/// (backlog 72, backlog 108: no <c>FRAVASHI_</c> variable is, however the image bakes one for the studio's own
/// ASP.NET Core process, which is never put through <see cref="AgentProcessEnvironment.Restrict"/>), not whether
/// an agent could ever have one set (it can, explicitly, on a command it runs, which this cannot see either
/// way). <see cref="IsWindows"/>, <see cref="HasPowerShellOnPath"/> and <see cref="HasContainerEngineOnPath"/>
/// answer for THIS PROCESS's own platform and PATH, which an agent child inherits (Restrict removes names, not
/// PATH's own directory list) with a project Node prefix added; they do not account for a project variable added
/// only for agents (backlog 154) narrowing that further.
/// </para>
/// </summary>
public static class ContainerCapabilities
{
    /// <summary>Whether this process is running on Windows.</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>Whether a PowerShell interpreter (<c>pwsh</c>, then <c>powershell</c>) is on this process's PATH.</summary>
    public static bool HasPowerShellOnPath => HasPowerShellOnPathIn(Environment.GetEnvironmentVariable("PATH"));

    /// <summary>The same fact, told the PATH value to read rather than reading the process-wide one: a test's seam,
    /// so asserting it never mutates <c>Environment</c> state a parallel test class could be reading at the same time.</summary>
    public static bool HasPowerShellOnPathIn(string? path) => IsOnPath("pwsh", path) || IsOnPath("powershell", path);

    /// <summary>Whether a container engine (<c>podman</c>, then <c>docker</c>) is on this process's PATH.</summary>
    public static bool HasContainerEngineOnPath => HasContainerEngineOnPathIn(Environment.GetEnvironmentVariable("PATH"));

    /// <summary>The same fact, told the PATH value to read; see <see cref="HasPowerShellOnPathIn"/>.</summary>
    public static bool HasContainerEngineOnPathIn(string? path) => IsOnPath("podman", path) || IsOnPath("docker", path);

    /// <summary>
    /// Whether a named environment variable is INHERITED by an agent CLI child with a non-blank value: allowed
    /// by <see cref="AgentProcessEnvironment.IsAllowed"/> (what every agent CLI child's environment is restricted
    /// to on starting, backlog 72/108) AND actually set in this process. This answers inheritance only: an agent
    /// can still set a variable explicitly on a command IT runs (CLAUDE.md tells it to, for the real-browser
    /// tier), which this cannot see either way, so a tier gated on one is read as unclear rather than claimed
    /// either way by <c>CommitContainerCoverage</c> when its own reason text says the container can run it. A
    /// project can also hand its agents variables of its own (backlog 154), which this does not see either; such
    /// a variable is reported as not inherited when it really is handed to the agent some other way, the
    /// conservative direction (a false "not run" beside the verdict, never a false claim of coverage).
    /// </summary>
    public static bool EnvVarIsSet(string name) =>
        AgentProcessEnvironment.IsAllowed(name) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    /// <summary>Whether a file exists at an absolute path, exactly as a test's own condition names it.</summary>
    public static bool FileExists(string path) => File.Exists(path);

    /// <summary>
    /// Whether an executable named <paramref name="name"/> is in a directory on this process's PATH. A stat per
    /// PATH entry (plus Windows' usual executable extensions there), never a process start: the studio asks
    /// whether something IS there, not whether running it would answer.
    /// </summary>
    public static bool IsOnPath(string name) => IsOnPath(name, Environment.GetEnvironmentVariable("PATH"));

    /// <summary>The same fact, told the PATH value to read; see <see cref="HasPowerShellOnPathIn"/>.</summary>
    public static bool IsOnPath(string name, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string[] extensions = OperatingSystem.IsWindows() ? [string.Empty, ".exe", ".cmd", ".bat"] : [string.Empty];
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, name + extension)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // A PATH entry can hold characters Path.Combine refuses; such an entry has nothing on it either way.
                }
            }
        }

        return false;
    }
}
