using AwesomeAssertions;
using Fravashi.Domain.WorkItems.AgentLayer;
using Xunit;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>
/// Backlog 55's rule, which is the sentence the product is making rather than a detail of how a command
/// was started: every step green with zero errors is the only pass, and a partial run is reported as
/// partial, never as a pass.
/// </summary>
public class ScenarioRunTests
{
    private static ScenarioRun Run(params StepOutcome[] outcomes) =>
        new("s1", [.. outcomes.Select((o, i) => new StepResult("step" + i, o, "detail"))]);

    [Fact]
    public void Every_step_passing_is_the_only_pass()
    {
        Run(StepOutcome.Passed, StepOutcome.Passed).Result.Should().Be(EvidenceResult.Pass);
    }

    [Fact]
    public void One_failure_fails_the_run_however_many_passed()
    {
        Run(StepOutcome.Passed, StepOutcome.Passed, StepOutcome.Failed).Result.Should().Be(EvidenceResult.Fail);
    }

    [Fact]
    public void A_step_that_did_not_run_makes_the_run_partial_and_not_a_pass()
    {
        Run(StepOutcome.Passed, StepOutcome.NotRun).Result.Should().Be(EvidenceResult.Partial);
    }

    [Fact]
    public void A_failure_beats_a_step_that_did_not_run()
    {
        // Both are true, and "it failed" is the one that changes what anybody does next.
        Run(StepOutcome.Failed, StepOutcome.NotRun).Result.Should().Be(EvidenceResult.Fail);
    }

    [Fact]
    public void A_scenario_with_no_steps_proves_nothing_and_is_not_a_pass()
    {
        // Calling it a pass would make "verified" mean "nobody wrote a check".
        new ScenarioRun("s1", []).Result.Should().Be(EvidenceResult.Partial);
    }

    [Fact]
    public void Nothing_running_at_all_is_partial_rather_than_a_pass()
    {
        Run(StepOutcome.NotRun, StepOutcome.NotRun).Result.Should().Be(EvidenceResult.Partial);
    }

    // ---- what it says -------------------------------------------------------------------------

    [Fact]
    public void A_partial_run_says_it_is_not_a_pass_rather_than_counting_the_ones_that_worked()
    {
        var summary = Run(StepOutcome.Passed, StepOutcome.Passed, StepOutcome.NotRun).Summarise("Log in");

        summary.Should().Contain("not a pass");
        summary.Should().Contain("2 of 3").And.Contain("1 were not run");
    }

    [Fact]
    public void A_failed_run_says_how_many_failed_and_how_many_were_never_reached()
    {
        var summary = Run(StepOutcome.Failed, StepOutcome.NotRun).Summarise("Log in");

        summary.Should().Contain("failed").And.Contain("not reached");
    }

    [Fact]
    public void A_passing_run_says_every_step_ran()
    {
        Run(StepOutcome.Passed, StepOutcome.Passed).Summarise("Log in")
            .Should().Contain("all 2 steps ran");
    }

    [Fact]
    public void An_empty_scenario_says_that_running_it_proved_nothing()
    {
        new ScenarioRun("s1", []).Summarise("Log in").Should().Contain("proved nothing");
    }

    [Fact]
    public void The_counts_agree_with_the_steps()
    {
        var run = Run(StepOutcome.Passed, StepOutcome.NotRun, StepOutcome.NotRun);

        run.Passed.Should().Be(1);
        run.NotRun.Should().Be(2);
    }
}
