using System.Globalization;
using System.Net.Http.Json;
using Fravashi.Application.Notifications;
using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Notifications;

/// <summary>
/// The one <see cref="IWaitingChannel"/> this version ships (ADR 0031 decision 12, backlog 304): a Windows
/// toast raised by fravashi.exe's tray, reached through the hosted bridge's <c>POST /bridge/notify</c>. This is a
/// singleton, not circuit-scoped (a poll fires with no browser request behind it), so it reads the bridge's
/// coordinates straight from configuration and builds its own short-lived <see cref="HttpClient"/>, the same
/// way <c>HttpBridgeReachProbe</c>/<c>BridgeReachMonitor</c> already do.
/// <para>
/// Builds a GENERIC, kind-only sentence rather than forwarding <see cref="WaitingForPersonEntry.What"/>, which
/// can carry a tool name or a shortened question: a toast names the item and that it waits, never its content.
/// </para>
/// </summary>
public sealed class WindowsToastWaitingChannel : IWaitingChannel
{
    private readonly IConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;
    private readonly HttpMessageHandler? _handler;

    /// <param name="handler">For tests; null uses a fresh socket handler per call.</param>
    public WindowsToastWaitingChannel(
        IConfiguration configuration,
        ILogger<WindowsToastWaitingChannel>? logger = null,
        TimeSpan? timeout = null,
        HttpMessageHandler? handler = null)
    {
        _configuration = configuration;
        _logger = logger ?? NullLogger<WindowsToastWaitingChannel>.Instance;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _handler = handler;
    }

    public async Task NotifyAsync(WaitingForPersonEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var url = _configuration[BridgeWorkspaceContext.UrlKey];
        var token = _configuration[BridgeWorkspaceContext.TokenKey];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
        {
            // No fravashi.exe bridge in this wiring (dev `dotnet run`, or a studio with no fravashi.exe): silently
            // nothing to raise a toast through.
            _logger.LogDebug("No waiting toast raised for {Kind}: the host bridge is not configured.", entry.Kind);
            return;
        }

        try
        {
            using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
            http.Timeout = _timeout;
            http.DefaultRequestHeaders.Add(TokenHeader, token);
            var message = Message(entry);
            using var response = await http
                .PostAsJsonAsync(new Uri(new Uri(url), "bridge/notify"), new { message }, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Raising a waiting toast was refused ({Status}).", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not raise a waiting toast through the host bridge.");
        }
    }

    // Duplicated from BridgeClient's own private constant (backlog 304): this channel is a singleton outside
    // any circuit, so it cannot take BridgeClient, which is scoped to BridgeWorkspaceContext.
    private const string TokenHeader = "X-Fravashi-Bridge";

    private static string Message(WaitingForPersonEntry entry) => entry.Kind switch
    {
        WaitingForPersonKind.Approval => "A tool call is waiting for your approval.",
        WaitingForPersonKind.Question => "The agent asked a question and is waiting for your answer.",
        WaitingForPersonKind.Parked => entry.ItemNumber is { } number
            ? string.Create(CultureInfo.InvariantCulture, $"Work item #{number} is parked, waiting on you.")
            : "An autopilot item is parked, waiting on you.",
        _ => $"Something is waiting on you in {ProductName.Short}.",
    };
}
