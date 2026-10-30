using Bunit;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>Backlog 383: the project-wide bunit wait timeout is in force before any test runs.</summary>
public class BunitDefaultsTests
{
    [Fact]
    public void Every_bunit_wait_in_this_project_uses_the_project_default()
    {
        Assert.Equal(BunitDefaults.WaitTimeout, BunitContext.DefaultWaitTimeout);
        Assert.True(BunitDefaults.WaitTimeout > TimeSpan.FromSeconds(1), "more room than bunit's own default");
        Assert.True(BunitDefaults.WaitTimeout <= TimeSpan.FromSeconds(10), "a real hang still fails within a bounded time");
    }
}
