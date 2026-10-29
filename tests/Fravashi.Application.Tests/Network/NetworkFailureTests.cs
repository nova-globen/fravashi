using System.Net.Sockets;
using AwesomeAssertions;
using Fravashi.Application.Network;
using Xunit;

namespace Fravashi.Application.Tests.Network;

/// <summary>
/// Backlog 228: the one classifier for "the network failed". Shapes are built here the way the real handler builds
/// them; Infrastructure.Tests' NetworkFailureShapesTests holds them equal to what a real socket produces.
/// </summary>
public sealed class NetworkFailureTests
{
    [Fact]
    public void A_name_resolution_error_is_name_not_resolved_and_never_left()
    {
        var failure = NetworkFailure.Classify(new HttpRequestException(
            HttpRequestError.NameResolutionError, "Name or service not known", new SocketException((int)SocketError.HostNotFound)));

        failure.Kind.Should().Be(NetworkFailureKind.NameNotResolved);
        failure.NeverLeft.Should().BeTrue();
        failure.Words.Should().Be("name not resolved");
    }

    [Fact]
    public void A_connection_error_before_the_request_was_written_never_left()
    {
        var refused = NetworkFailure.Classify(new HttpRequestException(
            HttpRequestError.ConnectionError, "Connection refused", new SocketException((int)SocketError.ConnectionRefused)));
        var reset = NetworkFailure.Classify(new HttpRequestException(
            HttpRequestError.ConnectionError, "Connection reset by peer", new SocketException((int)SocketError.ConnectionReset)));
        var noRoute = NetworkFailure.Classify(new HttpRequestException(
            HttpRequestError.ConnectionError, "Network is unreachable", new SocketException((int)SocketError.NetworkUnreachable)));
        var tls = NetworkFailure.Classify(new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS handshake failed"));

        refused.Kind.Should().Be(NetworkFailureKind.Refused);
        reset.Kind.Should().Be(NetworkFailureKind.ResetBeforeSend);
        noRoute.Kind.Should().Be(NetworkFailureKind.Unreachable);
        tls.Kind.Should().Be(NetworkFailureKind.ResetBeforeSend);
        new[] { refused, reset, noRoute, tls }.Should().OnlyContain(f => f.NeverLeft && f.IsNetwork);
    }

    [Fact]
    public void A_connect_that_timed_out_is_timed_out_connecting()
    {
        var socket = NetworkFailure.Classify(new HttpRequestException(
            HttpRequestError.ConnectionError, "Connection timed out", new SocketException((int)SocketError.TimedOut)));
        var connectTimeout = NetworkFailure.Classify(new HttpRequestException(
            HttpRequestError.ConnectionError, "Connect timeout", new TimeoutException()));

        socket.Kind.Should().Be(NetworkFailureKind.TimedOutConnecting);
        connectTimeout.Kind.Should().Be(NetworkFailureKind.TimedOutConnecting);
        socket.NeverLeft.Should().BeTrue();
    }

    [Fact]
    public void A_reset_after_the_request_was_written_left_with_no_answer()
    {
        var reset = NetworkFailure.Classify(new HttpRequestException(
            HttpRequestError.Unknown,
            "An error occurred while sending the request.",
            new IOException("Unable to read data from the transport connection", new SocketException((int)SocketError.ConnectionReset))));
        var ended = NetworkFailure.Classify(new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

        reset.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        reset.NeverLeft.Should().BeFalse();
        reset.IsNetwork.Should().BeTrue();
        ended.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
    }

    [Fact]
    public void The_clients_own_timeout_left_with_no_answer()
    {
        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            new TimeoutException("The operation was canceled."));

        var failure = NetworkFailure.Classify(timeout, CancellationToken.None);

        failure.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        failure.NeverLeft.Should().BeFalse();
    }

    [Fact]
    public void A_cancellation_the_caller_asked_for_is_not_a_network_failure()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        NetworkFailure.Classify(new TaskCanceledException("The operation was canceled."), caller.Token)
            .Kind.Should().Be(NetworkFailureKind.NotNetwork);
        NetworkFailure.Classify(new TaskCanceledException("x", new TimeoutException()), caller.Token)
            .Kind.Should().Be(NetworkFailureKind.NotNetwork, "the caller's own cancellation wins over a timeout that raced it");
        NetworkFailure.Classify(new OperationCanceledException()).Kind.Should().Be(NetworkFailureKind.NotNetwork);
    }

    [Fact]
    public void Failures_that_are_not_the_network_are_not_network()
    {
        NetworkFailure.Classify(new InvalidOperationException("bad state")).Should().Be(NetworkFailure.None);
        NetworkFailure.Classify(new HttpRequestException(HttpRequestError.UserAuthenticationError, "proxy auth")).IsNetwork.Should().BeFalse();
        NetworkFailure.Classify(new HttpRequestException(HttpRequestError.ConfigurationLimitExceeded, "headers")).IsNetwork.Should().BeFalse();
        NetworkFailure.None.NeverLeft.Should().BeFalse();
    }

    [Fact]
    public void A_failure_inside_a_wrapper_or_an_aggregate_is_found()
    {
        var inner = new HttpRequestException(HttpRequestError.NameResolutionError, "no name");

        NetworkFailure.Classify(new InvalidOperationException("wrapped", inner)).Kind.Should().Be(NetworkFailureKind.NameNotResolved);
        NetworkFailure.Classify(new AggregateException(new InvalidOperationException("other"), inner))
            .Kind.Should().Be(NetworkFailureKind.NameNotResolved);
    }

    [Fact]
    public void A_bare_socket_error_claims_never_left_only_for_a_name_error_or_a_refusal()
    {
        NetworkFailure.Classify(new SocketException((int)SocketError.TryAgain)).Kind.Should().Be(NetworkFailureKind.NameNotResolved);
        NetworkFailure.Classify(new SocketException((int)SocketError.ConnectionRefused)).Kind.Should().Be(NetworkFailureKind.Refused);

        // With the phase unknown these can hit a connection in use: Linux reports a route lost under an established
        // socket as EHOSTUNREACH or ENETUNREACH on its next read or write.
        foreach (var error in new[]
        {
            SocketError.NetworkUnreachable, SocketError.HostUnreachable, SocketError.NetworkDown, SocketError.HostDown,
            SocketError.TimedOut, SocketError.ConnectionReset, SocketError.ConnectionAborted,
        })
        {
            var failure = NetworkFailure.Classify(new SocketException((int)error));
            failure.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer, error.ToString());
            failure.NeverLeft.Should().BeFalse(error.ToString());
        }
    }

    [Fact]
    public void A_stream_error_left_with_no_answer_whatever_socket_error_it_carries()
    {
        var failure = NetworkFailure.Classify(new IOException(
            "Unable to write data to the transport connection", new SocketException((int)SocketError.HostUnreachable)));

        failure.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        failure.NeverLeft.Should().BeFalse();
    }

    [Fact]
    public void A_socket_error_from_a_connect_is_known_never_to_have_left()
    {
        NetworkFailure.ClassifyConnect(SocketError.ConnectionReset).Kind.Should().Be(NetworkFailureKind.ResetBeforeSend);
        NetworkFailure.ClassifyConnect(SocketError.TimedOut).Kind.Should().Be(NetworkFailureKind.TimedOutConnecting);
        NetworkFailure.ClassifyConnect(SocketError.HostUnreachable).Kind.Should().Be(NetworkFailureKind.Unreachable);
        NetworkFailure.ClassifyConnect(SocketError.HostUnreachable).NeverLeft.Should().BeTrue();
    }

    [Theory]
    [InlineData("API Error: Can't reach the API server (getaddrinfo EAI_AGAIN api.anthropic.com)", NetworkFailureKind.NameNotResolved)]
    [InlineData("Error: getaddrinfo ENOTFOUND api.anthropic.com", NetworkFailureKind.NameNotResolved)]
    [InlineData("API Error: Connection error. (read ECONNRESET)", NetworkFailureKind.LeftWithNoAnswer)]
    [InlineData("connect ECONNREFUSED 127.0.0.1:443", NetworkFailureKind.Refused)]
    [InlineData("connect ETIMEDOUT 160.79.104.10:443", NetworkFailureKind.TimedOutConnecting)]
    [InlineData("connect ENETUNREACH 160.79.104.10:443", NetworkFailureKind.Unreachable)]
    [InlineData("read ETIMEDOUT", NetworkFailureKind.LeftWithNoAnswer)]
    [InlineData("read EHOSTUNREACH", NetworkFailureKind.LeftWithNoAnswer)]
    [InlineData("API Error: Request failed (ETIMEDOUT)", NetworkFailureKind.LeftWithNoAnswer)]
    [InlineData("API Error: Can’t reach the API server", NetworkFailureKind.LeftWithNoAnswer)]
    [InlineData("API Error: Can't reach the API server", NetworkFailureKind.LeftWithNoAnswer)]
    [InlineData("API Error: Connection error.", NetworkFailureKind.LeftWithNoAnswer)]
    [InlineData("Error: socket hang up", NetworkFailureKind.LeftWithNoAnswer)]
    [InlineData("API Error: Request timed out.", NetworkFailureKind.LeftWithNoAnswer)]
    public void The_agent_clis_network_texts_are_classified(string text, NetworkFailureKind expected)
    {
        NetworkFailure.ClassifyCliText(text).Kind.Should().Be(expected);
    }

    [Theory]
    [InlineData("getaddrinfo EAI_AGAIN api.anthropic.com", true)]
    [InlineData("connect ECONNREFUSED 127.0.0.1:443", true)]
    [InlineData("connect ETIMEDOUT 160.79.104.10:443", true)]
    [InlineData("connect EHOSTUNREACH 160.79.104.10:443", true)]
    [InlineData("read ETIMEDOUT", false)]
    [InlineData("write EHOSTUNREACH", false)]
    [InlineData("read ECONNRESET", false)]
    [InlineData("API Error: Can't reach the API server", false)]
    public void A_cli_text_claims_never_left_only_when_it_proves_the_phase(string text, bool neverLeft)
    {
        NetworkFailure.ClassifyCliText(text).NeverLeft.Should().Be(neverLeft);
    }

    [Theory]
    [InlineData("API Error: 401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\"}}")]
    [InlineData("API Error: 400 prompt is too long")]
    [InlineData("Reached max budget")]
    [InlineData("XECONNRESETX is not a code")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_cli_texts_are_not_network(string? text)
    {
        NetworkFailure.ClassifyCliText(text).Should().Be(NetworkFailure.None);
    }

    [Fact]
    public void Every_kind_has_its_own_plain_words()
    {
        var words = Enum.GetValues<NetworkFailureKind>().Select(NetworkFailure.WordsFor).ToList();

        words.Should().OnlyHaveUniqueItems();
        words.Should().OnlyContain(w => !string.IsNullOrWhiteSpace(w));
    }
}
