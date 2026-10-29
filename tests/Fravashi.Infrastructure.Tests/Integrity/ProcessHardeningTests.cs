using System.Runtime.InteropServices;
using Fravashi.Infrastructure.Integrity;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Integrity;

public sealed class ProcessHardeningTests
{
    [Fact]
    public void MakeNonDumpable_hardens_on_linux_and_is_a_noop_elsewhere()
    {
        var hardened = ProcessHardening.MakeNonDumpable();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // The real prctl syscall ran. Restore the test runner's dumpable flag immediately so the
            // rest of the suite (and any coverage tooling) is not left running non-dumpable; the fix
            // itself is proven end to end against the container in backlog 90, not here.
            hardened.Should().BeTrue("prctl PR_SET_DUMPABLE should succeed on Linux");
            ProcessHardening.SetDumpable(true).Should().BeTrue("the runner's dumpable flag is restored");
        }
        else
        {
            hardened.Should().BeFalse("it is a deliberate no-op where /proc does not exist");
        }
    }

    [Fact]
    public void MakeNonDumpable_never_throws()
    {
        // Startup hardening that crashes the studio would be worse than not hardening: whatever the
        // platform, the call returns rather than throwing.
        var act = () => ProcessHardening.MakeNonDumpable();
        act.Should().NotThrow();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            ProcessHardening.SetDumpable(true);
        }
    }
}
