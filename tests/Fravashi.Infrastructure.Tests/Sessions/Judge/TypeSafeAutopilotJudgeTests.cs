using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Infrastructure.Sessions.Judge;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions.Judge;

/// <summary>
/// The studio's own copy of the broker's TypeSafe adapter (ADR 0031 decision 8), narrowed to noul
/// questions, reading its key from the secret store rather than an environment variable. No network: every
/// case here drives a canned <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class TypeSafeAutopilotJudgeTests
{
    private const string Key = "ts-TESTKEY-do-not-use";

    private static ISecretStore Secrets(string? key = Key)
    {
        var store = Substitute.For<ISecretStore>();
        store.Get(AutopilotJudgeSecret.Reference).Returns(key);
        return store;
    }

    private static TypeSafeAutopilotJudge Judge(HttpMessageHandler handler, ISecretStore? secrets = null, Action<TypeSafeAutopilotJudgeOptions>? configure = null)
    {
        var options = new TypeSafeAutopilotJudgeOptions { RetryDelay = TimeSpan.Zero };
        configure?.Invoke(options);
        return new TypeSafeAutopilotJudge(new HttpClient(handler), secrets ?? Secrets(), options, TimeProvider.System);
    }

    private static string AnswerDocument(params (string Id, double Value)[] answers)
    {
        var body = new JsonObject { ["model"] = "jev-1.13.0", ["answers"] = new JsonObject() };
        foreach (var (id, value) in answers)
        {
            ((JsonObject)body["answers"]!)[id] = new JsonObject { ["noul"] = value };
        }

        return body.ToJsonString();
    }

    private static JsonObject AllQuestionsAnswered(double value = 0.01) =>
        new(AutopilotJudgeQuestions.All.Select(id => new KeyValuePair<string, JsonNode?>(id, new JsonObject { ["noul"] = value })));

    [Fact]
    public async Task No_key_stored_is_unavailable_and_calls_nothing()
    {
        var handler = new CapturingHandler(Json(HttpStatusCode.OK, "{}"));
        var judge = Judge(handler, Secrets(key: null));

        var act = () => judge.EvaluateAsync(new JsonObject { ["command"] = "dotnet build" }, AutopilotJudgeQuestions.Load(), CancellationToken.None);

        (await act.Should().ThrowAsync<AutopilotJudgeUnavailableException>()).Which.Reason.Should().Be("no_key");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_well_formed_noul_only_answer_is_read_for_every_question()
    {
        var document = AnswerDocument([.. AutopilotJudgeQuestions.All.Select(id => (id, 0.02))]);
        var handler = new CapturingHandler(Json(HttpStatusCode.OK, document));
        var judge = Judge(handler);

        var answers = await judge.EvaluateAsync(
            new JsonObject { ["command"] = "dotnet build" }, AutopilotJudgeQuestions.Load(), CancellationToken.None);

        answers.Should().HaveCount(AutopilotJudgeQuestions.All.Count);
        answers[AutopilotJudgeQuestions.ReachesNetwork].Should().Be(0.02);
    }

    [Fact]
    public async Task The_bearer_and_body_carry_the_key_and_the_documented_shape()
    {
        var handler = new CapturingHandler(Json(HttpStatusCode.OK, AnswerDocument([.. AutopilotJudgeQuestions.All.Select(id => (id, 0.0))])));
        var judge = Judge(handler);

        await judge.EvaluateAsync(new JsonObject { ["command"] = "dotnet build" }, AutopilotJudgeQuestions.Load(), CancellationToken.None);

        handler.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be(Key);
        handler.LastRequestBody.Should().Contain("\"command\":\"dotnet build\"").And.Contain("jev-latest");
    }

    [Fact]
    public async Task A_missing_answer_is_unavailable()
    {
        var document = AnswerDocument([(AutopilotJudgeQuestions.WritesOutsideCopy, 0.0)]);
        var handler = new CapturingHandler(Json(HttpStatusCode.OK, document));
        var judge = Judge(handler);

        var act = () => judge.EvaluateAsync(new JsonObject { ["command"] = "x" }, AutopilotJudgeQuestions.Load(), CancellationToken.None);

        (await act.Should().ThrowAsync<AutopilotJudgeUnavailableException>())
            .Which.Reason.Should().StartWith("answer_unreadable_");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"answers\":{}}")]
    public void An_unreadable_answer_document_is_no_judgement(string body)
    {
        var act = () => TypeSafeAutopilotJudge.Parse(body, AllQuestionsAnswered());

        act.Should().Throw<AutopilotJudgeUnavailableException>();
    }

    [Theory]
    [InlineData(401, "http_401")]
    [InlineData(422, "http_422")]
    [InlineData(500, "http_500")]
    public async Task An_http_failure_is_no_judgement_and_never_carries_the_key(int status, string reason)
    {
        var handler = new CapturingHandler(Json((HttpStatusCode)status, "{\"error\":\"nope\"}"));
        var judge = Judge(handler);

        var act = () => judge.EvaluateAsync(new JsonObject { ["command"] = "x" }, AutopilotJudgeQuestions.Load(), CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AutopilotJudgeUnavailableException>()).Which;
        thrown.Reason.Should().Be(reason);
        thrown.ToString().Should().NotContain(Key);
    }

    [Fact]
    public async Task Overload_gets_one_retry_then_succeeds()
    {
        var document = AnswerDocument([.. AutopilotJudgeQuestions.All.Select(id => (id, 0.0))]);
        var handler = new SequenceHandler(Json((HttpStatusCode)529, "{}"), Json(HttpStatusCode.OK, document));
        var judge = Judge(handler);

        var answers = await judge.EvaluateAsync(new JsonObject { ["command"] = "x" }, AutopilotJudgeQuestions.Load(), CancellationToken.None);

        answers.Should().HaveCount(AutopilotJudgeQuestions.All.Count);
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Two_overloads_in_a_row_are_no_judgement()
    {
        var handler = new SequenceHandler(Json((HttpStatusCode)529, "{}"), Json((HttpStatusCode)529, "{}"));
        var judge = Judge(handler);

        var act = () => judge.EvaluateAsync(new JsonObject { ["command"] = "x" }, AutopilotJudgeQuestions.Load(), CancellationToken.None);

        (await act.Should().ThrowAsync<AutopilotJudgeUnavailableException>()).Which.Reason.Should().Be("http_529");
    }

    [Fact]
    public async Task An_insecure_endpoint_with_the_flag_off_is_no_judgement_and_sends_nothing()
    {
        var handler = new CapturingHandler(Json(HttpStatusCode.OK, "{}"));
        var judge = Judge(handler, configure: o => o.Endpoint = "http://api.typesafe.ai/v1/systemone");

        var act = () => judge.EvaluateAsync(new JsonObject { ["command"] = "dotnet build" }, AutopilotJudgeQuestions.Load(), CancellationToken.None);

        (await act.Should().ThrowAsync<AutopilotJudgeUnavailableException>()).Which.Reason.Should().Be("insecure_endpoint");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_insecure_endpoint_with_the_flag_on_is_allowed()
    {
        var document = AnswerDocument([.. AutopilotJudgeQuestions.All.Select(id => (id, 0.0))]);
        var handler = new CapturingHandler(Json(HttpStatusCode.OK, document));
        var judge = Judge(handler, configure: o =>
        {
            o.Endpoint = "http://127.0.0.1/systemone";
            o.AllowInsecureEndpoint = true;
        });

        var answers = await judge.EvaluateAsync(new JsonObject { ["command"] = "x" }, AutopilotJudgeQuestions.Load(), CancellationToken.None);

        answers.Should().HaveCount(AutopilotJudgeQuestions.All.Count);
    }

    [Fact]
    public async Task No_answer_in_time_is_no_judgement()
    {
        var handler = new HangingHandler();
        var judge = Judge(handler, configure: o => o.Timeout = TimeSpan.FromMilliseconds(200));

        var act = () => judge.EvaluateAsync(new JsonObject { ["command"] = "x" }, AutopilotJudgeQuestions.Load(), CancellationToken.None);

        (await act.Should().ThrowAsync<AutopilotJudgeUnavailableException>()).Which.Reason.Should().Be("timeout");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return response;
        }
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses[Math.Min(Calls++, responses.Length - 1)]);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
