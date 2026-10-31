using System.Text.Json.Serialization;

namespace Fravashi.Application.AgentRuntimes.Broker;

/// <summary>
/// What the credential broker has counted for this person (backlog 377), as its <c>GET {BaseUrl}/broker/usage/me</c>
/// answers it (website 2.9.5). The limits are per WINDOW, which starts at the person's first request in it and lasts
/// <see cref="WindowSeconds"/>, so a surface says "in this window, resets at ..." rather than "today". The counters live
/// in the broker's memory and start again when it restarts, which <see cref="CountedSinceUtc"/> says.
/// </summary>
public sealed record BrokerUsage(
    [property: JsonPropertyName("asOfUtc")] DateTimeOffset AsOfUtc,
    [property: JsonPropertyName("countedSinceUtc")] DateTimeOffset? CountedSinceUtc,
    [property: JsonPropertyName("windowSeconds")] long WindowSeconds,
    [property: JsonPropertyName("unitsUsed")] long UnitsUsed,
    [property: JsonPropertyName("unitsLimit")] long? UnitsLimit,
    [property: JsonPropertyName("unitsResetAtUtc")] DateTimeOffset? UnitsResetAtUtc,
    [property: JsonPropertyName("requestsUsed")] long RequestsUsed,
    [property: JsonPropertyName("requestsLimit")] long? RequestsLimit,
    [property: JsonPropertyName("requestsResetAtUtc")] DateTimeOffset? RequestsResetAtUtc,
    [property: JsonPropertyName("suspended")] bool Suspended);

/// <summary>Why there is no usage to show; each is said differently, and none is an error the person must act on.</summary>
public enum BrokerUsageGap
{
    None,

    /// <summary>The broker does not carry this install's turns (no free tier here, or the person's own key is used).</summary>
    NotBrokered,

    /// <summary>No broker token is stored yet, or the broker refused it (401): the next mint fixes it.</summary>
    NoToken,

    /// <summary>The broker predates the read (404): it answers nothing about usage.</summary>
    NotOffered,

    /// <summary>The broker is not serving, or could not be reached.</summary>
    Unavailable,
}

/// <summary>A usage read: the figures, or why there are none.</summary>
public sealed record BrokerUsageRead(BrokerUsage? Usage, BrokerUsageGap Gap)
{
    public static BrokerUsageRead Of(BrokerUsage usage) => new(usage, BrokerUsageGap.None);

    public static BrokerUsageRead Missing(BrokerUsageGap gap) => new(null, gap);
}

/// <summary>Reads this person's free-tier usage from the broker. Never throws; spends and records nothing there.</summary>
public interface IBrokerUsageReader
{
    Task<BrokerUsageRead> ReadAsync(CancellationToken cancellationToken = default);
}
