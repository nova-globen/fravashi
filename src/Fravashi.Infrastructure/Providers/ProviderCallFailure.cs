using Fravashi.Application.Mirroring;
using Fravashi.Application.Network;

namespace Fravashi.Infrastructure.Providers;

/// <summary>
/// The ONE place a provider adapter decides what a call that threw means for the outbox (backlog 232, slice E of 219,
/// plan @58). Before this the Jira, Azure DevOps boards, Confluence and Azure DevOps wiki adapters each carried their
/// own copy of "never left", and four copies of the rule the outbox's rule 3 rests on can drift apart one at a time.
/// <para>
/// The rule itself is <see cref="NetworkFailure.Classify"/>: a call goes back on the queue as
/// <see cref="ProviderOutcome.Unreachable"/> ONLY when the failure proves it never left. Everything else that is caught
/// (a reset after the request was written, a premature end, the client's own timeout, a failure that is not the network)
/// is <see cref="ProviderOutcome.Indeterminate"/>, which is looked up by its operation id and never resent blindly.
/// </para>
/// <para>
/// What is caught is exactly what the four copies caught: an <see cref="HttpRequestException"/>, and a
/// <see cref="TaskCanceledException"/> the caller did not ask for. Anything else still escapes, as it did, and a
/// cancellation the caller asked for is still the caller's.
/// </para>
/// </summary>
public static class ProviderCallFailure
{
    /// <summary>True when <paramref name="exception"/> is a failed provider call this class classifies.</summary>
    public static bool Catches(Exception exception, CancellationToken callerToken) =>
        exception is HttpRequestException || (exception is TaskCanceledException && !callerToken.IsCancellationRequested);

    /// <summary>
    /// The outcome of a provider call that threw <paramref name="exception"/>, carrying the classified failure so the
    /// outage can be named ("Jira unreachable since 14:02 (name not resolved)").
    /// </summary>
    /// <param name="provider">The provider as a person names it, for the sentence.</param>
    /// <param name="exception">What the call threw; <see cref="Catches"/> was true for it.</param>
    /// <param name="callerToken">The caller's token, so its own cancellation is never read as the network.</param>
    public static ProviderResult<T> Result<T>(string provider, Exception exception, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var failure = NetworkFailure.Classify(exception, callerToken);

        if (failure.NeverLeft)
        {
            return ProviderResult<T>.Unreachable($"{provider} could not be reached, so nothing was sent ({failure.Words}).")
                with { Network = failure };
        }

        var why = failure.IsNetwork ? failure.Words : Detail(exception);
        return ProviderResult<T>.Indeterminate($"The request to {provider} left and no answer came back ({why}).")
            with { Network = failure.IsNetwork ? failure : null };
    }

    private static string Detail(Exception exception) => exception switch
    {
        HttpRequestException http => http.HttpRequestError.ToString(),
        TaskCanceledException => "the call was cancelled before an answer came",
        _ => exception.GetType().Name,
    };
}
