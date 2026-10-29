using System.Net;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Providers;

/// <summary>
/// The ONE place a provider's HTTP failure becomes a sentence a user can act on (backlog 76).
/// <para>
/// Every integration we ship talks to somebody else's API, and the three ways those APIs break on us
/// (an expired or revoked credential, a project/repo/space that was renamed or deleted, and a schema or
/// API-generation change) all used to arrive as a bare status code or a .NET default message
/// ("Response status code does not indicate success: 401 (Unauthorized)."). That reads like a Fravashi
/// bug. Each helper here names the PROVIDER, the THING that was addressed, and the CONDITION, so the
/// user can tell whose problem it is. The provider's own error text rides along when it sent one, because
/// it often carries the real cause (a missing scope, a retired endpoint).
/// </para>
/// <para>
/// The support contract these sentences implement is <c>docs/providers/support-contract.md</c>; when a
/// statement there says "the failure names the provider and the condition", this class is what does it.
/// </para>
/// </summary>
public static class ProviderFailure
{
    /// <summary>
    /// A failure sentence for a provider HTTP response that was not a success.
    /// <paramref name="subject"/> names what was addressed in the user's own terms, for example
    /// "the repository acme/widget on github.com" or "the space DOCS". <paramref name="providerSaid"/> is
    /// the provider's own error text when the body carried one.
    /// </summary>
    public static string Describe(string provider, string subject, HttpStatusCode status, string? providerSaid = null)
    {
        var code = (int)status;
        var sentence = status switch
        {
            HttpStatusCode.Unauthorized =>
                $"{provider} rejected the credential for {subject} ({code}): the stored token is expired, revoked, "
                + "or not valid for this account. Re-enter it and retry.",
            HttpStatusCode.Forbidden =>
                $"{provider} accepted the credential but refused {subject} ({code}): the token lacks the permission "
                + "or scope this operation needs, or the host policy forbids it.",
            HttpStatusCode.NotFound =>
                $"{provider} does not have {subject} ({code}): it may have been renamed, moved or deleted, "
                + "or this credential cannot see it.",
            HttpStatusCode.Gone =>
                $"{provider} has retired the API this build calls for {subject} ({code}): the provider dropped that "
                + $"API generation, so {ProductName.Short} needs an update rather than a retry.",
            HttpStatusCode.TooManyRequests =>
                $"{provider} is rate-limiting {ProductName.Short} on {subject} ({code}): retry later.",
            _ when code >= 500 =>
                $"{provider} failed on its own side for {subject} ({code}): this is the provider, not {ProductName.Short}.",
            _ => $"{provider} returned {code} for {subject}.",
        };

        var said = Summarize(providerSaid);
        return said is null ? sentence : $"{sentence} {provider} said: {said}";
    }

    /// <summary>
    /// A failure sentence for a 2xx response whose body could not be read as the JSON this build expects:
    /// an API schema change, or something that is not the API at all answering (a proxy, a captive portal,
    /// or a sign-in page).
    /// </summary>
    public static string UnreadableBody(string provider, string subject, string? body = null)
    {
        var sentence =
            $"{provider} answered {subject} with a body {ProductName.Short} could not read as the JSON it expects: "
            + "the provider's API shape has changed, or something other than the API (a proxy or a sign-in page) "
            + "answered instead.";
        var said = Summarize(body);
        return said is null ? sentence : $"{sentence} It sent: {said}";
    }

    /// <summary>
    /// A failure sentence for a readable response that is missing a field this build reads. Distinct from
    /// <see cref="UnreadableBody"/>: the JSON parsed, but the contract moved under us.
    /// </summary>
    public static string MissingField(string provider, string subject, string field) =>
        $"{provider} returned {subject} without the '{field}' field {ProductName.Short} reads: the provider's API shape has "
        + "changed, so this build cannot interpret the answer.";

    /// <summary>One line of the provider's own text, capped so a whole error page never becomes the message.</summary>
    private static string? Summarize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 300 ? collapsed[..300] : collapsed;
    }
}
