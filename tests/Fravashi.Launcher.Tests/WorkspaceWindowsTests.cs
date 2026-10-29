using AwesomeAssertions;
using Fravashi.Launcher;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>Backlog 29: the window map, the navigation rule and the caption behind close/switch workspace.</summary>
public sealed class WorkspaceWindowsTests
{
    private sealed class Window(string name)
    {
        public override string ToString() => name;
    }

    private static readonly Func<string, bool> NobodyElse = _ => false;

    [Fact]
    public void A_window_that_adopts_another_workspace_is_refiled_under_it_and_not_under_the_old_one()
    {
        var map = new WorkspaceWindowMap<Window>();
        var window = new Window("a");
        map.Rebind(window, "ws-old").Outcome.Should().Be(WindowRebindOutcome.Rebound);

        map.Rebind(window, "ws-new").Outcome.Should().Be(WindowRebindOutcome.Rebound);

        // The pre-29 bug: filed at creation and never re-keyed, so the old id still found this window.
        map.OwnerOf("ws-old").Should().BeNull();
        map.OwnerOf("ws-new").Should().BeSameAs(window);
        map.WorkspaceOf(window).Should().Be("ws-new");
        map.All.Should().ContainSingle();
    }

    [Fact]
    public void Switching_to_a_workspace_another_window_shows_moves_nothing_and_names_that_window()
    {
        var map = new WorkspaceWindowMap<Window>();
        var first = new Window("first");
        var second = new Window("second");
        map.Rebind(first, "ws-1");
        map.Rebind(second, "ws-2");

        var result = map.Rebind(second, "ws-1");

        result.Outcome.Should().Be(WindowRebindOutcome.ShownElsewhere);
        result.Owner.Should().BeSameAs(first);
        map.OwnerOf("ws-1").Should().BeSameAs(first);
        map.OwnerOf("ws-2").Should().BeSameAs(second);
    }

    [Fact]
    public void Rebinding_a_window_to_the_workspace_it_already_shows_is_unchanged()
    {
        var map = new WorkspaceWindowMap<Window>();
        var window = new Window("a");
        map.Rebind(window, "ws-1");

        map.Rebind(window, "ws-1").Outcome.Should().Be(WindowRebindOutcome.Unchanged);
        map.OwnerOf("ws-1").Should().BeSameAs(window);
    }

    [Fact]
    public void The_unbound_window_that_opens_a_workspace_frees_the_unbound_slot()
    {
        var map = new WorkspaceWindowMap<Window>();
        var home = new Window("home");
        map.TryAddUnbound(home).Should().BeNull();

        map.Rebind(home, "ws-1").Outcome.Should().Be(WindowRebindOutcome.Rebound);

        map.Unbound.Should().BeNull();
        map.OwnerOf("ws-1").Should().BeSameAs(home);
        map.TryAddUnbound(new Window("fresh")).Should().BeNull();
    }

    [Fact]
    public void Closing_the_workspace_makes_the_window_the_unbound_one_when_there_is_none()
    {
        var map = new WorkspaceWindowMap<Window>();
        var window = new Window("a");
        map.Rebind(window, "ws-1");

        var result = map.Unbind(window);

        result.Outcome.Should().Be(WindowUnbindOutcome.BecameUnbound);
        map.Unbound.Should().BeSameAs(window);
        map.OwnerOf("ws-1").Should().BeNull();
        map.WorkspaceOf(window).Should().BeNull();
        map.Contains(window).Should().BeTrue();
    }

    [Fact]
    public void Closing_the_workspace_while_an_unbound_window_exists_closes_this_one_in_its_favour()
    {
        var map = new WorkspaceWindowMap<Window>();
        var home = new Window("home");
        var bound = new Window("bound");
        map.TryAddUnbound(home);
        map.Rebind(bound, "ws-1");

        var result = map.Unbind(bound);

        // At most one unbound window: this one leaves the map, the existing one is the survivor.
        result.Outcome.Should().Be(WindowUnbindOutcome.CloseInFavourOf);
        result.Survivor.Should().BeSameAs(home);
        map.Unbound.Should().BeSameAs(home);
        map.Contains(bound).Should().BeFalse();
        map.OwnerOf("ws-1").Should().BeNull();
        map.All.Should().Equal(home);
    }

    [Fact]
    public void Closing_the_workspace_of_the_unbound_window_changes_nothing()
    {
        var map = new WorkspaceWindowMap<Window>();
        var home = new Window("home");
        map.TryAddUnbound(home);

        map.Unbind(home).Outcome.Should().Be(WindowUnbindOutcome.AlreadyUnbound);
        map.Unbound.Should().BeSameAs(home);
    }

    [Fact]
    public void A_second_unbound_window_is_refused_and_the_existing_one_is_named()
    {
        var map = new WorkspaceWindowMap<Window>();
        var home = new Window("home");
        map.TryAddUnbound(home).Should().BeNull();

        map.TryAddUnbound(new Window("second")).Should().BeSameAs(home);
        map.TryAddUnbound(home).Should().BeNull();
        map.All.Should().Equal(home);
    }

    [Fact]
    public void A_closed_window_is_forgotten_under_whatever_it_showed_last()
    {
        var map = new WorkspaceWindowMap<Window>();
        var window = new Window("a");
        map.Rebind(window, "ws-created-with");
        map.Rebind(window, "ws-adopted");

        map.Remove(window);

        map.OwnerOf("ws-created-with").Should().BeNull();
        map.OwnerOf("ws-adopted").Should().BeNull();
        map.All.Should().BeEmpty();
    }

    [Fact]
    public void All_lists_the_unbound_window_first()
    {
        var map = new WorkspaceWindowMap<Window>();
        var bound = new Window("bound");
        var home = new Window("home");
        map.Rebind(bound, "ws-1");
        map.TryAddUnbound(home);

        map.All.Should().Equal(home, bound);
    }

    [Fact]
    public void A_navigation_without_ws_in_a_bound_window_re_imposes_its_workspace()
    {
        var decision = WorkspaceNavigation.Decide("ws-1", null, NobodyElse);

        decision.Should().Be(new WorkspaceNavigationDecision(WorkspaceNavigationAction.ReImpose, "ws-1"));
        WorkspaceNavigation.Decide("ws-1", "  ", NobodyElse).Action.Should().Be(WorkspaceNavigationAction.ReImpose);
    }

    [Fact]
    public void Once_the_host_cleared_the_binding_the_same_navigation_proceeds_unbound()
    {
        // Close workspace = the host clears the binding, THEN navigates home without ws.
        WorkspaceNavigation.Decide(null, null, NobodyElse)
            .Should().Be(new WorkspaceNavigationDecision(WorkspaceNavigationAction.ProceedUnbound, null));
        WorkspaceNavigation.Decide(string.Empty, string.Empty, NobodyElse).Action
            .Should().Be(WorkspaceNavigationAction.ProceedUnbound);
    }

    [Fact]
    public void A_navigation_carrying_the_bound_workspace_proceeds()
    {
        WorkspaceNavigation.Decide("ws-1", "ws-1", _ => true)
            .Should().Be(new WorkspaceNavigationDecision(WorkspaceNavigationAction.ProceedBound, "ws-1"));
    }

    [Fact]
    public void A_navigation_carrying_a_workspace_no_other_window_shows_is_adopted()
    {
        WorkspaceNavigation.Decide("ws-1", "ws-2", NobodyElse)
            .Should().Be(new WorkspaceNavigationDecision(WorkspaceNavigationAction.Adopt, "ws-2"));
        WorkspaceNavigation.Decide(null, "ws-2", NobodyElse).Action.Should().Be(WorkspaceNavigationAction.Adopt);
    }

    [Fact]
    public void A_navigation_carrying_a_workspace_another_window_shows_focuses_that_window()
    {
        var asked = new List<string>();
        var decision = WorkspaceNavigation.Decide(null, "ws-2", id =>
        {
            asked.Add(id);
            return true;
        });

        decision.Should().Be(new WorkspaceNavigationDecision(WorkspaceNavigationAction.FocusOwner, "ws-2"));
        asked.Should().Equal("ws-2");
    }

    /// <summary>
    /// #372: the identity provider's redirect back to a sign-in callback carries a single-use state. A workspace window
    /// re-imposed its ws on it, the cancelled original still reached the studio and used the state up, and the re-imposed
    /// copy was refused as "possible CSRF". A callback passes the window untouched; every other studio page does not.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:9090/auth/callback/idp?code=c&state=s", true)]
    [InlineData("http://localhost:9090/auth/callback/atlassian?code=c&state=s", true)]
    [InlineData("http://localhost:9090/AUTH/CALLBACK/idp?code=c&state=s", true)]
    [InlineData("http://localhost:9090/", false)]
    [InlineData("http://localhost:9090/sign-in?error=x", false)]
    [InlineData("http://localhost:9090/auth/callbacks", false)]
    [InlineData("http://localhost:9090/code?next=/auth/callback/idp", false)]
    public void Only_a_sign_in_callback_passes_the_window_untouched(string target, bool untouched)
    {
        WorkspaceNavigation.PassesUntouched(new Uri(target)).Should().Be(untouched);
    }

    [Theory]
    [InlineData("http://localhost:5090/code", "http://localhost:5090/code?ws=ws-1")]
    [InlineData("http://localhost:5090/git?tab=remote", "http://localhost:5090/git?tab=remote&ws=ws-1")]
    [InlineData("http://localhost:5090/", "http://localhost:5090/?ws=ws-1")]
    public void Re_imposition_adds_ws_to_the_query(string target, string expected)
    {
        WorkspaceNavigation.WithWorkspace(new Uri(target), "ws-1").Should().Be(expected);
    }

    [Fact]
    public void Re_imposition_escapes_the_id_and_keeps_a_fragment_after_the_query()
    {
        WorkspaceNavigation.WithWorkspace(new Uri("http://localhost:5090/code#x"), "a b&c")
            .Should().Be("http://localhost:5090/code?ws=a%20b%26c#x");
    }

    [Fact]
    public void The_switch_and_home_urls_are_rooted_at_the_studio_base()
    {
        WorkspaceNavigation.WorkspaceUrl("http://localhost:5090/", "ws-1").Should().Be("http://localhost:5090/code?ws=ws-1");
        WorkspaceNavigation.HomeUrl("http://localhost:5090").Should().Be("http://localhost:5090/");
        WorkspaceNavigation.HomeUrl("http://localhost:5090/").Should().Be("http://localhost:5090/");
    }

    [Theory]
    [InlineData(null, "Fravashi")]
    [InlineData("", "Fravashi")]
    [InlineData("   ", "Fravashi")]
    [InlineData("MyRepo", "MyRepo - Fravashi")]
    [InlineData(" MyRepo ", "MyRepo - Fravashi")]
    public void The_caption_follows_the_workspace_shown(string? name, string expected)
    {
        WindowCaption.For(name).Should().Be(expected);
    }
}
