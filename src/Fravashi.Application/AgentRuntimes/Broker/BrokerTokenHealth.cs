using System.Security.Cryptography;
using System.Text;
using Fravashi.Application.Time;
using Fravashi.Domain;

namespace Fravashi.Application.AgentRuntimes.Broker;

/// <summary>Why a mint did not produce a token (backlog 235, plan @60).</summary>
public enum BrokerMintFailure
{
    /// <summary>It did: a token was minted.</summary>
    None,

    /// <summary>The broker tier is off (the single-user default).</summary>
    Inactive,

    /// <summary>There is no identity proof to mint from.</summary>
    NotSignedIn,

    /// <summary>
    /// The broker could not be reached, or answered that it cannot serve right now (5xx, 408, 429). Retried with
    /// backoff: a mint is safe to repeat, so a request that may have arrived is retried like one that never left.
    /// </summary>
    Unreachable,

    /// <summary>The broker answered and said no (401 <c>identity_rejected</c>, 403 <c>not_allowlisted</c> or <c>not_entitled</c>, a suspension). Never retried as if it were the network.</summary>
    Refused,

    /// <summary>The broker answered with something that is not a usable token.</summary>
    Unusable,
}

/// <summary>The broker could not be reached for a mint, since when, and when the next attempt is due.</summary>
/// <param name="Since">When the first failed attempt of this outage was made.</param>
/// <param name="Reason">What failed, in plain words ("could not reach broker.example: name not resolved").</param>
/// <param name="NextAttemptAt">When the next attempt is due on the backoff.</param>
public sealed record BrokerOutage(DateTimeOffset Since, string Reason, DateTimeOffset NextAttemptAt)
{
    /// <summary>
    /// What a mint that met this outage answers, as parts (backlog 246): "Could not mint a broker token (could not reach
    /// broker.example: name not resolved). Fravashi retries by itself; next attempt at 14:04:15."
    /// </summary>
    public StampedWords MintWords =>
        StampedWords.Of(
            $"Could not mint a broker token ({Reason}). {ProductName.Short} retries by itself; next attempt at ",
            StampedWords.AtSeconds(NextAttemptAt),
            ".");
}

/// <summary>A mint the broker refused.</summary>
/// <param name="At">When.</param>
/// <param name="Words">What the broker said, in plain words.</param>
/// <param name="ProofFingerprint">
/// For a refusal of the identity PROOF (401 <c>identity_rejected</c>): a fingerprint of the access token it refused
/// (never the token), so a renewed sign-in asks again. Null for a refusal of the PERSON (403 <c>not_allowlisted</c>,
/// <c>not_entitled</c>, a suspension): the identity provider renews the access token every hour or so, and that must not
/// lift it, so it stands until the person presses Mint token now.
/// </param>
public sealed record BrokerRefusal(DateTimeOffset At, string Words, string? ProofFingerprint);

/// <summary>
/// The broker token's health for this studio process (backlog 235, plan @60): an outage with its backoff, or a refusal
/// (of the person, or of one identity proof). One per process, shared by the mint, the background refresher, the
/// conversations waiting for a fresh token and the Settings card, so every one of them says the same thing.
/// <para>
/// Kept in memory on purpose, as <c>ProviderOutages</c> is: the durable facts (the token, its expiry, the identity proof)
/// are in the credential store, and after a restart the first attempt finds the outage or the refusal again.
/// </para>
/// <para>
/// Nothing here holds a token or an identity proof. A refusal of the proof keeps a SHA-256 prefix of the access token it
/// refused, which is enough to notice a renewed sign-in and says nothing about the proof.
/// </para>
/// </summary>
public sealed class BrokerTokenHealth(TimeProvider? clock = null)
{
    /// <summary>The first wait after a mint that could not reach the broker (plan @48 S3: 15 seconds).</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(15);

    /// <summary>The longest wait between attempts; the wait doubles from <see cref="FirstRetry"/> up to this (2 minutes).</summary>
    public static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private BrokerOutage? _outage;
    private BrokerRefusal? _refusal;
    private TimeSpan _nextRetry = FirstRetry;

    /// <summary>Raised when the outage or the refusal begins, ends or changes.</summary>
    public event Action? Changed;

    /// <summary>Raised with the new token's id (never its value) each time a token is minted.</summary>
    public event Action<string>? Renewed;

    /// <summary>The current outage, or null while the broker answers.</summary>
    public BrokerOutage? Outage
    {
        get
        {
            lock (_gate)
            {
                return _outage;
            }
        }
    }

    /// <summary>The last refusal, or null when nothing was refused since the last token.</summary>
    public BrokerRefusal? Refusal
    {
        get
        {
            lock (_gate)
            {
                return _refusal;
            }
        }
    }

    /// <summary>
    /// What the Settings card says, or null when there is nothing wrong to say: "Broker unreachable since 14:02 (could
    /// not reach broker.example: name not resolved), retrying; next attempt at 14:04:15." or the refusal's own words.
    /// </summary>
    public string? Words => Sentence?.Plain();

    /// <summary>The same sentence as parts, so the card says the times in the person's zone (backlog 246).</summary>
    public StampedWords? Sentence
    {
        get
        {
            lock (_gate)
            {
                if (_refusal is { } refused)
                {
                    return StampedWords.Of(refused.Words);
                }

                return _outage is { } down
                    ? StampedWords.Of(
                        "Broker unreachable since ",
                        StampedWords.At(down.Since),
                        $" ({down.Reason}), retrying; next attempt at ",
                        StampedWords.AtSeconds(down.NextAttemptAt),
                        ".")
                    : null;
            }
        }
    }

    /// <summary>True while an outage's next attempt is not due yet, so a caller that is not forced does not call.</summary>
    public bool BackingOff(DateTimeOffset now) => Outage is { } down && now < down.NextAttemptAt;

    /// <summary>
    /// True when asking with this identity proof would be refused again: a refusal of the person stands whatever the
    /// proof, and a refusal of the proof stands while the stored proof is that same one.
    /// </summary>
    public bool Refused(string proofFingerprint)
    {
        lock (_gate)
        {
            return _refusal is { } refused
                && (refused.ProofFingerprint is null
                    || string.Equals(refused.ProofFingerprint, proofFingerprint, StringComparison.Ordinal));
        }
    }

    /// <summary>A token was minted: the outage and the refusal end, and the backoff starts again from <see cref="FirstRetry"/>.</summary>
    public void Minted(string tokenId)
    {
        lock (_gate)
        {
            _outage = null;
            _refusal = null;
            _nextRetry = FirstRetry;
        }

        Changed?.Invoke();
        Renewed?.Invoke(tokenId);
    }

    /// <summary>
    /// A mint could not reach the broker: the outage starts or goes on, and the next attempt is put back on the backoff.
    /// Raises <see cref="Changed"/> every time, not only when the outage begins, because each attempt moves the next
    /// attempt's time and may change the reason, and the Settings card shows both (backlog 269).
    /// </summary>
    public BrokerOutage Unreachable(string reason)
    {
        var now = _clock.GetUtcNow();
        BrokerOutage outage;
        lock (_gate)
        {
            outage = new BrokerOutage(_outage?.Since ?? now, reason, now + _nextRetry);
            _outage = outage;
            _nextRetry = TimeSpan.FromTicks(Math.Min(_nextRetry.Ticks * 2, MaxRetry.Ticks));
        }

        Changed?.Invoke();
        return outage;
    }

    /// <summary>
    /// The broker refused to mint. <paramref name="proofFingerprint"/> names the refused proof for a refusal of the proof,
    /// and is null for a refusal of the person (see <see cref="BrokerRefusal.ProofFingerprint"/>). It answered, so any
    /// outage is over.
    /// </summary>
    public void Refuse(string words, string? proofFingerprint)
    {
        lock (_gate)
        {
            _outage = null;
            _nextRetry = FirstRetry;
            _refusal = new BrokerRefusal(_clock.GetUtcNow(), words, proofFingerprint);
        }

        Changed?.Invoke();
    }

    /// <summary>The broker answered with something that was not a refusal and not a token: it is reachable, so the outage ends.</summary>
    public void Answered()
    {
        bool ended;
        lock (_gate)
        {
            ended = _outage is not null;
            _outage = null;
            _nextRetry = FirstRetry;
        }

        if (ended)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// A fingerprint of an identity proof (the IdP access token): the first 16 hex characters of its SHA-256. Enough to
    /// tell a renewed proof from the one the broker refused, and nothing about the proof itself.
    /// </summary>
    public static string Fingerprint(string? proof) =>
        string.IsNullOrEmpty(proof)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proof)))[..16];
}
