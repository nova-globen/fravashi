using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Fravashi.Launcher;

/// <summary>
/// Names every thread alive in this process, as the OS knows them: .NET publishes
/// <see cref="Thread.Name"/> as the OS thread description (Windows: <c>SetThreadDescription</c>,
/// Linux: the task's <c>comm</c>, 15 characters), and the runtime names its own threads the same way
/// (".NET TP Worker", ".NET Timer", ".NET System Events", ...). The exit watchdog prints this list
/// when it fires (backlog 5), so a report of a process that would not end names the thread instead
/// of guessing at it. There is no managed API for another thread's name, hence the OS route.
/// </summary>
public static class LiveThreads
{
    private const uint ThreadQueryLimitedInformation = 0x0800;

    /// <summary>One entry per live thread: <c>0x1d30 "fravashi main"</c>, or <c>(unnamed)</c> in place of the name.</summary>
    public static IReadOnlyList<string> Describe()
    {
        var entries = new List<string>();
        if (OperatingSystem.IsLinux())
        {
            foreach (var task in Directory.GetDirectories("/proc/self/task"))
            {
                var id = Path.GetFileName(task);
                entries.Add(Format(id, ReadLinuxName(task)));
            }

            return entries;
        }

        using var process = Process.GetCurrentProcess();
        foreach (ProcessThread thread in process.Threads)
        {
            var name = OperatingSystem.IsWindows() ? ReadWindowsName((uint)thread.Id) : null;
            entries.Add(Format($"0x{thread.Id:x}", name));
        }

        return entries;
    }

    private static string Format(string id, string? name) =>
        string.IsNullOrWhiteSpace(name) ? $"{id} (unnamed)" : $"{id} \"{name}\"";

    private static string? ReadLinuxName(string taskDirectory)
    {
        try
        {
            return File.ReadAllText(Path.Combine(taskDirectory, "comm")).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadWindowsName(uint threadId)
    {
        var handle = OpenThread(ThreadQueryLimitedInformation, false, threadId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (GetThreadDescription(handle, out var description) < 0 || description == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(description);
            }
            finally
            {
                LocalFree(description);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll")]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
