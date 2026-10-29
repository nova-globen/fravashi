using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;

namespace Fravashi.Infrastructure.Sessions.Judge;

/// <summary>
/// Adapter configuration for <see cref="TypeSafeAutopilotJudge"/>: the HTTP side only, distinct from
/// <see cref="AutopilotJudgeOptions"/>'s decision thresholds, which belong to the orchestrator and do not
/// care which provider answers them.
/// </summary>
public sealed class TypeSafeAutopilotJudgeOptions
{
    /// <summary>TypeSafe's System One endpoint. Must be https outside a test.</summary>
    public string Endpoint { get; set; } = "https://api.typesafe.ai/v1/systemone";

    /// <summary>Test escape hatch: permit an http <see cref="Endpoint"/> (a stub under test). Never for a real service.</summary>
    public bool AllowInsecureEndpoint { get; set; }

    /// <summary>The model the adapter asks for.</summary>
    public string Model { get; set; } = "jev-latest";

    /// <summary>How long one call may take, retries included.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The wait before the one retry a 429 or 529 earns.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(300);
}

/// <summary>
/// <see cref="IAutopilotJudgeModel"/> over TypeSafe's System One API (model Jev), the studio's own copy of
/// the broker's <c>TypeSafeSystemOne</c> adapter (ADR 0031 decision 8, the using-typesafe skill), narrowed
/// to noul questions only and reading its key from <see cref="AutopilotJudgeSecret"/> rather than an
/// environment variable. The key is added here and only here, and never appears in an exception, a log
/// line, or anything returned to a caller.
/// <para>
/// A 429 or 529 earns ONE retry after <see cref="TypeSafeAutopilotJudgeOptions.RetryDelay"/>, inside the
/// same overall <see cref="TypeSafeAutopilotJudgeOptions.Timeout"/>, matching the broker's own adapter.
/// Everything else that is not a readable noul answer to every question is an
/// <see cref="AutopilotJudgeUnavailableException"/>.
/// </para>
/// </summary>
public sealed class TypeSafeAutopilotJudge(
    HttpClient httpClient, ISecretStore secrets, TypeSafeAutopilotJudgeOptions options, TimeProvider clock)
    : IAutopilotJudgeModel
{
    public string Name => "typesafe";

    public async Task<IReadOnlyDictionary<string, double>> EvaluateAsync(
        JsonObject state, JsonObject questions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);

        // The key never leaves except on purpose (ADR 0031 decision 8), and an http endpoint would send it in
        // clear, so an endpoint that is not https (and not the explicit test exception) is no judgement at all,
        // checked before the key is even read.
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || !(endpoint.Scheme == Uri.UriSchemeHttps
                || (options.AllowInsecureEndpoint && endpoint.Scheme == Uri.UriSchemeHttp)))
        {
            throw new AutopilotJudgeUnavailableException("insecure_endpoint");
        }

        var key = secrets.Get(AutopilotJudgeSecret.Reference);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new AutopilotJudgeUnavailableException("no_key");
        }

        var payload = new JsonObject
        {
            ["state"] = state.DeepClone(),
            ["model"] = options.Model,
            ["questions"] = questions.DeepClone(),
        }.ToJsonString();

        using var deadline = new CancellationTokenSource(options.Timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.Accept.ParseAdd("application/json");

                using var response = await httpClient.SendAsync(request, linked.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if ((status == 429 || status == 529) && attempt == 1)
                {
                    await Task.Delay(options.RetryDelay, clock, linked.Token).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new AutopilotJudgeUnavailableException("http_" + status);
                }

                var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                return Parse(body, questions);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AutopilotJudgeUnavailableException("timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new AutopilotJudgeUnavailableException("unreachable", ex);
        }
    }

    /// <summary>Reads an answer document. Internal so the answer shape is tested without a network.</summary>
    internal static IReadOnlyDictionary<string, double> Parse(string body, JsonObject questions)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new AutopilotJudgeUnavailableException("unreadable_answer", ex);
        }

        if (root?["answers"] is not JsonObject answers)
        {
            throw new AutopilotJudgeUnavailableException("unreadable_answer");
        }

        var read = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (id, _) in questions)
        {
            if (answers[id] is not JsonObject answer
                || answer["noul"] is not JsonValue value
                || !value.TryGetValue<double>(out var noul)
                || !double.IsFinite(noul))
            {
                throw new AutopilotJudgeUnavailableException("answer_unreadable_" + id);
            }

            read[id] = noul;
        }

        return read;
    }
}
