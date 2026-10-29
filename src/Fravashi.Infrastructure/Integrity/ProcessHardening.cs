using System.Runtime.InteropServices;

namespace Fravashi.Infrastructure.Integrity;

/// <summary>
/// Makes the studio process non-dumpable on Linux, so its own <c>/proc/&lt;pid&gt;/environ</c> becomes
/// root-owned and cannot be read by anything running at the container's uid (backlog 90). The agent
/// CLIs run at that same uid in the same container, so without this an approved shell command or any
/// process an agent starts could read the bridge token, the window token and the integrity key out of
/// <c>/proc/1/environ</c>, a path the read-scope gate does not cover.
///
/// Verified in the container 2026-08-09: with <c>PR_SET_DUMPABLE = 0</c> a same-uid reader is denied
/// <c>environ</c> (owner flips to <c>root:root</c>), while the process itself still reads its own
/// <c>/proc/self/status</c> and <c>/proc/self/mountinfo</c> (which the integrity runtime-shape probe
/// needs) and gets its own environment from process memory rather than <c>/proc</c>. A no-op off Linux
/// (a developer's <c>dotnet run</c>), where <c>/proc</c> does not exist.
///
/// This HIDES the values behind a <c>/proc</c> permission; it does not remove them from the
/// environment. The env allowlist (backlog 72) keeps them out of the agent CHILD's environment; this
/// closes the remaining read path for the PARENT. Neither removes the value from the parent's own
/// memory, which is the residual accepted in the agent threat model.
/// </summary>
public static class ProcessHardening
{
    private const int PrSetDumpable = 4;

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, long arg2, long arg3, long arg4, long arg5);

    /// <summary>
    /// Marks the current process non-dumpable so its <c>/proc</c> entry is root-owned. Returns true on
    /// success, false when the platform is not Linux (a deliberate no-op) or the syscall is unavailable.
    /// Never throws: startup hardening that crashes the studio would be worse than not hardening.
    /// </summary>
    public static bool MakeNonDumpable() => SetDumpable(false);

    /// <summary>
    /// The underlying toggle. <see cref="MakeNonDumpable"/> is the production entry point; this is public
    /// only so a Linux test can exercise the real syscall and then restore the runner's dumpable flag
    /// rather than leaving the whole test process non-dumpable. Nothing in the studio ever re-enables it.
    /// </summary>
    public static bool SetDumpable(bool dumpable)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            return prctl(PrSetDumpable, dumpable ? 1 : 0, 0, 0, 0) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
