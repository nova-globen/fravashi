using System.Runtime.InteropServices;

namespace Fravashi.Desktop;

/// <summary>
/// Console attachment for the GUI-subsystem fravashi.exe (decided 2026-08-01: AttachConsole over a
/// companion console exe). Console verbs attach to the PARENT process console so `fravashi status` prints
/// in the terminal that invoked it. When stdout is already a redirected pipe or file (fravashi status >
/// out.txt, CI, dotnet run harnesses) the inherited handle is kept: attaching would REPLACE the
/// redirected handles with the console's and silently swallow the output. With no parent console at
/// all (a verb launched from Explorer or Run) the attach fails silently; output still lands in the
/// launcher log file. Known cosmetic quirk of attached GUI apps: the interactive shell prompt returns
/// immediately and verb output interleaves after it.
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
            // stdout is redirected (pipe/file): Console.WriteLine already reaches it.
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
