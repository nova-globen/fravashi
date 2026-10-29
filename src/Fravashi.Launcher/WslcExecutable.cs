namespace Fravashi.Launcher;

/// <summary>
/// Where <c>wslc.exe</c> is. WSL installs it at <c>%ProgramFiles%\WSL\wslc.exe</c> and does not put that folder on
/// PATH (measured on the dev host, wslc 3.0.1, 2026-10-04), so a bare <c>wslc</c> reported the engine as absent on the one
/// machine where it is the only engine. PATH still wins when it has one, so a person who added it keeps theirs.
/// </summary>
public static class WslcExecutable
{
    /// <summary>The bare name, used when it is on PATH and as the answer when nothing is found (the probe then says missing).</summary>
    public const string BareName = "wslc";

    /// <summary>
    /// Resolves the executable: <c>wslc</c> when PATH has <c>wslc.exe</c>, else the full path under Program Files when
    /// it exists, else the bare name. The environment and the file check are injectable for tests.
    /// </summary>
    public static string Resolve(
        string? pathVariable = null,
        string? programFiles = null,
        Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (exists(Path.Combine(folder, "wslc.exe")) || exists(Path.Combine(folder, BareName)))
            {
                return BareName;
            }
        }

        var root = programFiles ?? Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var installed = Path.Combine(root, "WSL", "wslc.exe");
            if (exists(installed))
            {
                return installed;
            }
        }

        return BareName;
    }
}
