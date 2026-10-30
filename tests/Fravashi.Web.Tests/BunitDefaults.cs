using System.Runtime.CompilerServices;
using Bunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// One wait timeout for every bunit wait in this project (backlog 383). bunit's own default is about a second, and a
/// full run on a loaded dev host (other sessions' containers on the same PC) timed out two different tests that pass
/// alone: GitPageTests' path filter in a CI run, and HomeDashboardTests' picker defaults in a local run.
/// The project has nearly 800 WaitForAssertion calls and only a few name their own timeout, so the fix is the default,
/// not those two tests. Five seconds gives a loaded machine room while a real hang still fails within a bounded time.
/// A test that needs a different bound still passes its own to the call.
/// </summary>
internal static class BunitDefaults
{
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [ModuleInitializer]
    internal static void Apply() => BunitContext.DefaultWaitTimeout = WaitTimeout;
}
