using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Xunit;

namespace Fravashi.Application.Tests.Sessions;

/// <summary>
/// ADR 0031 decision 8: the judge runs a command only on a unanimous safe answer, and asks the person on
/// anything else, an error or a timeout. A judge that is down must ask MORE, never less.
/// </summary>
public sealed class AutopilotCommandJudgeTests
{
    private static readonly AutopilotJudgeOptions Options = new() { SafeBelow = 0.1, Timeout = TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task No_model_configured_is_unavailable_and_never_calls_anything()
    {
        var judge = new AutopilotCommandJudge(model: null, Options, TimeProvider.System);

        judge.IsOn.Should().BeFalse();
        var outcome = await judge.JudgeAsync("dotnet build", "repo", "An item", CancellationToken.None);

        outcome.Verdict.Should().Be(AutopilotJudgeVerdict.Unavailable);
        outcome.Reason.Should().Be("no_judge_configured");
    }

    [Fact]
    public async Task A_unanimous_answer_below_the_threshold_is_safe()
    {
        var model = new FakeJudgeModel(AutopilotJudgeQuestions.All.ToDictionary(id => id, _ => 0.01));
        var judge = new AutopilotCommandJudge(model, Options, TimeProvider.System);

        var outcome = await judge.JudgeAsync("dotnet build", "repo", "An item", CancellationToken.None);

        outcome.Verdict.Should().Be(AutopilotJudgeVerdict.Safe);
        model.Calls.Should().Be(1);
    }

    [Fact]
    public async Task One_answer_at_or_above_the_threshold_is_unclear_and_names_the_question()
    {
        var answers = AutopilotJudgeQuestions.All.ToDictionary(id => id, _ => 0.01);
        answers[AutopilotJudgeQuestions.ReachesNetwork] = 0.5;
        var model = new FakeJudgeModel(answers);
        var judge = new AutopilotCommandJudge(model, Options, TimeProvider.System);

        var outcome = await judge.JudgeAsync("curl https://example.com", "repo", "An item", CancellationToken.None);

        outcome.Verdict.Should().Be(AutopilotJudgeVerdict.Unclear);
        outcome.Reason.Should().Contain(AutopilotJudgeQuestions.ReachesNetwork);
    }

    [Fact]
    public async Task A_missing_answer_is_unavailable()
    {
        var answers = AutopilotJudgeQuestions.All.Skip(1).ToDictionary(id => id, _ => 0.0);
        var model = new FakeJudgeModel(answers);
        var judge = new AutopilotCommandJudge(model, Options, TimeProvider.System);

        var outcome = await judge.JudgeAsync("dotnet build", "repo", "An item", CancellationToken.None);

        outcome.Verdict.Should().Be(AutopilotJudgeVerdict.Unavailable);
        outcome.Reason.Should().StartWith("answer_missing_");
    }

    [Fact]
    public async Task A_thrown_unavailable_exception_is_unavailable_with_its_reason()
    {
        var model = new FakeJudgeModel(throwReason: "http_503");
        var judge = new AutopilotCommandJudge(model, Options, TimeProvider.System);

        var outcome = await judge.JudgeAsync("dotnet build", "repo", "An item", CancellationToken.None);

        outcome.Verdict.Should().Be(AutopilotJudgeVerdict.Unavailable);
        outcome.Reason.Should().Be("http_503");
    }

    [Fact]
    public async Task A_model_that_never_answers_in_time_times_out_as_unavailable()
    {
        var model = new FakeJudgeModel(hang: true);
        var judge = new AutopilotCommandJudge(model, new AutopilotJudgeOptions { Timeout = TimeSpan.FromMilliseconds(50) }, TimeProvider.System);

        var outcome = await judge.JudgeAsync("dotnet build", "repo", "An item", CancellationToken.None);

        outcome.Verdict.Should().Be(AutopilotJudgeVerdict.Unavailable);
        outcome.Reason.Should().Be("timeout");
    }

    private sealed class FakeJudgeModel : IAutopilotJudgeModel
    {
        private readonly IReadOnlyDictionary<string, double>? _answers;
        private readonly string? _throwReason;
        private readonly bool _hang;

        public FakeJudgeModel(IReadOnlyDictionary<string, double>? answers = null, string? throwReason = null, bool hang = false)
        {
            _answers = answers;
            _throwReason = throwReason;
            _hang = hang;
        }

        public int Calls { get; private set; }

        public string Name => "fake";

        public async Task<IReadOnlyDictionary<string, double>> EvaluateAsync(
            JsonObject state, JsonObject questions, CancellationToken cancellationToken)
        {
            Calls++;
            if (_throwReason is { } reason)
            {
                throw new AutopilotJudgeUnavailableException(reason);
            }

            if (_hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return _answers ?? new Dictionary<string, double>();
        }
    }
}
