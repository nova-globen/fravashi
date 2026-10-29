using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Xunit;

namespace Fravashi.Application.Tests.Sessions;

/// <summary>
/// Work item #351: a conversation's title is short, fits the list, says what the conversation is about, and is made
/// locally. The cases are the owner's screenshot of the Sessions list (8.png) and the rules the item names.
/// </summary>
public sealed class ConversationTitlesTests
{
    [Fact]
    public void A_greeting_is_trimmed_and_a_cut_word_is_not_shown_as_a_whole_one()
    {
        // As stored before #351: the first 57 characters and "...".
        var title = ConversationTitles.Display(null, "Hi. many things are not visible properly in the lefthand ...");

        title.Should().Be("many things are not visible properly in the lefthand...");
        title.Length.Should().BeLessThanOrEqualTo(ConversationTitles.MaxLength);
    }

    [Fact]
    public void The_first_meaningful_clause_is_the_title()
    {
        ConversationTitles.FromPrompt("Hello, please fix the reconnect dialog. It shows twice after a restart and then hangs.")
            .Should().Be("fix the reconnect dialog");
    }

    [Fact]
    public void A_too_short_first_sentence_takes_the_rest_with_it()
    {
        ConversationTitles.FromPrompt("Quick one. The tray icon is missing")
            .Should().Be("Quick one. The tray icon is missing");
    }

    [Fact]
    public void A_long_clause_is_capped_at_a_word_boundary()
    {
        var title = ConversationTitles.FromPrompt(
            "Investigate why the provider sync keeps reporting stale versions on every second refresh of the board");

        title.Length.Should().BeLessThanOrEqualTo(ConversationTitles.MaxLength);
        title.Should().EndWith("...");
        title[..^3].Should().NotEndWith(" ");
        "Investigate why the provider sync keeps reporting stale versions on every second refresh of the board"
            .Should().StartWith(title[..^3]);
    }

    [Fact]
    public void A_word_that_starts_like_a_greeting_is_kept()
    {
        ConversationTitles.FromPrompt("Hiding the sidebar breaks the layout").Should().Be("Hiding the sidebar breaks the layout");
    }

    [Fact]
    public void A_rename_always_wins_even_over_a_linked_item()
    {
        ConversationTitles.Display("My own name", "Hi. something", 343, "Sidebar sections squeeze")
            .Should().Be("My own name");
    }

    [Fact]
    public void A_linked_work_item_names_the_conversation()
    {
        ConversationTitles.Display(null, "Hi. many things are not visible", 343,
                "Sidebar sections squeeze to unreadable slivers with multiple panes open")
            .Should().Be("#343 Sidebar sections squeeze to unreadable slivers...");
    }

    [Fact]
    public void An_imported_untitled_conversation_keeps_its_name()
    {
        ConversationTitles.Display(null, ConversationTitles.Untitled).Should().Be(ConversationTitles.Untitled);
    }

    [Fact]
    public void A_review_names_its_item_round_and_verdict()
    {
        ConversationTitles.ForReview(335, 2, "feat(tools): load them", "pass").Should().Be("Review of #335, round 2: pass");
        ConversationTitles.ForReview(335, 1, "feat(tools): load them", null).Should().Be("Review of #335");
    }

    [Fact]
    public void A_review_with_no_item_names_its_subject_without_the_commit_type()
    {
        ConversationTitles.ForReview(null, 1, "fix(web): stop slivers", "fail")
            .Should().Be("Review: stop slivers (fail)");

        var longer = ConversationTitles.ForReview(
            null, 1, "feat(agent-tools): mark the few tools nearly every builder uses", "fail");
        longer.Should().StartWith("Review: mark the few tools").And.EndWith("... (fail)");
        longer.Length.Should().BeLessThanOrEqualTo(ConversationTitles.MaxLength);
    }

    [Fact]
    public void A_review_stored_before_the_change_reads_as_a_review_not_a_prompt()
    {
        ConversationTitles.Display(null, "Commit review: feat(agent-tools): mark the few tools nearly ev...")
            .Should().Be("Review: mark the few tools nearly...");
        ConversationTitles.Display(null, "Commit review: fix(web): stop slivers (#343)")
            .Should().Be("Review of #343");
    }

    [Fact]
    public void A_review_is_not_retitled_by_a_link()
    {
        ConversationTitles.Display(null, "Review of #335, round 2: pass", 12, "Something else")
            .Should().Be("Review of #335, round 2: pass");
    }

    [Fact]
    public void The_item_a_commit_message_names_is_found_by_the_wiki_parser()
    {
        ConversationTitles.ItemIn("fix(web): stop slivers\n\nWork item #343, plan @159.").Should().Be(343);
        ConversationTitles.ItemIn("fix(web): `#12` is code").Should().BeNull();
    }
}
