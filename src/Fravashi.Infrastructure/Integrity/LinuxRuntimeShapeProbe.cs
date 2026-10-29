namespace Fravashi.Infrastructure.Integrity;

/// <summary>
/// The real runtime-shape probe used in the container (F3 / ADR 0007). Reads environment variables, the effective
/// uid from <c>/proc/self/status</c>, and per-mount existence/writability/fstype from the filesystem +
/// <c>/proc/self/mountinfo</c>. Every read is defensive: on a non-Linux host (developer machine) <c>/proc</c> is
/// absent, so uid/fstype come back null and the evaluator treats those as report-only - the app never fails closed
/// off a missing <c>/proc</c>.
/// </summary>
public sealed class LinuxRuntimeShapeProbe : IRuntimeShapeProbe
{
    public string? GetEnv(string name) => Environment.GetEnvironmentVariable(name);

    public int? TryGetEffectiveUid()
    {
        try
        {
            if (!File.Exists("/proc/self/status"))
            {
                return null;
            }

            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    continue;
                }

                // "Uid:\t<real>\t<effective>\t<saved>\t<fs>" - the real uid (first field) is what the app runs as.
                var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out var uid))
                {
                    return uid;
                }
            }
        }
        catch (IOException)
        {
            // Unreadable /proc - reported as unknown (null), never a fail-closed.
        }

        return null;
    }

    public MountObservation ObserveMount(string path)
    {
        var exists = Directory.Exists(path);
        var writable = exists && IsWritable(path);
        return new MountObservation(exists, writable, exists ? FsTypeOf(path) : null);
    }

    private static bool IsWritable(string path)
    {
        var probe = Path.Combine(path, $".fravashi-integrity-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "1");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (IOException)
            {
                // A leftover probe file is harmless.
            }
        }
    }

    /// <summary>
    /// The filesystem type mounted at (or covering) <paramref name="path"/>, from <c>/proc/self/mountinfo</c>.
    /// Chooses the longest mount-point prefix that contains the path, so <c>/tmp</c>'s tmpfs is recognised even
    /// when the root is a different fs. Null when <c>/proc</c> is unavailable or no mount matches.
    /// </summary>
    private static string? FsTypeOf(string path)
    {
        try
        {
            if (!File.Exists("/proc/self/mountinfo"))
            {
                return null;
            }

            string? best = null;
            var bestLen = -1;
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                // mountinfo: fields... <mountPoint> ... " - " <fsType> <source> <superopts>
                var dash = line.IndexOf(" - ", StringComparison.Ordinal);
                if (dash < 0)
                {
                    continue;
                }

                var left = line[..dash].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var right = line[(dash + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (left.Length < 5 || right.Length < 1)
                {
                    continue;
                }

                var mountPoint = left[4];
                var fsType = right[0];
                if ((path == mountPoint || path.StartsWith(mountPoint.TrimEnd('/') + "/", StringComparison.Ordinal))
                    && mountPoint.Length > bestLen)
                {
                    best = fsType;
                    bestLen = mountPoint.Length;
                }
            }

            return best;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
