using System.Net;
using System.Text;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Infrastructure.AgentRuntimes.Broker;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

/// <summary>
/// Backlog 377: the free tier's usage is the broker's own count (<c>GET {BaseUrl}/broker/usage/me</c>, website 2.9.5),
/// read with the stored bearer, asked only while the broker carries the turns, and every answer but a 200 is a gap with
/// its own words, never an exception.
/// </summary>
public sealed class BrokerUsageReaderTests
{
    private const string Body = """
        {"asOfUtc":"2026-10-31T09:00:00Z","countedSinceUtc":"2026-10-31T06:00:00Z","windowSeconds":86400,
         "unitsUsed":12345,"unitsLimit":1000000,"unitsResetAtUtc":"2026-11-01T08:12:00Z",
         "requestsUsed":12,"requestsLimit":300,"requestsResetAtUtc":"2026-11-01T08:11:58Z","suspended":false}
        """;

    private static readonly BrokerOptions Broker = new() { Enabled = true, BaseUrl = "https://host.example.test/fravashi/api" };

    private sealed class Stub(HttpStatusCode status, string body = "{}", bool fail = false) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            return fail
                ? throw new HttpRequestException("no route")
                : Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (BrokerUsageReader Reader, Stub Http) Reader(Stub http, bool carries = true, string? token = "broker-token")
    {
        var secrets = new FakeSecretStore();
        if (token is not null)
        {
            secrets.Set(Broker.UserTokenSecretRef, token);
        }

        return (new BrokerUsageReader(secrets, Broker, new HttpClient(http), () => carries), http);
    }

    [Fact]
    public async Task The_brokers_count_is_read_with_the_stored_bearer_from_the_usage_path()
    {
        var (reader, http) = Reader(new Stub(HttpStatusCode.OK, Body));

        var read = await reader.ReadAsync();

        read.Gap.Should().Be(BrokerUsageGap.None);
        read.Usage!.UnitsUsed.Should().Be(12345);
        read.Usage.UnitsLimit.Should().Be(1_000_000);
        read.Usage.WindowSeconds.Should().Be(86_400);
        http.Seen!.RequestUri!.ToString().Should().Be("https://host.example.test/fravashi/api/broker/usage/me");
        http.Seen.Headers.Authorization!.ToString().Should().Be("Bearer broker-token");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BrokerUsageGap.NoToken)]
    [InlineData(HttpStatusCode.NotFound, BrokerUsageGap.NotOffered)]
    [InlineData(HttpStatusCode.ServiceUnavailable, BrokerUsageGap.Unavailable)]
    public async Task Every_other_answer_is_a_named_gap(HttpStatusCode status, BrokerUsageGap gap)
    {
        (await Reader(new Stub(status)).Reader.ReadAsync()).Gap.Should().Be(gap);
    }

    [Fact]
    public async Task A_broker_that_cannot_be_reached_is_unavailable_not_an_exception()
    {
        (await Reader(new Stub(HttpStatusCode.OK, fail: true)).Reader.ReadAsync()).Gap.Should().Be(BrokerUsageGap.Unavailable);
    }

    [Fact]
    public async Task Nothing_is_asked_while_the_broker_does_not_carry_the_turns_or_no_token_is_stored()
    {
        var (notCarried, http) = Reader(new Stub(HttpStatusCode.OK, Body), carries: false);
        (await notCarried.ReadAsync()).Gap.Should().Be(BrokerUsageGap.NotBrokered);
        http.Seen.Should().BeNull();

        var (noToken, http2) = Reader(new Stub(HttpStatusCode.OK, Body), token: null);
        (await noToken.ReadAsync()).Gap.Should().Be(BrokerUsageGap.NoToken);
        http2.Seen.Should().BeNull();
    }

    [Fact]
    public async Task The_line_says_the_window_and_its_reset_never_today()
    {
        var read = await Reader(new Stub(HttpStatusCode.OK, Body)).Reader.ReadAsync();

        BrokerUsageReader.Describe(read, _ => "09:12")
            .Should().Be("Free tier: 12,345 of 1,000,000 units used in the last 24 hours, resets 09:12.");
        BrokerUsageReader.Describe(BrokerUsageRead.Missing(BrokerUsageGap.NotBrokered), _ => "x").Should().BeNull();
        BrokerUsageReader.Describe(BrokerUsageRead.Missing(BrokerUsageGap.NotOffered), _ => "x")
            .Should().Contain("not available right now");
    }
}
