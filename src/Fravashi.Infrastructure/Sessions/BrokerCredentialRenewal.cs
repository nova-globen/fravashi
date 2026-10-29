using Fravashi.Application.AgentRuntimes.Broker;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>What asking for a fresh broker token found (backlog 235).</summary>
public enum TokenRenewalState
{
    /// <summary>A valid token is stored that is not the one a conversation was refused with.</summary>
    Fresh,

    /// <summary>None yet: the broker could not be reached, or did not answer with a token. Asked again on the backoff.</summary>
    Waiting,

    /// <summary>The broker refused to mint, or nobody is signed in. Not asked again by itself.</summary>
    Refused,
}

/// <summary>
/// The answer: the state, the fresh token's id (never its value) when there is one, and the words otherwise.
/// <paramref name="Outage"/> is the broker outage the words describe when the mint could not reach the broker, so a
/// page can say its times in the person's zone (backlog 246).
/// </summary>
public sealed record TokenRenewal(
    TokenRenewalState State, string? TokenId, string? Words, DateTimeOffset? NextAttemptAt, BrokerOutage? Outage = null);

/// <summary>
/// What the live sessions ask about the broker token (backlog 235, plan @60): which token is current, by its id, and
/// "get me a fresh one". Only where the broker carries the runtime (<see cref="Applies"/>); everywhere else it answers
/// fresh without asking anything, so the single-user default behaves exactly as before.
/// <para>
/// The token service is a typed HTTP client, so it is resolved per call rather than captured: a captured client would
/// pin one handler, and with it the DNS answer of the moment it was built, which is the very thing an unstable network
/// changes.
/// </para>
/// </summary>
public sealed class BrokerCredentialRenewal(Func<IBrokerTokenService> tokens, BrokerTokenHealth health, Func<bool> applies)
{
    public BrokerCredentialRenewal(Func<IBrokerTokenService> tokens, BrokerTokenHealth health, bool applies)
        : this(tokens, health, () => applies)
    {
    }

    /// <summary>
    /// True when the broker carries the agent runtime, asked at each use: under the free tier it
    /// stops carrying it the moment the person stores a credential of their own.
    /// </summary>
    public bool Applies => applies();

    /// <summary>When the next mint attempt is due during an outage; null while the broker answers.</summary>
    public DateTimeOffset? NextAttemptAt => health.Outage?.NextAttemptAt;

    /// <summary>The id of the stored token while it is valid; null when none is, or when the broker does not apply.</summary>
    public string? CurrentTokenId()
    {
        if (!Applies)
        {
            return null;
        }

        var status = tokens().GetStatus();
        return status.TokenValid ? status.TokenId : null;
    }

    /// <summary>
    /// Asks for a token that is valid and is not <paramref name="refused"/> (the token a conversation's model call was
    /// just refused with, or null when none was refused), minting one when needed. <paramref name="now"/> says the
    /// broker's host was just seen to answer, so an outage's backoff is not waited out. A standing refusal is answered
    /// without asking the broker again.
    /// </summary>
    public async Task<TokenRenewal> FreshAsync(string? refused, bool now, CancellationToken cancellationToken)
    {
        if (!Applies)
        {
            return new(TokenRenewalState.Fresh, null, null, null);
        }

        var service = tokens();
        var result = await service.RenewAsync(refused, now, cancellationToken).ConfigureAwait(false);
        var status = service.GetStatus();
        if (status is { TokenValid: true, TokenId: { } id } && !string.Equals(id, refused, StringComparison.Ordinal))
        {
            return new(TokenRenewalState.Fresh, id, null, null);
        }

        return result?.Failure is BrokerMintFailure.Refused or BrokerMintFailure.NotSignedIn
            ? new(TokenRenewalState.Refused, null, result.Error, null)
            : new(TokenRenewalState.Waiting, null, result?.Error ?? health.Words, health.Outage?.NextAttemptAt,
                result is null or { Failure: BrokerMintFailure.Unreachable } ? health.Outage : null);
    }
}
