using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Dashboard;
using Fravashi.Web.Components.Pages;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #360: Home's cards can be moved, resized and hidden. The arrangement is a small JSON document in the
/// settings table (<see cref="HomeLayoutStore.Key"/>), applied on every render, saved once per move, and forgiving: an
/// unknown card id is dropped and a card the document does not name is put back at its default place.
/// </summary>
public class HomeLayoutTests
{
    private static (BunitContext Context, FakeConfigurationStore Settings) PageContext(string? stored = null)
    {
        var settings = new FakeConfigurationStore();
        if (stored is not null)
        {
            settings.Values[HomeLayoutStore.Key] = stored;
        }

        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IConfigurationStore>(settings);
        ctx.Services.AddNoUpdates();
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddUnboundHomeSurface();
        return (ctx, settings);
    }

    private static List<string> CardOrder(IRenderedComponent<Home> cut) =>
        cut.FindAll("[data-home-card]").Select(e => e.GetAttribute("data-home-card")!).ToList();

    private static HomeLayout Saved(FakeConfigurationStore settings) =>
        HomeLayout.Parse(settings.Get(HomeLayoutStore.Key) ?? throw new Xunit.Sdk.XunitException("nothing was saved"));

    private static async Task Arrange(IRenderedComponent<Home> cut) =>
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-arrange]").Click());

    // ---- the document --------------------------------------------------------------------------------------------

    [Fact]
    public void The_built_in_default_is_the_owners_arrangement_and_fills_every_row()
    {
        // Work item #363: the owner arranged Home in the window and handed over the document "Copy layout" gives.
        var layout = HomeLayout.Default;

        Assert.Equal(
            [
                HomeLayout.Conversation, HomeLayout.Autopilot, HomeLayout.Progress, HomeLayout.RecentDispatches,
                HomeLayout.LiveSessions, HomeLayout.Waiting, HomeLayout.Updates, HomeLayout.Wiki,
                HomeLayout.RecentConversations, HomeLayout.TimeSpent, HomeLayout.AgentSpend, HomeLayout.Throughput,
                HomeLayout.RepositoryHealth,
            ],
            layout.Cards.Select(c => c.Id));
        Assert.Equal((6, 4), (layout.Find(HomeLayout.RecentDispatches)!.Width, layout.Find(HomeLayout.RecentDispatches)!.Height));
        Assert.Equal((6, 4), (layout.Find(HomeLayout.LiveSessions)!.Width, layout.Find(HomeLayout.LiveSessions)!.Height));
        Assert.Equal((8, 1), (layout.Find(HomeLayout.RecentConversations)!.Width, layout.Find(HomeLayout.RecentConversations)!.Height));
        Assert.DoesNotContain(layout.Cards, c => c.Hidden);

        // Sparse placement leaves a hole only where a card does not fit beside the ones before it; the default has
        // none, and the cards that share a row share a height, so no taller card leaves rows half filled beside it.
        var used = 0;
        var rowHeight = 0;
        foreach (var card in layout.Cards)
        {
            if (used + card.Width > HomeLayout.Columns)
            {
                Assert.Equal(HomeLayout.Columns, used);
                used = 0;
            }

            if (used == 0)
            {
                rowHeight = card.Height;
            }

            Assert.Equal(rowHeight, card.Height);
            used += card.Width;
        }

        Assert.Equal(HomeLayout.Columns, used);
    }

    [Fact]
    public void The_built_in_default_names_every_card_once_and_round_trips()
    {
        var layout = HomeLayout.Default;

        Assert.Equal(HomeLayout.Kinds.Select(k => k.Id).Order(), layout.Cards.Select(c => c.Id).Order());
        Assert.Equal(HomeLayout.Conversation, layout.Cards[0].Id);
        var reparsed = HomeLayout.Parse(layout.ToJson());
        Assert.Equal(layout.Cards, reparsed.Cards);
        Assert.Contains("\"cards\"", layout.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_document_is_the_default()
    {
        Assert.Equal(HomeLayout.Default.Cards, HomeLayout.Parse("not json").Cards);
        Assert.Equal(HomeLayout.Default.Cards, HomeLayout.Parse("").Cards);
        Assert.Equal(HomeLayout.Default.Cards, HomeLayout.Parse("{\"cards\": null}").Cards);
    }

    [Fact]
    public void An_unknown_id_is_dropped_and_a_missing_card_is_added_at_its_default_place()
    {
        // The document names no "wiki" (a card a later release added, say) and one this build does not know.
        var document = """
            { "version": 1, "cards": [
                { "id": "progress", "width": 4 },
                { "id": "from-the-future", "width": 6 },
                { "id": "conversation", "width": 12 },
                { "id": "updates", "width": 6 },
                { "id": "live-sessions", "width": 6, "height": 2 },
                { "id": "progress", "width": 12 }
            ] }
            """;

        var layout = HomeLayout.Parse(document);
        var ids = layout.Cards.Select(c => c.Id).ToList();

        Assert.DoesNotContain("from-the-future", ids);
        Assert.Single(ids, id => id == HomeLayout.Progress);
        Assert.Equal(4, layout.Find(HomeLayout.Progress)!.Width);
        Assert.Equal(HomeLayout.Kinds.Count, ids.Count);
        // Wiki follows updates, the card before it in the default; waiting follows live-sessions.
        Assert.Equal(ids.IndexOf(HomeLayout.Updates) + 1, ids.IndexOf(HomeLayout.Wiki));
        Assert.Equal(ids.IndexOf(HomeLayout.LiveSessions) + 1, ids.IndexOf(HomeLayout.Waiting));
        Assert.Equal(HomeLayout.Progress, ids[0]);
    }

    [Fact]
    public void A_missing_card_with_nothing_before_it_goes_before_the_card_that_follows_it()
    {
        var layout = HomeLayout.Parse("""{ "cards": [ { "id": "recent-conversations" }, { "id": "updates" } ] }""");
        var ids = layout.Cards.Select(c => c.Id).ToList();

        // The conversation box precedes everything in the default, so it goes before Updates (the first card after it
        // that the document names), not to the very top; the cards between them in the default follow it. The cards
        // after the recent conversations in the default follow that card instead.
        Assert.Equal(HomeLayout.RecentConversations, ids[0]);
        var box = ids.IndexOf(HomeLayout.Conversation);
        Assert.True(box > 0);
        Assert.Equal(
            [HomeLayout.Conversation, HomeLayout.Autopilot, HomeLayout.Progress, HomeLayout.RecentDispatches,
             HomeLayout.LiveSessions, HomeLayout.Waiting, HomeLayout.Updates],
            ids.Skip(box).Take(7));
        Assert.Equal(HomeLayout.TimeSpent, ids[1]);
    }

    [Fact]
    public void Sizes_are_clamped_to_the_grid()
    {
        var layout = HomeLayout.Parse("""{ "cards": [ { "id": "progress", "width": 40, "height": 0 }, { "id": "wiki", "width": 1, "height": 99 } ] }""");

        Assert.Equal((12, 1), (layout.Find(HomeLayout.Progress)!.Width, layout.Find(HomeLayout.Progress)!.Height));
        Assert.Equal((HomeLayout.MinWidth, HomeLayout.MaxHeight), (layout.Find(HomeLayout.Wiki)!.Width, layout.Find(HomeLayout.Wiki)!.Height));
    }

    [Fact]
    public void Moving_a_row_passes_as_many_cards_as_fill_the_grid()
    {
        var layout = HomeLayout.Default;

        // Throughput (a third) moves up past cards until 12 columns are passed: agent-spend (4), time-spent (8), then
        // recent-conversations (16). Agent-spend moves down past throughput (4) and repository-health (8), the last.
        var up = layout.MoveRow(HomeLayout.Throughput, -1).Visible.Select(c => c.Id).ToList();
        Assert.Equal(up.IndexOf(HomeLayout.RecentConversations) - 1, up.IndexOf(HomeLayout.Throughput));

        var down = layout.MoveRow(HomeLayout.AgentSpend, 1).Visible.Select(c => c.Id).ToList();
        Assert.Equal(down.IndexOf(HomeLayout.RepositoryHealth) + 1, down.IndexOf(HomeLayout.AgentSpend));
    }

    // ---- the page --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_stored_document_sets_the_order_the_width_and_what_is_hidden()
    {
        var (ctx, _) = PageContext("""
            { "version": 1, "columns": 12, "cards": [
                { "id": "progress", "width": 4, "height": 3, "hidden": false },
                { "id": "conversation", "width": 8, "height": 1, "hidden": false },
                { "id": "recent-conversations", "width": 12, "height": 1, "hidden": true }
            ] }
            """);
        using var _ctx = ctx;

        var cut = ctx.Render<Home>();

        // Progress first, as stored; the cards the document did not name follow the card before them in the default
        // (the autopilot after the conversation box, the dispatches after Progress).
        var order = CardOrder(cut);
        Assert.Equal(HomeLayout.Progress, order[0]);
        Assert.Equal(order.IndexOf(HomeLayout.Conversation) + 1, order.IndexOf(HomeLayout.Autopilot));
        Assert.Equal(order.IndexOf(HomeLayout.Progress) + 1, order.IndexOf(HomeLayout.RecentDispatches));
        Assert.Contains("--home-w: 4; --home-h: 3", cut.Find("[data-testid=home-card-progress]").GetAttribute("style"));
        Assert.Contains("--home-w: 8; --home-h: 1", cut.Find("[data-testid=home-card-conversation]").GetAttribute("style"));
        Assert.DoesNotContain(HomeLayout.RecentConversations, order);
        Assert.Empty(cut.FindAll("[data-testid=recent-conversations]"));
        // The cards the document did not name are all still there.
        Assert.Contains(HomeLayout.Updates, order);
        Assert.Contains(HomeLayout.RepositoryHealth, order);
    }

    [Fact]
    public void Outside_the_arrange_mode_the_page_carries_no_arranging_and_saves_nothing()
    {
        var (ctx, settings) = PageContext();
        using var _ctx = ctx;

        var cut = ctx.Render<Home>();

        Assert.NotNull(cut.Find("[data-testid=home-arrange]"));
        Assert.Empty(cut.FindAll("[data-testid=home-arrange-panel]"));
        Assert.Empty(cut.FindAll(".home-card-chrome"));
        Assert.Empty(cut.FindAll("[data-home-card-handle]"));
        Assert.Empty(cut.FindAll("[data-home-card-resize]"));
        Assert.Empty(cut.FindAll("[draggable]"));
        Assert.Empty(cut.FindAll("[inert]"));
        Assert.Null(settings.Get(HomeLayoutStore.Key));
        Assert.Equal(HomeLayout.Default.Visible.Select(c => c.Id), CardOrder(cut));
    }

    [Fact]
    public async Task Arrange_shows_a_handle_per_card_and_Done_puts_the_page_back()
    {
        var (ctx, settings) = PageContext();
        using var _ctx = ctx;
        var cut = ctx.Render<Home>();

        await Arrange(cut);

        cut.WaitForAssertion(() => Assert.Equal(CardOrder(cut).Count, cut.FindAll("[data-home-card-handle]").Count));
        Assert.All(cut.FindAll("[data-home-card]"), card => Assert.Equal("true", card.GetAttribute("draggable")));
        Assert.All(cut.FindAll(".home-card-body"), body => Assert.True(body.HasAttribute("inert")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=home-arrange-done]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-home-card-handle]")));
        Assert.Empty(cut.FindAll("[draggable]"));
        Assert.Null(settings.Get(HomeLayoutStore.Key));
    }

    [Fact]
    public async Task The_keys_on_a_handle_move_resize_and_hide_a_card_and_each_is_saved()
    {
        var (ctx, settings) = PageContext();
        using var _ctx = ctx;
        var cut = ctx.Render<Home>();
        await Arrange(cut);

        await cut.InvokeAsync(() => cut.Find("[data-testid=home-card-handle-conversation]").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" }));
        cut.WaitForAssertion(() => Assert.Equal(HomeLayout.Conversation, CardOrder(cut)[1]));
        Assert.Equal(HomeLayout.Conversation, Saved(settings).Cards[1].Id);

        await cut.InvokeAsync(() => cut.Find("[data-testid=home-card-handle-updates]").KeyDown(new KeyboardEventArgs { Key = "ArrowLeft", ShiftKey = true }));
        cut.WaitForAssertion(() => Assert.Contains("--home-w: 3;", cut.Find("[data-testid=home-card-updates]").GetAttribute("style")));
        Assert.Equal(3, Saved(settings).Find(HomeLayout.Updates)!.Width);

        await cut.InvokeAsync(() => cut.Find("[data-testid=home-card-handle-updates]").KeyDown(new KeyboardEventArgs { Key = "ArrowDown", ShiftKey = true }));
        cut.WaitForAssertion(() => Assert.Equal(2, Saved(settings).Find(HomeLayout.Updates)!.Height));

        await cut.InvokeAsync(() => cut.Find("[data-testid=home-card-handle-progress]").KeyDown(new KeyboardEventArgs { Key = "Delete" }));
        cut.WaitForAssertion(() => Assert.DoesNotContain(HomeLayout.Progress, CardOrder(cut)));
        Assert.True(Saved(settings).Find(HomeLayout.Progress)!.Hidden);

        // A hidden card is offered back, and showing it is saved too.
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-show-progress]").Click());
        cut.WaitForAssertion(() => Assert.Contains(HomeLayout.Progress, CardOrder(cut)));
        Assert.False(Saved(settings).Find(HomeLayout.Progress)!.Hidden);
    }

    [Fact]
    public async Task A_key_that_changes_nothing_saves_nothing()
    {
        var (ctx, settings) = PageContext();
        using var _ctx = ctx;
        var cut = ctx.Render<Home>();
        await Arrange(cut);

        // The first card cannot move earlier, and a full-width card cannot grow wider.
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-card-handle-conversation]").KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" }));
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-card-handle-conversation]").KeyDown(new KeyboardEventArgs { Key = "ArrowRight", ShiftKey = true }));
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-card-handle-conversation]").KeyDown(new KeyboardEventArgs { Key = "a" }));

        Assert.Null(settings.Get(HomeLayoutStore.Key));
    }

    [Fact]
    public async Task Dropping_a_dragged_card_on_another_moves_it_there_and_saves_once()
    {
        var (ctx, settings) = PageContext();
        using var _ctx = ctx;
        var cut = ctx.Render<Home>();
        await Arrange(cut);

        await cut.InvokeAsync(() => cut.Find("[data-testid=home-card-recent-conversations]").DragStart());
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-card-progress]").DragEnter());
        cut.WaitForAssertion(() => Assert.Contains("is-drop-target", cut.Find("[data-testid=home-card-progress]").ClassName));
        Assert.Null(settings.Get(HomeLayoutStore.Key));
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-card-progress]").Drop());

        cut.WaitForAssertion(() =>
        {
            var order = CardOrder(cut);
            Assert.Equal(order.IndexOf(HomeLayout.Progress) - 1, order.IndexOf(HomeLayout.RecentConversations));
        });
        var saved = Saved(settings).Cards.Select(c => c.Id).ToList();
        Assert.Equal(saved.IndexOf(HomeLayout.Progress) - 1, saved.IndexOf(HomeLayout.RecentConversations));
        Assert.Empty(cut.FindAll(".is-drop-target"));
    }

    [Fact]
    public async Task A_corner_resize_reaches_the_page_as_one_change_and_is_saved()
    {
        var (ctx, settings) = PageContext();
        using var _ctx = ctx;
        var cut = ctx.Render<Home>();
        await Arrange(cut);

        // What homeLayout.js writes when the pointer is let go.
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-arrange-resize-input]").Change("progress|8|3"));

        cut.WaitForAssertion(() => Assert.Contains("--home-w: 8; --home-h: 3", cut.Find("[data-testid=home-card-progress]").GetAttribute("style")));
        Assert.Equal((8, 3), (Saved(settings).Find(HomeLayout.Progress)!.Width, Saved(settings).Find(HomeLayout.Progress)!.Height));

        // Garbage changes nothing.
        settings.Values.Remove(HomeLayoutStore.Key);
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-arrange-resize-input]").Change("progress|wide"));
        Assert.Null(settings.Get(HomeLayoutStore.Key));
    }

    [Fact]
    public async Task Reset_returns_to_the_built_in_default_and_forgets_the_saved_layout()
    {
        var (ctx, settings) = PageContext("""{ "cards": [ { "id": "recent-conversations", "width": 4 }, { "id": "progress", "hidden": true } ] }""");
        using var _ctx = ctx;
        var cut = ctx.Render<Home>();
        Assert.Equal(HomeLayout.RecentConversations, CardOrder(cut)[0]);
        await Arrange(cut);

        await cut.InvokeAsync(() => cut.Find("[data-testid=home-arrange-reset]").Click());

        cut.WaitForAssertion(() => Assert.Equal(HomeLayout.Default.Visible.Select(c => c.Id), CardOrder(cut)));
        Assert.Null(settings.Get(HomeLayoutStore.Key));
        Assert.Contains("built-in layout", cut.Find("[data-testid=home-arrange-message]").TextContent);
    }

    [Fact]
    public async Task Copy_layout_shows_the_current_document_and_asks_the_clipboard_for_it()
    {
        var (ctx, _) = PageContext();
        using var _ctx = ctx;
        var copy = ctx.JSInterop.Setup<Fravashi.Web.Components.Sessions.CopyOutcome?>(
            Fravashi.Web.Components.Sessions.TranscriptClipboard.CopyFunction, _ => true);
        copy.SetResult(new Fravashi.Web.Components.Sessions.CopyOutcome(true, null));
        var cut = ctx.Render<Home>();
        await Arrange(cut);
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-card-handle-wiki]").KeyDown(new KeyboardEventArgs { Key = "ArrowLeft", ShiftKey = true }));

        await cut.InvokeAsync(() => cut.Find("[data-testid=home-arrange-copy]").Click());

        cut.WaitForAssertion(() => Assert.Contains("on the clipboard", cut.Find("[data-testid=home-arrange-message]").TextContent));
        var shown = cut.Find("[data-testid=home-arrange-json]").TextContent;
        var layout = HomeLayout.Parse(shown);
        Assert.Equal(HomeLayout.Default.Find(HomeLayout.Wiki)!.Width - 1, layout.Find(HomeLayout.Wiki)!.Width);
        Assert.Equal(HomeLayout.Kinds.Count, layout.Cards.Count);
        var call = Assert.Single(copy.Invocations);
        Assert.Equal(shown, call.Arguments[0]);
    }

    [Fact]
    public async Task A_refused_copy_says_so_and_still_shows_the_document()
    {
        var (ctx, _) = PageContext();
        using var _ctx = ctx;
        ctx.JSInterop.Setup<Fravashi.Web.Components.Sessions.CopyOutcome?>(
            Fravashi.Web.Components.Sessions.TranscriptClipboard.CopyFunction, _ => true)
            .SetResult(new Fravashi.Web.Components.Sessions.CopyOutcome(false, "no permission"));
        var cut = ctx.Render<Home>();
        await Arrange(cut);

        await cut.InvokeAsync(() => cut.Find("[data-testid=home-arrange-copy]").Click());

        cut.WaitForAssertion(() => Assert.Contains("Not copied (no permission)", cut.Find("[data-testid=home-arrange-message]").TextContent));
        Assert.Contains("\"conversation\"", cut.Find("[data-testid=home-arrange-json]").TextContent);
    }
}
