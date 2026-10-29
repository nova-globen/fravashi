using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>What the operating system had to say about one path.</summary>
internal enum KernelPathOutcome
{
    /// <summary>The OS resolved it, and the answer is the path it would really open.</summary>
    Resolved,

    /// <summary>Nothing is there under that name, so the OS has nothing to resolve.</summary>
    Missing,

    /// <summary>
    /// The OS would not answer: a symlink loop, a component it may not read, a name it rejects. That
    /// is "cannot tell", which every caller here has to treat as a reason to ask.
    /// </summary>
    Refused,
}

/// <summary>
/// Canonicalises a path THROUGH THE OPERATING SYSTEM, because the operating system is the thing that
/// will actually open it.
/// <para>
/// <see cref="Path.GetFullPath(string)"/> is not this. It is lexical string arithmetic whose rules
/// change with the platform it runs on, and the containment boundary above
/// (<see cref="AgentReadScope"/>) was broken four times running by the gap between its answer and the
/// kernel's. Measured in the studio's own Linux container (Ubuntu 24.04, .NET 10, 2026-08-06):
/// <c>Path.GetFullPath("/w/w1/a\b\c")</c> yields ONE component named <c>a\b\c</c>, because a backslash
/// is an ordinary filename character there, while the same string split on <c>'\\'</c> as a separator
/// yields three. Two halves of one check disagreeing about how deep a path is was the whole escape.
/// </para>
/// <para>
/// So nothing here predicts. On Linux it calls <c>realpath(3)</c>; on Windows it opens a handle and
/// asks for <c>GetFinalPathNameByHandle</c>. Both follow every symlink, junction, <c>..</c>, <c>.</c>
/// and case rule the way the kernel will when the read actually happens, and neither can disagree with
/// it about what a separator is.
/// </para>
/// <para>
/// Neither call can answer for a path that does not exist, which is why <see cref="Missing"/> is a
/// distinct outcome rather than a failure: the caller canonicalises the longest EXISTING prefix and
/// keeps the rest literal, so an absent leaf cannot be used to dodge the check. Everything else fails
/// closed as <see cref="Refused"/>.
/// </para>
/// </summary>
internal static class KernelPath
{
    // errno, Linux (asm-generic, the same on x86-64 and aarch64). Only "there is nothing there" is a
    // non-answer the caller may carry on from; ELOOP, EACCES, ENAMETOOLONG and the rest all refuse.
    private const int Enoent = 2;
    private const int Enotdir = 20;

    // Win32 error codes that mean the same thing: nothing is there to open.
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidName = 123;

    private const uint OpenExisting = 3;
    private const uint FileShareAll = 7;

    /// <summary>Lets CreateFile open a DIRECTORY handle, not just a file one.</summary>
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>Room for a long path on the first try; the call reports what it really needs.</summary>
    private const uint FirstBuffer = 1024;

    /// <summary>
    /// What the OS says <paramref name="path"/> really is. <paramref name="canonical"/> is set only on
    /// <see cref="KernelPathOutcome.Resolved"/>.
    /// </summary>
    public static KernelPathOutcome Canonicalise(string path, out string? canonical)
    {
        canonical = null;
        if (string.IsNullOrEmpty(path))
        {
            return KernelPathOutcome.Refused;
        }

        try
        {
            return OperatingSystem.IsWindows()
                ? OnWindows(path, out canonical)
                : OnUnix(path, out canonical);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or ArgumentException or OutOfMemoryException)
        {
            // The studio could not ask the OS at all, which is the strongest possible reason not to
            // guess on its behalf.
            return KernelPathOutcome.Refused;
        }
    }

    private static KernelPathOutcome OnUnix(string path, out string? canonical)
    {
        canonical = null;

        // realpath(path, NULL) mallocs the answer, so it is bounded by the kernel's own PATH_MAX
        // rather than by a guess made here, and it is freed below.
        var resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            return Marshal.GetLastPInvokeError() is Enoent or Enotdir
                ? KernelPathOutcome.Missing
                : KernelPathOutcome.Refused;
        }

        try
        {
            canonical = Marshal.PtrToStringUTF8(resolved);
            return canonical is { Length: > 0 } ? KernelPathOutcome.Resolved : KernelPathOutcome.Refused;
        }
        finally
        {
            Free(resolved);
        }
    }

    private static KernelPathOutcome OnWindows(string path, out string? canonical)
    {
        canonical = null;

        // Access 0 asks for no read at all: this only needs the handle's identity, so it works on a
        // file the studio may not open and on a directory alike.
        using var handle = CreateFile(
            path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return Marshal.GetLastPInvokeError() is ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidName
                ? KernelPathOutcome.Missing
                : KernelPathOutcome.Refused;
        }

        var size = FirstBuffer;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(checked((int)size * sizeof(char)));
            try
            {
                var written = GetFinalPathNameByHandle(handle, buffer, size, 0);
                if (written == 0)
                {
                    return KernelPathOutcome.Refused;
                }

                if (written >= size)
                {
                    // Too small: the call reports the length it wants, including the terminator.
                    size = written + 1;
                    continue;
                }

                canonical = Plain(Marshal.PtrToStringUni(buffer, checked((int)written)));
                return canonical is null ? KernelPathOutcome.Refused : KernelPathOutcome.Resolved;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return KernelPathOutcome.Refused;
    }

    /// <summary>
    /// Drops the extended-length prefix Windows answers with, so a canonical path compares with an
    /// ordinary one. Both sides of every comparison come through here, so the shape stays consistent.
    /// </summary>
    private static string? Plain(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + value[8..];
        }

        return value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
    }

    // DllImport rather than the source-generated LibraryImport: that generator emits unsafe code and
    // would mean turning AllowUnsafeBlocks on for the whole Infrastructure project, which is a much
    // bigger change than four declarations. Every string here is marshalled explicitly.
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, IntPtr buffer, uint size, uint flags);
}
