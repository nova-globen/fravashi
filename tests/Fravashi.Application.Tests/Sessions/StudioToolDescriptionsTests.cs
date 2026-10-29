using AwesomeAssertions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Application.Tests.Sessions;

/// <summary>
/// Work item #351: one describer gives a studio tool call its row label (past tense) and its approval sentence (what it
/// will do), from the call's arguments. The examples are the item's own.
/// </summary>
public sealed class StudioToolDescriptionsTests
{
    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void The_items_label_examples_read_as_written()
    {
        StudioToolDescriptions.Label("mcp__fravashi__session_work_link", Args(("number", "343")))
            .Should().Be("Linked this conversation to #343");
        StudioToolDescriptions.Label("mcp__fravashi__wiki_write", Args(("number", "159"), ("type", "plan"), ("title", "Plan: x")))
            .Should().Be("Wrote plan @159");
        StudioToolDescriptions.Label("mcp__fravashi__wiki_link", Args(("number", "159"), ("kind", "work_item"), ("target", "343")))
            .Should().Be("Linked @159 to #343");
        StudioToolDescriptions.Label("mcp__fravashi__work_item_transition", Args(("number", "343"), ("status", "in_progress")))
            .Should().Be("Moved #343 to In Progress");
        StudioToolDescriptions.Label("mcp__fravashi__work_item_read", Args(("number", "342")))
            .Should().Be("Read #342");
        StudioToolDescriptions.Label("mcp__fravashi__git_commit", Args(("message", "fix(web): stop slivers\n\nbody")))
            .Should().Be("Committed: fix(web): stop slivers");
    }

    [Fact]
    public void The_items_approval_examples_read_as_written()
    {
        StudioToolDescriptions.ApprovalSentence(
                "mcp__fravashi__work_item_transition",
                Args(("number", "343"), ("version", "4"), ("status", "in_progress")),
                new StudioToolFacts(WorkItemStatus.Ready))
            .Should().Be("Move #343 from Ready to In Progress");
        StudioToolDescriptions.ApprovalSentence(
                "mcp__fravashi__work_item_update",
                Args(("number", "342"), ("version", "2"), ("description", "x"), ("acceptanceCriteria", "[\"a\"]")))
            .Should().Be("Update #342: description and acceptance criteria");
        StudioToolDescriptions.ApprovalSentence(
                "mcp__fravashi__work_item_evidence",
                Args(("number", "335"), ("criterion", "2"), ("result", "pass"), ("summary", "ok")))
            .Should().Be("Record evidence on #335, criterion 2: pass");
        StudioToolDescriptions.ApprovalSentence(
                "mcp__fravashi__git_commit",
                Args(("message", "feat(web): readable rows"), ("paths", "[\"a.cs\",\"b.cs\",\"c.cs\"]")))
            .Should().Be("Commit 3 files: feat(web): readable rows");
    }

    [Fact]
    public void A_move_without_the_current_status_leaves_the_from_out()
    {
        StudioToolDescriptions.ApprovalSentence(
                "work_item_transition", Args(("number", "343"), ("status", "done"), ("resolution", "completed")))
            .Should().Be("Move #343 to Done, resolved as completed");
    }

    [Fact]
    public void A_row_stored_without_arguments_still_reads_as_words()
    {
        StudioToolDescriptions.Label("mcp__fravashi__work_item_transition", null).Should().Be("Moved a work item");
        StudioToolDescriptions.Label("mcp__fravashi__session_work_link", null).Should().Be("Linked this conversation to a work item");
    }

    [Fact]
    public void Built_in_tools_and_other_servers_are_not_described()
    {
        StudioToolDescriptions.Label("Read", Args(("file_path", "/x"))).Should().BeNull();
        StudioToolDescriptions.Label("mcp__other__work_item_read", Args(("number", "1"))).Should().BeNull();
        StudioToolDescriptions.ApprovalSentence("Bash", Args(("command", "ls"))).Should().BeNull();
    }

    [Fact]
    public void A_number_that_arrived_as_a_json_string_still_reads()
    {
        StudioToolDescriptions.Label("work_item_read", Args(("number", "\"343\""))).Should().Be("Read #343");
    }

    [Fact]
    public void Every_described_tool_has_both_forms_even_with_no_arguments()
    {
        foreach (var name in StudioToolDescriptions.Described)
        {
            StudioToolDescriptions.Label(name, null).Should().NotBeNullOrWhiteSpace(name);
            StudioToolDescriptions.ApprovalSentence(name, null).Should().NotBeNullOrWhiteSpace(name);
        }
    }
}
