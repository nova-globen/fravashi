using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests.Setup;

/// <summary>
/// Item #373: when a window on a folder that is not set up lands on the setup. Its first load does, whatever page it
/// restored; a reload, and a link followed inside an open window, do not; the start dialog's "open this folder" does.
/// </summary>
public sealed class FirstOpenLandingRuleTests
{
    [Theory]
    [InlineData("/code/src/Stomme.Server/Calendar.cs")]
    [InlineData("/code")]
    [InlineData("/")]
    [InlineData("/workitems")]
    [InlineData("/sessions/5f0c")]
    public void A_windows_first_load_lands_whatever_page_it_asked_for(string path) =>
        Assert.True(FirstOpenLandingRule.ShouldLand(null, "ws-core", path));

    [Theory]
    [InlineData("/code/src/A.cs")]
    [InlineData("/code")]
    [InlineData("/workitems")]
    public void A_reload_of_the_same_folder_does_not_land_again(string path) =>
        Assert.False(FirstOpenLandingRule.ShouldLand(["ws-core"], "ws-core", path));

    [Theory]
    [InlineData("/sessions/5f0c")]
    [InlineData("/workitems/23")]
    [InlineData("/")]
    [InlineData("/code/src/A.cs")]
    public void A_link_followed_inside_an_open_window_does_not_land(string path) =>
        Assert.False(FirstOpenLandingRule.ShouldLand(["ws-other"], "ws-core", path));

    [Theory]
    [InlineData("/code")]
    [InlineData("/code/")]
    public void Opening_the_folder_in_a_window_that_was_elsewhere_lands(string path)
    {
        Assert.True(FirstOpenLandingRule.ShouldLand(["ws-other"], "ws-core", path));
        Assert.True(FirstOpenLandingRule.ShouldLand([], "ws-core", path));
    }

    [Theory]
    [InlineData("/setup")]
    [InlineData("/setup/import")]
    [InlineData("/SETUP/board")]
    public void The_setup_itself_never_lands(string path) =>
        Assert.False(FirstOpenLandingRule.ShouldLand(null, "ws-core", path));

    [Fact]
    public void The_window_remembers_each_folder_once_and_reads_back_what_it_wrote()
    {
        var first = FirstOpenLandingRule.Remember(null, "ws-core");
        Assert.Equal("[\"ws-core\"]", first);
        var second = FirstOpenLandingRule.Remember(FirstOpenLandingRule.Parse(first), "ws-core");
        Assert.Equal(first, second);
        Assert.Equal(["ws-core", "ws-other"], FirstOpenLandingRule.Parse(FirstOpenLandingRule.Remember(FirstOpenLandingRule.Parse(first), "ws-other")));

        // A window with no folder still records that it has loaded a page.
        Assert.Equal("[]", FirstOpenLandingRule.Remember(null, null));
        Assert.Empty(FirstOpenLandingRule.Parse("[]")!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    public void An_unreadable_memory_is_a_first_load(string? stored) =>
        Assert.Null(FirstOpenLandingRule.Parse(stored));

    [Theory]
    [InlineData("code/src/A.cs?ws=ws-core", "/setup?from=%2Fcode%2Fsrc%2FA.cs")]
    [InlineData("workitems?ws=ws-core#top", "/setup?from=%2Fworkitems")]
    [InlineData("?ws=ws-core", "/setup")]
    [InlineData("", "/setup")]
    [InlineData("setup/board", "/setup")]
    public void The_setup_is_told_where_the_window_was_going_without_its_folder(string loaded, string target) =>
        Assert.Equal(target, FirstOpenLandingRule.SetupTarget(loaded));
}
