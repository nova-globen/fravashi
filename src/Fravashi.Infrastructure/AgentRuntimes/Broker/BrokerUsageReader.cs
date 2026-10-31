using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Persistence;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.AgentRuntimes.Broker;

/// <summary>
/// <see cref="IBrokerUsageReader"/> over the broker's <c>GET {BaseUrl}/broker/usage/me</c> (backlog 377, website 2.9.5),
/// authorized by the same stored bearer the agent CLI forwards with. Asked only while the broker carries this install's
/// turns; every other answer is a <see cref="BrokerUsageGap"/>, never an exception.
/// </summary>
public sealed class BrokerUsageReader(
    ISecretStore secrets,
    BrokerOptions broker,
    HttpClient http,
    Func<bool> brokerCarries) : IBrokerUsageReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BrokerUsageRead> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!broker.IsActive || !brokerCarries())
        {
            return BrokerUsageRead.Missing(BrokerUsageGap.NotBrokered);
        }

        if (secrets.Get(broker.UserTokenSecretRef) is not { Length: > 0 } token)
        {
            return BrokerUsageRead.Missing(BrokerUsageGap.NoToken);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(broker.BaseUrl!.TrimEnd('/') + "/"), "broker/usage/me"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK when await response.Content.ReadFromJsonAsync<BrokerUsage>(Json, cancellationToken)
                    .ConfigureAwait(false) is { } usage => BrokerUsageRead.Of(usage),
                HttpStatusCode.Unauthorized => BrokerUsageRead.Missing(BrokerUsageGap.NoToken),
                HttpStatusCode.NotFound => BrokerUsageRead.Missing(BrokerUsageGap.NotOffered),
                _ => BrokerUsageRead.Missing(BrokerUsageGap.Unavailable),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return BrokerUsageRead.Missing(BrokerUsageGap.Unavailable);
        }
    }

    /// <summary>The words for a read, as one line; null when there is nothing worth saying.</summary>
    public static string? Describe(BrokerUsageRead read, Func<DateTimeOffset, string> time)
    {
        if (read.Usage is not { } usage)
        {
            return read.Gap switch
            {
                BrokerUsageGap.NotOffered or BrokerUsageGap.Unavailable =>
                    "Free tier: the broker's count is not available right now. The limit is " +
                    FreeTierUnits.DailyLimit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " units a day.",
                _ => null,
            };
        }

        if (usage.Suspended)
        {
            return "Free tier: suspended for this account by the broker.";
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var limit = usage.UnitsLimit is { } l ? " of " + l.ToString("N0", invariant) : string.Empty;
        var reset = usage.UnitsResetAtUtc is { } at ? $", resets {time(at)}" : string.Empty;
        var window = usage.WindowSeconds == 86_400 ? "in the last 24 hours" : "in this window";
        return $"Free tier: {usage.UnitsUsed.ToString("N0", invariant)}{limit} units used {window}{reset}.";
    }
}
