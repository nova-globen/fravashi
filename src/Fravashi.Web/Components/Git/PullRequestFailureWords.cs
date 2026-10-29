using Fravashi.Application.Network;

namespace Fravashi.Web.Components.Git;

/// <summary>
/// How the PR panel words a failed call to the forge (backlog 231): the network in slice A's words, and a write whose
/// answer was lost as "may have been made", never as failed. An answer from the forge (a status) keeps its own message.
/// </summary>
internal static class PullRequestFailureWords
{
    /// <summary>The exceptions the panel catches: the forge's own, the client's, and its timeout.</summary>
    public static bool Catches(Exception ex) =>
        ex is HttpRequestException or InvalidOperationException or OperationCanceledException;

    /// <summary>The sentence for a failed call; <paramref name="write"/> says whether it could have changed something.</summary>
    public static string For(Exception ex, bool write)
    {
        ArgumentNullException.ThrowIfNull(ex);
        // An answer keeps its own words: a status from the forge, or a failure the client raised itself after reading
        // one (no phase and nothing inside, like an unreadable body).
        if (ex is HttpRequestException { StatusCode: not null } or InvalidOperationException
            or HttpRequestException { HttpRequestError: HttpRequestError.Unknown, InnerException: null })
        {
            return ex.Message;
        }

        var failure = NetworkFailure.Classify(ex);
        if (failure.NeverLeft)
        {
            return $"Could not reach the forge ({failure.Words}). Nothing changed.";
        }

        // Slice A's words for this kind already say "no answer came back", so they are not repeated in brackets.
        return write
            ? "No answer came back from the forge; the change may have been made. Refresh before trying again."
            : "No answer came back from the forge. Try again in a minute.";
    }
}
