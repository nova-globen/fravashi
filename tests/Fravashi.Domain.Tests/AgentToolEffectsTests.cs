using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests;

/// <summary>
/// Arguments whose VALUE changes what a tool call means. The escape these close is the padded-path one
/// a layer out: the prompt rendered a true sentence and left out the argument the answer turned on. An
/// Agent call names no path, so the resolved-target block is empty for it, and
/// <c>isolation: "remote"</c> ships the workspace copy to a cloud environment off this machine.
/// <para>
/// Every field name and default asserted here was read from the CLI's own <c>sdk-tools.d.ts</c> inside
/// the container, not inferred: <c>run_in_background</c> defaults to TRUE on Agent and to false on Bash,
/// and only the schema says so. What the CLI then does with it is read with the studio's switches on
/// (backlog 192): background tasks are off, so an Agent call runs in the foreground here.
/// </para>
/// </summary>
public class AgentToolEffectsTests
{
    private static IReadOnlyList<AgentToolEffect> Of(string tool, params (string Key, string Value)[] input) =>
        AgentToolEffects.Of(tool, input.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    [Fact]
    public void Remote_isolation_is_an_alarm_that_names_the_machine_it_leaves()
    {
        var effects = Of(
            "Agent",
            ("description", "Tidy the helpers"),
            ("prompt", "Rename the helpers."),
            ("isolation", "remote"));

        var isolation = effects.Should().ContainSingle(e => e.Field == "isolation").Subject;
        isolation.Weight.Should().Be(AgentEffectWeight.Alarm);
        isolation.Notice.Should().ContainEquivalentOf("not on this machine");
    }

    [Fact]
    public void A_worktree_isolation_is_stated_without_the_alarm()
    {
        // An alarm that fires on every isolation says nothing about the one that leaves the machine.
        var effects = Of("Agent", ("isolation", "worktree"));

        effects.Should().ContainSingle(e => e.Field == "isolation")
            .Which.Weight.Should().Be(AgentEffectWeight.Note);
    }

    [Fact]
    public void An_isolation_value_nobody_anticipated_is_an_alarm_rather_than_silence()
    {
        // Fail closed: a value the studio cannot read is a value it cannot promise anything about, and
        // a rule written as "warn about the known-bad values" would have gone quiet here.
        var effects = Of("Agent", ("isolation", "cloud-v2"));

        var isolation = effects.Should().ContainSingle(e => e.Field == "isolation").Subject;
        isolation.Weight.Should().Be(AgentEffectWeight.Alarm);
        isolation.Notice.Should().Contain("cloud-v2");
    }

    [Fact]
    public void An_agent_call_that_never_mentions_run_in_background_is_said_to_run_in_the_foreground()
    {
        // Backlog 192: the CLI's schema says "Agents run in the background by default", but the studio starts
        // the CLI with background tasks off, and the pinned CLI then runs a non-remote agent in the foreground
        // (measured in the #50 trial: the result came back inside the turn). The card used to say BACKGROUND.
        var effects = Of("Agent", ("description", "Tidy the helpers"), ("prompt", "Rename the helpers."));

        var effect = effects.Should().ContainSingle(e => e.Field == "run_in_background").Subject;
        effect.Notice.Should().Contain("runs in the FOREGROUND")
            .And.Contain(AgentCliSwitches.DisableBackgroundTasksVariable)
            .And.NotContain("runs in the BACKGROUND");
        effect.Weight.Should().Be(AgentEffectWeight.Note);
    }

    [Fact]
    public void An_agent_call_asking_for_the_background_is_described_as_the_cli_treats_it_here()
    {
        // With the switch on the pinned CLI does not offer the field and launches an agent asynchronously only
        // for a remote isolation. Whether it refuses a call that sends it anyway is not settled from its code,
        // so the sentence says both what is known and what is not.
        var effect = Of("Agent", ("prompt", "Rename the helpers."), ("run_in_background", "true"))
            .Should().ContainSingle(e => e.Field == "run_in_background").Subject;

        effect.Value.Should().Be("true");
        effect.Notice.Should().Contain("does not offer this field")
            .And.Contain("this agent runs in the FOREGROUND")
            .And.Contain("not settled from the CLI's code");
    }

    [Fact]
    public void The_spawning_tool_keeps_its_rule_under_its_older_name_too()
    {
        // Measured on CLI 2.1.222 in the container: the hook payload and the tool_use block both say
        // "Agent", and the run's final permission_denials summary still says "Task". A name the studio
        // fails to recognise here does not mislabel the call, it says nothing about how the agent runs.
        Of("Task", ("description", "Tidy the helpers"), ("prompt", "Rename the helpers."))
            .Should().ContainSingle(e => e.Field == "run_in_background")
            .Which.Notice.Should().Contain("FOREGROUND");
    }

    [Fact]
    public void An_agent_told_to_run_synchronously_says_nothing_about_the_background()
    {
        Of("Agent", ("run_in_background", "false")).Should().BeEmpty();
    }

    [Fact]
    public void The_same_field_keeps_its_own_tools_default_so_a_plain_shell_call_stays_quiet()
    {
        // run_in_background defaults to false on Bash, so absent means foreground there. One shared
        // rule across both tools would have been wrong for one of them.
        Of("Bash", ("command", "npm test")).Should().BeEmpty();

        // Backlog 192: and an explicit true is described as the CLI treats it with the studio's switch on,
        // which leaves the field out of Bash's schema, rather than as a promise that it backgrounds.
        Of("Bash", ("command", "npm test"), ("run_in_background", "true"))
            .Should().ContainSingle(e => e.Field == "run_in_background")
            .Which.Notice.Should().Contain("does not offer this field")
            .And.Contain(AgentCliSwitches.DisableBackgroundTasksVariable)
            .And.Contain("not settled from the CLI's code");
    }

    [Fact]
    public void Disabling_the_sandbox_is_an_alarm_whatever_tool_asks_for_it()
    {
        var effects = Of("Bash", ("command", "npm install"), ("dangerouslyDisableSandbox", "true"));

        var sandbox = effects.Should().ContainSingle(e => e.Field == "dangerouslyDisableSandbox").Subject;
        sandbox.Weight.Should().Be(AgentEffectWeight.Alarm);
        sandbox.Notice.Should().ContainEquivalentOf("sandboxing turned OFF");
    }

    [Fact]
    public void Deleting_a_notebook_cell_is_named_because_a_deletion_shows_no_replacement_text()
    {
        var effects = Of(
            "NotebookEdit",
            ("notebook_path", "/workspaces/w1/a.ipynb"),
            ("edit_mode", "delete"));

        effects.Should().ContainSingle(e => e.Field == "edit_mode")
            .Which.Weight.Should().Be(AgentEffectWeight.Alarm);
    }

    [Fact]
    public void An_edit_that_replaces_every_occurrence_says_so_because_only_one_is_shown()
    {
        var effects = Of(
            "Edit",
            ("file_path", "/workspaces/w1/a.cs"),
            ("old_string", "x"),
            ("new_string", "y"),
            ("replace_all", "true"));

        effects.Should().ContainSingle(e => e.Field == "replace_all")
            .Which.Notice.Should().ContainEquivalentOf("EVERY occurrence");
    }

    [Fact]
    public void Removing_a_worktree_and_discarding_its_uncommitted_work_are_both_alarms()
    {
        var effects = Of("ExitWorktree", ("action", "remove"), ("discard_changes", "true"));

        effects.Should().HaveCount(2);
        effects.Should().OnlyContain(e => e.Weight == AgentEffectWeight.Alarm);
    }

    [Fact]
    public void Alarms_are_listed_before_the_notes_so_the_reason_to_stop_is_not_the_last_line()
    {
        var effects = Of(
            "Agent",
            ("prompt", "Rename the helpers."),
            ("isolation", "remote"),
            ("run_in_background", "true"));

        effects.Select(e => e.Field).Should().Equal("isolation", "run_in_background");
    }

    [Fact]
    public void An_ordinary_change_earns_no_lines_at_all()
    {
        Of("Write", ("file_path", "/workspaces/w1/README.md"), ("content", "# Widgets"))
            .Should().BeEmpty();
    }

    [Fact]
    public void A_tool_the_studio_has_never_heard_of_is_still_read_for_the_fields_that_mean_the_same_thing()
    {
        // The allowlist that matters is the auto-allow one; this list is about what the user is TOLD, so
        // an unknown tool sending a known field gets the sentence rather than nothing.
        Of("SomeFutureTool", ("isolation", "remote"))
            .Should().ContainSingle(e => e.Field == "isolation");
    }

    [Fact]
    public void A_field_sent_as_something_other_than_a_string_is_flagged_rather_than_read_as_false()
    {
        // A non-string flattens to its rendering, which is not a quiet value, so the fail-closed branch
        // speaks. Nothing here decides containment, so the JSON kind AgentToolInput guards does not
        // come into it; what matters is that a shape the studio cannot read never reads as "off".
        Of("Agent", ("isolation", "{\"mode\":\"remote\"}"))
            .Should().ContainSingle(e => e.Field == "isolation")
            .Which.Weight.Should().Be(AgentEffectWeight.Alarm);
    }

    [Fact]
    public void A_value_long_enough_to_be_the_prompt_is_capped_before_it_becomes_one()
    {
        var effects = Of("Agent", ("isolation", new string('x', 5_000)));

        effects.Should().ContainSingle(e => e.Field == "isolation")
            .Which.Value.Length.Should().BeLessThanOrEqualTo(80);
    }

    [Fact]
    public void A_call_with_no_arguments_and_no_tool_name_answers_with_nothing()
    {
        AgentToolEffects.Of(null, new Dictionary<string, string>(StringComparer.Ordinal)).Should().BeEmpty();
    }
}
