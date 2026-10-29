using System.Runtime.InteropServices;

namespace Fravashi.WebSetup;

/// <summary>
/// Console attachment for /quiet, the same approach as fravashi.exe's: a GUI-subsystem EXE has no console, so it
/// attaches to its PARENT's when there is one and keeps an already redirected stdout (a pipe or a file) as it
/// is, because attaching would replace it. With no parent console the attach fails silently and the log file
/// still has everything.
/// </summary>
internal static class NativeConsole
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const uint FileTypeUnknown = 0x0000;

    public static void AttachToParent()
    {
        var stdout = GetStdHandle(StdOutputHandle);
        if (stdout != IntPtr.Zero && stdout != new IntPtr(-1) && GetFileType(stdout) != FileTypeUnknown)
        {
            return;
        }

        _ = AttachConsole(AttachParentProcess);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);
}
