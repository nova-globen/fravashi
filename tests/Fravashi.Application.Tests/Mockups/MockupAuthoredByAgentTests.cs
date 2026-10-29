using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// #50's acceptance: "an agent can author a valid tree from a written specification". The fixtures are the
/// evidence. A fresh agent (a Haiku subagent, 2026-10-05) that did not write the schema was given ONLY the
/// reference document (wiki @14) and <c>team-directory.spec.md</c>, with no tools, and each attempt file is
/// one of its answers, re-indented and otherwise unedited. Between attempts it was handed the reader's error
/// messages verbatim and nothing else. So these tests are a record of what happened, not a tree tuned to pass.
/// <para>
/// One honest wrinkle, recorded on #50: the field-label rule was added AFTER attempt 2, because attempt 2 gave
/// its search box only a placeholder and the reader accepted it. That was the schema's gap, not the agent's,
/// and attempt 3 is the agent's answer to the new rule's message.
/// </para>
/// </summary>
public class MockupAuthoredByAgentTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    [Fact]
    public void The_first_attempt_is_refused_with_every_mistake_named_at_its_place()
    {
        var result = MockupReader.Read(Fixture("team-directory.attempt-1.json"));

        result.IsValid.Should().BeFalse();
        result.Errors.Select(e => e.Path).Should().BeEquivalentTo(
            "$.screens[0].children[0].children[0]",
            "$.screens[1].children[1].states.visible",
            "$.screens[1]");
        result.Errors.Single(e => e.Path == "$.screens[1]").Message.Should().Contain("A screen is a window, and this is a dialog");
        result.Errors.Single(e => e.Path.EndsWith(".visible", StringComparison.Ordinal)).Message
            .Should().Contain("\"visible\" is not a state").And.Contain("\"hidden\"");
    }

    [Fact]
    public void The_second_attempt_fixed_what_it_was_told_and_is_refused_only_for_the_unlabelled_field()
    {
        var result = MockupReader.Read(Fixture("team-directory.attempt-2.json"));

        result.Errors.Should().ContainSingle()
            .Which.Should().Be(new MockupError(
                "$.screens[0].children[0].children[0]",
                "A field needs \"text\" (its visible label), or \"a11y\": {\"label\": \"...\"} when it shows none. "
                + "A placeholder is not a label: it disappears as soon as somebody types."));
    }

    [Fact]
    public void The_third_attempt_is_a_valid_tree()
    {
        var result = MockupReader.Read(Fixture("team-directory.attempt-3.json"));

        result.Errors.Should().BeEmpty();
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_valid_tree_carries_what_the_verifier_needs_from_the_specification()
    {
        var document = MockupReader.Read(Fixture("team-directory.attempt-3.json")).Document!;
        var nodes = document.Screens.SelectMany(All).ToList();
        MockupNode Node(string id) => nodes.Single(n => n.Id == id);

        // Responsive variants: two widths, and the toolbar stacks on the narrow one.
        document.Viewports.Select(v => (v.Name, v.Width)).Should().Equal(("wide", 1280), ("narrow", 390));
        nodes.Single(n => n.Type == MockupComponent.Toolbar).At!["narrow"].Layout.Should().Be(MockupLayout.Column);

        // Component states: the table's loading and empty states, the Save button's loading text.
        Node("people-table").States!.Keys.Should().BeEquivalentTo([MockupState.Loading, MockupState.Empty]);
        Node("people-table").States![MockupState.Empty].Text.Should().Be("Nobody matches that search.");
        Node("save-btn").States![MockupState.Loading].Text.Should().Be("Saving...");

        // Interactions: open the dialog, close it, submit sets Save loading, select and back navigate.
        Node("add-person").On![MockupEvent.Click].Open.Should().Be("add-dialog");
        Node("cancel-btn").On![MockupEvent.Click].Close.Should().Be("add-dialog");
        Node("add-form").On![MockupEvent.Submit].Set.Should().Contain("save-btn", "loading");
        Node("people-table").On![MockupEvent.Select].Go.Should().Be("profile");
        Node("back-btn").On![MockupEvent.Click].Go.Should().Be("directory");

        // Sample data: the four people, bound to the table.
        document.Data.Single(d => d.Name == "people").Records.Should().HaveCount(4);
        Node("people-table").Data.Should().Be("people");

        // Accessibility intent: the icon button's name, the portrait's description, the heading, the live error.
        Node("refresh").A11y!.Label.Should().Be("Refresh");
        Node("person-photo").A11y!.Label.Should().NotBeNullOrEmpty();
        Node("person-name").A11y!.Heading.Should().Be(1);
        Node("error-message").A11y!.Live.Should().Be("assertive");
        Node("error-message").State.Should().Be(MockupState.Hidden);
    }

    private static IEnumerable<MockupNode> All(MockupNode node) =>
        new[] { node }.Concat((node.Children ?? []).SelectMany(All));
}
