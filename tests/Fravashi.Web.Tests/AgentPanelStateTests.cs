using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 23. The agent panel is drawn by the Code page and toggled from the window title bar,
/// which the layout renders; neither component contains the other, so the open state has to be a
/// circuit-scoped service rather than a field on the page.
/// </summary>
public class AgentPanelStateTests
{
    [Fact]
    public void The_panel_starts_closed_and_with_nothing_to_toggle()
    {
        var state = new AgentPanelState();

        Assert.False(state.IsOpen);
        Assert.False(state.IsAvailable);
    }

    [Fact]
    public void Toggling_flips_the_panel_and_tells_every_subscriber()
    {
        var state = new AgentPanelState();
        var changes = 0;
        state.Changed += () => changes++;

        state.Toggle();
        Assert.True(state.IsOpen);

        state.Toggle();
        Assert.False(state.IsOpen);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Setting_the_state_it_is_already_in_raises_nothing()
    {
        var state = new AgentPanelState();
        state.Open();
        var changes = 0;
        state.Changed += () => changes++;

        state.Open();
        state.Close();
        state.Close();

        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_mounted_surface_makes_the_panel_available_to_toggle()
    {
        var state = new AgentPanelState();
        var changes = 0;
        state.Changed += () => changes++;

        state.Attach();

        Assert.True(state.IsAvailable);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_departing_instance_does_not_switch_off_the_arriving_one()
    {
        // Around a navigation the new component initialises before the old one disposes, so the
        // count is what keeps availability honest; a bool would go false on the departing detach.
        var state = new AgentPanelState();
        state.Attach();
        state.Attach();

        state.Detach();

        Assert.True(state.IsAvailable);

        state.Detach();

        Assert.False(state.IsAvailable);
    }

    [Fact]
    public void Detaching_more_often_than_attaching_never_goes_negative()
    {
        var state = new AgentPanelState();

        state.Detach();
        state.Detach();
        state.Attach();

        Assert.True(state.IsAvailable);
    }

    [Fact]
    public void Leaving_the_page_and_coming_back_finds_the_panel_as_it_was_left()
    {
        // A window-chrome toggle is expected to hold its position; openness outlives the surface.
        var state = new AgentPanelState();
        state.Attach();
        state.Open();

        state.Detach();
        state.Attach();

        Assert.True(state.IsOpen);
    }

    // ---- a chosen conversation and follow (work item #366) ----

    [Fact]
    public void Showing_a_conversation_beside_the_code_opens_the_panel_on_it()
    {
        var state = new AgentPanelState();
        var changes = 0;
        state.Changed += () => changes++;
        var id = new Fravashi.Domain.AgentSessions.AgentSessionId(Guid.NewGuid());

        state.ShowBesideCode(id);
        state.ShowBesideCode(id);

        Assert.True(state.IsOpen);
        Assert.Equal(id, state.SessionId);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Returning_to_sessions_closes_the_panel_and_forgets_the_choice()
    {
        var state = new AgentPanelState();
        state.ShowBesideCode(new Fravashi.Domain.AgentSessions.AgentSessionId(Guid.NewGuid()));

        state.ReturnToSessions();

        Assert.False(state.IsOpen);
        Assert.Null(state.SessionId);
    }

    [Fact]
    public void Follow_is_on_until_switched_off()
    {
        var state = new AgentPanelState();
        Assert.True(state.FollowAgent);

        state.SetFollow(false);

        Assert.False(state.FollowAgent);
    }
}
