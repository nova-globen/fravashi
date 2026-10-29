using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Notifications;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Notifications;

/// <summary>
/// Backlog 304: the one <see cref="IWaitingChannel"/> this version ships. It must never forward
/// <see cref="WaitingForPersonEntry.What"/> (which can carry a tool name or a shortened question) into the
/// toast, only a generic, kind-only sentence, and it must do nothing when no bridge is configured.
/// </summary>
public sealed class WindowsToastWaitingChannelTests
{
    [Fact]
    public async Task Posts_a_generic_sentence_never_the_entrys_own_words()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var channel = new WindowsToastWaitingChannel(Configured(), handler: handler);
        var entry = new WaitingForPersonEntry(
            WaitingForPersonKind.Approval, "Waiting on your approval: a very specific command line",
            DateTimeOffset.UtcNow, "ws-1", SessionId: new AgentSessionId(Guid.NewGuid()));

        await channel.NotifyAsync(entry);

        handler.SentUri.Should().NotBeNull();
        var body = JsonSerializer.Deserialize<NotifyBody>(handler.SentBody!, JsonOptions);
        body!.Message.Should().Be("A tool call is waiting for your approval.");
        body.Message.Should().NotContain("command line");
        handler.SentUri!.AbsolutePath.Should().Be("/bridge/notify");
        handler.SentHeader.Should().Be("the-token");
    }

    [Fact]
    public async Task A_parked_entrys_sentence_names_only_the_item_number()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var channel = new WindowsToastWaitingChannel(Configured(), handler: handler);
        var entry = new WaitingForPersonEntry(
            WaitingForPersonKind.Parked, "#162 Fix the thing is parked: the CI log says why",
            DateTimeOffset.UtcNow, "ws-1", ItemNumber: 162);

        await channel.NotifyAsync(entry);

        var body = JsonSerializer.Deserialize<NotifyBody>(handler.SentBody!, JsonOptions);
        body!.Message.Should().Be("Work item #162 is parked, waiting on you.");
        body.Message.Should().NotContain("CI log");
    }

    [Fact]
    public async Task No_bridge_configured_is_a_silent_no_op()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var configuration = new ConfigurationBuilder().Build();
        var channel = new WindowsToastWaitingChannel(configuration, handler: handler);

        await channel.NotifyAsync(new WaitingForPersonEntry(WaitingForPersonKind.Approval, "x", DateTimeOffset.UtcNow, "ws-1"));

        handler.SentUri.Should().BeNull();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static IConfiguration Configured() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://bridge.example/",
            [BridgeWorkspaceContext.TokenKey] = "the-token",
        })
        .Build();

    private sealed record NotifyBody(string Message);

    /// <summary>Reads the request's body and header INSIDE the handler, before the caller disposes it.</summary>
    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public Uri? SentUri { get; private set; }

        public string? SentBody { get; private set; }

        public string? SentHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SentUri = request.RequestUri;
            SentBody = request.Content is { } content ? await content.ReadAsStringAsync(ct) : null;
            SentHeader = request.Headers.TryGetValues("X-Fravashi-Bridge", out var values) ? values.FirstOrDefault() : null;
            return new HttpResponseMessage(status);
        }
    }
}
