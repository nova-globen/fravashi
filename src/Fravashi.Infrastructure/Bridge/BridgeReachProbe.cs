using System.Net.Sockets;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>One cheap question to the host bridge from inside the container (backlog 252).</summary>
public interface IBridgeReachProbe
{
    Task<BridgeReachProbeResult> ProbeAsync(Uri bridge, CancellationToken cancellationToken);
}

/// <summary>What the probe found, and the failure's own words when it has no better name for it.</summary>
public sealed record BridgeReachProbeResult(BridgeReachOutcome Outcome, string? Reason = null);

/// <summary>
/// Asks <c>GET /bridge/healthz</c>, the bridge's one endpoint that needs no token, within a short budget. ANY HTTP
/// answer counts as reachable: a bridge that says something, even an error status, is not behind a firewall.
/// </summary>
public sealed class HttpBridgeReachProbe : IBridgeReachProbe
{
    /// <summary>Long enough for a WSL hop on a busy machine, short enough for a page to ask while it opens.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan _budget;

    /// <param name="handler">For tests; null uses a fresh socket handler per question.</param>
    /// <param name="budget">How long one question may take.</param>
    public HttpBridgeReachProbe(HttpMessageHandler? handler = null, TimeSpan? budget = null)
    {
        _handler = handler;
        _budget = budget ?? DefaultBudget;
    }

    public async Task<BridgeReachProbeResult> ProbeAsync(Uri bridge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        using var http = _handler is null
            ? new HttpClient(new SocketsHttpHandler { ConnectTimeout = _budget }, disposeHandler: true)
            : new HttpClient(_handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_budget);
        try
        {
            using var response = await http
                .GetAsync(new Uri(bridge, "/bridge/healthz"), HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);
            return new BridgeReachProbeResult(BridgeReachOutcome.Answered);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BridgeReachProbeResult(BridgeReachOutcome.TimedOut);
        }
        catch (HttpRequestException ex)
        {
            return Classify(ex);
        }
    }

    /// <summary>Names a failed request by what the socket underneath reported. Public for tests.</summary>
    public static BridgeReachProbeResult Classify(HttpRequestException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        for (Exception? inner = failure; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return new BridgeReachProbeResult(BridgeReachOutcome.Refused);
                case SocketException { SocketErrorCode: SocketError.TimedOut }:
                case TimeoutException:
                    return new BridgeReachProbeResult(BridgeReachOutcome.TimedOut);
            }
        }

        return new BridgeReachProbeResult(BridgeReachOutcome.Failed, failure.InnerException?.Message ?? failure.Message);
    }
}
